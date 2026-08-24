using System.Net.Sockets;
using Npgsql;

namespace Middleware.Api;

/// <summary>
/// Turns "the shared store is unreachable" into a retryable 503 (FR-022).
///
/// Central rather than per-endpoint, because the rule is a property of the whole service: with the
/// store down there is no operation this middleware can honour, and every endpoint should say so
/// the same way. A per-endpoint try/catch would leave the next endpoint someone adds behaving
/// differently by omission.
///
/// Nothing is buffered here or anywhere else (FR-022a). The request is refused outright, which
/// keeps the acknowledgement honest: the client is told to retry rather than handed an identifier
/// for a message the service could not persist and would not remember.
/// </summary>
public sealed class StoreUnavailableMiddleware(RequestDelegate next, ILogger<StoreUnavailableMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (IsStoreUnreachable(ex))
        {
            if (context.Response.HasStarted)
            {
                // Too late to change the status; let it surface rather than corrupt the response.
                throw;
            }

            // No content in the log — only the path, which never carries a message body.
            logger.LogError(
                ex, "Refusing {Method} {Path}: the shared store is unreachable.",
                context.Request.Method, context.Request.Path);

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://datatracker.ietf.org/doc/html/rfc9457",
                title = "The shared store is unavailable.",
                status = StatusCodes.Status503ServiceUnavailable,
                // Says plainly that this is transient, so a client knows to retry rather than
                // treating it as a permanent rejection of the request.
                detail = "The request was not accepted and nothing was recorded. "
                       + "This is transient — please retry.",
            });
        }
    }

    /// <summary>
    /// Connection-level failures only. A constraint violation or a syntax error is a bug and must
    /// keep surfacing as a 500 rather than being disguised as a transient outage.
    /// </summary>
    private static bool IsStoreUnreachable(Exception ex) => ex switch
    {
        NpgsqlException { InnerException: SocketException } => true,
        NpgsqlException { InnerException: TimeoutException } => true,
        PostgresException => false,
        NpgsqlException => true,
        SocketException => true,
        TimeoutException => true,
        _ => false,
    };
}

public static class StoreUnavailableMiddlewareExtensions
{
    public static IApplicationBuilder UseStoreUnavailableHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<StoreUnavailableMiddleware>();
}
