using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class SystemStatsSummaryFeedarrTests
{
    [Fact]
    public async Task StatsSummary_ReturnsExpectedCounts_AndMatchingPercent()
    {
        using var workspace = new StatsTestWorkspace();
        var db = SystemStatsTestFactory.CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        SeedSourcesAndReleases(db);

        var controller = SystemStatsTestFactory.CreateController(db, workspace);
        var ok = Assert.IsType<OkObjectResult>(await controller.StatsSummary(CancellationToken.None));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

        Assert.Equal(1, doc.RootElement.GetProperty("activeIndexers").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("releasesCount").GetInt32());
        Assert.Equal(67, doc.RootElement.GetProperty("matchingPercent").GetInt32());
    }

    [Fact]
    public async Task StatsSummary_UsesCache_WithinTtl()
    {
        using var workspace = new StatsTestWorkspace();
        var db = SystemStatsTestFactory.CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        SeedSourcesAndReleases(db);

        var controller = SystemStatsTestFactory.CreateController(db, workspace);

        var first = Assert.IsType<OkObjectResult>(await controller.StatsSummary(CancellationToken.None));
        using var firstDoc = JsonDocument.Parse(JsonSerializer.Serialize(first.Value));
        Assert.Equal(3, firstDoc.RootElement.GetProperty("releasesCount").GetInt32());

        InsertRelease(db, "guid-new-summary-cache", "Cache test release");

        var second = Assert.IsType<OkObjectResult>(await controller.StatsSummary(CancellationToken.None));
        using var secondDoc = JsonDocument.Parse(JsonSerializer.Serialize(second.Value));
        Assert.Equal(3, secondDoc.RootElement.GetProperty("releasesCount").GetInt32());
    }

    [Fact]
    public async Task StatsSummary_WhenCancelled_ThrowsOperationCanceled()
    {
        using var workspace = new StatsTestWorkspace();
        var db = SystemStatsTestFactory.CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        SeedSourcesAndReleases(db);

        var controller = SystemStatsTestFactory.CreateController(db, workspace);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await controller.StatsSummary(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task StatsFeedarr_NormalizesDaysTo30_AndCachesByNormalizedKey()
    {
        using var workspace = new StatsTestWorkspace();
        var db = SystemStatsTestFactory.CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
        SeedSourcesAndReleases(db);

        var controller = SystemStatsTestFactory.CreateController(db, workspace);

        // days=15 is normalized to 30 in controller logic
        var first = Assert.IsType<OkObjectResult>(await controller.StatsFeedarr(15, CancellationToken.None));
        using var firstDoc = JsonDocument.Parse(JsonSerializer.Serialize(first.Value));
        Assert.Equal(3, firstDoc.RootElement.GetProperty("releasesCount").GetInt32());

        InsertRelease(db, "guid-new-feedarr-cache", "Cache test feedarr");

        // Same normalized key (30) -> still cached old count
        var second = Assert.IsType<OkObjectResult>(await controller.StatsFeedarr(30, CancellationToken.None));
        using var secondDoc = JsonDocument.Parse(JsonSerializer.Serialize(second.Value));
        Assert.Equal(3, secondDoc.RootElement.GetProperty("releasesCount").GetInt32());

        // days=7 uses another cache key -> recomputed value
        var third = Assert.IsType<OkObjectResult>(await controller.StatsFeedarr(7, CancellationToken.None));
        using var thirdDoc = JsonDocument.Parse(JsonSerializer.Serialize(third.Value));
        Assert.Equal(4, thirdDoc.RootElement.GetProperty("releasesCount").GetInt32());
    }

    private static void SeedSourcesAndReleases(Db db)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var conn = db.Open();

        conn.Execute(
            """
            INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
            VALUES ('Source Enabled', 1, 'http://localhost:9117/enabled', 'k1', 'query', @now, @now),
                   ('Source Disabled', 0, 'http://localhost:9117/disabled', 'k2', 'query', @now, @now);
            """,
            new { now });

        var sourceId = conn.ExecuteScalar<long>(
            "SELECT id FROM sources WHERE name='Source Enabled' LIMIT 1;");

        conn.Execute(
            """
            INSERT INTO releases(source_id, guid, title, created_at_ts, poster_file, unified_category, grabs, seeders, size_bytes)
            VALUES
            (@sourceId, 'guid-summary-1', 'Release 1', @now, 'posters/r1.jpg', 'Film', 0, 0, 1024),
            (@sourceId, 'guid-summary-2', 'Release 2', @now, 'posters/r2.jpg', 'Serie', 0, 0, 2048),
            (@sourceId, 'guid-summary-3', 'Release 3', @now, NULL, 'Anime', 0, 0, 4096);
            """,
            new { sourceId, now });
    }

    private static void InsertRelease(Db db, string guid, string title)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var conn = db.Open();
        var sourceId = conn.ExecuteScalar<long>(
            "SELECT id FROM sources WHERE name='Source Enabled' LIMIT 1;");

        conn.Execute(
            """
            INSERT INTO releases(source_id, guid, title, created_at_ts, poster_file, unified_category, grabs, seeders, size_bytes)
            VALUES (@sourceId, @guid, @title, @now, 'posters/new.jpg', 'Film', 0, 0, 1024);
            """,
            new { sourceId, guid, title, now });
    }
}
