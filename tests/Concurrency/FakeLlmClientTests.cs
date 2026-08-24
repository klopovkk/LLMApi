using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LLM.Abstraction;
using LLM.FakeProvider;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Concurrency;

/// <summary>
/// The fake provider's contract (FR-019, FR-019a, and the provider obligations in
/// contracts/llm-boundary.md).
/// </summary>
public sealed class FakeLlmClientTests
{
    [Fact]
    public async Task Submit_returns_before_the_answer_exists()
    {
        // Principle IV: the caller is not waiting. If SubmitAsync blocked for the delay, provider
        // latency would become request latency and the middleware would be holding a request open
        // across the wait.
        using var recorder = new CallbackRecorder();
        var client = NewClient(recorder, delay: TimeSpan.FromMilliseconds(400));

        var stopwatch = Stopwatch.StartNew();
        await client.SubmitAsync(NewSubmission(recorder), CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 200,
            $"SubmitAsync blocked for {stopwatch.ElapsedMilliseconds} ms; it must return promptly.");
        Assert.Empty(recorder.Received);
    }

    [Fact]
    public async Task Posts_exactly_one_completion_over_http()
    {
        using var recorder = new CallbackRecorder();
        var client = NewClient(recorder, delay: TimeSpan.FromMilliseconds(20));
        var submission = NewSubmission(recorder);

        await client.SubmitAsync(submission, CancellationToken.None);
        await recorder.WaitForAsync(1, TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        var completion = Assert.Single(recorder.Received);
        Assert.Equal(submission.MessageId, completion.MessageId);
        Assert.True(completion.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(completion.Answer));
    }

    [Fact]
    public async Task The_answer_is_derived_from_the_identifier_and_never_from_the_content()
    {
        // Nothing that must stay out of logs may come back by way of an echoed answer.
        using var recorder = new CallbackRecorder();
        var client = NewClient(recorder, delay: TimeSpan.FromMilliseconds(20));
        const string secret = "correct-horse-battery-staple";

        await client.SubmitAsync(
            new LlmSubmission(Guid.NewGuid(), "user", secret, recorder.CallbackUri),
            CancellationToken.None);
        await recorder.WaitForAsync(1, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(secret, recorder.Received[0].Answer ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fail_mode_reports_a_provider_failure()
    {
        using var recorder = new CallbackRecorder();
        var client = NewClient(recorder, delay: TimeSpan.FromMilliseconds(20), mode: FakeProviderMode.Fail);

        await client.SubmitAsync(NewSubmission(recorder), CancellationToken.None);
        await recorder.WaitForAsync(1, TimeSpan.FromSeconds(5));

        Assert.False(recorder.Received[0].Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(recorder.Received[0].Error));
    }

    [Fact]
    public async Task NeverRespond_posts_nothing()
    {
        using var recorder = new CallbackRecorder();
        var client = NewClient(recorder, delay: TimeSpan.FromMilliseconds(20), mode: FakeProviderMode.NeverRespond);

        await client.SubmitAsync(NewSubmission(recorder), CancellationToken.None);
        await Task.Delay(400);

        Assert.Empty(recorder.Received);
    }

    [Fact]
    public async Task A_refused_delivery_is_dropped_and_never_retried()
    {
        // FR-019a: at-most-once. A lost answer is indistinguishable from a provider that never
        // answered, which is what makes claim expiry the single recovery mechanism.
        using var recorder = new CallbackRecorder();
        recorder.Refuse = true;
        var client = NewClient(recorder, delay: TimeSpan.FromMilliseconds(20));

        await client.SubmitAsync(NewSubmission(recorder), CancellationToken.None);
        await Task.Delay(600);

        Assert.Equal(1, recorder.AttemptCount);
    }

    private static FakeLlmClient NewClient(
        CallbackRecorder recorder,
        TimeSpan delay,
        FakeProviderMode mode = FakeProviderMode.Respond)
    {
        var options = Options.Create(new FakeProviderOptions
        {
            Mode = mode,
            Delay = delay,
            CallbackBaseUri = recorder.BaseUri.ToString(),
        });

        return new FakeLlmClient(
            new HttpClient(),
            options,
            NullLogger<FakeLlmClient>.Instance);
    }

    private static LlmSubmission NewSubmission(CallbackRecorder recorder) =>
        new(Guid.NewGuid(), "user", "some content", recorder.CallbackUri);
}

/// <summary>
/// A minimal HTTP listener standing in for a middleware instance's callback endpoint, so the test
/// observes what actually goes over the wire rather than an in-process invocation.
/// </summary>
internal sealed class CallbackRecorder : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<LlmCompletion> _received = [];
    private readonly Lock _gate = new();

    public CallbackRecorder()
    {
        var port = GetFreePort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _ = Task.Run(ListenAsync);
    }

    public Uri BaseUri { get; }

    public Uri CallbackUri => new(BaseUri, "callbacks/llm");

    /// <summary>When true, every request is answered 500 so delivery fails.</summary>
    public bool Refuse { get; set; }

    public int AttemptCount { get; private set; }

    public IReadOnlyList<LlmCompletion> Received
    {
        get { lock (_gate) { return _received.ToArray(); } }
    }

    public async Task WaitForAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Received.Count >= count) return;
            await Task.Delay(10);
        }

        throw new TimeoutException($"Expected {count} callbacks, saw {Received.Count}.");
    }

    private async Task ListenAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();

            lock (_gate)
            {
                AttemptCount++;
                if (!Refuse)
                {
                    // Parsed as the wire shape from contracts/openapi.yaml — messageId plus a
                    // status string — rather than as LlmCompletion, which is the in-process
                    // representation. Asserting on the wire shape is the point: it is what a real
                    // provider would have to send.
                    var wire = JsonSerializer.Deserialize<JsonElement>(body);
                    var status = wire.GetProperty("status").GetString();
                    _received.Add(new LlmCompletion(
                        wire.GetProperty("messageId").GetGuid(),
                        status == "completed",
                        wire.TryGetProperty("answer", out var a) && a.ValueKind != JsonValueKind.Null
                            ? a.GetString()
                            : null,
                        wire.TryGetProperty("error", out var e) && e.ValueKind != JsonValueKind.Null
                            ? e.GetString()
                            : null));
                }
            }

            context.Response.StatusCode = Refuse ? 500 : 204;
            context.Response.Close();
        }
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (_listener.IsListening) _listener.Stop();
        _listener.Close();
    }
}
