using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Fanart;
using Feedarr.Api.Services.GoogleBooks;
using Feedarr.Api.Services.Igdb;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Jikan;
using Feedarr.Api.Services.ComicVine;
using Feedarr.Api.Services.TheAudioDb;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.TvMaze;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services.Posters;

public sealed record PosterFetchResult(bool Ok, int StatusCode, object? Body, long? SourceId);

public sealed class PosterFetchService
{
    private const int TmdbCandidateLimit = 10;
    private const float TmdbScoreThreshold = 0.50f;      // Réduit de 0.62 pour améliorer le matching
    private const float TmdbWeakScoreThreshold = 0.25f;  // Réduit de 0.30 pour plus de tolérance

    private const float HighConfidenceThreshold = 0.80f;
    private const float TmdbCommonTitleThreshold = 0.75f;
    private const float TvMazeScoreThreshold = 0.55f;
    private const float TvMazeAmbiguousThreshold = 0.65f;
    private const float TvMazeEmissionThreshold = 0.65f;
    private const int MinTokensForYearlessSeriesReuse = 4;

    private readonly ReleaseRepository _releases;
    private readonly ActivityRepository _activity;
    private readonly TmdbClient _tmdb;
    private readonly FanartClient _fanart;
    private readonly IgdbClient _igdb;
    private readonly TvMazeClient _tvmaze;
    private readonly JikanClient _jikan;
    private readonly TheAudioDbClient _theAudioDb;
    private readonly GoogleBooksClient _googleBooks;
    private readonly ComicVineClient _comicVine;
    private readonly PosterMatchCacheService _matchCache;
    private readonly AppOptions _opt;
    private readonly IWebHostEnvironment _env;
    private readonly PosterMatchingOrchestrator _matchingOrchestrator;
    private readonly ActiveExternalProviderConfigResolver _activeConfigResolver;

    public PosterFetchService(
        ReleaseRepository releases,
        ActivityRepository activity,
        TmdbClient tmdb,
        FanartClient fanart,
        IgdbClient igdb,
        TvMazeClient tvmaze,
        JikanClient jikan,
        TheAudioDbClient theAudioDb,
        GoogleBooksClient googleBooks,
        ComicVineClient comicVine,
        PosterMatchCacheService matchCache,
        IOptions<AppOptions> opt,
        IWebHostEnvironment env,
        PosterMatchingOrchestrator matchingOrchestrator,
        ActiveExternalProviderConfigResolver activeConfigResolver)
    {
        _releases = releases;
        _activity = activity;
        _tmdb = tmdb;
        _fanart = fanart;
        _igdb = igdb;
        _tvmaze = tvmaze;
        _jikan = jikan;
        _theAudioDb = theAudioDb;
        _googleBooks = googleBooks;
        _comicVine = comicVine;
        _matchCache = matchCache;
        _opt = opt.Value;
        _env = env;
        _matchingOrchestrator = matchingOrchestrator;
        _activeConfigResolver = activeConfigResolver;
    }

    // data/ posteurs = relatif => on l'ancre sur le ContentRoot (racine du projet)
    private string DataDirAbs =>
        Path.IsPathRooted(_opt.DataDir)
            ? _opt.DataDir
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, _opt.DataDir));

    private string PostersDirAbs => Path.Combine(DataDirAbs, "posters");

    public string PostersDirPath => PostersDirAbs;

    public int GetLocalPosterCount()
    {
        try
        {
            if (!Directory.Exists(PostersDirAbs)) return 0;
            return Directory.EnumerateFiles(PostersDirAbs, "*.*", SearchOption.TopDirectoryOnly)
                .Count();
        }
        catch
        {
            return 0;
        }
    }

    public int ClearPosterCache()
    {
        var cleared = 0;
        try
        {
            if (Directory.Exists(PostersDirAbs))
            {
                var files = Directory.EnumerateFiles(PostersDirAbs, "*.*", SearchOption.TopDirectoryOnly).ToList();
                foreach (var file in files)
                {
                    try
                    {
                        System.IO.File.Delete(file);
                        cleared++;
                    }
                    catch
                    {
                        // Ignore individual file deletion errors
                    }
                }
            }

            // Cache clear is intentionally global: remove all local references in DB.
            _releases.ClearAllPosterReferences();
        }
        catch
        {
            // Ignore directory errors
        }
        return cleared;
    }

    public async Task<string?> SaveTmdbPosterAsync(long id, int tmdbId, string posterPath, CancellationToken ct, bool logSingle = true)
    {
        if (tmdbId <= 0 || string.IsNullOrWhiteSpace(posterPath)) return null;

        Directory.CreateDirectory(PostersDirAbs);

        var bytesTmdb = await _tmdb.DownloadPosterW500Async(posterPath, ct);
        if (bytesTmdb is null || bytesTmdb.Length == 0) return null;

        var ext = Path.GetExtension(posterPath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";

        var file = $"tmdb-{tmdbId}-manual{ext}";
        var full = Path.Combine(PostersDirAbs, file);
        await System.IO.File.WriteAllBytesAsync(full, bytesTmdb, ct);

        _releases.SavePoster(id, tmdbId, posterPath, file);
        var mediaType = (string?)_releases.GetForPoster(id)?.MediaType ?? "";
        await UpdateExternalDetailsFromTmdbAsync(id, tmdbId, mediaType, ct);
        var hash = ComputeSha256Hex(bytesTmdb);
        PosterAudit.UpdateAttemptSuccess(_releases, id, "tmdb", tmdbId.ToString(CultureInfo.InvariantCulture), null, "w500", hash);

        if (logSingle)
        {
            LogActivity(null, "info", "poster_fetch", "Poster set manually (TMDB)", new
            {
                releaseId = id,
                tmdbId,
                posterPath,
                posterFile = file
            });
        }

        return $"/api/posters/release/{id}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    public async Task<string?> SaveIgdbPosterAsync(long id, int igdbId, string coverUrl, CancellationToken ct, bool logSingle = true)
    {
        if (igdbId <= 0 || string.IsNullOrWhiteSpace(coverUrl)) return null;

        Directory.CreateDirectory(PostersDirAbs);

        var bytes = await _igdb.DownloadCoverAsync(coverUrl, ct);
        if (bytes is null || bytes.Length == 0) return null;

        var file = $"igdb-{igdbId}-manual.jpg";
        var full = Path.Combine(PostersDirAbs, file);
        await System.IO.File.WriteAllBytesAsync(full, bytes, ct);

        _releases.SavePoster(id, igdbId, coverUrl, file);
        await UpdateExternalDetailsFromIgdbAsync(id, igdbId, ct);
        var size = InferIgdbSize(coverUrl);
        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, "igdb", igdbId.ToString(CultureInfo.InvariantCulture), null, size, hash);

        if (logSingle)
        {
            LogActivity(null, "info", "poster_fetch", "Poster set manually (IGDB)", new
            {
                releaseId = id,
                igdbId,
                coverUrl,
                posterFile = file
            });
        }

        return $"/api/posters/release/{id}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    public async Task<string?> SaveTheAudioDbPosterAsync(long id, string? providerId, string posterUrl, CancellationToken ct, bool logSingle = true)
    {
        if (string.IsNullOrWhiteSpace(posterUrl)) return null;

        Directory.CreateDirectory(PostersDirAbs);

        var bytes = await _theAudioDb.DownloadImageAsync(posterUrl, ct);
        if (bytes is null || bytes.Length == 0) return null;

        var ext = InferFileExtensionFromUrl(posterUrl, ".jpg");
        var normalizedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? Guid.NewGuid().ToString("N")
            : SanitizeForFile(providerId);
        var file = $"theaudiodb-{normalizedProviderId}-manual{ext}";
        var full = Path.Combine(PostersDirAbs, file);
        await System.IO.File.WriteAllBytesAsync(full, bytes, ct);

        _releases.SavePoster(id, null, posterUrl, file);
        _releases.UpdateExternalDetails(
            id,
            ExternalProviderKeys.TheAudioDb,
            providerId?.Trim() ?? normalizedProviderId,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(
            _releases,
            id,
            ExternalProviderKeys.TheAudioDb,
            providerId?.Trim() ?? normalizedProviderId,
            null,
            "original",
            hash);

        if (logSingle)
        {
            LogActivity(null, "info", "poster_fetch", "Poster set manually (TheAudioDB)", new
            {
                releaseId = id,
                providerId = providerId?.Trim(),
                posterUrl,
                posterFile = file
            });
        }

        return $"/api/posters/release/{id}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private void LogActivity(long? sourceId, string level, string eventType, string message, object? data = null)
    {
        var json = data is null ? null : JsonSerializer.Serialize(data);
        _activity.Add(sourceId, level, eventType, message, json);
    }

    private void LogProviderAttempted(long? sourceId, bool logSingle, string provider, object? data = null)
    {
        if (!logSingle) return;
        LogActivity(sourceId, "info", "poster_fetch", "providerAttempted", new
        {
            provider,
            data
        });
    }

    private void LogFallbackUsed(long? sourceId, bool logSingle, string fromProvider, string toProvider, object? data = null)
    {
        if (!logSingle) return;
        LogActivity(sourceId, "info", "poster_fetch", "fallbackUsed", new
        {
            from = fromProvider,
            to = toProvider,
            data
        });
    }

    private bool IsTvmazeEnabled()
    {
        return _activeConfigResolver.GetActiveEnabled(ExternalProviderKeys.Tvmaze);
    }

    public async Task<PosterFetchResult> FetchPosterAsync(long id, CancellationToken ct, bool logSingle, bool skipIfExists = true)
    {
        Directory.CreateDirectory(PostersDirAbs);

        var r = _releases.GetForPoster(id);
        if (r is null)
        {
            if (logSingle)
            {
                LogActivity(null, "warn", "poster_fetch", "Poster fetch failed: release not found", new { releaseId = id });
            }
            PosterAudit.UpdateAttemptFailure(_releases, id, null, null, null, null, "release not found");
            return new PosterFetchResult(false, 404, new { error = "release not found" }, null);
        }

        string? existingFile = (string?)r.PosterFile;
        long? sourceId = (long?)r.SourceId;
        int? tmdbIdStored = (int?)r.TmdbId;
        if (tmdbIdStored <= 0) tmdbIdStored = null;
        int? tvdbIdStored = (int?)r.TvdbId;
        if (tvdbIdStored <= 0) tvdbIdStored = null;

        if (skipIfExists && !string.IsNullOrWhiteSpace(existingFile))
        {
            var path = Path.Combine(PostersDirAbs, existingFile);
            if (System.IO.File.Exists(path))
            {
                var existingProvider = (string?)r.PosterProvider;
                var existingProviderId = (string?)r.PosterProviderId;
                var existingLang = (string?)r.PosterLang;
                var existingSize = (string?)r.PosterSize;
                var existingHash = (string?)r.PosterHash;
                PosterAudit.UpdateAttemptSuccess(_releases, id, existingProvider, existingProviderId, existingLang, existingSize, existingHash);
                return new PosterFetchResult(true, 200, new
                {
                    ok = true,
                    cached = true,
                    posterFile = existingFile,
                    posterUrl = $"/api/posters/release/{id}"
                }, sourceId);
            }
        }

        string title = (string?)r.TitleClean ?? "";
        int? year = (int?)r.Year;
        string categoryName = (string?)r.CategoryName ?? "";
        var unifiedValue = (string?)r.UnifiedCategory;
        UnifiedCategoryMappings.TryParse(unifiedValue, out var unifiedCategory);
        var mediaType = UnifiedCategoryMappings.ToMediaType(unifiedCategory);
        if (string.IsNullOrWhiteSpace(mediaType) || mediaType == "unknown")
            mediaType = (string?)r.MediaType ?? "unknown";
        mediaType = mediaType.ToLowerInvariant();
        if (unifiedCategory == UnifiedCategory.Emission)
            mediaType = "series";

        if (string.IsNullOrWhiteSpace(title))
        {
            if (logSingle)
            {
                LogActivity(sourceId, "warn", "poster_fetch", "Poster fetch failed: title_clean missing", new { releaseId = id, mediaType, year });
            }
            PosterAudit.UpdateAttemptFailure(_releases, id, null, null, null, null, "title_clean missing");
            return new PosterFetchResult(false, 400, new { error = "title_clean missing (sync first?)" }, sourceId);
        }

        static string CleanTitle(string s)
        {
            s = (s ?? "").Trim();
            while (s.EndsWith("-") || s.EndsWith(".") || s.EndsWith(" "))
                s = s.TrimEnd('-', '.', ' ');
            // Normalise les accents pour améliorer le matching avec TMDB
            s = TitleNormalizer.RemoveDiacritics(s.Trim());
            return s;
        }

        var titleCleanRaw = title;
        title = CleanTitle(title);

        var season = r.Season is null ? (int?)null : Convert.ToInt32(r.Season);
        var episode = r.Episode is null ? (int?)null : Convert.ToInt32(r.Episode);
        var normalizedTitle = TitleNormalizer.NormalizeTitle(titleCleanRaw);
        var ambiguity = TitleAmbiguityEvaluator.Evaluate(normalizedTitle, mediaType, year);
        var titleKey = new PosterTitleKey(mediaType, normalizedTitle, year, season, episode);
        var fingerprint = PosterMatchCacheService.BuildFingerprint(titleKey);
        var knownIds = new PosterMatchIds(tmdbIdStored, tvdbIdStored, null, null, null);
        var tvmazeEnabled = IsTvmazeEnabled();

        var cacheResult = await TryReuseFromMatchCacheAsync(
            id,
            title,
            year,
            mediaType,
            unifiedCategory,
            titleKey,
            fingerprint,
            ambiguity,
            knownIds,
            tvmazeEnabled,
            ct,
            logSingle,
            sourceId);
        if (cacheResult is not null)
            return cacheResult;

        var reuseKey = string.IsNullOrWhiteSpace(titleCleanRaw) ? title : titleCleanRaw;
        var normalizedReuseKey = TitleNormalizer.NormalizeTitle(reuseKey);
        var reuseMediaType = string.IsNullOrWhiteSpace(mediaType) || mediaType == "unknown" ? null : mediaType;
        var allowReuse = !ambiguity.IsLikelyChannelOrProgram;
        if (!year.HasValue && mediaType == "series" && ambiguity.SignificantTokenCount < MinTokensForYearlessSeriesReuse)
            allowReuse = false;
        if (!year.HasValue && ambiguity.IsAmbiguous)
            allowReuse = false;

        if (allowReuse)
        {
            var reuse = _releases.GetPosterForTitleClean(id, reuseKey, normalizedReuseKey, reuseMediaType, year);
            if (reuse is not null)
            {
                var reuseFile = (string?)reuse.PosterFile;
                if (!string.IsNullOrWhiteSpace(reuseFile))
                {
                    var reusePath = Path.Combine(PostersDirAbs, reuseFile);
                    if (System.IO.File.Exists(reusePath))
                    {
                        var reuseTmdbId = reuse.TmdbId is null ? (int?)null : Convert.ToInt32(reuse.TmdbId);
                        var reuseTvdbId = reuse.TvdbId is null ? (int?)null : Convert.ToInt32(reuse.TvdbId);
                        var reuseIds = new PosterMatchIds(reuseTmdbId, reuseTvdbId, null, null, null);
                        if (ambiguity.IsAmbiguous && !reuseIds.Overlaps(knownIds))
                        {
                            reuse = null;
                        }
                        else
                        {
                            var reusePosterPath = (string?)reuse.PosterPath ?? "";
                            var reuseProvider = (string?)reuse.PosterProvider;
                            var reuseProviderId = (string?)reuse.PosterProviderId;
                            var reuseLang = (string?)reuse.PosterLang;
                            var reuseSize = (string?)reuse.PosterSize;
                            var reuseHash = (string?)reuse.PosterHash;
                            var reuseExtProvider = (string?)reuse.ExtProvider;
                            var reuseExtProviderId = (string?)reuse.ExtProviderId;
                            var reuseExtTitle = (string?)reuse.ExtTitle;
                            var reuseExtOverview = (string?)reuse.ExtOverview;
                            var reuseExtTagline = (string?)reuse.ExtTagline;
                            var reuseExtGenres = (string?)reuse.ExtGenres;
                            var reuseExtReleaseDate = (string?)reuse.ExtReleaseDate;
                            var reuseExtRuntime = reuse.ExtRuntimeMinutes is null ? (int?)null : Convert.ToInt32(reuse.ExtRuntimeMinutes);
                            var reuseExtRating = reuse.ExtRating is null ? (double?)null : Convert.ToDouble(reuse.ExtRating);
                            var reuseExtVotes = reuse.ExtVotes is null ? (int?)null : Convert.ToInt32(reuse.ExtVotes);
                            var reuseExtDirectors = (string?)reuse.ExtDirectors;
                            var reuseExtWriters = (string?)reuse.ExtWriters;
                            var reuseExtCast = (string?)reuse.ExtCast;
                            if (string.IsNullOrWhiteSpace(reuseHash))
                                reuseHash = ComputeSha256Hex(await System.IO.File.ReadAllBytesAsync(reusePath, ct));
                            _releases.SavePoster(id, reuseTmdbId, reusePosterPath, reuseFile);
                            if (!string.IsNullOrWhiteSpace(reuseExtOverview))
                            {
                                _releases.UpdateExternalDetails(
                                    id,
                                    reuseExtProvider ?? "tmdb",
                                    reuseExtProviderId ?? "",
                                    reuseExtTitle,
                                    reuseExtOverview,
                                    reuseExtTagline,
                                    reuseExtGenres,
                                    reuseExtReleaseDate,
                                    reuseExtRuntime,
                                    reuseExtRating,
                                    reuseExtVotes,
                                    reuseExtDirectors,
                                    reuseExtWriters,
                                    reuseExtCast
                                );
                            }
                            PosterAudit.UpdateAttemptSuccess(_releases, id, reuseProvider, reuseProviderId, reuseLang, reuseSize, reuseHash);
                            if (reuseTvdbId is int tvdbId)
                                _releases.SaveTvdbId(id, tvdbId);

                            var reuseConfidence = ComputeReuseConfidence(ambiguity, reuseTmdbId, reuseTvdbId);
                            _matchCache.Upsert(new PosterMatch(
                                fingerprint,
                                mediaType,
                                normalizedTitle,
                                year,
                                season,
                                episode,
                                PosterMatchCacheService.SerializeIds(reuseIds),
                                reuseConfidence,
                                "reuse",
                                reuseFile,
                                reuseProvider,
                                reuseProviderId,
                                reuseLang,
                                reuseSize,
                                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                null,
                                null));

                            if (logSingle)
                            {
                                LogActivity(sourceId, "info", "poster_fetch", "Poster reused from existing title", new
                                {
                                    releaseId = id,
                                    title,
                                    reusedFromId = (long?)reuse.Id,
                                    posterFile = reuseFile
                                });
                            }

                            return new PosterFetchResult(true, 200, new
                            {
                                ok = true,
                                reused = true,
                                posterFile = reuseFile,
                                posterUrl = $"/api/posters/release/{id}"
                            }, sourceId);
                        }
                    }
                }
            }
        }

        var routingContext = new PosterFetchRoutingContext(
            id,
            title,
            year,
            categoryName,
            unifiedCategory,
            mediaType,
            tmdbIdStored,
            tvdbIdStored,
            normalizedTitle,
            season,
            episode,
            ambiguity,
            titleKey,
            fingerprint,
            knownIds,
            tvmazeEnabled,
            logSingle,
            sourceId);

        return await _matchingOrchestrator.FetchPosterAsync(this, routingContext, ct);
    }

    internal Task<PosterFetchResult> FetchGameBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchLegacyCategoryBranchAsync(context, ct, forceGameBranch: true);

    internal Task<PosterFetchResult> FetchVideoBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchLegacyCategoryBranchAsync(context, ct, forceGameBranch: false);

    internal Task<PosterFetchResult> FetchAnimeBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchAnimeCategoryBranchAsync(context, ct);

    internal Task<PosterFetchResult> FetchAudioBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchAudioCategoryBranchAsync(context, ct);

    internal Task<PosterFetchResult> FetchGenericBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchGenericCategoryBranchAsync(context, ct);

    private async Task<PosterFetchResult> FetchLegacyCategoryBranchAsync(
        PosterFetchRoutingContext context,
        CancellationToken ct,
        bool forceGameBranch)
    {
        var id = context.ReleaseId;
        var title = context.Title;
        var year = context.Year;
        var categoryName = context.CategoryName;
        var unifiedCategory = context.UnifiedCategory;
        var mediaType = context.MediaType;
        var tmdbIdStored = context.TmdbIdStored;
        var tvdbIdStored = context.TvdbIdStored;
        var normalizedTitle = context.NormalizedTitle;
        var season = context.Season;
        var episode = context.Episode;
        var ambiguity = context.Ambiguity;
        var titleKey = context.TitleKey;
        var fingerprint = context.Fingerprint;
        var knownIds = context.KnownIds;
        var tvmazeEnabled = context.TvmazeEnabled;
        var logSingle = context.LogSingle;
        var sourceId = context.SourceId;
        // Utilise la catégorie + le mediaType parsé + indices dans le titre brut
        var isGame = forceGameBranch || unifiedCategory == UnifiedCategory.JeuWindows;

        if (isGame)
        {
            var igdbQuery = QuerySanitizeGame(title);
            LogProviderAttempted(sourceId, logSingle, "igdb", new { releaseId = id, query = igdbQuery, title, year });
            var igdbMatch = await _igdb.SearchGameCoverAsync(igdbQuery, year, ct);
            if (igdbMatch is null)
            {
                if (logSingle)
                {
                    LogActivity(sourceId, "warn", "poster_fetch", "Poster fetch failed: no IGDB match", new { releaseId = id, title, year, categoryName });
                }
                PosterAudit.UpdateAttemptFailure(_releases, id, "igdb", null, null, null, "no igdb match");
                return new PosterFetchResult(false, 404, new { error = "no igdb match", title, year }, sourceId);
            }

            var bytes = await _igdb.DownloadCoverAsync(igdbMatch.Value.coverUrl, ct);
            if (bytes is null || bytes.Length == 0)
            {
                if (logSingle)
                {
                    LogActivity(sourceId, "error", "poster_fetch", "Poster fetch failed: IGDB download error", new { releaseId = id, title, year, igdbMatch.Value.igdbId });
                }
                PosterAudit.UpdateAttemptFailure(_releases, id, "igdb", igdbMatch.Value.igdbId.ToString(CultureInfo.InvariantCulture), null, null, "igdb cover download failed");
                return new PosterFetchResult(false, 502, new { error = "igdb cover download failed" }, sourceId);
            }

            var file = $"igdb-{igdbMatch.Value.igdbId}-cover.jpg";
            var full = Path.Combine(PostersDirAbs, file);
            await System.IO.File.WriteAllBytesAsync(full, bytes, ct);

            _releases.SavePoster(id, igdbMatch.Value.igdbId, igdbMatch.Value.coverUrl, file);
            await UpdateExternalDetailsFromIgdbAsync(id, igdbMatch.Value.igdbId, ct);
            var hash = ComputeSha256Hex(bytes);
            var size = InferIgdbSize(igdbMatch.Value.coverUrl);
            PosterAudit.UpdateAttemptSuccess(_releases, id, "igdb", igdbMatch.Value.igdbId.ToString(CultureInfo.InvariantCulture), null, size, hash);
            var igdbIds = new PosterMatchIds(null, null, null, igdbMatch.Value.igdbId, null);
            _matchCache.Upsert(new PosterMatch(
                fingerprint,
                mediaType,
                normalizedTitle,
                year,
                season,
                episode,
                PosterMatchCacheService.SerializeIds(igdbIds),
                0.85,
                "igdb",
                file,
                "igdb",
                igdbMatch.Value.igdbId.ToString(CultureInfo.InvariantCulture),
                null,
                size,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                null,
                null));

            if (logSingle)
            {
                LogActivity(sourceId, "info", "poster_fetch", "Poster fetched (IGDB)", new
                {
                    releaseId = id,
                    title,
                    year,
                    igdbId = igdbMatch.Value.igdbId,
                    posterFile = file
                });
            }

            return new PosterFetchResult(true, 200, new
            {
                ok = true,
                igdbId = igdbMatch.Value.igdbId,
                posterFile = file,
                posterUrl = $"/api/posters/release/{id}"
            }, sourceId);
        }

        var shouldTryTvmaze = tvmazeEnabled && mediaType == "series" &&
                              (unifiedCategory == UnifiedCategory.Emission || PosterProviderSelector.ShouldUseTvMaze(ambiguity, unifiedCategory));
        var tvmazeHadCandidates = false;

        if (shouldTryTvmaze)
        {
            LogProviderAttempted(sourceId, logSingle, "tvmaze", new { releaseId = id, query = title, year, mediaType });
            var tvmazeAttempt = await TryFetchFromTvMazeAsync(
                id,
                title,
                year,
                unifiedCategory,
                ambiguity,
                knownIds,
                titleKey,
                fingerprint,
                ct,
                logSingle,
                sourceId);

            tvmazeHadCandidates = tvmazeAttempt.HadCandidates;
            if (tvmazeAttempt.Result is not null)
                return tvmazeAttempt.Result;

            LogFallbackUsed(sourceId, logSingle, "tvmaze", "tmdb", new
            {
                releaseId = id,
                reason = tvmazeHadCandidates ? "no_match" : "no_results"
            });
        }

        var tmdbTvOnly = unifiedCategory == UnifiedCategory.Emission;
        LogProviderAttempted(sourceId, logSingle, "tmdb", new { releaseId = id, query = title, year, mediaType, tvOnly = tmdbTvOnly });
        var tmdbCandidates = await GetTmdbCandidatesAsync(title, year, mediaType, ct, tmdbTvOnly);

        var scoredAnyCandidates = ScoreCandidates(title, year, unifiedCategory, tmdbCandidates, requirePoster: false);
        var bestAny = scoredAnyCandidates.FirstOrDefault();

        var scoredPosterCandidates = ScoreCandidates(title, year, unifiedCategory, tmdbCandidates, requirePoster: true);
        var bestPoster = scoredPosterCandidates.FirstOrDefault();

        var tmdbStrongThreshold = PosterProviderSelector.GetTmdbStrongThreshold(ambiguity);
        if (ambiguity.IsCommonTitle)
            tmdbStrongThreshold = Math.Max(tmdbStrongThreshold, TmdbCommonTitleThreshold);

        var allowWeak = !ambiguity.IsAmbiguous && !ambiguity.IsCommonTitle && year.HasValue;
        var hasConfidentAny = scoredAnyCandidates.Count > 0 && bestAny.Score >= tmdbStrongThreshold;
        var hasWeakAny = allowWeak && scoredAnyCandidates.Count > 0 &&
            IsWeakTmdbMatchAcceptable(title, year, unifiedCategory, bestAny.Candidate, bestAny.Score);
        var hasWeakPoster = allowWeak && scoredPosterCandidates.Count > 0 &&
            IsWeakTmdbMatchAcceptable(title, year, unifiedCategory, bestPoster.Candidate, bestPoster.Score);

        if (!hasConfidentAny && logSingle)
        {
            // Logs détaillés: top 5 candidats avec leurs scores pour faciliter le diagnostic
            var top5Candidates = scoredAnyCandidates
                .Take(5)
                .Select(c => new
                {
                    tmdbId = c.Candidate.TmdbId,
                    title = c.Candidate.Title,
                    originalTitle = c.Candidate.OriginalTitle,
                    year = c.Candidate.Year,
                    type = c.Candidate.MediaType,
                    score = Math.Round(c.Score, 3),
                    hasPoster = !string.IsNullOrWhiteSpace(c.Candidate.PosterPath)
                })
                .ToList();

            LogActivity(sourceId, "info", "poster_fetch", "TMDB no confident match", new
            {
                releaseId = id,
                searchTitle = title,
                searchYear = year,
                searchMediaType = mediaType,
                threshold = tmdbStrongThreshold,
                bestScore = scoredAnyCandidates.Count == 0 ? (double?)null : Math.Round(bestAny.Score, 3),
                totalCandidates = scoredAnyCandidates.Count,
                topCandidates = top5Candidates
            });
        }

        var tmdbMatch = bestPoster.Candidate is not null &&
            TmdbMatchPolicy.IsAcceptable(title, year, unifiedCategory, ambiguity, bestPoster.Candidate, bestPoster.Score, allowWeak, tmdbStrongThreshold, TmdbCommonTitleThreshold)
                ? bestPoster
                : default;
        var tmdbMatchForIds = bestAny.Candidate is not null &&
            TmdbMatchPolicy.IsAcceptable(title, year, unifiedCategory, ambiguity, bestAny.Candidate, bestAny.Score, allowWeak, tmdbStrongThreshold, TmdbCommonTitleThreshold)
                ? bestAny
                : default;

        int? tvdbIdResolved = tvdbIdStored;
        if (mediaType == "series" && tmdbMatchForIds.Candidate is not null && !tvdbIdResolved.HasValue)
        {
            var tvdbId = await _tmdb.GetTvdbIdAsync(tmdbMatchForIds.Candidate.TmdbId, ct);
            if (tvdbId.HasValue)
            {
                tvdbIdResolved = tvdbId.Value;
                _releases.SaveTvdbId(id, tvdbId.Value);
            }
        }

        if (tmdbMatch.Candidate is not null && !string.IsNullOrWhiteSpace(tmdbMatch.Candidate.PosterPath))
        {
            var selectedPosterPath = tmdbMatch.Candidate.PosterPath!;
            var mediaTypeForImages = string.Equals(tmdbMatch.Candidate.MediaType, "series", StringComparison.OrdinalIgnoreCase)
                ? "series"
                : "movie";
            try
            {
                var preferredPosterPath = await _tmdb.GetPreferredPosterPathAsync(tmdbMatch.Candidate.TmdbId, mediaTypeForImages, ct);
                if (!string.IsNullOrWhiteSpace(preferredPosterPath))
                    selectedPosterPath = preferredPosterPath;
            }
            catch
            {
                // Keep the matched search poster path as fallback if TMDB images lookup fails.
            }

            var bytesTmdb = await _tmdb.DownloadPosterW500Async(selectedPosterPath, ct);
            if (bytesTmdb is not null && bytesTmdb.Length > 0)
            {
                var file = $"tmdb-{tmdbMatch.Candidate.TmdbId}-w500.jpg";
                var full = Path.Combine(PostersDirAbs, file);
                await System.IO.File.WriteAllBytesAsync(full, bytesTmdb, ct);
                _releases.SavePoster(id, tmdbMatch.Candidate.TmdbId, selectedPosterPath, file);
                await UpdateExternalDetailsFromTmdbAsync(id, tmdbMatch.Candidate.TmdbId, mediaType, ct);
                var hash = ComputeSha256Hex(bytesTmdb);
                PosterAudit.UpdateAttemptSuccess(_releases, id, "tmdb", tmdbMatch.Candidate.TmdbId.ToString(CultureInfo.InvariantCulture), null, "w500", hash);
                var tmdbIds = new PosterMatchIds(tmdbMatch.Candidate.TmdbId, tvdbIdResolved, null, null, null);
                var tmdbConfidence = AdjustConfidence(tmdbMatch.Score, ambiguity, unifiedCategory == UnifiedCategory.Emission);
                _matchCache.Upsert(new PosterMatch(
                    fingerprint,
                    mediaType,
                    normalizedTitle,
                    year,
                    season,
                    episode,
                    PosterMatchCacheService.SerializeIds(tmdbIds),
                    tmdbConfidence,
                    "tmdb",
                    file,
                    "tmdb",
                    tmdbMatch.Candidate.TmdbId.ToString(CultureInfo.InvariantCulture),
                    null,
                    "w500",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    null,
                    null));

                if (logSingle)
                {
                    LogActivity(sourceId, "info", "poster_fetch", "Poster fetched", new
                    {
                        releaseId = id,
                        title,
                        year,
                        mediaType,
                        tmdbId = tmdbMatch.Candidate.TmdbId,
                        posterPath = selectedPosterPath,
                        score = tmdbMatch.Score,
                        posterFile = file
                    });
                }

                return new PosterFetchResult(true, 200, new
                {
                    ok = true,
                    tmdbId = tmdbMatch.Candidate.TmdbId,
                    posterPath = selectedPosterPath,
                    posterFile = file,
                    posterUrl = $"/api/posters/release/{id}"
                }, sourceId);
            }
        }

        // TMDB poster failed -> try IGDB as fallback if title looks like a game
// TMDB failed -> try Fanart
        if (tmdbMatchForIds.Candidate is not null)
        {
            var tmdbIdsOnly = new PosterMatchIds(tmdbMatchForIds.Candidate.TmdbId, tvdbIdResolved, null, null, null);
            var tmdbIdsConfidence = AdjustConfidence(tmdbMatchForIds.Score, ambiguity, unifiedCategory == UnifiedCategory.Emission);
            _matchCache.Upsert(new PosterMatch(
                fingerprint,
                mediaType,
                normalizedTitle,
                year,
                season,
                episode,
                PosterMatchCacheService.SerializeIds(tmdbIdsOnly),
                tmdbIdsConfidence,
                "tmdb",
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                null));
        }

        var tmdbIdForFanart = tmdbMatchForIds.Candidate?.TmdbId ?? tmdbIdStored;
        var tvdbIdForFanart = tvdbIdResolved ?? tvdbIdStored;

        if (mediaType == "series" && !tvdbIdForFanart.HasValue && tmdbIdForFanart.HasValue)
        {
            var tvdbId = await _tmdb.GetTvdbIdAsync(tmdbIdForFanart.Value, ct);
            if (tvdbId.HasValue)
            {
                tvdbIdForFanart = tvdbId.Value;
                _releases.SaveTvdbId(id, tvdbId.Value);
            }
        }

        if (!tmdbIdForFanart.HasValue)
        {
            if (logSingle)
            {
                LogActivity(
                    sourceId,
                    "warn",
                    "poster_fetch",
                    "Poster fetch failed: no TMDB match",
                    new { releaseId = id, title, year, mediaType }
                );
            }
            PosterAudit.UpdateAttemptFailure(_releases, id, "tmdb", null, null, null, "no tmdb match");
            return new PosterFetchResult(false, 404, new { error = "no tmdb match", title, year, mediaType }, sourceId);
        }

        if (mediaType == "series" && !tvdbIdForFanart.HasValue)
        {
            if (logSingle)
            {
                LogActivity(
                    sourceId,
                    "warn",
                    "poster_fetch",
                    "Poster fetch failed: missing TVDB id for Fanart",
                    new { releaseId = id, title, year, mediaType, tmdbIdForFanart }
                );
            }
            PosterAudit.UpdateAttemptFailure(_releases, id, "tmdb", tmdbIdForFanart.Value.ToString(CultureInfo.InvariantCulture), null, null, "missing tvdb id");
            return new PosterFetchResult(false, 404, new { error = "missing tvdb id", title, year, mediaType }, sourceId);
        }

        // Récupère la langue originale du candidat TMDB pour améliorer le choix de poster Fanart
        var originalLanguage = tmdbMatchForIds.Candidate?.OriginalLanguage;

        LogFallbackUsed(sourceId, logSingle, "tmdb", "fanart", new
        {
            releaseId = id,
            reason = tmdbMatch.Candidate is not null ? "tmdb_poster_unavailable" :
                     tmdbMatchForIds.Candidate is not null ? "tmdb_match_no_poster" :
                     "tmdb_no_match"
        });
        LogProviderAttempted(sourceId, logSingle, "fanart", new
        {
            releaseId = id,
            tmdbId = tmdbIdForFanart,
            tvdbId = tvdbIdForFanart,
            mediaType
        });

        string? fanartUrl = null;
        if (mediaType == "series")
        {
            if (tvdbIdForFanart.HasValue)
                fanartUrl = await _fanart.GetTvPosterUrlAsync(tvdbIdForFanart.Value, ct, originalLanguage);
        }
        else
        {
            fanartUrl = await _fanart.GetMoviePosterUrlAsync(tmdbIdForFanart.Value, ct, originalLanguage);
        }

        if (string.IsNullOrWhiteSpace(fanartUrl))
        {
            if (logSingle)
            {
                LogActivity(sourceId, "warn", "poster_fetch", "Poster fetch failed: no Fanart match", new { releaseId = id, title, year, mediaType, tmdbIdForFanart, tvdbIdForFanart });
            }
            PosterAudit.UpdateAttemptFailure(_releases, id, "fanart", null, null, null, "no fanart match");
            return new PosterFetchResult(false, 404, new { error = "no fanart match", title, year, mediaType }, sourceId);
        }

        var fanartBytes = await _fanart.DownloadAsync(fanartUrl, ct);
        if (fanartBytes is null || fanartBytes.Length == 0)
        {
            if (logSingle)
            {
                LogActivity(sourceId, "error", "poster_fetch", "Poster fetch failed: Fanart download error", new { releaseId = id, title, year, mediaType, fanartUrl });
            }
            PosterAudit.UpdateAttemptFailure(_releases, id, "fanart", null, null, null, "fanart poster download failed");
            return new PosterFetchResult(false, 502, new { error = "fanart poster download failed" }, sourceId);
        }

        var ext = Path.GetExtension(fanartUrl);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
        var fanartFile = $"fanart-{tmdbIdForFanart.Value}{ext}";
        var fanartFull = Path.Combine(PostersDirAbs, fanartFile);

        await System.IO.File.WriteAllBytesAsync(fanartFull, fanartBytes, ct);

        _releases.SavePoster(id, tmdbIdForFanart.Value, fanartUrl, fanartFile);
        await UpdateExternalDetailsFromTmdbAsync(id, tmdbIdForFanart.Value, mediaType, ct);
        var fanartProviderId = mediaType == "series" && tvdbIdForFanart.HasValue
            ? tvdbIdForFanart.Value.ToString(CultureInfo.InvariantCulture)
            : tmdbIdForFanart.Value.ToString(CultureInfo.InvariantCulture);
        var fanartHash = ComputeSha256Hex(fanartBytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, "fanart", fanartProviderId, null, "original", fanartHash);
        var fanartIds = new PosterMatchIds(tmdbIdForFanart.Value, tvdbIdForFanart, null, null, null);
        var fanartBaseScore = tmdbMatchForIds.Candidate is not null ? tmdbMatchForIds.Score : 0.5f;
        var fanartConfidence = AdjustConfidence(fanartBaseScore, ambiguity, unifiedCategory == UnifiedCategory.Emission);
        _matchCache.Upsert(new PosterMatch(
            fingerprint,
            mediaType,
            normalizedTitle,
            year,
            season,
            episode,
            PosterMatchCacheService.SerializeIds(fanartIds),
            fanartConfidence,
            "fanart",
            fanartFile,
            "fanart",
            fanartProviderId,
            null,
            "original",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            null,
            null));

        if (logSingle)
        {
            LogActivity(sourceId, "info", "poster_fetch", "Poster fetched (Fanart)", new
            {
                releaseId = id,
                title,
                year,
                mediaType,
                tmdbIdForFanart.Value,
                fanartUrl,
                posterFile = fanartFile
            });
        }

        return new PosterFetchResult(true, 200, new
        {
            ok = true,
            tmdbId = tmdbIdForFanart.Value,
            posterPath = fanartUrl,
            posterFile = fanartFile,
            posterUrl = $"/api/posters/release/{id}"
        }, sourceId);
    }

    private async Task<PosterFetchResult> FetchAnimeCategoryBranchAsync(
        PosterFetchRoutingContext context,
        CancellationToken ct)
    {
        var id = context.ReleaseId;
        var title = context.Title;
        var year = context.Year;
        var sourceId = context.SourceId;
        var logSingle = context.LogSingle;

        LogProviderAttempted(sourceId, logSingle, ExternalProviderKeys.Jikan, new { releaseId = id, query = title, year });
        var match = await _jikan.SearchAnimeAsync(title, year, ct);
        if (match is null)
        {
            if (logSingle)
                LogActivity(sourceId, "warn", "poster_fetch", "Poster fetch failed: no Jikan match", new { releaseId = id, title, year });

            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.Jikan, null, null, null, "no jikan match");
            return new PosterFetchResult(false, 404, new { error = "no jikan match", title, year }, sourceId);
        }

        if (string.IsNullOrWhiteSpace(match.ImageUrl))
        {
            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.Jikan, match.MalId.ToString(CultureInfo.InvariantCulture), null, null, "missing jikan image");
            return new PosterFetchResult(false, 404, new { error = "missing jikan image", title, year }, sourceId);
        }

        var bytes = await _jikan.DownloadImageAsync(match.ImageUrl, ct);
        if (bytes is null || bytes.Length == 0)
        {
            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.Jikan, match.MalId.ToString(CultureInfo.InvariantCulture), null, null, "jikan image download failed");
            return new PosterFetchResult(false, 502, new { error = "jikan image download failed" }, sourceId);
        }

        var ext = InferFileExtensionFromUrl(match.ImageUrl, ".jpg");
        var file = $"jikan-{match.MalId}{ext}";
        await SavePosterFileAsync(file, bytes, ct);

        _releases.SavePoster(id, null, match.ImageUrl, file);
        _releases.UpdateExternalDetails(
            id,
            ExternalProviderKeys.Jikan,
            match.MalId.ToString(CultureInfo.InvariantCulture),
            match.Title,
            match.Synopsis,
            null,
            match.Genres,
            match.Year.HasValue ? $"{match.Year.Value:0000}-01-01" : null,
            null,
            match.Rating,
            null,
            null,
            null,
            null);

        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, ExternalProviderKeys.Jikan, match.MalId.ToString(CultureInfo.InvariantCulture), null, "original", hash);

        _matchCache.Upsert(new PosterMatch(
            context.Fingerprint,
            context.MediaType,
            context.NormalizedTitle,
            context.Year,
            context.Season,
            context.Episode,
            PosterMatchCacheService.SerializeIds(new PosterMatchIds(null, null, null, null, null)),
            0.70,
            ExternalProviderKeys.Jikan,
            file,
            ExternalProviderKeys.Jikan,
            match.MalId.ToString(CultureInfo.InvariantCulture),
            null,
            "original",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            null,
            null));

        if (logSingle)
        {
            LogActivity(sourceId, "info", "poster_fetch", "Poster fetched (Jikan)", new
            {
                releaseId = id,
                title,
                year,
                malId = match.MalId,
                posterFile = file
            });
        }

        return new PosterFetchResult(true, 200, new
        {
            ok = true,
            provider = ExternalProviderKeys.Jikan,
            providerId = match.MalId,
            posterFile = file,
            posterUrl = $"/api/posters/release/{id}"
        }, sourceId);
    }

    private async Task<PosterFetchResult> FetchAudioCategoryBranchAsync(
        PosterFetchRoutingContext context,
        CancellationToken ct)
    {
        var id = context.ReleaseId;
        var title = context.Title;
        var year = context.Year;
        var sourceId = context.SourceId;
        var logSingle = context.LogSingle;

        var (artist, trackOrAlbum) = ParseAudioQuery(title);
        var query = string.IsNullOrWhiteSpace(trackOrAlbum) ? title : trackOrAlbum;
        LogProviderAttempted(sourceId, logSingle, ExternalProviderKeys.TheAudioDb, new { releaseId = id, query, artist, year });

        var match = await _theAudioDb.SearchAudioAsync(query, artist, year, ct);
        if (match is null)
        {
            if (logSingle)
                LogActivity(sourceId, "warn", "poster_fetch", "Poster fetch failed: no TheAudioDB match", new { releaseId = id, title, year });

            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.TheAudioDb, null, null, null, "no theaudiodb match");
            return new PosterFetchResult(false, 404, new { error = "no theaudiodb match", title, year }, sourceId);
        }

        if (string.IsNullOrWhiteSpace(match.PosterUrl))
        {
            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.TheAudioDb, match.ProviderId, null, null, "missing theaudiodb image");
            return new PosterFetchResult(false, 404, new { error = "missing theaudiodb image", title, year }, sourceId);
        }

        var bytes = await _theAudioDb.DownloadImageAsync(match.PosterUrl, ct);
        if (bytes is null || bytes.Length == 0)
        {
            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.TheAudioDb, match.ProviderId, null, null, "theaudiodb image download failed");
            return new PosterFetchResult(false, 502, new { error = "theaudiodb image download failed" }, sourceId);
        }

        var ext = InferFileExtensionFromUrl(match.PosterUrl, ".jpg");
        var file = $"theaudiodb-{match.ProviderId}{ext}";
        await SavePosterFileAsync(file, bytes, ct);

        _releases.SavePoster(id, null, match.PosterUrl, file);
        _releases.UpdateExternalDetails(
            id,
            ExternalProviderKeys.TheAudioDb,
            match.ProviderId,
            match.Title,
            match.Description,
            null,
            match.Genre,
            match.Released,
            null,
            match.Rating,
            null,
            null,
            null,
            match.Artist);

        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, ExternalProviderKeys.TheAudioDb, match.ProviderId, null, "original", hash);
        _matchCache.Upsert(new PosterMatch(
            context.Fingerprint,
            context.MediaType,
            context.NormalizedTitle,
            context.Year,
            context.Season,
            context.Episode,
            null,
            0.65,
            ExternalProviderKeys.TheAudioDb,
            file,
            ExternalProviderKeys.TheAudioDb,
            match.ProviderId,
            null,
            "original",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            null,
            null));

        return new PosterFetchResult(true, 200, new
        {
            ok = true,
            provider = ExternalProviderKeys.TheAudioDb,
            providerId = match.ProviderId,
            posterFile = file,
            posterUrl = $"/api/posters/release/{id}"
        }, sourceId);
    }

    private async Task<PosterFetchResult> FetchGenericCategoryBranchAsync(
        PosterFetchRoutingContext context,
        CancellationToken ct)
    {
        return context.UnifiedCategory switch
        {
            UnifiedCategory.Book => await FetchBookBranchAsync(context, ct),
            UnifiedCategory.Comic => await FetchComicBranchAsync(context, ct),
            _ => new PosterFetchResult(false, 400, new { error = "unsupported generic category" }, context.SourceId)
        };
    }

    private async Task<PosterFetchResult> FetchBookBranchAsync(
        PosterFetchRoutingContext context,
        CancellationToken ct)
    {
        var id = context.ReleaseId;
        var title = context.Title;
        var year = context.Year;
        var sourceId = context.SourceId;
        var logSingle = context.LogSingle;

        var isbn = TryExtractIsbn(title);
        LogProviderAttempted(sourceId, logSingle, ExternalProviderKeys.GoogleBooks, new { releaseId = id, query = title, isbn });
        var match = await _googleBooks.SearchBookAsync(title, isbn, ct);
        if (match is null)
        {
            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.GoogleBooks, null, null, null, "no google books match");
            return new PosterFetchResult(false, 404, new { error = "no google books match", title, year }, sourceId);
        }

        if (string.IsNullOrWhiteSpace(match.ThumbnailUrl))
        {
            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.GoogleBooks, match.VolumeId, null, null, "missing google books image");
            return new PosterFetchResult(false, 404, new { error = "missing google books image", title, year }, sourceId);
        }

        var bytes = await _googleBooks.DownloadImageAsync(match.ThumbnailUrl, ct);
        if (bytes is null || bytes.Length == 0)
        {
            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.GoogleBooks, match.VolumeId, null, null, "google books image download failed");
            return new PosterFetchResult(false, 502, new { error = "google books image download failed" }, sourceId);
        }

        var ext = InferFileExtensionFromUrl(match.ThumbnailUrl, ".jpg");
        var file = $"googlebooks-{SanitizeForFile(match.VolumeId)}{ext}";
        await SavePosterFileAsync(file, bytes, ct);

        _releases.SavePoster(id, null, match.ThumbnailUrl, file);
        _releases.UpdateExternalDetails(
            id,
            ExternalProviderKeys.GoogleBooks,
            match.VolumeId,
            match.Title,
            match.Description,
            null,
            match.Genres,
            match.PublishedDate,
            null,
            match.Rating,
            match.RatingCount,
            null,
            null,
            null);

        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, ExternalProviderKeys.GoogleBooks, match.VolumeId, null, "thumb", hash);
        _matchCache.Upsert(new PosterMatch(
            context.Fingerprint,
            context.MediaType,
            context.NormalizedTitle,
            context.Year,
            context.Season,
            context.Episode,
            null,
            0.65,
            ExternalProviderKeys.GoogleBooks,
            file,
            ExternalProviderKeys.GoogleBooks,
            match.VolumeId,
            null,
            "thumb",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            null,
            null));

        return new PosterFetchResult(true, 200, new
        {
            ok = true,
            provider = ExternalProviderKeys.GoogleBooks,
            providerId = match.VolumeId,
            posterFile = file,
            posterUrl = $"/api/posters/release/{id}"
        }, sourceId);
    }

    private async Task<PosterFetchResult> FetchComicBranchAsync(
        PosterFetchRoutingContext context,
        CancellationToken ct)
    {
        var id = context.ReleaseId;
        var title = context.Title;
        var year = context.Year;
        var sourceId = context.SourceId;
        var logSingle = context.LogSingle;

        LogProviderAttempted(sourceId, logSingle, ExternalProviderKeys.ComicVine, new { releaseId = id, query = title, year });
        var match = await _comicVine.SearchComicAsync(title, year, ct);
        if (match is null)
        {
            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.ComicVine, null, null, null, "no comic vine match");
            return new PosterFetchResult(false, 404, new { error = "no comic vine match", title, year }, sourceId);
        }

        if (string.IsNullOrWhiteSpace(match.CoverUrl))
        {
            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.ComicVine, match.ProviderId, null, null, "missing comic vine image");
            return new PosterFetchResult(false, 404, new { error = "missing comic vine image", title, year }, sourceId);
        }

        var bytes = await _comicVine.DownloadImageAsync(match.CoverUrl, ct);
        if (bytes is null || bytes.Length == 0)
        {
            PosterAudit.UpdateAttemptFailure(_releases, id, ExternalProviderKeys.ComicVine, match.ProviderId, null, null, "comic vine image download failed");
            return new PosterFetchResult(false, 502, new { error = "comic vine image download failed" }, sourceId);
        }

        var ext = InferFileExtensionFromUrl(match.CoverUrl, ".jpg");
        var file = $"comicvine-{match.ProviderId}{ext}";
        await SavePosterFileAsync(file, bytes, ct);

        _releases.SavePoster(id, null, match.CoverUrl, file);
        _releases.UpdateExternalDetails(
            id,
            ExternalProviderKeys.ComicVine,
            match.ProviderId,
            match.Title,
            match.Description,
            null,
            null,
            match.ReleaseDate,
            null,
            null,
            null,
            null,
            null,
            null);

        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, ExternalProviderKeys.ComicVine, match.ProviderId, null, "original", hash);
        _matchCache.Upsert(new PosterMatch(
            context.Fingerprint,
            context.MediaType,
            context.NormalizedTitle,
            context.Year,
            context.Season,
            context.Episode,
            null,
            0.65,
            ExternalProviderKeys.ComicVine,
            file,
            ExternalProviderKeys.ComicVine,
            match.ProviderId,
            null,
            "original",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            null,
            null));

        return new PosterFetchResult(true, 200, new
        {
            ok = true,
            provider = ExternalProviderKeys.ComicVine,
            providerId = match.ProviderId,
            posterFile = file,
            posterUrl = $"/api/posters/release/{id}"
        }, sourceId);
    }

    private async Task<PosterFetchResult?> TryReuseFromMatchCacheAsync(
        long id,
        string title,
        int? year,
        string mediaType,
        UnifiedCategory category,
        PosterTitleKey titleKey,
        string fingerprint,
        TitleAmbiguityResult ambiguity,
        PosterMatchIds knownIds,
        bool tvmazeEnabled,
        CancellationToken ct,
        bool logSingle,
        long? sourceId)
    {
        var cached = _matchCache.TryGet(fingerprint);

        if (cached is null && !year.HasValue)
        {
            var fallback = _matchCache.TryGetByTitleKey(mediaType, titleKey.NormalizedTitle, null);
            if (fallback is not null)
            {
                var fallbackIds = PosterMatchCacheService.DeserializeIds(fallback.IdsJson);
                var allow = fallback.Confidence >= HighConfidenceThreshold ||
                            (fallbackIds is not null && fallbackIds.Overlaps(knownIds));
                if (ambiguity.IsAmbiguous)
                    allow = allow && (fallback.Confidence >= 0.90 || (fallbackIds is not null && fallbackIds.Overlaps(knownIds)));
                if (allow)
                    cached = fallback;
            }
        }

        if (cached is null) return null;

        _matchCache.TouchSeen(cached.Fingerprint);
        var cachedIds = PosterMatchCacheService.DeserializeIds(cached.IdsJson);

        if (!string.IsNullOrWhiteSpace(cached.PosterFile))
        {
            var cachedPath = Path.Combine(PostersDirAbs, cached.PosterFile);
            if (System.IO.File.Exists(cachedPath))
            {
                var tmdbIdResolved = cachedIds?.TmdbId;
                if (!tmdbIdResolved.HasValue && cachedIds?.TvdbId is int cachedTvdbId)
                    tmdbIdResolved = await TryResolveTmdbFromTvdbAsync(cachedTvdbId, ct);

                _releases.SavePoster(id, tmdbIdResolved, null, cached.PosterFile);
                if (tmdbIdResolved.HasValue)
                    _releases.SaveTmdbId(id, tmdbIdResolved.Value);
                if (cachedIds?.TvdbId is not null)
                    _releases.SaveTvdbId(id, cachedIds.TvdbId.Value);

                var provider = cached.PosterProvider ?? cached.MatchSource;
                PosterAudit.UpdateAttemptSuccess(_releases, id, provider, cached.PosterProviderId, cached.PosterLang, cached.PosterSize, null);

                if (logSingle)
                {
                    LogActivity(sourceId, "info", "poster_fetch", "Poster reused from shared cache", new
                    {
                        releaseId = id,
                        title,
                        cacheFingerprint = cached.Fingerprint,
                        posterFile = cached.PosterFile
                    });
                }

                return new PosterFetchResult(true, 200, new
                {
                    ok = true,
                    cached = true,
                    posterFile = cached.PosterFile,
                    posterUrl = $"/api/posters/release/{id}"
                }, sourceId);
            }

            _matchCache.RecordError(cached.Fingerprint, "cached poster missing");
        }

        if (tvmazeEnabled && cachedIds is not null && cachedIds.TvmazeId is not null)
        {
            _matchCache.RecordAttempt(cached.Fingerprint);
            var show = await _tvmaze.GetShowAsync(cachedIds.TvmazeId.Value, ct);
            if (show is null) return null;

            var imageUrl = show.ImageOriginal ?? show.ImageMedium;
            if (string.IsNullOrWhiteSpace(imageUrl)) return null;

            var bytes = await _tvmaze.DownloadImageAsync(imageUrl, ct);
            if (bytes is null || bytes.Length == 0) return null;

            var size = show.ImageOriginal == imageUrl ? "original" : "medium";
            var ext = Path.GetExtension(new Uri(imageUrl).AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
            var file = $"tvmaze-{show.Id}-{size}{ext}";
            var full = Path.Combine(PostersDirAbs, file);
            await System.IO.File.WriteAllBytesAsync(full, bytes, ct);

            var tmdbIdResolved = cachedIds.TmdbId;
            if (show.TvdbId.HasValue)
                _releases.SaveTvdbId(id, show.TvdbId.Value);
            if (!tmdbIdResolved.HasValue && show.TvdbId.HasValue)
                tmdbIdResolved = await TryResolveTmdbFromTvdbAsync(show.TvdbId.Value, ct);
            if (tmdbIdResolved.HasValue)
                _releases.SaveTmdbId(id, tmdbIdResolved.Value);

            _releases.SavePoster(id, tmdbIdResolved, imageUrl, file);

            var hash = ComputeSha256Hex(bytes);
            PosterAudit.UpdateAttemptSuccess(_releases, id, "tvmaze", show.Id.ToString(CultureInfo.InvariantCulture), null, size, hash);

            var mergedIds = new PosterMatchIds(
                tmdbIdResolved,
                show.TvdbId ?? cachedIds.TvdbId,
                show.Id,
                cachedIds.IgdbId,
                show.ImdbId ?? cachedIds.ImdbId);

            var confidence = AdjustConfidence((float)Math.Max(cached.Confidence, 0.6f), ambiguity, category == UnifiedCategory.Emission);
            _matchCache.Upsert(new PosterMatch(
                cached.Fingerprint,
                cached.MediaType,
                cached.NormalizedTitle,
                cached.Year,
                cached.Season,
                cached.Episode,
                PosterMatchCacheService.SerializeIds(mergedIds),
                confidence,
                cached.MatchSource ?? "tvmaze",
                file,
                "tvmaze",
                show.Id.ToString(CultureInfo.InvariantCulture),
                null,
                size,
                cached.CreatedTs,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                null));

            if (logSingle)
            {
                LogActivity(sourceId, "info", "poster_fetch", "Poster fetched (TVmaze cached)", new
                {
                    releaseId = id,
                    title,
                    tvmazeId = show.Id,
                    posterFile = file
                });
            }

            return new PosterFetchResult(true, 200, new
            {
                ok = true,
                tvmazeId = show.Id,
                posterFile = file,
                posterUrl = $"/api/posters/release/{id}"
            }, sourceId);
        }

        return null;
    }

    private sealed record TvMazeAttempt(PosterFetchResult? Result, bool HadCandidates);

    private async Task<TvMazeAttempt> TryFetchFromTvMazeAsync(
        long id,
        string title,
        int? year,
        UnifiedCategory category,
        TitleAmbiguityResult ambiguity,
        PosterMatchIds knownIds,
        PosterTitleKey titleKey,
        string fingerprint,
        CancellationToken ct,
        bool logSingle,
        long? sourceId)
    {
        if (category != UnifiedCategory.Emission && ambiguity.SignificantTokenCount < 2)
            return new TvMazeAttempt(null, false);
        if (category != UnifiedCategory.Emission && ambiguity.IsCommonTitle && !year.HasValue)
            return new TvMazeAttempt(null, false);

        var candidates = await _tvmaze.SearchShowsAsync(title, ct, TmdbCandidateLimit);
        if (candidates.Count == 0) return new TvMazeAttempt(null, false);

        var scored = candidates
            .Select(c => (Candidate: c, Score: TvMazeScorer.ScoreCandidate(title, year, category, c, knownIds)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Candidate.PremieredYear.HasValue && year.HasValue && x.Candidate.PremieredYear == year)
            .ToList();

        var best = scored.First();
        var threshold = PosterProviderSelector.GetTvMazeThreshold(ambiguity, category);
        if (ambiguity.IsAmbiguous)
            threshold = Math.Max(threshold, TvMazeAmbiguousThreshold);
        if (category == UnifiedCategory.Emission)
            threshold = Math.Max(threshold, TvMazeEmissionThreshold);

        if (best.Score < threshold) return new TvMazeAttempt(null, true);

        if (ambiguity.IsCommonTitle)
        {
            if (!year.HasValue || !best.Candidate.PremieredYear.HasValue || best.Candidate.PremieredYear != year)
                return new TvMazeAttempt(null, true);
            if (best.Score < TmdbCommonTitleThreshold)
                return new TvMazeAttempt(null, true);
        }

        var imageUrl = best.Candidate.ImageOriginal ?? best.Candidate.ImageMedium;
        if (string.IsNullOrWhiteSpace(imageUrl)) return new TvMazeAttempt(null, true);

        var bytes = await _tvmaze.DownloadImageAsync(imageUrl, ct);
        if (bytes is null || bytes.Length == 0) return new TvMazeAttempt(null, true);

        var size = best.Candidate.ImageOriginal == imageUrl ? "original" : "medium";
        var ext = Path.GetExtension(new Uri(imageUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
        var file = $"tvmaze-{best.Candidate.Id}-{size}{ext}";
        var full = Path.Combine(PostersDirAbs, file);
        await System.IO.File.WriteAllBytesAsync(full, bytes, ct);

        int? tmdbIdResolved = null;
        if (best.Candidate.TvdbId.HasValue)
            _releases.SaveTvdbId(id, best.Candidate.TvdbId.Value);
        if (best.Candidate.TvdbId.HasValue)
            tmdbIdResolved = await TryResolveTmdbFromTvdbAsync(best.Candidate.TvdbId.Value, ct);
        if (tmdbIdResolved.HasValue)
            _releases.SaveTmdbId(id, tmdbIdResolved.Value);

        _releases.SavePoster(id, tmdbIdResolved, imageUrl, file);

        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, "tvmaze", best.Candidate.Id.ToString(CultureInfo.InvariantCulture), null, size, hash);

        var ids = new PosterMatchIds(tmdbIdResolved, best.Candidate.TvdbId, best.Candidate.Id, null, best.Candidate.ImdbId);
        var confidence = AdjustConfidence(best.Score, ambiguity, category == UnifiedCategory.Emission);
        _matchCache.Upsert(new PosterMatch(
            fingerprint,
            titleKey.MediaType,
            titleKey.NormalizedTitle,
            titleKey.Year,
            titleKey.Season,
            titleKey.Episode,
            PosterMatchCacheService.SerializeIds(ids),
            confidence,
            "tvmaze",
            file,
            "tvmaze",
            best.Candidate.Id.ToString(CultureInfo.InvariantCulture),
            null,
            size,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            null,
            null));

        if (logSingle)
        {
            LogActivity(sourceId, "info", "poster_fetch", "Poster fetched (TVmaze)", new
            {
                releaseId = id,
                title,
                year,
                tvmazeId = best.Candidate.Id,
                posterFile = file,
                score = best.Score
            });
        }

        var result = new PosterFetchResult(true, 200, new
        {
            ok = true,
            tmdbId = tmdbIdResolved,
            tvmazeId = best.Candidate.Id,
            posterFile = file,
            posterUrl = $"/api/posters/release/{id}"
        }, sourceId);
        return new TvMazeAttempt(result, true);
    }

    private async Task<int?> TryResolveTmdbFromTvdbAsync(int tvdbId, CancellationToken ct)
    {
        if (tvdbId <= 0) return null;
        try
        {
            return await _tmdb.GetTvTmdbIdByTvdbIdAsync(tvdbId, ct);
        }
        catch
        {
            return null;
        }
    }

    private static double AdjustConfidence(float score, TitleAmbiguityResult ambiguity, bool isEmission)
    {
        var confidence = score;
        if (ambiguity.IsAmbiguous) confidence -= 0.1f;
        if (isEmission) confidence -= 0.05f;
        return Math.Clamp(confidence, 0f, 1f);
    }

    private static double ComputeReuseConfidence(TitleAmbiguityResult ambiguity, int? tmdbId, int? tvdbId)
    {
        var confidence = ambiguity.IsAmbiguous ? 0.55 : 0.75;
        if (tmdbId.HasValue || tvdbId.HasValue)
            confidence += 0.1;
        return Math.Clamp(confidence, 0f, 0.95f);
    }


    private async Task<List<(TmdbClient.SearchResult Candidate, float Score)>> GetScoredTmdbCandidatesAsync(
        string title,
        int? year,
        UnifiedCategory category,
        string mediaType,
        CancellationToken ct,
        bool requirePoster,
        bool tvOnly = false)
    {
        var candidates = await GetTmdbCandidatesAsync(title, year, mediaType, ct, tvOnly);
        return ScoreCandidates(title, year, category, candidates, requirePoster);
    }

    private async Task<List<TmdbClient.SearchResult>> GetTmdbCandidatesAsync(
        string title,
        int? year,
        string mediaType,
        CancellationToken ct,
        bool tvOnly = false)
    {
        var results = new List<TmdbClient.SearchResult>();

        if (tvOnly)
        {
            results.AddRange(await _tmdb.SearchTvListAsync(title, year, ct, TmdbCandidateLimit));
            if (year.HasValue)
                results.AddRange(await _tmdb.SearchTvListAsync(title, null, ct, TmdbCandidateLimit));
            return DedupCandidates(results);
        }

        if (mediaType == "series")
        {
            results.AddRange(await _tmdb.SearchTvListAsync(title, year, ct, TmdbCandidateLimit));
            if (year.HasValue)
                results.AddRange(await _tmdb.SearchTvListAsync(title, null, ct, TmdbCandidateLimit));

            results.AddRange(await _tmdb.SearchMovieListAsync(title, year, ct, TmdbCandidateLimit));
            if (year.HasValue)
                results.AddRange(await _tmdb.SearchMovieListAsync(title, null, ct, TmdbCandidateLimit));
        }
        else
        {
            results.AddRange(await _tmdb.SearchMovieListAsync(title, year, ct, TmdbCandidateLimit));
            if (year.HasValue)
                results.AddRange(await _tmdb.SearchMovieListAsync(title, null, ct, TmdbCandidateLimit));

            results.AddRange(await _tmdb.SearchTvListAsync(title, year, ct, TmdbCandidateLimit));
            if (year.HasValue)
                results.AddRange(await _tmdb.SearchTvListAsync(title, null, ct, TmdbCandidateLimit));
        }

        return DedupCandidates(results);
    }

    private static List<(TmdbClient.SearchResult Candidate, float Score)> ScoreCandidates(
        string title,
        int? year,
        UnifiedCategory category,
        IEnumerable<TmdbClient.SearchResult> candidates,
        bool requirePoster)
    {
        return candidates
            .Where(c => c.TmdbId > 0)
            .Where(c => !requirePoster || !string.IsNullOrWhiteSpace(c.PosterPath))
            .Select(c => (Candidate: c, Score: MatchScorer.ScoreCandidate(
                title,
                year,
                category,
                c.Title,
                c.OriginalTitle,
                c.Year,
                c.MediaType
            )))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Candidate.Year.HasValue && year.HasValue && x.Candidate.Year == year)
            .ToList();
    }

    private static List<TmdbClient.SearchResult> DedupCandidates(IEnumerable<TmdbClient.SearchResult> items)
    {
        var seen = new HashSet<int>();
        var list = new List<TmdbClient.SearchResult>();
        foreach (var item in items)
        {
            if (item.TmdbId <= 0) continue;
            if (!seen.Add(item.TmdbId)) continue;
            list.Add(item);
        }
        return list;
    }

    private static bool IsWeakTmdbMatchAcceptable(
        string title,
        int? year,
        UnifiedCategory category,
        TmdbClient.SearchResult candidate,
        float score)
    {
        if (score < TmdbWeakScoreThreshold) return false;
        if (!year.HasValue || !candidate.Year.HasValue || candidate.Year != year) return false;
        var expectedMediaType = UnifiedCategoryMappings.ToMediaType(category);
        if (!string.IsNullOrWhiteSpace(expectedMediaType) &&
            expectedMediaType != "unknown" &&
            !string.Equals(expectedMediaType, candidate.MediaType, StringComparison.OrdinalIgnoreCase))
            return false;

        var overlap = CountSignificantTokenOverlap(title, candidate.Title, candidate.OriginalTitle);
        return overlap > 0;
    }

    private static int CountSignificantTokenOverlap(string query, string candidate, string? originalCandidate)
        => TitleTokenHelper.CountSignificantTokenOverlap(query, candidate, originalCandidate);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Anglais
        "the", "and", "with", "from", "into", "over", "under", "without", "of", "to", "in", "on", "for", "by",
        "a", "an", "is", "it", "its", "at", "as", "be", "but", "or", "not", "this", "that", "was", "are",
        // Français
        "le", "la", "les", "des", "du", "de", "au", "aux", "un", "une", "et", "ou", "en", "sur", "dans",
        "ce", "cette", "ces", "son", "sa", "ses", "mon", "ma", "mes", "ton", "ta", "tes", "leur", "leurs",
        "qui", "que", "quoi", "dont", "avec", "pour", "par", "sans", "sous", "vers", "chez", "entre",
        "comme", "mais", "donc", "car", "ni", "ne", "pas", "plus", "moins", "tres", "bien", "tout", "tous",
        // Allemand
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einer", "eines", "einem", "einen",
        "und", "oder", "aber", "doch", "wenn", "weil", "dass", "ob", "als", "wie", "wo", "was", "wer",
        "mit", "von", "zu", "bei", "nach", "vor", "aus", "um", "auf", "an", "im", "am",
        "ist", "sind", "war", "waren", "hat", "haben", "wird", "werden", "kann", "konnen",
        "nicht", "auch", "noch", "nur", "schon", "sehr", "mehr", "viel", "alle", "alles",
        // Espagnol
        "el", "los", "lo", "las", "unos", "unas", "y", "o", "pero", "sino", "porque",
        "cual", "quien", "donde", "cuando", "como", "con", "sin", "para", "por", "sobre",
        "del", "al", "se", "su", "sus", "mi", "mis", "tu", "tus", "es", "son", "esta",
        "este", "esto", "eso", "ese", "no", "si", "muy", "mas", "menos", "todo", "todos", "nada",
        // Italien
        "il", "gli", "i", "uno", "ed", "ma", "che", "chi", "cui", "dove",
        "per", "tra", "fra", "di", "da", "della", "dei", "delle", "nel", "nella",
        "non", "molto", "poco", "tutto", "tutti", "questo", "quello",
        // Portugais
        "os", "um", "uma", "uns", "umas", "ao", "aos", "do", "dos", "da", "das", "nas",
        "em", "sem", "ate", "desde", "contra",
        "eu", "ele", "ela", "nos", "vos", "eles", "elas", "meu", "minha", "seu", "sua",
        "nao", "sim", "muito", "pouco", "bem", "mal"
    };

    private static bool IsStopWord(string token)
    {
        return StopWords.Contains(token);
    }

    private async Task UpdateExternalDetailsFromTmdbAsync(long id, int tmdbId, string mediaType, CancellationToken ct)
    {
        if (tmdbId <= 0) return;

        TmdbClient.DetailsResult? details = null;
        if (mediaType == "series")
        {
            details = await _tmdb.GetTvDetailsAsync(tmdbId, ct);
        }
        else if (mediaType == "movie")
        {
            details = await _tmdb.GetMovieDetailsAsync(tmdbId, ct);
        }
        else
        {
            details = await _tmdb.GetMovieDetailsAsync(tmdbId, ct);
            details ??= await _tmdb.GetTvDetailsAsync(tmdbId, ct);
        }

        if (details is null) return;

        _releases.UpdateExternalDetails(
            id,
            "tmdb",
            tmdbId.ToString(CultureInfo.InvariantCulture),
            details.Title,
            details.Overview,
            details.Tagline,
            details.Genres,
            details.ReleaseDate,
            details.RuntimeMinutes,
            details.Rating,
            details.Votes,
            details.Directors,
            details.Writers,
            details.Cast
        );
    }

    private async Task UpdateExternalDetailsFromIgdbAsync(long id, int igdbId, CancellationToken ct)
    {
        if (igdbId <= 0) return;
        var details = await _igdb.GetGameDetailsAsync(igdbId, ct);
        if (details is null) return;

        _releases.UpdateExternalDetails(
            id,
            "igdb",
            igdbId.ToString(CultureInfo.InvariantCulture),
            details.Title,
            details.Summary,
            null,
            details.Genres,
            details.ReleaseDate,
            null,
            details.Rating,
            details.Votes,
            null,
            null,
            null
        );
    }

    private async Task SavePosterFileAsync(string fileName, byte[] bytes, CancellationToken ct)
    {
        var full = Path.Combine(PostersDirAbs, fileName);
        await System.IO.File.WriteAllBytesAsync(full, bytes, ct);
    }

    private static string InferFileExtensionFromUrl(string? url, string fallback)
    {
        if (string.IsNullOrWhiteSpace(url))
            return fallback;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return fallback;

        var ext = Path.GetExtension(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(ext) ? fallback : ext;
    }

    private static (string? artist, string title) ParseAudioQuery(string input)
    {
        var raw = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return (null, "");

        var separators = new[] { " - ", " – ", " — ", " | ", " : " };
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

    private static string? TryExtractIsbn(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var normalized = input.Replace("-", "", StringComparison.Ordinal);
        var matches = System.Text.RegularExpressions.Regex.Matches(normalized, @"\b(?:97[89]\d{10}|\d{9}[\dXx])\b");
        if (matches.Count == 0)
            return null;

        return matches[0].Value;
    }

    private static string SanitizeForFile(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Guid.NewGuid().ToString("N");

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? InferIgdbSize(string coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return null;
        var lower = coverUrl.ToLowerInvariant();
        if (lower.Contains("cover_big")) return "cover_big";
        if (lower.Contains("t_cover")) return "cover";
        return null;
    }

    private static string QuerySanitizeGame(string titleClean)
    {
        if (string.IsNullOrWhiteSpace(titleClean)) return titleClean;

        var sb = new System.Text.StringBuilder(titleClean.Length);
        foreach (var ch in titleClean)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        var rawTokens = sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tokens = new List<string>();
        foreach (var token in rawTokens)
        {
            var lower = token.ToLowerInvariant();
            if (IsGameNoiseToken(lower)) continue;
            if (IsGameOsToken(lower)) continue;
            if (IsGameYearToken(lower)) continue;
            tokens.Add(token);
        }

        while (tokens.Count > 0 && IsTrailingBuildToken(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);

        var result = string.Join(" ", tokens);
        return string.IsNullOrWhiteSpace(result) ? titleClean : result;
    }

    private static bool IsGameNoiseToken(string token)
    {
        return token is "build" or "fix" or "patch" or "update" or "hotfix" ||
               (token.StartsWith("build") && token.Length > 5 && token[5..].All(char.IsDigit)) ||
               (token.StartsWith("patch") && token.Length > 5 && token[5..].All(char.IsDigit)) ||
               (token.StartsWith("update") && token.Length > 6 && token[6..].All(char.IsDigit)) ||
               (token.StartsWith("hotfix") && token.Length > 6 && token[6..].All(char.IsDigit));
    }

    private static bool IsGameOsToken(string token)
    {
        if (token is "win" or "windows" or "linux" or "mac" or "osx" or "macos")
            return true;

        if (token.StartsWith("win") && token.Length > 3 && token[3..].All(char.IsDigit))
            return true;
        if (token.StartsWith("windows") && token.Length > 7 && token[7..].All(char.IsDigit))
            return true;
        if (token.StartsWith("linux") && token.Length > 5 && token[5..].All(char.IsDigit))
            return true;

        return false;
    }

    private static bool IsGameYearToken(string token)
    {
        if (token.Length != 4) return false;
        if (!token.All(char.IsDigit)) return false;
        return token.StartsWith("19") || token.StartsWith("20");
    }

    private static bool IsTrailingBuildToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!token.Any(char.IsDigit)) return false;
        if (IsGameYearToken(token)) return true;
        return token.Length >= 3;
    }
}


