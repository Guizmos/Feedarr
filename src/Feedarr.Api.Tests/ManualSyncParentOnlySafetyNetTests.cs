using Feedarr.Api.Services;

namespace Feedarr.Api.Tests;

public sealed class ManualSyncParentOnlySafetyNetTests
{
    [Fact]
    public void ShouldRejectParentOnlyMatch_ParentSelectedButOnlyUnselectedChildPresent_ReturnsTrue()
    {
        var ids = new[] { 5000, 5030 };
        var selected = new[] { 5000 };

        var reject = SyncOrchestrationService.ShouldRejectParentOnlyMatch(ids, selected);

        Assert.True(reject);
    }

    [Fact]
    public void ShouldRejectParentOnlyMatch_ParentAndChildSelected_ReturnsFalse()
    {
        var ids = new[] { 5000, 5030 };
        var selected = new[] { 5000, 5030 };

        var reject = SyncOrchestrationService.ShouldRejectParentOnlyMatch(ids, selected);

        Assert.False(reject);
    }
}
