using Feedarr.Api.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Feedarr.Api.Services.Security;

public static class AuthenticationPolicy
{
    public static bool RequiresHttps(SecuritySettings settings, IConfiguration? configuration = null)
        => settings.RequireHttps && !(configuration?.GetValue<bool>("Security:AllowHttpRecovery") ?? false);

    public static bool CanUseTransport(HttpRequest request, SecuritySettings settings, IConfiguration? configuration = null)
        => request.IsHttps || !RequiresHttps(settings, configuration);

    // Normalize security settings from installed databases without reintroducing anonymous access.
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

    public static bool IsAuthConfigured(SecuritySettings settings)
    {
        return
            !string.IsNullOrWhiteSpace(settings.Username) &&
            !string.IsNullOrWhiteSpace(settings.PasswordHash) &&
            !string.IsNullOrWhiteSpace(settings.PasswordSalt);
    }

    public static bool IsAuthRequired(HttpContext context, SecuritySettings settings)
    {
        return true; // Every private request requires an administrator, on every network.
    }

}
