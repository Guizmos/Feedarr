using Feedarr.Api.Services.Backup;

namespace Feedarr.Api.Tests;

public sealed class BackupExecutionCoordinatorTests
{
    [Fact]
    public async Task RunExclusiveAsync_ConcurrentOperations_RunOneAfterAnother()
    {
        var coordinator = new BackupExecutionCoordinator();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.RunExclusiveAsync(
            "create",
            null,
            async ct =>
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(ct);
                return 1;
            });

        await firstStarted.Task;

        var second = coordinator.RunExclusiveAsync(
            "restore",
            "backup.zip",
            async ct =>
            {
                secondStarted.SetResult();
                await Task.Yield();
                return 2;
            });

        await Task.Delay(100);
        Assert.False(secondStarted.Task.IsCompleted);

        releaseFirst.SetResult();

        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.True(secondStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task RunExclusiveAsync_CanBeCancelledWhileWaitingForGate()
    {
        var coordinator = new BackupExecutionCoordinator();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.RunExclusiveAsync(
            "create",
            null,
            async ct =>
            {
                await releaseFirst.Task.WaitAsync(ct);
                return 1;
            });

        using var cts = new CancellationTokenSource();
        var second = coordinator.RunExclusiveAsync(
            "restore",
            "backup.zip",
            async ct =>
            {
                await Task.Yield();
                return 2;
            },
            cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);

        releaseFirst.SetResult();
        Assert.Equal(1, await first);
    }
}
