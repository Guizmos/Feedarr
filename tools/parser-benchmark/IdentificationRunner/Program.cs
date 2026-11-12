using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Tmdb;

if (args.Length != 1) return 2;
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
options.Converters.Add(new JsonStringEnumConverter());
using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
if (document.RootElement.TryGetProperty("captureComparison", out var captures))
{
    var replay = JsonSerializer.Deserialize<CaptureReplayInput[]>(captures.GetRawText(), options)!;
    Console.WriteLine(JsonSerializer.Serialize(new { decisions = replay.Select(r => CaptureComparison.Evaluate(r, options)).ToArray() }, options));
    return 0;
}
var entries = document.RootElement.GetProperty("corpus").GetProperty("entries").EnumerateArray().ToArray();
var recorded = document.RootElement.TryGetProperty("responses", out var responses) ? responses : default;
var parser = new TitleParser();

async Task<object> Replay(JsonElement entry)
{
    var id = entry.GetProperty("id").GetString()!;
    var category = Enum.Parse<UnifiedCategory>(entry.GetProperty("category").GetString()!);
    var association = entry.GetProperty("association");
    bool Number(string key) => association.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.Number && x.GetInt32() > 0;
    bool hasStoredId = Number("tmdbId") || Number("tvdbId") ||
        (association.TryGetProperty("providerId", out var pi) && pi.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pi.GetString()));
    var manual = association.GetProperty("manualMatch").GetBoolean() ||
        (entry.TryGetProperty("manualTitle", out var mt) && mt.GetBoolean());
    var stored = entry.GetProperty("stored");
    var raw = entry.GetProperty("raw").GetString()!;
    var parsed = parser.Parse(raw, category);
    var proposals = SearchCandidateGenerator.Generate(new(raw, category, hasStoredId, manual));
    var shadow = SearchCandidateGenerator.Generate(new(raw, category)); // Diagnostics only, never eligible for applying an identity.
    var media = UnifiedCategoryMappings.ToMediaType(category);
    if (media == "unknown" && stored.TryGetProperty("MediaType", out var sm) && sm.ValueKind == JsonValueKind.String) media = sm.GetString()!;
    if (category == UnifiedCategory.Emission) media = "series";
    var expected = media switch { "movie" => SearchMediaType.Movie, "series" => SearchMediaType.Series,
        "game" => SearchMediaType.Game, _ => SearchMediaType.Unknown };
    var context = new CandidateRankingContext(proposals, category, expected, Season: parsed.Season, Episode: parsed.Episode,
        IsLocked: hasStoredId, HasManualCorrection: manual);
    string? storedTitle = stored.TryGetProperty("TitleClean", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
    int? storedYear = stored.TryGetProperty("Year", out var sy) && sy.ValueKind == JsonValueKind.Number ? sy.GetInt32() : null;
    int? StoredNumber(string name) => stored.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : null;
    var historicalTitle = string.IsNullOrWhiteSpace(storedTitle) ? "" : HistoricalAdapter.CleanStoredTitle(storedTitle);
    var normalized = TitleNormalizer.NormalizeTitle(storedTitle);
    var ambiguity = TitleAmbiguityEvaluator.Evaluate(normalized, media, storedYear);
    IReadOnlyList<PlannedQuery> plan = Array.Empty<PlannedQuery>();
    var planScope = "unsupported-category";
    if (historicalTitle.Length > 0 && category == UnifiedCategory.JeuWindows)
    {
        var query = HistoricalAdapter.GameQuery(historicalTitle);
        plan = new[] { new PlannedQuery("igdb", "game", query, storedYear, 1), new PlannedQuery("rawg", "game", query, storedYear, 1) };
        planScope = "conditional-game-providers-configuration-and-fallback-unavailable";
    }
    else if (historicalTitle.Length > 0 && media is "movie" or "series")
    {
        plan = await new HistoricalAdapter().TmdbPlan(historicalTitle, storedYear, media, category == UnifiedCategory.Emission);
        planScope = "tmdb-network-query-plan-only-cache-routing-configuration-not-replayed";
    }
    var hasResponse = recorded.ValueKind == JsonValueKind.Object && recorded.TryGetProperty(id, out _);
    CandidateRankingResult? ranking = null;
    object? historicalDecision = null;
    var availability = "provider-responses-unavailable";
    if (manual || hasStoredId)
    {
        ranking = SearchCandidateRanker.Rank(context, Array.Empty<ProviderSearchCandidate>());
        availability = "preservation-guard-only-not-an-identification-decision";
    }
    else if (hasResponse)
    {
        var hits = JsonSerializer.Deserialize<RecordedHit[]>(recorded.GetProperty(id).GetRawText(), options)!;
        ranking = SearchCandidateRanker.Rank(context, hits.Select(h => h.Hit).ToArray());
        availability = "synthetic-fixture-evaluated";
        if (category != UnifiedCategory.JeuWindows && (media is "movie" or "series") && hits.All(h => h.Hit.Identity.Provider == "tmdb"))
        {
            var tmdbHits = hits.Where(h => h.Hit.Identity.Provider == "tmdb").Select(h => new TmdbClient.SearchResult(
                int.Parse(h.Hit.Identity.Id), h.Hit.Title, h.Hit.AlternativeTitles?.FirstOrDefault(), h.HasPoster ? "recorded-presence" : null,
                h.Hit.Identity.MediaType == SearchMediaType.Movie ? "movie" : "tv", h.Hit.Year, null)).ToList();
            historicalDecision = HistoricalAdapter.Assess(historicalTitle, storedYear, category, ambiguity, tmdbHits,
                new(StoredNumber("Season"), StoredNumber("Episode")));
        }
    }
    return new { id, parsed, association, hasStoredId, manual, proposals, shadowProposals = shadow,
        rankingInput = context, rankingAvailability = availability, ranking,
        historical = new { title = historicalTitle, primaryQueryTitle = category == UnifiedCategory.JeuWindows ? HistoricalAdapter.GameQuery(historicalTitle) : historicalTitle, year = storedYear, normalized, ambiguity,
            selectorProviderOrder = PosterProviderSelector.GetExternalProviderOrder(media, category, ambiguity),
            manualGuard = manual, queryPlan = plan, planScope, decision = historicalDecision,
            note = "stored identity is observation, not ground truth; cache/files/configuration not replayed" },
        titleChanged = parsed.TitleClean != storedTitle, yearChanged = parsed.Year != storedYear };
}

// Warm the complete offline replay; no network, database, cache or filesystem branch.
foreach (var entry in entries) await Replay(entry);
var rows = new List<object>(); var times = new List<double>(); var allocations = new List<long>();
foreach (var entry in entries)
{
    object? stable = null; string? serialized = null;
    for (var i = 0; i < 3; i++)
    {
        var thread = Environment.CurrentManagedThreadId;
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
        var result = await Replay(entry);
        times.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Allocation measurement changed thread");
        allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
        var current = JsonSerializer.Serialize(result, options);
        if (serialized is not null && serialized != current) throw new InvalidOperationException("Non-deterministic replay");
        serialized = current; stable = result;
    }
    rows.Add(stable!);
}
times.Sort(); allocations.Sort();
Console.WriteLine(JsonSerializer.Serialize(new { rows, performance = new { iterations = 3, warmup = 1, samples = times.Count,
    medianMicroseconds = times[times.Count / 2], p95Microseconds = times[(int)Math.Ceiling(times.Count * .95) - 1],
    medianAllocatedBytes = allocations[allocations.Count / 2], p95AllocatedBytes = allocations[(int)Math.Ceiling(allocations.Count * .95) - 1],
    replayMilliseconds = times.Sum() / 1000, runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString() } }, options));
return 0;
