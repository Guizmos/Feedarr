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

    [Fact]
    public async Task NormalJobsPassBackgroundWithFifoWithinEachPriority()
    {
        var queue=CreateQueue();
        foreach(var id in new long[]{101,102}) await queue.EnqueueAsync(CreateJob(id) with { Priority=PosterFetchPriority.Background },default,TimeSpan.Zero);
        foreach(var id in new long[]{1,2,3}) await queue.EnqueueAsync(CreateJob(id),default,TimeSpan.Zero);
        foreach(var id in new long[]{1,2,3,101,102})
        {
            var job=await queue.DequeueAsync(default); Assert.Equal(id,job.ItemId);
            queue.Complete(job,new(true));
        }
        Assert.Equal(0,queue.Count);
    }

    [Fact]
    public async Task BackgroundReceivesOneTurnAfterFourNormalJobs()
    {
        var queue=CreateQueue();
        foreach(var id in new long[]{101,102}) await queue.EnqueueAsync(CreateJob(id) with { Priority=PosterFetchPriority.Background },default,TimeSpan.Zero);
        for(int id=1;id<=12;id++) await queue.EnqueueAsync(CreateJob(id),default,TimeSpan.Zero);
        foreach(var id in new long[]{1,2,3,4,101,5,6,7,8,102,9,10,11,12})
        {
            var job=await queue.DequeueAsync(default); Assert.Equal(id,job.ItemId); queue.Complete(job,new(true));
        }
    }

    [Fact]
    public async Task PromotionAtCapacityDoesNotDuplicateOrDowngradeTheJob()
    {
        var queue=CreateQueue();
        for(int i=1;i<=2000;i++) await queue.EnqueueAsync(CreateJob(i) with { Priority=PosterFetchPriority.Background },default,TimeSpan.Zero);
        var promoted=await queue.EnqueueAsync(CreateJob(2000,true) with { RetroLogFile="manual.log" },default,TimeSpan.Zero);
        await queue.EnqueueAsync(CreateJob(2000) with { Priority=PosterFetchPriority.Background },default,TimeSpan.Zero);
        Assert.Equal(PosterFetchEnqueueStatus.Coalesced,promoted.Status);
        Assert.Equal(2000,queue.Count); Assert.Equal(2000,queue.GetSnapshot().JobsEnqueued); Assert.Equal(2,queue.GetSnapshot().JobsCoalesced);
        var job=await queue.DequeueAsync(default);
        Assert.Equal(2000,job.ItemId); Assert.True(job.ForceRefresh); Assert.Equal("manual.log",job.RetroLogFile);
        Assert.Equal(PosterFetchPriority.Normal,job.Priority); queue.Complete(job,new(true));
        var background=await queue.DequeueAsync(default); Assert.Equal(1,background.ItemId); queue.Complete(background,new(true));
        Assert.Equal(1998,queue.ClearPending()); Assert.Equal(0,queue.Count);
        await queue.EnqueueAsync(CreateJob(3000),default,TimeSpan.Zero);
        Assert.Equal(3000,(await queue.DequeueAsync(default)).ItemId);
    }

    [Fact]
    public async Task MixedPriorityCapacityAndCancelledWaitersDoNotLoseJobsOrTokens()
    {
        var queue=CreateQueue();
        for(int i=1;i<=2000;i++) await queue.EnqueueAsync(CreateJob(i) with { Priority=i%2==0 ? PosterFetchPriority.Normal : PosterFetchPriority.Background },default,TimeSpan.Zero);
        using var cancel=new CancellationTokenSource();
        var waiting=queue.EnqueueAsync(CreateJob(3000),cancel.Token,TimeSpan.FromSeconds(10)).AsTask();
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiting);
        Assert.Equal(2000,queue.Count); Assert.Equal(0,queue.GetSnapshot().JobsTimedOut);
        Assert.Equal(PosterFetchEnqueueStatus.TimedOut,(await queue.EnqueueAsync(CreateJob(3001),default,TimeSpan.FromMilliseconds(10))).Status);
        var job=await queue.DequeueAsync(default); Assert.Equal(2,job.ItemId);
        Assert.Equal(PosterFetchEnqueueStatus.Enqueued,(await queue.EnqueueAsync(CreateJob(3002),default,TimeSpan.Zero)).Status);
        Assert.Equal(2000,queue.Count); queue.Complete(job,new(true));
        Assert.Equal(2000,queue.ClearPending());
        using var stop=new CancellationTokenSource();
        var read=queue.DequeueAsync(stop.Token).AsTask(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>read);
        Assert.Equal(0,queue.GetSnapshot().InFlightCount);
    }

    [Fact]
    public async Task ActiveBackgroundJobIsNotInterruptedAndExplicitRefreshRunsOnceAfterIt()
    {
        var queue=CreateQueue();
        await queue.EnqueueAsync(CreateJob(1) with { Priority=PosterFetchPriority.Background },default,TimeSpan.Zero);
        var active=await queue.DequeueAsync(default);
        await queue.EnqueueAsync(CreateJob(1,true),default,TimeSpan.Zero);
        await queue.EnqueueAsync(CreateJob(1,true),default,TimeSpan.Zero);
        Assert.Equal(0,queue.Count); Assert.Equal(1,queue.GetSnapshot().InFlightCount);
        var followUp=queue.Complete(active,new(false));
        Assert.NotNull(followUp); Assert.True(followUp!.ForceRefresh); Assert.Equal(PosterFetchPriority.Normal,followUp.Priority);
        Assert.Null(queue.Complete(followUp,new(true)));
        Assert.Equal(2,queue.GetSnapshot().JobsProcessed); Assert.Equal(1,queue.GetSnapshot().JobsFailed); Assert.Equal(1,queue.GetSnapshot().JobsSucceeded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MixedPrioritiesAndPromotionsKeepIdsExclusiveWithOneOrTwoWorkers(int workers)
    {
        var queue=CreateQueue();
        for(int id=1;id<=60;id++) await queue.EnqueueAsync(CreateJob(id) with { Priority=id<=50 ? PosterFetchPriority.Background : PosterFetchPriority.Normal },default,TimeSpan.Zero);
        for(int id=1;id<=5;id++) await queue.EnqueueAsync(CreateJob(id),default,TimeSpan.Zero);
        var active=new System.Collections.Concurrent.ConcurrentDictionary<long,bool>();
        var seen=new System.Collections.Concurrent.ConcurrentDictionary<long,bool>();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(0,workers).Select(async worker=>{
            for(int index=worker;index<60;index+=workers)
            {
                var job=await queue.DequeueAsync(deadline.Token);
                Assert.True(active.TryAdd(job.ItemId,true)); Assert.True(seen.TryAdd(job.ItemId,true));
                await Task.Yield();
                active.TryRemove(job.ItemId,out _); Assert.Null(queue.Complete(job,new(true)));
            }
        }));
        Assert.Equal(60,seen.Count); Assert.Empty(active); Assert.Equal(0,queue.Count);
        Assert.Equal(0,queue.GetSnapshot().InFlightCount); Assert.Equal(60,queue.GetSnapshot().JobsSucceeded);
    }

    [Fact]
    public async Task ClearingBothPrioritiesKeepsTheActiveJobAndItsFollowUp()
    {
        var queue=CreateQueue();
        await queue.EnqueueAsync(CreateJob(1) with { Priority=PosterFetchPriority.Background },default,TimeSpan.Zero);
        var active=await queue.DequeueAsync(default);
        await queue.EnqueueAsync(CreateJob(1,true),default,TimeSpan.Zero);
        await queue.EnqueueAsync(CreateJob(2),default,TimeSpan.Zero);
        await queue.EnqueueAsync(CreateJob(3) with { Priority=PosterFetchPriority.Background },default,TimeSpan.Zero);
        Assert.Equal(2,queue.ClearPending()); Assert.Equal(0,queue.Count);
        Assert.Equal(1,queue.GetSnapshot().InFlightCount);
        var followUp=queue.Complete(active,new(true)); Assert.NotNull(followUp);
        Assert.Null(queue.Complete(followUp!,new(true))); Assert.False(queue.GetSnapshot().IsProcessing);
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
