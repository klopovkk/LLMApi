using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Xunit;

namespace Concurrency.Contracts;

/// <summary>
/// POST /messages — acceptance without client-side coordination (FR-001, FR-009, FR-009a, FR-017).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SubmitMessageTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Accepts_a_submission_and_returns_an_identifier()
    {
        await using var instance = NewInstance();
        using var client = instance.CreateClient();

        var response = await client.PostAsJsonAsync("/messages",
            new { userId = UserId(), content = "hello" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, body.GetProperty("messageId").GetGuid());
        Assert.True(body.GetProperty("sequence").GetInt64() > 0);
        Assert.Equal("Pending", body.GetProperty("state").GetString());
        Assert.True(body.GetProperty("acceptedAt").GetDateTimeOffset() > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task Accepts_rapid_submissions_without_the_client_coordinating()
    {
        // The point of FR-001: fire everything at once, wait for nothing, and expect the
        // middleware to sort out the ordering.
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        var userId = UserId();

        var submissions = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            client.PostAsJsonAsync("/messages", new { userId, content = $"message {i}" })));

        Assert.All(submissions, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));

        var accepted = await Task.WhenAll(
            submissions.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));

        var ids = accepted.Select(a => a.GetProperty("messageId").GetGuid()).ToArray();
        Assert.Equal(10, ids.Distinct().Count());

        var sequences = accepted.Select(a => a.GetProperty("sequence").GetInt64()).ToArray();
        Assert.Equal(10, sequences.Distinct().Count());
    }

    [Fact]
    public async Task Identical_submissions_become_two_distinct_messages()
    {
        // FR-009a: no de-duplication, no idempotency key. A retry is genuinely a second message.
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        var userId = UserId();
        var payload = new { userId, content = "the very same words" };

        var first = await client.PostAsJsonAsync("/messages", payload);
        var second = await client.PostAsJsonAsync("/messages", payload);

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.NotEqual(
            firstBody.GetProperty("messageId").GetGuid(),
            secondBody.GetProperty("messageId").GetGuid());
        Assert.True(
            secondBody.GetProperty("sequence").GetInt64()
            > firstBody.GetProperty("sequence").GetInt64());
    }

    [Theory]
    [InlineData("", "content")]
    [InlineData("   ", "content")]
    [InlineData("user", "")]
    [InlineData("user", "   ")]
    public async Task Rejects_a_blank_user_or_content(string userId, string content)
    {
        // FR-017: rejected at acceptance, and no queued state created.
        await using var instance = NewInstance();
        using var client = instance.CreateClient();

        var response = await client.PostAsJsonAsync("/messages", new { userId, content });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_submission_with_missing_fields()
    {
        await using var instance = NewInstance();
        using var client = instance.CreateClient();

        var response = await client.PostAsJsonAsync("/messages", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private InstanceFactory NewInstance() =>
        new(postgres.ConnectionString, "submit-tests", fakeProviderMode: "NeverRespond");

    private static string UserId() => $"user-{Guid.NewGuid():N}";
}
