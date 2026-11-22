using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Microsoft.Extensions.Logging;

namespace Feedarr.Api.Tests;

public sealed class VideoResultBudgetDiagnosticTests
{
    [Theory]
    [InlineData(false, 11)]
    [InlineData(false, 12)]
    [InlineData(false, 13)]
    [InlineData(true, 99)]
    [InlineData(true, 100)]
    [InlineData(true, 101)]
    public void BothBudgetsKeepTheExistingStrictLimitAndPublicReason(bool projection, int count)
    {
        var limit = projection ? new CandidateRankingPolicy().MaximumResults : VideoSearchCollector.MaximumPages;
        var counters = new VideoResultBudgetCounters(1, 1, 1, 0);
        var diagnostic = projection
            ? VideoResultBudgetDiagnostic.CheckProjection(count, limit, counters)
            : VideoResultBudgetDiagnostic.CheckSearchPages(count, limit, counters);

        Assert.Equal(projection ? 100 : 12, limit);
        Assert.Equal(count > limit, diagnostic is not null);
        Assert.Equal("not_evaluable_result_budget", VideoResultBudgetDiagnostic.AbstentionReason);
        if (diagnostic is null) return;
        Assert.Equal(projection ? "not_evaluable_projected_candidate_budget" : "not_evaluable_search_budget", diagnostic.Reason);
        Assert.Equal(projection ? "projected_candidates" : "search_pages", diagnostic.LimitName);
        Assert.Equal(count, diagnostic.Count);
        Assert.Equal(limit, diagnostic.Limit);
    }

    [Fact]
    public void PageBudgetLogsQueryTotalsWithoutInventingAProjection()
    {
        var diagnostic = VideoResultBudgetDiagnostic.CheckSearchPages(13, 12,
            new(1, 1, 1, 0, Page: 1, TotalPages: 13, TotalResults: 241))!;
        var logger = new CaptureLogger();

        diagnostic.Log(logger, 42, UnifiedCategory.Film);

        Assert.Equal(1, logger.Fields["SearchQueries"]);
        Assert.Equal(1, logger.Fields["SearchRequests"]);
        Assert.Equal(1, logger.Fields["PagesReceived"]);
        Assert.Equal(1, logger.Fields["Page"]);
        Assert.Equal(13, logger.Fields["TotalPages"]);
        Assert.Equal(241, logger.Fields["TotalResults"]);
        Assert.Null(logger.Fields["ProjectedCandidates"]);
        Assert.Equal(0, logger.Fields["ObservedResults"]);
        AssertSafeCommonFields(logger, "not_evaluable_search_budget", 13, 12);
    }

    [Fact]
    public void ProjectionOverflowLogsAggregatedCountsWithoutInventingQueryTotals()
    {
        var diagnostic = VideoResultBudgetDiagnostic.CheckProjection(120, 100, new(2, 6, 6, 120))!;
        var logger = new CaptureLogger();

        diagnostic.Log(logger, 42, UnifiedCategory.Serie);

        Assert.Equal(2, logger.Fields["SearchQueries"]);
        Assert.Equal(6, logger.Fields["SearchRequests"]);
        Assert.Equal(6, logger.Fields["PagesReceived"]);
        Assert.Equal(120, logger.Fields["ObservedResults"]);
        Assert.Equal(120, logger.Fields["ProjectedCandidates"]);
        Assert.Null(logger.Fields["Page"]);
        Assert.Null(logger.Fields["TotalPages"]);
        Assert.Null(logger.Fields["TotalResults"]);
        AssertSafeCommonFields(logger, "not_evaluable_projected_candidate_budget", 120);
    }

    private static void AssertSafeCommonFields(CaptureLogger logger, string reason, int count, int limit = 100)
    {
        Assert.Equal(LogLevel.Warning, logger.Level);
        Assert.Equal(42L, logger.Fields["ReleaseId"]);
        Assert.Equal("collection", logger.Fields["Stage"]);
        Assert.Equal(reason, logger.Fields["DiagnosticReason"]);
        Assert.Equal(count, logger.Fields["Count"]);
        Assert.Equal(limit, logger.Fields["Limit"]);
        Assert.DoesNotContain(logger.Fields.Keys, key => key is "Title" or "Query" or "Url" or "ApiKey" or "Response");
        Assert.All(logger.Fields.Where(pair => pair.Key != "{OriginalFormat}"), pair =>
            Assert.True(pair.Value is null or int or long or UnifiedCategory ||
                pair.Key is "Stage" or "DiagnosticReason" or "LimitName"));
    }

    private sealed class CaptureLogger : ILogger
    {
        internal LogLevel Level { get; private set; }
        internal Dictionary<string, object?> Fields { get; private set; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            Fields = ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(pair => pair.Key, pair => pair.Value);
        }
    }
}
