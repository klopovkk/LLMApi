using LLM.Abstraction;
using LLM.FakeProvider;
using Middleware.Api;
using Middleware.Configuration;
using Middleware.Messages;
using Middleware.Processing;
using Middleware.Schema;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ProcessingOptions>(
    builder.Configuration.GetSection(ProcessingOptions.SectionName));

builder.Services.Configure<FakeProviderOptions>(
    builder.Configuration.GetSection(FakeProviderOptions.SectionName));

// Where this instance asks the provider to post completions. In the composed environment each
// instance is given its peer's address, so every callback crosses a process boundary (R-011).
builder.Services.Configure<FakeProviderCallbackOptions>(callbacks =>
{
    var baseUri = builder.Configuration[$"{FakeProviderOptions.SectionName}:CallbackBaseUri"]
        ?? "http://localhost";
    callbacks.CallbackUri = new Uri(new Uri(baseUri), "/callbacks/llm");
});

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
builder.Services.AddSingleton<MessageDispatcher>();
builder.Services.AddHostedService<ProcessingSweeper>();

// The composition root is the only place that knows a concrete provider exists; everything else
// sees ILlmClient (Principle IV).
builder.Services.AddHttpClient<ILlmClient, FakeLlmClient>();

var app = builder.Build();

// Applied best-effort. An unreachable store must not stop the instance from starting: FR-022
// requires it to stay up and answer 503 rather than fail to boot, and /health is how an operator
// tells those two situations apart.
await ApplySchemaAsync(app);

app.UseStoreUnavailableHandling();

app.MapHealthEndpoints();
app.MapMessageEndpoints();
app.MapCallbackEndpoints();

app.Run();

static async Task ApplySchemaAsync(WebApplication app)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    try
    {
        await app.Services.GetRequiredService<SchemaInitializer>().EnsureAppliedAsync(CancellationToken.None);
        logger.LogInformation("Schema applied.");
    }
    catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
    {
        logger.LogError(ex, "Could not apply schema: the shared store is unreachable.");
    }
}

// Exposed so the test harness can reference this host with WebApplicationFactory.
public partial class Program;
