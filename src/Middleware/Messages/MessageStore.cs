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
