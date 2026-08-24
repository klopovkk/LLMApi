using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Concurrency.Fixtures;

/// <summary>
/// One middleware instance, configured independently of every other.
///
/// Two of these against a single <see cref="PostgresFixture"/> reproduce the deployment topology
/// the feature is defined by: separate application state, one shared coordination store. That is
/// the whole property under test, so the tests need nothing heavier than this to be honest about
/// two instances (R-010).
/// </summary>
public sealed class InstanceFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly FakeTimeProvider? _timeProvider;

    public InstanceFactory(
        string connectionString,
        string instanceId,
        TimeSpan? claimTimeout = null,
        TimeSpan? sweepInterval = null,
        string fakeProviderMode = "Respond",
        TimeSpan? fakeProviderDelay = null,
        string? callbackBaseUri = null,
        FakeTimeProvider? timeProvider = null)
    {
        InstanceId = instanceId;
        _timeProvider = timeProvider;
        _settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["Processing:InstanceId"] = instanceId,
            ["Processing:ClaimTimeout"] = (claimTimeout ?? TimeSpan.FromSeconds(30)).ToString(),
            ["Processing:SweepInterval"] = (sweepInterval ?? TimeSpan.FromMilliseconds(100)).ToString(),
            ["FakeProvider:Mode"] = fakeProviderMode,
            ["FakeProvider:Delay"] = (fakeProviderDelay ?? TimeSpan.FromMilliseconds(50)).ToString(),
            ["FakeProvider:CallbackBaseUri"] = callbackBaseUri ?? "http://localhost",
        };
    }

    public string InstanceId { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(_settings));

        if (_timeProvider is not null)
        {
            // Expiry reads the cutoff from TimeProvider, so a test can advance time rather than
            // sleep through a real claim timeout (R-005).
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(_timeProvider);
            });
        }
    }
}
