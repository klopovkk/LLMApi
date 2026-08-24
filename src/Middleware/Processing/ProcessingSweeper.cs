using Microsoft.Extensions.Options;
using Middleware.Configuration;
using Middleware.Messages;
using Npgsql;

namespace Middleware.Processing;

/// <summary>
/// The safety net. Runs on every instance, and needs no leader election because the atomic claim
/// decides every contest it could possibly enter.
///
/// The accept path and the callback path already start work on the hot path, which makes this look
/// redundant until you ask what happens when an instance dies between persisting a submission and
/// claiming it, or when the store was unreachable at the moment work arrived. Then this is the
/// only thing that will ever start that message. It is what turns an instance failure into a
/// little latency instead of a stuck user.
/// </summary>
public sealed class ProcessingSweeper(
    MessageStore store,
    MessageDispatcher dispatcher,
    IOptions<ProcessingOptions> options,
    ILogger<ProcessingSweeper> logger) : BackgroundService
{
    private readonly ProcessingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Sweeper started on {InstanceId} with an interval of {SweepInterval}.",
            _options.InstanceId, _options.SweepInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                // The store is unreachable. Log and carry on: FR-022b requires the instance to
                // resume by itself once the store returns, which means the sweeper must survive
                // the outage rather than take the host down with it.
                logger.LogError(
                    ex, "Sweep failed on {InstanceId}: the shared store is unreachable.",
                    _options.InstanceId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure during sweep on {InstanceId}.", _options.InstanceId);
            }

            try
            {
                await Task.Delay(_options.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var users = await store.FindUsersWithPendingWorkAsync(_options.SweepBatchSize, cancellationToken);

        foreach (var user in users)
        {
            if (cancellationToken.IsCancellationRequested) return;

            // One attempt per user per sweep. Losing to another instance is an ordinary outcome
            // and needs no handling here — TryStartNextAsync already reports it as "not started".
            await dispatcher.TryStartNextAsync(user, cancellationToken);
        }
    }
}
