using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

/// <summary>
/// Regression tests for three poster-cache bugs:
///   1. Upsert with null poster_file must not overwrite an existing poster_file (COALESCE guard).
///   2. Upsert with confidence = 0.0 must not overwrite an existing positive confidence value.
///   3. UpdateExternalDetails must invalidate the poster_matches entry for the affected release.
/// </summary>
public sealed class PosterMatchCacheServiceTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static (Db db, TestWorkspace workspace) CreateDb()
    {
        var workspace = new TestWorkspace();
        var db = new Db(OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        }));
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        return (db, workspace);
    }

    private static PosterMatch MakePosterMatch(
        string fingerprint,
        string normalizedTitle = "inception",
        string mediaType = "movie",
        int? year = 2010,
        double confidence = 0.85,
        string? posterFile = "tmdb-123-w500.jpg",
        string? posterProvider = "tmdb",
        string? posterProviderId = "123")
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new PosterMatch(
            fingerprint, mediaType, normalizedTitle, year,
            season: null, episode: null,
            idsJson: null,
            confidence,
            matchSource: "tmdb",
            posterFile,
            posterProvider,
            posterProviderId,
            posterLang: null,
            posterSize: "w500",
            createdTs: ts, lastSeenTs: ts,
            lastAttemptTs: ts,
            lastError: null);
    }

    // ── Fix 1 regression: Upsert(null poster_file) must not erase existing file ─

    [Fact]
    public void Upsert_NullPosterFile_PreservesExistingPosterFile()
    {
        var (db, workspace) = CreateDb();
        using var _ = workspace;

        var cache = new PosterMatchCacheService(db);
        const string fp = "fp-null-poster-test";

        // Arrange: a good entry with a real poster file
        cache.Upsert(MakePosterMatch(fp, posterFile: "tmdb-123-w500.jpg", confidence: 0.85));

        // Act: upsert again with poster_file = null (simulates the removed code path
        // in PosterFetchService that wrote a cache entry even when no image was saved)
        cache.Upsert(MakePosterMatch(fp, posterFile: null, confidence: 0.70));

        // Assert: the original poster_file must be preserved by COALESCE
        var result = cache.TryGet(fp);
        Assert.NotNull(result);
        Assert.Equal("tmdb-123-w500.jpg", result.PosterFile);
    }

    // ── Fix 2: Upsert with confidence = 0 must not overwrite a good confidence ──

    [Fact]
    public void PosterMatchUpsert_DoesNotOverwriteConfidence_WhenIncomingIsZero()
    {
        var (db, workspace) = CreateDb();
        using var _ = workspace;

        var cache = new PosterMatchCacheService(db);
        const string fp = "fp-zero-confidence-test";

        // Arrange: existing entry with a meaningful confidence score
        cache.Upsert(MakePosterMatch(fp, confidence: 0.85));

        // Act: upsert with confidence = 0.0 (unset / default double value)
        // Before fix: COALESCE(0.0, 0.85) = 0.0  → overwrote good value.
        // After fix:  CASE WHEN 0.0 > 0 THEN 0.0 ELSE 0.85 END = 0.85 → preserved.
        cache.Upsert(MakePosterMatch(fp, confidence: 0.0));

        var result = cache.TryGet(fp);
        Assert.NotNull(result);
        Assert.Equal(0.85, result.Confidence, precision: 6);
    }

    [Fact]
    public void PosterMatchUpsert_OverwritesConfidence_WhenIncomingIsPositive()
    {
        var (db, workspace) = CreateDb();
        using var _ = workspace;

        var cache = new PosterMatchCacheService(db);
        const string fp = "fp-positive-confidence-test";

        cache.Upsert(MakePosterMatch(fp, confidence: 0.72));
        cache.Upsert(MakePosterMatch(fp, confidence: 0.65));

        var result = cache.TryGet(fp);
        Assert.NotNull(result);
        Assert.Equal(0.65, result.Confidence, precision: 6);
    }

    // ── Fix 3: UpdateExternalDetails must delete related poster_matches entry ──

    [Fact]
    public void UpdateExternalDetails_DeletesPosterMatchForCurrentPosterFile()
    {
        var (db, workspace) = CreateDb();
        using var _ = workspace;

        // Arrange: create a source + release with a poster_file already set
        long releaseId;
        using (var conn = db.Open())
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            conn.Execute(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES('test', 1, 'http://localhost', 'key', 'query', @ts, @ts);
                """,
                new { ts });
            var sourceId = conn.ExecuteScalar<long>("SELECT id FROM sources LIMIT 1;");

            conn.Execute(
                """
                INSERT INTO releases(source_id, guid, title, title_clean, media_type, year,
                                     poster_file, created_at_ts)
                VALUES(@sourceId, 'guid-1', 'Inception', 'Inception', 'movie', 2010,
                       'tmdb-27205-w500.jpg', @ts);
                """,
                new { sourceId, ts });
            releaseId = conn.ExecuteScalar<long>("SELECT id FROM releases LIMIT 1;");
        }

        // Arrange: insert a poster_matches entry using the same fingerprint that
        // UpdateExternalDetails will compute from the release's title_clean/year/media_type.
        var cache = new PosterMatchCacheService(db);
        var fp = PosterMatchCacheService.BuildFingerprint(
            new PosterTitleKey("movie", "inception", 2010, null, null));
        cache.Upsert(MakePosterMatch(
            fp,
            normalizedTitle: "inception",
            mediaType: "movie",
            year: 2010,
            posterFile: "tmdb-27205-w500.jpg",
            confidence: 0.90));

        // Pre-condition: entry exists in cache
        Assert.NotNull(cache.TryGet(fp));

        // Act: update external details (simulates correcting a TMDB ID)
        var repo = new ReleaseRepository(db, new TitleParser(), new UnifiedCategoryResolver());
        repo.UpdateExternalDetails(
            releaseId,
            provider: "tmdb",
            providerId: "27205",
            title: "Inception",
            overview: "A thief who steals corporate secrets.",
            tagline: null, genres: null, releaseDate: null,
            runtimeMinutes: 148, rating: 8.8, votes: 2_000_000,
            directors: null, writers: null, cast: null);

        // Assert: poster_matches entry must be gone — stale IDs invalidated
        Assert.Null(cache.TryGet(fp));
    }

    // ── TestWorkspace ────────────────────────────────────────────────────────

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
            try { if (Directory.Exists(RootDir)) Directory.Delete(RootDir, true); } catch { }
        }
    }
}
