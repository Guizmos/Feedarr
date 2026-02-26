using System.Net;
using System.Security.Cryptography;
using System.Text;
using Feedarr.Api.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Feedarr.Api.Services.Security;

public static class SmartAuthPolicy
{
    public const string BootstrapSecretHeader = "X-Feedarr-Bootstrap-Secret";
    public const string BootstrapTokenHeader = "X-Feedarr-Bootstrap-Token";

    public static string NormalizeAuthMode(SecuritySettings settings)
    {
        return NormalizeAuthMode(settings.AuthMode);
    }

    public static string NormalizeAuthMode(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "smart" => "smart",
            "strict" => "strict",
            "open" => "open",
            _ => "smart"
        };
    }

    public static bool IsAuthConfigured(SecuritySettings settings, string? bootstrapSecret)
    {
        var hasBasic =
            !string.IsNullOrWhiteSpace(settings.Username) &&
            !string.IsNullOrWhiteSpace(settings.PasswordHash) &&
            !string.IsNullOrWhiteSpace(settings.PasswordSalt);

        return hasBasic || !string.IsNullOrWhiteSpace(bootstrapSecret);
    }

    public static bool IsAuthRequired(HttpContext context, SecuritySettings settings)
    {
        return NormalizeAuthMode(settings) switch
        {
            "open" => false,
            "strict" => true,
            _ => IsExposedRequest(context, settings)
        };
    }

    public static bool IsExposedRequest(HttpContext context, SecuritySettings settings)
    {
        if (IsNonLocalPublicBaseUrl(settings.PublicBaseUrl))
            return true;

        if (HasForwardedHeaders(context.Request.Headers))
            return true;

        var host = context.Request.Host.Host;
        if (!string.IsNullOrWhiteSpace(host) && !IsLocalHost(host))
            return true;

        return false;
    }

    public static bool IsLoopbackRequest(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        return ip is not null && IPAddress.IsLoopback(ip);
    }

    public static bool HasForwardedHeaders(IHeaderDictionary headers)
    {
        if (headers.ContainsKey("Forwarded"))
            return true;

        foreach (var key in headers.Keys)
        {
            if (key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var normalized = host.Trim();
        if (normalized.StartsWith("[", StringComparison.Ordinal) &&
            normalized.EndsWith("]", StringComparison.Ordinal))
        {
            normalized = normalized[1..^1];
        }

        if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(normalized, out var ip) && IPAddress.IsLoopback(ip);
    }

    public static bool IsBootstrapTokenRequest(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
            return false;

        var path = request.Path.Value ?? "";
        return PathEqualsOrUnder(path, "/api/setup/bootstrap-token");
    }

    public static bool HasValidBootstrapSecretHeader(HttpContext context, string? bootstrapSecret)
    {
        var expected = (bootstrapSecret ?? "").Trim();
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        if (!context.Request.Headers.TryGetValue(BootstrapSecretHeader, out var values))
            return false;

        var provided = values.ToString().Trim();
        if (string.IsNullOrWhiteSpace(provided))
            return false;

        return FixedTimeEquals(provided, expected);
    }

    public static string GetBootstrapSecret(IConfiguration config)
    {
        var configured = config["App:Security:BootstrapSecret"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        var env = Environment.GetEnvironmentVariable("FEEDARR_BOOTSTRAP_SECRET");
        return (env ?? "").Trim();
    }

    private static bool IsNonLocalPublicBaseUrl(string? publicBaseUrl)
    {
        var raw = (publicBaseUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;

        return !IsLocalHost(uri.Host);
    }

    private static bool PathEqualsOrUnder(string path, string prefix)
    {
        if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
