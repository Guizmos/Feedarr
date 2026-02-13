using System.Text.Json;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Arr;
using Feedarr.Api.Services.Arr;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/arr")]
public sealed class ArrController : ControllerBase
{
    private const int MaxStatusItemsPerRequest = 250;

    private readonly ArrApplicationRepository _repo;
    private readonly ArrLibraryRepository _library;
    private readonly ReleaseRepository _releases;
    private readonly SonarrClient _sonarr;
    private readonly RadarrClient _radarr;
    private readonly ArrLibraryCacheService _cache;
    private readonly ArrLibrarySyncService _syncService;
    private readonly MediaEntityArrStatusService _entityStatus;
    private readonly ActivityRepository _activity;
    private readonly ILogger<ArrController> _logger;
    private readonly IWebHostEnvironment _env;

    public ArrController(
        ArrApplicationRepository repo,
        ArrLibraryRepository library,
        ReleaseRepository releases,
        SonarrClient sonarr,
        RadarrClient radarr,
        ArrLibraryCacheService cache,
        ArrLibrarySyncService syncService,
        MediaEntityArrStatusService entityStatus,
        ActivityRepository activity,
        ILogger<ArrController> logger,
        IWebHostEnvironment env)
    {
        _repo = repo;
        _library = library;
        _releases = releases;
        _sonarr = sonarr;
        _radarr = radarr;
        _cache = cache;
        _syncService = syncService;
        _entityStatus = entityStatus;
        _activity = activity;
        _logger = logger;
        _env = env;
    }

    private static List<int> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<int>>(tagsJson) ?? new();
        }
        catch
        {
            return new();
        }
    }

    // POST /api/arr/sonarr/add
    [HttpPost("sonarr/add")]
    public async Task<ActionResult<ArrAddResponseDto>> AddToSonarr(
        [FromBody] SonarrAddRequestDto dto,
        CancellationToken ct)
    {
        if (dto.TvdbId <= 0)
            return BadRequest(new ArrAddResponseDto { Ok = false, Status = "error", Message = "tvdbId is required" });

        // Get app
        var app = dto.AppId.HasValue
            ? _repo.Get(dto.AppId.Value)
            : _repo.GetDefault("sonarr");

        if (app is null)
            return BadRequest(new ArrAddResponseDto { Ok = false, Status = "error", Message = "No Sonarr app configured" });

        if (!app.IsEnabled)
            return BadRequest(new ArrAddResponseDto { Ok = false, Status = "error", Message = "Sonarr app is disabled" });

        if (string.IsNullOrWhiteSpace(app.ApiKeyEncrypted))
            return BadRequest(new ArrAddResponseDto { Ok = false, Status = "error", Message = "Sonarr API key not configured" });

        try
        {
            // Check cache first
            var cached = _cache.CheckSonarrExists(dto.TvdbId);
            if (cached.exists)
            {
                _activity.Add(null, "info", "arr", $"Series already exists (tvdbId={dto.TvdbId})",
                    dataJson: $"{{\"tvdbId\":{dto.TvdbId},\"status\":\"exists\"}}");

                return Ok(new ArrAddResponseDto
                {
                    Ok = true,
                    Status = "exists",
                    OpenUrl = cached.openUrl,
                    Message = "Series already exists in Sonarr"
                });
            }

            // Lookup series
            var lookupResults = await _sonarr.LookupSeriesAsync(app.BaseUrl, app.ApiKeyEncrypted, dto.TvdbId, ct);
            var lookup = lookupResults.FirstOrDefault();

            if (lookup is null)
            {
                return Ok(new ArrAddResponseDto
                {
                    Ok = false,
                    Status = "error",
                    Message = $"Series not found with tvdbId={dto.TvdbId}"
                });
            }

            // Merge defaults with request overrides
            var rootFolder = dto.RootFolderPath ?? app.RootFolderPath;
            var qualityProfile = dto.QualityProfileId ?? app.QualityProfileId;
            var tags = dto.Tags ?? ParseTags(app.Tags);
            var seriesType = dto.SeriesType ?? app.SeriesType ?? "standard";
            var seasonFolder = dto.SeasonFolder ?? app.SeasonFolder;
            var monitorMode = dto.MonitorMode ?? app.MonitorMode ?? "all";
            var searchMissing = dto.SearchMissing ?? app.SearchMissing;
            var searchCutoff = dto.SearchCutoff ?? app.SearchCutoff;

            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                var folders = await _sonarr.GetRootFoldersAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
                rootFolder = folders.FirstOrDefault()?.Path;
            }

            if (!qualityProfile.HasValue)
            {
                var profiles = await _sonarr.GetQualityProfilesAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
                qualityProfile = profiles.FirstOrDefault()?.Id;
            }

            if (string.IsNullOrWhiteSpace(rootFolder) || !qualityProfile.HasValue)
            {
                return Ok(new ArrAddResponseDto
                {
                    Ok = false,
                    Status = "error",
                    Message = "Root folder or quality profile not configured"
                });
            }

            // Add series
            var result = await _sonarr.AddSeriesAsync(
                app.BaseUrl,
                app.ApiKeyEncrypted,
                lookup,
                rootFolder,
                qualityProfile.Value,
                tags,
                seriesType,
                seasonFolder,
                monitorMode,
                searchMissing,
                searchCutoff,
                ct
            );

            string? openUrl = null;
            if (result.SeriesId.HasValue && !string.IsNullOrWhiteSpace(result.TitleSlug))
            {
                openUrl = _sonarr.BuildOpenUrl(app.BaseUrl, result.TitleSlug);
                _cache.AddToSonarrCache(dto.TvdbId, result.SeriesId.Value, result.TitleSlug, app.BaseUrl);
            }

            _activity.Add(null, "info", "arr", $"Series {result.Status}: {lookup.Title} (tvdbId={dto.TvdbId})",
                dataJson: $"{{\"tvdbId\":{dto.TvdbId},\"status\":\"{result.Status}\",\"seriesId\":{result.SeriesId ?? 0}}}");

            return Ok(new ArrAddResponseDto
            {
                Ok = result.Success,
                Status = result.Status,
                OpenUrl = openUrl,
                Message = result.Message
            });
        }
        catch (Exception ex)
        {
            var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "sonarr add failed");
            _activity.Add(null, "error", "arr", $"Sonarr add failed: {safeError}",
                dataJson: $"{{\"tvdbId\":{dto.TvdbId}}}");

            return Ok(new ArrAddResponseDto
            {
                Ok = false,
                Status = "error",
                Message = safeError
            });
        }
    }

    // POST /api/arr/radarr/add
    [HttpPost("radarr/add")]
    public async Task<ActionResult<ArrAddResponseDto>> AddToRadarr(
        [FromBody] RadarrAddRequestDto dto,
        CancellationToken ct)
    {
        if (dto.TmdbId <= 0)
            return BadRequest(new ArrAddResponseDto { Ok = false, Status = "error", Message = "tmdbId is required" });

        // Get app
        var app = dto.AppId.HasValue
            ? _repo.Get(dto.AppId.Value)
            : _repo.GetDefault("radarr");

        if (app is null)
            return BadRequest(new ArrAddResponseDto { Ok = false, Status = "error", Message = "No Radarr app configured" });

        if (!app.IsEnabled)
            return BadRequest(new ArrAddResponseDto { Ok = false, Status = "error", Message = "Radarr app is disabled" });

        if (string.IsNullOrWhiteSpace(app.ApiKeyEncrypted))
            return BadRequest(new ArrAddResponseDto { Ok = false, Status = "error", Message = "Radarr API key not configured" });

        try
        {
            // Check cache first
            var cached = _cache.CheckRadarrExists(dto.TmdbId);
            if (cached.exists)
            {
                _activity.Add(null, "info", "arr", $"Movie already exists (tmdbId={dto.TmdbId})",
                    dataJson: $"{{\"tmdbId\":{dto.TmdbId},\"status\":\"exists\"}}");

                return Ok(new ArrAddResponseDto
                {
                    Ok = true,
                    Status = "exists",
                    OpenUrl = cached.openUrl,
                    Message = "Movie already exists in Radarr"
                });
            }

            // Lookup movie
            var lookupResults = await _radarr.LookupMovieAsync(app.BaseUrl, app.ApiKeyEncrypted, dto.TmdbId, ct);
            var lookup = lookupResults.FirstOrDefault();

            if (lookup is null)
            {
                return Ok(new ArrAddResponseDto
                {
                    Ok = false,
                    Status = "error",
                    Message = $"Movie not found with tmdbId={dto.TmdbId}"
                });
            }

            // Merge defaults with request overrides
            var rootFolder = dto.RootFolderPath ?? app.RootFolderPath;
            var qualityProfile = dto.QualityProfileId ?? app.QualityProfileId;
            var tags = dto.Tags ?? ParseTags(app.Tags);
            var minAvail = dto.MinimumAvailability ?? app.MinimumAvailability ?? "released";
            var searchForMovie = dto.SearchForMovie ?? app.SearchForMovie;

            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                var folders = await _radarr.GetRootFoldersAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
                rootFolder = folders.FirstOrDefault()?.Path;
            }

            if (!qualityProfile.HasValue)
            {
                var profiles = await _radarr.GetQualityProfilesAsync(app.BaseUrl, app.ApiKeyEncrypted, ct);
                qualityProfile = profiles.FirstOrDefault()?.Id;
            }

            if (string.IsNullOrWhiteSpace(rootFolder) || !qualityProfile.HasValue)
            {
                return Ok(new ArrAddResponseDto
                {
                    Ok = false,
                    Status = "error",
                    Message = "Root folder or quality profile not configured"
                });
            }

            // Add movie
            var result = await _radarr.AddMovieAsync(
                app.BaseUrl,
                app.ApiKeyEncrypted,
                lookup,
                rootFolder,
                qualityProfile.Value,
                tags,
                minAvail,
                searchForMovie,
                ct
            );

            string? openUrl = null;
            if (result.MovieId.HasValue)
            {
                // Use tmdbId for URL (not internal movieId) - Radarr URLs use tmdbId
                openUrl = _radarr.BuildOpenUrl(app.BaseUrl, dto.TmdbId);
                _cache.AddToRadarrCache(dto.TmdbId, result.MovieId.Value, app.BaseUrl);
            }

            _activity.Add(null, "info", "arr", $"Movie {result.Status}: {lookup.Title} (tmdbId={dto.TmdbId})",
                dataJson: $"{{\"tmdbId\":{dto.TmdbId},\"status\":\"{result.Status}\",\"movieId\":{result.MovieId ?? 0}}}");

            return Ok(new ArrAddResponseDto
            {
                Ok = result.Success,
                Status = result.Status,
                OpenUrl = openUrl,
                Message = result.Message
            });
        }
        catch (Exception ex)
        {
            var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "radarr add failed");
            _activity.Add(null, "error", "arr", $"Radarr add failed: {safeError}",
                dataJson: $"{{\"tmdbId\":{dto.TmdbId}}}");

            return Ok(new ArrAddResponseDto
            {
                Ok = false,
                Status = "error",
                Message = safeError
            });
        }
    }

    // POST /api/arr/status
    // Uses persistent database storage (synced every 10 minutes by background service)
    [HttpPost("status")]
    public async Task<ActionResult<ArrStatusResponseDto>> CheckStatus([FromBody] ArrStatusRequestDto dto, CancellationToken ct)
    {
        var items = dto?.Items?.Where(i => i is not null).ToList() ?? new List<ArrStatusItemDto>();
        if (items.Count == 0)
            return Problem(title: "items missing", statusCode: StatusCodes.Status400BadRequest);
        if (items.Count > MaxStatusItemsPerRequest)
            return Problem(
                title: $"too many items (max {MaxStatusItemsPerRequest})",
                statusCode: StatusCodes.Status400BadRequest);

        var response = new ArrStatusResponseDto();
        var sonarrCacheEnsured = false;
        var radarrCacheEnsured = false;
        var sw = Stopwatch.StartNew();
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var statusRows = new List<ReleaseArrStatusRow>(items.Count);
        var releaseIds = items
            .Where(i => i.ReleaseId.HasValue)
            .Select(i => i.ReleaseId!.Value)
            .Distinct()
            .ToArray();
        var entityStatusByRelease = _entityStatus.GetArrStatusForReleaseIds(releaseIds);

        _logger.LogInformation("Arr status check started: {Count} items", items.Count);
        var seriesCount = _library.GetTotalCountByType("series");
        var movieCount = _library.GetTotalCountByType("movie");
        _logger.LogDebug("Arr status DB counts: {SeriesCount} series, {MovieCount} movies", seriesCount, movieCount);

        foreach (var item in items)
        {
            if (item.ReleaseId.HasValue &&
                entityStatusByRelease.TryGetValue(item.ReleaseId.Value, out var entityStatus))
            {
                var cachedResult = new ArrStatusResultDto
                {
                    TvdbId = entityStatus.TvdbId ?? item.TvdbId,
                    TmdbId = entityStatus.TmdbId ?? item.TmdbId,
                    InSonarr = entityStatus.InSonarr,
                    InRadarr = entityStatus.InRadarr,
                    SonarrSeriesId = entityStatus.SonarrItemId,
                    RadarrMovieId = entityStatus.RadarrItemId,
                    SonarrUrl = entityStatus.SonarrUrl,
                    RadarrUrl = entityStatus.RadarrUrl
                };

                cachedResult.Exists = cachedResult.InSonarr || cachedResult.InRadarr;
                cachedResult.ExternalId = cachedResult.SonarrSeriesId ?? cachedResult.RadarrMovieId;
                cachedResult.OpenUrl = cachedResult.SonarrUrl ?? cachedResult.RadarrUrl;

                statusRows.Add(new ReleaseArrStatusRow
                {
                    ReleaseId = item.ReleaseId.Value,
                    InSonarr = cachedResult.InSonarr,
                    InRadarr = cachedResult.InRadarr,
                    SonarrUrl = cachedResult.SonarrUrl,
                    RadarrUrl = cachedResult.RadarrUrl,
                    CheckedAtTs = nowTs
                });

                response.Results.Add(cachedResult);
                continue;
            }

            var result = new ArrStatusResultDto
            {
                TvdbId = item.TvdbId,
                TmdbId = item.TmdbId
            };

            var isSeries = item.MediaType?.Equals("series", StringComparison.OrdinalIgnoreCase) == true;
            var isMovie = item.MediaType?.Equals("movie", StringComparison.OrdinalIgnoreCase) == true;
            var hasTitle = !string.IsNullOrWhiteSpace(item.Title);
            var normalizedTitle = hasTitle ? TitleNormalizer.NormalizeTitleStrict(item.Title) : "";

            _logger.LogDebug("Checking: TvdbId={TvdbId}, TmdbId={TmdbId}, MediaType={MediaType}, Title={Title}, NormalizedTitle={NormalizedTitle}, IsSeries={IsSeries}, IsMovie={IsMovie}",
                item.TvdbId, item.TmdbId, item.MediaType, item.Title, normalizedTitle, isSeries, isMovie);

            // Check Sonarr (series): multi-layer matching
            // Layer 1: Match by tvdbId (most reliable)
            // Layer 2: Match by title if mediaType is "series"
            // Layer 3: Try title match even if mediaType unknown (series that aren't detected)
            LibraryMatchResult? sonarrMatch = null;
            string sonarrMatchMethod = "";

            if (item.TvdbId.HasValue)
            {
                _logger.LogDebug("  Trying Sonarr match by tvdbId={TvdbId}", item.TvdbId.Value);
                sonarrMatch = _library.FindSeriesByTvdbId(item.TvdbId.Value);
                if (sonarrMatch != null)
                {
                    sonarrMatchMethod = "tvdbId";
                    _logger.LogDebug("  Sonarr MATCH by tvdbId: {Title}", sonarrMatch.Title);
                }
                else
                {
                    _logger.LogDebug("  No Sonarr match by tvdbId");
                }
            }

            // Try title matching for series
            if (sonarrMatch == null && hasTitle && isSeries)
            {
                _logger.LogDebug("  Trying Sonarr match by title (mediaType=series): {Title}", item.Title);
                sonarrMatch = _library.FindSeriesByTitle(item.Title!);
                if (sonarrMatch != null)
                {
                    sonarrMatchMethod = "title (series)";
                    _logger.LogDebug("  Sonarr MATCH by title (series): {MatchedTitle}", sonarrMatch.Title);
                }
                else
                {
                    _logger.LogDebug("  No Sonarr match by title (series)");
                }
            }

            // Fallback: Try title matching even if not explicitly marked as series
            // This catches series that might be incorrectly categorized
            if (sonarrMatch == null && hasTitle && !isMovie)
            {
                _logger.LogDebug("  Trying Sonarr match by title (fallback, not movie): {Title}", item.Title);
                sonarrMatch = _library.FindSeriesByTitle(item.Title!);
                if (sonarrMatch != null)
                {
                    sonarrMatchMethod = "title (fallback)";
                    _logger.LogDebug("  Sonarr MATCH by title (fallback): {MatchedTitle}", sonarrMatch.Title);
                }
                else
                {
                    _logger.LogDebug("  No Sonarr match by title (fallback)");
                }
            }

            if (sonarrMatch != null)
            {
                _logger.LogDebug("Sonarr match found via {Method}: {Title} (tvdbId={TvdbId}, internalId={InternalId})",
                    sonarrMatchMethod, sonarrMatch.Title, sonarrMatch.TvdbId, sonarrMatch.InternalId);

                result.Exists = true;
                result.InSonarr = true;
                result.SonarrSeriesId = sonarrMatch.InternalId;
                result.SonarrUrl = string.IsNullOrWhiteSpace(sonarrMatch.TitleSlug)
                    ? null
                    : _sonarr.BuildOpenUrl(sonarrMatch.BaseUrl, sonarrMatch.TitleSlug);
                if (sonarrMatch.TvdbId.HasValue)
                    result.TvdbId = sonarrMatch.TvdbId;
                result.ExternalId = sonarrMatch.InternalId;
                result.OpenUrl = result.SonarrUrl;
            }
            else
            {
                if (item.TvdbId.HasValue)
                {
                    var cached = await _cache.CheckSonarrExistsWithRefreshAsync(item.TvdbId.Value, ct);
                    if (cached.exists)
                    {
                        result.Exists = true;
                        result.InSonarr = true;
                        result.SonarrSeriesId = cached.seriesId;
                        result.SonarrUrl = cached.openUrl;
                        result.ExternalId = cached.seriesId;
                        result.OpenUrl = cached.openUrl;
                    }
                }

                if (!result.InSonarr && hasTitle && (isSeries || !isMovie))
                {
                    if (!sonarrCacheEnsured)
                    {
                        await _cache.EnsureSonarrCacheFreshAsync(ct);
                        sonarrCacheEnsured = true;
                    }

                    var cachedTitle = _cache.CheckSonarrExistsByTitle(item.Title);
                    if (cachedTitle.exists)
                    {
                        result.Exists = true;
                        result.InSonarr = true;
                        result.SonarrSeriesId = cachedTitle.seriesId;
                        result.SonarrUrl = cachedTitle.openUrl;
                        if (cachedTitle.foundTvdbId.HasValue)
                            result.TvdbId = cachedTitle.foundTvdbId;
                        result.ExternalId = cachedTitle.seriesId;
                        result.OpenUrl = cachedTitle.openUrl;
                    }
                }

                if (!result.InSonarr && (isSeries || (hasTitle && !isMovie)))
                {
                    _logger.LogDebug("No Sonarr match for: TvdbId={TvdbId}, Title={Title}", item.TvdbId, item.Title);
                }
            }

            // Check Radarr (movies): multi-layer matching
            // Layer 1: Match by tmdbId (most reliable)
            // Layer 2: Match by title if mediaType is "movie"
            // Layer 3: Try title match even if mediaType unknown (movies that aren't detected)
            LibraryMatchResult? radarrMatch = null;
            string radarrMatchMethod = "";

            if (item.TmdbId.HasValue)
            {
                radarrMatch = _library.FindMovieByTmdbId(item.TmdbId.Value);
                if (radarrMatch != null) radarrMatchMethod = "tmdbId";
            }

            // Try title matching for movies
            if (radarrMatch == null && hasTitle && isMovie)
            {
                radarrMatch = _library.FindMovieByTitle(item.Title!);
                if (radarrMatch != null) radarrMatchMethod = "title (movie)";
            }

            // Fallback: Try title matching even if not explicitly marked as movie
            // This catches movies that might be incorrectly categorized
            if (radarrMatch == null && hasTitle && !isSeries)
            {
                radarrMatch = _library.FindMovieByTitle(item.Title!);
                if (radarrMatch != null) radarrMatchMethod = "title (fallback)";
            }

            if (radarrMatch != null)
            {
                _logger.LogDebug("Radarr match found via {Method}: {Title} (tmdbId={TmdbId}, internalId={InternalId})",
                    radarrMatchMethod, radarrMatch.Title, radarrMatch.TmdbId, radarrMatch.InternalId);

                result.Exists = true;
                result.InRadarr = true;
                result.RadarrMovieId = radarrMatch.InternalId;
                result.RadarrUrl = radarrMatch.TmdbId.HasValue
                    ? _radarr.BuildOpenUrl(radarrMatch.BaseUrl, radarrMatch.TmdbId.Value)
                    : null;
                if (radarrMatch.TmdbId.HasValue)
                    result.TmdbId = radarrMatch.TmdbId;
                result.ExternalId = radarrMatch.InternalId;
                result.OpenUrl = result.RadarrUrl;
            }
            else
            {
                if (item.TmdbId.HasValue)
                {
                    var cached = await _cache.CheckRadarrExistsWithRefreshAsync(item.TmdbId.Value, ct);
                    if (cached.exists)
                    {
                        result.Exists = true;
                        result.InRadarr = true;
                        result.RadarrMovieId = cached.movieId;
                        result.RadarrUrl = cached.openUrl;
                        result.ExternalId = cached.movieId;
                        result.OpenUrl = cached.openUrl;
                    }
                }

                if (!result.InRadarr && hasTitle && (isMovie || !isSeries))
                {
                    if (!radarrCacheEnsured)
                    {
                        await _cache.EnsureRadarrCacheFreshAsync(ct);
                        radarrCacheEnsured = true;
                    }

                    var cachedTitle = _cache.CheckRadarrExistsByTitle(item.Title);
                    if (cachedTitle.exists)
                    {
                        result.Exists = true;
                        result.InRadarr = true;
                        result.RadarrMovieId = cachedTitle.movieId;
                        result.RadarrUrl = cachedTitle.openUrl;
                        if (cachedTitle.foundTmdbId.HasValue)
                            result.TmdbId = cachedTitle.foundTmdbId;
                        result.ExternalId = cachedTitle.movieId;
                        result.OpenUrl = cachedTitle.openUrl;
                    }
                }

                if (!result.InRadarr && (isMovie || (hasTitle && !isSeries)))
                {
                    _logger.LogDebug("No Radarr match for: TmdbId={TmdbId}, Title={Title}", item.TmdbId, item.Title);
                }
            }

            if (item.ReleaseId.HasValue)
            {
                statusRows.Add(new ReleaseArrStatusRow
                {
                    ReleaseId = item.ReleaseId.Value,
                    InSonarr = result.InSonarr,
                    InRadarr = result.InRadarr,
                    SonarrUrl = result.SonarrUrl,
                    RadarrUrl = result.RadarrUrl,
                    CheckedAtTs = nowTs
                });
            }

            response.Results.Add(result);
        }

        var matchedCount = response.Results.Count(r => r.Exists);
        if (statusRows.Count > 0)
        {
            _releases.UpsertArrStatus(statusRows);
        }

        sw.Stop();
        var sonarrCount = response.Results.Count(r => r.InSonarr);
        var radarrCount = response.Results.Count(r => r.InRadarr);
        _logger.LogInformation(
            "Arr status check completed: {Matched}/{Total} (sonarr={SonarrCount}, radarr={RadarrCount}) in {ElapsedMs}ms",
            matchedCount,
            items.Count,
            sonarrCount,
            radarrCount,
            sw.ElapsedMilliseconds);

        return Ok(response);
    }

    // POST /api/arr/cache/refresh - Deprecated, use /api/arr/sync instead
    [HttpPost("cache/refresh")]
    public async Task<IActionResult> RefreshCache(CancellationToken ct)
    {
        try
        {
            // Also trigger the persistent sync
            await _syncService.SyncAllAppsAsync(ct);
            return Ok(new { ok = true, message = "Library synced" });
        }
        catch (BackupOperationException ex) when (ex.StatusCode == StatusCodes.Status409Conflict)
        {
            var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "arr sync unavailable");
            return Problem(title: safeError, statusCode: StatusCodes.Status409Conflict);
        }
    }

    // POST /api/arr/sync - Sync all enabled apps
    [HttpPost("sync")]
    public async Task<IActionResult> SyncAll(CancellationToken ct)
    {
        try
        {
            await _syncService.SyncAllAppsAsync(ct);
            return Ok(new { ok = true, message = "Library synced" });
        }
        catch (BackupOperationException ex) when (ex.StatusCode == StatusCodes.Status409Conflict)
        {
            var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "arr sync unavailable");
            return Problem(title: safeError, statusCode: StatusCodes.Status409Conflict);
        }
    }

    // POST /api/arr/sync/{appId} - Sync a specific app
    [HttpPost("sync/{appId:long}")]
    public async Task<IActionResult> SyncApp([FromRoute] long appId, CancellationToken ct)
    {
        try
        {
            await _syncService.SyncAppAsync(appId, ct);
            return Ok(new { ok = true, message = $"App {appId} synced" });
        }
        catch (BackupOperationException ex) when (ex.StatusCode == StatusCodes.Status409Conflict)
        {
            var safeError = ErrorMessageSanitizer.ToOperationalMessage(ex, "arr sync unavailable");
            return Problem(title: safeError, statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Arr app sync failed for appId={AppId}", appId);
            return Problem(
                title: "arr sync failed",
                detail: "upstream arr service unavailable",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // GET /api/arr/sync/status - Get sync status for all apps
    [HttpGet("sync/status")]
    public IActionResult GetSyncStatus()
    {
        var status = _library.GetSyncStatus();
        return Ok(status);
    }

    // GET /api/arr/library/debug - Debug endpoint to check library contents
    [HttpGet("library/debug")]
    public IActionResult GetLibraryDebug([FromQuery] string? type, [FromQuery] string? search, [FromQuery] int limit = 20)
    {
        if (!_env.IsDevelopment())
            return NotFound(new { error = "endpoint not found" });

        var items = _library.GetDebugItems(type, search, limit);
        var counts = _library.GetItemCountByApp();
        var totalSeries = _library.GetTotalCountByType("series");
        var totalMovies = _library.GetTotalCountByType("movie");

        return Ok(new
        {
            totalSeries,
            totalMovies,
            countsByApp = counts,
            items
        });
    }

    // POST /api/arr/library/test-match - Test matching a specific title
    [HttpPost("library/test-match")]
    public IActionResult TestMatch([FromBody] ArrStatusItemDto item)
    {
        if (!_env.IsDevelopment())
            return NotFound(new { error = "endpoint not found" });

        var results = new List<object>();

        // Test Sonarr matching
        if (item.TvdbId.HasValue)
        {
            var match = _library.FindSeriesByTvdbId(item.TvdbId.Value);
            results.Add(new { method = "FindSeriesByTvdbId", input = item.TvdbId.Value, found = match != null, match });
        }

        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            var match = _library.FindSeriesByTitle(item.Title);
            results.Add(new { method = "FindSeriesByTitle", input = item.Title, found = match != null, match });
        }

        // Test Radarr matching
        if (item.TmdbId.HasValue)
        {
            var match = _library.FindMovieByTmdbId(item.TmdbId.Value);
            results.Add(new { method = "FindMovieByTmdbId", input = item.TmdbId.Value, found = match != null, match });
        }

        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            var match = _library.FindMovieByTitle(item.Title);
            results.Add(new { method = "FindMovieByTitle", input = item.Title, found = match != null, match });
        }

        // Also show normalized title for debugging
        var normalizedTitle = TitleNormalizer.NormalizeTitleStrict(item.Title);

        return Ok(new
        {
            input = item,
            normalizedTitle,
            results
        });
    }
}
