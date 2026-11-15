using System.Text.Json;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services;
using System.Diagnostics;

namespace Feedarr.Api.Services.Jikan;

public sealed class JikanClient
{
    private readonly HttpClient _http;
    private readonly ActiveExternalProviderConfigResolver _resolver;
    private readonly ProviderStatsService _stats;

    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static long _lastRequest;


    public JikanClient(
        HttpClient http,
        ActiveExternalProviderConfigResolver resolver,
        ProviderStatsService stats)
    {
        _http = http;
        _resolver = resolver;
        _stats = stats;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<bool> TestApiAsync(CancellationToken ct)
    {
        var active = _resolver.Resolve(ExternalProviderKeys.Jikan);
        if (!active.Enabled)
            return false;

        var endpoint = BuildEndpoint("anime?q=naruto&limit=1");
        using var resp = await GetTrackedAsync(endpoint, ct).ConfigureAwait(false);        return resp.IsSuccessStatusCode;
    }

    internal bool MatchingEnabled => _resolver.Resolve(ExternalProviderKeys.Jikan).Enabled;

    internal async Task<JsonElement?> ReadMatchingAsync(string relative, CancellationToken ct)
    {
        if (!MatchingEnabled) return null;
        using var response = await GetTrackedAsync(BuildEndpoint(relative), ct, disableRetries: true).ConfigureAwait(false);
        return await Feedarr.Api.Services.Matching.TelevisionCandidates.ReadAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> DownloadImageAsync(string? url, CancellationToken ct)
    {
        var normalized = NormalizeAbsoluteUrl(url);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        using var resp = await GetTrackedAsync(normalized, ct).ConfigureAwait(false);        if (!resp.IsSuccessStatusCode)
            return null;

        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);    }

    private string BuildEndpoint(string relative)
    {
        var active = _resolver.Resolve(ExternalProviderKeys.Jikan);
        var rawBase = string.IsNullOrWhiteSpace(active.BaseUrl)
            ? "https://api.jikan.moe/v4/"
            : active.BaseUrl!;
        var baseUri = EnsureTrailingSlash(rawBase);
        var relativePath = relative.TrimStart('/');
        return new Uri(baseUri, relativePath).ToString();
    }

    private async Task<HttpResponseMessage> GetTrackedAsync(string endpoint, CancellationToken ct, bool disableRetries = false)
    {
        await WaitForRequestAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (disableRetries) request.Options.Set(Feedarr.Api.Services.Resilience.TransientHttpRetryHandler.DisableRetries, true);
            var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            _stats.RecordExternal(ExternalProviderKeys.Jikan, resp.IsSuccessStatusCode, sw.ElapsedMilliseconds);
            return resp;
        }
        catch
        {
            _stats.RecordExternal(ExternalProviderKeys.Jikan, ok: false, sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task WaitForRequestAsync(CancellationToken ct)
    {
        await RequestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lastRequest != 0)
            {
                var remaining = TimeSpan.FromMilliseconds(1100) - Stopwatch.GetElapsedTime(_lastRequest);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            _lastRequest = Stopwatch.GetTimestamp();
        }
        finally { RequestGate.Release(); }
    }

    private static Uri EnsureTrailingSlash(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            uri = new Uri("https://api.jikan.moe/v4/");

        var normalized = uri.ToString();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
            normalized += "/";
        return new Uri(normalized, UriKind.Absolute);
    }

    private static string? NormalizeAbsoluteUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var raw = input.Trim();
        if (raw.StartsWith("//", StringComparison.Ordinal))
            raw = "https:" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return null;

        return uri.ToString();
    }

}
