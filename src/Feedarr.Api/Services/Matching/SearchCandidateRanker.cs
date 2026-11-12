using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Matching;

internal enum SearchMediaType { Unknown, Movie, Series, Game, Audio, Book, Comic }
internal enum CandidateDecision { Accept, Ambiguous, Reject }

// Identity is always scoped by provider AND media type; numeric IDs alone are never compared.
internal sealed record SearchIdentity(string Provider, SearchMediaType MediaType, string Id);
internal enum ProviderTitleKind { ResponseTitle, Localized, Original, Alternative }
internal enum ProviderTitleSource { SearchTitle, SearchOriginalTitle, AlternativeTitlesEndpoint }
// Constructed only from provider-owned fields, never from the release or a guessed translation.
internal sealed record OfficialProviderTitle(string Title, SearchIdentity Identity, ProviderTitleKind Kind,
    ProviderTitleSource Source, string? Language = null);
internal sealed record ProviderSearchCandidate(
    SearchIdentity Identity, string Title, int? Year = null,
    IReadOnlyList<string>? AlternativeTitles = null, UnifiedCategory? Category = null,
    int? Season = null, int? Episode = null,
    IReadOnlyList<SearchIdentity>? KnownExternalIdentities = null,
    IReadOnlyList<OfficialProviderTitle>? OfficialTitles = null);

internal sealed record CandidateRankingContext(
    IReadOnlyList<SearchCandidate> Queries, UnifiedCategory Category, SearchMediaType ExpectedMediaType,
    int? ConfirmedYear = null, int? Season = null, int? Episode = null,
    SearchIdentity? TrustedIdentityHint = null, SearchIdentity? EstablishedIdentity = null,
    bool IsLocked = false, bool HasManualCorrection = false);

internal sealed record CandidateRankingPolicy(
    double MinimumTitleSimilarity = 0.90, double AcceptConfidence = 0.92,
    double MinimumConfidenceGap = 0.08, int ParsedYearTolerance = 1,
    int ConfirmedYearTolerance = 0, int MaximumResults = 100,
    bool NumericTitlesRequireTrustedIdentity = true, bool RequireExactTitleForAcceptance = true)
{
    internal void Validate()
    {
        if (!double.IsFinite(MinimumTitleSimilarity) || MinimumTitleSimilarity is <= 0 or > 1 ||
            !double.IsFinite(AcceptConfidence) || AcceptConfidence is <= 0 or > 1 ||
            !double.IsFinite(MinimumConfidenceGap) || MinimumConfidenceGap is <= 0 or > 1 ||
            ParsedYearTolerance < 0 || ConfirmedYearTolerance < 0 || MaximumResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(CandidateRankingPolicy));
    }
}

internal sealed record CandidateContradiction(string Code, bool IsStrong);
internal sealed record RankedSearchCandidate(
    SearchIdentity Identity, int QueryIndex, double Confidence,
    IReadOnlyList<string> PositiveReasons, IReadOnlyList<CandidateContradiction> Contradictions,
    CandidateDecision Decision, CompoundTitleEvidence? CompoundTitle = null, int? TrailingYearHypothesis = null);
internal sealed record CandidateRankingResult(
    CandidateDecision Decision, SearchIdentity? SelectedIdentity, bool PreserveExisting,
    IReadOnlyList<string> Reasons, IReadOnlyList<RankedSearchCandidate> Candidates);

/// <summary>
/// Offline proposal evaluation only. Confidence is an explainable heuristic, not a
/// calibrated probability. No DI registration, provider access, persisted-state read or write.
/// </summary>
internal static class SearchCandidateRanker
{
    internal static CandidateRankingResult Rank(CandidateRankingContext context,
        IReadOnlyList<ProviderSearchCandidate> results, CandidateRankingPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(results);
        policy ??= new();
        policy.Validate();
        if (context.IsLocked || context.HasManualCorrection || context.EstablishedIdentity is not null)
            return Result(CandidateDecision.Ambiguous, "existing-identity-or-lock-preserved", preserve: true);
        if (context.Queries.Count == 0 || context.Queries.Count > SearchCandidateGenerator.MaxCandidates)
            return Result(CandidateDecision.Reject, "invalid-query-count");
        if (results.Count == 0) return Result(CandidateDecision.Reject, "no-provider-results");
        // Never truncate results: doing so could hide a close competing identity.
        if (results.Count > policy.MaximumResults) return Result(CandidateDecision.Reject, "provider-result-limit-exceeded");

        var evaluations = new List<RankedSearchCandidate>();
        foreach (var group in results.GroupBy(r => Key(r.Identity)))
        {
            var rows = group.ToArray();
            var evaluated = rows.SelectMany(r => context.Queries.Select((q, i) => Evaluate(context, q, i, r, policy)))
                .OrderBy(r => r.Decision == CandidateDecision.Reject ? 1 : 0)
                .ThenByDescending(r => r.Confidence).ThenBy(r => r.QueryIndex)
                .ThenBy(r => string.Join("|", r.PositiveReasons), StringComparer.Ordinal)
                .ThenBy(r => string.Join("|", r.Contradictions.Select(c => c.Code)), StringComparer.Ordinal).First();
            // Repeated hits for one identity are not independent votes. Inconsistent
            // metadata for that identity must not be hidden by the most favorable row.
            var conflicts = rows.Select(r => r.Year).Where(v => v.HasValue).Distinct().Count() > 1 ||
                rows.Select(r => r.Season).Where(v => v.HasValue).Distinct().Count() > 1 ||
                rows.Select(r => r.Episode).Where(v => v.HasValue).Distinct().Count() > 1 ||
                rows.Select(r => r.Category).Where(v => v.HasValue).Distinct().Count() > 1;
            if (evaluated.Decision != CandidateDecision.Reject && (conflicts || (rows.Length > 1 && rows.Any(r => context.Queries.All((q) =>
                    Evaluate(context, q, 0, r, policy).Contradictions.Any(c => c.IsStrong))))))
                evaluated = evaluated with { Decision = CandidateDecision.Ambiguous,
                    Contradictions = Array.AsReadOnly(evaluated.Contradictions.Append(new("conflicting-provider-records", true)).ToArray()) };
            evaluations.Add(evaluated);
        }
        var ranked = evaluations.OrderBy(r => r.Decision == CandidateDecision.Reject ? 1 : 0)
            .ThenByDescending(r => r.Confidence).ThenBy(r => Key(r.Identity).Provider, StringComparer.Ordinal)
            .ThenBy(r => r.Identity.MediaType).ThenBy(r => Key(r.Identity).Id, StringComparer.Ordinal).ToArray();
        var eligible = ranked.Where(r => r.Decision != CandidateDecision.Reject).ToArray();
        if (eligible.Length == 0)
            return new(CandidateDecision.Reject, null, false, Array.AsReadOnly(new[] { "all-provider-results-rejected" }), Array.AsReadOnly(ranked));
        if (eligible.Length > 1 && eligible[0].Confidence - eligible[1].Confidence < policy.MinimumConfidenceGap)
        {
            ranked = ranked.Select(r => r.Decision == CandidateDecision.Accept
                ? r with { Decision = CandidateDecision.Ambiguous,
                    Contradictions = Array.AsReadOnly(r.Contradictions.Append(new("close-competing-identities", false)).ToArray()) } : r).ToArray();
            return new(CandidateDecision.Ambiguous, null, false, Array.AsReadOnly(new[] { "close-competing-identities" }), Array.AsReadOnly(ranked));
        }
        var best = eligible[0];
        return new(best.Decision, best.Decision == CandidateDecision.Accept ? best.Identity : null, false,
            Array.AsReadOnly(new[] { best.Decision == CandidateDecision.Accept ? "unique-supported-identity" : "insufficient-independent-evidence" }),
            Array.AsReadOnly(ranked));
    }

    private static RankedSearchCandidate Evaluate(CandidateRankingContext context, SearchCandidate query,
        int queryIndex, ProviderSearchCandidate candidate, CandidateRankingPolicy policy)
    {
        var positive = new List<string>();
        var contradictions = new List<CandidateContradiction>();
        var sufficient = true;
        void Contradict(string code, bool strong = true) => contradictions.Add(new(code, strong));
        var identity = candidate.Identity;
        if (string.IsNullOrWhiteSpace(identity.Provider) || string.IsNullOrWhiteSpace(identity.Id) || identity.MediaType == SearchMediaType.Unknown)
            Contradict("invalid-provider-identity");
        if (context.ExpectedMediaType == SearchMediaType.Unknown) sufficient = false;
        else if (identity.MediaType != context.ExpectedMediaType) Contradict("media-type-mismatch");
        else positive.Add("media-type-agrees");
        if (context.Category != UnifiedCategory.Autre && candidate.Category.HasValue && candidate.Category != context.Category)
            Contradict("category-mismatch");
        else if (candidate.Category == context.Category) positive.Add("category-agrees");

        if (context.Season is < 0 || context.Episode is < 0 || candidate.Season is < 0 || candidate.Episode is < 0)
            Contradict("invalid-episode-metadata");
        if (context.ExpectedMediaType != SearchMediaType.Series && (context.Season.HasValue || context.Episode.HasValue))
            Contradict("episode-context-type-mismatch");
        if (identity.MediaType != SearchMediaType.Series && (candidate.Season.HasValue || candidate.Episode.HasValue))
            Contradict("provider-episode-type-mismatch");

        var trusted = false;
        if (context.TrustedIdentityHint is { } hint)
        {
            var identities = new[] { identity }.Concat(candidate.KnownExternalIdentities ?? Array.Empty<SearchIdentity>()).ToArray();
            var scoped = identities.Where(i => Key(i).Provider == Key(hint).Provider && i.MediaType == hint.MediaType).ToArray();
            if (identities.Any(i => i.MediaType != identity.MediaType)) Contradict("external-identity-type-mismatch");
            if (scoped.Select(i => Key(i).Id).Distinct().Count() > 1) Contradict("conflicting-external-identities");
            trusted = scoped.Any(i => Key(i) == Key(hint));
            if (!trusted && scoped.Length > 0) Contradict("trusted-identity-mismatch");
            else if (!trusted) { Contradict("trusted-identity-unverified", false); sufficient = false; }
            else positive.Add("trusted-identity-agrees");
        }

        var normalizedQuery = TitleNormalizer.NormalizeTitleStrict(query.Title);
        var titles = new[] { (Title: candidate.Title, Alternate: false, Official: (OfficialProviderTitle?)null) }
            .Concat((candidate.AlternativeTitles ?? Array.Empty<string>()).Select(t => (Title: t, Alternate: true, Official: (OfficialProviderTitle?)null)))
            .Concat((candidate.OfficialTitles ?? Array.Empty<OfficialProviderTitle>())
                .Where(t => Key(t.Identity) == Key(identity) && ValidTitleSource(t))
                .Select(t => (Title: t.Title, Alternate: true, Official: (OfficialProviderTitle?)t)))
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .Select(t => (Title: TitleNormalizer.NormalizeTitleStrict(t.Title), RawTitle: t.Title, t.Alternate, t.Official));
        var best = titles.Select(t => (t.Title, t.RawTitle, t.Alternate, t.Official, Score: Similarity(normalizedQuery, t.Title)))
            .OrderByDescending(t => t.Score).ThenBy(t => t.Official is null).ThenBy(t => t.Alternate).ThenBy(t => t.Title, StringComparer.Ordinal)
            .DefaultIfEmpty(("", "", false, null, 0d)).First();
        var compound = best.Score == 1 ? null : OfficialCompoundTitle.Match(normalizedQuery, candidate);
        int? trailingYearHypothesis = null;
        if (compound is not null)
            best = (normalizedQuery, compound.First.Title + " " + compound.Second.Title, true, null, 1d);
        // A bare trailing number remains hypothetical, even if it equals this candidate's date.
        // Provider-owned exact title coverage can lift a false rejection to ambiguity, never acceptance.
        if (best.Score < 1 && query.Year is null && query.YearEvidence == CandidateYearEvidence.None && context.ConfirmedYear is null &&
            string.Equals(identity.Provider, "tmdb", StringComparison.OrdinalIgnoreCase))
        {
            var split = normalizedQuery.LastIndexOf(' ');
            if (split > 0 && normalizedQuery[(split + 1)..] is { Length: 4 } number &&
                number.All(c => c is >= '0' and <= '9') && int.TryParse(number, out var possibleYear) && possibleYear is >= 1900 and <= 2099 && candidate.Year == possibleYear)
            {
                var prefix = normalizedQuery[..split];
                var single = OfficialCompoundTitle.Verified(candidate).FirstOrDefault(t => TitleNormalizer.NormalizeTitleStrict(t.Title) == prefix);
                var pair = single is null ? OfficialCompoundTitle.Match(prefix, candidate) : null;
                if (single is not null || pair is not null)
                {
                    compound = pair;
                    best = (prefix, single?.Title ?? pair!.First.Title + " " + pair.Second.Title, true, single, 1d);
                    trailingYearHypothesis = possibleYear;
                    sufficient = false;
                    Contradict("trailing-year-needs-independent-evidence", false);
                    positive.Add("official-title-with-trailing-year-hypothesis");
                }
            }
        }
        if (compound is not null) positive.Add("official-composed-title-exact");
        if (best.Score < policy.MinimumTitleSimilarity) Contradict("title-similarity-insufficient");
        else positive.Add(best.Score == 1 ? best.Alternate ? "known-alternate-title-agrees" : "exact-title" : "similar-title");
        if (policy.RequireExactTitleForAcceptance && best.Score < 1 && !trusted)
        { Contradict("non-exact-title-needs-independent-evidence", false); sufficient = false; }
        if (best.Score == 1 && best.Official is { } official)
            positive.Add(official.Kind switch
            {
                ProviderTitleKind.Localized => "official-localized-title-exact",
                ProviderTitleKind.Original => "official-original-title-exact",
                ProviderTitleKind.Alternative => "official-alternative-title-exact",
                _ => "official-response-title-exact"
            });
        var queryNumbers = Numbers(trailingYearHypothesis.HasValue ? best.Title : normalizedQuery);
        if (string.Equals(identity.Provider, "tmdb", StringComparison.OrdinalIgnoreCase) &&
            !OfficialCompoundTitle.NumericPunctuationAgrees(query.Title, best.RawTitle)) Contradict("title-numeric-punctuation-mismatch");
        if (!queryNumbers.SequenceEqual(Numbers(best.Title))) Contradict("title-number-mismatch");
        if (normalizedQuery.Length == 0) Contradict("empty-query-title");
        if (policy.NumericTitlesRequireTrustedIdentity && normalizedQuery.Length > 0 &&
            normalizedQuery.All(c => char.IsDigit(c) || c == ' ') && !trusted)
        { Contradict("numeric-title-needs-independent-identity", false); sufficient = false; }

        // Weights are evidence classes, not a promise of statistical precision:
        // title .80, media .10, independent year/identity support up to .10.
        var confidence = .80 * best.Score + (identity.MediaType == context.ExpectedMediaType && identity.MediaType != SearchMediaType.Unknown ? .10 : 0);
        if (trusted) confidence += .10;
        if (query.Year is <= 0 or > 9999 || context.ConfirmedYear is <= 0 or > 9999 ||
            (query.Year is null && query.YearEvidence != CandidateYearEvidence.None)) Contradict("invalid-query-year");
        if (candidate.Year is <= 0 or > 9999) Contradict("invalid-provider-year");
        var year = context.ConfirmedYear ?? query.Year;
        var hypothesis = context.ConfirmedYear is null && query.YearEvidence == CandidateYearEvidence.Hypothesis;
        if (query.Year.HasValue && query.YearEvidence == CandidateYearEvidence.None) Contradict("query-year-evidence-missing");
        if (year.HasValue && candidate.Year.HasValue)
        {
            var distance = Math.Abs((long)year.Value - candidate.Year.Value);
            var tolerance = context.ConfirmedYear.HasValue ? policy.ConfirmedYearTolerance : policy.ParsedYearTolerance;
            if (hypothesis)
            {
                if (distance == 0) positive.Add("hypothesis-year-agrees");
                else Contradict("hypothesis-year-disagrees", false);
                // Hypothesis agreement never earns confirmed/parsed year support.
            }
            else if (distance > tolerance) Contradict(context.ConfirmedYear.HasValue ? "confirmed-year-mismatch" : "parsed-year-mismatch");
            else { confidence += distance == 0 ? .10 : .06; positive.Add(distance == 0 ? "year-agrees" : "parsed-year-nearby"); }
        }
        else if (year.HasValue) { Contradict("provider-year-unavailable", false); sufficient &= trusted; }
        if (hypothesis && !trusted) { Contradict("hypothesis-needs-independent-evidence", false); sufficient = false; }
        CheckEpisode(context.Season, candidate.Season, "season");
        CheckEpisode(context.Episode, candidate.Episode, "episode");
        void CheckEpisode(int? expected, int? actual, string field)
        {
            if (!expected.HasValue) return;
            if (!actual.HasValue) { Contradict(field + "-unavailable", false); sufficient = false; }
            else if (expected != actual) Contradict(field + "-mismatch");
            else positive.Add(field + "-agrees");
        }
        confidence = Math.Round(Math.Clamp(confidence, 0, 1), 6, MidpointRounding.AwayFromZero);
        var decision = contradictions.Any(c => c.IsStrong) ? CandidateDecision.Reject :
            sufficient && confidence >= policy.AcceptConfidence ? CandidateDecision.Accept : CandidateDecision.Ambiguous;
        var canonical = Key(identity);
        return new(new SearchIdentity(canonical.Provider, canonical.Type, canonical.Id), queryIndex, confidence, Array.AsReadOnly(positive.ToArray()), Array.AsReadOnly(contradictions.ToArray()), decision, compound, trailingYearHypothesis);
    }

    internal static bool ValidTitleSource(OfficialProviderTitle title) => title.Kind switch
    {
        ProviderTitleKind.ResponseTitle or ProviderTitleKind.Localized => title.Source == ProviderTitleSource.SearchTitle,
        ProviderTitleKind.Original => title.Source == ProviderTitleSource.SearchOriginalTitle,
        ProviderTitleKind.Alternative => title.Source == ProviderTitleSource.AlternativeTitlesEndpoint,
        _ => false
    };

    private static (string Provider, SearchMediaType Type, string Id) Key(SearchIdentity identity)
        => (identity.Provider?.Trim().ToLowerInvariant() ?? "", identity.MediaType, identity.Id?.Trim() ?? "");
    private static string[] Numbers(string title) => title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(t => t.Any(char.IsDigit)).ToArray();
    private static double Similarity(string query, string title)
    {
        if (query.Length == 0 || title.Length == 0) return 0;
        if (query == title) return 1;
        var a = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var setA = a.ToHashSet(StringComparer.Ordinal);
        var setB = b.ToHashSet(StringComparer.Ordinal);
        var overlap = 2d * setA.Count(setB.Contains) / (setA.Count + setB.Count);
        // Token overlap alone treats "Man Bites Dog" and "Dog Bites Man" as exact.
        // Ordered common tokens preserve word order and repeated words as evidence.
        var ordered = new int[b.Length + 1];
        foreach (var word in a)
        {
            var diagonal = 0;
            for (var j = 1; j <= b.Length; j++)
            {
                var previous = ordered[j];
                ordered[j] = word == b[j - 1] ? diagonal + 1 : Math.Max(ordered[j], ordered[j - 1]);
                diagonal = previous;
            }
        }
        return .5 * overlap + .5 * (2d * ordered[^1] / (a.Length + b.Length));
    }
    private static CandidateRankingResult Result(CandidateDecision decision, string reason, bool preserve = false)
        => new(decision, null, preserve, Array.AsReadOnly(new[] { reason }), Array.Empty<RankedSearchCandidate>());
}
