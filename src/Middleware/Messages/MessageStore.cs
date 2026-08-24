using Microsoft.Extensions.Options;
using Middleware.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Middleware.Messages;

/// <summary>
/// Every statement that touches message state, written out by hand.
///
/// No ORM, deliberately (R-006): the correctness of this feature *is* the exact SQL of the claim —
/// the single statement, the subselect ordering, the partial unique index it collides with. An ORM
/// would put a translation layer between the design and the statement actually executed, so
/// reviewing the invariant would mean reviewing generated SQL. There are five statements here;
/// keeping them legible is worth more than the mapping code it costs.
/// </summary>
public sealed class MessageStore(
    NpgsqlDataSource dataSource,
    IOptions<ProcessingOptions> options,
    TimeProvider timeProvider,
    ILogger<MessageStore> logger)
{
    private readonly ProcessingOptions _options = options.Value;

    /// <summary>
    /// Statement 1. The moment this commits is the moment the message is accepted, so the
    /// database-assigned sequence *is* the acceptance order (FR-002, R-003) — no clock involved,
    /// which is what makes the order agree across instances.
    /// </summary>
    public async Task<(Guid Id, long Sequence, DateTimeOffset AcceptedAt)> InsertAsync(
        string userId,
        string content,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var acceptedAt = timeProvider.GetUtcNow();

        const string sql = """
            INSERT INTO messages (id, user_id, content, state, accepted_at)
            VALUES (@id, @userId, @content, 'Pending', @acceptedAt)
            RETURNING sequence;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("acceptedAt", NpgsqlDbType.TimestampTz, acceptedAt);

        var sequence = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        logger.LogInformation(
            "Message {MessageId} accepted for user {UserId} at sequence {Sequence} on {InstanceId}.",
            id, userId, sequence, _options.InstanceId);

        return (id, sequence, acceptedAt);
    }

    /// <summary>
    /// Statement 2 — the claim. This single statement is where the feature is either correct or
    /// not, so it is written out literally rather than composed.
    ///
    /// Three guards, each covering a race the others cannot:
    ///
    /// <c>AND state = 'Pending'</c> on the target row is what stops the *same* message being
    /// claimed twice. It is not redundant with the subselect. Under READ COMMITTED, when a blocked
    /// UPDATE re-checks its qual after the winner commits (EvalPlanQual), the row's own columns are
    /// re-read from the new version but subqueries in the qual are *not* re-evaluated — they keep
    /// the original snapshot. Without this predicate every blocked caller still saw a Pending
    /// message and no active one, and each re-updated the same row: measured at 99 double-claims
    /// per 100 contended rounds. The unique index cannot catch that, because updating one row
    /// repeatedly never produces a second Processing row.
    ///
    /// The <c>NOT EXISTS</c> enforces per-user exclusivity in the uncontended case, making a busy
    /// user a no-op rather than an error.
    ///
    /// <c>ux_messages_active_per_user</c> is the backstop for the remaining race: two instances on
    /// different snapshots targeting *different* pending messages for one user, both passing the
    /// NOT EXISTS. One commits; the other takes a unique violation, caught below as "lost the
    /// race". That index — not this code — is the last guarantee behind FR-003.
    ///
    /// The subselect's <c>ORDER BY sequence</c> is what makes the claim FIFO (FR-002).
    /// </summary>
    /// <returns>The claimed message, or <c>null</c> if there was nothing to claim or the race was lost.</returns>
    public async Task<ClaimedMessage?> TryClaimNextAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE messages
               SET state      = 'Processing',
                   claimed_at = @now,
                   claimed_by = @instanceId
             WHERE id = (
                     SELECT id
                       FROM messages
                      WHERE user_id = @userId
                        AND state   = 'Pending'
                      ORDER BY sequence
                      LIMIT 1
                   )
               AND state = 'Pending'
               AND NOT EXISTS (
                     SELECT 1 FROM messages
                      WHERE user_id = @userId
                        AND state   = 'Processing'
                   )
            RETURNING id, user_id, content, sequence;
            """;

        try
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("userId", userId);
            command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
            command.Parameters.AddWithValue("instanceId", _options.InstanceId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var claimed = new ClaimedMessage(
                Id: reader.GetGuid(0),
                UserId: reader.GetString(1),
                Content: reader.GetString(2),
                Sequence: reader.GetInt64(3));

            logger.LogInformation(
                "Message {MessageId} claimed for user {UserId} at sequence {Sequence} by {InstanceId}.",
                claimed.Id, claimed.UserId, claimed.Sequence, _options.InstanceId);

            return claimed;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Losing the race is an ordinary outcome, not a fault: another instance claimed this
            // user's next message between our NOT EXISTS check and our write. The index did its
            // job. Debug rather than Warning — under contention this is expected traffic.
            logger.LogDebug(
                "Lost a claim race for user {UserId} on {InstanceId}.", userId, _options.InstanceId);
            return null;
        }
    }

    /// <summary>
    /// Statement 3 — completion from a callback.
    ///
    /// <c>claimed_at</c> is deliberately left in place rather than cleared. It records when the
    /// message started processing, which is the evidence FIFO is asserted from (SC-002) and which
    /// an operator needs in order to see how long a finished message actually took. Nothing reads
    /// it without also filtering on <c>state = 'Processing'</c>, so leaving it set is safe.
    ///
    /// <c>AND state = 'Processing'</c> is the whole of the idempotency story. A duplicate callback
    /// (FR-014), a post-expiry callback (FR-013b), and a callback for an already-failed message all
    /// match zero rows and change nothing. Because the successor claim runs only when this returns
    /// a user, the queue also advances exactly once (SC-010).
    /// </summary>
    /// <returns>The owning user id when this call completed the message; <c>null</c> when it was a no-op.</returns>
    public async Task<string?> TryCompleteAsync(
        Guid id,
        MessageState finalState,
        string? answer,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        if (finalState is not (MessageState.Completed or MessageState.Failed))
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalState), finalState, "Only Completed or Failed are final states.");
        }

        const string sql = """
            UPDATE messages
               SET state          = @finalState,
                   answer         = @answer,
                   failure_reason = @failureReason,
                   finished_at    = @now
             WHERE id    = @id
               AND state = 'Processing'
            RETURNING user_id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("finalState", finalState.ToString());
        command.Parameters.AddWithValue("answer", (object?)answer ?? DBNull.Value);
        command.Parameters.AddWithValue("failureReason", (object?)failureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());

        var userId = (string?)await command.ExecuteScalarAsync(cancellationToken);

        if (userId is null)
        {
            logger.LogDebug(
                "Completion for message {MessageId} was a no-op: it is not in Processing.", id);
        }
        else
        {
            logger.LogInformation(
                "Message {MessageId} for user {UserId} reached {FinalState} on {InstanceId}.",
                id, userId, finalState, _options.InstanceId);
        }

        return userId;
    }

    /// <summary>
    /// Statement 4 — expire stale claims.
    ///
    /// The cutoff is computed here, in application code, from the injected
    /// <see cref="TimeProvider"/> rather than from SQL <c>now()</c>. That is deliberate: it is what
    /// lets a test advance a fake clock instead of sleeping through a real timeout (R-005). Expiry
    /// is the only recovery path in the system, so it must be a scenario that actually gets run.
    ///
    /// The <c>state = 'Processing'</c> guard means an expiry cannot race a completing callback:
    /// whichever commits first wins and the other matches nothing.
    /// </summary>
    /// <returns>The users whose queues were released, ready to be started again.</returns>
    public async Task<IReadOnlyList<string>> ExpireStaleClaimsAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - _options.ClaimTimeout;

        const string sql = """
            UPDATE messages
               SET state          = 'Failed',
                   failure_reason = 'No completion callback was received before the claim expired.',
                   finished_at    = @now
             WHERE state      = 'Processing'
               AND claimed_at < @cutoff
            RETURNING id, user_id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
        command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);

        var expired = new List<(Guid Id, string UserId)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                expired.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        foreach (var (id, userId) in expired)
        {
            // Warning, not Information: nobody answered, and somebody may want to know why.
            logger.LogWarning(
                "Claim on message {MessageId} for user {UserId} expired on {InstanceId} after "
                + "{ClaimTimeout}; the message is failed and the queue released.",
                id, userId, _options.InstanceId, _options.ClaimTimeout);
        }

        return expired.Select(e => e.UserId).Distinct().ToArray();
    }

    /// <summary>
    /// Sweeper support: users who have work waiting and nothing active.
    ///
    /// Both instances running this concurrently is safe and expected — the claim decides the
    /// winner, so the sweeper needs no leader election and no coordination of its own. That is the
    /// property that lets an instance disappear without anything having to notice.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindUsersWithPendingWorkAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT p.user_id
              FROM messages p
             WHERE p.state = 'Pending'
               AND NOT EXISTS (
                     SELECT 1 FROM messages a
                      WHERE a.user_id = p.user_id
                        AND a.state   = 'Processing'
                   )
             LIMIT @batchSize;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var users = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(reader.GetString(0));
        }

        return users;
    }

    /// <summary>
    /// Statement 5. Content is not selected: the status response does not echo it (FR-021).
    /// </summary>
    public async Task<Message?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, user_id, sequence, state, answer, failure_reason,
                   accepted_at, claimed_at, finished_at, claimed_by
              FROM messages
             WHERE id = @id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Message(
            Id: reader.GetGuid(0),
            UserId: reader.GetString(1),
            Sequence: reader.GetInt64(2),
            State: Enum.Parse<MessageState>(reader.GetString(3)),
            Answer: reader.IsDBNull(4) ? null : reader.GetString(4),
            FailureReason: reader.IsDBNull(5) ? null : reader.GetString(5),
            AcceptedAt: reader.GetFieldValue<DateTimeOffset>(6),
            ClaimedAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            FinishedAt: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            ClaimedBy: reader.IsDBNull(9) ? null : reader.GetString(9));
    }
}
