using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Xunit;

namespace Concurrency;

/// <summary>
/// The targets from R-008, checked loosely.
///
/// These exist to make "no degradation" falsifiable, not because the feature has a throughput
/// mandate. The numbers are an order of magnitude above what an indexed insert costs, deliberately:
/// a tight budget would produce failures that say nothing about correctness, and a concurrency
/// suite that cries wolf about timing is a suite people stop believing.
///
/// Skipped unless RUN_PERF_TESTS is set, so a loaded CI machine cannot fail the build on timing
/// alone. Silence about that would be worse than the skip itself — hence the explicit gate rather
/// than a quietly generous threshold.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PerformanceSmokeTests(PostgresFixture postgres)
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("RUN_PERF_TESTS") is not (null or "" or "0" or "false");

    [SkippableFact]
    public async Task Acceptance_stays_within_the_latency_target()
    {
        Skip.IfNot(Enabled, "Set RUN_PERF_TESTS=1 to run performance smoke tests.");

        await using var instance = new InstanceFactory(
            postgres.ConnectionString, "perf-accept", fakeProviderMode: "NeverRespond");
        using var client = instance.CreateClient();

        // Warm the pool and the JIT: the first request pays for both, and measuring that would be
        // measuring startup rather than acceptance.
        for (var i = 0; i < 10; i++)
        {
            await client.PostAsJsonAsync("/messages",
                new { userId = $"warm-{Guid.NewGuid():N}", content = "warmup" });
        }

        var samples = new List<double>();
        for (var i = 0; i < 100; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await client.PostAsJsonAsync("/messages",
                new { userId = $"perf-{Guid.NewGuid():N}", content = "measured" });
            stopwatch.Stop();

            response.EnsureSuccessStatusCode();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var p95 = samples[(int)(samples.Count * 0.95)];

        Assert.True(p95 < 50, $"Acceptance p95 was {p95:F1} ms; the target is under 50 ms.");
    }

    [SkippableFact]
    public async Task The_successor_starts_promptly_after_its_predecessor_finishes()
    {
        Skip.IfNot(Enabled, "Set RUN_PERF_TESTS=1 to run performance smoke tests.");

        // The one number that matters behaviourally: it is what a user experiences as "messages
        // are answered one after another" rather than "the queue is stuck".
        await using var instance = new InstanceFactory(
            postgres.ConnectionString,
            "perf-successor",
            fakeProviderMode: "NeverRespond",
            sweepInterval: TimeSpan.FromMilliseconds(250));
        using var client = instance.CreateClient();
        var userId = $"succ-{Guid.NewGuid():N}";

        var first = await SubmitAsync(client, userId, "first");
        var second = await SubmitAsync(client, userId, "second");
        await WaitForStateAsync(client, first, "Processing");

        var stopwatch = Stopwatch.StartNew();
        await client.PostAsJsonAsync("/callbacks/llm",
            new { messageId = first, status = "completed", answer = "done" });
        await WaitForStateAsync(client, second, "Processing");
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 100,
            $"The successor took {stopwatch.ElapsedMilliseconds} ms to start; the target is under "
            + "100 ms on the inline path. Above the sweep interval would mean the inline claim is "
            + "not happening and the sweeper is quietly covering for it.");
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string userId, string content)
    {
        var response = await client.PostAsJsonAsync("/messages", new { userId, content });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("messageId").GetGuid();
    }

    private static async Task WaitForStateAsync(HttpClient client, Guid id, string state)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var message = await client.GetFromJsonAsync<JsonElement>($"/messages/{id}");
            if (message.GetProperty("state").GetString() == state) return;
            await Task.Delay(5);
        }

        throw new TimeoutException($"Message {id} never reached {state}.");
    }
}
