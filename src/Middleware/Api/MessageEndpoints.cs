using Middleware.Messages;

namespace Middleware.Api;

/// <summary>
/// Submission and status. See contracts/openapi.yaml for the authoritative shapes.
/// </summary>
public static class MessageEndpoints
{
    public static void MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/messages", SubmitAsync);
        app.MapGet("/messages/{messageId:guid}", GetAsync);
    }

    /// <summary>
    /// 202 rather than 201: acceptance is the whole of what has happened. Processing may not have
    /// started, and usually has not — the client is explicitly not waiting (FR-001).
    /// </summary>
    private static async Task<IResult> SubmitAsync(
        SubmitMessageRequest? request,
        MessageStore store,
        CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.Problem(
                title: "Invalid submission.",
                detail: "Both userId and content are required and must not be blank.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var (id, sequence, acceptedAt) =
            await store.InsertAsync(request.UserId, request.Content, cancellationToken);

        return Results.Accepted(
            $"/messages/{id}",
            new MessageAcceptedResponse(id, request.UserId, sequence, nameof(MessageState.Pending), acceptedAt));
    }

    private static async Task<IResult> GetAsync(
        Guid messageId,
        MessageStore store,
        CancellationToken cancellationToken)
    {
        var message = await store.GetByIdAsync(messageId, cancellationToken);

        if (message is null)
        {
            return Results.Problem(
                title: "Unknown message.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new MessageStatusResponse(
            message.Id,
            message.UserId,
            message.Sequence,
            message.State.ToString(),
            message.Answer,
            message.FailureReason,
            message.AcceptedAt,
            message.ClaimedAt,
            message.FinishedAt));
    }
}

public sealed record SubmitMessageRequest(string? UserId, string? Content);

public sealed record MessageAcceptedResponse(
    Guid MessageId,
    string UserId,
    long Sequence,
    string State,
    DateTimeOffset AcceptedAt);

/// <summary>
/// Carries <c>Sequence</c> and <c>ClaimedAt</c> so a test can assert FIFO from data — comparing
/// claim order against acceptance order — rather than by observing wall-clock timing, which would
/// be racy (SC-002).
/// </summary>
public sealed record MessageStatusResponse(
    Guid MessageId,
    string UserId,
    long Sequence,
    string State,
    string? Answer,
    string? FailureReason,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? FinishedAt);
