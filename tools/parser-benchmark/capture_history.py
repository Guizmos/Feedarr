"""Generate isolated pre-download selection from the actual provider client source."""
from pathlib import Path
import benchmark as b


def block(source, marker):
    start = source.index(marker); brace = source.index('{', start); depth = 1; end = brace + 1
    # Used only for model/helper blocks without interpolated braces. Fail fast at
    # compilation/source fixtures if upstream signatures change.
    while depth:
        depth += (source[end] == '{') - (source[end] == '}'); end += 1
    return source[start:end]


def generated():
    core = (b.ROOT / 'src/Feedarr.Api/Services/Posters/PosterFetchService.cs').read_text()
    rawg = (b.ROOT / 'src/Feedarr.Api/Services/Rawg/RawgClient.cs').read_text()
    igdb = (b.ROOT / 'src/Feedarr.Api/Services/Igdb/IgdbClient.cs').read_text()
    a = core.index('        if (category != UnifiedCategory.Emission && ambiguity.SignificantTokenCount < 2)', core.index('private async Task<TvMazeAttempt>'))
    z = core.index('        var bytes = await ', a)
    tv = core[a:z]
    line = 'var candidates = await RunTvMazeAsync(innerCt => _tvmaze.SearchShowsAsync(title, innerCt, TmdbCandidateLimit), ct).ConfigureAwait(false);'
    assert line in tv
    tv = tv.replace(line, 'var candidates = suppliedCandidates;')
    tv = tv.replace('return new TvMazeAttempt(null, false);', 'return new("Reject", null, null, new[] { "historical-tvmaze-no-selection" });')
    tv = tv.replace('return new TvMazeAttempt(null, true);', 'return new("Reject", null, null, new[] { "historical-tvmaze-threshold-or-image" });')
    a = rawg.index('        // If year provided', rawg.index('public async Task<(int rawgId'))
    z = rawg.index('\n    }', a); raw = rawg[a:z]
    a = igdb.index('var best = data?.FirstOrDefault', igdb.index('public async Task<(int igdbId'))
    z = igdb.index('\n    }', a); ig = igdb[a:z]
    helpers = rawg[rawg.index('    private static (int rawgId, string title, int? year, string coverUrl) ToTuple'):rawg.rfind('\n}')]
    return '''
internal sealed partial class HistoricalAdapter
{
    private static readonly float TvMazeAmbiguousThreshold = (float)Core.GetField("TvMazeAmbiguousThreshold", Flags)!.GetRawConstantValue()!;
    private static readonly float TvMazeEmissionThreshold = (float)Core.GetField("TvMazeEmissionThreshold", Flags)!.GetRawConstantValue()!;
    internal static HistoricalProviderDecision AssessTvMaze(string title, int? year, UnifiedCategory category,
        TitleAmbiguityResult ambiguity, PosterMatchIds knownIds,
        List<Feedarr.Api.Services.TvMaze.TvMazeClient.ShowResult> suppliedCandidates)
    {
''' + tv + '''
        return new("Accept", new("tvmaze", SearchMediaType.Series, best.Candidate.Id.ToString()), best.Score,
            new[] { "historical-tvmaze-scorer-threshold-before-image-download" });
    }
    internal static (int rawgId, string title, int? year, string coverUrl)? AssessRawg(List<CaptureHit> captured, int? year)
    {
        var results = captured.Select(c => new RawgGameItem { Id = int.Parse(c.Hit.Identity.Id), Name = c.Hit.Title,
            Released = c.Released, BackgroundImage = c.HasPoster ? "observed-image-presence" : null }).ToList();
        if (results.Count == 0) return null;
''' + raw + '''
    }
    internal static (int igdbId, string coverUrl)? AssessIgdb(List<CaptureHit> captured)
    {
        var data = captured.Select(c => new GameItem { Id = int.Parse(c.Hit.Identity.Id), Name = c.Hit.Title,
            FirstReleaseDate = c.FirstReleaseUnix ?? 0, Cover = c.HasPoster ? new CoverItem { Url = "observed-image-presence" } : null }).ToList();
        ''' + ig + '''
    }
''' + helpers + '\n' + block(rawg, '    private sealed class RawgGameItem') + '\n' + block(igdb, '    private sealed class GameItem') + '\n' + block(igdb, '    private sealed class CoverItem') + '\n' + block(igdb, '    private static string NormalizeCoverUrl') + '\n}\n'
