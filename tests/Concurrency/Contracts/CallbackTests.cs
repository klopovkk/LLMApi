using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Xunit;

namespace Concurrency.Contracts;

/// <summary>
/// POST /callbacks/llm — asynchronous completion (FR-005, FR-014, FR-015, FR-016, FR-020a).
///
/// The provider is held in NeverRespond throughout, so the test itself plays the provider. That
/// exercises the real endpoint with the real payload and keeps the timing deterministic.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CallbackTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Completes_the_message_and_records_the_answer()
    {
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        var userId = UserId();

        var messageId = await SubmitAsync(client, userId, "question");
        await WaitForStateAsync(client, messageId, "Processing");

        var response = await PostCallbackAsync(client, messageId, "completed", answer: "the answer");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var message = await ReadAsync(client, messageId);
        Assert.Equal("Completed", message.GetProperty("state").GetString());
        Assert.Equal("the answer", message.GetProperty("answer").GetString());
        // claimedAt survives completion: it records when processing started, which is what FIFO
        // is asserted from and what tells an operator how long the message actually took.
        Assert.NotEqual(JsonValueKind.Null, message.GetProperty("claimedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, message.GetProperty("finishedAt").ValueKind);
    }

    [Fact]
    public async Task Starts_the_next_message_for_that_user()
    {
        // FR-016: reaching a final state releases the queue, with no client action.
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        var userId = UserId();

        var first = await SubmitAsync(client, userId, "first");
        var second = await SubmitAsync(client, userId, "second");
        await WaitForStateAsync(client, first, "Processing");

        await PostCallbackAsync(client, first, "completed", answer: "done");

        await WaitForStateAsync(client, second, "Processing");
    }

    [Fact]
    public async Task A_failed_callback_still_releases_the_queue()
    {
        // A failure must not permanently block the user (FR-016).
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        var userId = UserId();

        var first = await SubmitAsync(client, userId, "first");
        var second = await SubmitAsync(client, userId, "second");
        await WaitForStateAsync(client, first, "Processing");

        await PostCallbackAsync(client, first, "failed", error: "the model refused");

        var failed = await ReadAsync(client, first);
        Assert.Equal("Failed", failed.GetProperty("state").GetString());
        Assert.Equal("the model refused", failed.GetProperty("failureReason").GetString());

        await WaitForStateAsync(client, second, "Processing");
    }

    [Fact]
    public async Task A_duplicate_callback_completes_once_and_advances_once()
    {
        // FR-014 and SC-010. The second delivery must not complete the message again, and must not
        // advance the queue a second time — which would start a third message out of turn.
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        var userId = UserId();

        var first = await SubmitAsync(client, userId, "first");
        var second = await SubmitAsync(client, userId, "second");
        var third = await SubmitAsync(client, userId, "third");
        await WaitForStateAsync(client, first, "Processing");

        var firstDelivery = await PostCallbackAsync(client, first, "completed", answer: "once");
        await WaitForStateAsync(client, second, "Processing");

        var secondDelivery = await PostCallbackAsync(client, first, "completed", answer: "twice");

        Assert.Equal(HttpStatusCode.NoContent, firstDelivery.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondDelivery.StatusCode);

        var completed = await ReadAsync(client, first);
        Assert.Equal("once", completed.GetProperty("answer").GetString());

        // The third message must still be waiting: the duplicate advanced nothing.
        await Task.Delay(300);
        var thirdState = await ReadAsync(client, third);
        Assert.Equal("Pending", thirdState.GetProperty("state").GetString());
    }

    [Fact]
    public async Task An_unknown_identifier_is_rejected_without_touching_anything_else()
    {
        // FR-015: no other message's turn may be released by a callback naming a message that
        // does not exist.
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        var userId = UserId();

        var held = await SubmitAsync(client, userId, "held");
        await WaitForStateAsync(client, held, "Processing");

        var response = await PostCallbackAsync(client, Guid.NewGuid(), "completed", answer: "ghost");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stillHeld = await ReadAsync(client, held);
        Assert.Equal("Processing", stillHeld.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_malformed_callback_is_rejected()
    {
        await using var instance = NewInstance();
        using var client = instance.CreateClient();

        var response = await client.PostAsJsonAsync("/callbacks/llm",
            new { messageId = Guid.NewGuid(), status = "sideways" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task No_credential_is_required()
    {
        // FR-020a: the endpoint verifies nothing about its caller. Asserted so that adding a
        // credential check later is a deliberate decision that breaks a test, not a drift.
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        var userId = UserId();

        var messageId = await SubmitAsync(client, userId, "question");
        await WaitForStateAsync(client, messageId, "Processing");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/callbacks/llm")
        {
            Content = JsonContent.Create(new { messageId, status = "completed", answer = "anonymous" }),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private InstanceFactory NewInstance() =>
        new(postgres.ConnectionString,
            "callback-tests",
            fakeProviderMode: "NeverRespond",
            sweepInterval: TimeSpan.FromMilliseconds(50));

    private static async Task<Guid> SubmitAsync(HttpClient client, string userId, string content)
    {
        var response = await client.PostAsJsonAsync("/messages", new { userId, content });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("messageId").GetGuid();
    }

    private static Task<HttpResponseMessage> PostCallbackAsync(
        HttpClient client, Guid messageId, string status, string? answer = null, string? error = null) =>
        client.PostAsJsonAsync("/callbacks/llm", new { messageId, status, answer, error });

    private static async Task<JsonElement> ReadAsync(HttpClient client, Guid messageId) =>
        await client.GetFromJsonAsync<JsonElement>($"/messages/{messageId}");

    private static async Task WaitForStateAsync(HttpClient client, Guid messageId, string state)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var message = await ReadAsync(client, messageId);
            if (message.GetProperty("state").GetString() == state) return;
            await Task.Delay(20);
        }

        var last = await ReadAsync(client, messageId);
        throw new TimeoutException(
            $"Message {messageId} never reached {state}; it is {last.GetProperty("state").GetString()}.");
    }

    private static string UserId() => $"cb-{Guid.NewGuid():N}";
}
