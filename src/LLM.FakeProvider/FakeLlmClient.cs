using System.Net.Http.Json;
using LLM.Abstraction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLM.FakeProvider;

/// <summary>
/// Stands in for a real provider: accepts a submission, waits, and posts the completion back over
/// HTTP exactly once.
///
/// In-process rather than a separate service, as the specification requires — but the callback
/// itself is genuinely an HTTP request to a configured address, because FR-006 is only exercised
/// if the completion crosses a process boundary.
/// </summary>
public sealed class FakeLlmClient(
    HttpClient httpClient,
    IOptions<FakeProviderOptions> options,
    ILogger<FakeLlmClient> logger) : ILlmClient
{
    private readonly FakeProviderOptions _options = options.Value;

    public Task SubmitAsync(LlmSubmission submission, CancellationToken cancellationToken)
    {
        if (_options.Mode == FakeProviderMode.NeverRespond)
        {
            logger.LogInformation(
                "Accepted message {MessageId} and will never answer (NeverRespond).",
                submission.MessageId);
            return Task.CompletedTask;
        }

        // Fire and forget, deliberately. Returning here is what keeps provider latency out of the
        // caller's request; the caller is holding no transaction or lock while this runs.
        _ = Task.Run(() => DeliverAsync(submission), CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task DeliverAsync(LlmSubmission submission)
    {
        try
        {
            await Task.Delay(_options.Delay);

            var completion = _options.Mode == FakeProviderMode.Fail
                ? new LlmCompletion(submission.MessageId, false, null, "The provider reported a failure.")
                // Derived from the identifier, never from the content: an echoed answer would be
                // another way for content to reach a log (FR-021).
                : new LlmCompletion(
                    submission.MessageId,
                    true,
                    $"Answer for {submission.MessageId:N}.",
                    null);

            // LlmCompletion is the in-process representation; the wire shape is the one defined in
            // contracts/openapi.yaml, which spells the outcome as a status string rather than a
            // boolean. Mapping happens here, at the edge, so the abstraction does not have to
            // carry a transport detail and the endpoint does not have to accept two shapes.
            var payload = new CompletionPayload(
                completion.MessageId,
                completion.Succeeded ? "completed" : "failed",
                completion.Answer,
                completion.Error);

            // One attempt. No retry, ever (FR-019a) — a lost answer is resolved by claim expiry,
            // which keeps exactly one recovery mechanism in the system rather than two.
            using var response = await httpClient.PostAsJsonAsync(submission.CallbackUri, payload);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Callback for message {MessageId} was refused with {StatusCode}; dropping it.",
                    submission.MessageId, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Never rethrown: this runs detached, and a lost callback is an expected condition
            // that claim expiry already covers. No content is logged.
            logger.LogWarning(
                ex, "Callback for message {MessageId} could not be delivered; dropping it.",
                submission.MessageId);
        }
    }
}

/// <summary>
/// The callback body exactly as contracts/openapi.yaml defines it. Private to the provider: it is
/// a transport detail, not part of the boundary.
/// </summary>
internal sealed record CompletionPayload(Guid MessageId, string Status, string? Answer, string? Error);
