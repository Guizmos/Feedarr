using System.Net;

namespace Feedarr.Api.Services.Security;

public static class OutboundUrlGuard
{
    private static readonly HashSet<string> BlockedMetadataHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "169.254.169.254",
        "169.254.170.2",
        "100.100.100.200",
        "metadata.google.internal",
        "metadata.azure.internal"
    };

    public static bool TryNormalizeProviderBaseUrl(
        string providerType,
        string? rawBaseUrl,
        out string normalizedBaseUrl,
        out string error)
    {
        var normalizedType = (providerType ?? string.Empty).Trim().ToLowerInvariant();
        var trimmed = (rawBaseUrl ?? string.Empty).Trim().TrimEnd('/');

        if (normalizedType == "jackett" &&
            trimmed.EndsWith("/api/v2.0", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/api/v2.0".Length];
        }

        if (normalizedType == "prowlarr" &&
            trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/api/v1".Length];
        }

        return TryNormalizeHttpBaseUrl(trimmed, allowImplicitHttp: true, out normalizedBaseUrl, out error);
    }

    public static bool TryNormalizeArrBaseUrl(
        string? rawBaseUrl,
        out string normalizedBaseUrl,
        out string error)
    {
        return TryNormalizeHttpBaseUrl(rawBaseUrl, allowImplicitHttp: true, out normalizedBaseUrl, out error);
    }

    public static bool TryNormalizeHttpBaseUrl(
        string? rawBaseUrl,
        bool allowImplicitHttp,
        out string normalizedBaseUrl,
        out string error)
    {
        normalizedBaseUrl = string.Empty;
        error = "baseUrl invalid";

        var raw = (rawBaseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "baseUrl missing";
            return false;
        }

        if (allowImplicitHttp &&
            !raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "http://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            error = "baseUrl invalid";
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "baseUrl must use http or https";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            error = "baseUrl must not contain credentials";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.Query) || !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            error = "baseUrl must not contain query or fragment";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "baseUrl host missing";
            return false;
        }

        if (IsBlockedHost(uri.Host))
        {
            error = "baseUrl host not allowed";
            return false;
        }

        var normalized = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Path = uri.AbsolutePath.TrimEnd('/')
        };

        normalizedBaseUrl = normalized.Uri.ToString().TrimEnd('/');
        return true;
    }

    public static bool TryNormalizeDownloadUrl(string? rawUrl, out string normalizedUrl, out string error)
    {
        normalizedUrl = string.Empty;
        error = "download_url invalid";

        var trimmed = (rawUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "download_url missing";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            error = "download_url invalid";
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "magnet"))
        {
            error = "download_url scheme not allowed";
            return false;
        }

        if (scheme is "http" or "https")
        {
            if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                error = "download_url must not contain credentials";
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "download_url host missing";
                return false;
            }
        }

        normalizedUrl = trimmed;
        return true;
    }

    private static bool IsBlockedHost(string host)
    {
        if (BlockedMetadataHosts.Contains(host))
            return true;

        if (!IPAddress.TryParse(host, out var ip))
            return false;

        if (IPAddress.Equals(ip, IPAddress.Any) ||
            IPAddress.Equals(ip, IPAddress.IPv6Any) ||
            IPAddress.Equals(ip, IPAddress.None) ||
            IPAddress.Equals(ip, IPAddress.Broadcast))
        {
            return true;
        }

        if (ip.IsIPv6Multicast)
            return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] >= 224 && bytes[0] <= 239)
                return true;
        }

        return false;
    }
}
