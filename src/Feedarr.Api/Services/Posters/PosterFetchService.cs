using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using Feedarr.Api.Services.MusicBrainz;
using Feedarr.Api.Services.OpenLibrary;
using Feedarr.Api.Services.Rawg;
using Feedarr.Api.Services.TheAudioDb;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.TvMaze;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services.Posters;

public sealed record PosterFetchResult(bool Ok, int StatusCode, object? Body, long? SourceId)
{
    internal bool PublicationCommitted { get; init; }
}

public sealed partial class PosterFetchService
{
    private readonly ReleaseRepository _releases;
    private readonly ActivityRepository _activity;
    private readonly TmdbClient _tmdb;
    private readonly FanartClient _fanart;
    private readonly IgdbClient _igdb;
    private readonly TvMazeClient _tvmaze;
    private readonly JikanClient _jikan;
    private readonly TheAudioDbClient _theAudioDb;
    private readonly GoogleBooksClient _googleBooks;
    private readonly OpenLibraryClient _openLibrary;
    private readonly RawgClient _rawg;
    private readonly ComicVineClient _comicVine;
    private readonly MusicBrainzClient _musicBrainz;
    private readonly PosterThumbService _thumbService;
    private readonly IPosterThumbQueue _thumbQueue;
    private readonly IExternalProviderLimiter _externalProviderLimiter;
    private readonly AppOptions _opt;
    private readonly IWebHostEnvironment _env;
    private readonly PosterMatchingOrchestrator _matchingOrchestrator;
    private readonly ActiveExternalProviderConfigResolver _activeConfigResolver;
    private readonly ILogger<PosterFetchService> _logger;
    private readonly string _dataDirAbs;
    private readonly string _postersDirAbs;
    private readonly string _posterStoreDirAbs;
    private readonly IPosterFileStore _fileStore;
    private PosterStorePathResolver? _storeResolver;
    // Per-storeDir semaphores to prevent concurrent races writing the same store directory.
    private readonly Dictionary<string, SemaphoreSlim> _storeLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _storeLocksGate = new();
    private readonly TimeSpan _thumbEnqueueTimeout;

    private enum StoreMaterializationStatus
    {
        Ok,
        Skip,
        Fail
    }

    private sealed record StoreMaterializationResult(
        StoreMaterializationStatus Status,
        string? StoreDir,
        string Reason,
        IReadOnlyList<int>? GeneratedWidths = null,
        IReadOnlyList<int>? MissingWidths = null);

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
        OpenLibraryClient openLibrary,
        RawgClient rawg,
        ComicVineClient comicVine,
        MusicBrainzClient musicBrainz,
        IOptions<AppOptions> opt,
        IWebHostEnvironment env,
        PosterMatchingOrchestrator matchingOrchestrator,
        ActiveExternalProviderConfigResolver activeConfigResolver,
        ILogger<PosterFetchService> logger,
        PosterThumbService? thumbService = null,
        IPosterThumbQueue? thumbQueue = null,
        IExternalProviderLimiter? externalProviderLimiter = null,
        IPosterFileStore? fileStore = null)
    {
        _releases = releases;
        _fileStore = fileStore ?? new PosterFileStore();
        _activity = activity;
        _tmdb = tmdb;
        _fanart = fanart;
        _igdb = igdb;
        _tvmaze = tvmaze;
        _jikan = jikan;
        _theAudioDb = theAudioDb;
        _googleBooks = googleBooks;
        _openLibrary = openLibrary;
        _rawg = rawg;
        _comicVine = comicVine;
        _musicBrainz = musicBrainz;
        _thumbService = thumbService ?? new PosterThumbService(Microsoft.Extensions.Logging.Abstractions.NullLogger<PosterThumbService>.Instance);
        _thumbQueue = thumbQueue ?? NoOpPosterThumbQueue.Instance;
        _externalProviderLimiter = externalProviderLimiter ?? NoOpExternalProviderLimiter.Instance;
        _opt = opt.Value;
        _env = env;
        _matchingOrchestrator = matchingOrchestrator;
        _activeConfigResolver = activeConfigResolver;
        _logger = logger;
        _thumbEnqueueTimeout = ResolveThumbEnqueueTimeout(_opt);

        // Compute once: data/ is relative → anchor on ContentRoot
        _dataDirAbs = Path.IsPathRooted(opt.Value.DataDir)
            ? opt.Value.DataDir
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, opt.Value.DataDir));
        _postersDirAbs = Path.Combine(_dataDirAbs, "posters");
        _posterStoreDirAbs = Path.Combine(_postersDirAbs, "store");
    }

    private string DataDirAbs => _dataDirAbs;
    private string PostersDirAbs => _postersDirAbs;
    public string PosterStoreDirPath => _posterStoreDirAbs;

    public string PostersDirPath => PostersDirAbs;

    private PosterStorePathResolver GetStoreResolver()
    {
        if (_storeResolver is not null) return _storeResolver;
        lock (_storeLocksGate)
        {
            _storeResolver ??= new PosterStorePathResolver(_posterStoreDirAbs);
        }
        return _storeResolver;
    }

    internal SemaphoreSlim GetStoreLock(string storeDir)
    {
        storeDir = storeDir.Trim();
        lock (_storeLocksGate)
        {
            if (!_storeLocks.TryGetValue(storeDir, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _storeLocks[storeDir] = sem;
            }
            return sem;
        }
    }

    private Task<T> RunProviderAsync<T>(ProviderKind kind, Func<CancellationToken, Task<T>> action, CancellationToken ct)
        => _externalProviderLimiter.RunAsync(kind, action, ct);

    private Task RunProviderAsync(ProviderKind kind, Func<CancellationToken, Task> action, CancellationToken ct)
        => _externalProviderLimiter.RunAsync(kind, action, ct);

    private Task<T> RunTmdbAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        => RunProviderAsync(ProviderKind.Tmdb, action, ct);

    private Task<T> RunIgdbAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        => RunProviderAsync(ProviderKind.Igdb, action, ct);

    private Task<T> RunFanartAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        => RunProviderAsync(ProviderKind.Fanart, action, ct);

    private Task<T> RunTvMazeAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        => RunProviderAsync(ProviderKind.TvMaze, action, ct);

    private Task<T> RunOthersAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        => RunProviderAsync(ProviderKind.Others, action, ct);

    internal bool TryResolveStoreThumbPath(string storeDir, int width, out string thumbPath)
    {
        thumbPath = string.Empty;
        if (string.IsNullOrWhiteSpace(storeDir))
            return false;

        return GetStoreResolver().TryResolvePosterFile(storeDir, $"w{width}.webp", out thumbPath);
    }

    internal bool TryResolveStoreOriginalPath(string storeDir, out string originalPath)
    {
        originalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(storeDir))
            return false;

        var resolver = GetStoreResolver();
        if (!resolver.TryResolveStoreDir(storeDir, out var storeDirFull) || !Directory.Exists(storeDirFull))
            return false;

        originalPath = Directory.EnumerateFiles(storeDirFull, "original.*", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(originalPath) && System.IO.File.Exists(originalPath);
    }

    internal async ValueTask<PosterThumbEnqueueResult> EnqueueThumbGenerationAsync(
        string? storeDir,
        PosterThumbJobReason reason,
        CancellationToken ct,
        IReadOnlyList<int>? widths = null,
        long? releaseId = null)
    {
        if (string.IsNullOrWhiteSpace(storeDir))
            return new PosterThumbEnqueueResult(PosterThumbEnqueueStatus.Rejected);

        var normalizedWidths = widths?
            .Distinct()
            .Where(width => PosterThumbService.SupportedWidths.Contains(width))
            .OrderBy(width => width)
            .ToArray();

        var job = new PosterThumbJob(
            StoreDir: storeDir,
            Widths: normalizedWidths is { Length: > 0 } ? normalizedWidths : null,
            Reason: reason,
            ReleaseId: releaseId);

        var enqueue = await _thumbQueue.EnqueueAsync(job, ct, _thumbEnqueueTimeout).ConfigureAwait(false);
        if (enqueue.IsTimedOut)
        {
            _logger.LogWarning(
                "Poster thumb enqueue timed out reason={Reason} storeDir={StoreDir} releaseId={ReleaseId} timeoutMs={TimeoutMs} backlog={Backlog}",
                reason,
                storeDir,
                releaseId,
                (int)_thumbEnqueueTimeout.TotalMilliseconds,
                _thumbQueue.Count);
        }

        return enqueue;
    }

    internal async Task<PosterThumbWorkResult> EnsureThumbsAsync(PosterThumbJob job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.StoreDir))
            return new PosterThumbWorkResult(false, true, "missing_store_dir", []);

        var widths = job.Widths is { Count: > 0 }
            ? job.Widths.Where(width => PosterThumbService.SupportedWidths.Contains(width)).Distinct().OrderBy(width => width).ToArray()
            : PosterThumbService.SupportedWidths;
        if (widths.Length == 0)
            widths = PosterThumbService.SupportedWidths;

        var resolver = GetStoreResolver();
        if (!resolver.TryResolveStoreDir(job.StoreDir, out var storeDirFull))
            return new PosterThumbWorkResult(false, true, "invalid_store_dir", []);

        var sem = GetStoreLock(job.StoreDir);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(storeDirFull))
                return new PosterThumbWorkResult(false, true, "missing_store_dir", []);

            var missing = widths
                .Where(width => !System.IO.File.Exists(Path.Combine(storeDirFull, $"w{width}.webp")))
                .ToArray();
            if (missing.Length == 0)
                return new PosterThumbWorkResult(true, true, "already_exists", []);

            if (!TryResolveStoreOriginalPath(job.StoreDir, out var originalPath))
                return new PosterThumbWorkResult(false, true, "missing_original", []);

            var generated = await _thumbService.GenerateThumbsAsync(originalPath, storeDirFull, missing, ct).ConfigureAwait(false);
            var remaining = missing
                .Where(width => !System.IO.File.Exists(Path.Combine(storeDirFull, $"w{width}.webp")))
                .ToArray();

            return remaining.Length == 0
                ? new PosterThumbWorkResult(true, false, "generated", generated)
                : new PosterThumbWorkResult(generated.Count > 0, generated.Count == 0, remaining.Length == missing.Length ? "generation_failed" : "partial", generated);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<StoreMaterializationResult> TrySaveToStoreAsync(
        long releaseId,
        string provider,
        string providerId,
        string ext,
        byte[] bytes,
        string posterFile,
        int? tmdbId,
        string? posterPath,
        CancellationToken ct)
    {
        return await TryMaterializeStoreForReleaseAsync(
            releaseId,
            provider,
            providerId,
            posterFile,
            bytes,
            null,
            forceRefresh: true,
            tmdbId,
            posterPath,
            ext,
            existingPosterKey: null,
            ct).ConfigureAwait(false);
    }

    private async Task<StoreMaterializationResult> TryMaterializeStoreForReleaseAsync(
        long releaseId,
        string? provider,
        string? providerId,
        string legacyPosterFile,
        byte[]? sourceBytes,
        string? legacyPosterPath,
        bool forceRefresh,
        int? tmdbId,
        string? posterPath,
        string? preferredOriginalExtension,
        string? existingPosterKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(legacyPosterFile))
            return new StoreMaterializationResult(StoreMaterializationStatus.Skip, null, "missing_legacy_poster_file");

        // Only a typed filename can supply a missing namespace; never infer it from legacy bytes.
        if (string.Equals(provider, "tmdb", StringComparison.OrdinalIgnoreCase) && providerId is not null && !providerId.Contains(':'))
        {
            foreach (var type in new[] { "movie", "tv" })
                if (legacyPosterFile.StartsWith($"tmdb-{type}-{providerId}-", StringComparison.OrdinalIgnoreCase))
                    providerId = $"{type}:{providerId}";
        }
        if (!TryResolveStoreIdentity(provider, providerId, existingPosterKey, out var normalizedProvider, out var normalizedProviderId, out var posterKey, out var storeDir))
            return new StoreMaterializationResult(StoreMaterializationStatus.Skip, null, "missing_store_identity");

        var resolver = GetStoreResolver();
        if (!resolver.TryResolveStoreDir(storeDir, out var storeDirFull))
        {
            _logger.LogWarning("[POSTER_STORE] materialize fail storeDir={StoreDir} releaseId={ReleaseId} reason=invalid_store_dir", storeDir, releaseId);
            return new StoreMaterializationResult(StoreMaterializationStatus.Fail, storeDir, "invalid_store_dir");
        }

        var sem = GetStoreLock(storeDir);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        string? displacedDir = null;
        try
        {
            _logger.LogInformation(
                "[POSTER_STORE] materialize start storeDir={StoreDir} releaseId={ReleaseId} provider={Provider} providerId={ProviderId}",
                storeDir,
                releaseId,
                normalizedProvider,
                normalizedProviderId);

            _releases.ValidateMatching(releaseId);
            Directory.CreateDirectory(storeDirFull);

            var originalPath = Directory.EnumerateFiles(storeDirFull, "original.*", SearchOption.TopDirectoryOnly).FirstOrDefault();
            var existingThumbs = PosterThumbService.SupportedWidths
                .Where(width => System.IO.File.Exists(Path.Combine(storeDirFull, $"w{width}.webp")))
                .ToArray();

            if (!forceRefresh && originalPath is not null && existingThumbs.Length == PosterThumbService.SupportedWidths.Length)
            {
                displacedDir = _releases.SavePosterWithStore(releaseId, tmdbId, posterPath, legacyPosterFile, posterKey, storeDir, normalizedProvider == "tmdb" ? normalizedProviderId.Split(':')[0] : null);
                await EnqueueThumbWarmupAsync(storeDir, releaseId, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "[POSTER_STORE] materialize skip storeDir={StoreDir} releaseId={ReleaseId} reason=already_up_to_date",
                    storeDir,
                    releaseId);
                return new StoreMaterializationResult(StoreMaterializationStatus.Skip, storeDir, "already_up_to_date", [], []);
            }

            var effectiveLegacyPath = string.IsNullOrWhiteSpace(legacyPosterPath)
                ? Path.Combine(PostersDirAbs, legacyPosterFile)
                : legacyPosterPath;

            var safeExt = !string.IsNullOrWhiteSpace(preferredOriginalExtension)
                ? preferredOriginalExtension
                : Path.GetExtension(!string.IsNullOrWhiteSpace(effectiveLegacyPath) ? effectiveLegacyPath : legacyPosterFile);
            if (string.IsNullOrWhiteSpace(safeExt))
                safeExt = ".jpg";

            if (!resolver.TryResolveFile(storeDirFull, $"original{safeExt}", out var defaultOriginalPath))
            {
                _logger.LogWarning("[POSTER_STORE] materialize fail storeDir={StoreDir} releaseId={ReleaseId} reason=invalid_original_name", storeDir, releaseId);
                return new StoreMaterializationResult(StoreMaterializationStatus.Fail, storeDir, "invalid_original_name");
            }

            originalPath ??= defaultOriginalPath;

            if (forceRefresh)
            {
                foreach (var width in PosterThumbService.SupportedWidths)
                {
                    var thumbPath = Path.Combine(storeDirFull, $"w{width}.webp");
                    if (System.IO.File.Exists(thumbPath))
                    {
                        try { System.IO.File.Delete(thumbPath); } catch { }
                    }
                }
            }

            if (forceRefresh || !System.IO.File.Exists(originalPath))
            {
                if (sourceBytes is not null && sourceBytes.Length > 0)
                {
                    await WriteAtomicAsync(originalPath, sourceBytes, ct).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(effectiveLegacyPath) && System.IO.File.Exists(effectiveLegacyPath))
                {
                    await CopyAtomicAsync(effectiveLegacyPath, originalPath, ct).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning(
                        "[POSTER_STORE] materialize fail storeDir={StoreDir} releaseId={ReleaseId} reason=missing_source",
                        storeDir,
                        releaseId);
                    return new StoreMaterializationResult(StoreMaterializationStatus.Fail, storeDir, "missing_source");
                }
            }

            if ((sourceBytes is null || sourceBytes.Length == 0) && System.IO.File.Exists(originalPath))
                sourceBytes = await System.IO.File.ReadAllBytesAsync(originalPath, ct).ConfigureAwait(false);

            IReadOnlyList<int> generated = [];
            if (sourceBytes is not null && sourceBytes.Length > 0)
                generated = await _thumbService.GenerateThumbsAsync(sourceBytes, storeDirFull, ct).ConfigureAwait(false);

            var missing = PosterThumbService.SupportedWidths
                .Where(width => !System.IO.File.Exists(Path.Combine(storeDirFull, $"w{width}.webp")))
                .ToArray();

            displacedDir = _releases.SavePosterWithStore(releaseId, tmdbId, posterPath, legacyPosterFile, posterKey, storeDir, normalizedProvider == "tmdb" ? normalizedProviderId.Split(':')[0] : null);
            await EnqueueThumbWarmupAsync(storeDir, releaseId, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "[POSTER_STORE] materialize ok storeDir={StoreDir} releaseId={ReleaseId} generated={Generated} missing={Missing}",
                storeDir,
                releaseId,
                generated.Count == 0 ? "none" : string.Join(",", generated),
                missing.Length == 0 ? "none" : string.Join(",", missing));

            return new StoreMaterializationResult(StoreMaterializationStatus.Ok, storeDir, "materialized", generated, missing);
        }
        catch (OperationCanceledException) { throw; }
        catch (ReleaseRepository.StaleMatchException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[POSTER_STORE] materialize fail storeDir={StoreDir} releaseId={ReleaseId}", storeDir, releaseId);
            return new StoreMaterializationResult(StoreMaterializationStatus.Fail, storeDir, "exception");
        }
        finally
        {
            sem.Release();
            // Never acquire an old store's lock while holding the new one's:
            // concurrent A -> B and B -> A replacements must not deadlock.
            TryCleanupDisplacedStoreDir(displacedDir);
        }
    }

    private void LogStoreOutcome(string fetchPath, long releaseId, string legacyPosterFile, StoreMaterializationResult result)
    {
        var status = result.Status switch
        {
            StoreMaterializationStatus.Ok => "ok",
            StoreMaterializationStatus.Fail => "fail",
            _ => "skip"
        };

        _logger.LogInformation(
            "[POSTER_STORE] fetch path={FetchPath} releaseId={ReleaseId} posterFile={PosterFile} store={StoreStatus} storeDir={StoreDir} reason={Reason}",
            fetchPath,
            releaseId,
            legacyPosterFile,
            status,
            result.StoreDir ?? "none",
            result.Reason);
    }

    /// <summary>
    /// Deletes a store directory that was displaced when a release switched to a
    /// different <c>poster_store_dir</c>, provided no other release still references it.
    /// </summary>
    private void TryCleanupDisplacedStoreDir(string? displacedDir)
    {
        if (!string.IsNullOrWhiteSpace(displacedDir))
            CleanupOrphanedStoreDir(displacedDir);
    }

    internal readonly record struct StoreCleanupResult(bool Deleted, bool Failed, long FreedBytes);

    /// <summary>
    /// All store collectors use the publisher's per-store lock. Candidate lists
    /// are only hints: references are re-read under the lock immediately before
    /// deletion. Entities without releases deliberately protect their stores.
    /// Restore stages/rescue copies live outside this root and are never scanned.
    /// </summary>
    internal StoreCleanupResult CleanupOrphanedStoreDir(
        string storeDir, bool measureBytes = false, CancellationToken ct = default)
    {
        if (!GetStoreResolver().TryResolveStoreDir(storeDir, out var fullPath))
        {
            _logger.LogWarning("[POSTER_STORE] delete skipped for unsafe storeDir={StoreDir}", storeDir);
            return new(false, true, 0);
        }

        storeDir = Path.GetFileName(fullPath);
        var sem = GetStoreLock(storeDir);
        sem.Wait(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(fullPath)) return default;

            long bytes = 0;
            if (measureBytes)
            {
                foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    bytes += new FileInfo(file).Length;
                }
            }

            if (_releases.GetOrphanedStoreDirs([storeDir]).Count == 0) return default;
            ct.ThrowIfCancellationRequested();
            _fileStore.DeleteDirectory(fullPath);
            if (Directory.Exists(fullPath))
                throw new IOException("Poster store still exists after deletion");

            _logger.LogInformation("[POSTER_STORE] orphan dir purged storeDir={StoreDir}", storeDir);
            return new(true, false, bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[POSTER_STORE] failed to purge orphan dir storeDir={StoreDir}", storeDir);
            return new(false, true, 0);
        }
        finally { sem.Release(); }
    }

    private async Task EnqueueThumbWarmupAsync(string? storeDir, long releaseId, CancellationToken ct)
    {
        try
        {
            var enqueue = await EnqueueThumbGenerationAsync(storeDir, PosterThumbJobReason.Warmup, ct, releaseId: releaseId).ConfigureAwait(false);
            if (enqueue.IsTimedOut)
            {
                _logger.LogWarning("[POSTER_STORE] thumb warmup timed out storeDir={StoreDir} releaseId={ReleaseId} backlog={Backlog}", storeDir, releaseId, _thumbQueue.Count);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[POSTER_STORE] thumb warmup enqueue failed storeDir={StoreDir} releaseId={ReleaseId}", storeDir, releaseId);
        }
    }

    private static TimeSpan ResolveThumbEnqueueTimeout(AppOptions options)
    {
        var timeoutMs = options.ThumbEnqueueTimeoutMs <= 0
            ? PosterThumbQueue.DefaultEnqueueTimeoutMs
            : options.ThumbEnqueueTimeoutMs;
        return TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 100, 30000));
    }

    private static string? NormalizePosterProviderId(string? providerId)
        => providerId?.Contains(':') == true ? providerId[(providerId.LastIndexOf(':') + 1)..] : providerId;

    private static bool TryResolveStoreIdentity(
        string? provider,
        string? providerId,
        string? posterKey,
        out string normalizedProvider,
        out string normalizedProviderId,
        out string resolvedPosterKey,
        out string storeDir)
    {
        normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        normalizedProviderId = (providerId ?? string.Empty).Trim();

        if ((string.IsNullOrWhiteSpace(normalizedProvider) || string.IsNullOrWhiteSpace(normalizedProviderId)) &&
            !string.IsNullOrWhiteSpace(posterKey))
        {
            var idx = posterKey.IndexOf(':');
            if (idx > 0 && idx < posterKey.Length - 1)
            {
                normalizedProvider = posterKey[..idx].Trim().ToLowerInvariant();
                normalizedProviderId = posterKey[(idx + 1)..].Trim();
            }
        }

        if (normalizedProvider == "tmdb" &&
            !(normalizedProviderId.StartsWith("movie:", StringComparison.Ordinal) || normalizedProviderId.StartsWith("tv:", StringComparison.Ordinal)))
        {
            resolvedPosterKey = string.Empty;
            storeDir = string.Empty;
            return false;
        }
        var sanitizedProviderId = PosterStorePathResolver.SanitizeStoreDir(normalizedProviderId);
        if (string.IsNullOrWhiteSpace(normalizedProvider) || string.IsNullOrWhiteSpace(normalizedProviderId) || string.IsNullOrWhiteSpace(sanitizedProviderId))
        {
            resolvedPosterKey = string.Empty;
            storeDir = string.Empty;
            return false;
        }

        resolvedPosterKey = $"{normalizedProvider}:{normalizedProviderId}";
        storeDir = $"{normalizedProvider}-{sanitizedProviderId}";
        return true;
    }

    private static async Task WriteAtomicAsync(string targetPath, byte[] bytes, CancellationToken ct)
    {
        var tmpPath = targetPath + ".tmp";
        try
        {
            await PosterImageWriter.WriteAsync(tmpPath, bytes, ct).ConfigureAwait(false);
            System.IO.File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            try { System.IO.File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    private static Task CopyAtomicAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var tmpPath = targetPath + ".tmp";
        try
        {
            System.IO.File.Copy(sourcePath, tmpPath, overwrite: true);
            ct.ThrowIfCancellationRequested();
            System.IO.File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            try { System.IO.File.Delete(tmpPath); } catch { }
            throw;
        }

        return Task.CompletedTask;
    }

    public int GetLocalPosterCount()
    {
        try
        {
            if (!Directory.Exists(PostersDirAbs)) return 0;
            return Directory.EnumerateFiles(PostersDirAbs, "*.*", SearchOption.TopDirectoryOnly)
                .Count();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to count local posters in {Dir}", PostersDirAbs);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear poster cache directory {Dir}", PostersDirAbs);
        }
        return cleared;
    }

    public async Task<string?> SaveTmdbPosterAsync(long id, int tmdbId, string posterPath, CancellationToken ct, bool logSingle = true, string? mediaType = null)
    {
        using var publication = await _releases.PosterLifecycle.EnterPublicationAsync(ct).ConfigureAwait(false);
        if (tmdbId <= 0 || string.IsNullOrWhiteSpace(posterPath)) return null;

        var seed = _releases.GetForPoster(id);
        var type = TmdbIdentity.NormalizeType(mediaType ?? seed?.TmdbMediaType ?? seed?.MediaType);
        if (seed is null || type is null) return null;
        Directory.CreateDirectory(PostersDirAbs);

        var bytesTmdb = await RunTmdbAsync(innerCt => _tmdb.DownloadPosterW500Async(posterPath, innerCt), ct).ConfigureAwait(false);
        if (bytesTmdb is null || bytesTmdb.Length == 0) return null;

        var ext = Path.GetExtension(posterPath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";

        var file = $"tmdb-{type}-{tmdbId}-manual-{Guid.NewGuid():N}{ext}";
        var full = Path.Combine(PostersDirAbs, file);
        await PosterImageWriter.WriteAsync(full, bytesTmdb, ct).ConfigureAwait(false);
        _releases.SetManualMatch(id, "tmdb", type, tmdbId.ToString(CultureInfo.InvariantCulture));
        using var matching = _releases.BeginMatching(_releases.GetForPoster(id)!);
        _releases.SavePoster(id, tmdbId, posterPath, file, type);
        await TrySaveToStoreAsync(id, "tmdb", $"{type}:{tmdbId}", ext, bytesTmdb, file, tmdbId, posterPath, ct).ConfigureAwait(false);
        await UpdateExternalDetailsFromTmdbAsync(id, tmdbId, type, ct).ConfigureAwait(false);        var hash = ComputeSha256Hex(bytesTmdb);
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
        using var publication = await _releases.PosterLifecycle.EnterPublicationAsync(ct).ConfigureAwait(false);
        if (igdbId <= 0 || string.IsNullOrWhiteSpace(coverUrl)) return null;

        Directory.CreateDirectory(PostersDirAbs);

        var bytes = await RunIgdbAsync(innerCt => _igdb.DownloadCoverAsync(coverUrl, innerCt), ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;

        var file = $"igdb-{igdbId}-manual-{Guid.NewGuid():N}.jpg";
        var full = Path.Combine(PostersDirAbs, file);
        await PosterImageWriter.WriteAsync(full, bytes, ct).ConfigureAwait(false);
        _releases.SetManualMatch(id, "igdb", "game", igdbId.ToString(CultureInfo.InvariantCulture));
        using var matching = _releases.BeginMatching(_releases.GetForPoster(id)!);
        _releases.SavePoster(id, null, coverUrl, file);
        await TrySaveToStoreAsync(id, "igdb", igdbId.ToString(CultureInfo.InvariantCulture), ".jpg", bytes, file, null, null, ct).ConfigureAwait(false);
        await UpdateExternalDetailsFromIgdbAsync(id, igdbId, ct).ConfigureAwait(false);        var size = InferIgdbSize(coverUrl);
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

    public async Task<string?> SaveRawgPosterAsync(long id, int rawgId, string coverUrl, CancellationToken ct, bool logSingle = true)
    {
        using var publication = await _releases.PosterLifecycle.EnterPublicationAsync(ct).ConfigureAwait(false);
        if (rawgId <= 0 || string.IsNullOrWhiteSpace(coverUrl)) return null;

        Directory.CreateDirectory(PostersDirAbs);

        var bytes = await RunOthersAsync(innerCt => _rawg.DownloadCoverAsync(coverUrl, innerCt), ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;

        var file = $"rawg-{rawgId}-manual-{Guid.NewGuid():N}.jpg";
        var full = Path.Combine(PostersDirAbs, file);
        await PosterImageWriter.WriteAsync(full, bytes, ct).ConfigureAwait(false);
        _releases.SetManualMatch(id, "rawg", "game", rawgId.ToString(CultureInfo.InvariantCulture));
        using var matching = _releases.BeginMatching(_releases.GetForPoster(id)!);
        _releases.SavePoster(id, null, coverUrl, file);
        await TrySaveToStoreAsync(id, "rawg", rawgId.ToString(CultureInfo.InvariantCulture), ".jpg", bytes, file, null, null, ct).ConfigureAwait(false);
        _releases.UpdateExternalDetails(id, ExternalProviderKeys.Rawg, rawgId.ToString(CultureInfo.InvariantCulture),
            null, null, null, null, null, null, null, null, null, null, null);
        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, "rawg", rawgId.ToString(CultureInfo.InvariantCulture), null, null, hash);

        if (logSingle)
            LogActivity(null, "info", "poster_fetch", "Poster set manually (RAWG)", new { releaseId = id, rawgId, coverUrl, posterFile = file });

        return $"/api/posters/release/{id}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    public async Task<string?> SaveTheAudioDbPosterAsync(long id, string? providerId, string posterUrl, CancellationToken ct, bool logSingle = true)
    {
        using var publication = await _releases.PosterLifecycle.EnterPublicationAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(posterUrl)) return null;

        Directory.CreateDirectory(PostersDirAbs);

        var bytes = await RunOthersAsync(innerCt => _theAudioDb.DownloadImageAsync(posterUrl, innerCt), ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;

        var ext = InferFileExtensionFromUrl(posterUrl, ".jpg");
        var normalizedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? Guid.NewGuid().ToString("N")
            : SanitizeForFile(providerId);
        var file = $"theaudiodb-{normalizedProviderId}-manual-{Guid.NewGuid():N}{ext}";
        var full = Path.Combine(PostersDirAbs, file);
        await PosterImageWriter.WriteAsync(full, bytes, ct).ConfigureAwait(false);
        _releases.SetManualMatch(id, ExternalProviderKeys.TheAudioDb, "audio", providerId?.Trim() ?? normalizedProviderId);
        using var matching = _releases.BeginMatching(_releases.GetForPoster(id)!);
        _releases.SavePoster(id, null, posterUrl, file);
        await TrySaveToStoreAsync(id, ExternalProviderKeys.TheAudioDb, normalizedProviderId, ext, bytes, file, null, null, ct).ConfigureAwait(false);
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

    public async Task<string?> SaveGoogleBooksPosterAsync(long id, string? providerId, string posterUrl, CancellationToken ct, bool logSingle = true)
    {
        using var publication = await _releases.PosterLifecycle.EnterPublicationAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(posterUrl)) return null;

        Directory.CreateDirectory(PostersDirAbs);

        var bytes = await RunOthersAsync(innerCt => _googleBooks.DownloadImageAsync(posterUrl, innerCt), ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;

        var ext = InferFileExtensionFromUrl(posterUrl, ".jpg");
        var normalizedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? Guid.NewGuid().ToString("N")
            : SanitizeForFile(providerId);
        var file = $"googlebooks-{normalizedProviderId}-manual-{Guid.NewGuid():N}{ext}";
        var full = Path.Combine(PostersDirAbs, file);
        await PosterImageWriter.WriteAsync(full, bytes, ct).ConfigureAwait(false);
        _releases.SetManualMatch(id, ExternalProviderKeys.GoogleBooks, "book", providerId?.Trim() ?? normalizedProviderId);
        using var matching = _releases.BeginMatching(_releases.GetForPoster(id)!);
        _releases.SavePoster(id, null, posterUrl, file);
        await TrySaveToStoreAsync(id, ExternalProviderKeys.GoogleBooks, normalizedProviderId, ext, bytes, file, null, null, ct).ConfigureAwait(false);
        _releases.UpdateExternalDetails(
            id,
            ExternalProviderKeys.GoogleBooks,
            providerId?.Trim() ?? normalizedProviderId,
            null, null, null, null, null, null, null, null, null, null, null);

        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, ExternalProviderKeys.GoogleBooks,
            providerId?.Trim() ?? normalizedProviderId, null, "manual", hash);

        if (logSingle)
        {
            LogActivity(null, "info", "poster_fetch", "Poster set manually (Google Books)", new
            {
                releaseId = id,
                providerId = providerId?.Trim(),
                posterUrl,
                posterFile = file
            });
        }

        return $"/api/posters/release/{id}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    public async Task<string?> SaveOpenLibraryPosterAsync(long id, string? providerId, string posterUrl, CancellationToken ct, bool logSingle = true)
    {
        using var publication = await _releases.PosterLifecycle.EnterPublicationAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(posterUrl)) return null;

        Directory.CreateDirectory(PostersDirAbs);

        var bytes = await RunOthersAsync(innerCt => _openLibrary.DownloadImageAsync(posterUrl, innerCt), ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;

        var normalizedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? Guid.NewGuid().ToString("N")
            : SanitizeForFile(providerId);
        var file = $"openlibrary-{normalizedProviderId}-manual-{Guid.NewGuid():N}.jpg";
        var full = Path.Combine(PostersDirAbs, file);
        await PosterImageWriter.WriteAsync(full, bytes, ct).ConfigureAwait(false);
        _releases.SetManualMatch(id, ExternalProviderKeys.OpenLibrary, "book", providerId?.Trim() ?? normalizedProviderId);
        using var matching = _releases.BeginMatching(_releases.GetForPoster(id)!);
        _releases.SavePoster(id, null, posterUrl, file);
        await TrySaveToStoreAsync(id, ExternalProviderKeys.OpenLibrary, normalizedProviderId, ".jpg", bytes, file, null, null, ct).ConfigureAwait(false);
        _releases.UpdateExternalDetails(
            id,
            ExternalProviderKeys.OpenLibrary,
            providerId?.Trim() ?? normalizedProviderId,
            null, null, null, null, null, null, null, null, null, null, null);

        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, ExternalProviderKeys.OpenLibrary,
            providerId?.Trim() ?? normalizedProviderId, null, "manual", hash);

        if (logSingle)
        {
            LogActivity(null, "info", "poster_fetch", "Poster set manually (Open Library)", new
            {
                releaseId = id,
                providerId = providerId?.Trim(),
                posterUrl,
                posterFile = file
            });
        }

        return $"/api/posters/release/{id}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    public async Task<string?> SaveComicVinePosterAsync(long id, string? providerId, string posterUrl, CancellationToken ct, bool logSingle = true)
    {
        using var publication = await _releases.PosterLifecycle.EnterPublicationAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(posterUrl)) return null;

        Directory.CreateDirectory(PostersDirAbs);

        var bytes = await RunOthersAsync(innerCt => _comicVine.DownloadImageAsync(posterUrl, innerCt), ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;

        var ext = InferFileExtensionFromUrl(posterUrl, ".jpg");
        var normalizedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? Guid.NewGuid().ToString("N")
            : SanitizeForFile(providerId);
        var file = $"comicvine-{normalizedProviderId}-manual-{Guid.NewGuid():N}{ext}";
        var full = Path.Combine(PostersDirAbs, file);
        await PosterImageWriter.WriteAsync(full, bytes, ct).ConfigureAwait(false);
        _releases.SetManualMatch(id, ExternalProviderKeys.ComicVine, "comic", providerId?.Trim() ?? normalizedProviderId);
        using var matching = _releases.BeginMatching(_releases.GetForPoster(id)!);
        _releases.SavePoster(id, null, posterUrl, file);
        await TrySaveToStoreAsync(id, ExternalProviderKeys.ComicVine, normalizedProviderId, ext, bytes, file, null, null, ct).ConfigureAwait(false);
        _releases.UpdateExternalDetails(
            id,
            ExternalProviderKeys.ComicVine,
            providerId?.Trim() ?? normalizedProviderId,
            null, null, null, null, null, null, null, null, null, null, null);

        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, ExternalProviderKeys.ComicVine,
            providerId?.Trim() ?? normalizedProviderId, null, "manual", hash);

        if (logSingle)
        {
            LogActivity(null, "info", "poster_fetch", "Poster set manually (ComicVine)", new
            {
                releaseId = id,
                providerId = providerId?.Trim(),
                posterUrl,
                posterFile = file
            });
        }

        return $"/api/posters/release/{id}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    public async Task<string?> SaveMusicBrainzPosterAsync(long id, string? providerId, string posterUrl, CancellationToken ct, bool logSingle = true)
    {
        using var publication = await _releases.PosterLifecycle.EnterPublicationAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(posterUrl)) return null;

        Directory.CreateDirectory(PostersDirAbs);

        var bytes = await RunOthersAsync(innerCt => _musicBrainz.DownloadImageAsync(posterUrl, innerCt), ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;

        var ext = InferFileExtensionFromUrl(posterUrl, ".jpg");
        var normalizedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? Guid.NewGuid().ToString("N")
            : SanitizeForFile(providerId);

        var file = $"musicbrainz-{normalizedProviderId}-manual-{Guid.NewGuid():N}{ext}";
        var full = Path.Combine(PostersDirAbs, file);
        await PosterImageWriter.WriteAsync(full, bytes, ct).ConfigureAwait(false);
        _releases.SetManualMatch(id, ExternalProviderKeys.MusicBrainz, "audio", providerId?.Trim() ?? normalizedProviderId);
        using var matching = _releases.BeginMatching(_releases.GetForPoster(id)!);
        _releases.SavePoster(id, null, posterUrl, file);
        await TrySaveToStoreAsync(id, ExternalProviderKeys.MusicBrainz, normalizedProviderId, ext, bytes, file, null, null, ct).ConfigureAwait(false);
        _releases.UpdateExternalDetails(
            id,
            ExternalProviderKeys.MusicBrainz,
            providerId?.Trim() ?? normalizedProviderId,
            null, null, null, null, null, null, null, null, null, null, null);

        var hash = ComputeSha256Hex(bytes);
        PosterAudit.UpdateAttemptSuccess(_releases, id, ExternalProviderKeys.MusicBrainz,
            providerId?.Trim() ?? normalizedProviderId, null, "manual", hash);

        if (logSingle)
        {
            LogActivity(null, "info", "poster_fetch", "Poster set manually (MusicBrainz)", new
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

    private bool IsTvmazeEnabled()
    {
        return _activeConfigResolver.GetActiveEnabled(ExternalProviderKeys.Tvmaze);
    }

    public async Task<PosterFetchResult> FetchPosterAsync(long id, CancellationToken ct, bool logSingle, bool skipIfExists = true)
    {
        var snapshot = _releases.GetForPoster(id);
        if (snapshot is null) return new(false, 404, new { error = "release not found" }, null);
        using var matching = _releases.BeginMatching(snapshot);
        try { return await FetchPosterCoreAsync(id, snapshot, ct, logSingle, skipIfExists).ConfigureAwait(false); }
        catch (ReleaseRepository.StaleMatchException)
        {
            _logger.LogInformation("Discarded obsolete poster match releaseId={ReleaseId}", id);
            return new(false, 409, new { error = "matching_identity_changed" }, snapshot.SourceId);
        }
    }

    private async Task<PosterFetchResult> FetchPosterCoreAsync(long id, ReleaseForPoster snapshot, CancellationToken ct, bool logSingle, bool skipIfExists)
    {
        using var publication = await _releases.PosterLifecycle.EnterPublicationAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(PostersDirAbs);
        var forceRefresh = !skipIfExists;

        var r = snapshot;
        if (r is null)
        {
            if (logSingle)
            {
                LogActivity(null, "warn", "poster_fetch", "Poster fetch failed: release not found", new { releaseId = id });
            }
            PosterAudit.UpdateAttemptFailure(_releases, id, null, null, null, null, "release not found");
            return new PosterFetchResult(false, 404, new { error = "release not found" }, null);
        }

        if (r.ManualMatchProvider is not null ||
            (r.MatchRevision is null && r.PosterFile?.Contains("-manual", StringComparison.OrdinalIgnoreCase) == true))
        {
            return await RefreshEstablishedMatchAsync(r, ct, skipIfExists).ConfigureAwait(false);
        }

        UnifiedCategoryMappings.TryParse(r.UnifiedCategory, out var initialCategory);
        if (initialCategory is (UnifiedCategory.Film or UnifiedCategory.Animation or UnifiedCategory.Spectacle or UnifiedCategory.Serie or UnifiedCategory.Emission or UnifiedCategory.Anime or UnifiedCategory.JeuWindows or UnifiedCategory.Autre) && MatchingShadowState.From(r).Protected)
        {
            return await RefreshEstablishedMatchAsync(r, ct, skipIfExists).ConfigureAwait(false);
        }

        if (initialCategory is UnifiedCategory.Book or UnifiedCategory.Comic)
            return await FetchPrintV2Async(id, r.SourceId, initialCategory, ct, skipIfExists).ConfigureAwait(false);

        if (initialCategory == UnifiedCategory.Audio)
            return await FetchAudioV2Async(id, r.SourceId, ct, skipIfExists).ConfigureAwait(false);

        if (initialCategory == UnifiedCategory.JeuWindows)
            return await FetchGameV2Async(id, r.SourceId, ct).ConfigureAwait(false);

        if (initialCategory == UnifiedCategory.Anime)
            return await FetchAnimeV2Async(id, r.SourceId, ct).ConfigureAwait(false);

        var type = ResolveVideoType(r, initialCategory);
        if (type is null)
            return new(false, 422, new { error = "matching_v2_abstained", reason = "video_type_unproven_or_conflicting" }, r.SourceId);
        var context = new PosterFetchRoutingContext(id, initialCategory, IsTvmazeEnabled(), forceRefresh, r.SourceId) { VideoType = type };
        return await _matchingOrchestrator.FetchPosterAsync(this, context, ct).ConfigureAwait(false);
    }

    internal Task<PosterFetchResult> FetchGameBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchGameV2Async(context.ReleaseId, context.SourceId, ct);

    internal Task<PosterFetchResult> FetchVideoBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchVideoV2Async(context, ct);

    internal Task<PosterFetchResult> FetchAnimeBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchAnimeV2Async(context.ReleaseId, context.SourceId, ct);

    internal Task<PosterFetchResult> FetchAudioBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchAudioCategoryBranchAsync(context, ct);

    internal Task<PosterFetchResult> FetchGenericBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchGenericCategoryBranchAsync(context, ct);

    private Task<PosterFetchResult> FetchAudioCategoryBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchAudioV2Async(context.ReleaseId, context.SourceId, ct, !context.ForceRefresh);

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

    private Task<PosterFetchResult> FetchBookBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchPrintV2Async(context.ReleaseId, context.SourceId, UnifiedCategory.Book, ct, !context.ForceRefresh);

    private Task<PosterFetchResult> FetchComicBranchAsync(PosterFetchRoutingContext context, CancellationToken ct)
        => FetchPrintV2Async(context.ReleaseId, context.SourceId, UnifiedCategory.Comic, ct, !context.ForceRefresh);

    private async Task UpdateExternalDetailsFromTmdbAsync(long id, int tmdbId, string mediaType, CancellationToken ct)
    {
        if (tmdbId <= 0) return;

        var normalizedMediaType = NormalizeMediaTypeForTmdb(mediaType);
        TmdbClient.DetailsResult? details = null;
        if (normalizedMediaType == "series")
        {
            details = await RunTmdbAsync(innerCt => _tmdb.GetTvDetailsAsync(tmdbId, innerCt), ct).ConfigureAwait(false);
        }
        else if (normalizedMediaType == "movie")
        {
            details = await RunTmdbAsync(innerCt => _tmdb.GetMovieDetailsAsync(tmdbId, innerCt), ct).ConfigureAwait(false);
        }
        else
        {
            return; // Historical ambiguous identity: do not guess movie versus TV.
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
            details.Cast,
            normalizedMediaType
        );
    }

    private static string? NormalizeMediaTypeForTmdb(string? mediaType)
    {
        var normalized = (mediaType ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "series" or "tv" or "show")
            return "series";

        if (normalized is "movie" or "film")
            return "movie";

        return null;
    }

    private async Task UpdateExternalDetailsFromIgdbAsync(long id, int igdbId, CancellationToken ct)
    {
        if (igdbId <= 0) return;
        var details = await RunIgdbAsync(innerCt => _igdb.GetGameDetailsAsync(igdbId, innerCt), ct).ConfigureAwait(false);
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
        await PosterImageWriter.WriteAsync(full, bytes, ct).ConfigureAwait(false);    }

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

        // Pre-clean: strip format/scene noise before separator detection
        var cleaned = CleanAudioTitle(raw);

        var separators = new[] { " - ", " – ", " — ", " | ", " : ", ", " };
        foreach (var separator in separators)
        {
            var idx = cleaned.IndexOf(separator, StringComparison.Ordinal);
            if (idx <= 0 || idx >= cleaned.Length - separator.Length)
                continue;

            var artist = cleaned[..idx].Trim();
            var title = cleaned[(idx + separator.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                return (artist, title);
        }

        // Compact hyphen separator: "Artist-Album" (no spaces around hyphen).
        // Only split when the left part is a single word (most common scene/web style).
        // Example: "Alkpote-Les Ultimes Marches" → ("Alkpote", "Les Ultimes Marches")
        var compactHyphen = cleaned.IndexOf('-');
        if (compactHyphen > 0 && compactHyphen < cleaned.Length - 1)
        {
            var leftPart = cleaned[..compactHyphen].Trim();
            var rightPart = cleaned[(compactHyphen + 1)..].Trim();
            if (!leftPart.Contains(' ') && !string.IsNullOrWhiteSpace(rightPart) && rightPart.Length > 2)
                return (leftPart, rightPart);
        }

        return (null, cleaned);
    }

    /// <summary>
    /// Strips scene-style noise from audio release names:
    /// dot-separated words, format/quality suffixes, embedded years, Remastered tags, release group tags.
    /// Example: "ACDC.Black.Ice.2008.FLAC[16bit.44.1kHz]-RmKv" → "ACDC Black Ice"
    /// Example: "Alkpote-Les Ultimes Marches-16BIT-44kHz-WEB" → "Alkpote-Les Ultimes Marches"
    /// </summary>
    private static string CleanAudioTitle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input ?? "";

        var s = input.Trim();
        var wasSceneName = false;

        // 1. Dot-to-space for scene-style names (≥3 dots AND ≤1 space)
        if (s.Count(c => c == '.') >= 3 && s.Count(c => c == ' ') <= 1)
        {
            s = s.Replace('.', ' ').Trim();
            wasSceneName = true;
        }

        // 1b. Strip compact hyphenated format/quality/source chain (no spaces around hyphens).
        //     Examples: "-16BIT-44kHz-WEB-FLAC", "-24BIT-96KHZ-CD", "-FLAC-RmKv"
        //     Trigger on an unambiguous audio technical marker preceded by a hyphen.
        s = Regex.Replace(s,
            @"-(?:\d+(?:[.,]\d+)?[Kk][Hh][Zz]|\d+BIT|FLAC|MP3|AAC|ALAC|OGG|SACD|WEB|VINYL).*$",
            "", RegexOptions.IgnoreCase).Trim();

        // 2. Strip audio format/quality suffix (truncates at first space/bracket marker)
        s = StripAudioFormatSuffix(s);

        // 3. Strip trailing year: "Black Ice 2008" → "Black Ice"
        s = Regex.Replace(s, @"\s*[\(\[]?\s*\b(19|20)\d{2}\b\s*[\)\]]?\s*$", "").Trim();

        // 4. Strip "Remastered" suffix
        s = Regex.Replace(s, @"\s+Remaster(?:ed|ised?)?\s*\d{0,4}\s*$", "", RegexOptions.IgnoreCase).Trim();

        // 5. Strip scene release group tag (e.g. "-RmKv", "-NOTAG") — only for scene-style names
        //    and only when no standard artist/title separator is present
        if (wasSceneName &&
            !s.Contains(" - ", StringComparison.Ordinal) &&
            !s.Contains(" – ", StringComparison.Ordinal) &&
            !s.Contains(" — ", StringComparison.Ordinal))
        {
            s = Regex.Replace(s, @"\s*-[A-Za-z0-9]{4,12}\s*$", "").Trim();
        }

        // 5b. Strip trailing ALL-CAPS hyphenated release group tag (e.g. "-EICHBAUM", "-NOTAG")
        //     regardless of scene-name detection — ALL-CAPS ≥ 4 chars after a hyphen = group tag
        s = Regex.Replace(s, @"-[A-Z][A-Z0-9]{3,11}\s*$", "").Trim();

        return s;
    }

    private static string StripAudioFormatSuffix(string s)
    {
        // Truncate at the earliest audio format marker found
        var markers = new[]
        {
            " FLAC", "[FLAC", "(FLAC", ".FLAC",
            " MP3",  "[MP3",  "(MP3",  ".MP3",
            " WEB-", "[WEB-", ".WEB-",
            " SACD", " AAC",  " ALAC", " OGG",
            "[16bit", "(16bit", ".16bit",
            "[24bit", "(24bit", ".24bit",
            " 320kbps", " 256kbps", " 192kbps", " 128kbps",
        };

        var earliest = -1;
        foreach (var marker in markers)
        {
            var idx = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && (earliest < 0 || idx < earliest))
                earliest = idx;
        }

        return earliest > 0 ? s[..earliest].Trim() : s;
    }

    /// <summary>
    /// Cleans a book release title_clean for use as a metadata search query.
    /// Returns (author, cleanTitle) where author may be null if no "Author - Title" pattern found.
    /// Strips: format extensions (epub/pdf/mobi), language markers (FRENCH/FR),
    /// release group tags (-NOTAG/-RiD), tome/volume markers (T1/Tome 2), trailing year.
    /// </summary>
    private static (string? author, string title) ParseBookQuery(string input)
    {
        var s = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return (null, s);

        // 1. Strip format extensions (whole-word, case-insensitive)
        s = Regex.Replace(s, @"\b(epub|pdf|mobi|azw3?|cbz|cbr|djvu|lit)\b", " ", RegexOptions.IgnoreCase);

        // 2. Strip language markers (whole-word)
        s = Regex.Replace(s, @"\b(FRENCH|FRANCAIS|ANGLAIS|ENGLISH|VOSTFR|VF|VO)\b", " ", RegexOptions.IgnoreCase);

        // 3. Strip release group tags attached with hyphen at end: "-NOTAG", "-RiD", etc.
        //    Match: hyphen + 2-12 alphanums at end-of-string
        s = Regex.Replace(s, @"-[A-Za-z][A-Za-z0-9]{1,11}\s*$", "");

        // 4. Strip tome/volume markers
        //    T1, T01..T99 as standalone token (not part of a longer word)
        s = Regex.Replace(s, @"(?<![A-Za-z])\bT\d{1,2}\b(?![A-Za-z])", " ", RegexOptions.IgnoreCase);
        //    "Tome 3", "Vol 2", "Volume 12"
        s = Regex.Replace(s, @"\b(Tome|Vol\.?|Volume)\s*\d+\b", " ", RegexOptions.IgnoreCase);

        // 5. Strip trailing year
        s = Regex.Replace(s, @"\s*[\(\[]?\s*\b(19|20)\d{2}\b\s*[\)\]]?\s*$", "");

        // 6. Collapse multiple spaces
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();

        // 7. Split "Author - Title" on " - " separator → use title, keep author as hint
        var dashIdx = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 2 && dashIdx < s.Length - 3)
        {
            var leftPart = s[..dashIdx].Trim();
            var rightPart = s[(dashIdx + 3)..].Trim();
            if (!string.IsNullOrWhiteSpace(leftPart) && !string.IsNullOrWhiteSpace(rightPart))
                return (leftPart, rightPart);
        }

        return (null, s);
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



}


