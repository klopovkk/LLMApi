using Concurrency.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Messages;
using Npgsql;
using Xunit;

namespace Concurrency;

/// <summary>
/// The sweeper's pickup pass.
///
/// The accept path already starts work opportunistically, so the sweeper looks redundant right up
/// until an instance dies between persisting a submission and claiming it. Then it is the only
/// thing that will ever start that message. These tests insert straight into the store, with no
/// accept-path dispatch at all, which is exactly that situation.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SweeperPickupTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Work_that_no_request_dispatched_is_picked_up_anyway()
    {
        await using var instance = new InstanceFactory(
            postgres.ConnectionString,
            "sweeper",
            fakeProviderMode: "NeverRespond",
            sweepInterval: TimeSpan.FromMilliseconds(50));

        // Force the host to start so the background service is running.
        using var client = instance.CreateClient();
        var store = instance.Services.GetRequiredService<MessageStore>();

        var userId = $"orphan-{Guid.NewGuid():N}";
        var (id, _, _) = await store.InsertAsync(userId, "nobody dispatched me", CancellationToken.None);

        await WaitForStateAsync(store, id, MessageState.Processing, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Several_users_are_picked_up_in_the_same_sweep()
    {
        await using var instance = new InstanceFactory(
            postgres.ConnectionString,
            "sweeper-batch",
            fakeProviderMode: "NeverRespond",
            sweepInterval: TimeSpan.FromMilliseconds(50));

        using var client = instance.CreateClient();
        var store = instance.Services.GetRequiredService<MessageStore>();

        var marker = Guid.NewGuid().ToString("N");
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var (id, _, _) = await store.InsertAsync(
                $"batch-{marker}-{i}", "waiting", CancellationToken.None);
            ids.Add(id);
        }

        foreach (var id in ids)
        {
            await WaitForStateAsync(store, id, MessageState.Processing, TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public async Task The_sweeper_respects_exclusivity()
    {
        // Two pending messages for one user, neither dispatched. The sweeper must start exactly
        // one — it has no licence to ignore the invariant just because it found two.
        await using var instance = new InstanceFactory(
            postgres.ConnectionString,
            "sweeper-exclusive",
            fakeProviderMode: "NeverRespond",
            sweepInterval: TimeSpan.FromMilliseconds(50));

        using var client = instance.CreateClient();
        var store = instance.Services.GetRequiredService<MessageStore>();
        var userId = $"sweep-excl-{Guid.NewGuid():N}";

        var (first, _, _) = await store.InsertAsync(userId, "first", CancellationToken.None);
        await store.InsertAsync(userId, "second", CancellationToken.None);

        await WaitForStateAsync(store, first, MessageState.Processing, TimeSpan.FromSeconds(10));
        await Task.Delay(300);

        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM messages WHERE user_id = @userId AND state = 'Processing'");
        command.Parameters.AddWithValue("userId", userId);

        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    private static async Task WaitForStateAsync(
        MessageStore store, Guid id, MessageState state, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var message = await store.GetByIdAsync(id, CancellationToken.None);
            if (message?.State == state) return;
            await Task.Delay(25);
        }

        var last = await store.GetByIdAsync(id, CancellationToken.None);
        throw new TimeoutException($"Message {id} never reached {state}; it is {last?.State}.");
    }
}
