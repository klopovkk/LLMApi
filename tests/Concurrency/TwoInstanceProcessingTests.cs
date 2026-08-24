using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Xunit;

namespace Concurrency;

/// <summary>
/// Required scenario 4: correct processing across two middleware instances (FR-007).
///
/// Two hosts, separate application state, one shared store — the deployment the feature is defined
/// by. Anything that held in the single-instance tests but depended on process memory fails here.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TwoInstanceProcessingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task SubmitMessage_OneUsersStreamSplitAcrossTwoInstances_ProcessingStartsInAcceptanceOrder()
    {
        // Arrange
        await using var one = NewInstance("instance-1");
        await using var two = NewInstance("instance-2");
        one.CallbackTarget = two;
        two.CallbackTarget = one;

        using var clientOne = one.CreateClient();
        using var clientTwo = two.CreateClient();
        var userId = $"two-inst-{Guid.NewGuid():N}";

        // Act — alternate instances so the user's stream is split across both.
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var client = i % 2 == 0 ? clientOne : clientTwo;
            ids.Add(await SubmitAsync(client, userId, $"message {i}"));
        }

        // Assert
        var finished = await SingleUserFifoTests.WaitForAllFinishedAsync(
            clientOne, ids, TimeSpan.FromSeconds(60));
        var ordered = finished.OrderBy(m => m.Sequence).ToArray();

        for (var i = 1; i < ordered.Length; i++)
        {
            Assert.True(
                ordered[i - 1].ClaimedAt <= ordered[i].ClaimedAt,
                $"Sequence {ordered[i - 1].Sequence} started at {ordered[i - 1].ClaimedAt}, after "
                + $"sequence {ordered[i].Sequence} at {ordered[i].ClaimedAt}. Ordering did not "
                + "survive being split across two instances.");
        }
    }

    [Fact]
    public async Task SubmitMessage_BothInstancesWorkingTheSameUser_NeverTwoActiveAtOnce()
    {
        // Not AAA: like the single-instance exclusivity test, the assertion is continuous. The
        // store is sampled while both instances work the same user, because the claim is about
        // every instant of the run rather than its end state.
        await using var one = NewInstance("excl-1", TimeSpan.FromMilliseconds(30));
        await using var two = NewInstance("excl-2", TimeSpan.FromMilliseconds(30));
        one.CallbackTarget = two;
        two.CallbackTarget = one;

        using var clientOne = one.CreateClient();
        using var clientTwo = two.CreateClient();
        var userId = $"two-excl-{Guid.NewGuid():N}";

        var ids = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add(await SubmitAsync(i % 2 == 0 ? clientOne : clientTwo, userId, $"m{i}"));
        }

        await using var dataSource = Npgsql.NpgsqlDataSource.Create(postgres.ConnectionString);
        var maxActive = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        var drained = false;

        while (DateTimeOffset.UtcNow < deadline && !drained)
        {
            await using (var command = dataSource.CreateCommand(
                "SELECT count(*) FROM messages WHERE user_id = @u AND state = 'Processing'"))
            {
                command.Parameters.AddWithValue("u", userId);
                maxActive = Math.Max(maxActive, Convert.ToInt32(await command.ExecuteScalarAsync()));
            }

            await using (var command = dataSource.CreateCommand(
                "SELECT count(*) FROM messages WHERE user_id = @u AND state IN ('Pending','Processing')"))
            {
                command.Parameters.AddWithValue("u", userId);
                drained = Convert.ToInt32(await command.ExecuteScalarAsync()) == 0;
            }
        }

        Assert.True(drained, "The user's queue never drained across two instances.");
        Assert.True(maxActive <= 1, $"Observed {maxActive} active messages for one user across two instances.");
    }

    private InstanceFactory NewInstance(string id, TimeSpan? delay = null) =>
        new(postgres.ConnectionString,
            id,
            fakeProviderDelay: delay ?? TimeSpan.FromMilliseconds(10),
            sweepInterval: TimeSpan.FromMilliseconds(50));

    private static async Task<Guid> SubmitAsync(HttpClient client, string userId, string content)
    {
        var response = await client.PostAsJsonAsync("/messages", new { userId, content });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("messageId").GetGuid();
    }
}
