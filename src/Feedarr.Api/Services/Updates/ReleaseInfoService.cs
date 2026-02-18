using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feedarr.Api.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services.Updates;

public sealed record LatestReleaseInfo(
    string TagName,
    string Name,
    string Body,
    DateTimeOffset? PublishedAt,
    string HtmlUrl,
    bool IsPrerelease);

public sealed record UpdateCheckResult(
    bool Enabled,
    string CurrentVersion,
    bool IsUpdateAvailable,
    int CheckIntervalHours,
    LatestReleaseInfo? LatestRelease,
    IReadOnlyList<LatestReleaseInfo> Releases);

public sealed class ReleaseInfoService
{
    private const string CacheKey = "updates:latest-release:v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<UpdatesOptions> _options;
    private readonly ILogger<ReleaseInfoService> _log;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private int _consecutiveFailures;
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

    public ReleaseInfoService(
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<UpdatesOptions> options,
        ILogger<ReleaseInfoService> log)
    {
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _log = log;
    }

    public async Task<UpdateCheckResult> GetLatestAsync(bool forceRefresh, CancellationToken ct)
    {
        var cfg = Normalize(_options.CurrentValue);
        var currentVersion = ResolveCurrentVersion();

        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.RepoOwner) || string.IsNullOrWhiteSpace(cfg.RepoName))
        {
            return new UpdateCheckResult(
                Enabled: false,
                CurrentVersion: currentVersion,
                IsUpdateAvailable: false,
                CheckIntervalHours: cfg.CheckIntervalHours,
                LatestRelease: null,
                Releases: Array.Empty<LatestReleaseInfo>());
        }

        var now = DateTimeOffset.UtcNow;
        var ttl = TimeSpan.FromHours(cfg.CheckIntervalHours);
        var cached = _cache.Get<CachedReleaseState>(CacheKey);
        if (!forceRefresh && cached is not null && now < cached.ExpiresAtUtc)
        {
            return BuildResult(cfg, currentVersion, cached.LatestRelease, cached.Releases);
        }

        if (!forceRefresh && now < _circuitOpenUntil && (cached?.LatestRelease is not null || cached?.Releases?.Count > 0))
        {
            return BuildResult(cfg, currentVersion, cached.LatestRelease, cached.Releases);
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;
            cached = _cache.Get<CachedReleaseState>(CacheKey);
            if (!forceRefresh && cached is not null && now < cached.ExpiresAtUtc)
            {
                return BuildResult(cfg, currentVersion, cached.LatestRelease, cached.Releases);
            }

            if (!forceRefresh && now < _circuitOpenUntil && (cached?.LatestRelease is not null || cached?.Releases?.Count > 0))
            {
                return BuildResult(cfg, currentVersion, cached.LatestRelease, cached.Releases);
            }

            try
            {
                var releases = await FetchReleasesAsync(cfg, ct);
                var latest = SelectLatestBySemVer(releases);
                var cacheState = new CachedReleaseState(latest, releases, now.Add(ttl));
                _cache.Set(CacheKey, cacheState, ttl);

                _consecutiveFailures = 0;
                _circuitOpenUntil = DateTimeOffset.MinValue;

                return BuildResult(cfg, currentVersion, latest, releases);
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                var openMinutes = Math.Min(30, Math.Max(2, _consecutiveFailures * 2));
                _circuitOpenUntil = now.AddMinutes(openMinutes);

                if (cached?.LatestRelease is not null || cached?.Releases?.Count > 0)
                {
                    _log.LogWarning(
                        ex,
                        "GitHub release fetch failed, returning cached value (repo={Owner}/{Repo}, failureCount={FailureCount})",
                        cfg.RepoOwner,
                        cfg.RepoName,
                        _consecutiveFailures);
                    return BuildResult(cfg, currentVersion, cached.LatestRelease, cached.Releases);
                }

                _log.LogWarning(
                    ex,
                    "GitHub release fetch failed with no cached value (repo={Owner}/{Repo}, failureCount={FailureCount})",
                    cfg.RepoOwner,
                    cfg.RepoName,
                    _consecutiveFailures);
                return BuildResult(cfg, currentVersion, null, Array.Empty<LatestReleaseInfo>());
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private UpdateCheckResult BuildResult(
        NormalizedOptions cfg,
        string currentVersion,
        LatestReleaseInfo? latest,
        IReadOnlyList<LatestReleaseInfo> releases)
    {
        var isUpdateAvailable = latest is not null
            && ReleaseVersionComparer.IsUpdateAvailable(currentVersion, latest.TagName, cfg.AllowPrerelease);

        return new UpdateCheckResult(
            Enabled: true,
            CurrentVersion: currentVersion,
            IsUpdateAvailable: isUpdateAvailable,
            CheckIntervalHours: cfg.CheckIntervalHours,
            LatestRelease: latest,
            Releases: releases);
    }

    private async Task<IReadOnlyList<LatestReleaseInfo>> FetchReleasesAsync(NormalizedOptions cfg, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("github-updates");
        var baseUrl = cfg.GitHubApiBaseUrl.TrimEnd('/');

        var uri = $"{baseUrl}/repos/{Uri.EscapeDataString(cfg.RepoOwner)}/{Uri.EscapeDataString(cfg.RepoName)}/releases?per_page=10";
        var list = await SendAndReadAsync<List<GitHubReleasePayload>>(client, uri, cfg, ct, allowNotFound: true) ?? new List<GitHubReleasePayload>();
        if (list.Count == 0)
        {
            return Array.Empty<LatestReleaseInfo>();
        }

        var items = new List<LatestReleaseInfo>();
        foreach (var item in list)
        {
            if (item is null || item.Draft)
                continue;
            if (!ReleaseVersionComparer.TryParse(item.TagName, out _))
                continue;
            if (!cfg.AllowPrerelease && item.Prerelease)
                continue;

            items.Add(ToLatestReleaseInfo(item));
        }

        return items
            .OrderByDescending(x => x.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private static LatestReleaseInfo? SelectLatestBySemVer(IReadOnlyList<LatestReleaseInfo> releases)
    {
        if (releases is null || releases.Count == 0)
            return null;

        LatestReleaseInfo? winner = null;
        ReleaseSemVersion winnerVer = default!;
        var hasWinner = false;

        foreach (var release in releases)
        {
            if (!ReleaseVersionComparer.TryParse(release.TagName, out var currentVer))
                continue;

            if (!hasWinner)
            {
                winner = release;
                winnerVer = currentVer;
                hasWinner = true;
                continue;
            }

            if (ReleaseVersionComparer.Compare(currentVer, winnerVer) > 0)
            {
                winner = release;
                winnerVer = currentVer;
            }
        }

        return winner;
    }

    private static LatestReleaseInfo ToLatestReleaseInfo(GitHubReleasePayload payload)
    {
        var tagName = (payload.TagName ?? "").Trim();
        var name = string.IsNullOrWhiteSpace(payload.Name) ? tagName : payload.Name.Trim();
        var body = payload.Body ?? "";
        var htmlUrl = payload.HtmlUrl ?? "";

        return new LatestReleaseInfo(
            TagName: tagName,
            Name: name,
            Body: body,
            PublishedAt: payload.PublishedAt,
            HtmlUrl: htmlUrl,
            IsPrerelease: payload.Prerelease);
    }

    private async Task<T?> SendAndReadAsync<T>(
        HttpClient client,
        string uri,
        NormalizedOptions cfg,
        CancellationToken ct,
        bool allowNotFound) where T : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("Feedarr-Updates");
        if (!string.IsNullOrWhiteSpace(cfg.GitHubToken))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.GitHubToken);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        if (allowNotFound && resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, timeoutCts.Token);
    }

    private static NormalizedOptions Normalize(UpdatesOptions? options)
    {
        var source = options ?? new UpdatesOptions();
        return new NormalizedOptions(
            Enabled: source.Enabled,
            RepoOwner: (source.RepoOwner ?? "").Trim(),
            RepoName: (source.RepoName ?? "").Trim(),
            CheckIntervalHours: Math.Clamp(source.CheckIntervalHours <= 0 ? 6 : source.CheckIntervalHours, 1, 168),
            TimeoutSeconds: Math.Clamp(source.TimeoutSeconds <= 0 ? 10 : source.TimeoutSeconds, 2, 120),
            AllowPrerelease: source.AllowPrerelease,
            GitHubApiBaseUrl: string.IsNullOrWhiteSpace(source.GitHubApiBaseUrl) ? "https://api.github.com" : source.GitHubApiBaseUrl.Trim(),
            GitHubToken: string.IsNullOrWhiteSpace(source.GitHubToken) ? null : source.GitHubToken.Trim());
    }

    private static string ResolveCurrentVersion()
    {
        var envVersion = Environment.GetEnvironmentVariable("FEEDARR_VERSION");
        if (ReleaseVersionComparer.TryParse(envVersion, out var env))
            return $"{env.Major}.{env.Minor}.{env.Patch}";

        var asm = typeof(Program).Assembly;
        var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (ReleaseVersionComparer.TryParse(infoVersion, out var info))
            return $"{info.Major}.{info.Minor}.{info.Patch}";

        var asmVersion = asm.GetName().Version?.ToString();
        if (ReleaseVersionComparer.TryParse(asmVersion, out var ver))
            return $"{ver.Major}.{ver.Minor}.{ver.Patch}";

        return "0.0.0";
    }

    private sealed record CachedReleaseState(
        LatestReleaseInfo? LatestRelease,
        IReadOnlyList<LatestReleaseInfo> Releases,
        DateTimeOffset ExpiresAtUtc);

    private sealed record NormalizedOptions(
        bool Enabled,
        string RepoOwner,
        string RepoName,
        int CheckIntervalHours,
        int TimeoutSeconds,
        bool AllowPrerelease,
        string GitHubApiBaseUrl,
        string? GitHubToken);

    private sealed class GitHubReleasePayload
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }
    }
}
