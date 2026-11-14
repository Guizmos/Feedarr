using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Backup.Configuration;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.ExternalProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Fixture = Feedarr.Api.Tests.ConfigurationArchiveServiceTests.Fixture;

namespace Feedarr.Api.Tests;

public sealed class ConfigurationBackupTests
{
    private const string Password = "A portable configuration password!";

    [Fact]
    public async Task EncryptedRoundTripPreservesTargetIdentitySessionsAndRuntime()
    {
        using var origin = new Fixture(); using var target = new Fixture();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new SettingsRepository(target.Db);
        using var c = target.Db.Open();
        target.Set(c, "security", new SecuritySettings { RequireHttps = true, Username = "target-owner", PasswordHash = "kept-hash", PasswordSalt = "kept-salt" });
        c.Execute("INSERT INTO sources(id,name,torznab_url,api_key,created_at_ts,updated_at_ts) VALUES(222,'history','https://history.example/api','',1,1); INSERT INTO media_entities(id,unified_category,title_clean,created_at_ts,updated_at_ts,poster_file,poster_store_dir) VALUES(7,'Film','history',1,1,'historical.jpg','tmdb-history'); INSERT INTO releases(source_id,guid,title,created_at_ts,entity_id,poster_file,poster_store_dir) VALUES(222,'history','history',1,7,'historical.jpg','tmdb-history')");
        var poster = Path.Combine(target.Root, "historical.jpg"); File.WriteAllBytes(poster, [1,2,3,4]);
        var sessions = new WebSessionService(target.Db, TimeProvider.System, settings);
        var context = new DefaultHttpContext(); context.Request.Scheme = "https";
        Assert.NotNull(sessions.Create(context, sessions.Generation(), false));
        var cookie = context.Response.Headers.SetCookie.ToString().Split(';')[0];
        var before = Runtime(target); var security = c.ExecuteScalar<string>("SELECT value_json FROM app_settings WHERE key='security'");
        using (var oc = origin.Db.Open())
        {
            origin.Set(oc, "general", new GeneralSettings { SyncIntervalMinutes = 123, AutoSyncEnabled = false });
            // A large regenerable CAPS cache must not consume the configuration archive's row budget.
            oc.Execute("WITH RECURSIVE ids(n) AS (SELECT 10000 UNION ALL SELECT n+1 FROM ids WHERE n<20000) INSERT INTO source_categories(source_id,cat_id,name) SELECT 135,n,'cached' FROM ids");
            // Same IGDB credentials avoid an unnecessary OAuth restart.
            oc.Execute("DELETE FROM external_provider_instances WHERE provider_key='igdb'");
        }
        c.Execute("DELETE FROM external_provider_instances WHERE provider_key='igdb'");
        var bytes = await origin.Service.ExportAsync(Password);
        Assert.StartsWith("FEEDARR-CONFIG-3\n", Encoding.UTF8.GetString(bytes));
        foreach (var secret in new[] { Password, "source-secret", "test-admin", "NO_RELEASES_SENTINEL" }) Assert.DoesNotContain(secret, Encoding.UTF8.GetString(bytes));
        var read = await origin.Service.ReadAsync(new MemoryStream(bytes), Password);
        Assert.False(read.Configuration.Settings.ContainsKey("security"));
        Assert.False(read.Configuration.Settings["ui"].ContainsKey("onboardingDone"));
        Assert.Single(read.Configuration.Categories);
        var restore = Service(target, cache);
        var preview = await restore.PreviewAsync(new MemoryStream(bytes), Password);
        Assert.Equal(before, Runtime(target)); Assert.Equal(1, preview.DisabledSources);
        var result = await restore.ApplyAsync(new MemoryStream(bytes), Password, preview.ExpectedState);
        Assert.True(result.Applied); Assert.False(result.RestartRequired);
        Assert.Equal(before, Runtime(target)); Assert.Equal(new byte[]{1,2,3,4}, File.ReadAllBytes(poster));
        Assert.Equal(security, c.ExecuteScalar<string>("SELECT value_json FROM app_settings WHERE key='security'"));
        var afterContext = new DefaultHttpContext(); afterContext.Request.Scheme = "https"; afterContext.Request.Headers.Cookie = cookie;
        Assert.NotNull(sessions.Authenticate(afterContext));
        Assert.Equal(123, settings.GetGeneral(new()).SyncIntervalMinutes);
        var instances = new ExternalProviderInstanceRepository(target.Db, settings, target.Protection, target.Registry, NullLogger<ExternalProviderInstanceRepository>.Instance);
        var resolver = new ActiveExternalProviderConfigResolver(instances, target.Registry, NullLogger<ActiveExternalProviderConfigResolver>.Instance);
        Assert.Equal("tmdb-apiKey-secret", resolver.Resolve("tmdb").Auth["apiKey"]);
        var sourceKey = c.ExecuteScalar<string>("SELECT api_key FROM sources WHERE id=135");
        Assert.Equal("source-secret", target.Protection.Unprotect(sourceKey!));
        Assert.False(origin.Protection.TryUnprotect(sourceKey!, out _));
        Assert.Equal("provider-secret", target.Protection.Unprotect(c.ExecuteScalar<string>("SELECT api_key_encrypted FROM providers WHERE enabled=1")!));
        Assert.Equal("app-secret", target.Protection.Unprotect(c.ExecuteScalar<string>("SELECT api_key_encrypted FROM arr_applications WHERE is_enabled=1")!));
        Assert.Equal(135, c.ExecuteScalar<long>("SELECT source_id FROM source_selected_categories"));
        Assert.Equal(135, c.ExecuteScalar<long>("SELECT source_id FROM source_category_mappings"));
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT enabled FROM sources WHERE id=222"));
        var exported = await target.Service.ExportAsync(Password);
        var roundTrip = await origin.Service.ReadAsync(new MemoryStream(exported), Password);
        Assert.Contains(roundTrip.Configuration.ExternalProviders, x => x["auth_json"]!.ToJsonString().Contains("tmdb-apiKey-secret"));
    }

    [Fact]
    public async Task InvalidPasswordsTamperingAndLegacyFilesNeverWrite()
    {
        using var origin = new Fixture(); using var target = new Fixture(false);
        using var cache = new MemoryCache(new MemoryCacheOptions()); var restore = Service(target, cache);
        var before = Configuration(target); var bytes = await origin.Service.ExportAsync(Password);
        Assert.NotEqual(bytes, await origin.Service.ExportAsync(Password));
        var wrong = await Assert.ThrowsAsync<ConfigurationArchiveException>(() => restore.PreviewAsync(new MemoryStream(bytes), "Wrong password long enough"));
        Assert.Equal(ConfigurationArchiveError.AuthenticationFailed, wrong.Code);
        bytes[^1] ^= 1;
        await Assert.ThrowsAsync<ConfigurationArchiveException>(() => restore.ApplyAsync(new MemoryStream(bytes), Password, "irrelevant"));
        await Assert.ThrowsAsync<ConfigurationArchiveException>(() => restore.PreviewAsync(new MemoryStream("PK old zip format"u8.ToArray()), Password));
        await Assert.ThrowsAsync<ConfigurationArchiveException>(() => origin.Service.ExportAsync(""));
        Assert.Equal(before, Configuration(target));
    }

    [Fact]
    public async Task SqlFailureCancellationAndStalePreviewRollBackEntireConfiguration()
    {
        using var origin = new Fixture(); using var target = new Fixture(false);
        using var cache = new MemoryCache(new MemoryCacheOptions()); var restore = Service(target, cache);
        var bytes = await origin.Service.ExportAsync(Password); var before = Configuration(target);
        var p = await restore.PreviewAsync(new MemoryStream(bytes), Password);
        using var c = target.Db.Open();
        c.Execute("CREATE TRIGGER fail_restore BEFORE INSERT ON sources BEGIN SELECT RAISE(ABORT,'injected SQL failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => restore.ApplyAsync(new MemoryStream(bytes), Password, p.ExpectedState));
        Assert.Equal(before, Configuration(target)); c.Execute("DROP TRIGGER fail_restore");
        using var cancellation = new CancellationTokenSource();
        restore.FaultPoint = phase => { if (phase == "before-commit") cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore.ApplyAsync(new MemoryStream(bytes), Password, p.ExpectedState, cancellation.Token));
        Assert.Equal(before, Configuration(target)); restore.FaultPoint = null;
        target.Set(c, "general", new GeneralSettings { SyncIntervalMinutes = 42 });
        var stale = await Assert.ThrowsAsync<BackupOperationException>(() => restore.ApplyAsync(new MemoryStream(bytes), Password, p.ExpectedState));
        Assert.Equal(409, stale.StatusCode);
        p = await restore.PreviewAsync(new MemoryStream(bytes), Password);
        Assert.True((await restore.ApplyAsync(new MemoryStream(bytes), Password, p.ExpectedState)).Applied);
    }

    [Fact]
    public async Task IndependentInstancesReprotectSecretsAndReportRequiredRestart()
    {
        using var origin = new Fixture(); using var target = new Fixture(false);
        using var cache = new MemoryCache(new MemoryCacheOptions()); var restore = Service(target, cache);
        var bytes = await origin.Service.ExportAsync(Password);
        var p = await restore.PreviewAsync(new MemoryStream(bytes), Password);
        Assert.True((await restore.ApplyAsync(new MemoryStream(bytes), Password, p.ExpectedState)).RestartRequired);
        p = await restore.PreviewAsync(new MemoryStream(bytes), Password);
        Assert.True((await restore.ApplyAsync(new MemoryStream(bytes), Password, p.ExpectedState)).RestartRequired);
        Assert.False((await Service(target, cache).ApplyAsync(new MemoryStream(bytes), Password, (await restore.PreviewAsync(new MemoryStream(bytes), Password)).ExpectedState)).RestartRequired);
        using var c = target.Db.Open();
        Assert.NotEqual(135, c.ExecuteScalar<long>("SELECT id FROM sources"));
        c.Execute("UPDATE sources SET api_key='ENC:broken'");
        var error = await Assert.ThrowsAsync<ConfigurationArchiveException>(() => target.Service.ExportAsync(Password));
        Assert.Equal(ConfigurationArchiveError.UnreadableSecrets, error.Code);
    }

    [Fact]
    public async Task SharedEndpointConnectionsRetainDistinctIdentities()
    {
        using var origin = new Fixture(); using var target = new Fixture();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        foreach (var f in new[] { origin, target })
        {
            using var c = f.Db.Open();
            c.Execute("INSERT INTO providers(type,name,base_url,api_key_encrypted,enabled,created_at_ts,updated_at_ts) VALUES('jackett','second','https://indexer.example',@key,1,1,1)", new { key = f.Protection.Protect("second-provider") });
            c.Execute("INSERT INTO arr_applications(type,name,base_url,api_key_encrypted,is_default,created_at_ts,updated_at_ts) VALUES('sonarr','second','http://sonarr:8989',@key,0,1,1)", new { key = f.Protection.Protect("second-application") });
        }
        var bytes = await origin.Service.ExportAsync(Password); var restore = Service(target, cache);
        var p = await restore.PreviewAsync(new MemoryStream(bytes), Password);
        await restore.ApplyAsync(new MemoryStream(bytes), Password, p.ExpectedState);
        using var targetDb = target.Db.Open();
        Assert.Equal(2, targetDb.ExecuteScalar<int>("SELECT COUNT(*) FROM providers"));
        Assert.Equal(2, targetDb.ExecuteScalar<int>("SELECT COUNT(*) FROM arr_applications"));
        Assert.Equal("provider-secret", target.Protection.Unprotect(targetDb.ExecuteScalar<string>("SELECT api_key_encrypted FROM providers WHERE id=98")!));
        Assert.Equal("second-provider", target.Protection.Unprotect(targetDb.ExecuteScalar<string>("SELECT api_key_encrypted FROM providers WHERE name='second'")!));
    }

    private static ConfigurationRestoreService Service(Fixture f, IMemoryCache cache) => new(f.Db, f.Service, f.Protection, f.Registry, new BackupExecutionCoordinator(), cache, new OutboundDestinationPolicy(new OutboundSecurityOptions()));
    private static string Configuration(Fixture f) { using var c=f.Db.Open(); using var tx=c.BeginTransaction(true); return ConfigurationRestorePlanner.Fingerprint(c,tx); }
    private static string Runtime(Fixture f)
    {
        using var c=f.Db.Open();
        return JsonSerializer.Serialize(new[]{"releases","media_entities","poster_store_refs","poster_matches","release_arr_status","media_entity_arr_status","activity_log"}.Select(t=>c.Query($"SELECT * FROM {t} ORDER BY rowid").ToArray()));
    }
}
