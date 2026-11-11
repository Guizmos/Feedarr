using System.Globalization;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private static bool IsTypedVideoPoster(string? file, string mediaType) =>
        TmdbIdentity.NormalizeType(mediaType) is { } type &&
        (file?.StartsWith($"tmdb-{type}-", StringComparison.OrdinalIgnoreCase) == true ||
         file?.StartsWith($"fanart-{type}-", StringComparison.OrdinalIgnoreCase) == true);

    private async Task<PosterFetchResult> RefreshManualMatchAsync(ReleaseForPoster snapshot, CancellationToken ct)
    {
        var id = snapshot.Id;
        // Historical manual evidence without an explicit type is preserved, never guessed.
        if (snapshot.ManualMatchProvider == "tmdb" && int.TryParse(snapshot.ManualMatchId, out var tmdbId) &&
            TmdbIdentity.NormalizeType(snapshot.ManualMatchType) is { } type)
        {
            await UpdateExternalDetailsFromTmdbAsync(id, tmdbId, type, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(snapshot.PosterFile) || !File.Exists(Path.Combine(PostersDirAbs, snapshot.PosterFile)))
            {
                var path = await RunTmdbAsync(token => _tmdb.GetPreferredPosterPathAsync(tmdbId, type == "tv" ? "series" : "movie", token), ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var bytes = await RunTmdbAsync(token => _tmdb.DownloadPosterW500Async(path, token), ct).ConfigureAwait(false);
                    if (bytes is { Length: > 0 })
                    {
                        var file = $"tmdb-{type}-{tmdbId}-manual.jpg";
                        _releases.ValidateMatching(id);
                        await SavePosterFileAsync(file, bytes, ct).ConfigureAwait(false);
                        _releases.SavePoster(id, tmdbId, path, file, type);
                        await TrySaveToStoreAsync(id, "tmdb", $"{type}:{tmdbId}", ".jpg", bytes, file, tmdbId, path, ct).ConfigureAwait(false);
                        PosterAudit.UpdateAttemptSuccess(_releases, id, "tmdb", tmdbId.ToString(CultureInfo.InvariantCulture), null, "w500", ComputeSha256Hex(bytes));
                    }
                }
            }
        }
        _releases.ValidateMatching(id);
        var current = _releases.GetForPoster(id)!;
        return new(true, 200, new { ok = true, manual = true, posterFile = current.PosterFile, posterUrl = $"/api/posters/release/{id}" }, snapshot.SourceId);
    }
}
