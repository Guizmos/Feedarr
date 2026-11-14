using System.Text.Json;
using Feedarr.Api.Models;

namespace Feedarr.Api.Tests;

public sealed class VideoMatchingStrategyContractTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Series_TvmazeHit_TakesPriority_GoldenContract()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeHit);
        var releaseId = rig.CreateRelease("Show Tvmaze Hit", "Show Tvmaze Hit", 2015, UnifiedCategory.Serie, "series");

        var actual = await rig.FetchSnapshotAsync(releaseId);
        AssertSnapshot("series_tvmaze_hit", actual);
    }

    [Fact]
    public async Task Series_TvmazeMiss_FallsBackToTmdb_GoldenContract()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeMissTmdb);
        var releaseId = rig.CreateRelease("Show Tmdb Hit", "Show Tmdb Hit", 2018, UnifiedCategory.Serie, "series");

        var actual = await rig.FetchSnapshotAsync(releaseId);
        AssertSnapshot("series_tvmaze_miss_tmdb", actual);
    }

    [Fact]
    public async Task Series_TmdbWithoutPoster_FallsBackToFanart_GoldenContract()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTmdbFallbackFanart);
        var releaseId = rig.CreateRelease("Show Fanart", "Show Fanart", 2019, UnifiedCategory.Serie, "series");

        var actual = await rig.FetchSnapshotAsync(releaseId);
        AssertSnapshot("series_tmdb_fallback_fanart", actual);
    }

    [Fact]
    public async Task Emission_Ambiguous_TvmazeCandidateBelowThreshold_FallsBackToTmdb_GoldenContract()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.EmissionAmbiguousTmdbFallback);
        var releaseId = rig.CreateRelease("TF1", "TF1", 2024, UnifiedCategory.Emission, "series");

        var actual = await rig.FetchSnapshotAsync(releaseId);
        AssertSnapshot("emission_ambiguous_tmdb", actual);
    }

    private static void AssertSnapshot(string key, PosterContractSnapshot actual)
    {
        var expectedMap = LoadExpected();
        Assert.True(expectedMap.TryGetValue(key, out var expected), $"Missing expected snapshot key: {key}");
        Assert.Equal(expected, actual);
    }

    private static Dictionary<string, PosterContractSnapshot> LoadExpected()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "video-contract-expected.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, PosterContractSnapshot>>(json, JsonOpts)
            ?? new Dictionary<string, PosterContractSnapshot>(StringComparer.OrdinalIgnoreCase);
    }
}
