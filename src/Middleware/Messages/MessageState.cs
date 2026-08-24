namespace Middleware.Messages;

/// <summary>
/// The explicit processing state of a message (FR-011, Principle II).
///
/// Stored as text so the state is legible in the database itself — an operator reading a row can
/// see what it means without a lookup table, which is what Principle II's inspectability
/// requirement is actually asking for.
/// </summary>
public enum MessageState
{
    /// <summary>Accepted and waiting its turn. Unbounded time here is normal.</summary>
    Pending,

    /// <summary>Claimed and submitted, or about to be. At most one per user (FR-003).</summary>
    Processing,

    /// <summary>An answer was recorded. Terminal.</summary>
    Completed,

    /// <summary>A failure was recorded, by the provider or by claim expiry. Terminal.</summary>
    Failed,
}
