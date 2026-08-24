using Middleware.Api;
using Middleware.Configuration;
using Middleware.Messages;
using Middleware.Schema;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ProcessingOptions>(
    builder.Configuration.GetSection(ProcessingOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Postgres is not configured. Supply it through the environment; "
            + "it must never be committed to tracked configuration.");

    return NpgsqlDataSource.Create(connectionString);
});

builder.Services.AddSingleton<SchemaInitializer>();
builder.Services.AddSingleton<MessageStore>();

var app = builder.Build();

// Applied best-effort. An unreachable store must not stop the instance from starting: FR-022
// requires it to stay up and answer 503 rather than fail to boot, and /health is how an operator
// tells those two situations apart.
await ApplySchemaAsync(app);

app.MapHealthEndpoints();
app.MapMessageEndpoints();

app.Run();

static async Task ApplySchemaAsync(WebApplication app)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    try
    {
        await app.Services.GetRequiredService<SchemaInitializer>().ApplyAsync(CancellationToken.None);
        logger.LogInformation("Schema applied.");
    }
    catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
    {
        logger.LogError(ex, "Could not apply schema: the shared store is unreachable.");
    }
}

// Exposed so the test harness can reference this host with WebApplicationFactory.
public partial class Program;
