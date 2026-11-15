using System.Text.Json;

namespace Feedarr.Api.Services.Matching;

internal sealed record VideoEvidenceResponse(JsonElement Value)
{
    internal static bool Complete(string provider, string path, JsonElement value)
    {
        static bool Has(JsonElement v, params string[] fields) => v.ValueKind == JsonValueKind.Object && fields.All(f => v.TryGetProperty(f, out _));
        static bool Id(JsonElement v, string id) => TelevisionCandidates.Number(v, "id")?.ToString(System.Globalization.CultureInfo.InvariantCulture) == id;
        var parts = path.Split('?')[0].Split('/');
        if (path.StartsWith("search/shows?", StringComparison.Ordinal))
            return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() < 50 && value.EnumerateArray().All(v =>
                v.TryGetProperty("show", out var show) && Has(show, "id", "name", "premiered") && TelevisionCandidates.Number(show, "id") is > 0 && TelevisionCandidates.Text(show, "name") is { Length: > 0 });
        if (parts.Length < 2) return false;
        if (parts.Length == 2)
            return Id(value, parts[1]) && (provider == "tmdb"
                ? Has(value, "name", "first_air_date", "seasons", "external_ids") && (!path.Contains("alternative_titles") || Has(value, "alternative_titles"))
                : Has(value, "name", "premiered", "externals"));
        if (provider == "tmdb" && parts.Length == 6 && int.TryParse(parts[3], out var season) && int.TryParse(parts[5], out var episode))
            return Has(value, "id", "show_id", "season_number", "episode_number", "air_date") &&
                TelevisionCandidates.Number(value, "id") is > 0 && IdShow(value, parts[1]) &&
                TelevisionCandidates.Number(value, "season_number") == season && TelevisionCandidates.Number(value, "episode_number") == episode;
        return provider == "tvmaze" && parts[2] is "episodes" or "episodesbydate" && value.ValueKind == JsonValueKind.Array &&
            value.EnumerateArray().All(e => Has(e, "id", "season", "number", "airdate") && TelevisionCandidates.Number(e, "id") is > 0 &&
                e.TryGetProperty("_links", out var links) && links.TryGetProperty("show", out var show) &&
                TelevisionCandidates.Text(show, "href") == "https://api.tvmaze.com/shows/" + parts[1]);
    }
    private static bool IdShow(JsonElement value, string id) => TelevisionCandidates.Number(value, "show_id")?.ToString(System.Globalization.CultureInfo.InvariantCulture) == id;
}
