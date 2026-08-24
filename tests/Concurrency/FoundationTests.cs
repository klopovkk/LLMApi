using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Middleware.Schema;
using Npgsql;
using Xunit;

namespace Concurrency;

/// <summary>
/// Foundation: the schema applies safely when two instances start at once, and an instance can
/// report whether it can reach the shared store.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FoundationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Schema_applied_concurrently_by_two_callers_yields_one_table_and_no_error()
    {
        // Two instances starting together is the normal case here, and CREATE TABLE IF NOT EXISTS
        // is not race-free in PostgreSQL — concurrent creation can raise a duplicate-key error on
        // the system catalog. The advisory lock in SchemaInitializer is what removes that (R-007).
        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);

        var initializers = Enumerable.Range(0, 8)
            .Select(_ => new SchemaInitializer(dataSource))
            .ToArray();

        var applications = initializers.Select(i => Task.Run(() => i.ApplyAsync(CancellationToken.None)));

        // Must not throw: a duplicate-key error on pg_class here means the lock is missing.
        await Task.WhenAll(applications);

        Assert.Equal(1, await ScalarAsync(dataSource,
            "SELECT count(*) FROM information_schema.tables WHERE table_name = 'messages'"));

        Assert.Equal(1, await ScalarAsync(dataSource,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'ux_messages_active_per_user'"));
        Assert.Equal(1, await ScalarAsync(dataSource,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_messages_pending'"));
        Assert.Equal(1, await ScalarAsync(dataSource,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_messages_processing_claimed_at'"));
    }

    [Fact]
    public async Task Health_reports_store_reachable_when_it_is()
    {
        await using var instance = new InstanceFactory(postgres.ConnectionString, "health-up");
        using var client = instance.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("storeReachable").GetBoolean());
        Assert.Equal("health-up", body.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task Health_reports_store_unreachable_when_it_is_not()
    {
        // The instance is up; the store is not. That distinction is the reason this endpoint
        // exists — FR-022 describes behaviour during an outage and it needs an observable signal.
        await using var instance = new InstanceFactory(
            "Host=127.0.0.1;Port=1;Database=nowhere;Username=none;Password=none;Timeout=1",
            "health-down");
        using var client = instance.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("storeReachable").GetBoolean());
    }

    private static async Task<int> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
