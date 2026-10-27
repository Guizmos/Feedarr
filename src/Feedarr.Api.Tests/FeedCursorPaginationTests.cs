using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class FeedCursorPaginationTests
{
    private static readonly JsonSerializerOptions HttpJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Releases_EmptyCatalog_ReturnsEmptyTerminalPage()
    {
        using var context = new FeedTestContext();

        using var document = GetPage(context.Controller, limit: 50);
        var root = document.RootElement;

        Assert.Empty(root.GetProperty("items").EnumerateArray());
        Assert.False(root.GetProperty("pagination").GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("pagination").GetProperty("nextCursor").ValueKind);
        Assert.Equal(0, root.GetProperty("pagination").GetProperty("snapshotMaxId").GetInt64());
    }

    [Fact]
    public void Releases_FirstAndNextPage_AreStableAndReachEnd()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("source-a");
        context.InsertReleases(sourceId, 5, firstPublishedAt: 1_000);

        using var first = GetPage(context.Controller, limit: 2);
        var firstItems = Items(first);
        Assert.Equal(2, firstItems.Count);
        Assert.True(Pagination(first).GetProperty("hasMore").GetBoolean());
        var cursor = Pagination(first).GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        using var second = GetPage(context.Controller, limit: 2, cursor: cursor);
        var secondItems = Items(second);
        Assert.Equal(2, secondItems.Count);
        Assert.True(Pagination(second).GetProperty("hasMore").GetBoolean());

        using var third = GetPage(
            context.Controller,
            limit: 2,
            cursor: Pagination(second).GetProperty("nextCursor").GetString());
        Assert.Single(Items(third));
        Assert.False(Pagination(third).GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Pagination(third).GetProperty("nextCursor").ValueKind);

        var allIds = firstItems.Concat(secondItems).Concat(Items(third)).Select(Id).ToList();
        Assert.Equal(5, allIds.Count);
        Assert.Equal(5, allIds.Distinct().Count());
    }

    [Fact]
    public void Releases_MoreThanFiveHundred_AllRowsAreReachable()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("large-source");
        context.InsertReleases(sourceId, 525, firstPublishedAt: 10_000);

        var ids = new List<long>();
        string? cursor = null;
        do
        {
            using var page = GetPage(context.Controller, limit: 200, cursor: cursor);
            ids.AddRange(Items(page).Select(Id));
            cursor = Pagination(page).GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : Pagination(page).GetProperty("nextCursor").GetString();
        } while (cursor is not null);

        Assert.Equal(525, ids.Count);
        Assert.Equal(525, ids.Distinct().Count());
    }

    [Fact]
    public void Releases_IdenticalDates_UseDescendingIdAsTieBreaker()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("ties");
        context.InsertReleases(sourceId, 4, firstPublishedAt: 500, useIdenticalDates: true);

        using var first = GetPage(context.Controller, limit: 2);
        using var second = GetPage(
            context.Controller,
            limit: 2,
            cursor: Pagination(first).GetProperty("nextCursor").GetString());

        var ids = Items(first).Concat(Items(second)).Select(Id).ToList();
        Assert.Equal(ids.OrderByDescending(id => id), ids);
        Assert.Equal(4, ids.Distinct().Count());
    }

    [Fact]
    public void Releases_UsesEnabledSourcesAndSupportsMultipleSourceFilters()
    {
        using var context = new FeedTestContext();
        var firstSource = context.InsertSource("enabled-a");
        var secondSource = context.InsertSource("enabled-b");
        var disabledSource = context.InsertSource("disabled", enabled: false);
        context.InsertReleases(firstSource, 2, 300);
        context.InsertReleases(secondSource, 2, 200);
        context.InsertReleases(disabledSource, 2, 400);

        using var allEnabled = GetPage(context.Controller, limit: 20);
        Assert.Equal(4, Items(allEnabled).Count);
        Assert.DoesNotContain(Items(allEnabled), item => SourceId(item) == disabledSource);

        using var filtered = GetPage(context.Controller, limit: 20, sourceIds: [secondSource, firstSource]);
        Assert.Equal(4, Items(filtered).Count);
        Assert.All(Items(filtered), item => Assert.Contains(SourceId(item), new[] { firstSource, secondSource }));

        using var oneSource = GetPage(context.Controller, limit: 20, sourceIds: [secondSource]);
        Assert.All(Items(oneSource), item => Assert.Equal(secondSource, SourceId(item)));
    }

    [Fact]
    public void Releases_SearchAndUnifiedCategoryFilterApplyToWholeResultSet()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("filters");
        context.InsertRelease(sourceId, "wanted-film", "Wanted Film", 300, "films", "Film");
        context.InsertRelease(sourceId, "other-film", "Other Film", 200, "films", "Film");
        context.InsertRelease(sourceId, "wanted-series", "Wanted Series", 100, "series", "Serie");

        using var searched = GetPage(context.Controller, limit: 20, q: "Wanted");
        Assert.Equal(2, Items(searched).Count);

        using var films = GetPage(context.Controller, limit: 20, unifiedCategory: "films");
        Assert.Equal(2, Items(films).Count);
        Assert.All(Items(films), item => Assert.Equal("films", item.GetProperty("unifiedCategoryKey").GetString()));

        using var combined = GetPage(context.Controller, limit: 20, q: "Wanted", unifiedCategory: "series");
        Assert.Single(Items(combined));
        Assert.Equal("Wanted Series", Items(combined)[0].GetProperty("title").GetString());
    }

    [Fact]
    public void Releases_SeenAndMinimumSeedersReuseHistoricalSemantics()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("state-filters");
        context.InsertRelease(sourceId, "seen-high", "Seen High", 300, "films", "Film", seen: 1, seeders: 20);
        context.InsertRelease(sourceId, "unseen-high", "Unseen High", 200, "films", "Film", seen: 0, seeders: 15);
        context.InsertRelease(sourceId, "seen-low", "Seen Low", 100, "films", "Film", seen: 1, seeders: 2);

        using var page = GetPage(context.Controller, limit: 20, seen: 1, minSeeders: 10);
        var item = Assert.Single(Items(page));
        Assert.Equal("Seen High", item.GetProperty("title").GetString());
    }

    [Fact]
    public void Releases_InvalidCursorAndCursorWithChangedFiltersReturnBadRequest()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("cursor-errors");
        context.InsertReleases(sourceId, 3, 300);

        var invalid = context.Controller.Releases(2, "not-a-cursor", "date", null, null, null, null, null);
        AssertError(invalid, "invalid_cursor");

        using var first = GetPage(context.Controller, limit: 1);
        var cursor = Pagination(first).GetProperty("nextCursor").GetString();
        var mismatch = context.Controller.Releases(1, cursor, "date", "changed", null, null, null, null);
        AssertError(mismatch, "cursor_query_mismatch");
    }

    [Fact]
    public void Releases_NewInsertionDoesNotEnterExistingSnapshot()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("snapshot");
        context.InsertReleases(sourceId, 3, 300);

        using var first = GetPage(context.Controller, limit: 1);
        var snapshotMaxId = Pagination(first).GetProperty("snapshotMaxId").GetInt64();
        var cursor = Pagination(first).GetProperty("nextCursor").GetString();
        var insertedId = context.InsertRelease(sourceId, "new-release", "New Release", 1_000, "films", "Film");
        Assert.True(insertedId > snapshotMaxId);

        using var second = GetPage(context.Controller, limit: 10, cursor: cursor);
        Assert.DoesNotContain(Items(second), item => Id(item) == insertedId);

        using var refreshed = GetPage(context.Controller, limit: 10);
        Assert.Contains(Items(refreshed), item => Id(item) == insertedId);
    }

    [Fact]
    public void Releases_UnsupportedSortReturnsBadRequest()
    {
        using var context = new FeedTestContext();
        var result = context.Controller.Releases(50, null, "popularity", null, null, null, null, null);
        AssertError(result, "invalid_sort");
    }

    [Theory]
    [InlineData("date")]
    [InlineData("seeders")]
    [InlineData("grabs")]
    [InlineData("score")]
    public void Releases_AllSortsPageMoreThanFiveHundredAcrossSourcesWithFilters(string sort)
    {
        using var context = new FeedTestContext();
        var firstSource = context.InsertSource($"bulk-{sort}-a");
        var secondSource = context.InsertSource($"bulk-{sort}-b");
        context.InsertSortableReleases(firstSource, 260, 10_000);
        context.InsertSortableReleases(secondSource, 260, 9_000);
        context.InsertRelease(firstSource, "excluded-category", "Wanted Excluded", 20_000, "series", "Serie", seeders: 500, grabs: 500);

        var ids = new List<long>();
        string? cursor = null;
        var pageCount = 0;
        do
        {
            using var page = GetPage(
                context.Controller,
                200,
                cursor: cursor,
                q: "Wanted",
                unifiedCategory: "films",
                sourceIds: [secondSource, firstSource],
                sort: sort);
            pageCount++;
            ids.AddRange(Items(page).Select(Id));
            cursor = Pagination(page).GetProperty("nextCursor").GetString();
        } while (cursor is not null);

        Assert.Equal(3, pageCount);
        Assert.Equal(520, ids.Count);
        Assert.Equal(520, ids.Distinct().Count());
    }

    [Theory]
    [InlineData("date")]
    [InlineData("seeders")]
    [InlineData("grabs")]
    [InlineData("score")]
    public void Releases_AllSortsUseDescendingIdForCompleteTiesAndNormalizeNulls(string sort)
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource($"ties-{sort}");
        var firstId = context.InsertRelease(sourceId, "first", "First", null, "films", "Film", seeders: null, grabs: null);
        var secondId = context.InsertRelease(sourceId, "second", "Second", -1, "films", "Film", seeders: 0, grabs: 0);
        var thirdId = context.InsertRelease(sourceId, "third", "Third", null, "films", "Film", seeders: null, grabs: null);

        using var firstPage = GetPage(context.Controller, 2, sort: sort);
        using var secondPage = GetPage(
            context.Controller,
            2,
            cursor: Pagination(firstPage).GetProperty("nextCursor").GetString(),
            sort: sort);

        var ids = Items(firstPage).Concat(Items(secondPage)).Select(Id).ToList();
        Assert.Equal(new[] { thirdId, secondId, firstId }, ids);
        Assert.Equal(3, ids.Distinct().Count());
        Assert.False(Pagination(secondPage).GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public void Releases_SeedersSortUsesDateThenIdAsTieBreakers()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("seeders-order");
        var lowId = context.InsertRelease(sourceId, "low", "Low", 500, "films", "Film", seeders: 1, grabs: 100);
        var oldHighId = context.InsertRelease(sourceId, "old-high", "Old High", 100, "films", "Film", seeders: 10, grabs: 0);
        var newHighId = context.InsertRelease(sourceId, "new-high", "New High", 200, "films", "Film", seeders: 10, grabs: 0);

        using var page = GetPage(context.Controller, 20, sort: "seeders");
        Assert.Equal(new[] { newHighId, oldHighId, lowId }, Items(page).Select(Id));
    }

    [Fact]
    public void Releases_GrabsSortUsesSeedersDateAndIdAsTieBreakers()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("grabs-order");
        var lowerGrabsId = context.InsertRelease(sourceId, "lower-grabs", "Lower Grabs", 900, "films", "Film", seeders: 100, grabs: 4);
        var lowerSeedersId = context.InsertRelease(sourceId, "lower-seeders", "Lower Seeders", 800, "films", "Film", seeders: 5, grabs: 5);
        var olderId = context.InsertRelease(sourceId, "older", "Older", 100, "films", "Film", seeders: 10, grabs: 5);
        var newerId = context.InsertRelease(sourceId, "newer", "Newer", 200, "films", "Film", seeders: 10, grabs: 5);

        using var page = GetPage(context.Controller, 20, sort: "grabs");
        Assert.Equal(new[] { newerId, olderId, lowerSeedersId, lowerGrabsId }, Items(page).Select(Id));
    }

    [Fact]
    public void Releases_ScoreSortMatchesTopFormulaThenUsesDateAndId()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("score-order");
        var lowerScoreId = context.InsertRelease(sourceId, "lower", "Lower", 900, "films", "Film", seeders: 9, grabs: 0);
        var olderTenId = context.InsertRelease(sourceId, "older-ten", "Older Ten", 100, "films", "Film", seeders: 0, grabs: 2);
        var newerTenId = context.InsertRelease(sourceId, "newer-ten", "Newer Ten", 200, "films", "Film", seeders: 5, grabs: 1);

        using var page = GetPage(context.Controller, 20, sort: "score");
        Assert.Equal(new[] { newerTenId, olderTenId, lowerScoreId }, Items(page).Select(Id));
    }

    [Fact]
    public void Releases_CursorCannotBeReusedWithAnotherSort()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("sort-cursor");
        context.InsertReleases(sourceId, 3, 300);
        using var first = GetPage(context.Controller, 1, sort: "date");

        var result = context.Controller.Releases(
            1,
            Pagination(first).GetProperty("nextCursor").GetString(),
            "seeders",
            null,
            null,
            null,
            null,
            null);

        AssertError(result, "invalid_cursor");
    }

    [Fact]
    public void Releases_StatisticChangesBetweenPagesAreBestEffort()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("mutable-statistics");
        var firstId = context.InsertRelease(sourceId, "first", "First", 200, "films", "Film", seeders: 10);
        context.InsertRelease(sourceId, "second", "Second", 100, "films", "Film", seeders: 5);
        using var first = GetPage(context.Controller, 1, sort: "seeders");

        context.UpdateSeeders(firstId, 0);

        using var second = GetPage(
            context.Controller,
            10,
            cursor: Pagination(first).GetProperty("nextCursor").GetString(),
            sort: "seeders");
        Assert.Contains(Items(second), item => Id(item) == firstId);
    }

    [Fact]
    public void Releases_JsonContractIsCamelCaseAndMatchesHistoricalItem()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("json-contract");
        context.InsertRelease(sourceId, "contract", "Contract", 300, "films", "Film");

        using var paged = GetPage(context.Controller, limit: 20);
        var pagedItem = Assert.Single(Items(paged));
        Assert.Equal(JsonValueKind.Number, pagedItem.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.Number, pagedItem.GetProperty("sourceId").ValueKind);
        Assert.Equal(JsonValueKind.String, pagedItem.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Number, pagedItem.GetProperty("publishedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, pagedItem.GetProperty("unifiedCategoryKey").ValueKind);
        Assert.False(pagedItem.TryGetProperty("Id", out _));
        Assert.False(pagedItem.TryGetProperty("UnifiedCategoryKey", out _));

        var legacyResult = Assert.IsType<OkObjectResult>(
            context.Controller.Latest(sourceId, 20, null, null, null, null));
        using var legacy = SerializeHttpJson(legacyResult.Value);
        var legacyItem = Assert.Single(legacy.RootElement.EnumerateArray().ToList());

        var pagedShape = pagedItem.EnumerateObject().Select(p => (p.Name, p.Value.ValueKind)).ToArray();
        var legacyShape = legacyItem.EnumerateObject().Select(p => (p.Name, p.Value.ValueKind)).ToArray();
        Assert.Equal(legacyShape, pagedShape);
    }

    [Theory]
    [InlineData("films", "Film")]
    [InlineData("series", "Serie")]
    [InlineData("emissions", "Emission")]
    [InlineData("games", "JeuWindows")]
    [InlineData("animation", "Animation")]
    [InlineData("spectacle", "Spectacle")]
    public void Releases_FiltersCanonicalCategoriesAndReturnsMatchingKey(string key, string unifiedCategory)
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource($"category-{key}");
        context.InsertRelease(sourceId, key, key, 100, key, unifiedCategory);

        using var page = GetPage(context.Controller, 20, unifiedCategory: key);
        var item = Assert.Single(Items(page));
        Assert.Equal(key, item.GetProperty("unifiedCategoryKey").GetString());
    }

    [Fact]
    public void Releases_MoviesAliasUsesCanonicalFilmsFilterAndCursorFingerprint()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("movies-alias");
        context.InsertReleases(sourceId, 2, 200);

        using var first = GetPage(context.Controller, 1, unifiedCategory: "movies");
        Assert.Equal("films", first.RootElement.GetProperty("query").GetProperty("unifiedCategory").GetString());
        Assert.Equal("films", Assert.Single(Items(first)).GetProperty("unifiedCategoryKey").GetString());

        using var second = GetPage(
            context.Controller,
            1,
            cursor: Pagination(first).GetProperty("nextCursor").GetString(),
            unifiedCategory: "films");
        Assert.Single(Items(second));
    }

    [Fact]
    public void Releases_CategoryFilterAndProjectionUseSameEffectiveCategory()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("category-consistency");
        context.InsertRelease(sourceId, "stale-map", "Stale Map", 200, "films", "Serie");
        context.InsertRelease(sourceId, "missing-top", "Missing Top", 100, null, "Film");
        context.SetSourceCategoryMapping(sourceId, "series");

        using var films = GetPage(context.Controller, 20, unifiedCategory: "films");
        Assert.Equal(2, Items(films).Count);
        Assert.All(Items(films), item => Assert.Equal("films", item.GetProperty("unifiedCategoryKey").GetString()));

        using var series = GetPage(context.Controller, 20, unifiedCategory: "series");
        Assert.Empty(Items(series));
    }

    [Fact]
    public void Releases_NullAndNegativeDatesRemainPageableAndSortAtEpoch()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("invalid-dates");
        var positiveId = context.InsertRelease(sourceId, "positive", "Positive", 10, "films", "Film");
        var nullId = context.InsertRelease(sourceId, "null", "Null", null, "films", "Film");
        var negativeId = context.InsertRelease(sourceId, "negative", "Negative", -10, "films", "Film");

        var ids = new List<long>();
        string? cursor = null;
        do
        {
            using var page = GetPage(context.Controller, 1, cursor: cursor);
            ids.AddRange(Items(page).Select(Id));
            cursor = Pagination(page).GetProperty("nextCursor").GetString();
        } while (cursor is not null);

        Assert.Equal(new[] { positiveId, negativeId, nullId }, ids);
    }

    [Theory]
    [InlineData(0, null, null, null, "invalid_limit")]
    [InlineData(201, null, null, null, "invalid_limit")]
    [InlineData(50, 2, null, null, "invalid_seen")]
    [InlineData(50, null, -1, null, "invalid_min_seeders")]
    [InlineData(50, null, null, 0L, "invalid_source_id")]
    public void Releases_InvalidNumericParametersReturnBadRequest(
        int limit,
        int? seen,
        int? minSeeders,
        long? sourceId,
        string expectedError)
    {
        using var context = new FeedTestContext();
        var result = context.Controller.Releases(
            limit, null, "date", null, null, seen, minSeeders, sourceId.HasValue ? [sourceId.Value] : null);
        AssertError(result, expectedError);
    }

    [Fact]
    public void Releases_OversizedCursorReturnsBadRequest()
    {
        using var context = new FeedTestContext();
        var result = context.Controller.Releases(50, new string('a', 2049), "date", null, null, null, null, null);
        AssertError(result, "invalid_cursor");
    }

    [Fact]
    public void Latest_HistoricalEndpointStillReturnsArray()
    {
        using var context = new FeedTestContext();
        var sourceId = context.InsertSource("legacy");
        context.InsertReleases(sourceId, 3, 300);

        var result = Assert.IsType<OkObjectResult>(
            context.Controller.Latest(sourceId, 2, null, null, null, null));
        using var document = SerializeHttpJson(result.Value);

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(2, document.RootElement.GetArrayLength());
    }

    private static JsonDocument GetPage(
        FeedController controller,
        int limit,
        string? cursor = null,
        string? q = null,
        string? unifiedCategory = null,
        int? seen = null,
        int? minSeeders = null,
        long[]? sourceIds = null,
        string sort = "date")
    {
        var result = Assert.IsType<OkObjectResult>(
            controller.Releases(limit, cursor, sort, q, unifiedCategory, seen, minSeeders, sourceIds));
        return SerializeHttpJson(result.Value);
    }

    private static List<JsonElement> Items(JsonDocument document) =>
        document.RootElement.GetProperty("items").EnumerateArray().ToList();

    private static JsonElement Pagination(JsonDocument document) =>
        document.RootElement.GetProperty("pagination");

    private static long Id(JsonElement item) => item.GetProperty("id").GetInt64();
    private static long SourceId(JsonElement item) => item.GetProperty("sourceId").GetInt64();

    private static JsonDocument SerializeHttpJson(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, HttpJsonOptions));

    private static void AssertError(IActionResult result, string expectedError)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        using var document = SerializeHttpJson(badRequest.Value);
        Assert.Equal(expectedError, document.RootElement.GetProperty("error").GetString());
    }

    private sealed class FeedTestContext : IDisposable
    {
        private readonly TestWorkspace _workspace = new();
        private readonly Db _db;

        public FeedController Controller { get; }

        public FeedTestContext()
        {
            var options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });
            _db = new Db(options);
            new MigrationsRunner(_db, NullLogger<MigrationsRunner>.Instance).Run();
            Controller = new FeedController(
                _db,
                new UnifiedCategoryService(),
                new MemoryCache(new MemoryCacheOptions()));
        }

        public long InsertSource(string name, bool enabled = true)
        {
            using var conn = _db.Open();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES (@name, @enabled, @url, 'key', 'query', @now, @now);
                SELECT last_insert_rowid();
                """,
                new { name, enabled = enabled ? 1 : 0, url = $"http://localhost/{name}", now });
        }

        public void InsertReleases(long sourceId, int count, long firstPublishedAt, bool useIdenticalDates = false)
        {
            using var conn = _db.Open();
            using var transaction = conn.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (var index = 0; index < count; index++)
            {
                conn.Execute(
                    """
                    INSERT INTO releases(
                        source_id, guid, title, published_at_ts, category_id, unified_category,
                        top_category_key, seeders, seen, created_at_ts)
                    VALUES (@sourceId, @guid, @title, @publishedAt, 2000, 'Film', 'films', 10, 0, @now);
                    """,
                    new
                    {
                        sourceId,
                        guid = $"release-{sourceId}-{index}",
                        title = $"Release {index}",
                        publishedAt = useIdenticalDates ? firstPublishedAt : firstPublishedAt - index,
                        now
                    },
                    transaction);
            }
            transaction.Commit();
        }

        public void InsertSortableReleases(long sourceId, int count, long firstPublishedAt)
        {
            using var conn = _db.Open();
            using var transaction = conn.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (var index = 0; index < count; index++)
            {
                conn.Execute(
                    """
                    INSERT INTO releases(
                        source_id, guid, title, published_at_ts, category_id, unified_category,
                        top_category_key, seeders, grabs, seen, created_at_ts)
                    VALUES (@sourceId, @guid, @title, @publishedAt, 2000, 'Film', 'films',
                            @seeders, @grabs, 0, @now);
                    """,
                    new
                    {
                        sourceId,
                        guid = $"sortable-{sourceId}-{index}",
                        title = $"Wanted Release {index}",
                        publishedAt = firstPublishedAt - index,
                        seeders = index % 17,
                        grabs = index % 13,
                        now
                    },
                    transaction);
            }
            transaction.Commit();
        }

        public long InsertRelease(
            long sourceId,
            string guid,
            string title,
            long? publishedAt,
            string? categoryKey,
            string unifiedCategory,
            int seen = 0,
            int? seeders = 10,
            int? grabs = 0)
        {
            using var conn = _db.Open();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO releases(
                    source_id, guid, title, published_at_ts, category_id, unified_category,
                    top_category_key, seeders, grabs, seen, created_at_ts)
                VALUES (@sourceId, @guid, @title, @publishedAt, 2000, @unifiedCategory,
                        @categoryKey, @seeders, @grabs, @seen, @now);
                SELECT last_insert_rowid();
                """,
                new { sourceId, guid, title, publishedAt, categoryKey, unifiedCategory, seeders, grabs, seen, now });
        }

        public void UpdateSeeders(long releaseId, int seeders)
        {
            using var conn = _db.Open();
            conn.Execute("UPDATE releases SET seeders = @seeders WHERE id = @releaseId;", new { releaseId, seeders });
        }

        public void SetSourceCategoryMapping(long sourceId, string key)
        {
            using var conn = _db.Open();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            conn.Execute(
                """
                INSERT INTO source_categories(
                    source_id, cat_id, name, parent_cat_id, is_sub, last_seen_at_ts, unified_key, unified_label)
                VALUES (@sourceId, 2000, 'Mapped', NULL, 0, @now, @key, 'Mapped');

                INSERT INTO source_category_mappings(
                    source_id, cat_id, group_key, group_label, created_at_ts, updated_at_ts)
                VALUES (@sourceId, 2000, @key, 'Mapped', @now, @now);
                """,
                new { sourceId, key, now });
        }

        public void Dispose() => _workspace.Dispose();
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-tests", Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(RootDir, "data");
            Directory.CreateDirectory(DataDir);
        }

        public string RootDir { get; }
        public string DataDir { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temporary test data.
            }
        }
    }
}
