using System.Globalization;
using System.Text.RegularExpressions;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Arr;

public sealed class MediaEntityArrStatusService
{
    private const long DefaultTtlSeconds = 6 * 60 * 60;

    private readonly MediaEntityArrStatusRepository _statusRepo;
    private readonly MediaEntityRepository _entities;
    private readonly ReleaseRepository _releases;
    private readonly ArrLibraryRepository _library;
    private readonly SonarrClient _sonarr;
    private readonly RadarrClient _radarr;
    private readonly ILogger<MediaEntityArrStatusService> _logger;

    public MediaEntityArrStatusService(
        MediaEntityArrStatusRepository statusRepo,
        MediaEntityRepository entities,
        ReleaseRepository releases,
        ArrLibraryRepository library,
        SonarrClient sonarr,
        RadarrClient radarr,
        ILogger<MediaEntityArrStatusService> logger)
    {
        _statusRepo = statusRepo;
        _entities = entities;
        _releases = releases;
        _library = library;
        _sonarr = sonarr;
        _radarr = radarr;
        _logger = logger;
    }

    public Dictionary<long, EntityArrStatusResult> GetArrStatusForReleaseIds(IEnumerable<long> releaseIds)
    {
        if (releaseIds is null) return new Dictionary<long, EntityArrStatusResult>();
        var releaseList = releaseIds.Distinct().ToArray();
        if (releaseList.Length == 0) return new Dictionary<long, EntityArrStatusResult>();

        var releaseToEntity = _releases.GetEntityIdsForReleaseIds(releaseList);
        var entityIds = releaseToEntity.Values
            .Where(id => id.HasValue && id.Value > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        if (entityIds.Length == 0) return new Dictionary<long, EntityArrStatusResult>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var minCheckedAt = now - DefaultTtlSeconds;

        var existing = _statusRepo.GetByEntityIds(entityIds);
        var statusByEntity = existing.ToDictionary(s => s.EntityId, s => s);
        var staleIds = entityIds
            .Where(id => !statusByEntity.TryGetValue(id, out var row) || row.CheckedAtTs < minCheckedAt)
            .ToArray();

        if (staleIds.Length > 0)
        {
            var refreshed = RefreshEntityStatusesInternal(staleIds, now);
            foreach (var row in refreshed)
            {
                statusByEntity[row.EntityId] = row;
            }
        }

        var entityInfos = _entities.GetByIds(entityIds).ToDictionary(e => e.Id, e => e);
        var result = new Dictionary<long, EntityArrStatusResult>();
        foreach (var releaseId in releaseList)
        {
            if (!releaseToEntity.TryGetValue(releaseId, out var entityId) || entityId is null || entityId <= 0)
                continue;

            if (!statusByEntity.TryGetValue(entityId.Value, out var statusRow))
                continue;

            entityInfos.TryGetValue(entityId.Value, out var entity);
            result[releaseId] = new EntityArrStatusResult
            {
                EntityId = entityId.Value,
                InSonarr = statusRow.InSonarr,
                InRadarr = statusRow.InRadarr,
                SonarrUrl = statusRow.SonarrUrl,
                RadarrUrl = statusRow.RadarrUrl,
                SonarrItemId = statusRow.SonarrItemId,
                RadarrItemId = statusRow.RadarrItemId,
                TmdbId = entity?.TmdbId,
                TvdbId = entity?.TvdbId
            };
        }

        return result;
    }

    public int RefreshStaleStatuses(int limit)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var minCheckedAt = now - DefaultTtlSeconds;
        var entityIds = _statusRepo.GetEntityIdsNeedingRefresh(minCheckedAt, limit);
        if (entityIds.Count == 0) return 0;

        var refreshed = RefreshEntityStatusesInternal(entityIds, now);
        return refreshed.Count;
    }

    private List<MediaEntityArrStatusRow> RefreshEntityStatusesInternal(IEnumerable<long> entityIds, long now)
    {
        var list = entityIds.Distinct().ToArray();
        if (list.Length == 0) return new List<MediaEntityArrStatusRow>();

        var entities = _entities.GetByIds(list);
        if (entities.Count == 0) return new List<MediaEntityArrStatusRow>();

        var rows = ComputeStatuses(entities, now);
        if (rows.Count > 0)
        {
            _statusRepo.UpsertMany(rows);
        }

        return rows;
    }

    private List<MediaEntityArrStatusRow> ComputeStatuses(List<MediaEntityInfo> entities, long now)
    {
        var works = entities
            .Select(e => new EntityWork
            {
                Entity = e,
                MediaType = ResolveMediaType(e.UnifiedCategory),
                NormalizedTitle = TitleNormalizer.NormalizeTitleStrict(e.TitleClean)
            })
            .ToList();

        var tmdbIds = works
            .Where(w => w.Entity.TmdbId.HasValue && w.Entity.TmdbId.Value > 0 && w.AllowMovieMatch)
            .Select(w => w.Entity.TmdbId!.Value)
            .Distinct()
            .ToArray();

        var tvdbIds = works
            .Where(w => w.Entity.TvdbId.HasValue && w.Entity.TvdbId.Value > 0 && w.AllowSeriesMatch)
            .Select(w => w.Entity.TvdbId!.Value)
            .Distinct()
            .ToArray();

        var movieMatches = _library.FindMoviesByTmdbIds(tmdbIds);
        var seriesMatches = _library.FindSeriesByTvdbIds(tvdbIds);
        var movieByTmdb = movieMatches
            .Where(m => m.TmdbId.HasValue)
            .GroupBy(m => m.TmdbId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var seriesByTvdb = seriesMatches
            .Where(s => s.TvdbId.HasValue)
            .GroupBy(s => s.TvdbId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var titleMovieCandidates = _library.FindMoviesByNormalizedTitles(
            works.Where(w => w.NeedsTitleFallback && w.AllowMovieMatch)
                 .Select(w => w.NormalizedTitle)
                 .Where(t => !string.IsNullOrWhiteSpace(t))
                 .Select(t => t!)
        );
        var titleSeriesCandidates = _library.FindSeriesByNormalizedTitles(
            works.Where(w => w.NeedsTitleFallback && w.AllowSeriesMatch)
                 .Select(w => w.NormalizedTitle)
                 .Where(t => !string.IsNullOrWhiteSpace(t))
                 .Select(t => t!)
        );

        var movieByTitle = titleMovieCandidates
            .Where(c => !string.IsNullOrWhiteSpace(c.TitleNormalized))
            .GroupBy(c => c.TitleNormalized!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var seriesByTitle = titleSeriesCandidates
            .Where(c => !string.IsNullOrWhiteSpace(c.TitleNormalized))
            .GroupBy(c => c.TitleNormalized!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<MediaEntityArrStatusRow>(works.Count);
        foreach (var work in works)
        {
            var entity = work.Entity;
            var row = new MediaEntityArrStatusRow
            {
                EntityId = entity.Id,
                InSonarr = false,
                InRadarr = false,
                SonarrUrl = null,
                RadarrUrl = null,
                CheckedAtTs = now,
                MatchMethod = "none",
                SonarrItemId = null,
                RadarrItemId = null
            };

            var matchedById = false;
            if (entity.TvdbId.HasValue && entity.TvdbId.Value > 0 && work.AllowSeriesMatch)
            {
                if (seriesByTvdb.TryGetValue(entity.TvdbId.Value, out var series))
                {
                    row.InSonarr = true;
                    row.SonarrItemId = series.InternalId;
                    row.SonarrUrl = string.IsNullOrWhiteSpace(series.TitleSlug)
                        ? null
                        : _sonarr.BuildOpenUrl(series.BaseUrl, series.TitleSlug);
                    matchedById = true;
                }
            }

            if (entity.TmdbId.HasValue && entity.TmdbId.Value > 0 && work.AllowMovieMatch)
            {
                if (movieByTmdb.TryGetValue(entity.TmdbId.Value, out var movie))
                {
                    row.InRadarr = true;
                    row.RadarrItemId = movie.InternalId;
                    row.RadarrUrl = movie.TmdbId.HasValue
                        ? _radarr.BuildOpenUrl(movie.BaseUrl, movie.TmdbId.Value)
                        : null;
                    matchedById = true;
                }
            }

            if (!matchedById && work.NeedsTitleFallback && !string.IsNullOrWhiteSpace(work.NormalizedTitle))
            {
                if (work.AllowSeriesMatch && seriesByTitle.TryGetValue(work.NormalizedTitle, out var seriesCandidates))
                {
                    var picked = PickBestTitleMatch(seriesCandidates, entity.Year, entity.Id);
                    if (picked is not null)
                    {
                        row.InSonarr = true;
                        row.SonarrItemId = picked.InternalId;
                        row.SonarrUrl = string.IsNullOrWhiteSpace(picked.TitleSlug)
                            ? null
                            : _sonarr.BuildOpenUrl(picked.BaseUrl, picked.TitleSlug);
                    }
                }

                if (work.AllowMovieMatch && movieByTitle.TryGetValue(work.NormalizedTitle, out var movieCandidates))
                {
                    var picked = PickBestTitleMatch(movieCandidates, entity.Year, entity.Id);
                    if (picked is not null)
                    {
                        row.InRadarr = true;
                        row.RadarrItemId = picked.InternalId;
                        row.RadarrUrl = picked.TmdbId.HasValue
                            ? _radarr.BuildOpenUrl(picked.BaseUrl, picked.TmdbId.Value)
                            : null;
                    }
                }
            }

            if (row.InSonarr || row.InRadarr)
            {
                row.MatchMethod = matchedById ? "id" : "title";
            }

            rows.Add(row);
        }

        var matched = rows.Count(r => r.InSonarr || r.InRadarr);
        _logger.LogInformation("Arr entity status refreshed: {Matched}/{Total}", matched, rows.Count);

        return rows;
    }

    private LibraryTitleMatchResult? PickBestTitleMatch(List<LibraryTitleMatchResult> candidates, int? entityYear, long entityId)
    {
        if (candidates.Count == 0) return null;

        var filtered = candidates
            .Where(c => YearMatches(entityYear, c.Title, c.OriginalTitle))
            .ToList();

        if (filtered.Count == 0) return null;

        if (filtered.Count > 1)
        {
            _logger.LogWarning("Ambiguous Arr title match for entity {EntityId}: {Count} candidates", entityId, filtered.Count);
        }

        return filtered[0];
    }

    private static bool YearMatches(int? entityYear, string? title, string? originalTitle)
    {
        if (!entityYear.HasValue) return true;
        var year = TryExtractYear(title) ?? TryExtractYear(originalTitle);
        if (!year.HasValue) return true;
        return Math.Abs(year.Value - entityYear.Value) <= 1;
    }

    private static int? TryExtractYear(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var match = Regex.Match(title, @"\b(19|20)\d{2}\b");
        if (!match.Success) return null;
        return int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    private static string? ResolveMediaType(string? unifiedCategory)
    {
        if (UnifiedCategoryMappings.TryParse(unifiedCategory, out var category) &&
            category != Feedarr.Api.Models.UnifiedCategory.Autre)
        {
            return UnifiedCategoryMappings.ToMediaType(category);
        }

        return null;
    }

    private sealed class EntityWork
    {
        public MediaEntityInfo Entity { get; set; } = null!;
        public string? MediaType { get; set; }
        public string? NormalizedTitle { get; set; }

        public bool AllowSeriesMatch =>
            string.IsNullOrWhiteSpace(MediaType) || MediaType.Equals("series", StringComparison.OrdinalIgnoreCase);

        public bool AllowMovieMatch =>
            string.IsNullOrWhiteSpace(MediaType) || MediaType.Equals("movie", StringComparison.OrdinalIgnoreCase);

        public bool NeedsTitleFallback =>
            (!Entity.TmdbId.HasValue || Entity.TmdbId.Value <= 0) &&
            (!Entity.TvdbId.HasValue || Entity.TvdbId.Value <= 0);
    }
}

public sealed class EntityArrStatusResult
{
    public long EntityId { get; set; }
    public bool InSonarr { get; set; }
    public bool InRadarr { get; set; }
    public string? SonarrUrl { get; set; }
    public string? RadarrUrl { get; set; }
    public int? SonarrItemId { get; set; }
    public int? RadarrItemId { get; set; }
    public int? TvdbId { get; set; }
    public int? TmdbId { get; set; }
}
