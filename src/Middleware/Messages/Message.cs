namespace Middleware.Messages;

/// <summary>
/// A message row as read back for status. Content is deliberately absent: the status response does
/// not echo it, which keeps the read path free of any reason to touch a field that must never
/// reach a log (FR-021).
/// </summary>
public sealed record Message(
    Guid Id,
    long Sequence,
    string UserId,
    MessageState State,
    string? Answer,
    string? FailureReason,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? FinishedAt,
    string? ClaimedBy);

/// <summary>
/// A message that has just been claimed, carrying what the dispatcher needs to submit it without a
/// second read.
/// </summary>
public sealed record ClaimedMessage(
    Guid Id,
    string UserId,
    string Content,
    long Sequence);
