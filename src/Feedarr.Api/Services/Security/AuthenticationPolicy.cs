using System.Net;
using System.Security.Cryptography;
using System.Text;
using Feedarr.Api.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Feedarr.Api.Services.Security;

public static class AuthenticationPolicy
{
    private static readonly object DirectLoopbackKey = new();

    public const string BootstrapSecretHeader = "X-Bootstrap-Secret";
    public const string LegacyBootstrapSecretHeader = "X-Feedarr-Bootstrap-Secret";
    public const string BootstrapTokenHeader = "X-Feedarr-Bootstrap-Token";

    public static bool RequiresHttps(SecuritySettings settings, IConfiguration? configuration = null)
        => settings.RequireHttps && !(configuration?.GetValue<bool>("Security:AllowHttpRecovery") ?? false);

    public static bool CanUseTransport(HttpRequest request, SecuritySettings settings, IConfiguration? configuration = null)
        => request.IsHttps || !RequiresHttps(settings, configuration);

    // Import old configuration archives without reintroducing an anonymous mode.
    public static void NormalizeSecurityDocument(System.Text.Json.Nodes.JsonObject document)
    {
        if (!document.ContainsKey("requireHttps"))
        {
            var legacy = document["authMode"]?.GetValue<string>() ??
                (document["authentication"]?.GetValue<string>() == "none" ? "open" : "protected");
            document["requireHttps"] = legacy != "open";
            document["transportMigration"] = legacy;
        }
        document.Remove("authMode"); document.Remove("authentication"); document.Remove("authenticationRequired");
    }

    public static bool IsAuthConfigured(SecuritySettings settings, string? bootstrapSecret)
    {
        _ = bootstrapSecret;
        return
            !string.IsNullOrWhiteSpace(settings.Username) &&
            !string.IsNullOrWhiteSpace(settings.PasswordHash) &&
            !string.IsNullOrWhiteSpace(settings.PasswordSalt);
    }

    public static bool IsAuthRequired(HttpContext context, SecuritySettings settings)
    {
        return true; // Every private request requires an administrator, on every network.
    }

    public static bool IsLoopbackRequest(HttpContext context)
    {
        // Forwarding metadata can only remove the local exemption, never grant it.
        if (HasForwardedHeaders(context.Request.Headers))
            return false;
        if (context.Items.TryGetValue(DirectLoopbackKey, out var direct) && direct is not true)
            return false;
        return IsLoopbackAddress(context.Connection.RemoteIpAddress);
    }

    // Must run before ForwardedHeadersMiddleware rewrites RemoteIpAddress and consumes headers.
    internal static void CaptureConnection(HttpContext context, ForwardedHeadersOptions options)
    {
        var peer = context.Connection.RemoteIpAddress;
        var normalized = peer?.IsIPv4MappedToIPv6 == true ? peer.MapToIPv4() : peer;
        var isProxy = peer is not null && (
            options.KnownProxies.Any(proxy => proxy.Equals(peer) ||
                (proxy.IsIPv4MappedToIPv6 ? proxy.MapToIPv4() : proxy).Equals(normalized)) ||
            options.KnownNetworks.Any(network => network.Contains(peer) ||
                network.Contains(peer.MapToIPv6()) ||
                (normalized is not null && network.Contains(normalized))));
        context.Items[DirectLoopbackKey] = IsLoopbackAddress(peer) && !isProxy &&
            !HasForwardedHeaders(context.Request.Headers);
    }

    private static bool IsLoopbackAddress(IPAddress? ip)
    {
        if (ip is null) return false;
        return IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
    }

    public static bool HasForwardedHeaders(IHeaderDictionary headers)
    {
        if (headers.ContainsKey("Forwarded") || headers.ContainsKey("X-Real-IP"))
            return true;

        foreach (var key in headers.Keys)
        {
            if (key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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

        var provided =
            TryReadHeader(context.Request.Headers, BootstrapSecretHeader) ??
            TryReadHeader(context.Request.Headers, LegacyBootstrapSecretHeader);

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

    private static string? TryReadHeader(IHeaderDictionary headers, string key)
    {
        if (!headers.TryGetValue(key, out var values))
            return null;

        var value = values.ToString().Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
