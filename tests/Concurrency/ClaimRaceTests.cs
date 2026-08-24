using Concurrency.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Messages;
using Xunit;

namespace Concurrency;

/// <summary>
/// The claim, driven directly against the store with no HTTP in the way.
///
/// This is the load-bearing test in the suite. Everything else in the feature assumes that a
/// contended claim has exactly one winner; if that is not true, ordering and exclusivity are both
/// decoration.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClaimRaceTests(PostgresFixture postgres)
{
    private const int Rounds = 100;

    [Fact]
    public async Task A_contended_claim_has_exactly_one_winner_every_time()
    {
        await using var instance = new InstanceFactory(
            postgres.ConnectionString, "claim-race", fakeProviderMode: "NeverRespond");
        var store = instance.Services.GetRequiredService<MessageStore>();

        var doubleWins = 0;
        var noWins = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var userId = $"race-{Guid.NewGuid():N}";
            await store.InsertAsync(userId, "contended", CancellationToken.None);

            // Eight callers reach for the same single pending message at once. Exactly one may
            // take it; the rest must come away empty rather than erroring or also winning.
            var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                Task.Run(() => store.TryClaimNextAsync(userId, CancellationToken.None))));

            var winners = attempts.Count(a => a is not null);
            if (winners > 1) doubleWins++;
            if (winners == 0) noWins++;
        }

        Assert.Equal(0, doubleWins);
        Assert.Equal(0, noWins);
    }

    [Fact]
    public async Task The_claim_always_takes_the_lowest_pending_sequence()
    {
        await using var instance = new InstanceFactory(
            postgres.ConnectionString, "claim-order", fakeProviderMode: "NeverRespond");
        var store = instance.Services.GetRequiredService<MessageStore>();
        var userId = $"order-{Guid.NewGuid():N}";

        var inserted = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var (_, sequence, _) = await store.InsertAsync(userId, $"m{i}", CancellationToken.None);
            inserted.Add(sequence);
        }

        var claimedSequences = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var claimed = await store.TryClaimNextAsync(userId, CancellationToken.None);
            Assert.NotNull(claimed);
            claimedSequences.Add(claimed!.Sequence);

            // Release it so the next one may start — exclusivity means only one at a time.
            await store.TryCompleteAsync(
                claimed.Id, MessageState.Completed, "done", null, CancellationToken.None);
        }

        Assert.Equal(inserted, claimedSequences);
    }

    [Fact]
    public async Task No_claim_is_possible_while_the_user_already_has_one_active()
    {
        await using var instance = new InstanceFactory(
            postgres.ConnectionString, "claim-busy", fakeProviderMode: "NeverRespond");
        var store = instance.Services.GetRequiredService<MessageStore>();
        var userId = $"busy-{Guid.NewGuid():N}";

        await store.InsertAsync(userId, "first", CancellationToken.None);
        await store.InsertAsync(userId, "second", CancellationToken.None);

        var first = await store.TryClaimNextAsync(userId, CancellationToken.None);
        Assert.NotNull(first);

        var second = await store.TryClaimNextAsync(userId, CancellationToken.None);
        Assert.Null(second);
    }
}
