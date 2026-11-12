using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.Matching.Replay;

internal sealed record ReplayRelease(MatchingShadowState State, bool HasPoster, long SourceId);
internal sealed class ReplayTmdbAccess(string key, string language)
{
    internal string Key { get; } = key;
    internal string Language { get; } = language;
    public override string ToString() => "TMDB diagnostic access";
}

public sealed class MatchingReplayReader(Db db, IApiKeyProtectionService protection)
{
    internal MatchingShadowState? Read(long id)
    {
        using var connection = MatchingReadOnly.Open(db);
        return MatchingReadOnly.ReadState(connection, id);
    }
    internal IReadOnlyList<ReplayRelease> Select(int count) => Select(ReplaySelectionRules.Normalize(new(count)), Array.Empty<long>());
    internal IReadOnlyList<ReplayRelease> Select(ReplayRequest request, IReadOnlyList<long> shadowIds)
    {
        var selection = request.Selection!;
        var targetIds = selection.Method == "ids" ? selection.ReleaseIds! : shadowIds;
        if (selection.Method == "shadow_divergences" && targetIds.Count == 0) return Array.Empty<ReplayRelease>();
        var categoryAliases = selection.Categories!.SelectMany(c => c switch
        {
            "films" => new[] { "film", "films" }, "series" => new[] { "serie", "series" },
            "emissions" => new[] { "emission", "emissions" }, _ => new[] { c }
        }).ToArray();
        using var connection = MatchingReadOnly.Open(db);
        var pool = connection.Query<Seed>("""
            WITH candidates AS (
              SELECT r.id Id,r.source_id SourceId,r.title RawTitle,r.title_clean TitleClean,r.year Year,r.unified_category Category,
                CASE WHEN COALESCE(r.poster_file,me.poster_file,r.poster_path,'')<>'' THEN 1 ELSE 0 END HasPoster,
                CASE WHEN COALESCE(me.tmdb_id,r.tmdb_id,0)>0 OR COALESCE(me.tvdb_id,r.tvdb_id,0)>0 OR
                  COALESCE(me.ext_provider_id,r.ext_provider_id,'')<>'' THEN 1 ELSE 0 END HasIdentity
              FROM releases r LEFT JOIN media_entities me ON me.id=r.entity_id
              WHERE lower(r.unified_category) IN @categoryAliases
                AND (@byId=0 OR r.id IN @targetIds)
                AND length(r.title) BETWEEN 1 AND 4096 AND (r.title_clean IS NULL OR length(r.title_clean)<=4096)
            ), ranked AS (
              SELECT *,ROW_NUMBER() OVER(PARTITION BY Category,SourceId,HasPoster,HasIdentity
                ORDER BY ((Id*1103515245+12345)&2147483647),Id) Position FROM candidates
              WHERE @filter='all' OR (@filter='without_poster' AND HasPoster=0) OR
                (@filter='without_identity' AND HasIdentity=0) OR (@filter='with_identity' AND HasIdentity=1)
            ) SELECT Id,SourceId,RawTitle,TitleClean,Year,Category,HasPoster,HasIdentity FROM ranked
              ORDER BY Position,((Id*1103515245+12345)&2147483647),Id LIMIT 5000
            """, new { categoryAliases, byId = selection.Method == "sample" ? 0 : 1, targetIds, filter = selection.Filter }).ToArray();
        IReadOnlyList<Seed> selected;
        if (selection.Method == "ids")
        {
            var byId = pool.ToDictionary(s => s.Id);
            if (targetIds.Any(id => !byId.ContainsKey(id))) throw new ArgumentException("replay_ids_unavailable_or_filtered");
            selected = targetIds.Select(id => byId[id]).ToArray();
        }
        else selected = Sample(pool, request.Count);
        var releases = selected.Select(seed => (seed, state: MatchingReadOnly.ReadState(connection, seed.Id)))
            .Where(x => x.state is not null).Select(x => new ReplayRelease(x.state!, x.seed.HasPoster, x.seed.SourceId)).ToArray();
        if (selection.Method == "ids" && releases.Length != targetIds.Count) throw new ArgumentException("replay_ids_unavailable_or_filtered");
        return releases;
    }
    internal static IReadOnlyList<Seed> Sample(IEnumerable<Seed> seeds, int count)
    {
        // Round-robin categories, then source/poster/identity strata. All tie breaks are stable.
        var categories = seeds.GroupBy(s => CanonicalCategory(s.Category)).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(category => new Queue<Seed>(Interleave(category.GroupBy(s => (s.SourceId, s.HasPoster, s.HasIdentity))
                .OrderBy(g => g.Key.HasPoster).ThenBy(g => g.Key.HasIdentity).ThenBy(g => g.Key.SourceId)
                .Select(g => new Queue<Seed>(g.OrderByDescending(s => Priority(s)).ThenBy(s => Hash(s.Id)).ThenBy(s => s.Id))).ToArray())))
            .ToArray();
        return Interleave(categories).Take(Math.Clamp(count, 0, 200)).ToArray();
    }
    private static IEnumerable<Seed> Interleave(Queue<Seed>[] queues)
    {
        while (queues.Any(q => q.Count > 0)) foreach (var queue in queues) if (queue.TryDequeue(out var seed)) yield return seed;
    }
    private static int Priority(Seed seed)
    {
        var localized = seed.RawTitle?.Any(c => c > 127) == true ||
            System.Text.RegularExpressions.Regex.IsMatch(seed.RawTitle ?? "", @"(?i)^(le|la|les|un|une|des|du|l)[ ._'’]");
        var ambiguity = TitleAmbiguityEvaluator.Evaluate(TitleNormalizer.NormalizeTitle(seed.TitleClean ?? seed.RawTitle), null, seed.Year);
        return (seed.HasPoster ? 0 : 4) + (seed.HasIdentity ? 0 : 4) + (localized ? 1 : 0) + (ambiguity.IsAmbiguous ? 2 : 0);
    }
    private static long Hash(long id) => unchecked(id * 1103515245 + 12345) & 2147483647;
    internal static string CanonicalCategory(string? value) =>
        UnifiedCategoryMappings.TryParse(value, out var c) || UnifiedCategoryMappings.TryParseKey(value, out c)
            ? UnifiedCategoryMappings.ToKey(c) : "other";
    internal sealed class Seed
    {
        public long Id { get; init; }
        public long SourceId { get; init; }
        public string? RawTitle { get; init; }
        public string? Category { get; init; }
        public string? TitleClean { get; init; }
        public int? Year { get; init; }
        public bool HasPoster { get; init; }
        public bool HasIdentity { get; init; }
    }
    internal ReplayTmdbAccess? ReadTmdbAccess()
    {
        using var connection = MatchingReadOnly.Open(db);
        var auth = connection.QueryFirstOrDefault<string>("""
            SELECT auth_json FROM external_provider_instances WHERE lower(provider_key)='tmdb' AND enabled=1
              ORDER BY updated_at_ts DESC,created_at_ts DESC LIMIT 1
            """);
        if (auth is null) return null;
        using var json = JsonDocument.Parse(auth);
        if (!json.RootElement.TryGetProperty("apiKey", out var key) || key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString())) return null;
        if (!protection.TryUnprotect(key.GetString()!, out var clear) || string.IsNullOrWhiteSpace(clear)) return null;
        var language = Feedarr.Api.Models.Settings.UiLanguageCatalog.DefaultMediaInfoLanguage;
        var ui = connection.QueryFirstOrDefault<string>("SELECT value_json FROM app_settings WHERE key='ui'");
        if (ui is not null)
        {
            using var settings = JsonDocument.Parse(ui);
            if (settings.RootElement.TryGetProperty("mediaInfoLanguage", out var locale) && locale.ValueKind == JsonValueKind.String)
                language = Feedarr.Api.Models.Settings.UiLanguageCatalog.NormalizeMediaInfoLanguage(locale.GetString());
        }
        return new(clear.Trim(), language);
    }
}
