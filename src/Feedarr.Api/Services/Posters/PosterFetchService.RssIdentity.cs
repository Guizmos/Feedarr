using System.Globalization;
using System.Text.Json;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Posters;

public sealed partial class PosterFetchService
{
    private async Task<(Feedarr.Api.Models.ReleaseForPoster Snapshot, TmdbClient.DetailsResult Details)?> ValidateHistoricalDonorAsync(ReleaseRepository.IdentityReuseResult reuse, CancellationToken ct)
    {
        if (reuse.Reason != "historical_identity_requires_validation" || reuse.DonorId is not { } id) return null;
        var donor = _releases.GetForPoster(id);
        if (donor?.TmdbId is not > 0 || donor.TmdbMediaType is not ("movie" or "tv")) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Existing localized, identity-checked response cache coalesces both concurrent
            // roots and successive releases/sources. No legacy flag is blindly promoted.
            var details = await RunTmdbAsync(token => donor.TmdbMediaType == "tv"
                ? _tmdb.GetTvPublicationDetailsAsync(donor.TmdbId.Value, token)
                : _tmdb.GetMoviePublicationDetailsAsync(donor.TmdbId.Value, token), timeout.Token).ConfigureAwait(false);
            if (details is not null && LocalIdentityReuse.MetadataCorroborates(donor, details.Title, details.ReleaseDate)) return (donor, details);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or JsonException or OperationCanceledException) { }
        return null;
    }

    // Null means resolution failed: continue through the existing textual V2 path.
    // Once resolved, a publication/poster failure must not select a different work.
    private async Task<PosterFetchResult?> TryFetchRssIdentityAsync(PosterFetchRoutingContext context,
        MatchingShadowState snapshot, RssMatchingEvidence? rss, CancellationToken ct,
        Action<string, string?> diagnosticStage, Action<Exception> diagnosticFailure)
    {
        if (rss is null || rss.ConflictingClaims || snapshot.Protected || context.VideoType is not ("movie" or "tv")) return null;
        var claims = new List<(string Provider, string Id)>();
        if (rss.TmdbId is > 0) claims.Add(("tmdb", rss.TmdbId.Value.ToString(CultureInfo.InvariantCulture)));
        if (rss.ImdbId is { } imdb && System.Text.RegularExpressions.Regex.IsMatch(imdb, @"^tt\d{7,10}$") && imdb[2..].Any(c => c != '0')) claims.Add(("imdb", imdb));
        if (rss.TvdbId is > 0 && context.VideoType == "tv") claims.Add(("tvdb", rss.TvdbId.Value.ToString(CultureInfo.InvariantCulture)));
        if (claims.Count > 0) MatchingAttemptDiagnostics.Current?.SetStrategy("rss_identity");
        using var resolutionTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        resolutionTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        var resolutionToken = resolutionTimeout.Token;
        foreach (var claim in claims)
        {
            var identityResolved = false;
            int? id;
            TmdbClient.DetailsResult? details;
            try
            {
                id = claim.Provider == "tmdb" ? int.Parse(claim.Id, CultureInfo.InvariantCulture) :
                    _releases.FindVerifiedRssTmdbIdentity(claim.Provider, claim.Id, context.VideoType) ??
                    await RunTmdbAsync(token => _tmdb.ResolveRssExternalIdAsync(claim.Provider, claim.Id, context.VideoType, token), resolutionToken).ConfigureAwait(false);
                if (id is not > 0) continue;
                var donor = context.ForceRefresh ? null : _releases.GetTmdbMetadataDonorsByTmdbIds(new[] { id.Value })
                    .FirstOrDefault(d => d.MediaTypeNormalized == (context.VideoType == "tv" ? "series" : "movie") &&
                        !string.IsNullOrWhiteSpace(d.ExtTitle) && _releases.GetForPoster(d.ReleaseId)?.TmdbMediaType == context.VideoType);
                details = donor is not null
                    ? new(donor.ExtTitle, donor.ExtOverview, donor.ExtTagline, donor.ExtGenres, donor.ExtReleaseDate,
                        donor.ExtRuntimeMinutes, donor.ExtRating, donor.ExtVotes, donor.ExtDirectors, donor.ExtWriters, donor.ExtCast)
                    : await RunTmdbAsync(token => _tmdb.GetRssPublicationDetailsAsync(id.Value, context.VideoType, token,
                        () => identityResolved = true), resolutionToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(details?.Title)) continue;
            }
            catch (OperationCanceledException) when (!identityResolved && !ct.IsCancellationRequested)
            {
                _logger.LogInformation("RSS identity resolution timed out releaseId={ReleaseId}", context.ReleaseId);
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (!identityResolved && error is (HttpRequestException or InvalidDataException or JsonException))
            {
                _logger.LogInformation("RSS identity resolution failed releaseId={ReleaseId} provider={Provider} exceptionType={ExceptionType}",
                    context.ReleaseId, claim.Provider, error.GetType().Name);
                continue;
            }
            if (_releases.ReadMatchingShadowState(context.ReleaseId) != snapshot) throw new ReleaseRepository.StaleMatchException();
            _releases.ValidateMatching(context.ReleaseId);
            _logger.LogInformation("RSS identity resolved releaseId={ReleaseId} provider={Provider} tmdbType={TmdbType} tmdbId={TmdbId}",
                context.ReleaseId, claim.Provider, context.VideoType, id.Value);
            MatchingAttemptDiagnostics.Current?.Collection("rss_identity", "identity_resolved");
            diagnosticStage("preparation", "tmdb");
            return await PublishVideoV2Async(context, new(id.Value, details.Title!, null, null, context.VideoType, null, null),
                ct, preservedDetails: details, diagnosticStage: diagnosticStage, diagnosticFailure: diagnosticFailure).ConfigureAwait(false);
        }
        if (claims.Count > 0)
            _logger.LogInformation("RSS identity fallback to V2 releaseId={ReleaseId}", context.ReleaseId);
        return null;
    }
}
