using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ArrLibraryRepositoryTests
{
    [Fact]
    public void Sync_InsertsAllItems()
    {
        using var context = new ArrLibraryRepositoryTestContext();
        var appId = context.CreateApp("sonarr");

        context.Repository.SyncAppLibrary(appId, "series", new List<LibraryItemDto>
        {
            CreateItem(1, tvdbId: 101, title: "Alpha"),
            CreateItem(2, tvdbId: 102, title: "Beta"),
            CreateItem(3, tvdbId: 103, title: "Gamma")
        });

        var rows = context.GetLibraryRows(appId, "series");
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.InternalId).ToArray());
        Assert.All(rows, row => Assert.Equal("series", row.Type));
        Assert.Equal(3, context.GetSyncCount(appId));
    }

    [Fact]
    public void Sync_ReplacesExisting()
    {
        using var context = new ArrLibraryRepositoryTestContext();
        var appId = context.CreateApp("radarr");

        context.Repository.SyncAppLibrary(appId, "movie", new List<LibraryItemDto>
        {
            CreateItem(10, tmdbId: 2010, title: "Old One"),
            CreateItem(11, tmdbId: 2011, title: "Old Two")
        });
        context.Repository.SyncAppLibrary(appId, "series", new List<LibraryItemDto>
        {
            CreateItem(30, tvdbId: 3030, title: "Series Keep")
        });

        context.Repository.SyncAppLibrary(appId, "movie", new List<LibraryItemDto>
        {
            CreateItem(20, tmdbId: 2020, title: "New One")
        });

        var movies = context.GetLibraryRows(appId, "movie");
        var series = context.GetLibraryRows(appId, "series");

        var movie = Assert.Single(movies);
        Assert.Equal(20, movie.InternalId);
        Assert.Equal("New One", movie.Title);
        Assert.Single(series);
        Assert.Equal(30, series[0].InternalId);
        Assert.Equal(1, context.GetSyncCount(appId));
    }

    [Fact]
    public void Sync_IsIdempotent()
    {
        using var context = new ArrLibraryRepositoryTestContext();
        var appId = context.CreateApp("sonarr");
        var items = new List<LibraryItemDto>
        {
            CreateItem(1, tvdbId: 1001, title: "Fallout", alternateTitles: new List<string> { "Fallout Alt" }),
            CreateItem(2, tvdbId: 1002, title: "Severance")
        };

        context.Repository.SyncAppLibrary(appId, "series", items);
        var first = context.GetLibraryRows(appId, "series");

        context.Repository.SyncAppLibrary(appId, "series", items);
        var second = context.GetLibraryRows(appId, "series");

        Assert.Equal(first.Select(ToComparableShape), second.Select(ToComparableShape));
        Assert.Equal(2, context.GetSyncCount(appId));
    }

    [Fact]
    public void Sync_LargeBatch_DoesNotTimeout()
    {
        using var context = new ArrLibraryRepositoryTestContext();
        var appId = context.CreateApp("radarr");
        var items = Enumerable.Range(1, 5000)
            .Select(index => CreateItem(index, tmdbId: 10_000 + index, title: $"Movie {index}"))
            .ToList();

        context.Repository.SyncAppLibrary(appId, "movie", items);

        Assert.Equal(5000, context.GetLibraryRowCount(appId, "movie"));
        Assert.Equal(5000, context.GetSyncCount(appId));
    }

    [Fact]
    public void Sync_StageEmpty_ClearsExistingAndUpdatesStatus()
    {
        using var context = new ArrLibraryRepositoryTestContext();
        var appId = context.CreateApp("sonarr");

        context.Repository.SyncAppLibrary(appId, "series", new List<LibraryItemDto>
        {
            CreateItem(1, tvdbId: 1001, title: "Initial")
        });

        context.Repository.SyncAppLibrary(appId, "series", new List<LibraryItemDto>());

        Assert.Empty(context.GetLibraryRows(appId, "series"));
        Assert.Equal(0, context.GetSyncCount(appId));
    }

    [Fact]
    public void Sync_DeduplicatesInternalId_LastWins()
    {
        using var context = new ArrLibraryRepositoryTestContext();
        var appId = context.CreateApp("radarr");

        context.Repository.SyncAppLibrary(appId, "movie", new List<LibraryItemDto>
        {
            CreateItem(7, tmdbId: 1007, title: "First Title"),
            CreateItem(7, tmdbId: 2007, title: "Second Title", alternateTitles: new List<string> { "Second Alt" })
        });

        var row = Assert.Single(context.GetLibraryRows(appId, "movie"));
        Assert.Equal(7, row.InternalId);
        Assert.Equal(2007, row.TmdbId);
        Assert.Equal("Second Title", row.Title);
        Assert.Contains("Second Alt", row.AlternateTitles ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, context.GetSyncCount(appId));
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

    private static string ToComparableShape(LibraryRow row)
    {
        return string.Join(
            "|",
            row.Type,
            row.InternalId,
            row.TmdbId?.ToString() ?? "",
            row.TvdbId?.ToString() ?? "",
            row.Title,
            row.OriginalTitle ?? "",
            row.TitleSlug ?? "",
            row.AlternateTitles ?? "",
            row.TitleNormalized ?? "");
    }

    private sealed class ArrLibraryRepositoryTestContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly ArrApplicationRepository _applications;

        public ArrLibraryRepositoryTestContext()
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

        public List<LibraryRow> GetLibraryRows(long appId, string type)
        {
            using var conn = Db.Open();
            return conn.Query<LibraryRow>(
                """
                SELECT
                    type AS Type,
                    internal_id AS InternalId,
                    tmdb_id AS TmdbId,
                    tvdb_id AS TvdbId,
                    title AS Title,
                    original_title AS OriginalTitle,
                    title_slug AS TitleSlug,
                    alternate_titles AS AlternateTitles,
                    title_normalized AS TitleNormalized
                FROM arr_library_items
                WHERE app_id = @appId AND type = @type
                ORDER BY internal_id;
                """,
                new { appId, type }).AsList();
        }

        public int GetLibraryRowCount(long appId, string type)
        {
            using var conn = Db.Open();
            return conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM arr_library_items WHERE app_id = @appId AND type = @type;",
                new { appId, type });
        }

        public int GetSyncCount(long appId)
        {
            using var conn = Db.Open();
            return conn.ExecuteScalar<int>(
                "SELECT COALESCE(last_sync_count, 0) FROM arr_sync_status WHERE app_id = @appId;",
                new { appId });
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class LibraryRow
    {
        public string Type { get; set; } = "";
        public int InternalId { get; set; }
        public int? TmdbId { get; set; }
        public int? TvdbId { get; set; }
        public string Title { get; set; } = "";
        public string? OriginalTitle { get; set; }
        public string? TitleSlug { get; set; }
        public string? AlternateTitles { get; set; }
        public string? TitleNormalized { get; set; }
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
