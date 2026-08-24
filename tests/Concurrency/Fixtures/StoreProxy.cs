using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Concurrency.Fixtures;

/// <summary>
/// A TCP passthrough in front of PostgreSQL that can be switched off and back on.
///
/// Used to simulate a store outage honestly. The alternative — handing the instance a different
/// connection string when the store "returns" — would test a recovery mechanism the real system
/// does not have: in production the connection string never changes, PostgreSQL simply becomes
/// reachable again. Closing this proxy produces exactly that, a refused connection on a stable
/// address, so the recovery path under test is the real one.
///
/// It also leaves the shared container untouched, so the rest of the collection keeps running.
/// </summary>
public sealed class StoreProxy : IAsyncDisposable
{
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly List<TcpClient> _clients = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptLoop;

    public StoreProxy(string upstreamConnectionString)
    {
        var upstream = new NpgsqlConnectionStringBuilder(upstreamConnectionString);
        _targetHost = upstream.Host!;
        _targetPort = upstream.Port;

        Port = FreePort();

        ConnectionString = new NpgsqlConnectionStringBuilder(upstreamConnectionString)
        {
            Host = "127.0.0.1",
            Port = Port,
            Timeout = 2,
            CommandTimeout = 2,
            // Pooled connections would otherwise survive the outage as stale handles and confuse
            // what the test is measuring. The pruning interval has to come down with the idle
            // lifetime; Npgsql rejects an idle lifetime below it.
            ConnectionIdleLifetime = 1,
            ConnectionPruningInterval = 1,
        }.ConnectionString;
    }

    public int Port { get; }

    /// <summary>Address the middleware is configured with. Constant across an outage.</summary>
    public string ConnectionString { get; }

    public void Open()
    {
        if (_listener is not null) return;

        _acceptLoop = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _ = Task.Run(() => AcceptAsync(_listener, _acceptLoop.Token));
    }

    public void Close()
    {
        _acceptLoop?.Cancel();
        _listener?.Stop();
        _listener = null;

        lock (_clients)
        {
            foreach (var client in _clients)
            {
                try { client.Close(); } catch { /* already gone */ }
            }

            _clients.Clear();
        }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient inbound;
            try
            {
                inbound = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception)
            {
                return;
            }

            lock (_clients) _clients.Add(inbound);
            _ = Task.Run(() => PumpAsync(inbound, cancellationToken), cancellationToken);
        }
    }

    private async Task PumpAsync(TcpClient inbound, CancellationToken cancellationToken)
    {
        try
        {
            using var outbound = new TcpClient();
            await outbound.ConnectAsync(_targetHost, _targetPort, cancellationToken);

            var upstream = inbound.GetStream();
            var downstream = outbound.GetStream();

            await Task.WhenAny(
                upstream.CopyToAsync(downstream, cancellationToken),
                downstream.CopyToAsync(upstream, cancellationToken));
        }
        catch
        {
            // The connection died with the proxy, which is the point.
        }
        finally
        {
            lock (_clients) _clients.Remove(inbound);
            try { inbound.Close(); } catch { /* already gone */ }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
