using System.Net.Http.Json;
using System.Text.Json;
using Concurrency.Fixtures;
using Xunit;

namespace Concurrency;

/// <summary>
/// Required scenario 1: FIFO processing for a single user (FR-002, SC-002).
///
/// Asserted from data, not from timing. Each message reports its <c>sequence</c> (acceptance
/// order) and its <c>claimedAt</c> (start order); FIFO means those two orders agree. Watching
/// wall-clock timing instead would be racy and would prove less.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SingleUserFifoTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Processing_starts_in_acceptance_order_for_one_user()
    {
        await using var instance = new InstanceFactory(
            postgres.ConnectionString,
            "fifo",
            fakeProviderDelay: TimeSpan.FromMilliseconds(1),
            sweepInterval: TimeSpan.FromMilliseconds(50));
        using var client = instance.CreateClient();
        var userId = $"fifo-{Guid.NewGuid():N}";

        const int count = 25;
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var response = await client.PostAsJsonAsync("/messages",
                new { userId, content = $"message {i}" });
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            ids.Add(body.GetProperty("messageId").GetGuid());
        }

        var finished = await WaitForAllFinishedAsync(client, ids, TimeSpan.FromSeconds(30));

        var ordered = finished.OrderBy(m => m.Sequence).ToArray();
        var claimTimes = ordered.Select(m => m.ClaimedAt).ToArray();

        Assert.All(claimTimes, t => Assert.NotNull(t));

        // Every adjacent pair must be non-decreasing: message n started no later than n+1.
        for (var i = 1; i < claimTimes.Length; i++)
        {
            Assert.True(
                claimTimes[i - 1] <= claimTimes[i],
                $"Message at sequence {ordered[i - 1].Sequence} was claimed at {claimTimes[i - 1]}, "
                + $"after sequence {ordered[i].Sequence} at {claimTimes[i]} — FIFO violated.");
        }
    }

    [Fact]
    public async Task Rapidly_submitted_messages_still_start_in_acceptance_order()
    {
        // Submitted concurrently, so acceptance order is whatever the store assigned rather than
        // the order the client happened to write them in. FIFO is defined against that.
        await using var instance = new InstanceFactory(
            postgres.ConnectionString,
            "fifo-burst",
            fakeProviderDelay: TimeSpan.FromMilliseconds(1),
            sweepInterval: TimeSpan.FromMilliseconds(50));
        using var client = instance.CreateClient();
        var userId = $"burst-{Guid.NewGuid():N}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            client.PostAsJsonAsync("/messages", new { userId, content = $"burst {i}" })));

        var ids = new List<Guid>();
        foreach (var response in responses)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            ids.Add(body.GetProperty("messageId").GetGuid());
        }

        var finished = await WaitForAllFinishedAsync(client, ids, TimeSpan.FromSeconds(30));
        var ordered = finished.OrderBy(m => m.Sequence).ToArray();

        for (var i = 1; i < ordered.Length; i++)
        {
            Assert.True(
                ordered[i - 1].ClaimedAt <= ordered[i].ClaimedAt,
                "Claim order diverged from acceptance order under concurrent submission.");
        }
    }

    internal static async Task<IReadOnlyList<MessageSnapshot>> WaitForAllFinishedAsync(
        HttpClient client,
        IReadOnlyCollection<Guid> ids,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshots = await SnapshotAsync(client, ids);
            if (snapshots.All(s => s.State is "Completed" or "Failed"))
            {
                return snapshots;
            }

            await Task.Delay(25);
        }

        var last = await SnapshotAsync(client, ids);
        var unfinished = last.Where(s => s.State is not ("Completed" or "Failed")).ToArray();
        throw new TimeoutException(
            $"{unfinished.Length} of {ids.Count} messages never finished. "
            + string.Join(", ", unfinished.Select(u => $"{u.Sequence}:{u.State}")));
    }

    internal static async Task<IReadOnlyList<MessageSnapshot>> SnapshotAsync(
        HttpClient client,
        IReadOnlyCollection<Guid> ids)
    {
        var snapshots = new List<MessageSnapshot>(ids.Count);

        foreach (var id in ids)
        {
            var body = await client.GetFromJsonAsync<JsonElement>($"/messages/{id}");
            snapshots.Add(new MessageSnapshot(
                id,
                body.GetProperty("userId").GetString()!,
                body.GetProperty("sequence").GetInt64(),
                body.GetProperty("state").GetString()!,
                body.GetProperty("claimedAt").ValueKind == JsonValueKind.Null
                    ? null
                    : body.GetProperty("claimedAt").GetDateTimeOffset(),
                body.GetProperty("finishedAt").ValueKind == JsonValueKind.Null
                    ? null
                    : body.GetProperty("finishedAt").GetDateTimeOffset()));
        }

        return snapshots;
    }
}

internal sealed record MessageSnapshot(
    Guid Id,
    string UserId,
    long Sequence,
    string State,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? FinishedAt);
