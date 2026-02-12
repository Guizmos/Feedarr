using System.Text.RegularExpressions;

namespace Feedarr.Api.Services.Security;

public static class ErrorMessageSanitizer
{
    private static readonly Regex SensitiveQueryPattern = new(
        @"(?i)([?&](?:apikey|api_key|token|access_token|refresh_token|password|pass|client_secret)=)[^&\s]+",
        RegexOptions.Compiled);

    private static readonly Regex SensitivePairPattern = new(
        @"(?i)\b(apikey|api_key|token|access_token|refresh_token|password|pass|client_secret|authorization|x-api-key)\s*[:=]\s*[^,\s;]+",
        RegexOptions.Compiled);

    public static string ToOperationalMessage(Exception ex, string fallback = "operation failed")
    {
        return ex switch
        {
            TimeoutException => "request timed out",
            OperationCanceledException => "request timed out",
            HttpRequestException => "upstream request failed",
            _ => Sanitize(ex.Message, fallback)
        };
    }

    public static string Sanitize(string? message, string fallback = "operation failed")
    {
        if (string.IsNullOrWhiteSpace(message))
            return fallback;

        var cleaned = SensitiveQueryPattern.Replace(message, "$1[redacted]");
        cleaned = SensitivePairPattern.Replace(cleaned, "$1=[redacted]");
        cleaned = cleaned.Replace('\r', ' ').Replace('\n', ' ').Trim();

        if (cleaned.Length == 0)
            return fallback;

        if (cleaned.Length > 180)
            cleaned = $"{cleaned[..180]}...";

        return cleaned;
    }
}
