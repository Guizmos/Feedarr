using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Feedarr.Api.Filters;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class WebSessionTests
{
    [Fact]
    public void Login_UsesLegacyCredentials_AndPersistsOnlyHash()
    {
        using var f = new Fixture();
        var context = f.Context("/api/auth/login", "POST");
        var response = Assert.IsType<OkObjectResult>(f.Controller(context).Login(new("admin", Fixture.Password)));
        var state = JsonSerializer.SerializeToElement(response.Value);
        Assert.True(state.GetProperty("authenticated").GetBoolean());
        Assert.False(state.GetProperty("rememberMe").GetBoolean());
        Assert.False(context.Response.Headers.ContainsKey("WWW-Authenticate"));
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("__Host-FeedarrSession=", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("domain=", cookie);
        Assert.DoesNotContain("expires=", cookie);
        using var db = f.Db.Open();
        var stored = db.QuerySingle<string>("SELECT token_hash FROM web_sessions");
        var token = cookie.Split(';')[0].Split('=')[1];
        Assert.NotEqual(token, stored);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), stored);
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM web_sessions"));
    }

    [Fact]
    public void LoginFailures_AreNeutral_AndThrottled()
    {
        using var f = new Fixture();
        for (var i = 0; i < 5; i++)
        {
            var context = f.Context("/api/auth/login", "POST");
            var result = Assert.IsType<ObjectResult>(f.Controller(context).Login(new("admin", "wrong")));
            Assert.Equal(401, result.StatusCode);
            Assert.Equal("invalid_credentials", Assert.IsType<ProblemDetails>(result.Value).Title);
            Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
        }
        var blocked = f.Context("/api/auth/login", "POST");
        Assert.Equal(429, Assert.IsType<ObjectResult>(f.Controller(blocked).Login(new("admin", Fixture.Password))).StatusCode);
        Assert.Equal("5", blocked.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public void Rotation_RevokesOldToken_AndRestartPreservesNewSession()
    {
        using var f = new Fixture();
        var (oldCookie, _) = f.Login();
        var context = f.Context("/api/auth/login", "POST", oldCookie);
        Assert.IsType<OkObjectResult>(f.Controller(context).Login(new("admin", Fixture.Password)));
        Assert.Null(f.Sessions.Authenticate(f.Context(cookie: oldCookie)));
        var restarted = new WebSessionService(f.Db, f.Clock, f.Settings);
        Assert.NotNull(restarted.Authenticate(f.Context(cookie: Cookie(context))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdleRenewal_AbsoluteExpiry_AndRememberMe(bool remember)
    {
        using var f = new Fixture();
        var (cookie, state) = f.Login(remember);
        var idle = remember ? WebSessionService.PersistentIdleSeconds : WebSessionService.IdleSeconds;
        var absolute = remember ? WebSessionService.PersistentAbsoluteSeconds : WebSessionService.AbsoluteSeconds;
        Assert.Equal(f.Clock.GetUtcNow().AddSeconds(absolute).ToUnixTimeSeconds(), state.AbsoluteExpiresAt.ToUnixTimeSeconds());
        f.Clock.Advance(TimeSpan.FromSeconds(idle - 1));
        var renewed = Assert.IsType<WebSession>(f.Sessions.Authenticate(f.Context(cookie: cookie)));
        Assert.True(renewed.ExpiresAt > state.ExpiresAt);
        f.Clock.Advance(TimeSpan.FromSeconds(idle));
        Assert.Null(f.Sessions.Authenticate(f.Context(cookie: cookie)));

        var (secondCookie, _) = f.Login(remember);
        for (var elapsed = 0; elapsed < absolute; elapsed += idle / 2)
        {
            f.Clock.Advance(TimeSpan.FromSeconds(idle / 2));
            var current = f.Sessions.Authenticate(f.Context(cookie: secondCookie));
            if (elapsed + idle / 2 >= absolute) Assert.Null(current);
            else Assert.NotNull(current);
        }
    }

    [Fact]
    public void PersistentCookie_HasBoundedExpiry_AndPathBase()
    {
        using var f = new Fixture();
        var c = f.Context("/api/auth/login", "POST");
        c.Request.PathBase = "/feedarr";
        Assert.IsType<OkObjectResult>(f.Controller(c).Login(new("admin", Fixture.Password, true)));
        var cookie = c.Response.Headers.SetCookie.ToString();
        Assert.StartsWith("FeedarrSession=", cookie);
        Assert.Contains("path=/feedarr", cookie);
        Assert.Contains("expires=", cookie);
        Assert.DoesNotContain("domain=", cookie);
        var request = f.Context(cookie: Cookie(c));
        request.Request.PathBase = "/feedarr";
        Assert.NotNull(f.Sessions.Authenticate(request));
    }

    [Theory]
    [InlineData("username")]
    [InlineData("password")]
    [InlineData("mode")]
    [InlineData("delete")]
    public void SecurityChanges_RevokeSessions_EvenIfSettingsAreRestored(string change)
    {
        using var f = new Fixture();
        var (cookie, _) = f.Login();
        var previous = f.Settings.GetSecurity(new());
        var next = f.Settings.GetSecurity(new());
        if (change == "username") next.Username = "another";
        if (change == "password") next.PasswordSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        if (change == "mode") next.AuthMode = "open";
        if (change == "delete")
        {
            using var db = f.Db.Open();
            db.Execute("DELETE FROM app_settings WHERE key = 'security'");
        }
        else f.Settings.SaveSecurity(next);
        f.Settings.SaveSecurity(previous);
        Assert.Null(f.Sessions.Authenticate(f.Context(cookie: cookie)));
    }

    [Fact]
    public void ConfigurationRace_RefusesSessionCreation()
    {
        using var f = new Fixture();
        var generation = f.Sessions.Generation();
        var settings = f.Settings.GetSecurity(new());
        settings.Username = "changed";
        f.Settings.SaveSecurity(settings);
        var context = f.Context();
        Assert.Null(f.Sessions.Create(context, generation, false));
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public void Logout_RequiresCsrf_RevokesServerSide_AndIsIdempotent()
    {
        using var f = new Fixture();
        var (cookie, session) = f.Login();
        var context = f.Context("/api/auth/logout", "POST", cookie);
        Assert.Equal(403, Assert.IsType<ObjectResult>(f.Controller(context).Logout()).StatusCode);
        Assert.NotNull(f.Sessions.Authenticate(f.Context(cookie: cookie)));
        context.Request.Headers[WebSessionSecurity.CsrfHeader] = session.CsrfToken;
        Assert.IsType<OkObjectResult>(f.Controller(context).Logout());
        Assert.Null(f.Sessions.Authenticate(f.Context(cookie: cookie)));
        Assert.IsType<OkObjectResult>(f.Controller(context).Logout());
    }

    [Theory]
    [InlineData("/api/settings/security")]
    [InlineData("/api/settings/general")]
    [InlineData("/api/configuration-backups/restore")]
    [InlineData("/api/system/onboarding")]
    [InlineData("/api/sources")]
    [InlineData("/api/releases/7/seen")]
    public async Task CookieMutations_RequireSameOriginAndSessionCsrf_EvenOnLegacyExemptions(string path)
    {
        using var f = new Fixture();
        var (cookie, session) = f.Login();
        foreach (var variant in new[] { "missing", "wrong", "foreign", "absent-origin", "other-session", "valid" })
        {
            var context = f.Context(path, "PUT", cookie);
            if (variant != "missing") context.Request.Headers[WebSessionSecurity.CsrfHeader] = session.CsrfToken;
            if (variant == "wrong") context.Request.Headers[WebSessionSecurity.CsrfHeader] = "wrong";
            if (variant == "foreign") context.Request.Headers.Origin = "https://attacker.example";
            if (variant == "absent-origin") context.Request.Headers.Remove("Origin");
            if (variant == "other-session")
            {
                var (_, other) = f.Login();
                context.Request.Headers[WebSessionSecurity.CsrfHeader] = other.CsrfToken;
            }
            var called = await f.Invoke(context);
            Assert.Equal(variant == "valid", called);
            Assert.Equal(variant == "valid" ? 200 : 403, context.Response.StatusCode);
            Assert.False(context.Response.Headers.ContainsKey("Location"));
        }
    }

    [Theory]
    [InlineData("open", true)]
    [InlineData("smart", true)]
    [InlineData("strict", true)]
    public async Task Modes_CookieMutationsAlwaysRequireCsrf(string mode, bool required)
    {
        using var f = new Fixture();
        var settings = f.Settings.GetSecurity(new());
        settings.AuthMode = mode;
        f.Settings.SaveSecurity(settings);
        var (cookie, _) = f.Login();
        var c = f.Context("/api/settings/security", "PUT", cookie);
        if (mode == "smart" && !required) { c.Request.Host = new("localhost"); c.Connection.RemoteIpAddress = IPAddress.Loopback; }
        var state = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(f.Controller(c).Session()).Value);
        Assert.Equal(required, state.GetProperty("authRequired").GetBoolean());
        Assert.False(await f.Invoke(c));
        Assert.Equal(403, c.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/feed/releases")]
    [InlineData("/api/feed/top")]
    [InlineData("/api/sources")]
    [InlineData("/api/releases/7/download")]
    [InlineData("/api/posters/7")]
    public async Task ApiAccess_BasicAndCookieCoexist_AndInvalidBasicNeverFallsBack(string path)
    {
        using var f = new Fixture();
        var (cookie, _) = f.Login();
        foreach (var basic in new[] { "none", "valid", "invalid", "malformed", "bare", "tab" })
        {
            var context = f.Context(path, cookie: cookie);
            if (basic != "none") context.Request.Headers.Authorization = basic switch
            {
                "malformed" => "Basic !!!", "bare" => "Basic", "tab" => "Basic\tbroken",
                _ => Basic("admin", basic == "valid" ? Fixture.Password : "wrong")
            };
            var accepted = basic is "none" or "valid";
            Assert.Equal(accepted, await f.Invoke(context));
            Assert.Equal(accepted ? 200 : 401, context.Response.StatusCode);
            Assert.Equal(!accepted, context.Response.Headers.ContainsKey("WWW-Authenticate"));
            Assert.Equal(basic == "none", context.Items.ContainsKey(WebSessionSecurity.SessionKey));
            Assert.False(context.Response.Headers.ContainsKey("Location"));
        }
        var onlyBasic = f.Context(path);
        onlyBasic.Request.Headers.Authorization = Basic("admin", Fixture.Password);
        Assert.True(await f.Invoke(onlyBasic));
        var invalidCookie = f.Context(path, cookie: "__Host-FeedarrSession=" + new string('A', 64));
        Assert.False(await f.Invoke(invalidCookie));
        Assert.Equal(401, invalidCookie.Response.StatusCode);
    }

    [Fact]
    public async Task BasicUnsafeRequests_DoNotRequireSessionCsrf()
    {
        using var f = new Fixture();
        var (cookie, _) = f.Login();
        var context = f.Context("/api/sources", "POST", cookie);
        context.Request.Headers.Authorization = Basic("admin", Fixture.Password);
        context.Request.Headers.Remove("Origin");
        context.Request.Headers["X-Feedarr-Request"] = "1";
        Assert.True(await f.Invoke(context));
        Assert.False(context.Items.ContainsKey(WebSessionSecurity.SessionKey));
    }

    [Theory]
    [InlineData("https://attacker.example")]
    [InlineData("")]
    [InlineData("null")]
    public async Task LoginAndLogout_RejectUntrustedOrigin(string origin)
    {
        using var f = new Fixture();
        foreach (var path in new[] { "/api/auth/login", "/api/auth/logout" })
        {
            var context = f.Context(path, "POST");
            context.Request.Headers.Origin = origin;
            Assert.False(await f.Invoke(context));
            Assert.Equal(403, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task OnboardingFlag_DoesNotRestrictAuthenticatedAccess()
    {
        using var f = new Fixture();
        var (cookie, _) = f.Login();
        f.Setup.ResetSetupCompleted();
        var protectedRequest = f.Context("/api/feed/releases", cookie: cookie);
        Assert.True(await f.Invoke(protectedRequest));
        var login = f.Context("/api/auth/login", "POST", cookie);
        Assert.True(await f.Invoke(login));
        Assert.IsType<OkObjectResult>(f.Controller(login).Login(new("admin", Fixture.Password)));
        var state = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(f.Controller(f.Context()).Session()).Value);
        Assert.True(state.GetProperty("setupCompleted").GetBoolean());
        var unknown = f.Context("/api/auth/login/child");
        Assert.False(await f.Invoke(unknown));
    }

    [Fact]
    public async Task HttpsOnlyPolicy_RefusesBothCookieAndBasicOverHttp()
    {
        using var f = new Fixture();
        var c = f.Context("/api/auth/login", "POST");
        c.Request.Scheme = "http";
        c.Request.Headers.Origin = "http://feedarr.example";
        c.Request.Headers["X-Forwarded-Proto"] = "https";
        Assert.Equal(403, Assert.IsType<ObjectResult>(f.Controller(c).Login(new("admin", Fixture.Password))).StatusCode);
        Assert.False(c.Response.Headers.ContainsKey("Set-Cookie"));
        var basic = f.Context("/api/feed/releases");
        basic.Request.Scheme = "http";
        basic.Request.Headers.Authorization = Basic("admin", Fixture.Password);
        Assert.False(await f.Invoke(basic));
        Assert.Equal(403, basic.Response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForwardedHttps_OnlyTrustedProxyCanCreateSession(bool trusted)
    {
        using var f = new Fixture();
        var c = f.Context("/api/auth/login", "POST");
        c.Request.Scheme = "http";
        c.Request.Host = new("internal:8080");
        c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.2");
        c.Request.Headers["X-Forwarded-Proto"] = "https";
        c.Request.Headers["X-Forwarded-Host"] = "feedarr.example";
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost };
        options.KnownNetworks.Clear(); options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Parse(trusted ? "10.0.0.2" : "10.0.0.3"));
        IActionResult? result = null;
        await new ForwardedHeadersMiddleware(ctx => { result = f.Controller(ctx).Login(new("admin", Fixture.Password)); return Task.CompletedTask; },
            NullLoggerFactory.Instance, OptionsFactory.Create(options)).Invoke(c);
        Assert.Equal(trusted, result is OkObjectResult);
        Assert.Equal(trusted, c.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("smart")]
    [InlineData("strict")]
    public async Task UnconfiguredSessionState_IsAnonymousWithoutBasicChallenge(string mode)
    {
        using var f = new Fixture();
        f.Settings.SaveSecurity(new() { AuthMode = mode });
        var c = f.Context("/api/auth/session");
        Assert.True(await f.Invoke(c));
        Assert.False(c.Response.Headers.ContainsKey("WWW-Authenticate"));
        var state = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(f.Controller(c).Session()).Value);
        Assert.False(state.GetProperty("authenticated").GetBoolean());
        Assert.False(state.GetProperty("authConfigured").GetBoolean());
        Assert.Equal("no-store", c.Response.Headers.CacheControl.ToString());
        Assert.Equal(409, Assert.IsType<ObjectResult>(f.Controller(f.Context("/api/auth/login", "POST")).Login(new("admin", Fixture.Password))).StatusCode);
    }

    [Fact]
    public async Task RealMvcRouting_JsonContractsNoStoreAndLoginIpRateLimit()
    {
        using var f = new Fixture();
        await using var app = f.Application();
        var pipeline = ((IApplicationBuilder)app).Build();
        var anonymous = f.Context("/api/auth/session");
        anonymous.RequestServices = app.Services;
        await pipeline(anonymous);
        Assert.Equal(200, anonymous.Response.StatusCode);
        Assert.Contains("no-store", anonymous.Response.Headers.CacheControl.ToString());
        Assert.False(anonymous.Response.Headers.ContainsKey("WWW-Authenticate"));
        Assert.Contains("application/json", anonymous.Response.ContentType);
        Assert.False(ReadJson(anonymous).GetProperty("authenticated").GetBoolean());

        for (var i = 0; i < 11; i++)
        {
            var login = f.Context("/api/auth/login", "POST");
            login.RequestServices = app.Services;
            SetJson(login, new { username = "varying-user-" + i, password = "wrong", rememberMe = false });
            await pipeline(login);
            Assert.Equal(i < 10 ? 401 : 429, login.Response.StatusCode);
            Assert.Contains("no-store", login.Response.Headers.CacheControl.ToString());
            Assert.False(login.Response.Headers.ContainsKey("Set-Cookie"));
            Assert.False(login.Response.Headers.ContainsKey("Location"));
            if (i < 10) Assert.Equal("invalid_credentials", ReadJson(login).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task RealMiddlewareInjection_RecognizesCookie_AndFilterAcceptsOnlyAuthorizedRequests()
    {
        using var f = new Fixture();
        await using var app = f.Application();
        var pipeline = ((IApplicationBuilder)app).Build();
        var login = f.Context("/api/auth/login", "POST");
        login.RequestServices = app.Services;
        SetJson(login, new { username = "admin", password = Fixture.Password, rememberMe = true });
        await pipeline(login);
        Assert.Equal(200, login.Response.StatusCode);
        var state = ReadJson(login);
        Assert.True(state.GetProperty("authenticated").GetBoolean());
        Assert.True(state.GetProperty("rememberMe").GetBoolean());
        var csrf = state.GetProperty("csrfToken").GetString();
        var cookie = Cookie(login);
        var logout = f.Context("/api/auth/logout", "POST", cookie);
        logout.RequestServices = app.Services;
        logout.Request.Headers[WebSessionSecurity.CsrfHeader] = csrf;
        await pipeline(logout);
        Assert.Equal(200, logout.Response.StatusCode);
        Assert.False(ReadJson(logout).GetProperty("authenticated").GetBoolean());
        Assert.Null(f.Sessions.Authenticate(f.Context(cookie: cookie)));

        var invalid = f.Context("/api/auth/session", cookie: cookie);
        invalid.RequestServices = app.Services;
        invalid.Request.Headers.Authorization = Basic("admin", "wrong");
        await pipeline(invalid);
        Assert.Equal(401, invalid.Response.StatusCode);
        Assert.Equal("Basic realm=\"Feedarr\"", invalid.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public void StrippedProxyPrefix_UsesPersistedPublicBaseUrl_NotForwardedPrefix()
    {
        using var f = new Fixture();
        var settings = f.Settings.GetSecurity(new());
        settings.PublicBaseUrl = "https://feedarr.example/feedarr/";
        f.Settings.SaveSecurity(settings);
        var c = f.Context("/api/auth/login", "POST");
        c.Request.Headers["X-Forwarded-Prefix"] = "/attacker";
        Assert.IsType<OkObjectResult>(f.Controller(c).Login(new("admin", Fixture.Password)));
        Assert.Contains("path=/feedarr", c.Response.Headers.SetCookie.ToString());
        Assert.StartsWith("FeedarrSession=", c.Response.Headers.SetCookie.ToString());
        Assert.NotNull(f.Sessions.Authenticate(f.Context(cookie: Cookie(c))));
    }



    [Fact]
    public void UnknownAlteredAndRevokedCookies_CannotAuthenticate()
    {
        using var f = new Fixture();
        var (cookie, _) = f.Login();
        var last = cookie[^1] == 'A' ? 'B' : 'A';
        Assert.Null(f.Sessions.Authenticate(f.Context(cookie: cookie[..^1] + last)));
        Assert.Null(f.Sessions.Authenticate(f.Context(cookie: "__Host-FeedarrSession=invalid")));
        f.Sessions.Revoke(f.Context(cookie: cookie));
        Assert.Null(new WebSessionService(f.Db, f.Clock, f.Settings).Authenticate(f.Context(cookie: cookie)));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/library")]
    [InlineData("/settings/users")]
    [InlineData("/setup")]
    public async Task NeutralShell_IsPublic_ButApiAndUnknownFilesRemainProtected(string path)
    {
        using var f = new Fixture();
        f.Setup.ResetSetupCompleted();
        var shell = f.Context(path);
        Assert.True(await f.Invoke(shell));
        Assert.Equal("public-v1", shell.Response.Headers["X-Feedarr-Shell"].ToString());
        Assert.False(shell.Response.Headers.ContainsKey("WWW-Authenticate"));
        foreach (var privatePath in new[] { "/api/feed/releases", "/api/sources", "/private.json", "/login/private.json" })
        {
            var api = f.Context(privatePath);
            Assert.False(await f.Invoke(api));
            Assert.False(api.Response.Headers.ContainsKey("X-Feedarr-Shell"));
        }
    }

    [Theory]
    [InlineData("/api/feed/releases")]
    [InlineData("/api/feed/top")]
    [InlineData("/api/sources")]
    [InlineData("/api/releases/7/download")]
    [InlineData("/api/posters/7")]
    public async Task BrowserUnauthorized_IsJsonWithoutChallenge_ExplicitBasicKeepsItsContract(string path)
    {
        using var f = new Fixture();
        var web = f.Context(path);
        web.Request.Headers["X-Feedarr-Web"] = "1";
        Assert.False(await f.Invoke(web));
        Assert.Equal(401, web.Response.StatusCode);
        Assert.False(web.Response.Headers.ContainsKey("WWW-Authenticate"));
        Assert.False(web.Response.Headers.ContainsKey("Location"));
        Assert.Equal("authentication_required", ReadJson(web).GetProperty("error").GetString());
        var basic = f.Context(path);
        basic.Request.Headers["X-Feedarr-Web"] = "1";
        basic.Request.Headers.Authorization = Basic("admin", "wrong");
        Assert.False(await f.Invoke(basic));
        Assert.Equal("Basic realm=\"Feedarr\"", basic.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal(0, basic.Response.Body.Length);
    }

    [Fact]
    public async Task AuthenticatedSession_DoesNotDependOnOnboardingCompletion()
    {
        using var f = new Fixture();
        f.Setup.ResetSetupCompleted();
        var (cookie, session) = f.Login();
        var setup = f.Context("/api/settings/ui", "PUT", cookie);
        setup.Request.Headers[WebSessionSecurity.CsrfHeader] = session.CsrfToken;
        Assert.True(await f.Invoke(setup));
        Assert.True(setup.Items.ContainsKey(WebSessionSecurity.SessionKey));
        var feed = f.Context("/api/feed/releases", cookie: cookie);
        Assert.True(await f.Invoke(feed));
    }

    private static JsonElement ReadJson(DefaultHttpContext context)
        => JsonSerializer.Deserialize<JsonElement>(((MemoryStream)context.Response.Body).ToArray());

    private static void SetJson(DefaultHttpContext context, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
    }

    private static string Basic(string user, string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
    private static string Cookie(HttpContext context) => context.Response.Headers.SetCookie.ToString().Split(';')[0];

    private sealed class Fixture : IDisposable
    {
        internal const string Password = "WebSessionPassword123!";
        private readonly string root = Path.Combine(Path.GetTempPath(), "feedarr-web-session-tests", Guid.NewGuid().ToString("N"));
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        internal Db Db { get; }
        internal SettingsRepository Settings { get; }
        internal SetupStateService Setup { get; }
        internal TestClock Clock { get; } = new();
        internal WebSessionService Sessions { get; }
        internal AuthThrottleService Throttle { get; }
        internal Fixture()
        {
            Db = new(OptionsFactory.Create(new AppOptions { DataDir = root, DbFileName = "test.db" }));
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();
            Settings = new(Db);
            Setup = new(Settings, cache);
            Sessions = new(Db, Clock, Settings);
            Throttle = new(OptionsFactory.Create(new BasicAuthThrottleOptions()), Clock);
            var salt = RandomNumberGenerator.GetBytes(16);
            Settings.SaveSecurity(new() { AuthMode = "strict", Username = "admin", PasswordSalt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Password, salt, 100_000, HashAlgorithmName.SHA256, 32)) });
            Setup.MarkSetupCompleted();
        }
        internal DefaultHttpContext Context(string path = "/api/auth/session", string method = "GET", string? cookie = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path; context.Request.Method = method;
            context.Request.Scheme = "https"; context.Request.Host = new("feedarr.example");
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            context.Request.Headers.Origin = "https://feedarr.example";
            if (cookie is not null) context.Request.Headers.Cookie = cookie;
            context.Response.Body = new MemoryStream();
            return context;
        }
        internal AuthController Controller(HttpContext context) => new(Settings, Sessions, Throttle)
            { ControllerContext = new() { HttpContext = context } };
        internal (string Cookie, WebSession Session) Login(bool remember = false)
        {
            var context = Context("/api/auth/login", "POST");
            Assert.IsType<OkObjectResult>(Controller(context).Login(new("admin", Password, remember)));
            var cookie = WebSessionTests.Cookie(context);
            return (cookie, Assert.IsType<WebSession>(Sessions.Authenticate(Context(cookie: cookie))));
        }
        internal async Task<bool> Invoke(HttpContext context)
        {
            var called = false;
            var config = new ConfigurationBuilder().Build();
            var csrf = new AntiCsrfOriginMiddleware(_ => { called = true; return Task.CompletedTask; });
            var middleware = new BasicAuthMiddleware(ctx => csrf.InvokeAsync(ctx, config, NullLogger<AntiCsrfOriginMiddleware>.Instance), config);
            await middleware.InvokeAsync(context, Settings, cache, Throttle,
                NullLogger<BasicAuthMiddleware>.Instance, Sessions);
            return called;
        }
        internal WebApplication Application()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Services.AddSingleton(Settings);
            builder.Services.AddSingleton(Setup);
            builder.Services.AddSingleton(Sessions);
            builder.Services.AddSingleton(Throttle);
            builder.Services.AddSingleton<IMemoryCache>(cache);
            builder.Services.AddScoped<RequireAuthFilter>();
            builder.Services.AddScoped<ApiErrorNormalizationFilter>();
            builder.Services.AddControllers(options =>
            {
                options.Filters.AddService<RequireAuthFilter>();
                options.Filters.AddService<ApiErrorNormalizationFilter>();
            }).AddApplicationPart(typeof(AuthController).Assembly);
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = 429;
                WebSessionSecurity.AddLoginRateLimit(options);
            });
            var app = builder.Build();
            app.UseRouting();
            app.Use(async (context, next) =>
            {
                WebSessionSecurity.DisableCaching(context);
                await next();
            });
            app.UseRateLimiter();
            app.UseMiddleware<BasicAuthMiddleware>();
            app.UseMiddleware<AntiCsrfOriginMiddleware>();
#pragma warning disable ASP0014 // Build the complete endpoint pipeline without opening a server socket.
            app.UseEndpoints(endpoints => endpoints.MapControllers());
#pragma warning restore ASP0014
            return app;
        }
        public void Dispose() { cache.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan duration) => now += duration;
    }
}
