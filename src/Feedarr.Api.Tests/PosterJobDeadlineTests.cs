using System.Diagnostics;
using System.Net;
using Feedarr.Api.Models;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Posters;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class PosterJobDeadlineTests
{
    [Fact]
    public async Task SlowStagesShareGlobalBudgetAndNextFastJobStillSucceeds()
    {
        var slow = true; var active = 0; var cancelled = 0; var calls = 0; var responses = 0; var disposed = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, tmdbResponse: async (request, ct) =>
        {
            Interlocked.Increment(ref calls); Interlocked.Increment(ref active);
            try
            {
                if (slow) await Task.Delay(700, ct);
                if (request.RequestUri!.Host == "image.tmdb.org")
                {
                    Interlocked.Increment(ref responses);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new TrackedImage(() => Interlocked.Increment(ref disposed)) };
                }
                return null;
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            finally { Interlocked.Decrement(ref active); }
        });
        var queue = new PosterFetchQueue(NullLogger<PosterFetchQueue>.Instance);
        var processor = Processor(rig, queue, request: 10, total: 1, attempts: 1);
        var id = Release(rig);
        var job = Job(id);
        await queue.EnqueueAsync(job, default, TimeSpan.FromSeconds(1));
        await queue.DequeueAsync(default);
        var watch = Stopwatch.StartNew();
        var result = await processor.ProcessJobAsync(job, 1, default);
        watch.Stop();
        Console.WriteLine($"Slow staged job: {watch.ElapsedMilliseconds}ms, succeeded={result.Succeeded}, HTTP calls={calls}, cancelled={cancelled}, active={active}");
        Assert.Null(queue.Complete(job, result));
        Assert.False(result.Succeeded);
        Assert.InRange(watch.ElapsedMilliseconds, 700, 1800);
        Assert.Equal(1, cancelled); Assert.Equal(0, active);
        Assert.Equal("time budget exceeded", rig.Repository.GetForPoster(id)!.PosterLastError);
        Assert.Equal(1, queue.GetSnapshot().JobsFailed); Assert.Equal(0, queue.GetSnapshot().InFlightCount);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.DoesNotContain(id, await rig.Repository.GetReleaseIdsMissingPosterActionableAsync(10, now));
        Assert.Contains(id, await rig.Repository.GetReleaseIdsMissingPosterActionableAsync(10, now + 16 * 60));
        slow = false;
        var next = Job(Release(rig));
        await queue.EnqueueAsync(next, default, TimeSpan.FromSeconds(1)); await queue.DequeueAsync(default);
        watch.Restart();
        var fast = await processor.ProcessJobAsync(next, 1, default);
        Console.WriteLine($"Next fast job: {watch.ElapsedMilliseconds}ms, succeeded={fast.Succeeded}");
        Assert.True(fast.Succeeded); Assert.Null(queue.Complete(next, fast));
        Assert.Equal(1, queue.GetSnapshot().JobsSucceeded); Assert.Equal(0, active);
        Assert.True(responses > 0); Assert.Equal(responses, disposed);
    }

    [Fact]
    public async Task GlobalDeadlineAlsoInterruptsRetryBackoffWithoutStartingAnotherAttempt()
    {
        var calls = 0; var active = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, tmdbResponse: async (_, ct) =>
        {
            Interlocked.Increment(ref calls); Interlocked.Increment(ref active);
            try { await Task.Delay(Timeout.Infinite, ct); return null; }
            finally { Interlocked.Decrement(ref active); }
        });
        var queue = new PosterFetchQueue(NullLogger<PosterFetchQueue>.Instance);
        var processor = Processor(rig, queue, request: 1, total: 2, attempts: 5);
        var id = Release(rig); var watch = Stopwatch.StartNew();
        var result = await processor.ProcessJobAsync(Job(id), 1, default);
        Console.WriteLine($"Budget including retry wait: {watch.ElapsedMilliseconds}ms, HTTP calls={calls}");
        Assert.False(result.Succeeded); Assert.Equal(1, calls); Assert.Equal(0, active);
        Assert.InRange(watch.ElapsedMilliseconds, 1700, 2800);
        Assert.Equal("time budget exceeded", rig.Repository.GetForPoster(id)!.PosterLastError);
        Assert.Equal(1, queue.GetSnapshot().JobsRetried);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDeadlinePreservesValidPosterAndStopsOutstandingRequest(bool serviceCancellation)
    {
        var slow = false; var active = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, tmdbResponse: async (_, ct) =>
        {
            Interlocked.Increment(ref active);
            try { if (slow) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return null; }
            finally { Interlocked.Decrement(ref active); }
        });
        var id = Release(rig); Assert.True((await rig.FetchAsync(id)).Ok);
        var before = rig.GetReleaseForPoster(id)!;
        var file = Path.Combine(rig.Posters.PostersDirPath, before.PosterFile!);
        var bytes = await File.ReadAllBytesAsync(file);
        slow = true;
        var processor = Processor(rig, new PosterFetchQueue(NullLogger<PosterFetchQueue>.Instance), 10, serviceCancellation ? 10 : 1, 3);
        using var stop = new CancellationTokenSource();
        var task = processor.ProcessJobAsync(Job(id), 1, stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (serviceCancellation) stop.Cancel();
        Assert.False((await task.WaitAsync(TimeSpan.FromSeconds(2))).Succeeded);
        Assert.Equal(0, active); Assert.Equal(before.PosterFile, rig.GetReleaseForPoster(id)!.PosterFile);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        Assert.Equal(before.ExtOverview, rig.GetReleaseForPoster(id)!.ExtOverview);
        Assert.Empty(Directory.EnumerateFiles(rig.Posters.PostersDirPath, "*.tmp", SearchOption.AllDirectories));
        if (!serviceCancellation) Assert.Equal("time budget exceeded", rig.GetReleaseForPoster(id)!.PosterLastError);
    }

    [Fact]
    public async Task PerAttemptTimeoutStillRetriesSuccessfullyWithinGlobalBudget()
    {
        var calls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, tmdbResponse: (_, _) =>
            Interlocked.Increment(ref calls) == 1
                ? Task.FromException<HttpResponseMessage?>(new OperationCanceledException("simulated HTTP timeout"))
                : Task.FromResult<HttpResponseMessage?>(null));
        var queue = new PosterFetchQueue(NullLogger<PosterFetchQueue>.Instance);
        var id = Release(rig);
        var processor = Processor(rig, queue, 10, 5, 2);
        var result = await processor.ProcessJobAsync(Job(id), 1, default);
        Assert.True(result.Succeeded); Assert.Equal(1, queue.GetSnapshot().JobsRetried);
        Assert.Null(rig.GetReleaseForPoster(id)!.PosterLastError);
    }

    private sealed class TrackedImage(Action onDispose) : ByteArrayContent(PosterTestImage.Create())
    {
        private int _disposed;
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
            base.Dispose(disposing);
        }
    }

    private static long Release(PosterMatchingContractTestRig rig) => rig.CreateRelease("The.Matrix.1999.1080p", "The Matrix", 1999, UnifiedCategory.Film, "movie");
    private static PosterFetchJob Job(long id) => new(id, "The Matrix", 1999, UnifiedCategory.Film, true, 0, null, null);
    private static PosterFetchJobProcessor Processor(PosterMatchingContractTestRig rig, PosterFetchQueue queue, int request, int total, int attempts) =>
        new(NullLogger<PosterFetchJobProcessor>.Instance, queue, rig.Posters, rig.Repository, null!,
            OptionsFactory.Create(new PosterFetchOptions { RequestTimeoutSeconds = request, MaxItemDurationSeconds = total, MaxAttempts = attempts, RetryDelaysSeconds = [1] }));
}
