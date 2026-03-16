using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Titles;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ReleaseRepositoryTmdbRebindTests
{
    [Fact]
    public void RebindEntitiesByTmdb_CollapsesFragmentedMovieEntitiesAcrossYears()
    {
        using var ctx = new TmdbRebindContext();
        var canonicalEntityId = ctx.CreateEntity(
            unifiedCategory: "Film",
            titleClean: "Same Movie",
            year: null,
            tmdbId: 5001,
            extUpdatedAtTs: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120,
            extOverview: "canonical overview");
        var fragmentedEntityId = ctx.CreateEntity(
            unifiedCategory: "Film",
            titleClean: "Same Movie",
            year: 2025,
            tmdbId: 5001);

        var releaseA = ctx.CreateRelease(
            entityId: canonicalEntityId,
            titleClean: "Same Movie",
            mediaType: "movie",
            year: null,
            tmdbId: 5001);
        var releaseB = ctx.CreateRelease(
            entityId: fragmentedEntityId,
            titleClean: "Same Movie",
            mediaType: "movie",
            year: 2025,
            tmdbId: 5001);

        var result = ctx.Repository.RebindEntitiesByTmdb(200);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.EligibleGroups);
        Assert.Equal(1, result.ReleasesRebound);
        Assert.Equal(1, result.GroupsCollapsed);
        Assert.Equal(0, result.Errors);

        var entityA = ctx.GetReleaseEntityId(releaseA);
        var entityB = ctx.GetReleaseEntityId(releaseB);
        Assert.Equal(canonicalEntityId, entityA);
        Assert.Equal(canonicalEntityId, entityB);
    }

    [Fact]
    public void RebindEntitiesByTmdb_DoesNotCrossMergeMovieAndSeries()
    {
        using var ctx = new TmdbRebindContext();
        var movieEntityId = ctx.CreateEntity(
            unifiedCategory: "Film",
            titleClean: "Movie Side",
            year: 2024,
            tmdbId: 6001);
        var seriesEntityId = ctx.CreateEntity(
            unifiedCategory: "Serie",
            titleClean: "Series Side",
            year: 2024,
            tmdbId: 6001);

        var movieReleaseId = ctx.CreateRelease(
            entityId: movieEntityId,
            titleClean: "Movie Side",
            mediaType: "movie",
            year: 2024,
            tmdbId: 6001);
        var seriesReleaseId = ctx.CreateRelease(
            entityId: seriesEntityId,
            titleClean: "Series Side",
            mediaType: "series",
            year: 2024,
            tmdbId: 6001);

        var result = ctx.Repository.RebindEntitiesByTmdb(200);

        Assert.Equal(0, result.Scanned);
        Assert.Equal(0, result.ReleasesRebound);
        Assert.Equal(movieEntityId, ctx.GetReleaseEntityId(movieReleaseId));
        Assert.Equal(seriesEntityId, ctx.GetReleaseEntityId(seriesReleaseId));
    }

    [Fact]
    public void SaveTmdbId_RebindsToExistingCanonicalEntityForSameTmdbAndType()
    {
        using var ctx = new TmdbRebindContext();
        var canonicalEntityId = ctx.CreateEntity(
            unifiedCategory: "Film",
            titleClean: "Canonical Movie",
            year: 2024,
            tmdbId: 7001,
            extUpdatedAtTs: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 180,
            extOverview: "canonical");
        _ = ctx.CreateRelease(
            entityId: canonicalEntityId,
            titleClean: "Canonical Movie",
            mediaType: "movie",
            year: 2024,
            tmdbId: 7001);

        var fragmentedEntityId = ctx.CreateEntity(
            unifiedCategory: "Film",
            titleClean: "Canonical Movie",
            year: null,
            tmdbId: null);
        var fragmentedReleaseId = ctx.CreateRelease(
            entityId: fragmentedEntityId,
            titleClean: "Canonical Movie",
            mediaType: "movie",
            year: null,
            tmdbId: null);

        ctx.Repository.SaveTmdbId(fragmentedReleaseId, 7001);

        Assert.Equal(canonicalEntityId, ctx.GetReleaseEntityId(fragmentedReleaseId));
        Assert.Equal(7001, ctx.GetReleaseTmdbId(fragmentedReleaseId));
        Assert.True(ctx.EntityExists(fragmentedEntityId));
    }

    [Fact]
    public void RebindEntitiesByTmdb_IgnoresRowsWithoutTmdbId()
    {
        using var ctx = new TmdbRebindContext();
        var entityA = ctx.CreateEntity("Film", "No Id", null, null);
        var entityB = ctx.CreateEntity("Film", "No Id", 2025, null);
        var releaseA = ctx.CreateRelease(entityA, "No Id", "movie", null, null);
        var releaseB = ctx.CreateRelease(entityB, "No Id", "movie", 2025, null);

        var result = ctx.Repository.RebindEntitiesByTmdb(200);

        Assert.Equal(0, result.Scanned);
        Assert.Equal(0, result.ReleasesRebound);
        Assert.Equal(entityA, ctx.GetReleaseEntityId(releaseA));
        Assert.Equal(entityB, ctx.GetReleaseEntityId(releaseB));
    }

    [Fact]
    public void RebindEntitiesByTmdb_PreservesVisiblePosterAndMetadataAfterRebind()
    {
        using var ctx = new TmdbRebindContext();
        var canonicalEntityId = ctx.CreateEntity(
            unifiedCategory: "Film",
            titleClean: "Visible Data",
            year: null,
            tmdbId: 9001,
            posterFile: "canonical-9001.jpg",
            extUpdatedAtTs: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120,
            extOverview: "Canonical overview");
        _ = ctx.CreateRelease(
            entityId: canonicalEntityId,
            titleClean: "Visible Data",
            mediaType: "movie",
            year: null,
            tmdbId: 9001);

        var fragmentedEntityId = ctx.CreateEntity(
            unifiedCategory: "Film",
            titleClean: "Visible Data",
            year: 2025,
            tmdbId: 9001);
        var targetReleaseId = ctx.CreateRelease(
            entityId: fragmentedEntityId,
            titleClean: "Visible Data",
            mediaType: "movie",
            year: 2025,
            tmdbId: 9001);

        var result = ctx.Repository.RebindEntitiesByTmdb(200);

        Assert.Equal(1, result.ReleasesRebound);
        var target = ctx.Repository.GetForPoster(targetReleaseId);
        Assert.NotNull(target);
        Assert.Equal(canonicalEntityId, target!.EntityId);
        Assert.Equal("canonical-9001.jpg", target.PosterFile);
        Assert.Equal("Canonical overview", target.ExtOverview);
    }

    [Fact]
    public void SaveTmdbId_RebindInvalidatesEntityArrStatusRows()
    {
        using var ctx = new TmdbRebindContext();
        var canonicalEntityId = ctx.CreateEntity(
            unifiedCategory: "Film",
            titleClean: "Status Movie",
            year: 2024,
            tmdbId: 9100,
            extUpdatedAtTs: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120,
            extOverview: "canonical");
        _ = ctx.CreateRelease(
            entityId: canonicalEntityId,
            titleClean: "Status Movie",
            mediaType: "movie",
            year: 2024,
            tmdbId: 9100);

        var fragmentedEntityId = ctx.CreateEntity(
            unifiedCategory: "Film",
            titleClean: "Status Movie",
            year: null,
            tmdbId: null);
        var fragmentedReleaseId = ctx.CreateRelease(
            entityId: fragmentedEntityId,
            titleClean: "Status Movie",
            mediaType: "movie",
            year: null,
            tmdbId: null);

        ctx.InsertEntityArrStatus(canonicalEntityId);
        ctx.InsertEntityArrStatus(fragmentedEntityId);

        ctx.Repository.SaveTmdbId(fragmentedReleaseId, 9100);

        Assert.Equal(canonicalEntityId, ctx.GetReleaseEntityId(fragmentedReleaseId));
        Assert.Equal(0, ctx.GetEntityArrStatusCount(canonicalEntityId));
        Assert.Equal(0, ctx.GetEntityArrStatusCount(fragmentedEntityId));
    }

    private sealed class TmdbRebindContext : IDisposable
    {
        private readonly TestWorkspace _workspace;

        public TmdbRebindContext()
        {
            _workspace = new TestWorkspace();
            Db = CreateDb(_workspace);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();
            Repository = new ReleaseRepository(Db, new TitleParser(), new UnifiedCategoryResolver());

            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SourceId = conn.ExecuteScalar<long>(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES('test', 1, 'http://localhost:9117/api', 'key', 'query', @ts, @ts);
                SELECT last_insert_rowid();
                """,
                new { ts });
        }

        public Db Db { get; }
        public ReleaseRepository Repository { get; }
        public long SourceId { get; }

        public long CreateEntity(
            string unifiedCategory,
            string titleClean,
            int? year,
            int? tmdbId,
            string? posterFile = null,
            long? extUpdatedAtTs = null,
            string? extOverview = null)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO media_entities(
                  unified_category,
                  title_clean,
                  year,
                  tmdb_id,
                  poster_file,
                  poster_updated_at_ts,
                  ext_provider,
                  ext_provider_id,
                  ext_overview,
                  ext_updated_at_ts,
                  created_at_ts,
                  updated_at_ts
                )
                VALUES(
                  @unifiedCategory,
                  @titleClean,
                  @year,
                  @tmdbId,
                  @posterFile,
                  @posterUpdatedAtTs,
                  @extProvider,
                  @extProviderId,
                  @extOverview,
                  @extUpdatedAtTs,
                  @ts,
                  @ts
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    unifiedCategory,
                    titleClean,
                    year,
                    tmdbId,
                    posterFile,
                    posterUpdatedAtTs = string.IsNullOrWhiteSpace(posterFile) ? (long?)null : ts,
                    extProvider = extUpdatedAtTs.HasValue ? "tmdb" : null,
                    extProviderId = tmdbId?.ToString(),
                    extOverview,
                    extUpdatedAtTs,
                    ts
                });
        }

        public long CreateRelease(
            long entityId,
            string titleClean,
            string mediaType,
            int? year,
            int? tmdbId)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO releases(
                  source_id,
                  guid,
                  title,
                  created_at_ts,
                  published_at_ts,
                  title_clean,
                  year,
                  unified_category,
                  media_type,
                  entity_id,
                  tmdb_id
                )
                VALUES(
                  @sourceId,
                  @guid,
                  @title,
                  @ts,
                  @ts,
                  @titleClean,
                  @year,
                  @unifiedCategory,
                  @mediaType,
                  @entityId,
                  @tmdbId
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    sourceId = SourceId,
                    guid = Guid.NewGuid().ToString("N"),
                    title = titleClean,
                    titleClean,
                    year,
                    unifiedCategory = string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase) ? "Serie" : "Film",
                    mediaType,
                    entityId,
                    tmdbId,
                    ts
                });
        }

        public void InsertEntityArrStatus(long entityId)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            conn.Execute(
                """
                INSERT INTO media_entity_arr_status(entity_id, in_sonarr, in_radarr, checked_at_ts, match_method)
                VALUES(@entityId, 0, 1, @ts, 'id')
                ON CONFLICT(entity_id) DO UPDATE SET
                  in_sonarr = excluded.in_sonarr,
                  in_radarr = excluded.in_radarr,
                  checked_at_ts = excluded.checked_at_ts,
                  match_method = excluded.match_method;
                """,
                new { entityId, ts });
        }

        public long GetReleaseEntityId(long releaseId)
        {
            using var conn = Db.Open();
            return conn.ExecuteScalar<long>(
                "SELECT entity_id FROM releases WHERE id = @releaseId;",
                new { releaseId });
        }

        public int? GetReleaseTmdbId(long releaseId)
        {
            using var conn = Db.Open();
            return conn.ExecuteScalar<int?>(
                "SELECT tmdb_id FROM releases WHERE id = @releaseId;",
                new { releaseId });
        }

        public bool EntityExists(long entityId)
        {
            using var conn = Db.Open();
            return conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM media_entities WHERE id = @entityId;",
                new { entityId }) > 0;
        }

        public int GetEntityArrStatusCount(long entityId)
        {
            using var conn = Db.Open();
            return conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM media_entity_arr_status WHERE entity_id = @entityId;",
                new { entityId });
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private static Db CreateDb(TestWorkspace workspace)
        => new(OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        }));

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
