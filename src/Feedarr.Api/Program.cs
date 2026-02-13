using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Arr;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Fanart;
using Feedarr.Api.Services.Igdb;
using Feedarr.Api.Services.Jackett;
using Feedarr.Api.Services.Prowlarr;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Torznab;
using Feedarr.Api.Services.Tmdb;
using Feedarr.Api.Services.TvMaze;
using Microsoft.AspNetCore.Routing;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Diagnostics;
using Feedarr.Api.Filters;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var enforceHttps = builder.Configuration.GetValue("App:Security:EnforceHttps", !builder.Environment.IsDevelopment());
var emitSecurityHeaders = builder.Configuration.GetValue("App:Security:EmitSecurityHeaders", true);
var statsRateLimitPermit = Math.Max(10, builder.Configuration.GetValue("App:RateLimit:Stats:PermitLimit", 120));
var statsRateLimitWindowSeconds = Math.Clamp(builder.Configuration.GetValue("App:RateLimit:Stats:WindowSeconds", 60), 10, 3600);

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
builder.Services.AddControllers(options =>
{
    options.Filters.AddService<ApiRequestMetricsFilter>();
    options.Filters.AddService<ApiErrorNormalizationFilter>();
});

SqlMapper.AddTypeHandler(new SqliteInt32Handler());
SqlMapper.AddTypeHandler(new SqliteNullableInt32Handler());

// Options + DB
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
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
builder.Services.AddSingleton<StatsRepository>();
builder.Services.AddSingleton<ArrApplicationRepository>();
builder.Services.AddSingleton<ArrLibraryRepository>();
builder.Services.AddSingleton<MediaEntityRepository>();
builder.Services.AddSingleton<MediaEntityArrStatusRepository>();

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
builder.Services.AddHostedService<RssSyncHostedService>();
builder.Services.AddSingleton<Feedarr.Api.Services.Titles.TitleParser>();
builder.Services.AddSingleton<PosterFetchService>();
builder.Services.AddSingleton<PosterMatchCacheService>();
builder.Services.AddSingleton<PosterFetchJobFactory>();
builder.Services.AddSingleton<SyncOrchestrationService>();
builder.Services.AddSingleton<RetroFetchLogService>();
builder.Services.AddSingleton<IPosterFetchQueue, PosterFetchQueue>();
builder.Services.AddHostedService<PosterFetchWorker>();
builder.Services.AddSingleton<MediaEntityArrStatusService>();
builder.Services.AddSingleton<RetentionService>();

// Torznab
builder.Services.AddSingleton<TorznabRssParser>();
builder.Services.AddHttpClient<TorznabClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(45);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
});

// TMDB
builder.Services.AddHttpClient<TmdbClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.BaseAddress = new Uri("https://api.themoviedb.org/3/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
});

// Fanart
builder.Services.AddHttpClient<FanartClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(25);
    c.BaseAddress = new Uri("https://webservice.fanart.tv/v3/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
});

// IGDB
builder.Services.AddHttpClient<IgdbClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(25);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
});

// TVmaze
builder.Services.AddHttpClient<TvMazeClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.BaseAddress = new Uri("https://api.tvmaze.com/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
});

// Sonarr
builder.Services.AddHttpClient<SonarrClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
});

// Radarr
builder.Services.AddHttpClient<RadarrClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
});

// Jackett
builder.Services.AddHttpClient<JackettClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
});

// Prowlarr
builder.Services.AddHttpClient<ProwlarrClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Feedarr/1.0");
});

// Arr Library Cache (in-memory, will be deprecated)
builder.Services.AddSingleton<ArrLibraryCacheService>();

// Arr Library Sync (background service for persistent storage)
// Register as singleton first, then as hosted service (allows injection in controllers)
builder.Services.AddSingleton<ArrLibrarySyncService>();
builder.Services.AddHostedService<ArrLibrarySyncService>(sp => sp.GetRequiredService<ArrLibrarySyncService>());

// (OPTIONNEL mais conseillé si front Vite sur 5173)
builder.Services.AddCors(o =>
{
    o.AddPolicy("dev", p =>
        p.WithOrigins("http://localhost:5173")
         .AllowAnyHeader()
         .AllowAnyMethod()
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
            headers.TryAdd("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'");
            return Task.CompletedTask;
        });
        await next();
    });
}

// CORS (optionnel)
app.UseCors("dev");

app.UseRateLimiter();

// Security (Basic Auth)
app.UseMiddleware<BasicAuthMiddleware>();

// DB migrations on boot
app.Services.GetRequiredService<Db>().EnsureWalMode();
app.Services.GetRequiredService<MigrationsRunner>().Run();

// Migrate existing API keys to encrypted format
await app.Services.GetRequiredService<ApiKeyMigrationService>().MigrateAsync();
app.Services.GetRequiredService<BackupService>().InitializeForStartup();

app.MapGet("/", () => Results.Text("Feedarr.Api OK"));
app.MapControllers();

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
