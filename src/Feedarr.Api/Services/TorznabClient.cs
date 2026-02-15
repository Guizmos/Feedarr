using System.Net.Http.Headers;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Feedarr.Api.Services.Torznab;

public sealed class TorznabClient
{
    private readonly HttpClient _http;
    private readonly TorznabRssParser _parser;
    private readonly ILogger<TorznabClient> _log;

    public TorznabClient(HttpClient http, TorznabRssParser parser, ILogger<TorznabClient> log)
    {
        _http = http;
        _parser = parser;
        _log = log;
    }

    private static string NormalizeAuthMode(string? mode)
    {
        mode = (mode ?? "").Trim().ToLowerInvariant();
        return mode == "header" ? "header" : "query";
    }

    private static string BuildUrl(string baseUrl, string authMode, string apiKey, Dictionary<string, string> query)
    {
        var uri = new Uri(baseUrl);
        var q = new List<string>();

        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            var existing = uri.Query.TrimStart('?');
            if (!string.IsNullOrWhiteSpace(existing)) q.Add(existing);
        }

        foreach (var kv in query)
            q.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");

        if (authMode == "query" && !string.IsNullOrWhiteSpace(apiKey))
            q.Add($"apikey={Uri.EscapeDataString(apiKey)}");

        var ub = new UriBuilder(uri) { Query = string.Join("&", q) };
        return ub.ToString();
    }

    private static string SanitizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query))
            return url;

        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 0) continue;
            if (string.Equals(kv[0], "apikey", StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(part);
        }

        var ub = new UriBuilder(uri) { Query = string.Join("&", kept) };
        return ub.Uri.ToString();
    }

    private HttpRequestMessage BuildReq(string url, string authMode, string apiKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);

        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Feedarr", "1.0"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        if (authMode == "header" && !string.IsNullOrWhiteSpace(apiKey))
            req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

        return req;
    }

    private async Task EnsureSuccessOrLogAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* best effort */ }
        if (body.Length > 500) body = body[..500];

        _log.LogWarning("HTTP {StatusCode} from {Url}: {Body}",
            (int)resp.StatusCode, resp.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path), body);

        resp.EnsureSuccessStatusCode(); // throw avec le status code standard
    }

    // ✅ retry 1 fois si timeout HttpClient (TaskCanceledException sans cancellation globale)
    // Utilise une factory pour créer un nouveau HttpRequestMessage à chaque tentative
    // (un HttpRequestMessage ne peut être envoyé qu'une seule fois)
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        var req = requestFactory();
        try
        {
            response = await _http.SendAsync(req, ct);
            return response;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            req.Dispose();
            response?.Dispose();
            await Task.Delay(500, ct);
            var retryReq = requestFactory();
            try
            {
                return await _http.SendAsync(retryReq, ct);
            }
            catch
            {
                retryReq.Dispose();
                throw;
            }
        }
        catch
        {
            req.Dispose();
            response?.Dispose();
            throw;
        }
    }

    // =========================
    // CAPS
    // =========================
    public async Task<string> FetchCapsRawAsync(string torznabUrl, string authMode, string apiKey, CancellationToken ct)
    {
        // Prowlarr Torznab proxy ALWAYS uses apikey in query
        var isProwlarr = IsProwlarrTorznabUrl(torznabUrl);
        authMode = isProwlarr ? "query" : NormalizeAuthMode(authMode);

        var url = BuildUrl(torznabUrl, authMode, apiKey, new() { ["t"] = "caps" });

        using var resp = await SendWithRetryAsync(() => BuildReq(url, authMode, apiKey), ct);
        await EnsureSuccessOrLogAsync(resp, ct);

        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<List<(int id, string name, bool isSub, int? parentId)>> FetchCapsAsync(
        string torznabUrl, string authMode, string apiKey, CancellationToken ct)
    {
        var xml = await FetchCapsRawAsync(torznabUrl, authMode, apiKey, ct);

        var doc = XDocument.Parse(xml);
        var cats = new List<(int, string, bool, int?)>();

        foreach (var cat in doc.Descendants().Where(x => x.Name.LocalName == "category"))
        {
            var idAttr = cat.Attribute("id")?.Value;
            var nameAttr = cat.Attribute("name")?.Value;

            if (int.TryParse(idAttr, out var id) && !string.IsNullOrWhiteSpace(nameAttr))
            {
                cats.Add((id, nameAttr!.Trim(), false, null));

                foreach (var sub in cat.Elements().Where(x => x.Name.LocalName == "subcat"))
                {
                    var sidAttr = sub.Attribute("id")?.Value;
                    var snameAttr = sub.Attribute("name")?.Value;

                    if (int.TryParse(sidAttr, out var sid) && !string.IsNullOrWhiteSpace(snameAttr))
                        cats.Add((sid, snameAttr!.Trim(), true, id));
                }
            }
        }

        return cats;
    }

    // Detect Prowlarr Torznab URL pattern: /{id}/api
    // Prowlarr Torznab proxy only supports t=search, not t=recent/t=rss
    private static bool IsProwlarrTorznabUrl(string url)
    {
        // Pattern: baseUrl/{numeric_id}/api
        return System.Text.RegularExpressions.Regex.IsMatch(
            url, @"/\d+/api/?(\?|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // =========================
    // RSS latest (recent/rss/search)
    // =========================
    public async Task<(List<TorznabItem> items, string usedMode)> FetchLatestAsync(
        string torznabUrl, string authMode, string apiKey, int limit, CancellationToken ct, bool allowSearch = true)
    {
        // Prowlarr Torznab proxy ALWAYS uses apikey in query, never header
        // and only supports t=search (not t=recent or t=rss)
        var isProwlarr = IsProwlarrTorznabUrl(torznabUrl);
        if (isProwlarr)
        {
            authMode = "query"; // Force query auth for Prowlarr Torznab
            _log.LogDebug("Detected Prowlarr Torznab URL, forcing authMode=query and t=search");
        }
        else
        {
            authMode = NormalizeAuthMode(authMode);
        }
        limit = Math.Clamp(limit, 1, 200);

        // Prowlarr only supports t=search, Jackett supports all modes
        var modes = isProwlarr
            ? new[] { "search" }
            : (allowSearch ? new[] { "recent", "rss", "search" } : new[] { "recent", "rss" });

        foreach (var mode in modes)
        {
            var query = new Dictionary<string, string>
            {
                ["t"] = mode,
                ["limit"] = limit.ToString()
            };

            if (mode == "search") query["q"] = "";

            var url = BuildUrl(torznabUrl, authMode, apiKey, query);
            if (_log.IsEnabled(LogLevel.Debug))
                _log.LogDebug("Torznab fetch url={Url} mode={Mode}", SanitizeUrl(url), mode);

            try
            {
                using var resp = await SendWithRetryAsync(() => BuildReq(url, authMode, apiKey), ct);
                await EnsureSuccessOrLogAsync(resp, ct);

                var xml = await resp.Content.ReadAsStringAsync(ct);
                var items = _parser.Parse(xml);

                if (items.Count > 0)
                    return (items, mode);
            }
            catch (HttpRequestException ex) when (!isProwlarr && mode != "search")
            {
                // For Jackett, if recent/rss fails, try next mode
                _log.LogDebug("Torznab mode={Mode} failed: {Error}, trying next", mode, ex.Message);
                continue;
            }
        }

        return (new List<TorznabItem>(), "none");
    }

    public async Task<(List<TorznabItem> items, string usedMode, bool usedAggregated)> FetchLatestByCategoriesAsync(
        string torznabUrl,
        string authMode,
        string apiKey,
        int limit,
        IReadOnlyCollection<int> categoryIds,
        CancellationToken ct)
    {
        if (categoryIds is null || categoryIds.Count == 0)
        {
            var (items, mode) = await FetchLatestAsync(torznabUrl, authMode, apiKey, limit, ct);
            return (items, mode, false);
        }

        // Prowlarr Torznab proxy ALWAYS uses apikey in query
        var isProwlarr = IsProwlarrTorznabUrl(torznabUrl);
        authMode = isProwlarr ? "query" : NormalizeAuthMode(authMode);
        limit = Math.Clamp(limit, 1, 200);

        var distinctIds = categoryIds.Where(id => id > 0).Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            var (items, mode) = await FetchLatestAsync(torznabUrl, authMode, apiKey, limit, ct);
            return (items, mode, false);
        }

        async Task<List<TorznabItem>> TrySearchAsync(string? searchQuery, string? cat)
        {
            var query = new Dictionary<string, string>
            {
                ["t"] = "search",
                ["limit"] = limit.ToString(),
                ["q"] = searchQuery ?? ""
            };
            if (!string.IsNullOrWhiteSpace(cat))
                query["cat"] = cat;

            var url = BuildUrl(torznabUrl, authMode, apiKey, query);
            if (_log.IsEnabled(LogLevel.Debug))
                _log.LogDebug("Torznab search url={Url} cat={Cat} q={Query}", SanitizeUrl(url), cat ?? "-", searchQuery ?? "");
            using var resp = await SendWithRetryAsync(() => BuildReq(url, authMode, apiKey), ct);
            await EnsureSuccessOrLogAsync(resp, ct);

            var xml = await resp.Content.ReadAsStringAsync(ct);
            return _parser.Parse(xml);
        }

        static string GetMergeKey(TorznabItem it)
        {
            if (!string.IsNullOrWhiteSpace(it.Guid)) return it.Guid;
            if (!string.IsNullOrWhiteSpace(it.InfoHash)) return it.InfoHash;
            if (!string.IsNullOrWhiteSpace(it.DownloadUrl)) return it.DownloadUrl;
            if (!string.IsNullOrWhiteSpace(it.Link)) return it.Link;
            if (!string.IsNullOrWhiteSpace(it.Title)) return it.Title;
            return Guid.NewGuid().ToString("N");
        }

        static void EnsureItemCategories(TorznabItem it, int? forcedCatId)
        {
            if (it.CategoryIds is null)
                it.CategoryIds = new List<int>();

            if (forcedCatId.HasValue)
            {
                if (!it.CategoryIds.Contains(forcedCatId.Value))
                    it.CategoryIds.Add(forcedCatId.Value);
                if (!it.CategoryId.HasValue)
                    it.CategoryId = forcedCatId.Value;
            }
            else if (it.CategoryId.HasValue && !it.CategoryIds.Contains(it.CategoryId.Value))
            {
                it.CategoryIds.Add(it.CategoryId.Value);
            }

            if (!it.SpecCategoryId.HasValue || !it.StdCategoryId.HasValue)
            {
                foreach (var id in it.CategoryIds)
                {
                    if (!it.SpecCategoryId.HasValue && id >= 10000)
                        it.SpecCategoryId = id;
                    else if (!it.StdCategoryId.HasValue && id >= 1000 && id <= 8999)
                        it.StdCategoryId = id;
                    if (it.SpecCategoryId.HasValue && it.StdCategoryId.HasValue)
                        break;
                }
            }
        }

        static void MergeItem(Dictionary<string, TorznabItem> merged, TorznabItem it, int? forcedCatId)
        {
            EnsureItemCategories(it, forcedCatId);

            var key = GetMergeKey(it);
            if (merged.TryGetValue(key, out var existing))
            {
                if (existing.CategoryIds is null)
                    existing.CategoryIds = new List<int>();

                if (it.CategoryIds is { Count: > 0 })
                {
                    foreach (var cid in it.CategoryIds)
                    {
                        if (!existing.CategoryIds.Contains(cid))
                            existing.CategoryIds.Add(cid);
                    }
                }
                if (!existing.CategoryId.HasValue && it.CategoryId.HasValue)
                    existing.CategoryId = it.CategoryId;
                if (!existing.SpecCategoryId.HasValue && it.SpecCategoryId.HasValue)
                    existing.SpecCategoryId = it.SpecCategoryId;
                if (!existing.StdCategoryId.HasValue && it.StdCategoryId.HasValue)
                    existing.StdCategoryId = it.StdCategoryId;
            }
            else
            {
                merged[key] = it;
            }
        }

        static List<int> GetMissingCats(IEnumerable<TorznabItem> items, HashSet<int> requested)
        {
            var seen = new HashSet<int>();
            foreach (var it in items)
            {
                if (it.CategoryIds is { Count: > 0 })
                {
                    foreach (var cid in it.CategoryIds)
                    {
                        if (cid > 0) seen.Add(cid);
                    }
                }
                else if (it.CategoryId.HasValue && it.CategoryId.Value > 0)
                {
                    seen.Add(it.CategoryId.Value);
                }
            }

            return requested.Where(cid => !seen.Contains(cid)).ToList();
        }

        // 1) Try aggregated categories (cat=1,2,3) with q=""
        var catList = string.Join(",", distinctIds);
        var merged = new Dictionary<string, TorznabItem>(StringComparer.OrdinalIgnoreCase);
        var requestedSet = new HashSet<int>(distinctIds);
        var aggregatedCount = 0;
        var perCatAdded = 0;
        var usedAggregated = false;
        var modeUsed = "search_percat_all";

        if (!string.IsNullOrWhiteSpace(catList))
        {
            try
            {
                var items = await TrySearchAsync("", catList);
                if (items.Count == 0)
                    items = await TrySearchAsync("*", catList); // fallback for indexers needing a wildcard

                if (items.Count > 0)
                {
                    foreach (var it in items)
                        MergeItem(merged, it, null);
                    usedAggregated = true;
                    modeUsed = "search_aggregated+percat_all";
                }
            }
            catch (HttpRequestException)
            {
                // fallback to per-category
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // fallback to per-category
            }
        }

        aggregatedCount = merged.Count;
        foreach (var catId in distinctIds)
        {
            var before = merged.Count;
            var catItems = await TrySearchAsync("", catId.ToString());
            if (catItems.Count == 0)
                catItems = await TrySearchAsync("*", catId.ToString());

            foreach (var it in catItems)
                MergeItem(merged, it, catId);

            var after = merged.Count;
            if (after > before) perCatAdded += (after - before);
        }

        var mergedItems = merged.Values.ToList();
        var missingFinal = GetMissingCats(mergedItems, requestedSet);
        if (_log.IsEnabled(LogLevel.Debug) && missingFinal.Count > 0)
        {
            foreach (var cid in missingFinal)
            {
                _log.LogDebug("Torznab category {CategoryId} returned 0 items", cid);
            }
        }

        var finalItems = mergedItems
            .OrderByDescending(it => it.PublishedAtTs ?? 0)
            .ThenBy(it => it.Title ?? "", StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        _log.LogInformation(
            "Torznab category search summary aggregatedCount={AggregatedCount} perCatAdded={PerCatAdded} finalCount={FinalCount} missingCats={MissingCats} modeUsed={ModeUsed}",
            aggregatedCount,
            perCatAdded,
            finalItems.Count,
            missingFinal.Count > 0 ? string.Join(",", missingFinal) : "-",
            modeUsed
        );

        if (merged.Count > 0)
            return (finalItems, modeUsed, usedAggregated);

        var (fallbackItems, fallbackMode) = await FetchLatestAsync(torznabUrl, authMode, apiKey, limit, ct);
        return (fallbackItems, fallbackMode, false);
    }
}
