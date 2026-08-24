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
    public async Task SubmitMessage_ValidSubmission_ReturnsAcceptedWithAnIdentifier()
    {
        // Arrange
        await using var instance = NewInstance();
        using var client = instance.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/messages",
            new { userId = UserId(), content = "hello" });

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, body.GetProperty("messageId").GetGuid());
        Assert.True(body.GetProperty("sequence").GetInt64() > 0);
        Assert.Equal("Pending", body.GetProperty("state").GetString());
        Assert.True(body.GetProperty("acceptedAt").GetDateTimeOffset() > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task SubmitMessage_TenConcurrentSubmissionsForOneUser_AcceptsAllWithDistinctIdentifiers()
    {
        // The point of FR-001: fire everything at once, wait for nothing, and expect the
        // middleware to sort out the ordering.
        // Arrange
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        var userId = UserId();

        // Act
        var submissions = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            client.PostAsJsonAsync("/messages", new { userId, content = $"message {i}" })));

        // Assert
        Assert.All(submissions, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));

        var accepted = await Task.WhenAll(
            submissions.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));

        var ids = accepted.Select(a => a.GetProperty("messageId").GetGuid()).ToArray();
        Assert.Equal(10, ids.Distinct().Count());

        var sequences = accepted.Select(a => a.GetProperty("sequence").GetInt64()).ToArray();
        Assert.Equal(10, sequences.Distinct().Count());
    }

    [Fact]
    public async Task SubmitMessage_IdenticalContentSubmittedTwice_CreatesTwoDistinctMessages()
    {
        // FR-009a: no de-duplication, no idempotency key. A retry is genuinely a second message.
        // Arrange
        await using var instance = NewInstance();
        using var client = instance.CreateClient();
        var userId = UserId();
        var payload = new { userId, content = "the very same words" };

        // Act
        var first = await client.PostAsJsonAsync("/messages", payload);
        var second = await client.PostAsJsonAsync("/messages", payload);

        // Assert
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
    public async Task SubmitMessage_BlankUserIdOrContent_ReturnsBadRequest(string userId, string content)
    {
        // FR-017: rejected at acceptance, and no queued state created.
        // Arrange
        await using var instance = NewInstance();
        using var client = instance.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/messages", new { userId, content });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SubmitMessage_FieldsMissingEntirely_ReturnsBadRequest()
    {
        // Arrange
        await using var instance = NewInstance();
        using var client = instance.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/messages", new { });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private InstanceFactory NewInstance() =>
        new(postgres.ConnectionString, "submit-tests", fakeProviderMode: "NeverRespond");

    private static string UserId() => $"user-{Guid.NewGuid():N}";
}
