using Microsoft.Extensions.Options;
using Middleware.Configuration;
using Npgsql;

namespace Middleware.Api;

/// <summary>
/// Reports whether this instance can reach the shared store.
///
/// The only endpoint not implied by a functional requirement. It is here because FR-022 describes
/// behaviour *during* a store outage, and asserting on that needs an observable signal that the
/// instance is alive while the store is not (R-009).
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (
            NpgsqlDataSource dataSource,
            IOptions<ProcessingOptions> options,
            CancellationToken cancellationToken) =>
        {
            var reachable = await IsStoreReachableAsync(dataSource, cancellationToken);
            var body = new { instanceId = options.Value.InstanceId, storeReachable = reachable };

            return reachable
                ? Results.Ok(body)
                : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }

    private static async Task<bool> IsStoreReachableAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
