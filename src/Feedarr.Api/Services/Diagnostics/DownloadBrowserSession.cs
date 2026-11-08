using System.Security.Cryptography;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Feedarr.Api.Services.Diagnostics;

// Browser isolation, not a user account or a substitute for Basic/Smart Auth.
public sealed class DownloadBrowserSession
{
    internal const string Purpose = "Feedarr.DownloadDiagnostics.BrowserSession.v1";
    private readonly ITimeLimitedDataProtector _protector;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    public DownloadBrowserSession(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    internal static string CookieName(HttpContext context) => context.Request.IsHttps
        ? "__Host-FeedarrDownloadSession" : "FeedarrDownloadSession";

    public bool TryGet(HttpContext context, out string session)
    {
        session = string.Empty;
        if (context.Items[BasicAuthMiddleware.AuthPassedKey] is not true) return false;
        if (!context.Request.Cookies.TryGetValue(CookieName(context), out var token) || token.Length > 4096) return false;
        try
        {
            var value = _protector.Unprotect(token);
            if (value.Length != 64 || !value.All(Uri.IsHexDigit)) return false;
            session = value;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return false; }
    }

    public bool Prepare(HttpContext context)
    {
        if (context.Items[BasicAuthMiddleware.AuthPassedKey] is not true) return false;
        if (TryGet(context, out _)) return true;
        // Never adopt an unverified client ID or silently replace a supplied invalid cookie.
        if (context.Request.Cookies.ContainsKey(CookieName(context)))
        {
            Clear(context);
            return false;
        }
        var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        context.Response.Cookies.Append(CookieName(context), _protector.Protect(session, Lifetime), Options(context));
        return true;
    }

    public void Clear(HttpContext context) => context.Response.Cookies.Delete(CookieName(context), Options(context));

    private static CookieOptions Options(HttpContext context) => new()
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true
    };
}
