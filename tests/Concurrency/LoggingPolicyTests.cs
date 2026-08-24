using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Concurrency;

/// <summary>
/// FR-021: message content must never reach a log.
///
/// The requirement says "informational severity or below", which covers Debug and Trace as well —
/// and logging content at Warning or Error would be a strange way to comply with a rule so
/// obviously aimed at keeping it out of logs entirely. So this asserts the stronger, simpler rule:
/// content appears at no level at all (R-009).
///
/// Asserted by capturing everything the application logs across a full message lifecycle rather
/// than by reviewing call sites, because a review only covers the call sites that exist today.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LoggingPolicyTests(PostgresFixture postgres)
{
    private const string Secret = "correct-horse-battery-staple-9f3c1d";

    [Fact]
    public async Task MessageLifecycle_ContentSubmittedAndFailed_NeverAppearsInTheLogAtAnyLevel()
    {
        // Not AAA: the point is that content stays out of the log across an entire lifecycle, so
        // the test drives several actions in sequence — accept, claim, complete, advance, fail,
        // reject — and asserts once over everything they logged. Each action in its own AAA test
        // would leave the paths between them unchecked, and those are where leaks hide.
        var recorder = new LogRecorder();

        await using var instance = new CapturingInstanceFactory(
            postgres.ConnectionString, "logging", recorder);
        using var client = instance.CreateClient();

        var userId = $"log-{Guid.NewGuid():N}";

        // A full lifecycle: accept, claim, submit, complete, and start the successor.
        var first = await SubmitAsync(client, userId, Secret);
        var second = await SubmitAsync(client, userId, Secret + "-second");
        await WaitForStateAsync(client, first, "Processing");

        await client.PostAsJsonAsync("/callbacks/llm",
            new { messageId = first, status = "completed", answer = "an answer" });
        await WaitForStateAsync(client, second, "Processing");

        // And a failure path, since error handling is where content usually leaks.
        await client.PostAsJsonAsync("/callbacks/llm",
            new { messageId = second, status = "failed", error = "the model refused" });

        await client.PostAsJsonAsync("/messages", new { userId = "", content = Secret });

        // Vacuity guard: a recorder that captured nothing would pass the assertion below without
        // proving anything at all. Both message identifiers must be present, which confirms the
        // lifecycle really was logged and the capture really was wired in.
        Assert.NotEmpty(recorder.Entries);
        Assert.Contains(recorder.Entries, e => e.Contains(first.ToString(), StringComparison.Ordinal));
        Assert.Contains(recorder.Entries, e => e.Contains(second.ToString(), StringComparison.Ordinal));

        var offenders = recorder.Entries
            .Where(e => e.Contains(Secret, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Message content reached the log {offenders.Length} time(s): "
            + string.Join(" | ", offenders.Take(3)));
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
            await Task.Delay(20);
        }

        throw new TimeoutException($"Message {id} never reached {state}.");
    }
}

internal sealed class LogRecorder
{
    private readonly List<string> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Entries
    {
        get { lock (_gate) { return _entries.ToArray(); } }
    }

    public void Record(string entry)
    {
        lock (_gate) _entries.Add(entry);
    }
}

internal sealed class CapturingInstanceFactory(
    string connectionString, string instanceId, LogRecorder recorder)
    : InstanceFactory(connectionString, instanceId,
        fakeProviderMode: "NeverRespond",
        sweepInterval: TimeSpan.FromMilliseconds(50))
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.AddLogging(logging =>
            {
                // Trace, so nothing escapes by being logged below the threshold the requirement
                // names.
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(new RecordingLoggerProvider(recorder));
            });
        });
    }
}

internal sealed class RecordingLoggerProvider(LogRecorder recorder) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(recorder);

    public void Dispose() { }

    private sealed class RecordingLogger(LogRecorder recorder) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            recorder.Record($"[{logLevel}] {formatter(state, exception)} {exception}");
        }
    }
}
