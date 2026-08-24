using Testcontainers.PostgreSql;
using Xunit;

namespace Concurrency.Fixtures;

/// <summary>
/// A real PostgreSQL server for the duration of a test collection.
///
/// Deliberately not an in-memory or SQLite substitute: the invariant under test is a property of
/// PostgreSQL's concurrency behaviour — a partial unique index rejecting a second concurrent
/// claim — so a substitute would make the tests pass while proving nothing (R-010).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("llmapi")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Published so helpers that need to read columns the status contract deliberately omits —
        // claimed_by, for instance — can reach the store directly.
        TestConnectionString.Value = ConnectionString;
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// Shares one container across every test class in the collection. Individual tests isolate
/// themselves by using distinct user identifiers rather than by resetting the database, which
/// keeps the suite fast and, more usefully, keeps concurrent activity in the store — closer to
/// the conditions the feature actually has to survive.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
