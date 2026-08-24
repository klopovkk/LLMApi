using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Xunit;

namespace Concurrency;

/// <summary>
/// Required scenario 5: a callback handled by an instance other than the submitting one
/// (FR-006, SC-006).
///
/// This is the scenario that forbids instance affinity outright. If any part of completing a
/// message needed state left behind by the submitting instance, these would fail.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CrossInstanceCallbackTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_callback_delivered_to_the_other_instance_completes_the_message()
    {
        await using var submitter = NewInstance("submitter");
        await using var receiver = NewInstance("receiver");

        using var submitterClient = submitter.CreateClient();
        using var receiverClient = receiver.CreateClient();
        var userId = $"xcb-{Guid.NewGuid():N}";

        // Submitted on instance 1...
        var messageId = await SubmitAsync(submitterClient, userId, "question");
        await WaitForStateAsync(submitterClient, messageId, "Processing");

        var claimedBy = await ClaimedByAsync(submitterClient, messageId);
        Assert.Equal("submitter", claimedBy);

        // ...completed via instance 2, which has never seen this message before.
        var response = await receiverClient.PostAsJsonAsync("/callbacks/llm",
            new { messageId, status = "completed", answer = "answered elsewhere" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var message = await ReadAsync(submitterClient, messageId);
        Assert.Equal("Completed", message.GetProperty("state").GetString());
        Assert.Equal("answered elsewhere", message.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task The_receiving_instance_starts_the_users_next_message()
    {
        // Not just completion: the queue must advance from whichever instance took the callback,
        // which means the successor claim cannot depend on local state either.
        await using var submitter = NewInstance("adv-submitter");
        await using var receiver = NewInstance("adv-receiver");

        using var submitterClient = submitter.CreateClient();
        using var receiverClient = receiver.CreateClient();
        var userId = $"xadv-{Guid.NewGuid():N}";

        var first = await SubmitAsync(submitterClient, userId, "first");
        var second = await SubmitAsync(submitterClient, userId, "second");
        await WaitForStateAsync(submitterClient, first, "Processing");

        await receiverClient.PostAsJsonAsync("/callbacks/llm",
            new { messageId = first, status = "completed", answer = "done" });

        await WaitForStateAsync(receiverClient, second, "Processing");

        // The successor was started by the instance that received the callback, not the one that
        // accepted the message.
        Assert.Equal("adv-receiver", await ClaimedByAsync(receiverClient, second));
    }

    [Fact]
    public async Task Every_callback_crosses_a_process_boundary_when_instances_point_at_each_other()
    {
        // The composed environment's arrangement (R-011): each instance's provider posts to its
        // peer, so the cross-instance path is exercised on every message rather than by chance.
        await using var one = new InstanceFactory(
            postgres.ConnectionString, "peer-1",
            fakeProviderDelay: TimeSpan.FromMilliseconds(10),
            sweepInterval: TimeSpan.FromMilliseconds(50));
        await using var two = new InstanceFactory(
            postgres.ConnectionString, "peer-2",
            fakeProviderDelay: TimeSpan.FromMilliseconds(10),
            sweepInterval: TimeSpan.FromMilliseconds(50));
        one.CallbackTarget = two;
        two.CallbackTarget = one;

        using var clientOne = one.CreateClient();
        using var clientTwo = two.CreateClient();

        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await SubmitAsync(clientOne, $"peer-{i}-{Guid.NewGuid():N}", $"m{i}"));
        }

        var finished = await SingleUserFifoTests.WaitForAllFinishedAsync(
            clientTwo, ids, TimeSpan.FromSeconds(30));

        Assert.All(finished, m => Assert.Equal("Completed", m.State));
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

    private static async Task<JsonElement> ReadAsync(HttpClient client, Guid messageId) =>
        await client.GetFromJsonAsync<JsonElement>($"/messages/{messageId}");

    private static async Task<string?> ClaimedByAsync(HttpClient client, Guid messageId)
    {
        // Read straight from the store: claimed_by is diagnostics only and deliberately absent
        // from the status contract, because exposing it would invite someone to route by it.
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(
            TestConnectionString.Value ?? throw new InvalidOperationException("No connection string."));
        await using var command = dataSource.CreateCommand(
            "SELECT claimed_by FROM messages WHERE id = @id");
        command.Parameters.AddWithValue("id", messageId);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task WaitForStateAsync(HttpClient client, Guid messageId, string state)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var message = await ReadAsync(client, messageId);
            if (message.GetProperty("state").GetString() == state) return;
            await Task.Delay(20);
        }

        throw new TimeoutException($"Message {messageId} never reached {state}.");
    }
}

/// <summary>Set once by the fixture so helpers can reach the store directly.</summary>
internal static class TestConnectionString
{
    public static string? Value { get; set; }
}
