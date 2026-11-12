using System.Security.Cryptography;
using System.Text;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;

namespace Feedarr.Api.Services.Security;

// Only token hashes and timestamps are persisted; no credentials or raw bearer tokens.
public sealed class WebSessionService(Db db, TimeProvider clock, SettingsRepository settings)
{
    public const int IdleSeconds = 30 * 60;
    public const int AbsoluteSeconds = 12 * 60 * 60;
    public const int PersistentIdleSeconds = 7 * 24 * 60 * 60;
    public const int PersistentAbsoluteSeconds = 30 * 24 * 60 * 60;

    public long Generation()
    {
        using var connection = db.Open();
        return connection.ExecuteScalar<long>("SELECT generation FROM web_security_generation WHERE id = 1");
    }

    internal string CookiePath(HttpRequest request)
    {
        if (request.PathBase.HasValue) return request.PathBase.Value!;
        // A stripping reverse proxy may leave PathBase empty. Only persisted admin
        // configuration can supply the external prefix, never a raw forwarded header.
        var publicUrl = settings.GetSecurity(new SecuritySettings()).PublicBaseUrl;
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http")) return "/";
        var path = uri.AbsolutePath.TrimEnd('/');
        return path.Length > 0 ? path : "/";
    }

    internal string CookieName(HttpRequest request)
        => CookiePath(request) == "/" ? "__Host-FeedarrSession" : "FeedarrSession";

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private CookieOptions Cookie(HttpRequest request, DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax,
        Path = CookiePath(request), Expires = expires, IsEssential = true
    };

    public WebSession? Create(HttpContext context, long expectedGeneration, bool rememberMe)
    {
        if (!context.Request.IsHttps) return null;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var idle = rememberMe ? PersistentIdleSeconds : IdleSeconds;
        var absolute = now + (rememberMe ? PersistentAbsoluteSeconds : AbsoluteSeconds);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        var inserted = connection.Execute("""
            INSERT INTO web_sessions(token_hash, generation, created_ts, last_seen_ts, expires_ts, idle_seconds, remember_me)
            SELECT @hash, generation, @now, @now, @absolute, @idle, @remember
            FROM web_security_generation WHERE id = 1 AND generation = @expectedGeneration
            """, new { hash = Hash(token), now, absolute, idle, remember = rememberMe ? 1 : 0, expectedGeneration }, transaction);
        if (inserted != 1) return null;
        // Rotation is transactional: a failed login never destroys the previous session.
        var previous = ReadToken(context.Request);
        if (previous is not null)
            connection.Execute("DELETE FROM web_sessions WHERE token_hash = @hash", new { hash = Hash(previous) }, transaction);
        connection.Execute("DELETE FROM web_sessions WHERE expires_ts <= @now OR last_seen_ts + idle_seconds <= @now", new { now }, transaction);
        transaction.Commit();
        context.Response.Cookies.Append(CookieName(context.Request), token,
            Cookie(context.Request, rememberMe ? DateTimeOffset.FromUnixTimeSeconds(absolute) : null));
        return new(WebSessionSecurity.CsrfFor(token), DateTimeOffset.FromUnixTimeSeconds(Math.Min(absolute, now + idle)),
            DateTimeOffset.FromUnixTimeSeconds(absolute), rememberMe);
    }

    public WebSession? Authenticate(HttpContext context)
    {
        if (!context.Request.IsHttps) return null;
        var token = ReadToken(context.Request);
        if (token is null) return null;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        using var connection = db.Open();
        // Check generation and both deadlines atomically on every authenticated request.
        var row = connection.QuerySingleOrDefault<SessionRow>("""
            UPDATE web_sessions SET last_seen_ts = @now
            WHERE token_hash = @hash AND generation = (SELECT generation FROM web_security_generation WHERE id = 1)
              AND expires_ts > @now AND last_seen_ts + idle_seconds > @now
            RETURNING expires_ts AS Absolute, idle_seconds AS Idle, remember_me AS Remember
            """, new { hash = Hash(token), now });
        return row is null ? null : new(WebSessionSecurity.CsrfFor(token),
            DateTimeOffset.FromUnixTimeSeconds(Math.Min(row.Absolute, now + row.Idle)),
            DateTimeOffset.FromUnixTimeSeconds(row.Absolute), row.Remember != 0);
    }

    public void Revoke(HttpContext context)
    {
        var token = ReadToken(context.Request);
        if (token is not null)
        {
            using var connection = db.Open();
            connection.Execute("DELETE FROM web_sessions WHERE token_hash = @hash", new { hash = Hash(token) });
        }
        context.Response.Cookies.Delete(CookieName(context.Request), Cookie(context.Request));
    }

    private string? ReadToken(HttpRequest request)
    {
        var token = request.Cookies[CookieName(request)];
        return token is { Length: 64 } && token.All(Uri.IsHexDigit) ? token : null;
    }

    private sealed class SessionRow
    {
        public long Absolute { get; set; }
        public long Idle { get; set; }
        public int Remember { get; set; }
    }
}
