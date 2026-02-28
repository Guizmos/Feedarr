namespace Feedarr.Api.Services.Security;

public static class SensitiveUrlSanitizer
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey",
        "api_key",
        "key",
        "token",
        "access_token",
        "secret",
        "password",
        "pwd",
        "pass",
        "auth",
        "authorization",
        "session",
        "sig",
        "signature"
    };

    public static string Sanitize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query))
            return url;

        var sanitizedParts = new List<string>();
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 0)
                continue;

            var key = Uri.UnescapeDataString(kv[0] ?? string.Empty);
            if (IsSensitiveQueryKey(key))
            {
                sanitizedParts.Add($"{kv[0]}=***");
                continue;
            }

            sanitizedParts.Add(part);
        }

        var builder = new UriBuilder(uri) { Query = string.Join("&", sanitizedParts) };
        return builder.Uri.ToString();
    }

    private static bool IsSensitiveQueryKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (SensitiveKeys.Contains(key))
            return true;

        var compact = key
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return compact.Contains("token", StringComparison.Ordinal) ||
               compact.Contains("secret", StringComparison.Ordinal) ||
               compact.Contains("password", StringComparison.Ordinal) ||
               compact.Contains("auth", StringComparison.Ordinal) ||
               compact.EndsWith("key", StringComparison.Ordinal) ||
               compact.Contains("session", StringComparison.Ordinal) ||
               compact.Contains("signature", StringComparison.Ordinal) ||
               compact.Equals("sig", StringComparison.Ordinal);
    }
}
