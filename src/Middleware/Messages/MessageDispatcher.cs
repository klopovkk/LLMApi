using LLM.Abstraction;
using Microsoft.Extensions.Options;
using Middleware.Configuration;

namespace Middleware.Messages;

/// <summary>
/// Claims a user's next message and submits it. The one place where the claim and the
/// language-model call meet, and therefore the one place where Principle IV is easy to violate.
/// </summary>
public sealed class MessageDispatcher(
    MessageStore store,
    ILlmClient llmClient,
    IOptions<ProcessingOptions> options,
    IOptions<FakeProviderCallbackOptions> callbackOptions,
    ILogger<MessageDispatcher> logger)
{
    private readonly ProcessingOptions _options = options.Value;

    /// <summary>
    /// Attempts to start the user's next message.
    ///
    /// The claim commits *before* submission, and submission happens outside any transaction. That
    /// ordering is the requirement, not an implementation detail: holding a transaction across an
    /// unbounded provider wait would convert provider slowness into database contention and would
    /// stop any other instance from making progress.
    ///
    /// A submission that throws leaves the message in Processing with no callback coming. That is
    /// deliberate and needs no compensating update — claim expiry resolves it, on exactly the same
    /// path a silently lost callback takes.
    /// </summary>
    public async Task<bool> TryStartNextAsync(string userId, CancellationToken cancellationToken)
    {
        var claimed = await store.TryClaimNextAsync(userId, cancellationToken);

        if (claimed is null)
        {
            return false;
        }

        var submission = new LlmSubmission(
            claimed.Id,
            claimed.UserId,
            claimed.Content,
            callbackOptions.Value.CallbackUri);

        try
        {
            await llmClient.SubmitAsync(submission, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            // No content in the log, and no state change: expiry owns the recovery.
            logger.LogError(
                ex,
                "Submitting message {MessageId} for user {UserId} failed on {InstanceId}; "
                + "leaving it claimed for expiry to resolve.",
                claimed.Id, claimed.UserId, _options.InstanceId);
            return true;
        }
    }
}

/// <summary>
/// Where this instance tells the provider to post completions. Separate from
/// <see cref="ProcessingOptions"/> because it belongs to the provider wiring rather than to
/// coordination, and separate from the provider's own options because the middleware — not the
/// provider — decides which address represents "a middleware instance".
/// </summary>
public sealed class FakeProviderCallbackOptions
{
    public Uri CallbackUri { get; set; } = new("http://localhost/callbacks/llm");
}
