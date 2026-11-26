using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Matching;

internal enum SearchMediaType { Unknown, Movie, Series, Game, Audio, Book, Comic, Anime }
internal enum CandidateDecision { Accept, Ambiguous, Reject }

// Identity is always scoped by provider AND media type; numeric IDs alone are never compared.
internal sealed record SearchIdentity(string Provider, SearchMediaType MediaType, string Id);
internal enum ProviderTitleKind { ResponseTitle, Localized, Original, Alternative }
internal enum ProviderTitleSource { SearchTitle, SearchOriginalTitle, AlternativeTitlesEndpoint, ProviderTitles }
// Constructed only from provider-owned fields, never from the release or a guessed translation.
internal sealed record OfficialProviderTitle(string Title, SearchIdentity Identity, ProviderTitleKind Kind,
    ProviderTitleSource Source, string? Language = null);
internal sealed record ProviderSearchCandidate(
    SearchIdentity Identity, string Title, int? Year = null,
    IReadOnlyList<string>? AlternativeTitles = null, UnifiedCategory? Category = null,
    int? Season = null, int? Episode = null,
    IReadOnlyList<SearchIdentity>? KnownExternalIdentities = null,
    IReadOnlyList<OfficialProviderTitle>? OfficialTitles = null, TelevisionEvidence? Television = null, AnimeEvidence? Anime = null, GameEvidence? Game = null, AudioEvidence? Audio = null, PrintEvidence? Print = null, IReadOnlyList<string>? OfficialOverviews = null);

internal sealed record CandidateRankingContext(
    IReadOnlyList<SearchCandidate> Queries, UnifiedCategory Category, SearchMediaType ExpectedMediaType,
    int? ConfirmedYear = null, int? Season = null, int? Episode = null,
    SearchIdentity? TrustedIdentityHint = null, SearchIdentity? EstablishedIdentity = null,
    bool IsLocked = false, bool HasManualCorrection = false, string? AirDate = null, string? AnimeFormat = null, string? GameEdition = null, string? AudioArtist = null, string? AudioKind = null, string? AudioEdition = null, string? PrintAuthor = null, string? PrintIsbn = null, string? PrintScope = null, string? PrintNumber = null, string? PrintLanguage = null, string? PrintEdition = null, RssMatchingEvidence? RssEvidence = null);

internal sealed record CandidateRankingPolicy(
    double MinimumTitleSimilarity = 0.90, double AcceptConfidence = 0.92,
    double MinimumConfidenceGap = 0.08, int ParsedYearTolerance = 1,
    int ConfirmedYearTolerance = 0, int MaximumResults = 100,
    bool NumericTitlesRequireTrustedIdentity = true, bool RequireExactTitleForAcceptance = true,
    bool ContextualVideoDecision = false)
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
    CandidateDecision Decision, CompoundTitleEvidence? CompoundTitle = null, int? TrailingYearHypothesis = null, double? EvidenceScore = null);
internal sealed record CandidateRankingResult(
    CandidateDecision Decision, SearchIdentity? SelectedIdentity, bool PreserveExisting,
    IReadOnlyList<string> Reasons, IReadOnlyList<RankedSearchCandidate> Candidates);

/// <summary>
/// Shared, side-effect-free candidate evaluation. Confidence is an explainable heuristic, not a
/// calibrated probability. No provider access, persisted-state read or write.
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
                .ThenByDescending(r => r.EvidenceScore ?? r.Confidence).ThenBy(r => r.QueryIndex)
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
            .ThenByDescending(r => r.EvidenceScore ?? r.Confidence).ThenBy(r => Key(r.Identity).Provider, StringComparer.Ordinal)
            .ThenBy(r => r.Identity.MediaType).ThenBy(r => Key(r.Identity).Id, StringComparer.Ordinal).ToArray();
        var eligible = ranked.Where(r => r.Decision != CandidateDecision.Reject).ToArray();
        if (eligible.Length == 0)
            return new(CandidateDecision.Reject, null, false, Array.AsReadOnly(new[] { "all-provider-results-rejected" }), Array.AsReadOnly(ranked));
        var minimumGap = policy.ContextualVideoDecision && eligible[0].PositiveReasons.Contains("p7-contextual-video") ? .05 : policy.MinimumConfidenceGap;
        if (eligible.Length > 1 && (eligible[0].EvidenceScore ?? eligible[0].Confidence) - (eligible[1].EvidenceScore ?? eligible[1].Confidence) < minimumGap)
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

    internal static ProviderSearchCandidate[] PruneMovieYearContradictionsBeforeBudget(
        CandidateRankingContext context, ProviderSearchCandidate[] results)
    {
        var policy = new CandidateRankingPolicy();
        // Only proven movie types may use definitive year contradictions before the budget.
        // Functional category does not change movie identity; TV, locks and uncertain years stay untouched.
        if (results.Length <= policy.MaximumResults ||
            context.ExpectedMediaType != SearchMediaType.Movie || context.IsLocked || context.HasManualCorrection ||
            context.EstablishedIdentity is not null || context.TrustedIdentityHint is not null ||
            context.ConfirmedYear.HasValue || context.AirDate is not null || context.Season.HasValue || context.Episode.HasValue ||
            context.Queries.Count == 0 || context.Queries.Count > SearchCandidateGenerator.MaxCandidates ||
            context.Queries.Any(q => q.YearEvidence != CandidateYearEvidence.Parsed || q.Year is not (> 0 and <= 9999)))
            return results;

        var excluded = new HashSet<(string Provider, SearchMediaType Type, string Id)>();
        foreach (var group in results.GroupBy(r => Key(r.Identity)))
        {
            var rows = group.ToArray();
            // Keep every record of a surviving identity, including conflicting and
            // missing metadata: even a rejected row can make its identity ambiguous.
            if (rows.Any(r => r.Year is not (> 0 and <= 9999) || string.IsNullOrWhiteSpace(r.Title) ||
                    r.Identity.Provider != "tmdb" || r.Identity.MediaType != SearchMediaType.Movie ||
                    string.IsNullOrWhiteSpace(r.Identity.Id)) ||
                rows.Select(r => r.Year).Distinct().Count() != 1)
                continue;
            if (rows.All(r => context.Queries.All(q => Evaluate(context, q, 0, r, policy)
                    .Contradictions.Any(c => c.IsStrong && c.Code == "parsed-year-mismatch"))))
                excluded.Add(group.Key);
        }
        var retained = results.Where(r => !excluded.Contains(Key(r.Identity))).ToArray();
        // Preserve the empty/nonempty rejection distinction and budget diagnosis.
        return retained.Length == 0 || retained.Length == results.Length ? results : retained;
    }

    // An official alias can repair title evidence, never type/year/identity contradictions.
    internal static bool CanGainMovieTitleEvidence(CandidateRankingContext context, ProviderSearchCandidate candidate)
        => context.ExpectedMediaType == SearchMediaType.Movie &&
            candidate.Identity.Provider == "tmdb" && candidate.Identity.MediaType == SearchMediaType.Movie &&
            context.Queries.Any(q => Evaluate(context, q, 0, candidate, new CandidateRankingPolicy()).Contradictions
                .All(c => !c.IsStrong || c.Code is "title-similarity-insufficient" or "title-number-mismatch" or "title-numeric-punctuation-mismatch"));

    // A dated, ordinal regular-season episode cannot belong to a series premiering
    // beyond the existing year tolerance in the future. Specials, date-only broadcasts,
    // annual numbering and unknown/conflicting premiere years stay visible.
    internal static ProviderSearchCandidate[] PruneTelevisionPremiereContradictions(
        CandidateRankingContext context, ProviderSearchCandidate[] candidates)
    {
        if (context.ExpectedMediaType != SearchMediaType.Series || context.IsLocked || context.HasManualCorrection ||
            context.EstablishedIdentity is not null || context.TrustedIdentityHint is not null ||
            context.Season is not (> 0 and < 100) || context.Episode is not > 0 ||
            !DateOnly.TryParseExact(context.AirDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var episodeDate) ||
            context.RssEvidence is { TmdbId: not null } or { TvdbId: not null } or { ImdbId: not null } or { ConflictingClaims: true }) return candidates;
        return candidates.GroupBy(c => c.Identity).Where(group =>
            group.Key.MediaType != SearchMediaType.Series || group.Key.Provider is not ("tmdb" or "tvmaze") ||
            group.Any(c => c.Year is not (> 0 and <= 9999)) || group.Select(c => c.Year).Distinct().Count() != 1 ||
            group.First().Year <= episodeDate.Year + new CandidateRankingPolicy().ParsedYearTolerance).SelectMany(group => group).ToArray();
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
        if (context.ExpectedMediaType is not (SearchMediaType.Series or SearchMediaType.Anime) && (context.Season.HasValue || context.Episode.HasValue))
            Contradict("episode-context-type-mismatch");
        if (identity.MediaType is not (SearchMediaType.Series or SearchMediaType.Anime) && (candidate.Season.HasValue || candidate.Episode.HasValue))
            Contradict("provider-episode-type-mismatch");

        var tv = identity.MediaType == SearchMediaType.Series && candidate.Television?.Identity == identity ? candidate.Television : null;
        var tvSupport = tv is not null && ((context.Season.HasValue && tv.SeasonExists == true && candidate.Season == context.Season &&
            (!context.Episode.HasValue || tv.EpisodeExists == true && candidate.Episode == context.Episode)) ||
            context.AirDate is not null && tv.AirDate == context.AirDate);
        // A verified cross-catalogue ID link is independent identity evidence, not
        // another vote for a repeated search hit. Explicit episode/date checks still apply.
        var corroboratedTv = tv?.CorroboratingIdentity is { } other && other.MediaType == SearchMediaType.Series &&
            !string.Equals(other.Provider, identity.Provider, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(other.Id) && (candidate.KnownExternalIdentities ?? Array.Empty<SearchIdentity>()).Contains(other);
        if (context.AirDate is not null)
        {
            if (identity.MediaType == SearchMediaType.Series && candidate.Year.HasValue &&
                context.Season is > 0 and < 100 && context.Episode is > 0 &&
                DateOnly.TryParseExact(context.AirDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var episodeDate) && candidate.Year > episodeDate.Year + policy.ParsedYearTolerance)
                Contradict("series-premiere-after-episode-date");
            if (tv?.AirDate is null) { sufficient = false; Contradict("air-date-unavailable", false); }
            else if (tv.AirDate != context.AirDate) Contradict("air-date-mismatch");
            else positive.Add("air-date-agrees");
        }
        var anime = context.Category == UnifiedCategory.Anime && identity.MediaType == SearchMediaType.Anime && candidate.Anime?.Identity == identity ? candidate.Anime : null;
        var animeSupport = false;
        if (context.ExpectedMediaType == SearchMediaType.Anime)
        {
            if (anime?.Format is null) { sufficient = false; Contradict("anime-format-unavailable", false); }
            else
            {
                if (!JikanCandidateAdapter.SupportsFormat(anime.Format)) Contradict("anime-format-unsupported");
                if (anime.AlternativeIdentityUnverified) { sufficient = false; Contradict("anime-alternative-identity-unverified", false); }
                if (context.AnimeFormat is not null && !context.AnimeFormat.Equals(anime.Format, StringComparison.OrdinalIgnoreCase)) Contradict("anime-format-mismatch");
                if ((context.Season.HasValue || context.Episode.HasValue) && anime.Format == "Movie") Contradict("anime-episodic-format-mismatch");
                if (context.Episode.HasValue && anime.Episodes.HasValue && context.Episode > anime.Episodes) Contradict("anime-episode-out-of-range");
                animeSupport = (context.Season.HasValue ? anime.SeasonOrdinal == context.Season : context.Episode.HasValue) && (!context.Episode.HasValue || anime.EpisodeVerified && candidate.Episode == context.Episode);
            }
        }
        var game = context.Category == UnifiedCategory.JeuWindows && identity.MediaType == SearchMediaType.Game && candidate.Game?.Identity == identity ? candidate.Game : null;
        var gameSupport = game?.Pc == true;
        if (context.ExpectedMediaType == SearchMediaType.Game)
        {
            if (game?.Pc is null) { sufficient = false; Contradict("pc-platform-unavailable", false); }
            else if (game.Pc == false) Contradict("pc-platform-mismatch");
            else positive.Add("verified-pc-platform");
            var gameEdition = game?.Edition ?? (context.GameEdition is "remake" or "remaster" &&
                string.Equals(context.GameEdition, game?.Kind, StringComparison.OrdinalIgnoreCase) ? context.GameEdition : null);
            if (context.GameEdition != gameEdition && !(context.GameEdition is null && game?.Edition is "remake" or "remaster" &&
                (context.ConfirmedYear ?? query.Year) is { } gameYear && candidate.Year == gameYear)) Contradict("game-edition-mismatch");
            // Related remakes/editions omitted by provider search remain competitors, not votes.
            if (game?.RelatedIdentities.Count > 0 && context.GameEdition is null && context.ConfirmedYear is null && query.Year is null)
            { sufficient = false; Contradict("game-related-editions-unresolved", false); }
        }
        var audio = context.Category == UnifiedCategory.Audio && identity.MediaType == SearchMediaType.Audio && candidate.Audio?.Identity == identity ? candidate.Audio : null;
        var audioSupport = false;
        if (context.ExpectedMediaType == SearchMediaType.Audio)
        {
            if (string.IsNullOrWhiteSpace(context.AudioArtist) || string.IsNullOrWhiteSpace(audio?.Artist))
            { sufficient = false; Contradict("audio-artist-unavailable", false); }
            else if (TitleNormalizer.NormalizeTitleStrict(context.AudioArtist) != TitleNormalizer.NormalizeTitleStrict(audio.Artist)) Contradict("audio-artist-mismatch");
            else { audioSupport = true; positive.Add("audio-artist-agrees"); }
            if (audio?.Kind is not ("album" or "track" or "single" or "ep")) { sufficient = false; Contradict("audio-release-type-unverified", false); }
            if (context.AudioKind is not null && context.AudioKind != audio?.Kind) Contradict("audio-release-type-mismatch");
            if (context.AudioEdition != audio?.Edition) Contradict("audio-edition-mismatch");
        }
        var print = context.Category is UnifiedCategory.Book or UnifiedCategory.Comic && candidate.Print?.Identity == identity ? candidate.Print : null;
        var printSupport = false;
        if (context.ExpectedMediaType is SearchMediaType.Book or SearchMediaType.Comic)
        {
            if (print is null || print.Scope == "unknown") { sufficient = false; Contradict("print-scope-unavailable", false); }
            if (context.PrintScope is not null && context.PrintScope != print?.Scope) Contradict("print-scope-mismatch");
            if (context.PrintIsbn is not null)
            {
                if (print?.Scope != "edition") Contradict("isbn-requires-edition-identity");
                if (print?.Isbns.Contains(context.PrintIsbn) == true) { printSupport = true; positive.Add("edition-isbn-agrees"); }
                else { sufficient = false; Contradict("edition-isbn-unverified", false); }
            }
            if (context.PrintAuthor is not null)
            {
                if (print?.Authors.Count is not > 0) { sufficient &= printSupport; Contradict("author-unavailable", false); }
                else if (!print.Authors.Any(a => TitleNormalizer.NormalizeTitleStrict(a) == TitleNormalizer.NormalizeTitleStrict(context.PrintAuthor))) Contradict("author-mismatch");
                else { printSupport = true; positive.Add("author-agrees"); }
            }
            if (context.PrintNumber is not null)
            {
                if (print?.Number is null) { sufficient = false; Contradict("print-number-unavailable", false); }
                else if (context.PrintNumber != (print.Number.TrimStart('0') is { Length: > 0 } printedNumber ? printedNumber : "0")) Contradict("print-number-mismatch");
                else { printSupport = true; positive.Add("print-number-agrees"); }
            }
            else if (print?.Scope == "issue") { sufficient = false; Contradict("issue-context-unavailable", false); }
            if (context.PrintLanguage is not null && print?.Scope == "work")
            { sufficient = false; Contradict("language-requires-edition-evidence", false); }
            if (context.PrintLanguage is not null && context.PrintLanguage != print?.Language)
            { sufficient = false; Contradict("print-language-unverified", print?.Language is not null); }
            if (context.PrintEdition != print?.Edition) Contradict("print-edition-mismatch");
        }
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
        var best = titles.Select(t => (t.Title, t.RawTitle, t.Alternate, t.Official, Score: Math.Max(Similarity(normalizedQuery, t.Title),
                Similarity(TitleNormalizer.NormalizeApostropheOmissions(query.Title), TitleNormalizer.NormalizeApostropheOmissions(t.RawTitle)))))
            .OrderByDescending(t => t.Score).ThenBy(t => t.Official is null).ThenBy(t => t.Alternate).ThenBy(t => t.Title, StringComparer.Ordinal)
            .DefaultIfEmpty(("", "", false, null, 0d)).First();
        string? titleVariant = null;
        if (best.Score < 1 && OfficialCompoundTitle.Recognize(query, context, candidate, out titleVariant) is { } recognized)
            best = (normalizedQuery, recognized.Title, true, recognized, 1d);
        var reliableCompound = query.YearEvidence == CandidateYearEvidence.Parsed && query.Year is not null && query.Year == candidate.Year &&
            context.ExpectedMediaType == identity.MediaType;
        var compound = best.Score == 1 ? null : OfficialCompoundTitle.Match(normalizedQuery, candidate, allowContained: reliableCompound) ??
            OfficialCompoundTitle.Match(TitleNormalizer.NormalizeApostropheOmissions(query.Title), candidate, omitApostrophes: true, allowContained: reliableCompound);
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
                    if (!tvSupport) { sufficient = false; Contradict("trailing-year-needs-independent-evidence", false); }
                    positive.Add("official-title-with-trailing-year-hypothesis");
                }
            }
        }
        if (compound is not null) positive.Add("official-composed-title-exact");
        // P7 is a final-decision policy only; collection and TV competitor discovery
        // retain their original policy. Provider ownership, type and independent
        // work-year/episode evidence are required before relaxing lexical equality.
        var officialVideo = best.Official is not null || compound is not null;
        var exactWorkYear = context.AirDate is null && query.YearEvidence == CandidateYearEvidence.Parsed &&
            query.Year is >= 1801 and <= 2099 && candidate.Year == query.Year;
        var structureRequested = context.Season.HasValue || context.Episode.HasValue || context.AirDate is not null;
        var contextualVideo = policy.ContextualVideoDecision && officialVideo && identity.MediaType == context.ExpectedMediaType &&
            (identity.MediaType == SearchMediaType.Movie && exactWorkYear ||
             identity.MediaType == SearchMediaType.Series && (tvSupport || exactWorkYear && !structureRequested));
        if (contextualVideo) positive.Add("p7-contextual-video");
        if (best.Score < (contextualVideo ? .85 : policy.MinimumTitleSimilarity)) Contradict("title-similarity-insufficient");
        else positive.Add(best.Score == 1 ? best.Alternate ? "known-alternate-title-agrees" : "exact-title" : "similar-title");
        if (policy.RequireExactTitleForAcceptance && best.Score < 1 && !trusted && !contextualVideo)
        { Contradict("non-exact-title-needs-independent-evidence", false); sufficient = false; }
        if (best.Score == 1 && best.Official is { } official)
            positive.Add(official.Kind switch
            {
                ProviderTitleKind.Localized => "official-localized-title-exact",
                ProviderTitleKind.Original => "official-original-title-exact",
                ProviderTitleKind.Alternative => "official-alternative-title-exact",
                _ => "official-response-title-exact"
            });
        if (titleVariant is not null) positive.Add(titleVariant);
        var queryNumbers = Numbers(titleVariant is not null || trailingYearHypothesis.HasValue ? best.Title : normalizedQuery);
        if (titleVariant is null && string.Equals(identity.Provider, "tmdb", StringComparison.OrdinalIgnoreCase) &&
            !OfficialCompoundTitle.NumericPunctuationAgrees(query.Title, best.RawTitle)) Contradict("title-numeric-punctuation-mismatch");
        if (!queryNumbers.SequenceEqual(Numbers(best.Title))) Contradict("title-number-mismatch");
        if (normalizedQuery.Length == 0) Contradict("empty-query-title");
        if (policy.NumericTitlesRequireTrustedIdentity && normalizedQuery.Length > 0 &&
            normalizedQuery.All(c => char.IsDigit(c) || c == ' ') && !trusted)
        { Contradict("numeric-title-needs-independent-identity", false); sufficient = false; }

        // Weights are evidence classes, not a promise of statistical precision:
        // title .80, media .10, independent year/identity support up to .10.
        var confidence = .80 * best.Score + (identity.MediaType == context.ExpectedMediaType && identity.MediaType != SearchMediaType.Unknown ? .10 : 0);
        var rssSupport = context.Category is UnifiedCategory.Film or UnifiedCategory.Serie &&
            context.RssEvidence?.Supports(candidate) == true;
        if (rssSupport) positive.Add("rss-id-and-official-synopsis-agree");
        var networkSupport = query.Origin == SearchCandidateOrigin.BroadcastSuffixHypothesis && best.Score == 1 &&
            !string.IsNullOrWhiteSpace(query.ScopeSuffix) && tv?.NetworkNames?.Any(n =>
                TitleNormalizer.NormalizeTitleStrict(n) == TitleNormalizer.NormalizeTitleStrict(query.ScopeSuffix)) == true;
        if (query.Origin == SearchCandidateOrigin.BroadcastSuffixHypothesis && !networkSupport)
        { sufficient = false; Contradict("broadcast-suffix-unverified", false); }
        var programmeIdentity = policy.ContextualVideoDecision && best.Score == 1 && officialVideo && corroboratedTv && !structureRequested;
        if (query.Origin == SearchCandidateOrigin.ProgrammeTitleHypothesis && !tvSupport)
        {
            if (programmeIdentity) positive.Add("programme-subject-programme-identity-warning");
            else { sufficient = false; Contradict("programme-subject-needs-episode-evidence", false); }
        }
        if (networkSupport) positive.Add("verified-broadcast-network-suffix");
        if (trusted || tvSupport || corroboratedTv || networkSupport || animeSupport || gameSupport || audioSupport || printSupport) confidence += .10;
        if (corroboratedTv) positive.Add("verified-cross-provider-tv-identity");
        if (animeSupport) positive.Add("verified-anime-structure");
        if (tvSupport) positive.Add("verified-tv-structure");
        if (query.Year is <= 0 or > 9999 || context.ConfirmedYear is <= 0 or > 9999 ||
            (query.Year is null && query.YearEvidence != CandidateYearEvidence.None)) Contradict("invalid-query-year");
        if (candidate.Year is <= 0 or > 9999) Contradict("invalid-provider-year");
        // Broadcast dates date the episode, not the series premiere.
        var year = context.ConfirmedYear ?? (context.AirDate is null ? query.Year : null);
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
        if (hypothesis && !trusted && !tvSupport && !networkSupport && !animeSupport) { Contradict("hypothesis-needs-independent-evidence", false); sufficient = false; }
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
        // Preserve the decision margin when base confidence is already saturated.
        // RSS corroboration is one additional evidence class, not a trusted ID override.
        var evidenceScore = confidence + (rssSupport ? .10 : 0);
        confidence = Math.Min(1, evidenceScore);
        var decision = contradictions.Any(c => c.IsStrong) ? CandidateDecision.Reject :
            sufficient && confidence >= (contextualVideo ? .90 : policy.AcceptConfidence) ? CandidateDecision.Accept : CandidateDecision.Ambiguous;
        var canonical = Key(identity);
        return new(new SearchIdentity(canonical.Provider, canonical.Type, canonical.Id), queryIndex, confidence, Array.AsReadOnly(positive.ToArray()), Array.AsReadOnly(contradictions.ToArray()), decision, compound, trailingYearHypothesis, evidenceScore);
    }

    internal static bool ValidTitleSource(OfficialProviderTitle title) =>
        title.Source == ProviderTitleSource.ProviderTitles && title.Identity.Provider is ("jikan" or "igdb" or "rawg") || title.Kind switch
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
