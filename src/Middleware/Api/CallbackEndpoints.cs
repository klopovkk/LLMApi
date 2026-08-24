using Middleware.Messages;

namespace Middleware.Api;

/// <summary>
/// The asynchronous completion path (FR-005, FR-006).
///
/// Any instance may receive this and completes the message regardless of which instance submitted
/// it. There is no routing key, no affinity, and no shared session — the message identifier plus
/// the shared store is the whole of what is needed, which is precisely why coordination cannot
/// live in process memory.
/// </summary>
public static class CallbackEndpoints
{
    public static void MapCallbackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/callbacks/llm", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        CompletionCallbackRequest? request,
        MessageStore store,
        MessageDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        // No caller verification of any kind (FR-020a). Every caller is trusted, because the
        // target environment is a local composed deployment.

        if (request is null || request.MessageId == Guid.Empty)
        {
            return Results.Problem(
                title: "Invalid callback.",
                detail: "messageId is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var finalState = request.Status?.ToLowerInvariant() switch
        {
            "completed" => MessageState.Completed,
            "failed" => MessageState.Failed,
            _ => (MessageState?)null,
        };

        if (finalState is null)
        {
            return Results.Problem(
                title: "Invalid callback.",
                detail: "status must be either 'completed' or 'failed'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var userId = await store.TryCompleteAsync(
            request.MessageId,
            finalState.Value,
            finalState == MessageState.Completed ? request.Answer : null,
            finalState == MessageState.Failed
                ? request.Error ?? "The provider reported a failure."
                : null,
            cancellationToken);

        if (userId is null)
        {
            // Zero rows changed. Either the message is unknown, or it is already in a final state.
            // Those need different answers: unknown is a client error (FR-015), while an
            // already-final message is the duplicate case and must look like success (FR-014), as
            // must a callback arriving after the claim expired (FR-013b).
            var existing = await store.GetByIdAsync(request.MessageId, cancellationToken);

            if (existing is null)
            {
                return Results.Problem(
                    title: "Unknown message.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.NoContent();
        }

        // A separate statement, deliberately not batched with the completion above: a failure to
        // start the successor must not roll back a completion that has already been reported.
        await dispatcher.TryStartNextAsync(userId, cancellationToken);

        return Results.NoContent();
    }
}

public sealed record CompletionCallbackRequest(
    Guid MessageId,
    string? Status,
    string? Answer,
    string? Error);
