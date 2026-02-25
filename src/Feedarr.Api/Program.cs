using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Arr;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Fanart;
using Feedarr.Api.Services.GoogleBooks;
using Feedarr.Api.Services.Igdb;
using Feedarr.Api.Services.Jackett;
using Feedarr.Api.Services.Jikan;
using Feedarr.Api.Services.Metadata;
using Feedarr.Api.Services.Prowlarr;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.ComicVine;
using Feedarr.Api.Services.TheAudioDb;
using Feedarr.Api.Services.MusicBrainz;
using Feedarr.Api.Services.OpenLibrary;
using Feedarr.Api.Services.Rawg;
using Feedarr.Api.Services.Torznab;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.TvMaze;
using Microsoft.AspNetCore.Routing;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Diagnostics;
using Feedarr.Api.Filters;
using Feedarr.Api.Services.Updates;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.RateLimiting;
using Feedarr.Api.Services.Resilience;

var builder = WebApplication.CreateBuilder(args);
// Default false: in a reverse-proxy setup the proxy owns TLS.
// Set App:Security:EnforceHttps=true only if ASP.NET Core terminates TLS directly.
var enforceHttps = builder.Configuration.GetValue("App:Security:EnforceHttps", false);
var emitSecurityHeaders = builder.Configuration.GetValue("App:Security:EmitSecurityHeaders", true);
// Allows self-signed / invalid TLS certs for internal services (Sonarr, Radarr, Jackett, etc.).
// SECURITY: leave false in production. Only enable on home-lab setups with self-signed certs.
var allowInvalidCerts = builder.Configuration.GetValue("App:HttpClients:AllowInvalidCertificates", false);
var statsRateLimitPermit = Math.Max(10, builder.Configuration.GetValue("App:RateLimit:Stats:PermitLimit", 120));
var statsRateLimitWindowSeconds = Math.Clamp(builder.Configuration.GetValue("App:RateLimit:Stats:WindowSeconds", 60), 10, 3600);
var globalRateLimitPermit = Math.Max(10, builder.Configuration.GetValue("App:RateLimit:Global:PermitLimit", 300));
var globalRateLimitWindowSeconds = Math.Clamp(builder.Configuration.GetValue("App:RateLimit:Global:WindowSeconds", 60), 10, 3600);

// Data Protection pour le chiffrement des clés API
var configuredDataDir = builder.Configuration.GetValue<string>("App:DataDir") ?? "data";
var dataDirPath = Path.IsPathRooted(configuredDataDir)
    ? configuredDataDir
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, configuredDataDir));
var keysPath = Path.Combine(dataDirPath, "keys");
Directory.CreateDirectory(keysPath);
MigrateLegacyDataProtectionKeys(Path.Combine(AppContext.BaseDirectory, "Data", "keys"), keysPath);

var dataProtectionBuilder = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("Feedarr");

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    var trustedProxies = builder.Configuration.GetSection("App:ReverseProxy:TrustedProxies").Get<string[]>() ?? Array.Empty<string>();
    foreach (var raw in trustedProxies)
    {
        if (IPAddress.TryParse(raw?.Trim(), out var ip))
            options.KnownProxies.Add(ip);
    }

    var trustedNetworks = builder.Configuration.GetSection("App:ReverseProxy:TrustedNetworks").Get<string[]>() ?? Array.Empty<string>();
    foreach (var raw in trustedNetworks)
    {
        if (TryParseIpNetwork(raw, out var network))
            options.KnownNetworks.Add(network);
    }
});

// Chiffrement des master keys selon la plateforme
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    // Windows: utilise DPAPI (chiffrement natif Windows lié au compte utilisateur)
    dataProtectionBuilder.ProtectKeysWithDpapi();
}
else
{
    // Linux/Docker: génère une clé maître et utilise AES-256-GCM
    var masterKeyProvider = new FileMasterKeyProvider(keysPath);
    builder.Services.AddSingleton<IMasterKeyProvider>(masterKeyProvider);

    // Configure DataProtection pour utiliser notre encrypteur AES personnalisé
    builder.Services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(options =>
    {
        options.XmlEncryptor = new AesXmlEncryptor(masterKeyProvider.GetMasterKey());
    });
}

builder.Services.AddScoped<ApiRequestMetricsFilter>();
builder.Services.AddScoped<ApiErrorNormalizationFilter>();
builder.Services.AddScoped<RequireAuthFilter>();
builder.Services.AddControllers(options =>
{
    options.Filters.AddService<ApiRequestMetricsFilter>();
    options.Filters.AddService<ApiErrorNormalizationFilter>();
    options.Filters.AddService<RequireAuthFilter>();
});

SqlMapper.AddTypeHandler(new SqliteInt32Handler());
SqlMapper.AddTypeHandler(new SqliteNullableInt32Handler());

// Options + DB
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.Configure<UpdatesOptions>(builder.Configuration.GetSection("App:Updates"));
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<MigrationsRunner>();

// Security - API Key Encryption
builder.Services.AddSingleton<IApiKeyProtectionService, ApiKeyProtectionService>();
builder.Services.AddSingleton<ApiKeyMigrationService>();

// Repositories
builder.Services.AddSingleton<SourceRepository>();
builder.Services.AddSingleton<ProviderRepository>();
builder.Services.AddSingleton<ReleaseRepository>();
builder.Services.AddSingleton<ActivityRepository>();
builder.Services.AddSingleton<SettingsRepository>();
builder.Services.AddSingleton<ExternalProviderInstanceRepository>();
builder.Services.AddSingleton<StatsRepository>();
builder.Services.AddSingleton<ArrApplicationRepository>();
builder.Services.AddSingleton<ArrLibraryRepository>();
builder.Services.AddSingleton<MediaEntityRepository>();
builder.Services.AddSingleton<MediaEntityArrStatusRepository>();

// Maintenance lock — prevents concurrent execution of heavy SQLite operations
builder.Services.AddSingleton<MaintenanceLockService>();

// Services
builder.Services.AddSingleton<BadgeSignal>();
builder.Services.AddSingleton<ProviderStatsService>();
builder.Services.AddSingleton<ApiRequestMetricsService>();
builder.Services.AddSingleton<BackupExecutionCoordinator>();
builder.Services.AddSingleton<BackupValidationService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<UnifiedCategoryService>();
builder.Services.AddSingleton<UnifiedCategoryResolver>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<CategoryRecommendationService>();
builder.Services.AddSingleton<ExternalProviderRegistry>();
builder.Services.AddSingleton<ActiveExternalProviderConfigResolver>();
builder.Services.AddSingleton<ExternalProviderTestService>();
builder.Services.AddHostedService<RssSyncHostedService>();
builder.Services.AddSingleton<Feedarr.Api.Services.Titles.TitleParser>();
builder.Services.AddSingleton<VideoMatchingStrategy>();
builder.Services.AddSingleton<GameMatchingStrategy>();
builder.Services.AddSingleton<AnimeMatchingStrategy>();
builder.Services.AddSingleton<AudioMatchingStrategy>();
builder.Services.AddSingleton<GenericMatchingStrategy>();
builder.Services.AddSingleton<PosterMatchingOrchestrator>();
builder.Services.AddSingleton<PosterFetchService>();
builder.Services.AddSingleton<PosterMatchCacheService>();
builder.Services.AddSingleton<PosterFetchJobFactory>();
builder.Services.AddSingleton<SyncOrchestrationService>();
builder.Services.AddSingleton<RetroFetchLogService>();
builder.Services.AddSingleton<IPosterFetchQueue, PosterFetchQueue>();
builder.Services.AddHostedService<PosterFetchWorker>();
builder.Services.AddSingleton<MediaEntityArrStatusService>();
builder.Services.AddSingleton<ExternalIdBackfillService>();
builder.Services.AddSingleton<RequestTmdbResolverService>();
builder.Services.AddSingleton<RequestTmdbBackfillService>();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddSingleton<ReleaseInfoService>();
builder.Services.AddSingleton<SetupStateService>();

builder.Services.AddHttpClient("github-updates", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
});

// Resilience: transient retry handler for external HTTP clients
builder.Services.AddTransient<TransientHttpRetryHandler>();
builder.Services.AddTransient<ProtocolDowngradeRedirectHandler>();

// Torznab (already has its own retry logic in TorznabClient)
builder.Services.AddSingleton<TorznabRssParser>();
builder.Services.AddHttpClient<TorznabClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(45);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 5,
    ServerCertificateCustomValidationCallback = allowInvalidCerts
        ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        : null
});

// TMDB
builder.Services.AddHttpClient<TmdbClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.BaseAddress = new Uri("https://api.themoviedb.org/3/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// Fanart
builder.Services.AddHttpClient<FanartClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(25);
    c.BaseAddress = new Uri("https://webservice.fanart.tv/v3/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// IGDB
builder.Services.AddHttpClient<IgdbClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(25);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// TVmaze
builder.Services.AddHttpClient<TvMazeClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.BaseAddress = new Uri("https://api.tvmaze.com/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// Jikan
builder.Services.AddHttpClient<JikanClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.BaseAddress = new Uri("https://api.jikan.moe/v4/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// Google Books
builder.Services.AddHttpClient<GoogleBooksClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.BaseAddress = new Uri("https://www.googleapis.com/books/v1/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// TheAudioDB
builder.Services.AddHttpClient<TheAudioDbClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.BaseAddress = new Uri("https://www.theaudiodb.com/api/v1/json/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// Comic Vine
builder.Services.AddHttpClient<ComicVineClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.BaseAddress = new Uri("https://comicvine.gamespot.com/api/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// Open Library — free, no API key required
builder.Services.AddHttpClient<OpenLibraryClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.BaseAddress = new Uri("https://openlibrary.org/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0 ( https://github.com/Guizmos/feedarr )");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// MusicBrainz — no API key required, User-Agent mandatory
builder.Services.AddHttpClient<MusicBrainzClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(25);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0 ( https://github.com/Guizmos/feedarr )");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// RAWG — free tier API key required (rawg.io/apidocs)
builder.Services.AddHttpClient<RawgClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.BaseAddress = new Uri("https://api.rawg.io/api/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// Sonarr
builder.Services.AddHttpClient<SonarrClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 5,
    ServerCertificateCustomValidationCallback = allowInvalidCerts
        ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        : null
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// Radarr
builder.Services.AddHttpClient<RadarrClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 5,
    ServerCertificateCustomValidationCallback = allowInvalidCerts
        ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        : null
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// Overseerr/Jellyseerr/Seer
builder.Services.AddHttpClient<EerrRequestClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 5,
    ServerCertificateCustomValidationCallback = allowInvalidCerts
        ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        : null
}).AddHttpMessageHandler<TransientHttpRetryHandler>();

// Jackett — AllowAutoRedirect=false + ProtocolDowngradeRedirectHandler
// pour suivre les redirections HTTPS → HTTP (reverse proxy)
builder.Services.AddHttpClient<JackettClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    ServerCertificateCustomValidationCallback = allowInvalidCerts
        ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        : null
}).AddHttpMessageHandler<ProtocolDowngradeRedirectHandler>()
  .AddHttpMessageHandler<TransientHttpRetryHandler>();

// Prowlarr — même traitement
builder.Services.AddHttpClient<ProwlarrClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    ServerCertificateCustomValidationCallback = allowInvalidCerts
        ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        : null
}).AddHttpMessageHandler<ProtocolDowngradeRedirectHandler>()
  .AddHttpMessageHandler<TransientHttpRetryHandler>();

// Arr Library Cache (in-memory, will be deprecated)
builder.Services.AddSingleton<ArrLibraryCacheService>();

// Arr Library Sync (background service for persistent storage)
// Register as singleton first, then as hosted service (allows injection in controllers)
builder.Services.AddSingleton<ArrLibrarySyncService>();
builder.Services.AddHostedService<ArrLibrarySyncService>(sp => sp.GetRequiredService<ArrLibrarySyncService>());

// CORS: restricted policy for development (Vite on port 5173)
builder.Services.AddCors(o =>
{
    o.AddPolicy("dev", p =>
        p.WithOrigins("http://localhost:5173")
         .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
         .WithHeaders("Content-Type", "Authorization", "X-Requested-With")
         .AllowCredentials()
    );
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = static async (context, token) =>
    {
        if (!context.HttpContext.Response.HasStarted)
        {
            context.HttpContext.Response.ContentType = "application/json";
            await context.HttpContext.Response.WriteAsJsonAsync(
                new { error = "too many requests" },
                cancellationToken: token);
        }
    };

    options.AddPolicy("stats-heavy", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = statsRateLimitPermit,
                Window = TimeSpan.FromSeconds(statsRateLimitWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        if (httpContext.Request.Path.StartsWithSegments("/api/posters", StringComparison.OrdinalIgnoreCase))
        {
            // Poster endpoints can spike heavily during retro-fetch and image retries.
            // Excluding them from the global limiter prevents app-wide 429 lockups.
            return RateLimitPartition.GetNoLimiter("posters");
        }

        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = globalRateLimitPermit,
                Window = TimeSpan.FromSeconds(globalRateLimitWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

if (enforceHttps)
{
    app.UseHttpsRedirection();
}

if (emitSecurityHeaders)
{
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.TryAdd("X-Content-Type-Options", "nosniff");
            headers.TryAdd("X-Frame-Options", "DENY");
            headers.TryAdd("Referrer-Policy", "no-referrer");
            headers.TryAdd("X-Permitted-Cross-Domain-Policies", "none");
            headers.TryAdd("Permissions-Policy", "geolocation=(), microphone=(), camera=()");
            // Hash covers the inline theme-detection <script> block baked into index.html by Vite.
            // Recompute if that block ever changes (run from feedarr-web/):
            //   node -e "const c=require('crypto'),h=require('fs').readFileSync('dist/index.html','utf8');const s=h.indexOf('<script>'),e=h.indexOf('</script>',s);console.log('sha256-'+c.createHash('sha256').update(h.slice(s+'<script>'.length,e)).digest('base64'));"
            headers.TryAdd("Content-Security-Policy",
                "default-src 'self'; " +
                "script-src 'self' 'sha256-xJMtJybfJiXqQTAxXQOlOZvL1dMOajZ39gyMoArS5Ck='; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: blob: https:; " +
                "font-src 'self'; " +
                "connect-src 'self'; " +
                "worker-src 'self'; " +
                "manifest-src 'self'; " +
                "object-src 'none'; " +
                "frame-ancestors 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'");
            return Task.CompletedTask;
        });
        await next();
    });
}

// CORS: only enable in development (Vite dev server)
if (app.Environment.IsDevelopment())
{
    app.UseCors("dev");
}

// Global exception handler: catch unhandled exceptions and return ProblemDetails
app.UseExceptionHandler(errApp =>
{
    errApp.Run(async context =>
    {
        var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
        var ex = exceptionFeature?.Error;

        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Feedarr.GlobalExceptionHandler");

        if (ex is ApiKeyDecryptionException decryptEx)
        {
            // Route to 422 or 503 depending on whether the problem is a bad stored credential
            // or an unavailable crypto subsystem (key ring files inaccessible).
            var isCryptoInfra = decryptEx.Reason == DecryptionFailureReason.CryptoSubsystemUnavailable;

            if (isCryptoInfra)
            {
                logger.LogError(decryptEx,
                    "Crypto subsystem unavailable for {Method} {Path} – data/keys may be unreadable",
                    context.Request.Method, context.Request.Path);
            }
            else
            {
                logger.LogError(decryptEx,
                    "API key decryption failure for {Method} {Path} – stored credential is invalid",
                    context.Request.Method, context.Request.Path);
            }

            context.Response.StatusCode = isCryptoInfra
                ? StatusCodes.Status503ServiceUnavailable   // transient infra — retry may work
                : StatusCodes.Status422UnprocessableEntity; // bad stored data — user must act

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "api_key_decryption_failed",
                reason = isCryptoInfra ? "crypto_subsystem_unavailable" : "invalid_stored_secret",
                message = isCryptoInfra
                    ? "The encryption subsystem is unavailable. " +
                      "Ensure the data/keys directory is mounted and readable, then restart Feedarr."
                    : "One or more API keys could not be decrypted. " +
                      "The DataProtection key ring may have changed (new machine, Docker volume reset, " +
                      "or backup restored from a different host). " +
                      "Go to Settings → External Providers and re-enter the affected credentials."
            });
            return;
        }

        if (ex is not null)
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        var problem = new { type = "https://tools.ietf.org/html/rfc9110#section-15.6.1", title = "internal server error", status = 500 };
        await context.Response.WriteAsJsonAsync(problem);
    });
});

app.UseRateLimiter();

// Security (Basic Auth)
app.UseMiddleware<BasicAuthMiddleware>();

// Monolithic SPA: serve React build from wwwroot/
app.UseDefaultFiles();
app.UseStaticFiles();

// DB migrations on boot
app.Services.GetRequiredService<Db>().EnsureWalMode();
app.Services.GetRequiredService<MigrationsRunner>().Run();

// Migrate existing API keys to encrypted format
await app.Services.GetRequiredService<ApiKeyMigrationService>().MigrateAsync();
app.Services.GetRequiredService<BackupService>().InitializeForStartup();

// Build fingerprint — visible dans les logs au démarrage pour confirmer que le bon binaire tourne.
// Chercher : [BUILD] CATS_STANDARDONLY_V2
var _startupLog = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Feedarr.Startup");
var _asm = typeof(Feedarr.Api.Controllers.CategoriesController).Assembly;
var _buildTs = System.IO.File.GetLastWriteTimeUtc(_asm.Location).ToString("yyyy-MM-dd HH:mm:ss UTC");
_startupLog.LogInformation("[BUILD] Feedarr.Api built={T} — CATS_STANDARDONLY_V2", _buildTs);

if (allowInvalidCerts)
{
    _startupLog.LogWarning(
        "[SECURITY] App:HttpClients:AllowInvalidCertificates = true — TLS certificate validation is DISABLED " +
        "for internal HTTP clients (Torznab, Sonarr, Radarr, Arr, Jackett, Prowlarr). " +
        "Only enable this on trusted home-lab networks with self-signed certificates.");
}

// GET /health — liveness/readiness probe pour Docker/orchestrateurs.
// Effectue un SELECT 1 sur la DB SQLite pour vérifier que la couche de données répond.
app.MapGet("/health", (Db db, ILoggerFactory loggerFactory) =>
{
    var healthLog = loggerFactory.CreateLogger("Feedarr.Health");
    try
    {
        using var conn = db.Open();
        conn.ExecuteScalar<int>("SELECT 1;");
        return Results.Ok(new { status = "up" });
    }
    catch (Exception ex)
    {
        healthLog.LogError(ex, "Health check failed – database is unavailable");
        return Results.Problem(
            detail: "database unavailable",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "service unavailable");
    }
});

app.MapControllers();

// Guard: unmatched /api/* routes must return JSON 404, not index.html.
// Without this, MapFallbackToFile would serve the SPA for invalid API paths.
app.MapFallback("/api/{**slug}", () => Results.NotFound(new { error = "not found" }));

// SPA fallback: any non-API route (e.g. /library, /settings) returns index.html
app.MapFallbackToFile("index.html");

if (app.Environment.IsDevelopment())
{
    app.MapGet("/__routes", (IEnumerable<EndpointDataSource> sources) =>
    {
        var routes = sources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => new
            {
                route = e.RoutePattern.RawText,
                methods = string.Join(",", e.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods ?? new List<string>()),
                displayName = e.DisplayName
            })
            .OrderBy(x => x.route);

        return Results.Ok(routes);
    });
}

await app.RunAsync();

static bool TryParseIpNetwork(string? value, out Microsoft.AspNetCore.HttpOverrides.IPNetwork network)
{
    network = default!;
    if (string.IsNullOrWhiteSpace(value))
        return false;

    var trimmed = value.Trim();
    var slash = trimmed.IndexOf('/');

    if (slash < 0)
    {
        if (!IPAddress.TryParse(trimmed, out var singleAddress))
            return false;

        var singlePrefix = singleAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        network = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(singleAddress, singlePrefix);
        return true;
    }

    var addressPart = trimmed[..slash].Trim();
    var prefixPart = trimmed[(slash + 1)..].Trim();
    if (!IPAddress.TryParse(addressPart, out var address))
        return false;
    if (!int.TryParse(prefixPart, out var prefixLength))
        return false;

    var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
    if (prefixLength < 0 || prefixLength > maxPrefix)
        return false;

    network = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(address, prefixLength);
    return true;
}

static void MigrateLegacyDataProtectionKeys(string legacyPath, string targetPath)
{
    var legacyFull = Path.GetFullPath(legacyPath);
    var targetFull = Path.GetFullPath(targetPath);

    if (string.Equals(legacyFull, targetFull, StringComparison.OrdinalIgnoreCase))
        return;

    if (!Directory.Exists(legacyFull))
        return;

    if (Directory.EnumerateFileSystemEntries(targetFull).Any())
        return;

    foreach (var sourceFile in Directory.EnumerateFiles(legacyFull, "*", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(legacyFull, sourceFile);
        var destinationFile = Path.Combine(targetFull, relativePath);
        var destinationDir = Path.GetDirectoryName(destinationFile);
        if (!string.IsNullOrWhiteSpace(destinationDir))
            Directory.CreateDirectory(destinationDir);

        File.Copy(sourceFile, destinationFile, overwrite: false);
    }
}

sealed class SqliteInt32Handler : SqlMapper.TypeHandler<int>
{
    public override int Parse(object value)
        => value is int i ? i : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    public override void SetValue(IDbDataParameter parameter, int value)
        => parameter.Value = value;
}

sealed class SqliteNullableInt32Handler : SqlMapper.TypeHandler<int?>
{
    public override int? Parse(object value)
        => value is null || value is DBNull
            ? null
            : value is int i ? i : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    public override void SetValue(IDbDataParameter parameter, int? value)
        => parameter.Value = value ?? (object)DBNull.Value;
}
