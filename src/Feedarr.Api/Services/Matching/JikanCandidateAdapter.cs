using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Services.Matching;

internal sealed record AnimeRelation(string Kind, SearchIdentity Identity);
internal sealed record AnimeEvidence(SearchIdentity Identity, string? Format, int? Episodes, string? Status,
    int? SeasonOrdinal, bool EpisodeVerified, IReadOnlyList<AnimeRelation> Relations, bool AlternativeIdentityUnverified = false);
internal sealed record JikanObservation(ProviderSearchCandidate Candidate, JsonElement Details);

// Provider facts and bounded collection only. All identity decisions belong to the shared ranker.
internal static class JikanCandidateAdapter
{
    private static readonly string[] Formats = ["TV", "Movie", "OVA", "ONA", "Special", "TV Special"];
    private static readonly Regex SeasonSuffix = new(@"(?i)\s+(?:(?<n>\d+)(?:st|nd|rd|th)\s+season|season\s+(?<n>\d+))$", RegexOptions.CultureInvariant);
    private static string QueryRaw(string raw) => Regex.Replace(raw, @"(?i)\[(?:TV|Movie|OVA|ONA|Special)\]|(?<![\p{L}\p{N}])(?:OVA|ONA)(?![\p{L}\p{N}])", " ");
    internal static bool SupportsFormat(string? format) => Formats.Contains(format);
    internal static string? FormatHint(string raw)
    {
        // Bare "Special" may be a real title (e.g. Special A), not a release-format tag.
        var match = Regex.Match(raw, @"(?i)\[(?<f>TV|Movie|Special)\]|(?<![\p{L}\p{N}])(?<f>OVA|ONA)(?![\p{L}\p{N}])");
        return match.Success ? Formats.First(f => f.Equals(match.Groups["f"].Value, StringComparison.OrdinalIgnoreCase)) : null;
    }

    internal static async Task<IReadOnlyList<JikanObservation>> CollectAsync(string rawTitle,
        Func<string, CancellationToken, Task<JsonElement?>> get, CancellationToken ct)
    {
        var parsed = new TitleParser().Parse(QueryRaw(rawTitle), UnifiedCategory.Anime);
        var queries = SearchCandidateGenerator.Generate(new(QueryRaw(rawTitle), UnifiedCategory.Anime));
        if (queries.Any(q => q.Title.Length > 200) || Regex.IsMatch(rawTitle,
            @"(?i)(https?://|www\.|@|(?:api[_-]?key|token|passkey|password)\s*[=:]|bearer\s+\S+)"))
            throw new InvalidDataException("jikan_query_unsupported");
        var found = new Dictionary<int, JsonElement>();
        var requests = 0;
        async Task<JsonElement?> Read(string path)
        {
            ct.ThrowIfCancellationRequested();
            if (++requests > 12) throw new InvalidDataException("jikan_http_budget");
            var response = await get(path, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return response;
        }
        foreach (var query in queries.Select(q => q.Title).Distinct(StringComparer.Ordinal))
        {
            int? total = null; var ids = new HashSet<int>();
            for (var page = 1; ; page++)
            {
                var root = await Read($"anime?q={Uri.EscapeDataString(query)}&limit=25&page={page}").ConfigureAwait(false);
                if (root is not { } response || !response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 25 ||
                    !response.TryGetProperty("pagination", out var pagination) || Number(pagination, "current_page") != page ||
                    !pagination.TryGetProperty("has_next_page", out var next) || next.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !pagination.TryGetProperty("items", out var items) || Number(items, "total") is not { } count || count < 0)
                    throw new InvalidDataException("jikan_search_coverage_unavailable");
                if (total.HasValue && total != count) throw new InvalidDataException("jikan_search_changed");
                total = count;
                foreach (var item in data.EnumerateArray())
                {
                    if (Number(item, "mal_id") is not > 0 || !ids.Add(Number(item, "mal_id")!.Value)) throw new InvalidDataException("jikan_search_identity_invalid");
                    var id = Number(item, "mal_id")!.Value;
                    if (found.TryGetValue(id, out var previous) && (Text(previous, "type") != Text(item, "type") || Year(previous) != Year(item)))
                        throw new InvalidDataException("jikan_search_conflicting_identity");
                    found[id] = item;
                }
                if (found.Count > 50) throw new InvalidDataException("jikan_candidate_budget");
                if (!next.GetBoolean())
                {
                    if (ids.Count != total) throw new InvalidDataException("jikan_search_partial");
                    break;
                }
                if (page >= 2) throw new InvalidDataException("jikan_page_budget");
            }
        }
        var results = found.Values.Select(item => {
            var candidate = Project(item);
            var bases = (candidate.OfficialTitles ?? Array.Empty<OfficialProviderTitle>())
                .Where(t => SeasonSuffix.Match(t.Title) is { Success: true } match && int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) == parsed.Season)
                .Select(t => SeasonSuffix.Replace(t.Title, "")).ToArray();
            return new JikanObservation(candidate with { AlternativeTitles = bases }, item);
        }).ToList();
        // Bound enrichment without silently hiding close competitors.
        var plausible = results.Where(r => SearchCandidateRanker.Rank(new(queries, UnifiedCategory.Anime, SearchMediaType.Anime), new[] { r.Candidate })
            .Candidates.Any(c => c.Contradictions.All(x => !x.IsStrong || x.Code == "parsed-year-mismatch"))).ToArray();
        if (plausible.Length > 5) throw new InvalidDataException("jikan_evidence_budget");
        foreach (var row in plausible)
        {
            var id = row.Candidate.Identity.Id;
            var root = await Read($"anime/{id}/full").ConfigureAwait(false);
            if (root is not { } response || !response.TryGetProperty("data", out var full) || Number(full, "mal_id")?.ToString(CultureInfo.InvariantCulture) != id)
                throw new InvalidDataException("jikan_details_identity_unavailable");
            var candidate = Project(full);
            if (candidate.Year != row.Candidate.Year || candidate.Anime?.Format != row.Candidate.Anime?.Format)
                throw new InvalidDataException("jikan_details_conflicting_identity");
            var proof = candidate.Anime!;
            int? ordinal = null;
            var titles = candidate.OfficialTitles ?? Array.Empty<OfficialProviderTitle>();
            var ordinals = titles.Select(t => SeasonSuffix.Match(t.Title)).Where(m => m.Success).Select(m => int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)).Distinct().ToArray();
            if (ordinals.Length > 1) throw new InvalidDataException("jikan_season_conflict");
            if (ordinals.Length == 1) ordinal = ordinals[0];
            // Empty relations may mean unknown data in Jikan. Never infer S01 from absence of a prequel.
            var episodeVerified = false;
            if (parsed.Episode is > 0 && (!parsed.Season.HasValue || ordinal == parsed.Season) && proof.Format != "Movie" && (!proof.Episodes.HasValue || parsed.Episode <= proof.Episodes))
            {
                var episodeRoot = await Read($"anime/{id}/episodes/{parsed.Episode}").ConfigureAwait(false);
                if (episodeRoot is { } er && er.TryGetProperty("data", out var episode) && Number(episode, "mal_id") == parsed.Episode &&
                    DateTimeOffset.TryParse(Text(episode, "aired"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired) && aired <= DateTimeOffset.UtcNow)
                    episodeVerified = true;
            }
            // A declared ordinal can qualify a provider title; no season guessing from list order.
            var bases = ordinal == parsed.Season ? titles.Where(t => SeasonSuffix.IsMatch(t.Title)).Select(t => SeasonSuffix.Replace(t.Title, "")).ToArray() : null;
            candidate = candidate with { Season = ordinal, Episode = episodeVerified ? parsed.Episode : null, AlternativeTitles = bases,
                Anime = proof with { SeasonOrdinal = ordinal, EpisodeVerified = episodeVerified } };
            results[results.IndexOf(row)] = new(candidate, full);
        }
        var enriched = results.Where(r => r.Details.TryGetProperty("relations", out _)).Select(r => r.Candidate.Identity).ToHashSet();
        return results.Select(r => r with { Candidate = r.Candidate with { Anime = r.Candidate.Anime! with {
            AlternativeIdentityUnverified = r.Candidate.Anime!.Relations.Any(relation =>
                relation.Kind is "Alternative version" or "Alternative setting" && !enriched.Contains(relation.Identity)) } } }).ToArray();
    }

    internal static ProviderSearchCandidate Project(JsonElement item)
    {
        var id = Number(item, "mal_id");
        if (id is not > 0 || ProviderTitle(item, "Default", "title") is not { Length: > 0 } title) throw new InvalidDataException("jikan_candidate_identity_invalid");
        var identity = new SearchIdentity("jikan", SearchMediaType.Anime, id.Value.ToString(CultureInfo.InvariantCulture));
        var titles = new List<OfficialProviderTitle> { new(title, identity, ProviderTitleKind.ResponseTitle, ProviderTitleSource.SearchTitle) };
        void Add(string? value, ProviderTitleKind kind, string? language = null)
        {
            if (!string.IsNullOrWhiteSpace(value)) titles.Add(new(value, identity, kind, ProviderTitleSource.ProviderTitles, language));
        }
        Add(ProviderTitle(item, "Japanese", "title_japanese"), ProviderTitleKind.Original, "ja"); Add(ProviderTitle(item, "English", "title_english"), ProviderTitleKind.Localized, "en");
        if (item.TryGetProperty("title_synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
            foreach (var value in synonyms.EnumerateArray()) if (value.ValueKind == JsonValueKind.String) Add(value.GetString(), ProviderTitleKind.Alternative);
        if (item.TryGetProperty("titles", out var all) && all.ValueKind == JsonValueKind.Array)
            foreach (var value in all.EnumerateArray()) Add(Text(value, "title"), ProviderTitleKind.Alternative);
        var related = new List<AnimeRelation>();
        if (item.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
            foreach (var relation in relations.EnumerateArray())
                if (relation.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    foreach (var entry in entries.EnumerateArray())
                        if (Text(entry, "type") == "anime" && Number(entry, "mal_id") is > 0)
                            related.Add(new(Text(relation, "relation") ?? "unknown", new("jikan", SearchMediaType.Anime, Number(entry, "mal_id")!.Value.ToString(CultureInfo.InvariantCulture))));
        var rawFormat = Text(item, "type");
        var format = string.IsNullOrWhiteSpace(rawFormat) ? null : rawFormat;
        return new(identity, title, Year(item), OfficialTitles: titles.Distinct().ToArray(),
            Anime: new(identity, format, Number(item, "episodes"), Text(item, "status"), null, false, related));
    }
    internal static string? ProviderTitle(JsonElement item, string type, string legacyField)
    {
        if (item.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Array)
        {
            var title = titles.EnumerateArray().Where(t => string.Equals(Text(t, "type"), type, StringComparison.OrdinalIgnoreCase))
                .Select(t => Text(t, "title")).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (title is not null) return title;
        }
        return Text(item, legacyField);
    }
    internal static CandidateRankingContext Context(string raw) { var parsed = new TitleParser().Parse(QueryRaw(raw), UnifiedCategory.Anime); return new(SearchCandidateGenerator.Generate(new(QueryRaw(raw), UnifiedCategory.Anime)), UnifiedCategory.Anime, SearchMediaType.Anime, Season: parsed.Season, Episode: parsed.Episode, AnimeFormat: FormatHint(raw)); }
    private static int? Year(JsonElement item)
    {
        var year = Number(item, "year");
        var date = item.TryGetProperty("aired", out var aired) ? Text(aired, "from") : null;
        int? from = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Year : null;
        if (year.HasValue && from.HasValue && year != from) throw new InvalidDataException("jikan_year_conflict");
        return year ?? from;
    }
    internal static int? Number(JsonElement item, string key) => TelevisionCandidates.Number(item, key);
    internal static string? Text(JsonElement item, string key) => TelevisionCandidates.Text(item, key);
}
