using Feedarr.Api.Models;
using Feedarr.Api.Services.Posters;
using Microsoft.Extensions.Logging.Abstractions;
namespace Feedarr.Api.Tests;
public sealed class PosterFetchBatchTests
{
    private static PosterFetchQueue Queue() => new(NullLogger<PosterFetchQueue>.Instance);
    private static PosterFetchJob Job(long id, bool force = false) => new(id, "Example", 2024, UnifiedCategory.Film, force, 0, null, null);
    [Theory]
    [InlineData(0)] [InlineData(19)] [InlineData(300)]
    public async Task ThreeHundredJobsFinishWithoutDatabaseTimestamps(int failures)
    {
        var q = Queue(); var ids = Enumerable.Range(1, 300).Select(i => (long)i).ToArray();
        var batch = q.BeginBatch(ids)!.Value;
        await q.EnqueueManyAsync(ids.Select(i => Job(i)).ToArray(), default, TimeSpan.Zero); q.SealBatch(batch);
        for (var i = 0; i < 300; i++) q.Complete(await q.DequeueAsync(default), new(i >= failures));
        var progress = q.GetBatchProgress(batch)!;
        Assert.Equal(300, progress.Done); Assert.Equal(failures, progress.Failed);
        Assert.True(progress.Terminal); Assert.Equal(0, q.CountOutstanding(ids));
    }
    [Fact]
    public async Task CoalescedBatchesWaitForFollowUpAndIgnoreLaterJobs()
    {
        var q = Queue(); await q.EnqueueAsync(Job(1), default, TimeSpan.Zero);
        var active = await q.DequeueAsync(default);
        var a = q.BeginBatch(new long[] { 1 })!.Value; var b = q.BeginBatch(new long[] { 1 })!.Value;
        await q.EnqueueAsync(Job(1, true), default, TimeSpan.Zero); q.SealBatch(a); q.SealBatch(b);
        var follow = q.Complete(active, new(true)); Assert.NotNull(follow);
        Assert.False(q.GetBatchProgress(a)!.Terminal);
        q.Complete(follow!, new(false));
        Assert.True(q.GetBatchProgress(a)!.Terminal); Assert.Equal(1, q.GetBatchProgress(b)!.Failed);
        await q.EnqueueAsync(Job(1), default, TimeSpan.Zero); Assert.True(q.GetBatchProgress(a)!.Terminal);
    }
    [Fact]
    public async Task MissingAndCancelledIdsAreTerminalAndSurviveRefresh()
    {
        var q = Queue(); var batch = q.BeginBatch(new long[] { 1, 2, 3 })!.Value;
        await q.EnqueueAsync(Job(1) with { Priority = PosterFetchPriority.Background }, default, TimeSpan.Zero);
        q.SealBatch(batch); q.ClearPending(); var state = q.GetBatchProgress(batch)!;
        Assert.True(state.Terminal); Assert.Equal(2, state.NotAccepted); Assert.Equal(1, state.Cancelled);
        Assert.Equal(state, q.GetBatchProgress(batch));
    }
    [Fact]
    public async Task ConcurrentCompletionsAcrossPriorities()
    {
        var q = Queue(); var batch = q.BeginBatch(new long[] { 1, 2 })!.Value;
        await q.EnqueueAsync(Job(1), default, TimeSpan.Zero);
        await q.EnqueueAsync(Job(2) with { Priority = PosterFetchPriority.Background }, default, TimeSpan.Zero); q.SealBatch(batch);
        var first = await q.DequeueAsync(default); var second = await q.DequeueAsync(default);
        await Task.WhenAll(Task.Run(() => q.Complete(first, new(true))), Task.Run(() => q.Complete(second, new(false))));
        Assert.True(q.GetBatchProgress(batch)!.Terminal); Assert.Equal(2, q.GetBatchProgress(batch)!.Done);
    }
    [Fact]
    public async Task TimestampProgressStallsAt281Despite300CompletedJobs()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var ids = Enumerable.Range(1, 300).Select(i => rig.CreateRelease("Example " + i, "Example " + i, 2024, UnifiedCategory.Film, "movie")).ToArray();
        var start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var id in ids.Take(281)) rig.Repository.UpdatePosterAttemptFailure(id, null, null, null, null, "failed");
        var q = Queue(); var batch = q.BeginBatch(ids)!.Value;
        await q.EnqueueManyAsync(ids.Select(i => Job(i)).ToArray(), default, TimeSpan.Zero); q.SealBatch(batch);
        foreach (var id in ids) q.Complete(await q.DequeueAsync(default), new(false));
        Assert.Equal(281, rig.Repository.GetPosterProgressByIds(ids, start).done);
        Assert.Equal(0, q.Count); Assert.Equal(0, q.GetSnapshot().InFlightCount);
        Assert.Equal(300, q.GetBatchProgress(batch)!.Done); Assert.True(q.GetBatchProgress(batch)!.Terminal);
    }
    [Fact]
    public async Task QueueCapacityRefusalCountsAsNotAccepted()
    {
        var q = Queue();
        await q.EnqueueManyAsync(Enumerable.Range(1, 2000).Select(i => Job(i)).ToArray(), default, TimeSpan.Zero);
        var batch = q.BeginBatch(new long[] { 2001 })!.Value;
        var refused = await q.EnqueueAsync(Job(2001), default, TimeSpan.Zero); q.SealBatch(batch);
        Assert.Equal(PosterFetchEnqueueStatus.TimedOut, refused.Status);
        Assert.Equal(1, q.GetBatchProgress(batch)!.NotAccepted); Assert.True(q.GetBatchProgress(batch)!.Terminal);
    }
    [Fact]
    public async Task CancelledEnqueueCanBeSealedWithoutLeavingPendingIds()
    {
        var q = Queue(); var batch = q.BeginBatch(new long[] { 1 })!.Value;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await q.EnqueueAsync(Job(1), cancellation.Token, TimeSpan.Zero));
        q.SealBatch(batch);
        Assert.True(q.GetBatchProgress(batch)!.Terminal); Assert.Equal(1, q.GetBatchProgress(batch)!.NotAccepted);
    }
}
