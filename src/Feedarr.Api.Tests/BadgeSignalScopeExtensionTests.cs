using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class BadgeSignalScopeExtensionTests
{
    [Fact]
    public async Task UpsertMany_Releases_EmitsReleasesSignal()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var sourceId = SeedSource(db);
        var signal = CreateSignal();
        var releases = new ReleaseRepository(
            db,
            new TitleParser(),
            new UnifiedCategoryResolver(),
            NullLogger<ReleaseRepository>.Instance,
            signal);

        var evt = await CaptureOneSignalAsync(signal, () =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            releases.UpsertMany(
                sourceId,
                indexerKey: "test-indexer",
                items:
                [
                    new TorznabItem
                    {
                        Guid = "release-1",
                        Title = "Test.Movie.2026.1080p",
                        PublishedAtTs = now
                    }
                ]);
        });

        Assert.Equal("releases", evt);
    }

    [Fact]
    public async Task SourceMutation_System_EmitsSystemSignal()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var signal = CreateSignal();
        var sources = new SourceRepository(
            db,
            new PassthroughProtectionService(),
            signal);

        var evt = await CaptureOneSignalAsync(signal, () =>
        {
            sources.Create(
                name: "Source SSE",
                torznabUrl: "https://example.test/torznab",
                apiKey: "api-key",
                authMode: "query");
        });

        Assert.Equal("system", evt);
    }

    [Fact]
    public async Task ActivitySignal_RemainsActivity()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var signal = CreateSignal();
        var activity = new ActivityRepository(db, signal);

        var evt = await CaptureOneSignalAsync(signal, () =>
        {
            activity.Add(null, "error", "sync", "sync failed");
        });

        Assert.Equal("activity", evt);
    }

    private static BadgeSignal CreateSignal()
        => new(
            OptionsFactory.Create(new AppOptions
            {
                BadgesSseCoalesceMs = 100
            }),
            NullLogger<BadgeSignal>.Instance);

    private static Db CreateDb(TestWorkspace workspace)
        => new(OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        }));

    private static long SeedSource(Db db)
    {
        using var conn = db.Open();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES ('Signal Source', 1, 'https://example.test', 'k', 'query', @ts, @ts);
            """,
            new { ts });
        return conn.ExecuteScalar<long>("SELECT id FROM sources ORDER BY id DESC LIMIT 1;");
    }

    private static async Task<string> CaptureOneSignalAsync(BadgeSignal signal, Action action, int timeoutMs = 4000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await using var enumerator = signal.Subscribe(cts.Token).GetAsyncEnumerator(cts.Token);

        action();

        var hasEvent = await enumerator.MoveNextAsync();
        Assert.True(hasEvent);
        return enumerator.Current;
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plaintext)
            => plaintext;

        public string Unprotect(string protectedValue)
            => protectedValue;

        public bool TryUnprotect(string protectedText, out string? plainText)
        {
            plainText = protectedText;
            return true;
        }

        public bool IsProtected(string value)
            => false;
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-badge-scope-tests", Guid.NewGuid().ToString("N"));
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
