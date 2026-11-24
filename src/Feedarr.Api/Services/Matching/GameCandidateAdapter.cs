using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Matching;

// Missing platform data never supplies positive PC evidence.
internal sealed record GameEvidence(SearchIdentity Identity, bool? Pc, string? Edition,
    IReadOnlyList<SearchIdentity> RelatedIdentities, string? Kind = null);
internal sealed record GameObservation(ProviderSearchCandidate Candidate, string? Image, TmdbClient.DetailsResult Details);

// Thin provider projection. Decisions and thresholds belong exclusively to SearchCandidateRanker.
internal static class GameCandidateAdapter
{
    private static readonly Regex Edition = new(@"\b(remaster(?:ed)?|remake|(?:(?:digital\s+)?deluxe|ultimate|definitive|complete|collector[’']?s|gold|enhanced)(?:\s+edition)|game\s+of\s+the\s+year(?:\s+edition)?|goty(?:\s+edition)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Words(string title) => title.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');
    internal static string? EditionOf(string? title)
    {
        var editions = Edition.Matches(Words(title ?? "")).Select(m => Regex.Replace(m.Value.ToLowerInvariant(), @"\s+", " ").Replace("remastered", "remaster").Replace("game of the year", "goty").Replace(" edition", "").Replace("digital deluxe", "deluxe")).Distinct().Order().ToArray();
        return editions.Length == 0 ? null : string.Join("|", editions);
    }
    private static string BaseTitle(string title) => Regex.Replace(Edition.Replace(Words(title), " "), @"\s+", " ").Trim();
    internal static CandidateRankingContext Context(string raw)
    {
        var queries = SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.JeuWindows));
        var edition = EditionOf(raw);
        var parsed = new Feedarr.Api.Services.Titles.TitleParser().Parse(raw, UnifiedCategory.JeuWindows);
        var bareDeluxe = edition is null && parsed.ReleaseGroup is not null &&
            queries.Any(q => Regex.IsMatch(q.Title, @"(?i)^.+\s+Deluxe$"));
        if (bareDeluxe) edition = "deluxe";
        return new(queries.Select(q => q with { Title = bareDeluxe
            ? Regex.Replace(BaseTitle(q.Title), @"(?i)\s+Deluxe$", "") : BaseTitle(q.Title) }).ToArray(),
            UnifiedCategory.JeuWindows, SearchMediaType.Game, GameEdition: edition);
    }
    internal static IReadOnlyList<GameObservation> Project(string provider, JsonElement root, bool completeIgdbCollection = false)
    {
        JsonElement data;
        if (provider == "igdb")
        {
            data = root;
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > (completeIgdbCollection ? 100 : 25)) throw new InvalidDataException("game_search_coverage_unavailable");
        }
        else
        {
            if (!root.TryGetProperty("results", out data) || data.ValueKind != JsonValueKind.Array ||
                Number(root, "count") != data.GetArrayLength() || data.GetArrayLength() > 25 ||
                !root.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("game_search_coverage_unavailable");
        }
        var rows = new List<GameObservation>();
        foreach (var item in data.EnumerateArray())
        {
            if (Number(item, "id") is not > 0 || Text(item, "name") is not { Length: > 0 } name) throw new InvalidDataException("game_search_identity_invalid");
            var identity = new SearchIdentity(provider, SearchMediaType.Game, Number(item, "id")!.Value.ToString(CultureInfo.InvariantCulture));
            bool? pc = null;
            if (item.TryGetProperty("platforms", out var platforms) && platforms.ValueKind == JsonValueKind.Array && platforms.GetArrayLength() > 0)
            {
                var ids = platforms.EnumerateArray().Select(p => provider == "igdb" ? Integer(p) : p.TryGetProperty("platform", out var platform) ? Number(platform, "id") : null).ToArray();
                if (ids.All(id => id is > 0)) pc = ids.Contains(provider == "igdb" ? 6 : 4);
            }
            string? date = Text(item, "released");
            if (provider == "igdb" && item.TryGetProperty("first_release_date", out var timestamp) && timestamp.TryGetInt64(out var seconds) && seconds > 0)
                date = DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            int? year = DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Year : null;
            var related = new List<SearchIdentity>();
            foreach (var field in new[] { "remakes", "remasters" })
                if (item.TryGetProperty(field, out var relations) && relations.ValueKind == JsonValueKind.Array)
                    related.AddRange(relations.EnumerateArray().Where(r => Integer(r) is > 0).Select(r => new SearchIdentity(provider, SearchMediaType.Game, Integer(r)!.Value.ToString(CultureInfo.InvariantCulture))));
            if (Number(item, "version_parent") is > 0) related.Add(new(provider, SearchMediaType.Game, Number(item, "version_parent")!.Value.ToString(CultureInfo.InvariantCulture)));
            var version = Text(item, "version_title");
            var kind = item.TryGetProperty("game_type", out var gameType) ? Text(gameType, "type") : null;
            // Provider nature (Remake/Remaster) is not a commercial edition claim.
            var edition = EditionOf(name + " " + (version is null ? "" : version + " edition"));
            var aliases = new List<OfficialProviderTitle>();
            if (item.TryGetProperty("alternative_names", out var names) && names.ValueKind == JsonValueKind.Array)
                foreach (var alias in names.EnumerateArray())
                    if (Text(alias, "name") is { Length: > 0 } text) aliases.Add(new(BaseTitle(text), identity, ProviderTitleKind.Alternative, ProviderTitleSource.ProviderTitles));
            var evidence = new GameEvidence(identity, pc, edition, related, kind);
            string? image = Text(item, "background_image");
            if (provider == "igdb" && item.TryGetProperty("cover", out var cover)) image = Text(cover, "url");
            if (image?.StartsWith("//", StringComparison.Ordinal) == true) image = "https:" + image;
            image = image?.Replace("t_thumb", "t_cover_big", StringComparison.OrdinalIgnoreCase);
            var genres = item.TryGetProperty("genres", out var genreList) && genreList.ValueKind == JsonValueKind.Array
                ? string.Join(", ", genreList.EnumerateArray().Select(g => Text(g, "name")).Where(g => g is not null)) : null;
            var ratingField = provider == "igdb" ? "total_rating" : "rating";
            double? rating = item.TryGetProperty(ratingField, out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var value) && value > 0 ? value : null;
            // IGDB ratings are percentages; Feedarr details and RAWG use ten/five-point scales respectively.
            if (rating.HasValue) rating = provider == "igdb" ? rating / 10 : rating * 2;
            rows.Add(new(new(identity, BaseTitle(name), year, OfficialTitles: aliases, Category: UnifiedCategory.JeuWindows, Game: evidence), image,
                new(name, Text(item, "summary"), null, genres, date, null, rating, Number(item, provider == "igdb" ? "total_rating_count" : "ratings_count"), null, null, null)));
        }
        if (rows.Select(r => r.Candidate.Identity).Distinct().Count() != rows.Count) throw new InvalidDataException("game_search_identity_invalid");
        return rows;
    }
    private static int? Integer(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
    private static int? Number(JsonElement item, string field) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var value) ? Integer(value) : null;
    private static string? Text(JsonElement item, string field) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
