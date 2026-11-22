using System.Globalization;
using System.Text.Json;

namespace Feedarr.Api.Services.Matching;

internal static class MovieTitleCandidates
{
    // Same small evidence window as television; search collection remains unchanged.
    internal const int MaximumIdentities = 6;

    internal static async Task<ProviderSearchCandidate[]> EnrichAsync(
        CandidateRankingContext context, ProviderSearchCandidate[] candidates,
        Func<string, CancellationToken, Task<JsonElement?>> get, CancellationToken ct)
    {
        if (context.Category != Models.UnifiedCategory.Film || context.ExpectedMediaType != SearchMediaType.Movie ||
            context.IsLocked || context.HasManualCorrection || context.EstablishedIdentity is not null ||
            SearchCandidateRanker.Rank(context, candidates).Decision == CandidateDecision.Accept)
            return candidates;

        var identities = candidates.Where(c => SearchCandidateRanker.CanGainMovieTitleEvidence(context, c))
            .Select(c => c.Identity).Distinct().ToArray();
        // No partial enrichment followed by acceptance: all repairable competitors or none.
        if (identities.Length == 0 || identities.Length > MaximumIdentities) return candidates;
        var titles = new Dictionary<SearchIdentity, OfficialProviderTitle[]>();
        foreach (var identity in identities)
        {
            ct.ThrowIfCancellationRequested();
            var response = await get(identity.Id, ct).ConfigureAwait(false);
            if (response is not { ValueKind: JsonValueKind.Object } value ||
                TelevisionCandidates.Number(value, "id")?.ToString(CultureInfo.InvariantCulture) != identity.Id ||
                !value.TryGetProperty("titles", out var aliases) || aliases.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("movie_title_evidence_invalid");
            var verified = new List<OfficialProviderTitle>();
            foreach (var alias in aliases.EnumerateArray())
            {
                if (TelevisionCandidates.Text(alias, "title") is not { Length: > 0 and <= 512 } title || string.IsNullOrWhiteSpace(title))
                    throw new InvalidDataException("movie_title_evidence_invalid");
                verified.Add(new(title, identity, ProviderTitleKind.Alternative, ProviderTitleSource.AlternativeTitlesEndpoint));
            }
            titles.Add(identity, verified.Distinct().ToArray());
        }
        // Keep all identities and every conflicting/missing metadata row. Only add scoped evidence.
        return candidates.Select(c => titles.TryGetValue(c.Identity, out var aliases)
            ? c with { OfficialTitles = (c.OfficialTitles ?? Array.Empty<OfficialProviderTitle>()).Concat(aliases).Distinct().ToArray() }
            : c).ToArray();
    }
}
