using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Feedarr.Api.Services.Matching.Replay;

internal static class ReplayEvidence
{
    internal static string? SafeLocale(string? locale) => locale is "fr-FR" or "en-US" ? locale : null;
    internal static string QueryPlan(IReadOnlyList<SearchCandidate> queries, string mediaType) => Hash(new
    {
        mediaType, queries = queries.Select(q => new { title = MatchingShadowDiagnostics.SafeText(q.Title), q.Year, evidence = q.YearEvidence.ToString() }).ToArray()
    });
    internal static string Provider(IReadOnlyList<ProviderSearchCandidate> candidates)
    {
        // Semantic projection only: no score/decision, URL, poster, credential or raw provider payload.
        var rows = candidates.Select(c => JsonSerializer.Serialize(new
        {
            provider = c.Identity.Provider, type = c.Identity.MediaType.ToString(), id = c.Identity.Id,
            title = MatchingShadowDiagnostics.SafeText(c.Title), c.Year, c.Season, c.Episode,
            titles = (c.OfficialTitles ?? Array.Empty<OfficialProviderTitle>()).Select(t => JsonSerializer.Serialize(new
            { title = MatchingShadowDiagnostics.SafeText(t.Title), kind = t.Kind.ToString(), source = t.Source.ToString(), language = MatchingShadowDiagnostics.SafeText(t.Language) }))
                .Distinct().Order(StringComparer.Ordinal).ToArray()
        })).Distinct().Order(StringComparer.Ordinal).ToArray();
        return Hash(rows);
    }
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
}
