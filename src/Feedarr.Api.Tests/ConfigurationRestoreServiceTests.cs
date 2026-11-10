using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Backup.Configuration;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ArchiveFixture = Feedarr.Api.Tests.ConfigurationArchiveServiceTests.Fixture;

namespace Feedarr.Api.Tests;

public sealed class ConfigurationRestoreServiceTests
{
    private const string Password = "A transactionally restored configuration!";

    [Fact]
    public async Task FreshRestoreIsProtectedAtomicPortableAndRemapsIdentifiers()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target();
        var before = destination.ActiveConfiguration();
        var preview = await destination.Prepare(origin);
        Assert.True(preview.FreshInstallation); Assert.True(preview.CanCompleteSetup);
        Assert.Equal(before, destination.ActiveConfiguration());
        var planFile = Path.Combine(destination.Data.Root, "configuration-restore", "plan.bin");
        foreach (var secret in new[] { "source-secret", "provider-secret", "app-secret", Password })
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(planFile)));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(preview));
        await destination.Restore.ConfirmAsync(preview.OperationId, destination.Caller, confirmSecurityChange: true);
        Assert.Null(destination.Coordinator.TryEnterSyncActivity("blocked-before-restart"));
        destination.Restore.ApplyAtStartup();
        using var c = destination.Data.Db.Open();
        var source = c.QuerySingle<(long Id, long ProviderId, string Key)>("SELECT id,provider_id,api_key FROM sources");
        Assert.NotEqual(135, source.Id); Assert.NotEqual(98, source.ProviderId);
        Assert.Equal("source-secret", destination.Data.Protection.Unprotect(source.Key));
        Assert.False(origin.Protection.TryUnprotect(source.Key, out _));
        Assert.Equal(source.Id, c.ExecuteScalar<long>("SELECT source_id FROM source_selected_categories"));
        Assert.Equal(source.Id, c.ExecuteScalar<long>("SELECT source_id FROM source_category_mappings"));
        Assert.Equal(source.Id.ToString(), destination.Settings.GetUi(new UiSettings()).DefaultFilterSourceId);
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT count(*) FROM releases"));
        Assert.True(destination.Setup.IsSetupCompleted()); Assert.False(File.Exists(planFile));
        Assert.Equal("pending", c.ExecuteScalar<string>("SELECT initial_sync_state FROM configuration_restore_operations"));
        using var lease = destination.Coordinator.TryEnterSyncActivity("after-apply"); Assert.NotNull(lease);
        var exported = await destination.Data.Service.ExportAsync(Password);
        var reread = await origin.Service.ReadAsync(new MemoryStream(exported), Password);
        Assert.True(reread.Preview.IsComplete);
        foreach (var e in origin.Registry.List()) Assert.Contains(reread.Configuration.ExternalProviders, x => x["provider_key"]!.GetValue<string>() == e.ProviderKey);
    }

    [Fact]
    public async Task ExistingLibraryAndHistoricalSourcesRemainByteForByteUnchanged()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target(seed: true);
        destination.AddHistoricalLibrary();
        var before = destination.Library(); var media = destination.PosterBytes();
        var preview = await destination.Prepare(origin);
        Assert.Equal(1, preview.MatchedSources); Assert.Equal(1, preview.DisabledHistoricalSources);
        await destination.Restore.ConfirmAsync(preview.OperationId, destination.Caller, true);
        destination.Restore.ApplyAtStartup();
        Assert.Equal(before, destination.Library()); Assert.Equal(media, destination.PosterBytes());
        using var c = destination.Data.Db.Open();
        Assert.Equal(135, c.ExecuteScalar<long>("SELECT id FROM sources WHERE enabled=1"));
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT enabled FROM sources WHERE id=222"));
        Assert.Equal(222, c.ExecuteScalar<long>("SELECT source_id FROM releases WHERE guid='history'"));
        Assert.Equal("held", c.ExecuteScalar<string>("SELECT initial_sync_state FROM configuration_restore_operations"));
        var calls = 0; Assert.True(await destination.Restore.RunInitialSyncCycleAsync((_, _) => { calls++; return Task.FromResult(true); }, default));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("after-providers")]
    [InlineData("after-configuration")]
    [InlineData("before-commit")]
    public async Task FailureBeforeCommitRollsBackEverythingAndCanResume(string point)
    {
        using var origin = new ArchiveFixture(); using var destination = new Target(seed: true);
        destination.AddHistoricalLibrary(); var before = destination.ActiveConfiguration(); var library = destination.Library();
        var p = await destination.Prepare(origin); await destination.Restore.ConfirmAsync(p.OperationId, destination.Caller, true);
        destination.Restore.FaultPoint = step => { if (step == point) throw new InjectedCrash(); };
        Assert.Throws<InjectedCrash>(() => destination.Restore.ApplyAtStartup());
        Assert.Equal(before, destination.ActiveConfiguration()); Assert.Equal(library, destination.Library());
        using (var c = destination.Data.Db.Open()) Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM configuration_restore_operations"));
        destination.RestartService(); destination.Restore.ApplyAtStartup();
        Assert.Equal(library, destination.Library());
        using (var c = destination.Data.Db.Open()) Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM configuration_restore_operations"));
    }

    [Fact]
    public async Task CrashAfterCommitReplaysOnlyFinalizationAndNeverAppliesTwice()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target();
        var p = await destination.Prepare(origin); await destination.Restore.ConfirmAsync(p.OperationId, destination.Caller, true);
        destination.Restore.FaultPoint = step => { if (step == "after-commit") throw new InjectedCrash(); };
        Assert.Throws<InjectedCrash>(() => destination.Restore.ApplyAtStartup());
        var after = destination.ActiveConfiguration();
        destination.RestartService(); destination.Restore.ApplyAtStartup(); destination.Restore.ApplyAtStartup();
        Assert.Equal(after, destination.ActiveConfiguration());
        using var c = destination.Data.Db.Open(); Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM configuration_restore_operations"));
    }

    [Fact]
    public async Task UnconfirmedOrStalePlanCannotChangeConfiguration()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target();
        var p = await destination.Prepare(origin); destination.Restore.ApplyAtStartup();
        using (var c = destination.Data.Db.Open()) Assert.Empty(c.Query("SELECT id FROM sources"));
        using (var c = destination.Data.Db.Open()) destination.Data.Set(c, "ui", new UiSettings { Theme = "dark" });
        await Assert.ThrowsAsync<BackupOperationException>(() => destination.Restore.ConfirmAsync(p.OperationId, destination.Caller, true));
        await destination.Restore.CancelAsync(p.OperationId, destination.Caller);
        Assert.Null(destination.Store.Read());
    }

    [Fact]
    public async Task AmbiguousSourceIdentityIsRejectedWithoutStagingOrMerging()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target(seed: true);
        using (var c = destination.Data.Db.Open()) c.Execute("INSERT INTO sources(name,torznab_url,api_key,created_at_ts,updated_at_ts) VALUES('ambiguous','https://indexer.example/api/','',1,1)");
        var before = destination.ActiveConfiguration();
        var error = await Assert.ThrowsAsync<BackupOperationException>(() => destination.Prepare(origin));
        Assert.Equal(409, error.StatusCode); Assert.Equal(before, destination.ActiveConfiguration()); Assert.Null(destination.Store.Read());
    }

    [Fact]
    public async Task DifferentSourceEndpointGetsNewIdInsteadOfReassigningHistory()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target(seed: true);
        using (var c = destination.Data.Db.Open()) c.Execute("UPDATE sources SET torznab_url='https://other.example/api' WHERE id=135");
        var library = destination.Library(); var p = await destination.Prepare(origin);
        Assert.Equal(1, p.NewSources); await destination.Restore.ConfirmAsync(p.OperationId, destination.Caller, true); destination.Restore.ApplyAtStartup();
        using var conn = destination.Data.Db.Open(); Assert.True(conn.ExecuteScalar<long>("SELECT id FROM sources WHERE enabled=1") > 135);
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT enabled FROM sources WHERE id=135")); Assert.Equal(library, destination.Library());
    }

    [Fact]
    public async Task IncompleteConfigurationLeavesWizardIncompleteAndSyncHeld()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target();
        using (var c = origin.Db.Open()) c.Execute("UPDATE sources SET api_key='';UPDATE providers SET api_key_encrypted=''");
        var p = await destination.Prepare(origin); Assert.False(p.CanCompleteSetup);
        await destination.Restore.ConfirmAsync(p.OperationId, destination.Caller, true); destination.Restore.ApplyAtStartup();
        Assert.False(destination.Setup.IsSetupCompleted());
        Assert.True(await destination.Restore.RunInitialSyncCycleAsync((_, _) => throw new InvalidOperationException("must not run"), default));
        await Assert.ThrowsAsync<BackupOperationException>(() => destination.Restore.ConfirmInitialSyncAsync(p.OperationId, destination.Caller));
    }

    [Fact]
    public async Task StricterRetentionNeedsSeparateConfirmationAndNeverPurgesOnApply()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target(seed: true);
        using (var c = destination.Data.Db.Open()) destination.Data.Set(c, "general", new GeneralSettings { RssLimitPerCategory = 100, RssLimitGlobalPerSource = 500 });
        var before = destination.Library(); var p = await destination.Prepare(origin); Assert.True(p.StricterRetention);
        await destination.Restore.ConfirmAsync(p.OperationId, destination.Caller, true); destination.Restore.ApplyAtStartup();
        Assert.Equal(100, destination.Settings.GetGeneral(new GeneralSettings()).RssLimitPerCategory); Assert.Equal(before, destination.Library());
        await Assert.ThrowsAsync<BackupOperationException>(() => destination.Restore.ConfirmInitialSyncAsync(p.OperationId, destination.Caller));
        await destination.Restore.ConfirmRetentionAsync(p.OperationId, destination.Caller);
        Assert.Equal(50, destination.Settings.GetGeneral(new GeneralSettings()).RssLimitPerCategory); Assert.Equal(before, destination.Library());
        await destination.Restore.ConfirmInitialSyncAsync(p.OperationId, destination.Caller);
    }

    [Fact]
    public async Task InitialSyncIsSingleFlightAndCompletedProgressSurvivesRestart()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target();
        var p = await destination.Prepare(origin); await destination.Restore.ConfirmAsync(p.OperationId, destination.Caller, true); destination.Restore.ApplyAtStartup();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var first = destination.Restore.RunInitialSyncCycleAsync(async (_, ct) => { Interlocked.Increment(ref calls); entered.SetResult(); await release.Task.WaitAsync(ct); return true; }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await destination.Restore.RunInitialSyncCycleAsync((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(true); }, default));
        release.SetResult(); Assert.True(await first); Assert.Equal(1, calls);
        destination.RestartService(); Assert.False(await destination.Restore.RunInitialSyncCycleAsync((_, _) => throw new InjectedCrash(), default));
    }

    [Fact]
    public async Task InitialSyncFailuresBackOffWithoutPersistingSensitiveErrors()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target();
        var p = await destination.Prepare(origin); await destination.Restore.ConfirmAsync(p.OperationId, destination.Caller, true); destination.Restore.ApplyAtStartup();
        var calls = 0; Task<bool> Fail(long _, CancellationToken token) { calls++; throw new IOException("https://secret.example/?apikey=private"); }
        Assert.True(await destination.Restore.RunInitialSyncCycleAsync(Fail, default));
        destination.RestartService(); Assert.True(await destination.Restore.RunInitialSyncCycleAsync(Fail, default)); Assert.Equal(1, calls);
        using var c = destination.Data.Db.Open(); Assert.DoesNotContain("private", JsonSerializer.Serialize(c.Query("SELECT * FROM configuration_restore_operations")));
        c.Execute("UPDATE configuration_restore_operations SET next_attempt_ts=0");
        Assert.True(await destination.Restore.RunInitialSyncCycleAsync((_, _) => Task.FromResult(true), default));
        Assert.Equal("done", c.ExecuteScalar<string>("SELECT initial_sync_state FROM configuration_restore_operations"));
    }

    [Fact]
    public async Task LegacyAndV2RestoresAreMutuallyExclusive()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target();
        destination.PrepareLegacy();
        await Assert.ThrowsAsync<BackupOperationException>(() => destination.Prepare(origin));
        destination.Legacy.Cancel(destination.Legacy.GetStatus().Id!);
        var p = await destination.Prepare(origin);
        Assert.Throws<BackupOperationException>(() => destination.Legacy.EnsureCanPrepare(null));
        File.WriteAllText(Path.Combine(destination.Data.Root, "restore-state", "pending.json"), "{}");
        Assert.Throws<BackupOperationException>(() => ConfigurationRestorePlanStore.EnsureNoLegacyConflict(destination.Data.Root));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PassphraseNeverGrantsAdministrativeOrBootstrapAuthorization(bool middleware, bool remote)
    {
        using var origin = new ArchiveFixture(); using var destination = new Target();
        var caller = new DefaultHttpContext(); caller.Items[BasicAuthMiddleware.AuthPassedKey] = middleware;
        caller.Connection.RemoteIpAddress = remote ? IPAddress.Parse("8.8.8.8") : IPAddress.Loopback;
        if (remote) caller.Items[BasicAuthMiddleware.AuthPassedKey] = true;
        if (!middleware || remote) caller.Request.Headers[RequestForgeryProtection.RequestHeaderName] = "1";
        await Assert.ThrowsAsync<BackupOperationException>(() => destination.Restore.PrepareAsync(new MemoryStream(origin.Service.ExportAsync(Password).Result), Password, caller));
    }

    [Fact]
    public async Task SecurityChangeAndConfirmationRequireExplicitConsent()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target();
        using (var c = origin.Db.Open()) origin.Set(c, "security", new SecuritySettings { AuthMode = "open" });
        var p = await destination.Prepare(origin); Assert.True(p.SecurityChangeRequiresConfirmation);
        await Assert.ThrowsAsync<BackupOperationException>(() => destination.Restore.ConfirmAsync(p.OperationId, destination.Caller));
        Assert.False(destination.Store.Read()!.Confirmed);
    }

    [Fact]
    public async Task DestinationSsrfPolicyAndTamperedLocalPlansFailClosed()
    {
        using var origin = new ArchiveFixture(); using var destination = new Target();
        using (var c = origin.Db.Open()) c.Execute("UPDATE providers SET base_url='http://169.254.169.254'");
        await Assert.ThrowsAsync<BackupOperationException>(() => destination.Prepare(origin));
        using (var c = origin.Db.Open()) c.Execute("UPDATE providers SET base_url='https://indexer.example'");
        var p = await destination.Prepare(origin);
        var file = Path.Combine(destination.Data.Root, "configuration-restore", "plan.bin"); var data = File.ReadAllBytes(file); data[^1] ^= 1; File.WriteAllBytes(file, data);
        await Assert.ThrowsAsync<BackupOperationException>(() => destination.Restore.ConfirmAsync(p.OperationId, destination.Caller, true));
        Assert.Throws<BackupOperationException>(() => destination.Restore.ApplyAtStartup());
    }

    [Fact]
    public async Task ConfigurationChangedAfterConfirmationRollsBackWithoutTouchingLibrary()
    {
        using var origin = new ArchiveFixture(); using var target = new Target(seed: true);
        var p = await target.Prepare(origin); await target.Restore.ConfirmAsync(p.OperationId, target.Caller, true);
        using (var c = target.Data.Db.Open()) c.Execute("UPDATE sources SET name='changed' WHERE id=135");
        var before = target.ActiveConfiguration(); var library = target.Library();
        Assert.Throws<BackupOperationException>(() => target.Restore.ApplyAtStartup());
        Assert.Equal(before, target.ActiveConfiguration()); Assert.Equal(library, target.Library());
    }

    [Fact]
    public async Task UnreadableDestinationCredentialCannotBeCommitted()
    {
        using var origin = new ArchiveFixture(); using var target = new Target(); using var foreign = new ArchiveFixture(seed: false);
        var p = await target.Prepare(origin); await target.Restore.ConfirmAsync(p.OperationId, target.Caller, true);
        var plan = target.Store.Read()!;
        plan.Configuration.Sources[0]["api_key"] = foreign.Protection.Protect("foreign-credential"); target.Store.Write(plan);
        var before = target.ActiveConfiguration();
        Assert.Throws<BackupOperationException>(() => target.Restore.ApplyAtStartup());
        Assert.Equal(before, target.ActiveConfiguration());
    }

    [Fact]
    public async Task SecondPreparationIsRejectedAndPendingPlanCanBeCancelled()
    {
        using var origin = new ArchiveFixture(); using var target = new Target();
        var p = await target.Prepare(origin);
        await Assert.ThrowsAsync<BackupOperationException>(() => target.Prepare(origin));
        Assert.Equal(p.OperationId, target.Store.Read()!.Id);
        await target.Restore.CancelAsync(p.OperationId, target.Caller); Assert.Null(target.Store.Read());
        Assert.NotNull(await target.Prepare(origin));
    }

    [Fact]
    public async Task CancellationAndPerSourceProgressResumeWithoutRepeatingCompletedSources()
    {
        using var origin = new ArchiveFixture(); using var target = new Target();
        using (var c = origin.Db.Open()) c.Execute("INSERT INTO sources(name,torznab_url,api_key,provider_id,created_at_ts,updated_at_ts) VALUES('second','https://indexer.example/second','',98,1,1)");
        var p = await target.Prepare(origin); await target.Restore.ConfirmAsync(p.OperationId, target.Caller, true); target.Restore.ApplyAtStartup();
        var calls = new List<long>(); using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.Restore.RunInitialSyncCycleAsync((id, _) => { calls.Add(id); cancel.Cancel(); return Task.FromResult(true); }, cancel.Token));
        target.RestartService(); using (var c = target.Data.Db.Open()) c.Execute("UPDATE configuration_restore_operations SET next_attempt_ts=0");
        await target.Restore.RunInitialSyncCycleAsync((id, _) => { calls.Add(id); return Task.FromResult(true); }, default);
        Assert.Equal(2, calls.Count); Assert.Equal(2, calls.Distinct().Count());
    }

    [Fact]
    public async Task StartupInvalidatesSecuritySetupAndBootstrapCaches()
    {
        using var origin = new ArchiveFixture(); using var target = new Target();
        Assert.False(target.Setup.IsSetupCompleted()); var token = target.Tokens.IssueToken();
        target.Cache.Set(SecuritySettingsCache.CacheKey, new SecuritySettings { AuthMode = "open" });
        var p = await target.Prepare(origin); await target.Restore.ConfirmAsync(p.OperationId, target.Caller, true); target.Restore.ApplyAtStartup();
        Assert.True(target.Setup.IsSetupCompleted()); Assert.False(target.Tokens.IsValid(token));
        Assert.False(target.Cache.TryGetValue(SecuritySettingsCache.CacheKey, out _));
    }

    [Fact]
    public async Task MetadataOrMediaWithoutReleasesIsStillProtectedAsExistingLibrary()
    {
        using var origin = new ArchiveFixture(); using var target = new Target();
        Directory.CreateDirectory(Path.Combine(target.Data.Root, "posters"));
        File.WriteAllBytes(Path.Combine(target.Data.Root, "posters", "existing.jpg"), [9, 8, 7]);
        var p = await target.Prepare(origin); Assert.True(p.HasLibrary); Assert.False(p.FreshInstallation);
        await target.Restore.ConfirmAsync(p.OperationId, target.Caller, true); target.Restore.ApplyAtStartup();
        using var c = target.Data.Db.Open(); Assert.Equal("held", c.ExecuteScalar<string>("SELECT initial_sync_state FROM configuration_restore_operations"));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(Path.Combine(target.Data.Root, "posters", "existing.jpg")));
    }

    [Fact]
    public async Task NewlyAppearingLibraryAfterPreparationBlocksApplication()
    {
        using var origin = new ArchiveFixture(); using var target = new Target();
        var p = await target.Prepare(origin); await target.Restore.ConfirmAsync(p.OperationId, target.Caller, true);
        Directory.CreateDirectory(Path.Combine(target.Data.Root, "posters")); File.WriteAllBytes(Path.Combine(target.Data.Root, "posters", "new.jpg"), [1]);
        Assert.Throws<BackupOperationException>(() => target.Restore.ApplyAtStartup());
        using var c = target.Data.Db.Open(); Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM sources"));
    }

    [Fact]
    public async Task ConcurrentPreparationsCannotPublishTwoPlans()
    {
        using var origin = new ArchiveFixture(); using var target = new Target();
        var bytes = await origin.Service.ExportAsync(Password);
        using var input = new PausedInput(bytes);
        var first = target.Restore.PrepareAsync(input, Password, target.Caller);
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = target.Restore.PrepareAsync(new MemoryStream(bytes), Password, target.Caller);
        Assert.False(second.IsCompleted); input.Release.SetResult();
        var p = await first; await Assert.ThrowsAsync<BackupOperationException>(() => second);
        Assert.Equal(p.OperationId, target.Restore.GetPendingPreview(target.Caller)!.OperationId);
    }

    [Fact]
    public async Task CancelledPreparationDoesNotPersistAPlan()
    {
        using var origin = new ArchiveFixture(); using var target = new Target();
        var before = target.ActiveConfiguration(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.Restore.PrepareAsync(new MemoryStream(), Password, target.Caller, cancel.Token));
        Assert.Null(target.Store.Read()); Assert.Equal(before, target.ActiveConfiguration());
    }

    [Fact]
    public async Task HeldInitialSyncCanBeCancelledWithoutChangingLibrary()
    {
        using var origin = new ArchiveFixture(); using var target = new Target(seed: true);
        var before = target.Library(); var p = await target.Prepare(origin);
        await target.Restore.ConfirmAsync(p.OperationId, target.Caller, true); target.Restore.ApplyAtStartup();
        await target.Restore.CancelInitialSyncAsync(p.OperationId, target.Caller);
        Assert.False(await target.Restore.RunInitialSyncCycleAsync((_, _) => throw new InjectedCrash(), default));
        Assert.Equal(before, target.Library()); Assert.NotNull(await target.Prepare(origin));
    }

    [Fact]
    public async Task UnsupportedRetentionBoundsAreRejectedInsteadOfSilentlyClamped()
    {
        using var origin = new ArchiveFixture(); using var target = new Target(seed: true);
        using (var c = origin.Db.Open()) origin.Set(c, "general", new GeneralSettings { RssLimitPerCategory = 0 });
        var before = target.ActiveConfiguration();
        await Assert.ThrowsAsync<BackupOperationException>(() => target.Prepare(origin));
        Assert.Equal(before, target.ActiveConfiguration()); Assert.Null(target.Store.Read());
    }

    private sealed class PausedInput(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return await base.ReadAsync(buffer, ct); }
    }

    private sealed class InjectedCrash : Exception;
    private sealed class Target : IDisposable
    {
        public ArchiveFixture Data { get; }
        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());
        public SettingsRepository Settings { get; }
        public SetupStateService Setup { get; }
        public BootstrapTokenService Tokens { get; } = new();
        public ConfigurationRestorePlanStore Store { get; private set; } = null!;
        public BackupExecutionCoordinator Coordinator { get; private set; } = null!;
        public ConfigurationRestoreService Restore { get; private set; } = null!;
        public PendingRestoreStore Legacy { get; }
        public HttpContext Caller { get; }
        public Target(bool seed = false)
        {
            Data = new ArchiveFixture(seed); Settings = new SettingsRepository(Data.Db, Data.Protection, NullLogger<SettingsRepository>.Instance);
            Setup = new SetupStateService(Settings, Cache); Legacy = new PendingRestoreStore(Data.Root, "feedarr.db");
            Caller = new DefaultHttpContext(); Caller.Connection.RemoteIpAddress = IPAddress.Loopback; Caller.Items[BasicAuthMiddleware.AuthPassedKey] = true;
            Caller.Request.Headers[RequestForgeryProtection.RequestHeaderName] = "1";
            RestartService();
        }
        public void RestartService()
        {
            Store = new ConfigurationRestorePlanStore(Data.Db, Data.DataProtection); Coordinator = new BackupExecutionCoordinator(configurationRestore: Store);
            Restore = new ConfigurationRestoreService(Data.Db, Data.Service, Store, Data.Protection, Data.Registry, Coordinator, Legacy, Setup, Tokens, Cache,
                new ConfigurationBuilder().Build(), new OutboundDestinationPolicy(new OutboundSecurityOptions()));
        }
        public async Task<ConfigurationRestorePreview> Prepare(ArchiveFixture source) => await Restore.PrepareAsync(new MemoryStream(await source.Service.ExportAsync(Password)), Password, Caller);
        public string ActiveConfiguration()
        {
            using var c = Data.Db.Open(); using var tx = c.BeginTransaction(deferred: true); return ConfigurationRestorePlanner.Fingerprint(c, tx);
        }
        public string Library()
        {
            using var c = Data.Db.Open(); return JsonSerializer.Serialize(new[] { "releases", "media_entities", "poster_store_refs", "poster_matches", "release_arr_status", "media_entity_arr_status" }.Select(t => c.Query($"SELECT * FROM {t} ORDER BY rowid").ToArray()).ToArray());
        }
        public byte[] PosterBytes() => File.ReadAllBytes(Path.Combine(Data.Root, "posters", "historical.jpg"));
        public void AddHistoricalLibrary()
        {
            using var c = Data.Db.Open();
            c.Execute("INSERT INTO sources(id,name,torznab_url,api_key,created_at_ts,updated_at_ts) VALUES(222,'history','https://history.example/api','',1,1)");
            c.Execute("INSERT INTO media_entities(id,unified_category,title_clean,created_at_ts,updated_at_ts,poster_file,poster_store_dir) VALUES(7,'Film','history',1,1,'historical.jpg','tmdb-history')");
            c.Execute("INSERT INTO releases(source_id,guid,title,created_at_ts,entity_id,poster_file,poster_store_dir) VALUES(222,'history','history',1,7,'historical.jpg','tmdb-history')");
            Directory.CreateDirectory(Path.Combine(Data.Root, "posters")); File.WriteAllBytes(Path.Combine(Data.Root, "posters", "historical.jpg"), [1, 2, 3, 4]);
        }
        public void PrepareLegacy()
        {
            var work = Legacy.NewWorkDirectory();
            using (var source = Data.Db.Open()) using (var dest = new SqliteConnection($"Data Source={Path.Combine(work, "incoming.db")}"))
            { dest.Open(); source.BackupDatabase(dest); }
            Legacy.Publish(work, "legacy.zip", null, default);
        }
        public void Dispose() { Cache.Dispose(); Data.Dispose(); }
    }
}
