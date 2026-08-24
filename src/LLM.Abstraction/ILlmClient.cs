namespace LLM.Abstraction;

/// <summary>
/// The Principle IV boundary.
///
/// Submission and completion are separate operations by design: <see cref="SubmitAsync"/> returns
/// as soon as the provider has accepted the work, never when an answer exists. The answer arrives
/// later as an inbound HTTP callback, which is what lets any instance complete the message and
/// what keeps provider latency from becoming service unavailability.
///
/// This assembly has no dependencies. Nothing about a provider, a transport, or a web framework
/// may leak into it — see contracts/llm-boundary.md for the obligations on both sides.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Hands the message to the provider. Must return promptly.
    ///
    /// The caller has already committed the message as claimed before calling this, and holds no
    /// transaction or lock across it. A throw means "no callback is coming" and needs no
    /// compensating update: claim expiry resolves the message, which is the same path a silently
    /// lost callback takes.
    /// </summary>
    Task SubmitAsync(LlmSubmission submission, CancellationToken cancellationToken);
}

/// <summary>What the middleware hands to the provider.</summary>
/// <param name="CallbackUri">
/// Where the completion is to be posted. Configuration rather than a constant, because in the
/// composed environment each instance points at its peer so every callback crosses a process
/// boundary.
/// </param>
public sealed record LlmSubmission(
    Guid MessageId,
    string UserId,
    string Content,
    Uri CallbackUri);

/// <summary>
/// What the provider posts back. Defined here rather than in the web layer so the callback shape
/// belongs to the boundary.
/// </summary>
public sealed record LlmCompletion(
    Guid MessageId,
    bool Succeeded,
    string? Answer,
    string? Error);
