using Feedarr.Api.Models;
using Feedarr.Api.Services.Posters;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class PosterFetchQueueTests
{
    [Fact]
    public async Task EnqueueAsync_WhenQueueIsFull_ReturnsTimedOutAndTracksMetric()
    {
        var queue = CreateQueue();

        for (var i = 1; i <= 2000; i++)
        {
            var result = await queue.EnqueueAsync(CreateJob(i), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
            Assert.Equal(PosterFetchEnqueueStatus.Enqueued, result.Status);
        }

        var timedOut = await queue.EnqueueAsync(CreateJob(5001), CancellationToken.None, TimeSpan.FromMilliseconds(25));

        Assert.Equal(PosterFetchEnqueueStatus.TimedOut, timedOut.Status);
        Assert.Equal(1, queue.GetSnapshot().JobsTimedOut);
    }

    [Fact]
    public async Task EnqueueAsync_CoalescesPendingJob()
    {
        var queue = CreateQueue();

        var first = await queue.EnqueueAsync(CreateJob(10, forceRefresh: false), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
        var second = await queue.EnqueueAsync(CreateJob(10, forceRefresh: false), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);

        Assert.Equal(PosterFetchEnqueueStatus.Enqueued, first.Status);
        Assert.Equal(PosterFetchEnqueueStatus.Coalesced, second.Status);

        var snapshot = queue.GetSnapshot();
        Assert.Equal(1, snapshot.PendingCount);
        Assert.Equal(1, snapshot.JobsCoalesced);
    }

    [Fact]
    public async Task EnqueueAsync_UpgradesPendingJobToForceRefresh()
    {
        var queue = CreateQueue();

        await queue.EnqueueAsync(CreateJob(20, forceRefresh: false), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
        var upgraded = await queue.EnqueueAsync(CreateJob(20, forceRefresh: true), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        Assert.Equal(PosterFetchEnqueueStatus.Coalesced, upgraded.Status);
        Assert.True(dequeued.ForceRefresh);
        Assert.Equal(1, queue.GetSnapshot().InFlightCount);
    }

    [Fact]
    public async Task EnqueueAsync_CoalescesInFlightAndSchedulesFollowUpRefresh()
    {
        var queue = CreateQueue();

        await queue.EnqueueAsync(CreateJob(30, forceRefresh: false), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
        var inFlight = await queue.DequeueAsync(CancellationToken.None);

        var duplicate = await queue.EnqueueAsync(CreateJob(30, forceRefresh: false), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
        var refresh = await queue.EnqueueAsync(CreateJob(30, forceRefresh: true), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
        var followUp = queue.Complete(inFlight, new PosterFetchProcessResult(true));

        Assert.Equal(PosterFetchEnqueueStatus.Coalesced, duplicate.Status);
        Assert.Equal(PosterFetchEnqueueStatus.Coalesced, refresh.Status);
        Assert.NotNull(followUp);
        Assert.True(followUp!.ForceRefresh);

        var followUpSnapshot = queue.GetSnapshot();
        Assert.Equal(0, followUpSnapshot.PendingCount);
        Assert.Equal(1, followUpSnapshot.InFlightCount);
        Assert.True(followUpSnapshot.IsProcessing);
        Assert.NotNull(followUpSnapshot.CurrentJob);
        Assert.True(followUpSnapshot.CurrentJob!.ForceRefresh);

        var terminalFollowUp = queue.Complete(followUp, new PosterFetchProcessResult(true));
        Assert.Null(terminalFollowUp);
        Assert.Equal(0, queue.GetSnapshot().InFlightCount);
    }

    [Fact]
    public async Task Snapshot_IsProcessingTrueWhenInFlightAndQueueEmpty()
    {
        var queue = CreateQueue();

        await queue.EnqueueAsync(CreateJob(40), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
        _ = await queue.DequeueAsync(CancellationToken.None);

        var snapshot = queue.GetSnapshot();

        Assert.Equal(0, snapshot.PendingCount);
        Assert.Equal(1, snapshot.InFlightCount);
        Assert.True(snapshot.IsProcessing);
        Assert.NotNull(snapshot.CurrentJob);
        Assert.NotNull(snapshot.LastJobStartedAtTs);
    }

    [Fact]
    public async Task DequeueAsync_AllowsTwoConcurrentReaders()
    {
        var queue = CreateQueue();

        await queue.EnqueueAsync(CreateJob(50), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);
        await queue.EnqueueAsync(CreateJob(51), CancellationToken.None, PosterFetchQueue.DefaultEnqueueTimeout);

        var firstTask = queue.DequeueAsync(CancellationToken.None).AsTask();
        var secondTask = queue.DequeueAsync(CancellationToken.None).AsTask();

        await Task.WhenAll(firstTask, secondTask);

        Assert.NotEqual(firstTask.Result.ItemId, secondTask.Result.ItemId);
        Assert.Equal(2, queue.GetSnapshot().InFlightCount);
    }

    private static PosterFetchQueue CreateQueue()
        => new(NullLogger<PosterFetchQueue>.Instance);

    private static PosterFetchJob CreateJob(long itemId, bool forceRefresh = false)
        => new(
            ItemId: itemId,
            Title: $"Title {itemId}",
            Year: 2024,
            Category: UnifiedCategory.Film,
            ForceRefresh: forceRefresh,
            AttemptCount: 0,
            EntityId: null,
            RetroLogFile: null);
}
