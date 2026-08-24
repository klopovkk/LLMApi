namespace Middleware.Configuration;

/// <summary>
/// Coordination settings. Bound from the <c>Processing</c> configuration section.
/// </summary>
public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    /// <summary>
    /// How long a message may stay in <see cref="Messages.MessageState.Processing"/> before the
    /// claim expires and the message is failed (FR-013).
    ///
    /// With at-most-once callback delivery (FR-019a) this is the only recovery path in the system,
    /// so it is a correctness setting rather than a tuning knob. Tests set it to milliseconds.
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the sweeper expires stale claims and picks up pending work.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Recorded as <c>claimed_by</c> for diagnostics only. Nothing routes by it — a callback is
    /// resolved by any instance regardless of which one claimed the message (FR-006).
    /// </summary>
    public string InstanceId { get; set; } = Environment.MachineName;

    /// <summary>How many users one sweep will consider for pickup.</summary>
    public int SweepBatchSize { get; set; } = 100;
}
