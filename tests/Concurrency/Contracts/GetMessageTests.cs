using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Xunit;

namespace Concurrency.Contracts;

/// <summary>
/// GET /messages/{messageId} — reading state and result back (FR-010, FR-015).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GetMessageTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Reports_the_state_of_an_accepted_message()
    {
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        var userId = UserId();

        var submission = await client.PostAsJsonAsync("/messages",
            new { userId, content = "hello" });
        var accepted = await submission.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = accepted.GetProperty("messageId").GetGuid();

        var response = await client.GetAsync($"/messages/{messageId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(messageId, body.GetProperty("messageId").GetGuid());
        Assert.Equal(userId, body.GetProperty("userId").GetString());
        Assert.Equal(
            accepted.GetProperty("sequence").GetInt64(),
            body.GetProperty("sequence").GetInt64());
        Assert.Contains(body.GetProperty("state").GetString(), new[] { "Pending", "Processing" });
        Assert.True(body.TryGetProperty("acceptedAt", out _));
        Assert.True(body.TryGetProperty("claimedAt", out _));
        Assert.True(body.TryGetProperty("finishedAt", out _));
    }

    [Fact]
    public async Task Never_echoes_the_submitted_content()
    {
        // Content must not travel any further than it has to. The read path has no reason to
        // carry it, and not carrying it removes a way for it to reach a log (FR-021).
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        const string secret = "correct-horse-battery-staple";

        var submission = await client.PostAsJsonAsync("/messages",
            new { userId = UserId(), content = secret });
        var messageId = (await submission.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("messageId").GetGuid();

        var raw = await client.GetStringAsync($"/messages/{messageId}");

        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Returns_404_for_an_unknown_identifier()
    {
        await using var instance = NewInstance();
        using var client = instance.CreateClient();

        var response = await client.GetAsync($"/messages/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private InstanceFactory NewInstance() =>
        new(postgres.ConnectionString, "get-tests", fakeProviderMode: "NeverRespond");

    private static string UserId() => $"user-{Guid.NewGuid():N}";
}
