using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Feedarr.Api.Services.Torznab;

namespace Feedarr.Api.Services.Matching;

// Release-owned indexer claims; the video ID route resolves these before association.
internal sealed record RssMatchingEvidence(int Version, int? TmdbId, int? TvdbId, string? ImdbId, string? Synopsis, bool ConflictingClaims = false)
{
    internal static RssMatchingEvidence From(TorznabItem item)
    {
        string? Attr(string name) => item.Attrs.FirstOrDefault(a => a.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var conflicting = false;
        int? Number(params string[] names)
        {
            var values = names.Select(Attr).Where(v => int.TryParse(v, out var n) && n > 0).Select(v => int.Parse(v!, System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToArray();
            if (values.Length > 1) conflicting = true;
            return values.Length == 1 ? values[0] : null;
        }
        var imdbIds = new[] { Attr("imdbid"), Attr("imdb") }.Where(v => v is not null &&
            Regex.IsMatch(v, @"^(?:tt)?\d{7,10}$", RegexOptions.CultureInvariant))
            .Select(v => "tt" + v!.Replace("tt", "", StringComparison.Ordinal))
            .Where(v => v[2..].Any(c => c != '0')).Distinct().ToArray();
        conflicting |= imdbIds.Length > 1;
        var text = Attr("synopsis") ?? Attr("plot") ?? item.Description;
        var tmdb = Number("tmdbid", "tmdb");
        var tvdb = Number("tvdbid", "tvdb");
        return new(1, tmdb, tvdb, imdbIds.Length == 1 ? imdbIds[0] : null, Clean(text), conflicting);
    }

    internal static RssMatchingEvidence? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32000) return null;
        try { return JsonSerializer.Deserialize<RssMatchingEvidence>(json) is { Version: 1 } value ? value : null; }
        catch (JsonException) { return null; }
    }

    internal bool Supports(ProviderSearchCandidate candidate)
    {
        if (ConflictingClaims || candidate.Identity.MediaType is not (SearchMediaType.Movie or SearchMediaType.Series)) return false;
        var reported = new[] {
            TmdbId is > 0 ? new SearchIdentity("tmdb", candidate.Identity.MediaType, TmdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
            TvdbId is > 0 ? new SearchIdentity("tvdb", candidate.Identity.MediaType, TvdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
            ImdbId is not null ? new SearchIdentity("imdb", candidate.Identity.MediaType, ImdbId) : null
        }.Where(i => i is not null).Cast<SearchIdentity>().ToArray();
        var official = new[] { candidate.Identity }.Concat(candidate.KnownExternalIdentities ?? Array.Empty<SearchIdentity>()).ToArray();
        if (!reported.Any(official.Contains) || reported.Any(r => official.Any(o =>
                o.Provider == r.Provider && o.MediaType == r.MediaType && o.Id != r.Id))) return false;
        var synopsis = TitleNormalizer.NormalizeTitleStrict(Clean(Synopsis));
        // Exact substantial text only. No fuzzy semantic score, technical boilerplate,
        // short tagline, or ID declaration alone can supply independent support.
        if (synopsis.Length < 160 || synopsis.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Count() < 20) return false;
        return (candidate.OfficialOverviews ?? Array.Empty<string>()).Any(o =>
            TitleNormalizer.NormalizeTitleStrict(Clean(o)) == synopsis);
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 12000) return null;
        return Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", " ")), @"\s+", " ").Trim();
    }
}
