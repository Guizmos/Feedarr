using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Options;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

/// <summary>
/// Vérifie via EXPLAIN QUERY PLAN que les indexes de performance lot 5
/// (migration 0043) sont effectivement utilisés par les requêtes stats.
/// </summary>
public sealed class SystemStatsPerfIndexTests
{
    // ─── Test 1 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// L'index d'expression idx_releases_source_unified_expr doit être utilisé
    /// par la requête GROUP BY de StatsIndexers, éliminant le USE TEMP B-TREE.
    /// </summary>
    [Fact]
    public void StatsIndexers_GroupBy_UsesExpressionIndex()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        // 3 sources × 4 catégories × 5 releases = 60 rows (volume minimal pour
        // que le query planner préfère l'index d'expression sur le temp b-tree).
        SeedIndexerData(db);

        const string sql =
            """
            SELECT s.id as sourceId,
                   s.name as sourceName,
                   COALESCE(NULLIF(TRIM(r.unified_category),''),'Autre') as unifiedCategory,
                   MAX(r.category_id) as categoryId,
                   COUNT(*) as count
            FROM releases r
            JOIN sources s ON r.source_id = s.id
            WHERE s.enabled = 1
            GROUP BY s.id, s.name, COALESCE(NULLIF(TRIM(r.unified_category),''),'Autre')
            ORDER BY s.name ASC, count DESC, s.id ASC,
                     COALESCE(NULLIF(TRIM(r.unified_category),''),'Autre') ASC
            LIMIT 100 OFFSET 0
            """;

        using var conn = db.Open();
        var details = conn
            .Query<QueryPlanRow>("EXPLAIN QUERY PLAN " + sql, new { })
            .Select(r => r.Detail)
            .ToList();

        // L'expression index doit apparaître dans le plan
        Assert.Contains(details, d => d.Contains("idx_releases_source_unified_expr",
            StringComparison.OrdinalIgnoreCase));

        // Pas de temp b-tree GROUP BY → streaming depuis l'index
        Assert.DoesNotContain(details, d => d.Contains("TEMP B-TREE FOR GROUP BY",
            StringComparison.OrdinalIgnoreCase));
    }

    // ─── Test 2 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// L'index couvrant idx_releases_provider_lookup doit remplacer le full scan
    /// de la table releases dans la subquery de StatsProviders.
    /// </summary>
    [Fact]
    public void StatsProviders_ProviderLookup_UsesCoveringIndex()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        // 100 releases avec 5 poster_providers différents (volume suffisant pour
        // que SQLite préfère le covering index au lieu des pages de table).
        SeedProviderData(db);

        const string sql =
            """
            SELECT providerKey, COUNT(1) as matchedCount
            FROM (
                SELECT LOWER(TRIM(COALESCE(
                    NULLIF(me.ext_provider, ''),
                    NULLIF(r.ext_provider, ''),
                    NULLIF(r.poster_provider, '')
                ))) as providerKey
                FROM releases r
                LEFT JOIN media_entities me ON me.id = r.entity_id
            ) x
            WHERE providerKey IS NOT NULL AND providerKey <> ''
            GROUP BY providerKey
            ORDER BY matchedCount DESC, providerKey ASC
            LIMIT 101
            """;

        using var conn = db.Open();
        var details = conn
            .Query<QueryPlanRow>("EXPLAIN QUERY PLAN " + sql, new { })
            .Select(r => r.Detail)
            .ToList();

        // Le covering index doit être utilisé pour le scan de releases
        Assert.Contains(details, d => d.Contains("idx_releases_provider_lookup",
            StringComparison.OrdinalIgnoreCase));
    }

    // ─── Test 3 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sanity check : les deux indexes de la migration 0043 doivent exister
    /// dans le schéma après application des migrations.
    /// </summary>
    [Fact]
    public void PerfIndexes_BothIndexesExistInSchema()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        using var conn = db.Open();
        var names = conn.Query<string>(
            """
            SELECT name FROM sqlite_master
            WHERE type = 'index'
              AND name IN ('idx_releases_source_unified_expr', 'idx_releases_provider_lookup')
            ORDER BY name;
            """
        ).ToList();

        Assert.Equal(2, names.Count);
        Assert.Contains("idx_releases_provider_lookup",    names);
        Assert.Contains("idx_releases_source_unified_expr", names);
    }

    // ─── Seed helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Seed 3 sources (enabled=1) × 4 catégories × 5 releases = 60 rows.
    /// Volume minimal pour que le planner SQLite préfère l'expression index.
    /// </summary>
    private static void SeedIndexerData(Db db)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var conn = db.Open();

        conn.Execute(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES ('Alpha', 1, 'http://a', 'k1', 'query', @now, @now),
                   ('Beta',  1, 'http://b', 'k2', 'query', @now, @now),
                   ('Gamma', 1, 'http://c', 'k3', 'query', @now, @now);
            """, new { now });

        var sources = conn.Query<long>("SELECT id FROM sources ORDER BY id;").ToList();

        var categories = new[] { "Film", "Serie", "Anime", "Musique" };
        var catId = 1000;
        var guid = 1;

        foreach (var srcId in sources)
        {
            foreach (var cat in categories)
            {
                for (var i = 0; i < 5; i++)
                {
                    conn.Execute(
                        """
                        INSERT INTO releases(source_id, guid, title, created_at_ts, unified_category, category_id, grabs, seeders, size_bytes)
                        VALUES (@srcId, @gid, @title, @now, @cat, @catId, 0, 0, 0);
                        """,
                        new { srcId, gid = $"g{guid}", title = $"T{guid}", now, cat, catId });
                    guid++;
                }
                catId++;
            }
        }
    }

    /// <summary>
    /// Seed 1 source + 100 releases avec 5 poster_providers × 20 releases.
    /// entity_id = NULL → pas de LEFT JOIN, covering index sur poster_provider.
    /// </summary>
    private static void SeedProviderData(Db db)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var conn = db.Open();

        conn.Execute(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES ('S', 1, 'http://s', 'k', 'query', @now, @now);
            """, new { now });

        var srcId = conn.ExecuteScalar<long>("SELECT id FROM sources ORDER BY id LIMIT 1;");

        var providers = new[] { "tmdb", "tvmaze", "fanart", "igdb", "jikan" };
        var guid = 5000;

        foreach (var provider in providers)
        {
            for (var i = 0; i < 20; i++)
            {
                conn.Execute(
                    """
                    INSERT INTO releases(source_id, guid, title, created_at_ts, poster_provider, grabs, seeders, size_bytes)
                    VALUES (@srcId, @gid, @title, @now, @provider, 0, 0, 0);
                    """,
                    new { srcId, gid = $"p{guid}", title = $"P{guid}", now, provider });
                guid++;
            }
        }
    }

    // ─── Infrastructure ──────────────────────────────────────────────────────

    private static Db CreateDb(TestWorkspace workspace) =>
        new Db(OptionsFactory.Create(new AppOptions
        {
            DataDir    = workspace.DataDir,
            DbFileName = "feedarr.db",
        }));

    /// <summary>Row returned by EXPLAIN QUERY PLAN (SQLite returns id/parent/notused as Int64).</summary>
    private sealed record QueryPlanRow(long Id, long Parent, long NotUsed, string Detail);

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
