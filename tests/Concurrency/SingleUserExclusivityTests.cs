using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Npgsql;
using Xunit;

namespace Concurrency;

/// <summary>
/// Required scenario 2: exclusivity for a single user (FR-003, FR-012, SC-003).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SingleUserExclusivityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task SubmitMessage_FifteenMessagesForOneUser_NeverTwoActiveAtOnce()
    {
        // Not AAA: the assertion is continuous rather than final. Exclusivity is a claim about
        // every instant while the queue drains, so the store is sampled throughout the action and
        // the verdict is over the whole window. A single reading afterwards would prove nothing.
        await using var instance = new InstanceFactory(
            postgres.ConnectionString,
            "exclusivity",
            fakeProviderDelay: TimeSpan.FromMilliseconds(20),
            sweepInterval: TimeSpan.FromMilliseconds(25));
        using var client = instance.CreateClient();
        var userId = $"excl-{Guid.NewGuid():N}";

        var ids = new List<Guid>();
        for (var i = 0; i < 15; i++)
        {
            var response = await client.PostAsJsonAsync("/messages",
                new { userId, content = $"message {i}" });
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            ids.Add(body.GetProperty("messageId").GetGuid());
        }

        // Poll the store while the queue drains. Any sample showing two Processing rows for this
        // user is a violation — and because exclusivity is a partial unique index, seeing one
        // would mean the index is missing rather than that the timing was unlucky.
        var maxObservedActive = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var allFinished = false;

        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);

        while (DateTimeOffset.UtcNow < deadline && !allFinished)
        {
            await using (var command = dataSource.CreateCommand(
                "SELECT count(*) FROM messages WHERE user_id = @userId AND state = 'Processing'"))
            {
                command.Parameters.AddWithValue("userId", userId);
                var active = Convert.ToInt32(await command.ExecuteScalarAsync());
                maxObservedActive = Math.Max(maxObservedActive, active);
            }

            await using (var command = dataSource.CreateCommand(
                "SELECT count(*) FROM messages WHERE user_id = @userId AND state IN ('Pending', 'Processing')"))
            {
                command.Parameters.AddWithValue("userId", userId);
                allFinished = Convert.ToInt32(await command.ExecuteScalarAsync()) == 0;
            }
        }

        Assert.True(allFinished, "The user's queue never drained.");
        Assert.True(
            maxObservedActive <= 1,
            $"Observed {maxObservedActive} simultaneously active messages for one user.");
    }

    [Fact]
    public async Task SubmitMessage_FirstMessageHeldUnanswered_SecondStaysPending()
    {
        // NeverRespond holds the first message open, so the second must sit in Pending
        // indefinitely rather than starting alongside it.
        // Arrange
        await using var instance = new InstanceFactory(
            postgres.ConnectionString,
            "exclusivity-hold",
            fakeProviderMode: "NeverRespond",
            sweepInterval: TimeSpan.FromMilliseconds(25));
        using var client = instance.CreateClient();
        var userId = $"hold-{Guid.NewGuid():N}";

        var firstId = await SubmitAsync(client, userId, "first");
        var secondId = await SubmitAsync(client, userId, "second");

        // Act — give the sweeper several passes: if exclusivity were broken, this is when it
        // would show.
        await Task.Delay(500);

        // Assert
        var snapshots = await SingleUserFifoTests.SnapshotAsync(client, new[] { firstId, secondId });
        var first = snapshots.Single(s => s.Id == firstId);
        var second = snapshots.Single(s => s.Id == secondId);

        Assert.Equal("Processing", first.State);
        Assert.Equal("Pending", second.State);
        Assert.Null(second.ClaimedAt);
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string userId, string content)
    {
        var response = await client.PostAsJsonAsync("/messages", new { userId, content });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("messageId").GetGuid();
    }
}
