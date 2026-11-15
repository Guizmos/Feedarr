using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Matching;

internal sealed record PrintEvidence(SearchIdentity Identity, string Scope, IReadOnlyList<string> Authors,
    IReadOnlyList<string> Isbns, string? Language, string? Number, string? Edition, string? Series, string? Publisher);
internal sealed record PrintObservation(ProviderSearchCandidate Candidate, string? Image, TmdbClient.DetailsResult Details);

// Provider facts only; edition identifiers and work/series identifiers are never merged.
internal static class PrintCandidateAdapter
{
    private static readonly Regex Number = new(@"(?i)(?:#|\bissue\s*|\bT(?:ome)?\s*|\bVol(?:ume)?\.?\s*)(?<n>\d+(?:\.\d+)?)(?!\d)");
    internal static string? NumberOf(string raw) => Number.Match(raw) is { Success: true } m ? m.Groups["n"].Value.TrimStart('0') is { Length: > 0 } n ? n : "0" : null;
    internal static string? Edition(string raw) => Regex.Match(raw, @"(?i)\b(?:revised|expanded|anniversary|\d+(?:st|nd|rd|th)\s+edition|\d+(?:e|eme|ème)\s+édition)\b") is { Success: true } m ? m.Value.ToLowerInvariant() : null;
    internal static string? Language(string raw) => Regex.IsMatch(raw, @"(?i)\b(FRENCH|FRANCAIS)\b") ? "fr" : Regex.IsMatch(raw, @"(?i)\b(ENGLISH|ANGLAIS)\b") ? "en" : null;
    internal static string Scope(string raw, UnifiedCategory category)
    {
        var parsed = new TitleParser().Parse(raw, category);
        return TitleNormalizer.NormalizeTitleStrict(parsed.TitleClean ?? "") + "|" + parsed.Year + "|" + NumberOf(raw) + "|" + Edition(raw) + "|" + Language(raw);
    }
    private static string BookTitle(string title) => Regex.Replace(Number.Replace(title, " "), @"\s+", " ").Trim();
    internal static CandidateRankingContext Context(string raw, UnifiedCategory category, string title, string? author, string? isbn)
    {
        var parsed = new TitleParser().Parse(raw, category);
        var scope = category == UnifiedCategory.Book ? isbn is not null || Edition(raw) is not null ? "edition" : null :
            Regex.IsMatch(raw, @"(?i)#|\bissue\b") ? "issue" : NumberOf(raw) is not null ? "tome" : null;
        return new(new[] { new SearchCandidate(category == UnifiedCategory.Book ? BookTitle(title) : title.Trim(), parsed.Year, parsed.Year.HasValue ? CandidateYearEvidence.Parsed : CandidateYearEvidence.None, SearchCandidateOrigin.Primary) },
            category, category == UnifiedCategory.Book ? SearchMediaType.Book : SearchMediaType.Comic,
            PrintAuthor: author, PrintIsbn: isbn, PrintScope: scope, PrintNumber: NumberOf(raw), PrintLanguage: Language(raw), PrintEdition: Edition(raw));
    }
    internal static IReadOnlyList<PrintObservation> Google(JsonElement root)
    {
        var items = Page(root, "items", "totalItems");
        return items.Select(row => {
            var id = Text(row, "id") ?? throw new InvalidDataException("print_identity_invalid");
            if (!row.TryGetProperty("volumeInfo", out var info)) throw new InvalidDataException("print_identity_invalid");
            var identity = new SearchIdentity("googlebooks", SearchMediaType.Book, id);
            var title = Text(info, "title") ?? "";
            var authors = Strings(info, "authors"); var isbns = new List<string>();
            if (info.TryGetProperty("industryIdentifiers", out var identifiers) && identifiers.ValueKind == JsonValueKind.Array)
                foreach (var identifier in identifiers.EnumerateArray())
                    if (Text(identifier, "type") is "ISBN_10" or "ISBN_13" && Text(identifier, "identifier") is { } isbn) isbns.Add(isbn.Replace("-", "").Replace(" ", "").ToUpperInvariant());
            var evidence = new PrintEvidence(identity, "edition", authors, isbns, Text(info, "language"), NumberOf(title + " " + Text(info, "subtitle")), Edition(title + " " + Text(info, "subtitle")), null, Text(info, "publisher"));
            string? image = info.TryGetProperty("imageLinks", out var links) ? Text(links, "thumbnail") ?? Text(links, "smallThumbnail") : null;
            var aliases = Text(info, "subtitle") is { Length: > 0 } subtitle ? new[] { title + ": " + subtitle } : Array.Empty<string>();
            return new PrintObservation(new(identity, BookTitle(title), Year(Text(info, "publishedDate")), AlternativeTitles: aliases, Category: UnifiedCategory.Book, Print: evidence), image,
                new(title, Text(info, "description"), null, string.Join(", ", Strings(info, "categories")), Text(info, "publishedDate"), null, null, null, null, null, string.Join(", ", authors)));
        }).ToArray();
    }
    internal static IReadOnlyList<PrintObservation> OpenLibrary(JsonElement root, bool edition)
    {
        var rows = edition ? new[] { root } : Page(root, "docs", "numFound");
        return rows.Select(row => {
            var key = Text(row, "key")?.TrimStart('/') ?? "";
            if (!Regex.IsMatch(key, edition ? @"^books/OL\d+M$" : @"^works/OL\d+W$")) throw new InvalidDataException("print_identity_invalid");
            var identity = new SearchIdentity("openlibrary", SearchMediaType.Book, key);
            var title = Text(row, "title") ?? "";
            var authors = Strings(row, "author_name"); // Author refs on editions are not author names.
            var isbns = edition ? Strings(row, "isbn_10").Concat(Strings(row, "isbn_13")).ToArray() : Array.Empty<string>();
            var language = Strings(row, "language");
            if (edition && row.TryGetProperty("languages", out var languages) && languages.ValueKind == JsonValueKind.Array)
                language = languages.EnumerateArray().Select(l => Text(l, "key")?.Split('/').LastOrDefault()).Where(l => l is not null).Select(l => l!).ToArray();
            var date = edition ? Text(row, "publish_date") : Int(row, "first_publish_year")?.ToString(CultureInfo.InvariantCulture);
            var evidence = new PrintEvidence(identity, edition ? "edition" : "work", authors, isbns, language.Length == 1 ? language[0] switch { "fre" or "fra" => "fr", "eng" => "en", var l => l } : null,
                NumberOf(title), Text(row, "edition_name"), null, string.Join(", ", Strings(row, "publisher")));
            int? cover = Int(row, "cover_i");
            if (edition && row.TryGetProperty("covers", out var covers) && covers.ValueKind == JsonValueKind.Array && covers.GetArrayLength() > 0 && covers[0].TryGetInt32(out var c)) cover = c;
            var image = cover is > 0 ? $"https://covers.openlibrary.org/b/id/{cover}-L.jpg" : null;
            return new PrintObservation(new(identity, BookTitle(title), Year(date), AlternativeTitles: Strings(row, "alternative_title"), Category: UnifiedCategory.Book, Print: evidence), image,
                new(title, null, null, null, date, null, null, null, null, null, string.Join(", ", authors)));
        }).ToArray();
    }
    internal static IReadOnlyList<PrintObservation> ComicVine(JsonElement root)
    {
        if (Int(root, "status_code") != 1) throw new InvalidDataException("print_provider_unavailable");
        var rows = Page(root, "results", "number_of_total_results");
        return rows.Select(row => {
            if (Int(row, "id") is not > 0) throw new InvalidDataException("print_identity_invalid");
            var kind = Text(row, "resource_type");
            var scope = kind == "issue" ? "issue" : kind == "volume" ? "series" : "unknown";
            var identity = new SearchIdentity("comicvine", SearchMediaType.Comic, (kind is "issue" or "volume" ? kind + ":" : "unknown:") + Int(row, "id"));
            var name = Text(row, "name") ?? "";
            var series = row.TryGetProperty("volume", out var volume) ? Text(volume, "name") : null;
            var number = Text(row, "issue_number");
            var title = scope == "issue" && series is not null && number is not null ? series + " #" + number : name;
            var date = Text(row, "cover_date") ?? Text(row, "start_year");
            var publisher = row.TryGetProperty("publisher", out var pub) ? Text(pub, "name") : null;
            var evidence = new PrintEvidence(identity, scope, Array.Empty<string>(), Array.Empty<string>(), null, number, null, series, publisher);
            string? image = row.TryGetProperty("image", out var links) ? Text(links, "original_url") ?? Text(links, "small_url") : null;
            var aliases = (Text(row, "aliases") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Append(name).Where(t => t.Length > 0).ToArray();
            return new PrintObservation(new(identity, title, Year(date), AlternativeTitles: aliases, Category: UnifiedCategory.Comic, Print: evidence), image,
                new(title, Text(row, "deck"), null, null, date, null, null, null, null, null, null));
        }).ToArray();
    }
    private static JsonElement[] Page(JsonElement root, string field, string countField)
    {
        var count = Int(root, countField) ?? throw new InvalidDataException("print_search_coverage_unavailable");
        if (count == 0 && !root.TryGetProperty(field, out _)) return Array.Empty<JsonElement>();
        if (!root.TryGetProperty(field, out var rows) || rows.ValueKind != JsonValueKind.Array || count != rows.GetArrayLength() || count > 10)
            throw new InvalidDataException("print_search_coverage_unavailable");
        return rows.EnumerateArray().ToArray();
    }
    private static int? Year(string? date) => Regex.Match(date ?? "", @"\b(?:19|20)\d{2}\b") is { Success: true } m ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    private static string[] Strings(JsonElement row, string field) => row.TryGetProperty(field, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : Array.Empty<string>();
    private static string? Text(JsonElement row, string field) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement row, string field) => row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
}
