using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ExternalProviderLimiterTests
{
    [Fact]
    public async Task RunAsync_TmdbLimitTwo_NeverExceedsTwoConcurrentActions()
    {
        using var ctx = new LimiterContext();
        ctx.Settings.SaveMaintenance(new MaintenanceSettings
        {
            ProviderRateLimitMode = "manual",
            ProviderConcurrencyManual = new ProviderConcurrencyManualSettings
            {
                Tmdb = 2,
                Igdb = 1,
                Fanart = 1,
                Tvmaze = 1,
                Others = 1
            }
        });

        var limiter = new ExternalProviderLimiter(ctx.Settings, NullLogger<ExternalProviderLimiter>.Instance);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var maxInFlight = 0;

        var tasks = Enumerable.Range(0, 4).Select(_ => limiter.RunAsync(ProviderKind.Tmdb, async innerCt =>
        {
            var current = Interlocked.Increment(ref inFlight);
            UpdateMax(ref maxInFlight, current);
            await release.Task.WaitAsync(innerCt);
            Interlocked.Decrement(ref inFlight);
            return current;
        }, CancellationToken.None)).ToArray();

        await Task.Delay(75);
        Assert.True(maxInFlight <= 2, $"Observed max concurrency {maxInFlight}");

        release.TrySetResult(true);
        await Task.WhenAll(tasks);
        Assert.True(maxInFlight <= 2, $"Observed max concurrency {maxInFlight}");
    }

    [Fact]
    public async Task RunAsync_IgdbLimitOne_SerializesActions()
    {
        using var ctx = new LimiterContext();
        ctx.Settings.SaveMaintenance(new MaintenanceSettings
        {
            ProviderRateLimitMode = "manual",
            ProviderConcurrencyManual = new ProviderConcurrencyManualSettings
            {
                Tmdb = 2,
                Igdb = 1,
                Fanart = 1,
                Tvmaze = 1,
                Others = 1
            }
        });

        var limiter = new ExternalProviderLimiter(ctx.Settings, NullLogger<ExternalProviderLimiter>.Instance);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var maxInFlight = 0;

        var tasks = Enumerable.Range(0, 3).Select(_ => limiter.RunAsync(ProviderKind.Igdb, async innerCt =>
        {
            var current = Interlocked.Increment(ref inFlight);
            UpdateMax(ref maxInFlight, current);
            await release.Task.WaitAsync(innerCt);
            Interlocked.Decrement(ref inFlight);
        }, CancellationToken.None)).ToArray();

        await Task.Delay(75);
        Assert.Equal(1, maxInFlight);

        release.TrySetResult(true);
        await Task.WhenAll(tasks);
        Assert.Equal(1, maxInFlight);
    }

    private static void UpdateMax(ref int target, int candidate)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref target);
            if (candidate <= snapshot)
                return;

            if (Interlocked.CompareExchange(ref target, candidate, snapshot) == snapshot)
                return;
        }
    }

    private sealed class LimiterContext : IDisposable
    {
        private readonly TestWorkspace _workspace;

        public LimiterContext()
        {
            _workspace = new TestWorkspace();
            var options = OptionsFactory.Create(new Feedarr.Api.Options.AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            var db = new Db(options);
            new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
            Settings = new SettingsRepository(db, new PassthroughProtectionService(), NullLogger<SettingsRepository>.Instance);
        }

        public SettingsRepository Settings { get; }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string? plainText) { plainText = protectedText; return true; }
        public bool IsProtected(string value) => false;
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-provider-limiter-tests", Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(RootDir, "data");
            Directory.CreateDirectory(DataDir);
        }

        public string RootDir { get; }
        public string DataDir { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, true);
            }
            catch
            {
            }
        }
    }
}
