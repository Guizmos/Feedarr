using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Posters;

// Exact evidence takes precedence over fuzzy margins. Collection, identity ownership
// and positive original-year conflicts remain authoritative; editions are image/release scope.
internal sealed class MovieExactYearPriority
{
    internal const string DecisionReason = "movie_exact_official_title_verified_original_year";
    private static readonly Regex Edition = new(@"\b(?:remaster\w*|resmastered|rm|restored|restoration|restaure\w*|restauration|re\s*release|re\s*issue|ressortie|reedition|edition|director\s*s?\s*cut|extended|unrated|uncut|anniversary|vfcut|vcut|dc)\b", RegexOptions.CultureInvariant);
    private readonly CandidateRankingContext _context;
    private readonly string _raw;
    private readonly VideoSearchCollection? _collection;
    private readonly Func<string, CancellationToken, Task<JsonElement?>> _evidence;
    private readonly Func<string, CancellationToken, Task<JsonElement?>> _titles;
    private readonly Dictionary<string, JsonElement?> _responses = new(StringComparer.Ordinal);
    private int _remainingAliases;
    private readonly bool _provisional;
    internal int DecisionLimit => _collection?.PageLimit ?? VideoSearchCollector.MaximumPages;
    internal List<string> Warnings { get; } = new();
    internal int Requests { get; private set; }
    internal string Outcome { get; private set; } = "not_applicable";
    internal string? ConflictEvidence { get; private set; }
    internal int? VerifiedYear { get; private set; }
    internal int VerifiedContenders { get; private set; }

    internal MovieExactYearPriority(CandidateRankingContext context, string raw, VideoSearchCollection? collection,
        ProviderSearchCandidate[] candidates,
        Func<string, CancellationToken, Task<JsonElement?>> evidence,
        Func<string, CancellationToken, Task<JsonElement?>> titles)
    {
        _context = context; _raw = raw; _collection = collection; _evidence = evidence; _titles = titles;
        Requests = collection?.Counters.SearchRequests ?? 0;
        _remainingAliases = MovieTitleCandidates.EvidenceIdentities(context, candidates).Length;
        _provisional = Plan(candidates, SearchCandidateRanker.Rank(context, candidates), out _, out _, out _);
    }

    internal async Task<JsonElement?> LoadTitlesAsync(string id, CancellationToken ct)
    {
        _remainingAliases--;
        // Reserve all unchanged alias calls and a fallback for this one. Optional
        // evidence must never prevent the legacy enrichment or exceed twelve calls.
        if (_provisional && Requests + _remainingAliases + 2 <= DecisionLimit)
        {
            var response = await EvidenceAsync(id, ct).ConfigureAwait(false);
            if (Aliases(response, id) is { } aliases) return aliases;
        }
        Requests++;
        return await _titles(id, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement?> EvidenceAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_responses.TryGetValue(id, out var cached)) return cached;
        Requests++;
        JsonElement? response;
        try { response = await _evidence(id, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { response = null; }
        _responses[id] = response;
        return response;
    }

    private static JsonElement? Aliases(JsonElement? response, string id)
    {
        if (response is not { ValueKind: JsonValueKind.Object } root ||
            TelevisionCandidates.Number(root, "id")?.ToString(CultureInfo.InvariantCulture) != id ||
            !root.TryGetProperty("alternative_titles", out var block) || block.ValueKind != JsonValueKind.Object ||
            !block.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array ||
            !OwnerMatches(block, id)) return null;
        // Validate before substitution so malformed optional evidence falls back to
        // the existing alias endpoint, rather than changing its error behavior.
        if (titles.EnumerateArray().Any(t => TelevisionCandidates.Text(t, "title") is not { Length: > 0 and <= 512 } value || string.IsNullOrWhiteSpace(value))) return null;
        return JsonSerializer.SerializeToElement(new { id = int.Parse(id, CultureInfo.InvariantCulture), titles });
    }

    internal async Task<CandidateRankingResult> ApplyAsync(ProviderSearchCandidate[] candidates,
        CandidateRankingResult normal, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Plan(candidates, normal, out var selected, out var contenders, out var year))
        {
            // Lifting a contextual numeric veto must not hide a second exact identity
            // behind a legacy Accept that happened to use different punctuation.
            if (Outcome == "exact_year_identity_not_unique" && normal.Decision == CandidateDecision.Accept)
                return normal with { Decision = CandidateDecision.Ambiguous, SelectedIdentity = null,
                    Reasons = new[] { "exact_year_identity_not_unique" },
                    Candidates = normal.Candidates.Select(c => c.Decision == CandidateDecision.Accept ? c with {
                        Decision = CandidateDecision.Ambiguous,
                        Contradictions = c.Contradictions.Append(new CandidateContradiction("exact-year-identity-not-unique", false)).ToArray() } : c).ToArray() };
            return normal;
        }
        var histories = new Dictionary<string, History>(StringComparer.Ordinal);
        foreach (var candidate in contenders.OrderBy(c => c.Identity == selected!.Identity ? 0 : 1))
        {
            var isSelected = candidate.Identity == selected!.Identity;
            if (!_responses.ContainsKey(candidate.Identity.Id) && Requests >= DecisionLimit)
            {
                if (isSelected) { Outcome = "evidence_budget_insufficient"; return normal; }
                Warnings.Add("neighbor_history_budget_unavailable");
                continue;
            }
            var response = await EvidenceAsync(candidate.Identity.Id, ct).ConfigureAwait(false);
            if (!History.TryRead(response, candidate, out var history))
            {
                if (isSelected || PositiveIdentityConflict(response, candidate))
                { Outcome = "release_history_unavailable_or_conflicting"; return normal; }
                Warnings.Add("neighbor_history_unavailable");
                continue;
            }
            histories.Add(candidate.Identity.Id, history!);
        }
        var winner = histories[selected!.Identity.Id];
        if (winner.PrimaryYear != year || winner.FirstOriginalYear != year || !winner.HasOriginalDate)
        { Outcome = "original_year_not_corroborated"; return normal; }
        foreach (var candidate in contenders)
        {
            if (candidate.Identity != selected.Identity && winner.ImdbId is not null &&
                (candidate.KnownExternalIdentities ?? Array.Empty<SearchIdentity>()).Any(i => i.Provider == "imdb" && i.Id == winner.ImdbId))
            { Outcome = "external_identity_collision"; ConflictEvidence = $"tmdb={candidate.Identity.Id};selected={selected.Identity.Id};sameImdb=True"; return normal; }
            if (!histories.TryGetValue(candidate.Identity.Id, out var history)) continue;
            if (history.AmbiguousEdition && !Warnings.Contains("video_edition_same_identity")) Warnings.Add("video_edition_same_identity");
            if (candidate.Identity == selected.Identity) continue;
            if (winner.ImdbId is not null && history.ImdbId == winner.ImdbId)
            {
                Outcome = "external_identity_collision";
                ConflictEvidence = $"tmdb={candidate.Identity.Id};selected={selected.Identity.Id};sameImdb=True";
                return normal;
            }
            if (!history.HasOriginalDate) { Warnings.Add("neighbor_original_year_unavailable"); continue; }
            if (history.FirstOriginalYear == year)
            {
                Outcome = "neighbor_original_year_competes";
                ConflictEvidence = $"tmdb={candidate.Identity.Id};primaryYear={history.PrimaryYear};firstOriginalYear={history.FirstOriginalYear};requestedYear={year};proofs={history.Proofs}";
                return normal;
            }
            if (history.Years.Contains(year) && !Warnings.Contains("secondary_release_in_requested_year")) Warnings.Add("secondary_release_in_requested_year");
        }
        ct.ThrowIfCancellationRequested();
        VerifiedYear = year;
        VerifiedContenders = histories.Count;
        Outcome = "applied";
        return normal with {
            Decision = CandidateDecision.Accept, SelectedIdentity = selected.Identity,
            Reasons = Array.AsReadOnly(new[] { DecisionReason }),
            Candidates = Array.AsReadOnly(normal.Candidates.Select(c => c.Identity == selected.Identity ? c with {
                Decision = CandidateDecision.Accept,
                PositiveReasons = Array.AsReadOnly(c.PositiveReasons.Append("verified-original-release-year-priority").Concat(Warnings).Distinct().ToArray()),
                Contradictions = Array.AsReadOnly(c.Contradictions.Where(x => !ExactOnlyVeto(x.Code)).ToArray())
            } : c).ToArray())
        };
    }

    private bool Plan(ProviderSearchCandidate[] candidates, CandidateRankingResult ranked,
        out ProviderSearchCandidate? selected, out ProviderSearchCandidate[] contenders, out int year)
    {
        selected = null; contenders = Array.Empty<ProviderSearchCandidate>(); year = 0;
        bool No(string reason) { Outcome = reason; return false; }
        if (_context.ExpectedMediaType != SearchMediaType.Movie || _context.IsLocked || _context.HasManualCorrection ||
            _context.EstablishedIdentity is not null || _context.TrustedIdentityHint is not null ||
            _context.RssEvidence is { TmdbId: not null } or { ImdbId: not null } or { ConflictingClaims: true } ||
            _context.Season.HasValue || _context.Episode.HasValue || _context.AirDate is not null)
            return No("scope_or_identity_protected");
        if (_collection is not { Complete: true, SearchMode: "targeted_year" } ||
            _collection.RequiredPages > _collection.PageLimit || _collection.Counters.SearchRequests > _collection.PageLimit)
            return No("complete_neighbor_year_collection_required");
        if (_context.Queries.Count != 1 || _context.Queries[0] is not { YearEvidence: CandidateYearEvidence.Parsed, Year: >= 1801 and <= 2099, Origin: SearchCandidateOrigin.Primary } query)
            return No("unambiguous_parsed_year_required");
        year = query.Year!.Value;
        var normalized = TitleNormalizer.NormalizeTitleStrict(query.Title);
        if (normalized.Length == 0) return No("empty_title");
        var raw = TitleNormalizer.NormalizeTitleStrict(_raw);
        if (raw.StartsWith(normalized + " ", StringComparison.Ordinal)) raw = raw[(normalized.Length + 1)..];
        if (Edition.IsMatch(raw) && !Warnings.Contains("video_release_edition")) Warnings.Add("video_release_edition");
        var years = Regex.Matches(raw, @"\b(?:18|19|20)\d{2}\b");
        if (years.Count != 1 || years[0].Value != year.ToString(CultureInfo.InvariantCulture)) return No("release_year_lexically_ambiguous");
        var parser = new TitleParser();
        var tv = parser.Parse(_raw, UnifiedCategory.Serie);
        if (tv.Season.HasValue || tv.Episode.HasValue || parser.Parse(_raw, UnifiedCategory.Emission).AirDate is not null) return No("episodic_or_broadcast_evidence");
        var eligible = ranked.Candidates.Where(c => c.Confidence >= new CandidateRankingPolicy().AcceptConfidence &&
            c.Contradictions.All(x => ExactOnlyVeto(x.Code))).ToArray();
        if (eligible.Length < 1 || eligible.Length > MovieTitleCandidates.MaximumIdentities ||
            eligible.Any(c => c.Identity.Provider != "tmdb" || c.Identity.MediaType != SearchMediaType.Movie ||
                !int.TryParse(c.Identity.Id, out var id) || id <= 0 || c.Contradictions.Any(x => !ExactOnlyVeto(x.Code))))
            return No("relevant_contender_or_contradiction");
        var parsedYear = year;
        contenders = candidates.Where(c => eligible.Any(e => e.Identity == c.Identity)).ToArray();
        if (contenders.Length != eligible.Length || contenders.Any(c => c.Year is null ||
            Math.Abs(c.Year.Value - parsedYear) > new CandidateRankingPolicy().ParsedYearTolerance || !OfficialExact(c, normalized) ||
            (c.OfficialTitles ?? Array.Empty<OfficialProviderTitle>()).Where(t => t.Identity == c.Identity && t.Kind == ProviderTitleKind.Original && t.Source == ProviderTitleSource.SearchOriginalTitle)
                .Select(t => TitleNormalizer.NormalizeTitleStrict(t.Title)).Distinct(StringComparer.Ordinal).Count() != 1))
            return No("contender_metadata_or_title_unproven");
        var exact = contenders.Where(c => c.Year == parsedYear).ToArray();
        if (exact.Length != 1) return No(exact.Length == 0 ? "exact_year_identity_unavailable" : "exact_year_identity_not_unique");
        if (ranked.Decision == CandidateDecision.Accept) return No("legacy_decision_preserved");
        selected = exact[0];
        var selectedIdentity = selected.Identity;
        if (ranked.Candidates.Single(c => c.Identity == selectedIdentity).Confidence < new CandidateRankingPolicy().AcceptConfidence)
            return No("existing_confidence_insufficient");
        foreach (var veto in ranked.Candidates.Single(c => c.Identity == selectedIdentity).Contradictions)
            if (veto.Code != "close-competing-identities" && !Warnings.Contains(veto.Code + "_warning")) Warnings.Add(veto.Code + "_warning");
        Outcome = "proof_required";
        return true;
    }

    private static bool ExactOnlyVeto(string code) => code is "close-competing-identities" or
        "numeric-title-needs-independent-identity" or "title-numeric-punctuation-mismatch";

    // Keep a complete targeted collection for the exact numeric route. This does not
    // accept an identity; ownership and original-year verification still follow.
    internal static bool HasResolvableNumericCandidate(CandidateRankingResult ranked) =>
        ranked.Candidates.Any(c => c.Confidence >= new CandidateRankingPolicy().AcceptConfidence &&
            c.PositiveReasons.Contains("year-agrees") && c.PositiveReasons.Contains("media-type-agrees") &&
            c.PositiveReasons.Any(p => p.StartsWith("official-", StringComparison.Ordinal) && p.EndsWith("-exact", StringComparison.Ordinal)) &&
            c.Contradictions.Count > 0 && c.Contradictions.All(x => ExactOnlyVeto(x.Code)));

    private bool OfficialExact(ProviderSearchCandidate candidate, string normalized) =>
        OfficialCompoundTitle.Verified(candidate).Any(t => TitleNormalizer.NormalizeTitleStrict(t.Title) == normalized) ||
        OfficialCompoundTitle.Match(normalized, candidate, allowContained: _context.Queries.Any(q => q.YearEvidence == CandidateYearEvidence.Parsed && q.Year == candidate.Year)) is not null ||
        _context.Queries.Any(q => OfficialCompoundTitle.Recognize(q, _context, candidate, out _) is not null);

    private static bool PositiveIdentityConflict(JsonElement? response, ProviderSearchCandidate candidate)
    {
        if (response is not { ValueKind: JsonValueKind.Object } root) return false;
        var id = candidate.Identity.Id;
        if (root.TryGetProperty("id", out _) && TelevisionCandidates.Number(root, "id")?.ToString(CultureInfo.InvariantCulture) != id ||
            root.TryGetProperty("media_type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "movie")) return true;
        if (TelevisionCandidates.Text(root, "release_date") is { } date && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && candidate.Year != parsed.Year) return true;
        if (TelevisionCandidates.Text(root, "original_title") is { Length: > 0 } title &&
            !OfficialCompoundTitle.Verified(candidate).Any(t => t.Kind == ProviderTitleKind.Original && TitleNormalizer.NormalizeTitleStrict(t.Title) == TitleNormalizer.NormalizeTitleStrict(title))) return true;
        return new[] { "release_dates", "alternative_titles" }.Any(field => root.TryGetProperty(field, out var block) && block.ValueKind == JsonValueKind.Object && !OwnerMatches(block, id));
    }

    private static bool OwnerMatches(JsonElement block, string id) =>
        !block.TryGetProperty("id", out _) || TelevisionCandidates.Number(block, "id")?.ToString(CultureInfo.InvariantCulture) == id;

    private sealed record History(int PrimaryYear, int FirstYear, int FirstOriginalYear, HashSet<int> Years, bool HasOriginalDate, bool AmbiguousEdition, string? ImdbId, string Proofs)
    {
        internal static bool TryRead(JsonElement? response, ProviderSearchCandidate candidate, out History? history)
        {
            history = null;
            if (response is not { ValueKind: JsonValueKind.Object } root ||
                TelevisionCandidates.Number(root, "id")?.ToString(CultureInfo.InvariantCulture) != candidate.Identity.Id ||
                root.TryGetProperty("media_type", out var media) && (media.ValueKind != JsonValueKind.String || media.GetString() != "movie") ||
                TelevisionCandidates.Text(root, "status") != "Released" ||
                TelevisionCandidates.Text(root, "release_date") is not { } primary || !DateOnly.TryParseExact(primary, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var primaryDate) ||
                primaryDate.Year != candidate.Year || TelevisionCandidates.Text(root, "original_title") is not { Length: > 0 } original ||
                !(candidate.OfficialTitles ?? Array.Empty<OfficialProviderTitle>()).Any(t => t.Identity == candidate.Identity && t.Kind == ProviderTitleKind.Original &&
                    t.Source == ProviderTitleSource.SearchOriginalTitle && TitleNormalizer.NormalizeTitleStrict(t.Title) == TitleNormalizer.NormalizeTitleStrict(original)) ||
                root.TryGetProperty("alternative_titles", out var titleBlock) &&
                    (titleBlock.ValueKind != JsonValueKind.Object || !OwnerMatches(titleBlock, candidate.Identity.Id)) ||
                !root.TryGetProperty("release_dates", out var block) || block.ValueKind != JsonValueKind.Object || !OwnerMatches(block, candidate.Identity.Id) ||
                !block.TryGetProperty("results", out var countries) || countries.ValueKind != JsonValueKind.Array || countries.GetArrayLength() is 0 or > 300) return false;
            var years = new HashSet<int>(); var first = 9999; var firstOriginal = 9999; var originalDate = false; var ambiguous = false; var total = 0;
            var proofs = new List<string>();
            string? earliestProof = null, editionProof = null;
            var primaryTypes = new HashSet<int>();
            if (ambiguous) proofs.Add("official_title_edition_marker");
            var countryIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var country in countries.EnumerateArray())
            {
                if (TelevisionCandidates.Text(country, "iso_3166_1") is not { Length: 2 } code || code.Any(c => c is < 'A' or > 'Z') || !countryIds.Add(code) ||
                    !country.TryGetProperty("release_dates", out var dates) || dates.ValueKind != JsonValueKind.Array || dates.GetArrayLength() == 0) return false;
                foreach (var date in dates.EnumerateArray())
                {
                    if (++total > 4096 || TelevisionCandidates.Number(date, "type") is not (>= 1 and <= 6) ||
                        TelevisionCandidates.Text(date, "release_date") is not { } text ||
                        !Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$") ||
                        !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
                    var editionMarker = Edition.IsMatch(TitleNormalizer.NormalizeTitleStrict(TelevisionCandidates.Text(date, "note")));
                    var witness = $"{code}:{parsed:yyyy-MM-dd}:type={TelevisionCandidates.Number(date, "type")}:editionMarker={editionMarker}";
                    if (parsed.Year < first) earliestProof = witness;
                    if (parsed.Year == primaryDate.Year) {
                        primaryTypes.Add(TelevisionCandidates.Number(date, "type")!.Value);
                        if (editionMarker) editionProof ??= witness;
                    }
                    years.Add(parsed.Year); first = Math.Min(first, parsed.Year);
                    if (TelevisionCandidates.Number(date, "type") <= 4 && (!editionMarker || parsed.Year == primaryDate.Year)) firstOriginal = Math.Min(firstOriginal, parsed.Year);
                    if (parsed.Year == primaryDate.Year) {
                        originalDate |= TelevisionCandidates.Number(date, "type") <= 4;
                        ambiguous |= Edition.IsMatch(TitleNormalizer.NormalizeTitleStrict(TelevisionCandidates.Text(date, "note")));
                    }
                }
            }
            var imdb = TelevisionCandidates.Text(root, "imdb_id");
            if (imdb is not null && !Regex.IsMatch(imdb, @"^tt\d{7,10}$")) return false;
            if (earliestProof is not null) proofs.Add("earliest=" + earliestProof);
            if (editionProof is not null) proofs.Add("edition=" + editionProof);
            proofs.Add("primaryTypes=" + string.Join(",", primaryTypes.Order()));
            history = new(primaryDate.Year, first, firstOriginal, years, originalDate, ambiguous, imdb, string.Join("|", proofs));
            return true;
        }
    }
}
