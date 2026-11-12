using Feedarr.Api.Controllers;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Feedarr.Api.Tests;

public sealed partial class MatchingReplayTests
{
    [Fact]
    public async Task GuardedStopCannotCancelReplacementSessionOrAdvanceGeneration()
    {
        using var fixture = new Fixture(); fixture.Seed(); fixture.Start(); var previous = await fixture.Done();
        fixture.Start(); var current = await fixture.Done();
        Assert.NotEqual(previous.SessionId, current.SessionId);
        Assert.Throws<InvalidOperationException>(() => fixture.Replay.Stop(expectedSessionId: previous.SessionId, expectedGeneration: previous.Generation));
        var context = new DefaultHttpContext(); context.Items[BasicAuthMiddleware.AuthPassedKey] = true;
        var controller = new MatchingReplayController(fixture.Replay) { ControllerContext = new() { HttpContext = context } };
        Assert.IsType<ConflictObjectResult>(controller.Stop(new(previous.SessionId, previous.Generation)));
        Assert.IsType<ConflictObjectResult>(controller.Stop(new(current.SessionId, previous.Generation)));
        Assert.Equal(current.Generation, fixture.Replay.Snapshot().Generation);
        Assert.Equal(current.SessionId, fixture.Replay.Snapshot().SessionId);
        Assert.IsType<OkObjectResult>(controller.Stop(new(current.SessionId, current.Generation)));
    }

    [Fact]
    public async Task GuardedOwnerStopCancelsItsInFlightReplayWithoutLatePublication()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Json(""); });
        fixture.Seed(); fixture.Start("tmdb"); var owner = fixture.Replay.Snapshot();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Replay.Stop(expectedSessionId: owner.SessionId, expectedGeneration: owner.Generation);
        var stopped = await fixture.Done();
        Assert.Equal("stopped", stopped.Status); Assert.Empty(stopped.Entries); Assert.Equal(1, fixture.Factory.Calls);
    }
}
