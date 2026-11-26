using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    // Called only after this release's own V2 acceptance, inside its publication lease.
    private async Task<PosterFetchResult?> TryPublishSharedVideoPosterAsync(PosterFetchRoutingContext context,
        string provider, string providerId, int? tmdbId, string type, TmdbClient.DetailsResult details, CancellationToken ct, Action<string, string?>? diagnosticStage = null, string? selectedImagePath = null)
    {
        if (context.ForceRefresh) return null;
        var key = provider == "tmdb" ? $"tmdb:{type}:{providerId}" : $"{provider}:{providerId}";
        var store = provider == "tmdb" ? $"tmdb-{type}-{providerId}" : $"{provider}-{providerId}";
        var resolver = GetStoreResolver();
        if (!resolver.TryResolveStoreDir(store, out var directory)) return null;
        var sem = GetStoreLock(store);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        string? displaced = null;
        try
        {
            if (!TryResolveStoreOriginalPath(store, out var original) ||
                File.GetLastWriteTimeUtc(original) < DateTime.UtcNow.AddDays(-1) ||
                PosterThumbService.SupportedWidths.Any(w => !File.Exists(Path.Combine(directory, $"w{w}.webp")) || new FileInfo(Path.Combine(directory, $"w{w}.webp")).Length == 0))
                return null;
            var hash = ComputeSha256Hex(await File.ReadAllBytesAsync(original, ct).ConfigureAwait(false));
            foreach (var donorId in _releases.GetPosterStoreDonors(store))
            {
                var donor = _releases.GetForPoster(donorId);
                if (donor is null || donor.ManualMatchProvider is not null || donor.ExtSnapshotVersion != 1 ||
                    donor.ExtProvider != provider || donor.ExtProviderId != providerId || donor.PosterProvider != provider ||
                    donor.PosterProviderId != providerId || donor.PosterKey != key || donor.PosterStoreDir != store ||
                    donor.PosterHash != hash || string.IsNullOrWhiteSpace(donor.PosterPath) || string.IsNullOrWhiteSpace(donor.PosterFile) ||
                    Path.GetFileName(donor.PosterFile) != donor.PosterFile ||
                    (provider == "tmdb" && (donor.TmdbId != tmdbId || donor.TmdbMediaType != type))) continue;
                // TVmaze has no localized image selection. TMDB's lower-priority image
                // is reusable only after the cached selection confirms FR is unavailable.
                if (provider == "tmdb" && !string.Equals(donor.PosterLang, _tmdb.PreferredPosterLanguage, StringComparison.OrdinalIgnoreCase) &&
                    selectedImagePath != donor.PosterPath) continue;
                var legacy = Path.Combine(PostersDirAbs, donor.PosterFile);
                if (!File.Exists(legacy) || ComputeSha256Hex(await File.ReadAllBytesAsync(legacy, ct).ConfigureAwait(false)) != hash) continue;
                _releases.ValidateMatching(context.ReleaseId);
                ct.ThrowIfCancellationRequested();
                displaced = _releases.GetForPoster(context.ReleaseId)?.PosterStoreDir;
                diagnosticStage?.Invoke("publication", null);
                _releases.PublishAcceptedVideo(context.ReleaseId, provider, providerId, tmdbId, type,
                    donor.PosterPath, donor.PosterFile, details, sharedPoster: donor);
                return new(true, 200, new { ok=true, reused=true, posterFile=donor.PosterFile, storePending=false,
                    posterUrl=$"/api/posters/release/{context.ReleaseId}" }, context.SourceId) { PublicationCommitted=true };
            }
            return null;
        }
        finally
        {
            sem.Release();
            // Never hold the new store's lock while collecting a displaced store.
            if (displaced != store) TryCleanupDisplacedStoreDir(displaced);
        }
    }
}
