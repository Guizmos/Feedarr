using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Diagnostics;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SystemStatsReleasesUnifiedTests
{
    [Fact]
    public void StatsReleases_UsesUnifiedCategoryCounts_WithCanonicalLabels_SortedDesc()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        SeedReleases(db);

        var controller = CreateController(db, workspace);
        var result = Assert.IsType<OkObjectResult>(controller.StatsReleases());

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var categories = doc.RootElement.GetProperty("releasesByCategory").EnumerateArray().ToList();

        Assert.True(categories.Count >= 3);

        var counts = categories.Select(c => c.GetProperty("count").GetInt32()).ToList();
        var expectedSorted = counts.OrderByDescending(x => x).ToList();
        Assert.Equal(expectedSorted, counts);

        foreach (var category in categories)
        {
            var label = category.GetProperty("label").GetString();
            Assert.False(string.IsNullOrWhiteSpace(label));
        }

        Assert.Contains(categories, c => c.GetProperty("key").GetString() == "Serie" && c.GetProperty("label").GetString() == "Series TV");
        Assert.Contains(categories, c => c.GetProperty("key").GetString() == "Film" && c.GetProperty("label").GetString() == "Films");
        Assert.Contains(categories, c => c.GetProperty("key").GetString() == "Anime" && c.GetProperty("label").GetString() == "Anime");
    }

    private static void SeedReleases(Db db)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var conn = db.Open();
        conn.Execute(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES ('Test Source', 1, 'http://localhost:9117/torznab/test', 'key', 'query', @now, @now);
            """,
            new { now });

        var sourceId = conn.ExecuteScalar<long>("SELECT id FROM sources ORDER BY id DESC LIMIT 1;");

        var seedRows = new (string guid, string title, string unifiedCategory, int grabs)[]
        {
            ("guid-serie-1", "Serie A", "Serie", 5),
            ("guid-serie-2", "Serie B", "Serie", 4),
            ("guid-serie-3", "Serie C", "Serie", 3),
            ("guid-film-1", "Film A", "Film", 2),
            ("guid-film-2", "Film B", "Film", 1),
            ("guid-anime-1", "Anime A", "Anime", 6),
        };

        foreach (var row in seedRows)
        {
            conn.Execute(
                """
                INSERT INTO releases(source_id, guid, title, created_at_ts, unified_category, grabs, seeders, size_bytes)
                VALUES (@sourceId, @guid, @title, @now, @unifiedCategory, @grabs, 10, 1073741824);
                """,
                new
                {
                    sourceId,
                    row.guid,
                    row.title,
                    now,
                    row.unifiedCategory,
                    row.grabs
                });
        }
    }

    private static SystemController CreateController(Db db, TestWorkspace workspace)
    {
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });

        var settings = new SettingsRepository(db);
        var backup = new BackupService(
            db,
            new TestWebHostEnvironment(workspace.RootDir),
            options,
            settings,
            new BackupValidationService(),
            new BackupExecutionCoordinator(),
            new PassthroughProtectionService(),
            NullLogger<BackupService>.Instance);
        backup.InitializeForStartup();

        return new SystemController(
            db,
            new TestWebHostEnvironment(workspace.RootDir),
            options,
            settings,
            new ProviderStatsService(new StatsRepository(db)),
            new ApiRequestMetricsService(),
            backup,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<SystemController>.Instance);
    }

    private static Db CreateDb(TestWorkspace workspace)
    {
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });
        return new Db(options);
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string plainText)
        {
            plainText = protectedText;
            return true;
        }

        public bool IsProtected(string value) => false;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string rootDir)
        {
            ApplicationName = "Feedarr.Api.Tests";
            EnvironmentName = "Test";
            ContentRootPath = rootDir;
            ContentRootFileProvider = new NullFileProvider();
            WebRootPath = rootDir;
            WebRootFileProvider = new NullFileProvider();
        }

        public string ApplicationName { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
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
