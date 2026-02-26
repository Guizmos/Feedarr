using System.Net.Http;
using System.Text.RegularExpressions;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Arr;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Fanart;
using Feedarr.Api.Services.Igdb;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.TvMaze;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class Lot2ApiReliabilityTests
{
    [Fact]
    public void Migrations_ApplyLot2PerformanceIndexes()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using var conn = db.Open();
        var indexes = conn.Query<string>(
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'index'
              AND name IN (
                'idx_releases_created_at',
                'idx_releases_category_not_null',
                'idx_releases_grabs_not_null',
                'idx_releases_source_category'
              );
            """
        ).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("idx_releases_created_at", indexes);
        Assert.Contains("idx_releases_category_not_null", indexes);
        Assert.Contains("idx_releases_grabs_not_null", indexes);
        Assert.Contains("idx_releases_source_category", indexes);
    }

    [Fact]
    public async Task ArrStatus_WhenItemsMissing_ReturnsProblem400()
    {
        var controller = CreateArrControllerForValidationOnly();

        var action = await controller.CheckStatus(
            new ArrStatusRequestDto { Items = new List<ArrStatusItemDto>() },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(400, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("items missing", problem.Title);
    }

    [Fact]
    public async Task ArrStatus_WhenTooManyItems_ReturnsProblem400()
    {
        var controller = CreateArrControllerForValidationOnly();
        var items = Enumerable.Range(0, 251).Select(_ => new ArrStatusItemDto()).ToList();

        var action = await controller.CheckStatus(
            new ArrStatusRequestDto { Items = items },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(400, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("too many items (max 250)", problem.Title);
    }

    [Fact]
    public async Task SettingsExternalTest_WhenProviderThrows_ReturnsProblem502()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new TestProtectionService();
        var settingsRepo = new SettingsRepository(db, protection, NullLogger<SettingsRepository>.Instance);
        settingsRepo.SaveExternalPartial(new ExternalSettings
        {
            TmdbApiKey = "test-key",
            TmdbEnabled = true
        });
        var registry = new ExternalProviderRegistry();
        var externalInstances = new ExternalProviderInstanceRepository(
            db,
            settingsRepo,
            protection,
            registry,
            NullLogger<ExternalProviderInstanceRepository>.Instance);
        var resolver = new ActiveExternalProviderConfigResolver(
            externalInstances,
            registry,
            NullLogger<ActiveExternalProviderConfigResolver>.Instance);

        var stats = new ProviderStatsService(new StatsRepository(db, new MemoryCache(new MemoryCacheOptions())));
        var throwingHttp = new HttpClient(new ThrowingHttpMessageHandler());

        var tmdb = new TmdbClient(throwingHttp, settingsRepo, stats, resolver);
        var tvmaze = new TvMazeClient(throwingHttp, stats, resolver);
        var fanart = new FanartClient(throwingHttp, stats, resolver, settingsRepo);
        var igdb = new IgdbClient(throwingHttp, stats, resolver);

        var controller = new SettingsController(
            settingsRepo,
            OptionsFactory.Create(new AppOptions()),
            tmdb,
            fanart,
            igdb,
            tvmaze,
            new MemoryCache(new MemoryCacheOptions()),
            BuildConfiguration(),
            NullLogger<SettingsController>.Instance);

        var action = await controller.TestExternal(
            new SettingsController.ExternalTestDto { Kind = "tmdb" },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action);
        Assert.Equal(502, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("provider test failed", problem.Title);
    }

    [Fact]
    public void ErrorExposureGuard_Controllers_DoNotExposeRawExceptionMessages()
    {
        var repoRoot = FindRepositoryRoot();
        var controllersDir = Path.Combine(repoRoot, "src", "Feedarr.Api", "Controllers");
        Assert.True(Directory.Exists(controllersDir), $"Controllers directory not found: {controllersDir}");

        var patterns = new[]
        {
            new Regex(@"Problem\s*\(\s*title\s*:\s*ex\.Message", RegexOptions.Compiled),
            new Regex(@"\berror\s*=\s*ex\.Message\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"\bmessage\s*=\s*ex\.Message\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"\bdetail\s*[:=]\s*ex\.Message\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"BadRequest\s*\(\s*ex\.Message", RegexOptions.Compiled),
            new Regex(@"Conflict\s*\(\s*ex\.Message", RegexOptions.Compiled),
            new Regex(@"StatusCode\s*\(\s*\d+\s*,\s*ex\.Message", RegexOptions.Compiled)
        };

        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(controllersDir, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            foreach (var pattern in patterns)
            {
                if (pattern.IsMatch(content))
                    violations.Add($"{Path.GetRelativePath(repoRoot, file)} matches `{pattern}`");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Raw exception message exposure detected in controllers:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void ErrorExposureGuard_BackupPipeline_DoesNotPersistRawExceptionPatterns()
    {
        var repoRoot = FindRepositoryRoot();
        var coordinatorPath = Path.Combine(repoRoot, "src", "Feedarr.Api", "Services", "Backup", "BackupExecutionCoordinator.cs");
        var backupServicePath = Path.Combine(repoRoot, "src", "Feedarr.Api", "Services", "Backup", "BackupService.cs");

        var coordinator = File.ReadAllText(coordinatorPath);
        var backupService = File.ReadAllText(backupServicePath);

        Assert.DoesNotContain("MarkFailed(ex.Message)", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("new BackupOperationException(ex.Message", backupService, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupCoordinator_WhenErrorContainsSecret_LastErrorIsSanitized()
    {
        const string secretToken = "very-secret-token-value";
        var coordinator = new BackupExecutionCoordinator();

        _ = Assert.Throws<BackupOperationException>(() =>
            coordinator.RunExclusive<int>("create", "manual", () =>
            {
                throw new BackupOperationException(
                    $"upstream failed apikey={secretToken}",
                    StatusCodes.Status409Conflict);
            }));

        var state = coordinator.GetState();
        Assert.False(string.IsNullOrWhiteSpace(state.LastError));
        Assert.DoesNotContain(secretToken, state.LastError!, StringComparison.Ordinal);
        Assert.Contains("[redacted]", state.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    private static ArrController CreateArrControllerForValidationOnly()
    {
        return new ArrController(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            NullLogger<ArrController>.Instance,
            null!);
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
    }

    private static Db CreateDb(TestWorkspace workspace)
    {
        return new Db(OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        }));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Feedarr.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test context.");
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("simulated upstream failure");
        }
    }

    private sealed class TestProtectionService : IApiKeyProtectionService
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
