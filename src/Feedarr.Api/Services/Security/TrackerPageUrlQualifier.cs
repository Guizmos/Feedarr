using System.Net;

namespace Feedarr.Api.Services.Security;

public static class TrackerPageUrlQualifier
{
    private static readonly HashSet<string> SensitiveQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "api_key", "key", "token", "access_token", "secret", "password", "pwd",
        "pass", "auth", "authorization", "session", "sig", "signature"
    };

    private static readonly string[] MetadataHosts =
    [
        "themoviedb.org", "tmdb.org", "thetvdb.com", "tvdb.com", "tvmaze.com",
        "igdb.com", "rawg.io", "fanart.tv", "openlibrary.org"
    ];

    public static string? Qualify(string? candidate)
    {
        var value = candidate?.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains("***", StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrWhiteSpace(uri.UserInfo) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            IsLocalOrInternalHost(uri.Host) ||
            IsMetadataHost(uri.Host) ||
            IsDownloadOrApiPath(uri.AbsolutePath) ||
            HasRejectedQuery(uri.Query))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    private static bool IsLocalOrInternalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            !host.Contains('.', StringComparison.Ordinal))
        {
            return true;
        }

        return IPAddress.TryParse(host, out _) && OutboundUrlGuard.IsBlockedHost(host);
    }

    private static bool IsMetadataHost(string host) =>
        MetadataHosts.Any(metadataHost =>
            host.Equals(metadataHost, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{metadataHost}", StringComparison.OrdinalIgnoreCase));

    private static bool IsDownloadOrApiPath(string path)
    {
        var normalized = path.ToLowerInvariant().TrimEnd('/');
        return normalized.EndsWith(".torrent", StringComparison.Ordinal) ||
               normalized.Contains("/download", StringComparison.Ordinal) ||
               normalized.Contains("/torznab", StringComparison.Ordinal) ||
               normalized.Contains("/api/v2.0/indexers", StringComparison.Ordinal) ||
               normalized.Contains("/api/v1/indexer", StringComparison.Ordinal) ||
               normalized.Contains("/search", StringComparison.Ordinal) ||
               System.Text.RegularExpressions.Regex.IsMatch(normalized, @"/\d+/api(?:/|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool HasRejectedQuery(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(pair[0]);
                value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
            }
            catch (UriFormatException)
            {
                return true;
            }

            var compactKey = key.Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
            if (SensitiveQueryKeys.Contains(key) ||
                compactKey.Contains("token", StringComparison.Ordinal) ||
                compactKey.Contains("secret", StringComparison.Ordinal) ||
                compactKey.Contains("password", StringComparison.Ordinal) ||
                compactKey.Contains("auth", StringComparison.Ordinal) ||
                compactKey.Contains("session", StringComparison.Ordinal) ||
                compactKey.Contains("signature", StringComparison.Ordinal) ||
                compactKey.EndsWith("key", StringComparison.Ordinal) ||
                compactKey == "sig")
            {
                return true;
            }

            if ((key.Equals("t", StringComparison.OrdinalIgnoreCase) &&
                 (value.Equals("search", StringComparison.OrdinalIgnoreCase) ||
                  value.Equals("rss", StringComparison.OrdinalIgnoreCase) ||
                  value.Equals("recent", StringComparison.OrdinalIgnoreCase))) ||
                key.Equals("download", StringComparison.OrdinalIgnoreCase) ||
                (key.Equals("action", StringComparison.OrdinalIgnoreCase) && value.Equals("download", StringComparison.OrdinalIgnoreCase)) ||
                key.Equals("q", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("query", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("search", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
