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
            return Has(value, "id", "season_number", "episode_number", "air_date") &&
                TelevisionCandidates.Number(value, "id") is > 0 && (!value.TryGetProperty("show_id", out _) || IdShow(value, parts[1])) &&
                TelevisionCandidates.Number(value, "season_number") == season && TelevisionCandidates.Number(value, "episode_number") == episode;
        if (provider == "tvmaze" && parts.Length == 3 && parts[2] == "akas")
            return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(a => TelevisionCandidates.Text(a, "name") is { Length: > 0 and <= 512 });
        return provider == "tvmaze" && parts[2] is "episodes" or "episodesbydate" && value.ValueKind == JsonValueKind.Array &&
            value.EnumerateArray().All(e => Has(e, "id", "season", "number", "airdate") && TelevisionCandidates.Number(e, "id") is > 0 &&
                e.TryGetProperty("_links", out var links) && links.TryGetProperty("show", out var show) &&
                TelevisionCandidates.Text(show, "href") == "https://api.tvmaze.com/shows/" + parts[1]);
    }
    // TVmaze search contains full show records, but fixtures/partial responses may not.
    // Only a complete, same-ID neutral-language record can replace a publication read.
    internal static bool CompleteTvmazePublication(JsonElement value, string id) =>
        Complete("tvmaze", "shows/" + id, value) && TelevisionCandidates.Text(value, "name") is { Length: > 0 } && new[] { "image", "summary", "genres", "runtime" }.All(f => value.TryGetProperty(f, out _)) &&
        value.GetProperty("genres").ValueKind == JsonValueKind.Array && value.GetProperty("genres").EnumerateArray().All(g => g.ValueKind == JsonValueKind.String) &&
        (value.GetProperty("image").ValueKind == JsonValueKind.Null || value.GetProperty("image").ValueKind == JsonValueKind.Object &&
            (TelevisionCandidates.Text(value.GetProperty("image"), "original") is { Length: > 0 } || TelevisionCandidates.Text(value.GetProperty("image"), "medium") is { Length: > 0 })) &&
        value.GetProperty("summary").ValueKind is JsonValueKind.String or JsonValueKind.Null &&
        value.GetProperty("runtime").ValueKind is JsonValueKind.Number or JsonValueKind.Null;

    private static bool IdShow(JsonElement value, string id) => TelevisionCandidates.Number(value, "show_id")?.ToString(System.Globalization.CultureInfo.InvariantCulture) == id;
}
