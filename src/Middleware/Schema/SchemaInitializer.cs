using System.Reflection;
using Npgsql;

namespace Middleware.Schema;

/// <summary>
/// Applies the schema at startup, serialized across instances by a PostgreSQL advisory lock.
///
/// The lock is not decoration. Two instances is the normal deployment here, and
/// <c>CREATE TABLE IF NOT EXISTS</c> is not race-free in PostgreSQL: concurrent creation can
/// raise a duplicate-key error on the system catalog. One lock around the whole script removes
/// that failure mode (R-007).
///
/// Keeping schema application inside the application, rather than in a migration tool or an init
/// container, means <c>docker compose up</c> and the test suite share exactly one code path — so
/// what the tests exercise is what runs.
/// </summary>
public sealed class SchemaInitializer(NpgsqlDataSource dataSource)
{
    /// <summary>Arbitrary but fixed: every instance must contend for the same lock key.</summary>
    private const long AdvisoryLockKey = 4919283746501L;

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var script = ReadScript();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Session-scoped lock, released explicitly below. A transaction-scoped lock would be
        // tidier, but the script contains CREATE INDEX statements that are better left outside
        // an explicit transaction.
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock($1)", connection))
        {
            acquire.Parameters.AddWithValue(AdvisoryLockKey);
            await acquire.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await using var apply = new NpgsqlCommand(script, connection);
            await apply.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
            release.Parameters.AddWithValue(AdvisoryLockKey);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static string ReadScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Middleware.Schema.schema.sql";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' was not found. Check the EmbeddedResource "
                + "item in Middleware.csproj.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
