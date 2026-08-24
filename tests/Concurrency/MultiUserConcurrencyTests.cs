using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Npgsql;
using Xunit;

namespace Concurrency;

/// <summary>
/// Required scenario 3: concurrent processing for different users (FR-004, SC-004).
///
/// Exclusivity is worthless if it is achieved by serializing everything, so these assert the
/// opposite of the single-user tests: that distinct users genuinely overlap.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MultiUserConcurrencyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_held_user_delays_nobody_else()
    {
        // The held user's provider never answers, so their message sits in Processing for the
        // whole test. Everyone else must sail past.
        await using var held = new InstanceFactory(
            postgres.ConnectionString, "held", fakeProviderMode: "NeverRespond");
        await using var others = new InstanceFactory(
            postgres.ConnectionString,
            "others",
            fakeProviderDelay: TimeSpan.FromMilliseconds(10),
            sweepInterval: TimeSpan.FromMilliseconds(50));
        others.CallbackTarget = others;

        using var heldClient = held.CreateClient();
        using var othersClient = others.CreateClient();

        var heldUser = $"held-{Guid.NewGuid():N}";
        var heldId = await SubmitAsync(heldClient, heldUser, "never answered");

        var otherIds = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            otherIds.Add(await SubmitAsync(othersClient, $"other-{i}-{Guid.NewGuid():N}", $"quick {i}"));
        }

        var finished = await SingleUserFifoTests.WaitForAllFinishedAsync(
            othersClient, otherIds, TimeSpan.FromSeconds(30));

        Assert.All(finished, m => Assert.Equal("Completed", m.State));

        var stillHeld = await othersClient.GetFromJsonAsync<JsonElement>($"/messages/{heldId}");
        Assert.Equal("Processing", stillHeld.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Several_users_are_processed_at_the_same_moment()
    {
        // Proves parallelism rather than merely fast serialization: at some observed instant more
        // than one message must be Processing across distinct users. Per-user exclusivity still
        // holds, because each of these users has exactly one message.
        await using var instance = new InstanceFactory(
            postgres.ConnectionString,
            "parallel",
            fakeProviderMode: "NeverRespond",
            sweepInterval: TimeSpan.FromMilliseconds(25));
        using var client = instance.CreateClient();

        var marker = Guid.NewGuid().ToString("N");
        var users = Enumerable.Range(0, 8).Select(i => $"par-{marker}-{i}").ToArray();

        foreach (var user in users)
        {
            await SubmitAsync(client, user, "held open");
        }

        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var maxConcurrent = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTimeOffset.UtcNow < deadline && maxConcurrent < users.Length)
        {
            await using var command = dataSource.CreateCommand(
                "SELECT count(*) FROM messages WHERE state = 'Processing' AND user_id LIKE @pattern");
            command.Parameters.AddWithValue("pattern", $"par-{marker}-%");
            maxConcurrent = Math.Max(maxConcurrent, Convert.ToInt32(await command.ExecuteScalarAsync()));
            if (maxConcurrent < users.Length) await Task.Delay(20);
        }

        Assert.True(
            maxConcurrent > 1,
            $"Only ever saw {maxConcurrent} message(s) processing at once across {users.Length} "
            + "distinct users — users are being serialized against each other.");
        Assert.Equal(users.Length, maxConcurrent);
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string userId, string content)
    {
        var response = await client.PostAsJsonAsync("/messages", new { userId, content });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("messageId").GetGuid();
    }
}
