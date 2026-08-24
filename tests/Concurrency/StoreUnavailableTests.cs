using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Npgsql;
using Xunit;

namespace Concurrency;

/// <summary>
/// Required scenario 7: the shared store is unreachable (FR-022, FR-022a, FR-022b, SC-013).
///
/// Coordination lives entirely in the store, so with it down the instance cannot know a user's
/// order or whether anyone is already being answered. Accepting anyway would mean acknowledging a
/// message it cannot honour or even remember, which is exactly what Principle I forbids.
///
/// The outage is simulated by pointing an instance at an address nothing is listening on. That is
/// a truer test than stopping the shared container would be, because it leaves the other tests in
/// the collection running against a live store.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StoreUnavailableTests(PostgresFixture postgres)
{
    private const string Unreachable =
        "Host=127.0.0.1;Port=1;Database=nowhere;Username=none;Password=none;Timeout=1;Command Timeout=1";

    [Fact]
    public async Task Submission_is_refused_as_retryable_and_issues_no_identifier()
    {
        await using var instance = new InstanceFactory(Unreachable, "outage-submit");
        using var client = instance.CreateClient();

        var response = await client.PostAsJsonAsync("/messages",
            new { userId = "someone", content = "during the outage" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("messageId", body, StringComparison.OrdinalIgnoreCase);

        // The client is told this is worth retrying, rather than being left to guess.
        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Contains(
            "retr",
            problem.GetProperty("detail").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reads_and_callbacks_are_refused_too()
    {
        await using var instance = new InstanceFactory(Unreachable, "outage-read");
        using var client = instance.CreateClient();

        var read = await client.GetAsync($"/messages/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);

        var callback = await client.PostAsJsonAsync("/callbacks/llm",
            new { messageId = Guid.NewGuid(), status = "completed", answer = "lost" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, callback.StatusCode);
    }

    [Fact]
    public async Task Health_reports_the_outage_while_the_instance_stays_up()
    {
        // The instance being alive and the store being reachable are different facts, and an
        // operator needs to tell them apart.
        await using var instance = new InstanceFactory(Unreachable, "outage-health");
        using var client = instance.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("storeReachable").GetBoolean());
        Assert.Equal("outage-health", body.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task Nothing_is_buffered_and_no_phantom_message_appears_afterwards()
    {
        // FR-022a. If an instance held refused submissions in memory and flushed them later, this
        // is where it would show: the user would acquire messages nobody was ever told about.
        var userId = $"outage-{Guid.NewGuid():N}";

        await using (var offline = new InstanceFactory(Unreachable, "outage-buffer"))
        {
            using var offlineClient = offline.CreateClient();

            for (var i = 0; i < 5; i++)
            {
                var response = await offlineClient.PostAsJsonAsync("/messages",
                    new { userId, content = $"refused {i}" });
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            }
        }

        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM messages WHERE user_id = @u");
        command.Parameters.AddWithValue("u", userId);

        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task An_instance_survives_the_outage_and_serves_normally_once_the_store_returns()
    {
        // FR-022b: recovery is unattended. The instance starts against a proxy that is switched
        // off — so even schema application fails — and must become useful when the store comes
        // back, without a restart and without anyone intervening.
        await using var proxy = new StoreProxy(postgres.ConnectionString);
        // Deliberately not opened yet: the instance boots into an outage.

        await using var instance = new InstanceFactory(
            proxy.ConnectionString, "outage-recover", sweepInterval: TimeSpan.FromMilliseconds(100));
        using var client = instance.CreateClient();

        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync("/health")).StatusCode);

        // The store returns, at the same address it always had.
        proxy.Open();

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        HttpStatusCode status;
        do
        {
            status = (await client.GetAsync("/health")).StatusCode;
            if (status == HttpStatusCode.OK) break;
            await Task.Delay(100);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Equal(HttpStatusCode.OK, status);

        var submission = await client.PostAsJsonAsync("/messages",
            new { userId = $"recovered-{Guid.NewGuid():N}", content = "after recovery" });

        Assert.Equal(HttpStatusCode.Accepted, submission.StatusCode);
    }

    [Fact]
    public async Task Work_pending_before_an_outage_resumes_afterwards()
    {
        // The other half of FR-022b: not just that the instance answers again, but that work it
        // already accepted is picked up rather than stranded.
        await using var proxy = new StoreProxy(postgres.ConnectionString);
        proxy.Open();

        await using var instance = new InstanceFactory(
            proxy.ConnectionString,
            "outage-resume",
            fakeProviderMode: "NeverRespond",
            sweepInterval: TimeSpan.FromMilliseconds(100));
        using var client = instance.CreateClient();

        var userId = $"resume-outage-{Guid.NewGuid():N}";
        var accepted = await client.PostAsJsonAsync("/messages",
            new { userId, content = "accepted before the outage" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        proxy.Close();
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync("/health")).StatusCode);

        proxy.Open();

        // Unattended: nobody restarts anything, and the sweeper picks the work back up.
        await using var direct = NpgsqlDataSource.Create(postgres.ConnectionString);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var processing = 0;

        while (DateTimeOffset.UtcNow < deadline && processing == 0)
        {
            await using var command = direct.CreateCommand(
                "SELECT count(*) FROM messages WHERE user_id = @u AND state = 'Processing'");
            command.Parameters.AddWithValue("u", userId);
            processing = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (processing == 0) await Task.Delay(100);
        }

        Assert.Equal(1, processing);
    }

    [Fact]
    public async Task No_outage_response_contains_message_content()
    {
        await using var instance = new InstanceFactory(Unreachable, "outage-content");
        using var client = instance.CreateClient();
        const string secret = "correct-horse-battery-staple";

        var response = await client.PostAsJsonAsync("/messages",
            new { userId = "someone", content = secret });

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
    }
}
