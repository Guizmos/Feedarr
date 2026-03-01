using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ArrAlternateTitlesLookupTests
{
    [Fact]
    public void Sync_PopulatesAlternateTitlesRows()
    {
        using var context = new AlternateTitlesTestContext();
        var appId = context.CreateApp("radarr");

        context.Repository.SyncAppLibrary(appId, "movie", new List<LibraryItemDto>
        {
            CreateItem(1, tmdbId: 101, title: "Edge of Tomorrow", alternateTitles: new List<string> { "Live Die Repeat" })
        });

        var rows = context.GetAlternateTitleRows(appId, "movie");

        Assert.Contains(rows, row => row.InternalId == 1 && row.TitleNorm == TitleNormalizer.NormalizeTitleStrict("Live Die Repeat"));
        Assert.Contains(rows, row => row.InternalId == 1 && row.TitleRaw == "Live Die Repeat");
    }

    [Fact]
    public void Lookup_FindsByAlternateTitle_WithoutReadingJsonFallback()
    {
        using var context = new AlternateTitlesTestContext();
        var appId = context.CreateApp("radarr");

        context.Repository.SyncAppLibrary(appId, "movie", new List<LibraryItemDto>
        {
            CreateItem(7, tmdbId: 707, title: "Edge of Tomorrow", alternateTitles: new List<string> { "Live Die Repeat" })
        });

        context.CorruptAlternateTitlesJson(appId, "movie", 7);

        var match = context.Repository.FindMovieByTitle("Live Die Repeat");

        Assert.NotNull(match);
        Assert.Equal(7, match!.InternalId);
        Assert.Equal(707, match.TmdbId);
    }

    [Fact]
    public void Sync_ReplacesAlternateTitles()
    {
        using var context = new AlternateTitlesTestContext();
        var appId = context.CreateApp("sonarr");

        context.Repository.SyncAppLibrary(appId, "series", new List<LibraryItemDto>
        {
            CreateItem(5, tvdbId: 5005, title: "Original", alternateTitles: new List<string> { "Old Alias" })
        });

        context.Repository.SyncAppLibrary(appId, "series", new List<LibraryItemDto>
        {
            CreateItem(5, tvdbId: 5005, title: "Original", alternateTitles: new List<string> { "New Alias" })
        });

        var rows = context.GetAlternateTitleRows(appId, "series");

        Assert.DoesNotContain(rows, row => row.TitleNorm == TitleNormalizer.NormalizeTitleStrict("Old Alias"));
        Assert.Contains(rows, row => row.TitleNorm == TitleNormalizer.NormalizeTitleStrict("New Alias"));
    }

    [Fact]
    public void LookupQueryPlan_UsesAlternateTitleIndex()
    {
        using var context = new AlternateTitlesTestContext();
        var appId = context.CreateApp("sonarr");

        var items = Enumerable.Range(1, 250)
            .Select(index => CreateItem(
                index,
                tvdbId: 10_000 + index,
                title: $"Series {index}",
                alternateTitles: new List<string> { $"Alias {index}" }))
            .ToList();

        context.Repository.SyncAppLibrary(appId, "series", items);

        var planDetails = context.ExplainAlternateTitleLookup("series", TitleNormalizer.NormalizeTitleStrict("Alias 125"));

        Assert.Contains(planDetails, detail => detail.Contains("idx_arr_alt_titles_lookup", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(planDetails, detail => detail.Contains("SCAN", StringComparison.OrdinalIgnoreCase) && detail.Contains("arr_alternate_titles", StringComparison.OrdinalIgnoreCase));
    }

    private static LibraryItemDto CreateItem(
        int internalId,
        int? tmdbId = null,
        int? tvdbId = null,
        string title = "Title",
        List<string>? alternateTitles = null)
    {
        return new LibraryItemDto
        {
            InternalId = internalId,
            TmdbId = tmdbId,
            TvdbId = tvdbId,
            Title = title,
            OriginalTitle = $"{title} Original",
            TitleSlug = title.ToLowerInvariant().Replace(' ', '-'),
            AlternateTitles = alternateTitles
        };
    }

    private sealed class AlternateTitlesTestContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly ArrApplicationRepository _applications;

        public AlternateTitlesTestContext()
        {
            _workspace = new TestWorkspace();
            var options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            Db = new Db(options);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();
            Repository = new ArrLibraryRepository(Db);
            _applications = new ArrApplicationRepository(Db, new PassthroughProtectionService());
        }

        public Db Db { get; }
        public ArrLibraryRepository Repository { get; }

        public long CreateApp(string type)
        {
            return _applications.Create(
                type,
                $"Test {type}",
                "http://localhost:8989",
                "secret",
                null,
                null,
                null,
                null,
                seasonFolder: true,
                monitorMode: null,
                searchMissing: true,
                searchCutoff: false,
                minimumAvailability: null,
                searchForMovie: true);
        }

        public List<AlternateTitleRow> GetAlternateTitleRows(long appId, string type)
        {
            using var conn = Db.Open();
            return conn.Query<AlternateTitleRow>(
                """
                SELECT
                    internal_id AS InternalId,
                    title_norm AS TitleNorm,
                    title_raw AS TitleRaw
                FROM arr_alternate_titles
                WHERE app_id = @appId AND type = @type
                ORDER BY internal_id, title_norm;
                """,
                new { appId, type }).AsList();
        }

        public void CorruptAlternateTitlesJson(long appId, string type, int internalId)
        {
            using var conn = Db.Open();
            conn.Execute(
                """
                UPDATE arr_library_items
                SET alternate_titles = '{broken-json'
                WHERE app_id = @appId AND type = @type AND internal_id = @internalId;
                """,
                new { appId, type, internalId });
        }

        public List<string> ExplainAlternateTitleLookup(string type, string titleNorm)
        {
            using var conn = Db.Open();
            return conn.Query<ExplainPlanRow>(
                """
                EXPLAIN QUERY PLAN
                SELECT li.internal_id
                FROM arr_alternate_titles t INDEXED BY idx_arr_alt_titles_lookup
                JOIN arr_library_items li
                  ON li.app_id = t.app_id
                 AND li.type = t.type
                 AND li.internal_id = t.internal_id
                JOIN arr_applications a ON a.id = li.app_id
                WHERE t.type = @type
                  AND t.title_norm = @titleNorm
                  AND a.is_enabled = 1
                LIMIT 1;
                """,
                new { type, titleNorm })
                .Select(static row => row.Detail ?? string.Empty)
                .ToList();
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class AlternateTitleRow
    {
        public int InternalId { get; set; }
        public string TitleNorm { get; set; } = "";
        public string? TitleRaw { get; set; }
    }

    private sealed class ExplainPlanRow
    {
        public string? Detail { get; set; }
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedData) => protectedData;
        public bool TryUnprotect(string protectedText, out string plainText)
        {
            plainText = protectedText;
            return true;
        }

        public bool IsProtected(string value) => false;
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
                    Directory.Delete(RootDir, true);
            }
            catch
            {
            }
        }
    }
}
