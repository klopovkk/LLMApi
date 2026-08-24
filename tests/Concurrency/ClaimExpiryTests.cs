using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Concurrency;

/// <summary>
/// Required scenario 6: claim expiry (FR-013, FR-013a, FR-013b, SC-011, SC-012).
///
/// With at-most-once callback delivery, this is the *only* recovery path in the system — a lost
/// answer is indistinguishable from a provider that never answered. That makes this test
/// load-bearing rather than incidental, so it is driven by advancing a FakeTimeProvider instead of
/// waiting out a real timeout: a scenario this important must not be one nobody runs because it is
/// slow (R-005).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClaimExpiryTests(PostgresFixture postgres)
{
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ExpireStaleClaims_ClaimHeldPastTimeout_FailsMessageAndStartsSuccessor()
    {
        // Not AAA: the claim is that the message expires *at* the bound and not before, so the
        // clock is advanced twice with an assertion between. The negative assertion before the
        // boundary is what separates a working timeout from code that fails everything at once.
        var time = NewClock();
        await using var instance = NewInstance(time, "expiry");
        using var client = instance.CreateClient();
        var userId = $"expire-{Guid.NewGuid():N}";

        var first = await SubmitAsync(client, userId, "never answered");
        var second = await SubmitAsync(client, userId, "waiting behind it");
        await WaitForStateAsync(client, first, "Processing");

        // Nothing has expired yet.
        time.Advance(ClaimTimeout - TimeSpan.FromSeconds(1));
        await Task.Delay(200);
        Assert.Equal("Processing", await StateAsync(client, first));
        Assert.Equal("Pending", await StateAsync(client, second));

        // Past the bound.
        time.Advance(TimeSpan.FromSeconds(2));

        await WaitForStateAsync(client, first, "Failed");
        var expired = await ReadAsync(client, first);
        Assert.Contains(
            "expired",
            expired.GetProperty("failureReason").GetString()!,
            StringComparison.OrdinalIgnoreCase);

        // The queue moved on: the successor is running (SC-011).
        await WaitForStateAsync(client, second, "Processing");
    }

    [Fact]
    public async Task ExpireStaleClaims_MessageAlreadyExpired_NeverResubmitsIt()
    {
        // FR-013a: expiry is final. No path returns a message to Pending, so each accepted message
        // is submitted at most once and an expired one is reported to the client as failed rather
        // than quietly retried.
        // Not AAA: time is advanced, expiry asserted, then advanced much further to prove no
        // later sweep resurrects the message. The second act only means something after the first
        // assertion has established the message was already final.
        var time = NewClock();
        await using var instance = NewInstance(time, "no-resubmit");
        using var client = instance.CreateClient();
        var userId = $"noretry-{Guid.NewGuid():N}";

        var id = await SubmitAsync(client, userId, "will expire");
        await WaitForStateAsync(client, id, "Processing");

        time.Advance(ClaimTimeout + TimeSpan.FromSeconds(1));
        await WaitForStateAsync(client, id, "Failed");

        // Several more sweeps must not resurrect it.
        time.Advance(ClaimTimeout * 3);
        await Task.Delay(400);

        Assert.Equal("Failed", await StateAsync(client, id));
    }

    [Fact]
    public async Task PostCallback_ArrivesAfterExpiry_LeavesMessageFailedAndActiveMessageUntouched()
    {
        // FR-013b and SC-012: the late answer must not revive the expired message, and must not
        // disturb whichever message is active for that user by then.
        // Arrange — submit two, let the first expire, so a late callback has something to fail
        // against and a successor it must not disturb.
        var time = NewClock();
        await using var instance = NewInstance(time, "late-callback");
        using var client = instance.CreateClient();
        var userId = $"late-{Guid.NewGuid():N}";

        var first = await SubmitAsync(client, userId, "expires");
        var second = await SubmitAsync(client, userId, "the new active one");
        await WaitForStateAsync(client, first, "Processing");

        time.Advance(ClaimTimeout + TimeSpan.FromSeconds(1));
        await WaitForStateAsync(client, first, "Failed");
        await WaitForStateAsync(client, second, "Processing");

        // Act — the answer finally turns up, far too late.
        var response = await client.PostAsJsonAsync("/callbacks/llm",
            new { messageId = first, status = "completed", answer = "too late" });

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var stillFailed = await ReadAsync(client, first);
        Assert.Equal("Failed", stillFailed.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, stillFailed.GetProperty("answer").ValueKind);

        // And the currently active message is untouched — this is the assertion that would catch a
        // late callback wrongly advancing the queue and starting a third message out of turn.
        Assert.Equal("Processing", await StateAsync(client, second));
    }

    [Fact]
    public async Task ExpireStaleClaims_ClaimStillWithinTimeout_LeavesMessageProcessing()
    {
        // Arrange
        var time = NewClock();
        await using var instance = NewInstance(time, "within-bound");
        using var client = instance.CreateClient();

        var userId = $"within-{Guid.NewGuid():N}";
        var id = await SubmitAsync(client, userId, "still working");
        await WaitForStateAsync(client, id, "Processing");

        // Act
        time.Advance(ClaimTimeout - TimeSpan.FromSeconds(5));
        await Task.Delay(300);

        // Assert
        Assert.Equal("Processing", await StateAsync(client, id));
    }

    private static FakeTimeProvider NewClock() => new(DateTimeOffset.UtcNow);

    private InstanceFactory NewInstance(FakeTimeProvider time, string id) =>
        new(postgres.ConnectionString,
            id,
            claimTimeout: ClaimTimeout,
            sweepInterval: TimeSpan.FromMilliseconds(50),
            fakeProviderMode: "NeverRespond",
            timeProvider: time);

    private static async Task<Guid> SubmitAsync(HttpClient client, string userId, string content)
    {
        var response = await client.PostAsJsonAsync("/messages", new { userId, content });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("messageId").GetGuid();
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, Guid id) =>
        await client.GetFromJsonAsync<JsonElement>($"/messages/{id}");

    private static async Task<string?> StateAsync(HttpClient client, Guid id) =>
        (await ReadAsync(client, id)).GetProperty("state").GetString();

    private static async Task WaitForStateAsync(HttpClient client, Guid id, string state)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await StateAsync(client, id) == state) return;
            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Message {id} never reached {state}; it is {await StateAsync(client, id)}.");
    }
}
