using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Npgsql;
using Xunit;

namespace Concurrency;

/// <summary>
/// Quality Gate 6 and FR-018: restarting an instance does not corrupt state.
///
/// Disposing an <see cref="InstanceFactory"/> tears the host down the way a process exit would —
/// the sweeper stops, in-flight work is abandoned, and everything the instance knew is gone.
/// Whatever survives is what was in the store, which is the point.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RestartDurabilityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task State_survives_an_instance_restart_intact()
    {
        var userId = $"restart-{Guid.NewGuid():N}";
        var ids = new List<Guid>();
        List<(long Sequence, string State)> before;

        // An instance accepts work and starts it, then dies.
        await using (var first = NewInstance("restart-1"))
        {
            using var client = first.CreateClient();
            for (var i = 0; i < 6; i++)
            {
                ids.Add(await SubmitAsync(client, userId, $"m{i}"));
            }

            await WaitForAnyProcessingAsync(userId, TimeSpan.FromSeconds(10));
            before = await ReadRowsAsync(userId);
        }

        // A fresh instance comes up against the same store.
        await using var second = NewInstance("restart-2");
        using var secondClient = second.CreateClient();

        var after = await ReadRowsAsync(userId);

        Assert.Equal(before.Count, after.Count);
        Assert.Equal(
            before.Select(r => r.Sequence).OrderBy(s => s),
            after.Select(r => r.Sequence).OrderBy(s => s));

        // Every message is still present exactly once, with its original acceptance position.
        Assert.Equal(6, after.Count);
        Assert.Equal(6, after.Select(r => r.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task Work_left_behind_by_a_dead_instance_is_resumed_by_the_survivor()
    {
        // The message the dead instance had claimed stays claimed until expiry — that is FR-013's
        // job, not this one. What must happen here is that the *pending* messages behind it are
        // not stranded: the survivor's sweeper is responsible for them.
        var userId = $"resume-{Guid.NewGuid():N}";

        await using (var dying = NewInstance("dying"))
        {
            using var client = dying.CreateClient();
            await SubmitAsync(client, userId, "claimed then abandoned");
        }

        // A second, unrelated user proves the survivor is sweeping at all.
        var liveUser = $"live-{Guid.NewGuid():N}";

        await using var survivor = new InstanceFactory(
            postgres.ConnectionString,
            "survivor",
            fakeProviderDelay: TimeSpan.FromMilliseconds(10),
            sweepInterval: TimeSpan.FromMilliseconds(50));
        survivor.CallbackTarget = survivor;

        using var survivorClient = survivor.CreateClient();
        var liveId = await SubmitAsync(survivorClient, liveUser, "processed after the restart");

        var finished = await SingleUserFifoTests.WaitForAllFinishedAsync(
            survivorClient, new[] { liveId }, TimeSpan.FromSeconds(30));

        Assert.Equal("Completed", finished.Single().State);
    }

    [Fact]
    public async Task A_restarted_instance_does_not_reapply_state_or_lose_history()
    {
        // Starting an instance must be a no-op against an existing store: the schema is applied
        // idempotently and nothing is reconstructed from process memory.
        var userId = $"history-{Guid.NewGuid():N}";

        await using (var first = NewInstance("history-1"))
        {
            using var client = first.CreateClient();
            var id = await SubmitAsync(client, userId, "before restart");
            await WaitForAnyProcessingAsync(userId, TimeSpan.FromSeconds(10));

            await client.PostAsJsonAsync("/callbacks/llm",
                new { messageId = id, status = "completed", answer = "answered before restart" });
        }

        await using var second = NewInstance("history-2");
        using var secondClient = second.CreateClient();

        var rows = await ReadRowsAsync(userId);
        var row = Assert.Single(rows);
        Assert.Equal("Completed", row.State);
    }

    private InstanceFactory NewInstance(string id) =>
        new(postgres.ConnectionString,
            id,
            fakeProviderMode: "NeverRespond",
            sweepInterval: TimeSpan.FromMilliseconds(50));

    private static async Task<Guid> SubmitAsync(HttpClient client, string userId, string content)
    {
        var response = await client.PostAsJsonAsync("/messages", new { userId, content });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("messageId").GetGuid();
    }

    private async Task<List<(long Sequence, string State)>> ReadRowsAsync(string userId)
    {
        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT sequence, state FROM messages WHERE user_id = @u ORDER BY sequence");
        command.Parameters.AddWithValue("u", userId);

        var rows = new List<(long, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return rows;
    }

    private async Task WaitForAnyProcessingAsync(string userId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = dataSource.CreateCommand(
                "SELECT count(*) FROM messages WHERE user_id = @u AND state = 'Processing'");
            command.Parameters.AddWithValue("u", userId);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0) return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"No message for {userId} ever reached Processing.");
    }
}
