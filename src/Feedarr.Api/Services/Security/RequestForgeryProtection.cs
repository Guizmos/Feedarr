using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Feedarr.Api.Services.Security;

internal static class RequestForgeryProtection
{
    public const string RequestHeaderName = "X-Feedarr-Request";
    public const string RequestHeaderValue = "1";
    public const string LegacyRequestHeaderName = "X-Requested-With";
    public const string LegacyRequestHeaderValue = "Feedarr";

    public static bool IsSafeMethod(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    public static bool RequireExplicitHeaderForUnsafeMethods(IConfiguration configuration)
        => configuration.GetValue("Security:RequireXsrfHeaderForUnsafeMethods", true);

    public static bool HasTrustedNonBrowserHeader(IHeaderDictionary headers)
    {
        if (headers.TryGetValue(RequestHeaderName, out var requestValues) &&
            string.Equals(requestValues.ToString().Trim(), RequestHeaderValue, StringComparison.Ordinal))
        {
            return true;
        }

        return headers.TryGetValue(LegacyRequestHeaderName, out var legacyValues) &&
               string.Equals(legacyValues.ToString().Trim(), LegacyRequestHeaderValue, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedOrigin(HttpRequest request, IConfiguration configuration, string candidateOrigin)
    {
        if (!TryNormalizeOrigin(candidateOrigin, out var normalizedCandidate))
            return false;

        if (TryNormalizeCurrentRequestOrigin(request, out var currentOrigin) &&
            string.Equals(currentOrigin, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var allowedOrigins = configuration.GetSection("Security:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        foreach (var allowed in allowedOrigins)
        {
            if (!TryNormalizeOrigin(allowed, out var normalizedAllowed))
                continue;

            if (string.Equals(normalizedAllowed, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool TryGetRefererOrigin(string referer, out string origin)
        => TryNormalizeOrigin(referer, out origin);

    internal static bool TryNormalizeCurrentRequestOrigin(HttpRequest request, out string origin)
    {
        origin = string.Empty;
        var host = request.Host;
        if (!host.HasValue)
            return false;

        var scheme = string.IsNullOrWhiteSpace(request.Scheme) ? "http" : request.Scheme;
        var uriBuilder = new UriBuilder(scheme, host.Host)
        {
            Port = host.Port ?? DefaultPortForScheme(scheme)
        };

        return TryNormalizeOrigin(uriBuilder.Uri.ToString(), out origin);
    }

    internal static bool TryNormalizeOrigin(string? value, out string origin)
    {
        origin = string.Empty;
        var raw = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo) || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        var normalizedScheme = uri.Scheme.ToLowerInvariant();
        var normalizedPort = uri.IsDefaultPort ? DefaultPortForScheme(normalizedScheme) : uri.Port;
        origin = $"{normalizedScheme}://{uri.IdnHost.ToLowerInvariant()}:{normalizedPort}";
        return true;
    }

    private static int DefaultPortForScheme(string scheme)
        => scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
}
