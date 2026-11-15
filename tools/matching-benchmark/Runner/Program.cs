using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Feedarr.Api.Data;
using Feedarr.Api.Models;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Matching.Replay;
using Feedarr.Api.Services.Resilience;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

try { return await Execute(args); }
catch (Exception e) { Console.Error.WriteLine($"Benchmark runner failed: {e.GetType().Name} (no credentials logged)"); return 1; }

static async Task<int> Execute(string[] args)
{
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(args.Single()), options)!;
    var services = new ServiceCollection(); services.AddLogging(b => b.ClearProviders());
    if (request.Record)
    {
        // Read only: unlike FileMasterKeyProvider, never creates/chmods the original key ring.
        services.AddSingleton<IMasterKeyProvider>(new ReadOnlyMaster(request.Keys));
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(request.Keys))
            .SetApplicationName("Feedarr").DisableAutomaticKeyGeneration();
        services.AddSingleton<IApiKeyProtectionService, ApiKeyProtectionService>();
        services.AddSingleton(new OutboundDestinationPolicy(new OutboundSecurityOptions()));
        services.AddSingleton<IOutboundDnsResolver, OutboundDnsResolver>();
        services.AddSingleton<IOutboundSocketConnector, OutboundSocketConnector>();
        services.AddSingleton<ValidatedOutboundConnector>();
    }
    else services.AddSingleton<IApiKeyProtectionService, OfflineProtection>();
    var captures = new Captures(request, options);
    var client = services.AddHttpClient("tmdb-matching-replay", c => {
        c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
    }).RemoveAllLoggers();
    if (request.Record) client.UsePublicProviderTransport();
    else client.ConfigurePrimaryHttpMessageHandler(() => new DenyNetwork());
    // Existing production replay allowlist/budget remains in force, including cache reads.
    client.AddHttpMessageHandler(() => new ReplayBudgetHandler()).AddHttpMessageHandler(() => new CaptureHandler(captures));
    using var provider = services.BuildServiceProvider(); using var lifetime = new Lifetime();
    var db = new Db(Options.Create(new AppOptions { DataDir = Path.GetDirectoryName(request.Snapshot)!, DbFileName = Path.GetFileName(request.Snapshot) }));
    var reader = new MatchingReplayReader(db, provider.GetRequiredService<IApiKeyProtectionService>());
    using var replay = new MatchingReplayService(reader, provider.GetRequiredService<IHttpClientFactory>(), lifetime);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Stop(); };
    var rows = new List<object>();
    void Save()
    {
        var data = JsonSerializer.Serialize(new {
            version = 1, request.DatasetHash, selectedIds = request.Ids, completed = rows.Count == request.Ids.Length,
            mode = request.Record ? "record" : "offline",
            engine = new { assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ReplayRequest).Assembly.Location))).ToLowerInvariant() },
            networkRequests = captures.Network, entries = rows
        }, options);
        if (Encoding.UTF8.GetByteCount(data) > 64*1024*1024) throw new IOException("run_size_limit");
        File.WriteAllText(request.Output, data);
    }
    foreach (var id in request.Ids)
    {
        var before = captures.Network; captures.LastProblem = null;
        var watch = Stopwatch.StartNew(); ReplayEntry? entry = null; string? problem = null;
        var state = reader.Read(id);
        if (state is null) problem = "snapshot_release_unavailable";
        else if (!ReplaySelectionRules.Categories.Contains(MatchingReplayReader.CanonicalCategory(state.Category))) problem = "category_not_supported_by_current_replay";
        else
        {
            var current = replay.Snapshot();
            replay.Start(new(10, "tmdb", 30, current.Generation, new("ids", ReleaseIds: new[] { id })));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (replay.Snapshot().Running) await Task.Delay(25, deadline.Token);
            entry = replay.Snapshot().Entries.SingleOrDefault();
            problem = captures.LastProblem ?? (entry is null ? "replay_session_incomplete" : null);
        }
        rows.Add(new { id, benchmarkStatus = problem, durationMs = watch.Elapsed.TotalMilliseconds,
            networkRequests = captures.Network - before, entry }); Save();
        if (lifetime.ApplicationStopping.IsCancellationRequested) break;
    }
    return 0;
}

sealed record Request(string Snapshot, string Captures, long[] Ids, bool Record, string Keys, int MaxRequests, string Output, string DatasetHash);
sealed record Captured(int Status, string Body, string? RetryAfter);
sealed class Captures(Request request, JsonSerializerOptions options)
{
    internal int Network; internal string? LastProblem; private bool halted;
    private long lastSent;
    internal async Task<HttpResponseMessage> Send(HttpRequestMessage message, Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        var uri = message.RequestUri!;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        var fields = new SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach (var key in new[] { "query", "language", "year", "first_air_date_year", "include_adult", "page", "q", "date", "append_to_response", "limit" })
            if (query.TryGetValue(key, out var value)) fields[key] = value.ToString();
        // Never serialize the request URL, API key, headers or private credentials.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { path = uri.AbsolutePath, fields })))).ToLowerInvariant();
        var file = Path.Combine(request.Captures, hash + ".json"); Captured capture;
        if (File.Exists(file))
        {
            if (new FileInfo(file).Length > 1100000) throw Problem("capture_too_large");
            capture = JsonSerializer.Deserialize<Captured>(File.ReadAllText(file), options)!;
        }
        else
        {
            if (!request.Record) throw Problem("capture_missing_no_network_allowed");
            if (halted) throw Problem("provider_halted_after_auth_or_rate_limit");
            if (Network >= request.MaxRequests) throw Problem("benchmark_http_budget_exhausted");
            if (Directory.GetFiles(request.Captures).Length >= 1000 || new DirectoryInfo(request.Captures).EnumerateFiles().Sum(f => f.Length) > 32*1024*1024)
                throw Problem("capture_store_limit");
            var remaining = TimeSpan.FromSeconds(1) - (lastSent == 0 ? TimeSpan.FromSeconds(2) : Stopwatch.GetElapsedTime(lastSent));
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
            ct.ThrowIfCancellationRequested(); Network++; lastSent = Stopwatch.GetTimestamp();
            using var response = await send();
            halted |= response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;
            var body = "{}";
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream();
                var bytes = new byte[16384]; int n;
                while ((n = await stream.ReadAsync(bytes, ct)) > 0) {
                    if (buffer.Length + n > 1048576) throw Problem("provider_response_too_large"); buffer.Write(bytes,0,n);
                }
                using var document = JsonDocument.Parse(buffer.ToArray()); var root = document.RootElement;
                body = JsonSerializer.Serialize(Project(root));
            }
            capture = new((int)response.StatusCode, body, response.Headers.RetryAfter?.ToString());
            var serialized = JsonSerializer.Serialize(capture, options);
            var size = Encoding.UTF8.GetByteCount(serialized);
            if (size > 1100000 || new DirectoryInfo(request.Captures).EnumerateFiles().Sum(f => f.Length) + size > 32*1024*1024)
                throw Problem("capture_store_limit");
            var temp = file + ".new";
            await File.WriteAllTextAsync(temp, serialized, ct); File.Move(temp,file);
        }
        var cached = new HttpResponseMessage((HttpStatusCode)capture.Status) { Content = new StringContent(capture.Body, Encoding.UTF8, "application/json") };
        if (capture.RetryAfter is not null) cached.Headers.TryAddWithoutValidation("Retry-After",capture.RetryAfter);
        return cached;
    }
    private static object? Project(JsonElement value, int depth = 0)
    {
        if (depth > 8) throw new IOException("capture_depth_limit");
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > 2000) throw new IOException("capture_array_limit");
            return value.EnumerateArray().Select(v => Project(v, depth + 1)).ToArray();
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var allowed = new HashSet<string>(new[] { "id", "title", "name", "original_title", "original_name", "release_date", "first_air_date", "original_language",
                "page", "total_pages", "total_results", "results", "show", "premiered", "externals", "thetvdb", "imdb", "external_ids", "tvdb_id", "imdb_id",
                "seasons", "season_number", "episode_number", "episode_count", "show_id", "number", "season", "air_date", "airdate", "alternative_titles", "iso_3166_1", "_links", "href", "data", "pagination", "current_page", "last_visible_page", "has_next_page", "items", "total", "count", "per_page",
                "mal_id", "title_english", "title_japanese", "title_synonyms", "titles", "type", "episodes", "status", "year", "aired", "from", "to", "relations", "relation", "entry" }, StringComparer.Ordinal);
            // Episode names are not matching evidence. Do not capture them (they may contain
            // words like "secret" which correctly redact series-title evidence).
            var episode = value.TryGetProperty("episode_number", out _) || value.TryGetProperty("number", out _) && value.TryGetProperty("season", out _);
            return value.EnumerateObject().Where(p => allowed.Contains(p.Name) && !(episode && p.Name is "name" or "title" or "original_name")).ToDictionary(p => p.Name, p => {
                if (p.Name == "href")
                {
                    var link = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                    return System.Text.RegularExpressions.Regex.IsMatch(link ?? "", @"^https://api\.tvmaze\.com/shows/[1-9][0-9]*$") ? (object?)link : null;
                }
                return Project(p.Value, depth + 1);
            });
        }
        if (value.ValueKind == JsonValueKind.String && (value.GetString()!.Length > 2048 || MatchingDiagnosticProjection.RedactionReason(value.GetString()) is not null))
            throw new IOException("sensitive_provider_field_not_recorded");
        return value.Clone();
    }
    private IOException Problem(string code) { LastProblem = code; return new IOException(code); }
}
sealed class CaptureHandler(Captures captures) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        => captures.Send(r, () => base.SendAsync(r,ct),ct);
}
sealed class DenyNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        => throw new InvalidOperationException("offline_network_forbidden");
}
sealed class OfflineProtection : IApiKeyProtectionService
{
    public string Protect(string value) => throw new NotSupportedException();
    public string Unprotect(string value) => "offline-never-transmitted";
    public bool TryUnprotect(string value, out string? result) { result=Unprotect(value); return true; }
    public bool IsProtected(string value) => true;
}
sealed class ReadOnlyMaster : IMasterKeyProvider, IDisposable
{
    private readonly byte[] key;
    public ReadOnlyMaster(string path) { key = File.ReadAllBytes(Path.Combine(path,".master-key")); if(key.Length!=32) throw new InvalidDataException(); }
    public byte[] GetMasterKey() => key;
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
sealed class Lifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource stop = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => stop.Token;
    public CancellationToken ApplicationStopped => stop.Token;
    public void StopApplication() => Stop();
    public void Stop() => stop.Cancel();
    public void Dispose() => stop.Dispose();
}
