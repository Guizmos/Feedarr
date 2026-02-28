using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Dtos.Providers;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ExternalProvidersBootstrapServiceTests
{
    [Fact]
    public void GetExternalProviders_IsReadOnly()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var settings = new SettingsRepository(db, new PassthroughProtectionService(), NullLogger<SettingsRepository>.Instance);
        var registry = new ExternalProviderRegistry();
        var repository = new ExternalProviderInstanceRepository(
            db,
            settings,
            new PassthroughProtectionService(),
            registry,
            NullLogger<ExternalProviderInstanceRepository>.Instance);
        var controller = new ExternalProvidersController(registry, repository, null!);

        var before = CountInstances(db);
        var result = controller.Get();
        var after = CountInstances(db);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Bootstrap_IsIdempotent()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var settings = new SettingsRepository(db, new PassthroughProtectionService(), NullLogger<SettingsRepository>.Instance);
        settings.SaveExternalPartial(new ExternalSettings
        {
            TmdbApiKey = "tmdb-secret",
            TmdbEnabled = true
        });

        var repository = new ExternalProviderInstanceRepository(
            db,
            settings,
            new PassthroughProtectionService(),
            new ExternalProviderRegistry(),
            NullLogger<ExternalProviderInstanceRepository>.Instance);
        var service = new ExternalProvidersBootstrapService(repository, NullLogger<ExternalProvidersBootstrapService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var firstCount = CountInstances(db);

        await service.StartAsync(CancellationToken.None);
        var secondCount = CountInstances(db);

        Assert.True(firstCount > 0);
        Assert.Equal(firstCount, secondCount);
    }

    [Fact]
    public void ActiveProviderQuery_ReturnsEnabledInstance_AndResolverDoesNotUseList()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var protection = new PassthroughProtectionService();
        var settings = new SettingsRepository(db, protection, NullLogger<SettingsRepository>.Instance);
        var registry = new ExternalProviderRegistry();
        var repository = new ExternalProviderInstanceRepository(
            db,
            settings,
            protection,
            registry,
            NullLogger<ExternalProviderInstanceRepository>.Instance);

        var enabled = repository.Create(new ExternalProviderCreateDto
        {
            ProviderKey = ExternalProviderKeys.Tmdb,
            Enabled = true,
            Auth = new Dictionary<string, string?> { ["apiKey"] = "enabled-secret" }
        });
        repository.Create(new ExternalProviderCreateDto
        {
            ProviderKey = ExternalProviderKeys.Tmdb,
            Enabled = false,
            Auth = new Dictionary<string, string?> { ["apiKey"] = "disabled-secret" }
        });

        var active = repository.GetActiveByProviderKeyWithSecrets(ExternalProviderKeys.Tmdb);
        Assert.NotNull(active);
        Assert.Equal(enabled.InstanceId, active!.InstanceId);
        Assert.Equal("enabled-secret", active.Auth["apiKey"]);

        var resolver = new ActiveExternalProviderConfigResolver(
            repository,
            registry,
            NullLogger<ActiveExternalProviderConfigResolver>.Instance);
        var resolved = resolver.Resolve(ExternalProviderKeys.Tmdb);
        Assert.True(resolved.Enabled);
        Assert.Equal("enabled-secret", resolved.Auth["apiKey"]);

        var repoRoot = FindRepositoryRoot();
        var resolverSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "Feedarr.Api",
            "Services",
            "ExternalProviders",
            "ActiveExternalProviderConfigResolver.cs"));
        Assert.DoesNotContain(".List()", resolverSource, StringComparison.Ordinal);
        Assert.Contains("GetActiveByProviderKeyWithSecrets", resolverSource, StringComparison.Ordinal);
    }

    private static int CountInstances(Db db)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(1) FROM external_provider_instances;");
    }

    private static Db CreateDb(TestWorkspace workspace)
        => new(OptionsFactory.Create(new AppOptions { DataDir = workspace.DataDir, DbFileName = "feedarr.db" }));

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(dir); i++)
        {
            var candidate = Path.Combine(dir, "src", "Feedarr.Api", "Services", "ExternalProviders", "ActiveExternalProviderConfigResolver.cs");
            if (File.Exists(candidate))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
        public bool TryUnprotect(string protectedText, out string? plainText)
        {
            plainText = protectedText;
            return true;
        }

        public bool IsProtected(string value) => false;
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-tests", Guid.NewGuid().ToString("N"));
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
