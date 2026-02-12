using Microsoft.AspNetCore.Mvc;
using System.Linq;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Igdb;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.Fanart;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/posters")]
public sealed class PostersController : ControllerBase
{
    private readonly ReleaseRepository _releases;
    private readonly MediaEntityRepository _mediaEntities;
    private readonly ActivityRepository _activity;
    private readonly PosterFetchService _posterFetch;
    private readonly IPosterFetchQueue _queue;
    private readonly PosterFetchJobFactory _jobFactory;
    private readonly RetroFetchLogService _retroLogs;
    private readonly TmdbClient _tmdb;
    private readonly FanartClient _fanart;
    private readonly IgdbClient _igdb;

    public PostersController(
        ReleaseRepository releases,
        MediaEntityRepository mediaEntities,
        ActivityRepository activity,
        PosterFetchService posterFetch,
        IPosterFetchQueue queue,
        PosterFetchJobFactory jobFactory,
        RetroFetchLogService retroLogs,
        TmdbClient tmdb,
        FanartClient fanart,
        IgdbClient igdb)
    {
        _releases = releases;
        _mediaEntities = mediaEntities;
        _activity = activity;
        _posterFetch = posterFetch;
        _queue = queue;
        _jobFactory = jobFactory;
        _retroLogs = retroLogs;
        _tmdb = tmdb;
        _fanart = fanart;
        _igdb = igdb;
    }

    // GET /api/posters/release/{id}
    [HttpGet("release/{id:long}")]
    public IActionResult GetPoster([FromRoute] long id)
    {
        var r = _releases.GetForPoster(id);
        if (r is null) return NotFound();

        var file = (string?)r.PosterFile;
        if (string.IsNullOrWhiteSpace(file)) return NotFound();

        var postersDir = _posterFetch.PostersDirPath;
        var path = Path.Combine(postersDir, file);

        // sécurité anti path traversal
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(postersDir, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "invalid poster path" });

        if (!System.IO.File.Exists(full)) return NotFound();

        var ext = Path.GetExtension(full).ToLowerInvariant();
        var ct = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

        return PhysicalFile(full, ct);
    }

    // GET /api/posters/entity/{entityId}
    [HttpGet("entity/{entityId:long}")]
    public IActionResult GetEntityPoster([FromRoute] long entityId)
    {
        var entity = _mediaEntities.GetPoster(entityId);
        if (entity is null) return NotFound();

        var file = entity.PosterFile;
        if (string.IsNullOrWhiteSpace(file)) return NotFound();

        var postersDir = _posterFetch.PostersDirPath;
        var path = Path.Combine(postersDir, file);

        var full = Path.GetFullPath(path);
        if (!full.StartsWith(postersDir, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "invalid poster path" });

        if (!System.IO.File.Exists(full)) return NotFound();

        var ext = Path.GetExtension(full).ToLowerInvariant();
        var ct = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

        return PhysicalFile(full, ct);
    }

    // GET /api/posters/banner/{id}
    [HttpGet("banner/{id:long}")]
    public async Task<IActionResult> GetBanner([FromRoute] long id, CancellationToken ct)
    {
        var r = _releases.GetForPoster(id);
        if (r is null) return NotFound();

        var postersDir = _posterFetch.PostersDirPath;
        var file = $"banner-{id}.jpg";
        var full = Path.Combine(postersDir, file);

        if (System.IO.File.Exists(full))
            return PhysicalFile(full, "image/jpeg");

        Directory.CreateDirectory(postersDir);

        var tmdbId = (int?)r.TmdbId ?? 0;
        var tvdbId = (int?)r.TvdbId ?? 0;
        var unifiedValue = (string?)r.UnifiedCategory;
        UnifiedCategoryMappings.TryParse(unifiedValue, out var unifiedCategory);
        var mediaType = UnifiedCategoryMappings.ToMediaType(unifiedCategory);
        if (string.IsNullOrWhiteSpace(mediaType) || mediaType == "unknown")
            mediaType = ((string?)r.MediaType ?? "").ToLowerInvariant();
        else
            mediaType = mediaType.ToLowerInvariant();

        byte[]? bytes = null;

        if (tmdbId > 0)
        {
            var path = await _tmdb.GetBackdropPathAsync(tmdbId, mediaType, ct);
            if (!string.IsNullOrWhiteSpace(path))
                bytes = await _tmdb.DownloadBackdropW780Async(path, ct);
        }

        if ((bytes is null || bytes.Length == 0) && mediaType == "series" && tvdbId > 0)
        {
            var url = await _fanart.GetTvBannerUrlAsync(tvdbId, ct);
            if (!string.IsNullOrWhiteSpace(url))
                bytes = await _fanart.DownloadAsync(url, ct);
        }

        if ((bytes is null || bytes.Length == 0) && mediaType != "series" && tmdbId > 0)
        {
            var url = await _fanart.GetMovieBannerUrlAsync(tmdbId, ct);
            if (!string.IsNullOrWhiteSpace(url))
                bytes = await _fanart.DownloadAsync(url, ct);
        }

        if (bytes is null || bytes.Length == 0)
        {
            var posterFile = (string?)r.PosterFile;
            if (!string.IsNullOrWhiteSpace(posterFile))
            {
                var posterFull = Path.Combine(postersDir, posterFile);
                if (System.IO.File.Exists(posterFull))
                    return PhysicalFile(posterFull, "image/jpeg");
            }
            return NotFound();
        }

        await System.IO.File.WriteAllBytesAsync(full, bytes, ct);
        return PhysicalFile(full, "image/jpeg");
    }

    // POST /api/posters/release/{id}/fetch
    [HttpPost("release/{id:long}/fetch")]
    public IActionResult FetchPoster([FromRoute] long id)
    {
        var job = _jobFactory.Create(id, forceRefresh: false);
        if (job is null) return NotFound(new { error = "release not found" });

        if (!_queue.Enqueue(job))
            return StatusCode(503, new { error = "poster queue full" });

        return Accepted(new { ok = true, enqueued = true });
    }

    // POST /api/posters/{itemId}/refresh
    [HttpPost("{itemId:long}/refresh")]
    public IActionResult RefreshPoster([FromRoute] long itemId)
    {
        var job = _jobFactory.Create(itemId, forceRefresh: true);
        if (job is null) return NotFound(new { error = "release not found" });

        if (!_queue.Enqueue(job))
            return StatusCode(503, new { error = "poster queue full" });

        return Accepted(new { ok = true, enqueued = true, forceRefresh = true });
    }

    public sealed class ManualPosterDto
    {
        public string? Provider { get; set; }
        public int? TmdbId { get; set; }
        public string? PosterPath { get; set; }
        public int? IgdbId { get; set; }
    }

    private sealed class PosterSearchResult
    {
        public string? Provider { get; set; }
        public int? TmdbId { get; set; }
        public int? IgdbId { get; set; }
        public string? Title { get; set; }
        public int? Year { get; set; }
        public string? MediaType { get; set; }
        public string? PosterPath { get; set; }
        public string? PosterUrl { get; set; }
        public string? PosterLang { get; set; }
        public string? PosterSize { get; set; }
    }

    // POST /api/posters/release/{id}/manual
    [HttpPost("release/{id:long}/manual")]
    public async Task<IActionResult> SetManualPoster([FromRoute] long id, [FromBody] ManualPosterDto dto, CancellationToken ct)
    {
        object BuildPosterResponse(string? fallbackPosterUrl)
        {
            var info = _releases.GetForPoster(id);
            long? entityId = null;
            string? posterFile = null;
            long? posterUpdatedAtTs = null;

            if (info is not null)
            {
                try { entityId = info.EntityId is null ? null : Convert.ToInt64(info.EntityId); } catch { }
                try { posterFile = info.PosterFile as string; } catch { }
                try { posterUpdatedAtTs = info.PosterUpdatedAtTs is null ? null : Convert.ToInt64(info.PosterUpdatedAtTs); } catch { }
            }

            var resolvedUrl = !string.IsNullOrWhiteSpace(posterFile)
                ? $"/api/posters/release/{id}?v={posterUpdatedAtTs ?? 0}"
                : fallbackPosterUrl;

            return new { ok = true, posterUrl = resolvedUrl, posterFile, posterUpdatedAtTs, entityId };
        }

        var provider = (dto?.Provider ?? "").Trim().ToLowerInvariant();
        if (provider != "tmdb" && provider != "igdb")
            return BadRequest(new { error = "unsupported provider" });

        if (provider == "tmdb")
        {
            if (dto?.TmdbId is null || string.IsNullOrWhiteSpace(dto?.PosterPath))
                return BadRequest(new { error = "tmdbId/posterPath missing" });

            var posterUrl = await _posterFetch.SaveTmdbPosterAsync(id, dto.TmdbId.Value, dto.PosterPath!, ct);
            if (string.IsNullOrWhiteSpace(posterUrl))
                return StatusCode(500, new { error = "poster download failed" });

            return Ok(BuildPosterResponse(posterUrl));
        }

        if (dto?.IgdbId is null || string.IsNullOrWhiteSpace(dto?.PosterPath))
            return BadRequest(new { error = "igdbId/coverUrl missing" });

        var igdbPosterUrl = await _posterFetch.SaveIgdbPosterAsync(id, dto.IgdbId.Value, dto.PosterPath!, ct);
        if (string.IsNullOrWhiteSpace(igdbPosterUrl))
            return StatusCode(500, new { error = "poster download failed" });

        return Ok(BuildPosterResponse(igdbPosterUrl));
    }

    // GET /api/posters/search?q=title&mediaType=movie|series|game
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] string? mediaType, CancellationToken ct)
    {
        var query = (q ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query)) return Ok(new { results = Array.Empty<object>() });

        mediaType = (mediaType ?? "").Trim().ToLowerInvariant();

        var results = new List<PosterSearchResult>();
        if (mediaType == "game")
        {
            var games = await _igdb.SearchGameListAsync(query, null, ct);
            results.AddRange(games.Select(r => new PosterSearchResult
            {
                Provider = "igdb",
                IgdbId = r.igdbId,
                Title = r.title,
                Year = r.year,
                MediaType = "game",
                PosterPath = r.coverUrl,
                PosterUrl = r.coverUrl,
                PosterLang = null,
                PosterSize = InferIgdbSize(r.coverUrl)
            }));
        }
        else if (mediaType == "series")
        {
            var tv = await _tmdb.SearchTvListAsync(query, null, ct);
            results.AddRange(tv.Select(r => new PosterSearchResult
            {
                Provider = "tmdb",
                TmdbId = r.TmdbId,
                Title = r.Title,
                Year = r.Year,
                MediaType = r.MediaType,
                PosterPath = r.PosterPath,
                PosterUrl = string.IsNullOrWhiteSpace(r.PosterPath) ? null : $"https://image.tmdb.org/t/p/w342{r.PosterPath}",
                PosterLang = r.OriginalLanguage,
                PosterSize = string.IsNullOrWhiteSpace(r.PosterPath) ? null : "w342"
            }));
        }
        else if (mediaType == "movie")
        {
            var movies = await _tmdb.SearchMovieListAsync(query, null, ct);
            results.AddRange(movies.Select(r => new PosterSearchResult
            {
                Provider = "tmdb",
                TmdbId = r.TmdbId,
                Title = r.Title,
                Year = r.Year,
                MediaType = r.MediaType,
                PosterPath = r.PosterPath,
                PosterUrl = string.IsNullOrWhiteSpace(r.PosterPath) ? null : $"https://image.tmdb.org/t/p/w342{r.PosterPath}",
                PosterLang = r.OriginalLanguage,
                PosterSize = string.IsNullOrWhiteSpace(r.PosterPath) ? null : "w342"
            }));
        }
        else
        {
            var movies = await _tmdb.SearchMovieListAsync(query, null, ct);
            var tv = await _tmdb.SearchTvListAsync(query, null, ct);
            results.AddRange(movies.Select(r => new PosterSearchResult
            {
                Provider = "tmdb",
                TmdbId = r.TmdbId,
                Title = r.Title,
                Year = r.Year,
                MediaType = r.MediaType,
                PosterPath = r.PosterPath,
                PosterUrl = string.IsNullOrWhiteSpace(r.PosterPath) ? null : $"https://image.tmdb.org/t/p/w342{r.PosterPath}",
                PosterLang = r.OriginalLanguage,
                PosterSize = string.IsNullOrWhiteSpace(r.PosterPath) ? null : "w342"
            }));
            results.AddRange(tv.Select(r => new PosterSearchResult
            {
                Provider = "tmdb",
                TmdbId = r.TmdbId,
                Title = r.Title,
                Year = r.Year,
                MediaType = r.MediaType,
                PosterPath = r.PosterPath,
                PosterUrl = string.IsNullOrWhiteSpace(r.PosterPath) ? null : $"https://image.tmdb.org/t/p/w342{r.PosterPath}",
                PosterLang = r.OriginalLanguage,
                PosterSize = string.IsNullOrWhiteSpace(r.PosterPath) ? null : "w342"
            }));
        }

        var deduped = results
            .GroupBy(BuildSearchDedupKey)
            .Select(group => group
                .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.PosterPath) || !string.IsNullOrWhiteSpace(r.PosterUrl))
                .First())
            .ToList();

        return Ok(new { results = deduped });
    }

    public sealed class BulkDto { public List<long> Ids { get; set; } = new(); }

    public sealed class RetroFetchDto { public int? Limit { get; set; } }
    public sealed class RetroFetchProgressDto
    {
        public List<long> Ids { get; set; } = new();
        public long? StartedAtTs { get; set; }
    }

    [HttpPost("releases/fetch")]
    public IActionResult FetchBulk([FromBody] BulkDto dto)
    {
        if (dto?.Ids is null || dto.Ids.Count == 0)
            return BadRequest(new { error = "ids missing" });

        return EnqueueBulk(dto.Ids, forceRefresh: false);
    }

    // POST /api/posters/refresh-bulk
    [HttpPost("refresh-bulk")]
    public IActionResult RefreshBulk([FromBody] BulkDto dto)
    {
        if (dto?.Ids is null || dto.Ids.Count == 0)
            return BadRequest(new { error = "ids missing" });

        return EnqueueBulk(dto.Ids, forceRefresh: true);
    }

    // POST /api/posters/releases/state
    [HttpPost("releases/state")]
    public IActionResult GetReleasesState([FromBody] BulkDto dto)
    {
        if (dto?.Ids is null || dto.Ids.Count == 0)
            return BadRequest(new { error = "ids missing" });

        var rows = _releases.GetPosterStateByReleaseIds(dto.Ids);
        var items = rows.Select(row => new
        {
            id = row.Id,
            entityId = row.EntityId,
            posterFile = row.PosterFile,
            posterUpdatedAtTs = row.PosterUpdatedAtTs,
            posterLastAttemptTs = row.PosterLastAttemptTs,
            posterLastError = row.PosterLastError,
            posterUrl = !string.IsNullOrWhiteSpace(row.PosterFile)
                ? $"/api/posters/release/{row.Id}?v={row.PosterUpdatedAtTs ?? 0}"
                : null
        });

        return Ok(new { items });
    }

    // POST /api/posters/retro-fetch
    [HttpPost("retro-fetch")]
    public IActionResult RetroFetch([FromBody] RetroFetchDto? dto)
    {
        var limit = Math.Clamp(dto?.Limit ?? 200, 1, 1000);
        var startedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var ids = _releases.GetIdsMissingPoster(limit);
        if (ids.Count == 0)
        {
            _activity.Add(null, "info", "poster_fetch", "Retro fetch: no missing posters");
            return Ok(new { ok = true, total = 0, enqueued = 0, missing = 0, failed = 0, ids = Array.Empty<long>(), startedAtTs });
        }

        string? logFile = null;
        try
        {
            logFile = _retroLogs.CreateRetroFetchLog();
        }
        catch
        {
            logFile = null;
        }

        var (result, enqueued, missing, failed, total) = EnqueueBulkInternal(ids, forceRefresh: false, total: ids.Count, retroLogFile: logFile);
        _activity.Add(null, "info", "poster_fetch", "Retro fetch enqueued",
            dataJson: $"{{\"total\":{total},\"enqueued\":{enqueued},\"missing\":{missing},\"failed\":{failed},\"logFile\":\"{logFile ?? ""}\"}}");
        return Accepted(new { ok = true, total, enqueued, missing, failed, ids, startedAtTs, logFile });
    }

    // POST /api/posters/retro-fetch/progress
    [HttpPost("retro-fetch/progress")]
    public IActionResult RetroFetchProgress([FromBody] RetroFetchProgressDto? dto)
    {
        var ids = dto?.Ids ?? new List<long>();
        var startedAtTs = dto?.StartedAtTs ?? 0;
        var (total, done, pending) = _releases.GetPosterProgressByIds(ids, startedAtTs);
        return Ok(new { total, done, pending });
    }

    // POST /api/posters/retro-fetch/stop
    [HttpPost("retro-fetch/stop")]
    public IActionResult StopRetroFetch()
    {
        var cleared = _queue.ClearPending();
        _activity.Add(null, "info", "poster_fetch", "Retro fetch stopped",
            dataJson: $"{{\"cleared\":{cleared}}}");
        return Ok(new { ok = true, cleared });
    }

    // GET /api/posters/count
    [HttpGet("count")]
    public IActionResult GetCount()
    {
        var count = _posterFetch.GetLocalPosterCount();
        return Ok(new { count });
    }

    // GET /api/posters/missing-count
    [HttpGet("missing-count")]
    public IActionResult GetMissingCount()
    {
        var count = _releases.GetMissingPosterCount();
        return Ok(new { count });
    }

    // GET /api/posters/stats
    [HttpGet("stats")]
    public IActionResult GetPosterStats()
    {
        const long shortCooldownSeconds = 6 * 60 * 60;
        const long longCooldownSeconds = 24 * 60 * 60;

        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var stats = _releases.GetPosterStats(nowTs, shortCooldownSeconds, longCooldownSeconds);
        var lastSyncTs = _activity.GetLatestTsByEventType("sync");
        var fingerprint = $"{stats.MissingActionable}|{stats.LastPosterChangeTs}|{lastSyncTs}";
        var lastChangeTs = Math.Max(stats.LastPosterChangeTs, lastSyncTs);

        return Ok(new
        {
            missingTotal = stats.MissingTotal,
            missingActionable = stats.MissingActionable,
            stateFingerprint = fingerprint,
            lastChangeTs
        });
    }

    // GET /api/posters/queue/status
    [HttpGet("queue/status")]
    public IActionResult GetQueueStatus()
    {
        var queueSize = _queue.Count;
        var isProcessing = queueSize > 0;
        return Ok(new { queueSize, isProcessing });
    }

    // POST /api/posters/cache/clear
    [HttpPost("cache/clear")]
    public IActionResult ClearCache()
    {
        var cleared = _posterFetch.ClearPosterCache();
        _activity.Add(null, "info", "poster_fetch", "Poster cache cleared",
            dataJson: $"{{\"cleared\":{cleared}}}");
        return Ok(new { ok = true, cleared });
    }

    private IActionResult EnqueueBulk(IEnumerable<long> ids, bool forceRefresh, int? total = null)
        => EnqueueBulkInternal(ids, forceRefresh, total).result;

    private (IActionResult result, int enqueued, int missing, int failed, int total) EnqueueBulkInternal(
        IEnumerable<long> ids,
        bool forceRefresh,
        int? total = null,
        string? retroLogFile = null)
    {
        var enqueued = 0;
        var missing = 0;
        var failed = 0;

        foreach (var id in ids.Distinct())
        {
            var job = _jobFactory.Create(id, forceRefresh, retroLogFile);
            if (job is null)
            {
                missing++;
                continue;
            }

            if (_queue.Enqueue(job))
                enqueued++;
            else
                failed++;
        }

        var totalCount = total ?? ids.Count();
        var result = Accepted(new
        {
            ok = true,
            total = totalCount,
            enqueued,
            missing,
            failed,
            forceRefresh
        });

        return (result, enqueued, missing, failed, totalCount);
    }

    // GET /api/posters/retro-fetch/log/{file}
    [HttpGet("retro-fetch/log/{file}")]
    public IActionResult DownloadRetroFetchLog([FromRoute] string file)
    {
        var full = _retroLogs.ResolveLogPath(file);
        if (string.IsNullOrWhiteSpace(full))
            return BadRequest(new { error = "invalid log file" });

        if (!System.IO.File.Exists(full))
            return NotFound();

        return PhysicalFile(full, "text/csv", Path.GetFileName(full));
    }

    private static string? InferIgdbSize(string coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return null;
        var lower = coverUrl.ToLowerInvariant();
        if (lower.Contains("cover_big")) return "cover_big";
        if (lower.Contains("t_cover")) return "cover";
        return null;
    }

    private static string BuildSearchDedupKey(PosterSearchResult result)
    {
        var provider = (result.Provider ?? "").Trim().ToLowerInvariant();
        if (result.TmdbId.HasValue) return $"{provider}:tmdb:{result.TmdbId.Value}";
        if (result.IgdbId.HasValue) return $"{provider}:igdb:{result.IgdbId.Value}";
        var title = (result.Title ?? "").Trim().ToLowerInvariant();
        var year = result.Year?.ToString() ?? "";
        return $"{provider}:{title}:{year}";
    }
}
