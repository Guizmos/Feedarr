using Microsoft.AspNetCore.Mvc;
using System.Linq;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Igdb;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.Fanart;
using Feedarr.Api.Services.TheAudioDb;
using Feedarr.Api.Services.GoogleBooks;
using Feedarr.Api.Services.ComicVine;
using Feedarr.Api.Services.MusicBrainz;
using Feedarr.Api.Services.OpenLibrary;
using Feedarr.Api.Services.Rawg;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.ExternalProviders;
using Microsoft.Extensions.Caching.Memory;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/posters")]
public sealed class PostersController : ControllerBase
{
    private const int MaxBulkIds = 1000;
    private static readonly TimeSpan PosterStatsCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly MemoryCache PosterStatsCache = new(new MemoryCacheOptions());

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
    private readonly TheAudioDbClient _theAudioDb;
    private readonly GoogleBooksClient _googleBooks;
    private readonly OpenLibraryClient _openLibrary;
    private readonly RawgClient _rawg;
    private readonly ComicVineClient _comicVine;
    private readonly MusicBrainzClient _musicBrainz;
    private readonly IPosterThumbQueue _thumbQueue;
    private readonly IExternalProviderLimiter _externalProviderLimiter;
    private readonly ILogger<PostersController> _log;

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
        IgdbClient igdb,
        TheAudioDbClient theAudioDb,
        GoogleBooksClient googleBooks,
        OpenLibraryClient openLibrary,
        RawgClient rawg,
        ComicVineClient comicVine,
        MusicBrainzClient musicBrainz,
        ILogger<PostersController> log,
        PosterThumbService? thumbService = null,
        IPosterThumbQueue? thumbQueue = null,
        IExternalProviderLimiter? externalProviderLimiter = null)
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
        _theAudioDb = theAudioDb;
        _googleBooks = googleBooks;
        _openLibrary = openLibrary;
        _rawg = rawg;
        _comicVine = comicVine;
        _musicBrainz = musicBrainz;
        _thumbQueue = thumbQueue ?? NoOpPosterThumbQueue.Instance;
        _externalProviderLimiter = externalProviderLimiter ?? NoOpExternalProviderLimiter.Instance;
        _log = log;
    }

    // GET /api/posters/release/{id}
    [HttpGet("release/{id:long}")]
    public IActionResult GetPoster([FromRoute] long id)
    {
        var r = _releases.GetForPoster(id);
        if (r is null) return NotFound();

        var file = (string?)r.PosterFile;
        if (string.IsNullOrWhiteSpace(file)) return NotFound();
        return BuildPosterFileResult(file, $"release:{id}");
    }

    // GET /api/posters/entity/{entityId}
    [HttpGet("entity/{entityId:long}")]
    public IActionResult GetEntityPoster([FromRoute] long entityId)
    {
        var entity = _mediaEntities.GetPoster(entityId);
        if (entity is null) return NotFound();

        var file = entity.PosterFile;
        if (string.IsNullOrWhiteSpace(file)) return NotFound();
        return BuildPosterFileResult(file, $"entity:{entityId}");
    }

    // GET /api/posters/release/{id}/thumb/{w}
    [HttpGet("release/{id:long}/thumb/{w:int}")]
    public async Task<IActionResult> GetReleasePosterThumb([FromRoute] long id, [FromRoute] int w, CancellationToken ct)
    {
        var r = _releases.GetForPoster(id);
        if (r is null) return NotFound();
        return await BuildThumbResultAsync(
            storeDir: r.PosterStoreDir,
            posterFile: r.PosterFile,
            w: w,
            logContext: $"release-thumb:{id}",
            ct: ct);
    }

    // GET /api/posters/entity/{entityId}/thumb/{w}
    [HttpGet("entity/{entityId:long}/thumb/{w:int}")]
    public async Task<IActionResult> GetEntityPosterThumb([FromRoute] long entityId, [FromRoute] int w, CancellationToken ct)
    {
        var entity = _mediaEntities.GetPoster(entityId);
        if (entity is null) return NotFound();
        return await BuildThumbResultAsync(
            storeDir: entity.PosterStoreDir,
            posterFile: entity.PosterFile,
            w: w,
            logContext: $"entity-thumb:{entityId}",
            ct: ct);
    }

    // GET /api/posters/banner/{id}
    [HttpGet("banner/{id:long}")]
    public async Task<IActionResult> GetBanner([FromRoute] long id, CancellationToken ct)
    {
        var r = _releases.GetForPoster(id);
        if (r is null) return NotFound();

        var postersDir = _posterFetch.PostersDirPath;
        var pathResolver = CreatePosterPathResolver();
        var file = $"banner-{id}.jpg";
        if (!pathResolver.TryResolvePosterFile(file, out var full))
            return NotFound();

        if (System.IO.File.Exists(full))
            return BuildPosterFileResult(file, $"banner-cache:{id}", "public, max-age=3600");

        try
        {
            Directory.CreateDirectory(postersDir);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogWarning(ex, "Banner cache directory access denied for release {ReleaseId} at {Path}", id, postersDir);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "poster file unavailable" });
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Banner cache directory creation failed for release {ReleaseId} at {Path}", id, postersDir);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "poster file unavailable" });
        }

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
                var fallbackResult = BuildPosterFileResult(posterFile, $"banner-fallback:{id}", "public, max-age=3600");
                if (fallbackResult is PhysicalFileResult or ObjectResult)
                    return fallbackResult;
            }
            return NotFound();
        }

        try
        {
            await System.IO.File.WriteAllBytesAsync(full, bytes, ct);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogWarning(ex, "Banner cache write denied for release {ReleaseId} at {Path}", id, full);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "poster file unavailable" });
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Banner cache write failed for release {ReleaseId} at {Path}", id, full);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "poster file unavailable" });
        }

        Response.Headers.CacheControl = "public, max-age=3600";
        Response.Headers.Vary = "Accept-Encoding";
        return PhysicalFile(full, "image/jpeg");
    }

    // POST /api/posters/release/{id}/fetch
    [HttpPost("release/{id:long}/fetch")]
    public async Task<IActionResult> FetchPoster([FromRoute] long id, CancellationToken ct)
    {
        var job = _jobFactory.Create(id, forceRefresh: false);
        if (job is null) return NotFound(new { error = "release not found" });

        var enqueue = await _queue.EnqueueAsync(job, ct, PosterFetchQueue.DefaultEnqueueTimeout).ConfigureAwait(false);
        if (enqueue.IsTimedOut)
            return StatusCode(503, new { error = "poster queue saturated", timedOut = true });
        if (enqueue.IsRejected)
            return BadRequest(new { error = "invalid poster job" });

        return Accepted(new { ok = true, enqueued = enqueue.IsEnqueued, coalesced = enqueue.IsCoalesced });
    }

    // POST /api/posters/{itemId}/refresh
    [HttpPost("{itemId:long}/refresh")]
    public async Task<IActionResult> RefreshPoster([FromRoute] long itemId, CancellationToken ct)
    {
        var job = _jobFactory.Create(itemId, forceRefresh: true);
        if (job is null) return NotFound(new { error = "release not found" });

        var enqueue = await _queue.EnqueueAsync(job, ct, PosterFetchQueue.DefaultEnqueueTimeout).ConfigureAwait(false);
        if (enqueue.IsTimedOut)
            return StatusCode(503, new { error = "poster queue saturated", timedOut = true });
        if (enqueue.IsRejected)
            return BadRequest(new { error = "invalid poster job" });

        return Accepted(new
        {
            ok = true,
            enqueued = enqueue.IsEnqueued,
            coalesced = enqueue.IsCoalesced,
            forceRefresh = true
        });
    }

    public sealed class ManualPosterDto
    {
        public string? Provider { get; set; }
        public int? TmdbId { get; set; }
        public string? PosterPath { get; set; }
        public int? IgdbId { get; set; }
        public string? ProviderId { get; set; }
    }

    private sealed class PosterSearchResult
    {
        public string? Provider { get; set; }
        public string? ProviderId { get; set; }
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
                try { entityId = info.EntityId is null ? null : Convert.ToInt64(info.EntityId); }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to parse EntityId for release {Id}", id); }
                posterFile = info.PosterFile as string;
                try { posterUpdatedAtTs = info.PosterUpdatedAtTs is null ? null : Convert.ToInt64(info.PosterUpdatedAtTs); }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to parse PosterUpdatedAtTs for release {Id}", id); }
            }

            var resolvedUrl = !string.IsNullOrWhiteSpace(posterFile)
                ? $"/api/posters/release/{id}?v={posterUpdatedAtTs ?? 0}"
                : fallbackPosterUrl;

            return new { ok = true, posterUrl = resolvedUrl, posterFile, posterUpdatedAtTs, entityId };
        }

        var provider = (dto?.Provider ?? "").Trim().ToLowerInvariant();
        if (provider != "tmdb" && provider != "igdb" && provider != ExternalProviderKeys.TheAudioDb
            && provider != ExternalProviderKeys.GoogleBooks && provider != ExternalProviderKeys.OpenLibrary
            && provider != ExternalProviderKeys.ComicVine && provider != ExternalProviderKeys.MusicBrainz
            && provider != ExternalProviderKeys.Rawg)
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

        if (provider == "igdb")
        {
            if (dto?.IgdbId is null || string.IsNullOrWhiteSpace(dto?.PosterPath))
                return BadRequest(new { error = "igdbId/coverUrl missing" });

            var igdbPosterUrl = await _posterFetch.SaveIgdbPosterAsync(id, dto.IgdbId.Value, dto.PosterPath!, ct);
            if (string.IsNullOrWhiteSpace(igdbPosterUrl))
                return StatusCode(500, new { error = "poster download failed" });

            return Ok(BuildPosterResponse(igdbPosterUrl));
        }

        if (provider == ExternalProviderKeys.Rawg)
        {
            if (string.IsNullOrWhiteSpace(dto?.ProviderId) || string.IsNullOrWhiteSpace(dto?.PosterPath))
                return BadRequest(new { error = "providerId/posterPath missing" });

            if (!int.TryParse(dto.ProviderId, out var rawgId) || rawgId <= 0)
                return BadRequest(new { error = "invalid rawgId" });

            var rawgPosterUrl = await _posterFetch.SaveRawgPosterAsync(id, rawgId, dto.PosterPath!, ct);
            if (string.IsNullOrWhiteSpace(rawgPosterUrl))
                return StatusCode(500, new { error = "poster download failed" });

            return Ok(BuildPosterResponse(rawgPosterUrl));
        }

        if (provider == ExternalProviderKeys.GoogleBooks)
        {
            if (string.IsNullOrWhiteSpace(dto?.PosterPath))
                return BadRequest(new { error = "posterUrl missing" });

            var bookPosterUrl = await _posterFetch.SaveGoogleBooksPosterAsync(id, dto.ProviderId, dto.PosterPath!, ct);
            if (string.IsNullOrWhiteSpace(bookPosterUrl))
                return StatusCode(500, new { error = "poster download failed" });

            return Ok(BuildPosterResponse(bookPosterUrl));
        }

        if (provider == ExternalProviderKeys.OpenLibrary)
        {
            if (string.IsNullOrWhiteSpace(dto?.PosterPath))
                return BadRequest(new { error = "posterUrl missing" });

            var olPosterUrl = await _posterFetch.SaveOpenLibraryPosterAsync(id, dto.ProviderId, dto.PosterPath!, ct);
            if (string.IsNullOrWhiteSpace(olPosterUrl))
                return StatusCode(500, new { error = "poster download failed" });

            return Ok(BuildPosterResponse(olPosterUrl));
        }

        if (provider == ExternalProviderKeys.ComicVine)
        {
            if (string.IsNullOrWhiteSpace(dto?.PosterPath))
                return BadRequest(new { error = "posterUrl missing" });

            var comicPosterUrl = await _posterFetch.SaveComicVinePosterAsync(id, dto.ProviderId, dto.PosterPath!, ct);
            if (string.IsNullOrWhiteSpace(comicPosterUrl))
                return StatusCode(500, new { error = "poster download failed" });

            return Ok(BuildPosterResponse(comicPosterUrl));
        }

        if (provider == ExternalProviderKeys.MusicBrainz)
        {
            if (string.IsNullOrWhiteSpace(dto?.PosterPath))
                return BadRequest(new { error = "posterUrl missing" });

            var mbPosterUrl = await _posterFetch.SaveMusicBrainzPosterAsync(id, dto.ProviderId, dto.PosterPath!, ct);
            if (string.IsNullOrWhiteSpace(mbPosterUrl))
                return StatusCode(500, new { error = "poster download failed" });

            return Ok(BuildPosterResponse(mbPosterUrl));
        }

        if (string.IsNullOrWhiteSpace(dto?.PosterPath))
            return BadRequest(new { error = "posterUrl missing" });

        var audioPosterUrl = await _posterFetch.SaveTheAudioDbPosterAsync(id, dto.ProviderId, dto.PosterPath!, ct);
        if (string.IsNullOrWhiteSpace(audioPosterUrl))
            return StatusCode(500, new { error = "poster download failed" });

        return Ok(BuildPosterResponse(audioPosterUrl));
    }

    // GET /api/posters/search?q=title&mediaType=movie|series|game|audio
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] string? mediaType, CancellationToken ct)
    {
        var query = (q ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query)) return Ok(new { results = Array.Empty<object>() });

        mediaType = (mediaType ?? "").Trim().ToLowerInvariant();

        var results = new List<PosterSearchResult>();
        if (mediaType == "game")
        {
            var igdbTask = RunProviderAsync(ProviderKind.Igdb, innerCt => _igdb.SearchGameListAsync(query, null, innerCt), ct);
            var rawgTask = RunProviderAsync(ProviderKind.Others, innerCt => _rawg.SearchGameListAsync(query, innerCt), ct);
            await Task.WhenAll(igdbTask, rawgTask);

            results.AddRange(igdbTask.Result.Select(r => new PosterSearchResult
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

            results.AddRange(rawgTask.Result.Select(r => new PosterSearchResult
            {
                Provider = ExternalProviderKeys.Rawg,
                ProviderId = r.rawgId.ToString(),
                Title = r.title,
                Year = r.year,
                MediaType = "game",
                PosterPath = r.coverUrl,
                PosterUrl = r.coverUrl,
                PosterLang = null,
                PosterSize = "cover"
            }));
        }
        else if (mediaType == "audio")
        {
            var (artist, title) = ParseAudioSearchQuery(query);

            // Run TheAudioDB and MusicBrainz searches in parallel
            var audioDbTask = RunProviderAsync(ProviderKind.Others, innerCt => _theAudioDb.SearchAudioListAsync(title, artist, null, innerCt), ct);
            var mbTask = RunProviderAsync(ProviderKind.Others, innerCt => _musicBrainz.SearchReleaseListAsync(title, artist, innerCt), ct);
            await Task.WhenAll(audioDbTask, mbTask);

            results.AddRange(audioDbTask.Result.Select(audio =>
            {
                var displayTitle = !string.IsNullOrWhiteSpace(audio.Artist)
                    ? $"{audio.Artist} – {audio.Title}"
                    : audio.Title;
                return new PosterSearchResult
                {
                    Provider = ExternalProviderKeys.TheAudioDb,
                    ProviderId = audio.ProviderId,
                    Title = displayTitle,
                    Year = int.TryParse(audio.Released, out var parsedYear) ? parsedYear : null,
                    MediaType = "audio",
                    PosterPath = audio.PosterUrl,
                    PosterUrl = audio.PosterUrl
                };
            }));

            results.AddRange(mbTask.Result.Select(mb =>
            {
                var displayTitle = !string.IsNullOrWhiteSpace(mb.Artist)
                    ? $"{mb.Artist} – {mb.Title}"
                    : mb.Title;
                return new PosterSearchResult
                {
                    Provider = ExternalProviderKeys.MusicBrainz,
                    ProviderId = mb.Mbid,
                    Title = displayTitle,
                    Year = mb.Released?.Length >= 4
                        && int.TryParse(mb.Released[..4], out var mbYear) ? mbYear : null,
                    MediaType = "audio",
                    PosterPath = mb.CoverUrl,
                    PosterUrl = mb.CoverUrl
                };
            }));
        }
        else if (mediaType == "book")
        {
            var gbTask = RunProviderAsync(ProviderKind.Others, innerCt => _googleBooks.SearchBookAsync(query, null, innerCt), ct);
            var olTask = RunProviderAsync(ProviderKind.Others, innerCt => _openLibrary.SearchBookListAsync(query, innerCt), ct);
            await Task.WhenAll(gbTask, olTask);
            var gbBook = await gbTask;
            var olBooks = await olTask;

            if (gbBook is not null)
            {
                results.Add(new PosterSearchResult
                {
                    Provider = ExternalProviderKeys.GoogleBooks,
                    ProviderId = gbBook.VolumeId,
                    Title = gbBook.Title,
                    Year = gbBook.PublishedDate?.Length >= 4
                        && int.TryParse(gbBook.PublishedDate[..4], out var bookYear) ? bookYear : null,
                    MediaType = "book",
                    PosterPath = gbBook.ThumbnailUrl,
                    PosterUrl = gbBook.ThumbnailUrl
                });
            }

            foreach (var olBook in olBooks)
            {
                results.Add(new PosterSearchResult
                {
                    Provider = ExternalProviderKeys.OpenLibrary,
                    ProviderId = olBook.WorkId,
                    Title = olBook.Title,
                    Year = olBook.PublishedYear is not null
                        && int.TryParse(olBook.PublishedYear, out var olYear) ? olYear : null,
                    MediaType = "book",
                    PosterPath = olBook.CoverUrl,
                    PosterUrl = olBook.CoverUrl
                });
            }
        }
        else if (mediaType == "comic")
        {
            var comic = await RunProviderAsync(ProviderKind.Others, innerCt => _comicVine.SearchComicAsync(query, null, innerCt), ct);
            if (comic is not null)
            {
                results.Add(new PosterSearchResult
                {
                    Provider = ExternalProviderKeys.ComicVine,
                    ProviderId = comic.ProviderId,
                    Title = comic.Title,
                    Year = comic.ReleaseDate?.Length >= 4
                        && int.TryParse(comic.ReleaseDate[..4], out var comicYear) ? comicYear : null,
                    MediaType = "comic",
                    PosterPath = comic.CoverUrl,
                    PosterUrl = comic.CoverUrl
                });
            }
        }
        else if (mediaType == "series")
        {
            var tv = await RunProviderAsync(ProviderKind.Tmdb, innerCt => _tmdb.SearchTvListAsync(query, null, innerCt), ct);
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
            var movies = await RunProviderAsync(ProviderKind.Tmdb, innerCt => _tmdb.SearchMovieListAsync(query, null, innerCt), ct);
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
            var moviesTask = RunProviderAsync(ProviderKind.Tmdb, innerCt => _tmdb.SearchMovieListAsync(query, null, innerCt), ct);
            var tvTask = RunProviderAsync(ProviderKind.Tmdb, innerCt => _tmdb.SearchTvListAsync(query, null, innerCt), ct);
            await Task.WhenAll(moviesTask, tvTask);
            var movies = moviesTask.Result;
            var tv = tvTask.Result;
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
    public Task<IActionResult> FetchBulk([FromBody] BulkDto dto, CancellationToken ct)
    {
        var ids = dto?.Ids;
        var validation = ValidateBulkPayloadIds(ids);
        if (validation is not null)
            return Task.FromResult(validation);

        return EnqueueBulk(ids!, forceRefresh: false, ct);
    }

    // POST /api/posters/refresh-bulk
    [HttpPost("refresh-bulk")]
    public Task<IActionResult> RefreshBulk([FromBody] BulkDto dto, CancellationToken ct)
    {
        var ids = dto?.Ids;
        var validation = ValidateBulkPayloadIds(ids);
        if (validation is not null)
            return Task.FromResult(validation);

        return EnqueueBulk(ids!, forceRefresh: true, ct);
    }

    // POST /api/posters/releases/state
    [HttpPost("releases/state")]
    public IActionResult GetReleasesState([FromBody] BulkDto dto)
    {
        var ids = dto?.Ids;
        var validation = ValidateBulkPayloadIds(ids);
        if (validation is not null)
            return validation;

        var rows = _releases.GetPosterStateByReleaseIds(ids!);
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
    public async Task<IActionResult> RetroFetch([FromBody] RetroFetchDto? dto, CancellationToken ct)
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

        var (result, enqueued, coalesced, missing, timedOut, total) = await EnqueueBulkInternal(
            ids,
            forceRefresh: false,
            ct,
            total: ids.Count,
            retroLogFile: logFile).ConfigureAwait(false);
        _activity.Add(null, "info", "poster_fetch", "Retro fetch enqueued",
            dataJson: $"{{\"total\":{total},\"enqueued\":{enqueued},\"coalesced\":{coalesced},\"missing\":{missing},\"timedOut\":{timedOut},\"failed\":{timedOut},\"logFile\":\"{logFile ?? ""}\"}}");
        return Accepted(new { ok = true, total, enqueued, coalesced, missing, timedOut, failed = timedOut, ids, startedAtTs, logFile });
    }

    // POST /api/posters/retro-fetch/progress
    [HttpPost("retro-fetch/progress")]
    public IActionResult RetroFetchProgress([FromBody] RetroFetchProgressDto? dto)
    {
        var ids = dto?.Ids ?? new List<long>();
        if (ids.Count > MaxBulkIds)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "too many ids", maxIds = MaxBulkIds });

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
        const string cacheKey = "posters:stats:v2";
        if (PosterStatsCache.TryGetValue<object>(cacheKey, out var cached) && cached is not null)
            return Ok(cached);

        const long shortCooldownSeconds = 6 * 60 * 60;
        const long longCooldownSeconds = 24 * 60 * 60;

        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var stats = _releases.GetPosterStats(nowTs, shortCooldownSeconds, longCooldownSeconds);
        var lastSyncTs = _activity.GetLatestTsByEventType("sync");
        var fingerprint = $"{stats.MissingActionable}|{stats.LastPosterChangeTs}|{lastSyncTs}";
        var lastChangeTs = Math.Max(stats.LastPosterChangeTs, lastSyncTs);

        var payload = new
        {
            missingTotal = stats.MissingTotal,
            missingActionable = stats.MissingActionable,
            stateFingerprint = fingerprint,
            lastChangeTs
        };

        PosterStatsCache.Set(cacheKey, payload, PosterStatsCacheDuration);
        return Ok(payload);
    }

    // GET /api/posters/queue/status
    [HttpGet("queue/status")]
    public IActionResult GetQueueStatus()
    {
        var snapshot = _queue.GetSnapshot();
        return Ok(new
        {
            pendingCount = snapshot.PendingCount,
            queueSize = snapshot.PendingCount,
            inFlightCount = snapshot.InFlightCount,
            isProcessing = snapshot.IsProcessing,
            oldestQueuedAgeMs = snapshot.OldestQueuedAgeMs,
            lastJobStartedAtTs = snapshot.LastJobStartedAtTs,
            lastJobEndedAtTs = snapshot.LastJobEndedAtTs,
            currentJob = snapshot.CurrentJob is null
                ? null
                : new
                {
                    itemId = snapshot.CurrentJob.ItemId,
                    forceRefresh = snapshot.CurrentJob.ForceRefresh,
                    startedAtTs = snapshot.CurrentJob.StartedAtTs
                },
            jobsEnqueued = snapshot.JobsEnqueued,
            jobsCoalesced = snapshot.JobsCoalesced,
            jobsTimedOut = snapshot.JobsTimedOut,
            jobsProcessed = snapshot.JobsProcessed,
            jobsSucceeded = snapshot.JobsSucceeded,
            jobsFailed = snapshot.JobsFailed,
            jobsRetried = snapshot.JobsRetried
        });
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

    private async Task<IActionResult> EnqueueBulk(IEnumerable<long> ids, bool forceRefresh, CancellationToken ct, int? total = null)
        => (await EnqueueBulkInternal(ids, forceRefresh, ct, total).ConfigureAwait(false)).result;

    private async Task<(IActionResult result, int enqueued, int coalesced, int missing, int timedOut, int total)> EnqueueBulkInternal(
        IEnumerable<long> ids,
        bool forceRefresh,
        CancellationToken ct,
        int? total = null,
        string? retroLogFile = null)
    {
        var requestedIds = ids as IReadOnlyCollection<long> ?? ids.ToList();
        var distinctIds = requestedIds.Distinct().ToList();

        var missing = 0;
        var jobs = new List<PosterFetchJob>(distinctIds.Count);

        foreach (var id in distinctIds)
        {
            var job = _jobFactory.Create(id, forceRefresh, retroLogFile);
            if (job is null)
            {
                missing++;
                continue;
            }

            jobs.Add(job);
        }

        var batch = await _queue.EnqueueManyAsync(jobs, ct, PosterFetchQueue.DefaultBatchEnqueueTimeout).ConfigureAwait(false);
        missing += batch.Rejected;

        var enqueued = batch.Enqueued;
        var coalesced = batch.Coalesced;
        var timedOut = batch.TimedOut;
        var totalCount = total ?? requestedIds.Count;
        var result = Accepted(new
        {
            ok = true,
            total = totalCount,
            enqueued,
            coalesced,
            missing,
            timedOut,
            failed = timedOut,
            forceRefresh
        });

        return (result, enqueued, coalesced, missing, timedOut, totalCount);
    }

    private IActionResult? ValidateBulkPayloadIds(IReadOnlyCollection<long>? ids)
    {
        if (ids is null || ids.Count == 0)
            return BadRequest(new { error = "ids missing" });
        if (ids.Count > MaxBulkIds)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "too many ids", maxIds = MaxBulkIds });

        return null;
    }

    // GET /api/posters/retro-fetch/log/{file}
    [HttpGet("retro-fetch/log/{file}")]
    public IActionResult DownloadRetroFetchLog([FromRoute] string file)
    {
        // Defense-in-depth: validate input in the controller before delegating to the service.

        if (string.IsNullOrWhiteSpace(file))
            return BadRequest(new { error = "invalid log file name" });

        // Reject absolute paths immediately (e.g. "/etc/passwd", "C:\\Windows\\...").
        if (Path.IsPathRooted(file))
        {
            _log.LogWarning("DownloadRetroFetchLog: rejected absolute path – value={File}", SanitizeForLog(file));
            return StatusCode(403, new { error = "invalid log file name" });
        }

        // Belt-and-suspenders: reject path separators explicitly before GetFileName.
        // Covers both OS-native separators and any literal slash passed through routing.
        if (file.Contains('/') || file.Contains('\\'))
        {
            _log.LogWarning("DownloadRetroFetchLog: rejected separator in filename – value={File}", SanitizeForLog(file));
            return StatusCode(403, new { error = "invalid log file name" });
        }

        // Strip to filename only (no trim — raw input must already be a plain filename).
        // If any separator survived the check above, Path.GetFileName would produce a
        // different string and the Ordinal comparison below would reject it.
        var safeFileName = Path.GetFileName(file);
        if (string.IsNullOrWhiteSpace(safeFileName))
            return BadRequest(new { error = "invalid log file name" });

        if (!string.Equals(safeFileName, file, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "DownloadRetroFetchLog: rejected path-traversal attempt – raw={File} sanitized={Safe}",
                SanitizeForLog(file), SanitizeForLog(safeFileName));
            return StatusCode(403, new { error = "invalid log file name" });
        }

        // Extension whitelist: RetroFetchLogService only ever writes .csv files.
        // Extend this list only if new log formats are added to that service.
        if (!safeFileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "invalid log file type: only .csv files are allowed" });

        // Delegate path resolution to the service (also applies Path.GetFileName + extension check).
        var full = _retroLogs.ResolveLogPath(safeFileName);
        if (string.IsNullOrWhiteSpace(full))
            return BadRequest(new { error = "invalid log file" });

        // Final containment check: canonical absolute path must sit inside the logs directory.
        var logsDirAbs = Path.GetFullPath(_retroLogs.LogsDirPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var resolvedAbs = Path.GetFullPath(full);
        if (!resolvedAbs.StartsWith(logsDirAbs, StringComparison.OrdinalIgnoreCase))
        {
            // Log the safe filename only — never expose full resolved paths in log output.
            _log.LogWarning(
                "DownloadRetroFetchLog: resolved path for {File} is outside logs directory",
                SanitizeForLog(safeFileName));
            return StatusCode(403, new { error = "invalid log file path" });
        }

        if (!System.IO.File.Exists(full))
            return NotFound();

        // Audit log: safe filename only, control characters stripped.
        _log.LogInformation("DownloadRetroFetchLog: serving {File}", SanitizeForLog(safeFileName));
        return PhysicalFile(full, "text/csv", safeFileName);
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

    private static (string? artist, string title) ParseAudioSearchQuery(string input)
    {
        var raw = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return (null, "");

        var separators = new[] { " - ", " – ", " — ", " | ", " : ", ", " };
        foreach (var separator in separators)
        {
            var idx = raw.IndexOf(separator, StringComparison.Ordinal);
            if (idx <= 0 || idx >= raw.Length - separator.Length)
                continue;

            var artist = raw[..idx].Trim();
            var title = raw[(idx + separator.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                return (artist, title);
        }

        return (null, raw);
    }

    /// <summary>
    /// Strips ASCII control characters (CR, LF, TAB) from a value before it is written to
    /// a log, preventing log-injection attacks that could forge or split log lines.
    /// Full paths are never passed here — only filenames.
    /// </summary>
    private static string SanitizeForLog(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    private Task<T> RunProviderAsync<T>(ProviderKind kind, Func<CancellationToken, Task<T>> action, CancellationToken ct)
        => _externalProviderLimiter.RunAsync(kind, action, ct);

    private PosterPathResolver CreatePosterPathResolver()
        => new(_posterFetch.PostersDirPath);

    /// <summary>
    /// Resolves and serves a poster thumbnail with the following fallback chain:
    /// 1. store/{storeDir}/w{w}.webp (pre-generated WebP thumb)
    /// 2. store/{storeDir}/original.* (full-size original from store — enqueue thumb warmup, serve original immediately)
    /// 3. legacy flat poster_file
    /// 4. 404
    /// All served paths get <c>Cache-Control: public, max-age=31536000, immutable</c>.
    /// </summary>
    private async Task<IActionResult> BuildThumbResultAsync(
        string? storeDir, string? posterFile, int w, string logContext, CancellationToken ct)
    {
        var effectiveWidth = PosterThumbService.SupportedWidths.Contains(w)
            ? w
            : PosterThumbService.SupportedWidths.OrderBy(sw => Math.Abs(sw - w)).First();

        if (!string.IsNullOrWhiteSpace(storeDir) &&
            _posterFetch.TryResolveStoreThumbPath(storeDir, effectiveWidth, out var thumbFull) &&
            System.IO.File.Exists(thumbFull))
        {
            Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            Response.Headers.Vary = "Accept-Encoding";
            return PhysicalFile(thumbFull, "image/webp");
        }

        if (!string.IsNullOrWhiteSpace(storeDir) &&
            _posterFetch.TryResolveStoreOriginalPath(storeDir, out var originalPath))
        {
            await EnqueueMissingThumbAsync(storeDir, effectiveWidth, logContext, ct).ConfigureAwait(false);
            return BuildAbsolutePosterFileResult(originalPath, logContext);
        }

        if (!string.IsNullOrWhiteSpace(posterFile))
            return BuildPosterFileResult(posterFile, logContext);

        return NotFound();
    }

    private bool TryResolveStoredPosterPath(string file, out string fullPath)
    {
        var resolver = CreatePosterPathResolver();
        var resolved = resolver.TryResolvePosterFile(file, out fullPath);
        if (!resolved)
        {
            _log.LogWarning("Rejected unsafe poster file path from storage: {File}", SanitizeForLog(file));
        }

        return resolved;
    }

    private IActionResult BuildPosterFileResult(string storedFile, string logContext, string cacheControl = "public, max-age=31536000, immutable")
    {
        if (!TryResolveStoredPosterPath(storedFile, out var fullPath))
            return NotFound();

        return BuildAbsolutePosterFileResult(fullPath, logContext, cacheControl, SanitizeForLog(storedFile));
    }

    private async Task EnqueueMissingThumbAsync(string storeDir, int width, string logContext, CancellationToken ct)
    {
        try
        {
            var enqueue = await _thumbQueue.EnqueueAsync(
                new PosterThumbJob(storeDir, [width], PosterThumbJobReason.MissingThumb),
                ct,
                PosterThumbQueue.DefaultEnqueueTimeout).ConfigureAwait(false);
            if (enqueue.IsTimedOut)
                _log.LogWarning("Poster thumb enqueue timed out for {Context} storeDir={StoreDir} width={Width}", logContext, storeDir, width);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Poster thumb enqueue failed for {Context} storeDir={StoreDir} width={Width}", logContext, storeDir, width);
        }
    }

    private IActionResult BuildAbsolutePosterFileResult(
        string fullPath,
        string logContext,
        string cacheControl = "public, max-age=31536000, immutable",
        string? logFile = null)
    {
        try
        {
            if (Directory.Exists(fullPath))
            {
                _log.LogWarning("Poster file path resolves to a directory for {Context}: {File}", logContext, logFile ?? SanitizeForLog(Path.GetFileName(fullPath)));
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "poster file unavailable" });
            }

            if (!System.IO.File.Exists(fullPath))
            {
                _log.LogInformation("Poster file missing for {Context}: {File}", logContext, logFile ?? SanitizeForLog(Path.GetFileName(fullPath)));
                return NotFound();
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogWarning(ex, "Poster file access denied for {Context}: {File}", logContext, logFile ?? SanitizeForLog(Path.GetFileName(fullPath)));
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "poster file unavailable" });
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Poster file read failed for {Context}: {File}", logContext, logFile ?? SanitizeForLog(Path.GetFileName(fullPath)));
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "poster file unavailable" });
        }

        Response.Headers.CacheControl = cacheControl;
        Response.Headers.Vary = "Accept-Encoding";
        return PhysicalFile(fullPath, GetContentTypeForPoster(fullPath));
    }

    private static string GetContentTypeForPoster(string fullPath)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}
