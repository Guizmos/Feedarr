using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Metadata;

public sealed record RequestTmdbResolveInput(
    long? ReleaseId,
    string? MediaType,
    int? TmdbId,
    int? TvdbId,
    string? Title,
    int? Year);

public sealed record RequestTmdbResolveResult(
    int? TmdbId,
    int? TvdbId,
    string Status,
    bool ResolvedFromTvdb);

public sealed class RequestTmdbResolverService
{
    private const string StatusCachedRequestTmdb = "cached_request_tmdb";
    private const string StatusFromExistingTmdb = "from_existing_tmdb";
    private const string StatusResolvedTvdbMap = "resolved_tvdb_map";
    private const string StatusResolvedTitleSearch = "resolved_title_search";
    private const string StatusMissingTmdbForMovie = "missing_tmdb_for_movie";
    private const string StatusUnresolvedNoMatch = "unresolved_no_match";

    private readonly ReleaseRepository _releases;
    private readonly TmdbClient _tmdb;
    private readonly ILogger<RequestTmdbResolverService> _logger;

    public RequestTmdbResolverService(
        ReleaseRepository releases,
        TmdbClient tmdb,
        ILogger<RequestTmdbResolverService> logger)
    {
        _releases = releases;
        _tmdb = tmdb;
        _logger = logger;
    }

    public async Task<RequestTmdbResolveResult> ResolveAsync(RequestTmdbResolveInput input, CancellationToken ct)
    {
        var releaseId = input.ReleaseId.HasValue && input.ReleaseId.Value > 0 ? input.ReleaseId.Value : (long?)null;
        var seed = releaseId.HasValue ? _releases.GetRequestTmdbSeed(releaseId.Value) : null;
        var mediaType = NormalizeMediaType(input.MediaType ?? seed?.MediaType);

        var requestTmdbId = NormalizeId(seed?.RequestTmdbId);
        var tmdbId = NormalizeId(input.TmdbId) ?? NormalizeId(seed?.TmdbId);
        var tvdbId = NormalizeId(input.TvdbId) ?? NormalizeId(seed?.TvdbId);
        var title = NormalizeTitle(input.Title ?? seed?.TitleClean);
        var year = NormalizeYear(input.Year ?? seed?.Year);

        if (mediaType == "movie")
        {
            if (tmdbId.HasValue)
            {
                Persist(releaseId, tmdbId.Value, StatusFromExistingTmdb, persistMainTmdb: false);
                return new RequestTmdbResolveResult(tmdbId, tvdbId, StatusFromExistingTmdb, false);
            }

            Persist(releaseId, null, StatusMissingTmdbForMovie, persistMainTmdb: false);
            return new RequestTmdbResolveResult(null, tvdbId, StatusMissingTmdbForMovie, false);
        }

        if (requestTmdbId.HasValue)
        {
            Persist(releaseId, requestTmdbId.Value, StatusCachedRequestTmdb, persistMainTmdb: false);
            return new RequestTmdbResolveResult(requestTmdbId, tvdbId, StatusCachedRequestTmdb, false);
        }

        if (tmdbId.HasValue)
        {
            Persist(releaseId, tmdbId.Value, StatusFromExistingTmdb, persistMainTmdb: false);
            return new RequestTmdbResolveResult(tmdbId, tvdbId, StatusFromExistingTmdb, false);
        }

        if (tvdbId.HasValue)
        {
            var mappedTmdb = await TryResolveFromTvdbAsync(tvdbId.Value, ct);
            if (mappedTmdb.HasValue)
            {
                Persist(releaseId, mappedTmdb.Value, StatusResolvedTvdbMap, persistMainTmdb: true);
                return new RequestTmdbResolveResult(mappedTmdb, tvdbId, StatusResolvedTvdbMap, true);
            }
        }

        var searchedTmdb = await TryResolveFromTitleSearchAsync(title, year, tvdbId, ct);
        if (searchedTmdb.HasValue)
        {
            Persist(releaseId, searchedTmdb.Value, StatusResolvedTitleSearch, persistMainTmdb: true);
            return new RequestTmdbResolveResult(searchedTmdb, tvdbId, StatusResolvedTitleSearch, false);
        }

        Persist(releaseId, null, StatusUnresolvedNoMatch, persistMainTmdb: false);
        return new RequestTmdbResolveResult(null, tvdbId, StatusUnresolvedNoMatch, false);
    }

    private async Task<int?> TryResolveFromTvdbAsync(int tvdbId, CancellationToken ct)
    {
        try
        {
            return await _tmdb.GetTvTmdbIdByTvdbIdAsync(tvdbId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Request TMDB resolve failed tvdb->tmdb for tvdbId={TvdbId}", tvdbId);
            return null;
        }
    }

    private async Task<int?> TryResolveFromTitleSearchAsync(string? title, int? year, int? expectedTvdbId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var candidates = new List<TmdbClient.SearchResult>();
        try
        {
            candidates.AddRange(await _tmdb.SearchTvListAsync(title, year, ct, limit: 12));
            if (year.HasValue)
                candidates.AddRange(await _tmdb.SearchTvListAsync(title, null, ct, limit: 12));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Request TMDB resolve failed title search for title='{Title}' year={Year}", title, year);
            return null;
        }

        var deduped = candidates
            .Where(c => c.TmdbId > 0)
            .GroupBy(c => c.TmdbId)
            .Select(g => g.First())
            .Select(c => new
            {
                Candidate = c,
                Score = MatchScorer.ScoreCandidate(
                    title,
                    year,
                    UnifiedCategory.Serie,
                    c.Title,
                    c.OriginalTitle,
                    c.Year,
                    "series")
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        if (deduped.Count == 0)
            return null;

        var minScore = year.HasValue ? 0.58f : 0.70f;
        var significantTokens = TitleTokenHelper.CountSignificantTokens(title);
        if (significantTokens <= 2)
            minScore = Math.Min(0.9f, minScore + 0.12f);

        foreach (var row in deduped)
        {
            if (row.Score < minScore)
                break;

            if (expectedTvdbId.HasValue && expectedTvdbId.Value > 0)
            {
                var candidateTvdb = await TryResolveTvdbFromTmdbAsync(row.Candidate.TmdbId, ct);
                if (!candidateTvdb.HasValue || candidateTvdb.Value != expectedTvdbId.Value)
                    continue;
            }

            return row.Candidate.TmdbId;
        }

        return null;
    }

    private async Task<int?> TryResolveTvdbFromTmdbAsync(int tmdbId, CancellationToken ct)
    {
        try
        {
            return await _tmdb.GetTvdbIdAsync(tmdbId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Request TMDB resolve failed tmdb->tvdb verify for tmdbId={TmdbId}", tmdbId);
            return null;
        }
    }

    private void Persist(long? releaseId, int? requestTmdbId, string status, bool persistMainTmdb)
    {
        if (!releaseId.HasValue || releaseId.Value <= 0)
            return;

        _releases.SaveRequestTmdbResolution(releaseId.Value, requestTmdbId, status);
        if (persistMainTmdb && requestTmdbId.HasValue && requestTmdbId.Value > 0)
            _releases.SaveTmdbId(releaseId.Value, requestTmdbId.Value);
    }

    private static string NormalizeMediaType(string? mediaType)
    {
        var value = (mediaType ?? string.Empty).Trim().ToLowerInvariant();
        if (value is "movie" or "film")
            return "movie";
        if (value is "series" or "serie" or "tv" or "show" or "")
            return "tv";
        return "tv";
    }

    private static int? NormalizeId(int? value)
        => value.HasValue && value.Value > 0 ? value.Value : (int?)null;

    private static int? NormalizeYear(int? value)
        => value.HasValue && value.Value is >= 1800 and <= 2100 ? value.Value : (int?)null;

    private static string? NormalizeTitle(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
