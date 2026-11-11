using System.Text.Json;
using Feedarr.Api.Models;

namespace Feedarr.Api.Tests;

public sealed class GameMatchingStrategyContractTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Game_IgdbHit_GoldenContract()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbHit);
        var releaseId = rig.CreateRelease("Game Hit", "Game Hit", 2022, UnifiedCategory.JeuWindows, "game");

        var actual = await rig.FetchSnapshotAsync(releaseId);
        AssertSnapshot("game_igdb_hit", actual);
    }

    [Fact]
    public async Task Game_IgdbMiss_FailsWithoutFallback_GoldenContract()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbMiss);
        var releaseId = rig.CreateRelease("Game Miss", "Game Miss", 2022, UnifiedCategory.JeuWindows, "game");

        var actual = await rig.FetchSnapshotAsync(releaseId);
        AssertSnapshot("game_igdb_miss", actual);
    }

    [Fact]
    public async Task Game_RawgFallback_PreservesProviderIdentityWithoutTmdbPollution()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit);
        var id = rig.CreateRelease("Game Hit", "Game Hit", 2022, UnifiedCategory.JeuWindows, "game");
        var actual = await rig.FetchSnapshotAsync(id);
        Assert.True(actual.Ok);
        Assert.Equal("rawg", actual.PosterProvider);
        Assert.Equal("rawg-123-cover.jpg", actual.PosterFile);
        Assert.Null(actual.TmdbId);
        Assert.True((await rig.FetchSnapshotAsync(id)).Cached);
    }

    private static void AssertSnapshot(string key, PosterContractSnapshot actual)
    {
        var expectedMap = LoadExpected();
        Assert.True(expectedMap.TryGetValue(key, out var expected), $"Missing expected snapshot key: {key}");
        Assert.Equal(expected, actual);
    }

    private static Dictionary<string, PosterContractSnapshot> LoadExpected()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "game-contract-expected.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, PosterContractSnapshot>>(json, JsonOpts)
            ?? new Dictionary<string, PosterContractSnapshot>(StringComparer.OrdinalIgnoreCase);
    }
}
