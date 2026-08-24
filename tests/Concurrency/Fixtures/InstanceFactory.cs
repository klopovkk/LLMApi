using LLM.Abstraction;
using LLM.FakeProvider;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
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
            ["FakeProvider:Delay"] = (fakeProviderDelay ?? TimeSpan.FromMilliseconds(20)).ToString(),
            ["FakeProvider:CallbackBaseUri"] = "http://localhost",
        };
    }

    public string InstanceId { get; }

    /// <summary>
    /// Which instance this one's provider posts its completions to. Defaults to itself.
    ///
    /// Set it to a different instance and every callback this instance generates is handled by
    /// that other instance, which is how the cross-instance path is exercised on every message
    /// rather than left to chance — the same arrangement the composed environment uses (R-011).
    ///
    /// Resolved lazily at send time because the target's server does not exist until its host is
    /// built, and two instances pointing at each other would otherwise be unconstructable.
    /// </summary>
    public InstanceFactory? CallbackTarget { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(_settings));

        builder.ConfigureServices(services =>
        {
            if (_timeProvider is not null)
            {
                // Expiry reads its cutoff from TimeProvider, so a test can advance time rather
                // than sleep through a real claim timeout (R-005).
                services.AddSingleton<TimeProvider>(_timeProvider);
            }

            // The provider makes a genuine HTTP request; in-process hosting has no listening
            // socket, so the request is routed to the target instance's test server instead. The
            // endpoint, the payload, and the cross-instance routing are all still real — only the
            // transport is short-circuited.
            services.AddSingleton<IHttpMessageHandlerBuilderFilter>(
                new CallbackRoutingFilter(() => (CallbackTarget ?? this).Server.CreateHandler()));
        });
    }

    /// <summary>
    /// Replaces the primary handler of the provider's typed HttpClient with one pointing at the
    /// target instance.
    /// </summary>
    private sealed class CallbackRoutingFilter(Func<HttpMessageHandler> handlerFactory)
        : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) =>
            builder =>
            {
                next(builder);

                if (builder.Name == typeof(ILlmClient).Name
                    || builder.Name == nameof(FakeLlmClient)
                    || builder.Name == typeof(FakeLlmClient).FullName)
                {
                    builder.PrimaryHandler = new LazyHandler(handlerFactory);
                }
            };
    }

    /// <summary>Defers resolving the target handler until the first request is actually sent.</summary>
    private sealed class LazyHandler(Func<HttpMessageHandler> factory) : DelegatingHandler
    {
        private HttpMessageHandler? _inner;
        private readonly Lock _gate = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (InnerHandler is null)
                {
                    _inner = factory();
                    InnerHandler = _inner;
                }
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
