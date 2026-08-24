using Concurrency.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Messages;
using Xunit;

namespace Concurrency;

/// <summary>
/// SC-005: at least 100 induced races between two instances, exactly one winner each.
///
/// ClaimRaceTests already drives concurrent claims, but from one process against one connection
/// pool. This is the version that matters for Principle I: two independent hosts, two pools, two
/// sets of application state, arbitrating only through the store.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClaimRaceAcrossInstancesTests(PostgresFixture postgres)
{
    private const int Rounds = 100;

    [Fact]
    public async Task Two_instances_racing_for_one_message_produce_exactly_one_winner()
    {
        await using var one = new InstanceFactory(
            postgres.ConnectionString, "race-1", fakeProviderMode: "NeverRespond",
            sweepInterval: TimeSpan.FromMinutes(5));
        await using var two = new InstanceFactory(
            postgres.ConnectionString, "race-2", fakeProviderMode: "NeverRespond",
            sweepInterval: TimeSpan.FromMinutes(5));

        // Sweep interval is deliberately long: the sweepers must not claim these messages out from
        // under the race, or the test would be measuring the sweeper rather than the race.
        using var _ = one.CreateClient();
        using var __ = two.CreateClient();

        var storeOne = one.Services.GetRequiredService<MessageStore>();
        var storeTwo = two.Services.GetRequiredService<MessageStore>();

        var doubleWins = 0;
        var noWins = 0;
        var winnersByInstance = new Dictionary<string, int> { ["race-1"] = 0, ["race-2"] = 0 };

        for (var round = 0; round < Rounds; round++)
        {
            var userId = $"xrace-{Guid.NewGuid():N}";
            await storeOne.InsertAsync(userId, "contended across instances", CancellationToken.None);

            var fromOne = Task.Run(() => storeOne.TryClaimNextAsync(userId, CancellationToken.None));
            var fromTwo = Task.Run(() => storeTwo.TryClaimNextAsync(userId, CancellationToken.None));
            var results = await Task.WhenAll(fromOne, fromTwo);

            var winners = results.Count(r => r is not null);
            if (winners > 1) doubleWins++;
            if (winners == 0) noWins++;

            var claimed = await storeOne.GetByIdAsync(results.First(r => r is not null)?.Id
                ?? Guid.Empty, CancellationToken.None);
            if (claimed?.ClaimedBy is { } instance && winnersByInstance.ContainsKey(instance))
            {
                winnersByInstance[instance]++;
            }
        }

        Assert.Equal(0, doubleWins);
        Assert.Equal(0, noWins);

        // Both instances should have won at least once, otherwise the "race" was never contended
        // and the zero double-wins above would be meaningless.
        Assert.True(
            winnersByInstance["race-1"] > 0 && winnersByInstance["race-2"] > 0,
            $"Races were not genuinely contended: {winnersByInstance["race-1"]} wins for race-1, "
            + $"{winnersByInstance["race-2"]} for race-2.");
    }
}
