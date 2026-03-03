using Feedarr.Api.Models;
using Feedarr.Api.Services.Sync;

namespace Feedarr.Api.Tests;

public sealed class SyncPlanBuilderTests
{
    [Fact]
    public void Build_ForSameInput_UsesSameRichFetchAcrossPolicies()
    {
        var builder = new SyncPlanBuilder();
        var input = CreateInput(defaultSeen: 1, rssOnly: false);

        var autoPlan = builder.Build(input, new AutoSyncPolicy());
        var manualPlan = builder.Build(input, new ManualSyncPolicy());
        var schedulerPlan = builder.Build(input, new SchedulerSyncPolicy());

        Assert.Equal(autoPlan.Fetch.PerCategoryLimit, manualPlan.Fetch.PerCategoryLimit);
        Assert.Equal(autoPlan.Fetch.PerCategoryLimit, schedulerPlan.Fetch.PerCategoryLimit);
        Assert.False(autoPlan.Fetch.AllowSearchInitial);
        Assert.False(manualPlan.Fetch.AllowSearchInitial);
        Assert.False(schedulerPlan.Fetch.AllowSearchInitial);
        Assert.True(autoPlan.Fetch.EnableCategoryFallback);
        Assert.True(manualPlan.Fetch.EnableCategoryFallback);
        Assert.True(schedulerPlan.Fetch.EnableCategoryFallback);

        Assert.Equal(autoPlan.Db.DefaultSeen, manualPlan.Db.DefaultSeen);
        Assert.Equal(autoPlan.Db.DefaultSeen, schedulerPlan.Db.DefaultSeen);
        Assert.Equal(autoPlan.Filter.SelectedCategoryIds, manualPlan.Filter.SelectedCategoryIds);
        Assert.Equal(autoPlan.Filter.SelectedCategoryIds, schedulerPlan.Filter.SelectedCategoryIds);
        Assert.Contains("films", autoPlan.Filter.SelectedUnifiedKeys);
    }

    [Fact]
    public void Build_EncapsulatesOnlyExplicitPolicyDifferences()
    {
        var builder = new SyncPlanBuilder();
        var input = CreateInput(defaultSeen: 0, rssOnly: false);

        var autoPlan = builder.Build(input, new AutoSyncPolicy());
        var manualPlan = builder.Build(input, new ManualSyncPolicy());
        var schedulerPlan = builder.Build(input, new SchedulerSyncPolicy());

        Assert.NotEqual(autoPlan.Telemetry.LogPrefix, manualPlan.Telemetry.LogPrefix);
        Assert.NotEqual(autoPlan.Telemetry.LogPrefix, schedulerPlan.Telemetry.LogPrefix);
        Assert.True(manualPlan.Telemetry.RecordPerSourceSyncJob);
        Assert.False(autoPlan.Telemetry.RecordPerSourceSyncJob);
        Assert.False(schedulerPlan.Telemetry.RecordPerSourceSyncJob);
        Assert.True(manualPlan.Telemetry.EmitCategoryDebugActivity);
        Assert.False(autoPlan.Telemetry.EmitCategoryDebugActivity);
        Assert.False(schedulerPlan.Telemetry.EmitCategoryDebugActivity);
    }

    [Fact]
    public void Build_RssOnlyDisablesFallbackButKeepsDefaultSeenSnapshot()
    {
        var builder = new SyncPlanBuilder();
        var input = CreateInput(defaultSeen: 1, rssOnly: true);

        var plan = builder.Build(input, new SchedulerSyncPolicy());

        Assert.True(plan.Fetch.RssOnly);
        Assert.False(plan.Fetch.EnableCategoryFallback);
        Assert.Equal(1, plan.Db.DefaultSeen);
        Assert.False(plan.Fetch.AllowSearchInitial);
    }

    private static SyncPlanInput CreateInput(int defaultSeen, bool rssOnly)
    {
        return new SyncPlanInput(
            new Source
            {
                Id = 42,
                Name = "Source",
                Enabled = true,
                TorznabUrl = "http://localhost:9117/api",
                ApiKey = "secret",
                AuthMode = "query",
                LastSyncAt = 123
            },
            new SyncEffectiveSettings(
                PerCategoryLimit: 50,
                GlobalLimit: 250,
                DefaultSeen: defaultSeen,
                RssOnly: rssOnly,
                EnableCategoryFallback: !rssOnly,
                AllowSearchInitial: false),
            new Dictionary<int, (string key, string label)>
            {
                [2000] = ("films", "Films")
            },
            PersistedCategoryIds: [2000],
            SelectedCategoryIds: [2000],
            MappedCategoryIds: [2000],
            UnmappedCategoryIds: [],
            LastSyncAt: 123,
            CorrelationId: "corr-1",
            TriggerReason: "scheduler");
    }
}
