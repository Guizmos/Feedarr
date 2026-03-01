using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class SourceCategoryMappingsRepositoryTests
{
    [Fact]
    public void PatchMappings_UpsertDelete_AndReadMappingMap()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var repository = new SourceRepository(db, new PassthroughProtectionService());
        var sourceId = repository.Create("Test source", "http://localhost:9117/api", "key", "query");

        var changed = repository.PatchCategoryMappings(
            sourceId,
            new[]
            {
                new SourceRepository.SourceCategoryMappingPatch { CatId = 2020, GroupKey = "films" },
                new SourceRepository.SourceCategoryMappingPatch { CatId = 5050, GroupKey = "series" }
            });

        Assert.True(changed >= 2);

        var map = repository.GetCategoryMappingMap(sourceId);
        Assert.Equal("films", map[2020].key);
        Assert.Equal("Films", map[2020].label);
        Assert.Equal("series", map[5050].key);
        Assert.Equal("Série TV", map[5050].label);

        repository.PatchCategoryMappings(
            sourceId,
            new[]
            {
                    new SourceRepository.SourceCategoryMappingPatch { CatId = 2020, GroupKey = null }
            });

        var remaining = repository.GetCategoryMappingMap(sourceId).Keys.OrderBy(id => id).ToArray();
        Assert.Single(remaining);
        Assert.Equal(5050, remaining[0]);
    }

    [Fact]
    public void PatchMappings_AliasKeys_AreStoredCanonical()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var repository = new SourceRepository(db, new PassthroughProtectionService());
        var sourceId = repository.Create("Alias source", "http://localhost:9117/api", "key", "query");

        repository.PatchCategoryMappings(
            sourceId,
            new[]
            {
                new SourceRepository.SourceCategoryMappingPatch { CatId = 2020, GroupKey = "film" },
                new SourceRepository.SourceCategoryMappingPatch { CatId = 5050, GroupKey = "shows" },
                new SourceRepository.SourceCategoryMappingPatch { CatId = 4050, GroupKey = "game" }
            });

        var map = repository.GetCategoryMappingMap(sourceId);
        Assert.Equal("films", map[2020].key);
        Assert.Equal("emissions", map[5050].key);
        Assert.Equal("games", map[4050].key);
        Assert.Equal("Jeux PC", map[4050].label);
    }

    [Fact]
    public void PatchMappings_InvalidKey_Throws()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var repository = new SourceRepository(db, new PassthroughProtectionService());
        var sourceId = repository.Create("Invalid source", "http://localhost:9117/api", "key", "query");

        var ex = Assert.Throws<ArgumentException>(() =>
            repository.PatchCategoryMappings(
                sourceId,
                new[]
                {
                    new SourceRepository.SourceCategoryMappingPatch { CatId = 2020, GroupKey = "other" }
                }));

        Assert.Contains("Invalid category group key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplaceSelectedCategoryIds_IsStrictReplace()
    {
        using var workspace = new TestWorkspace();
        var db = CreateDb(workspace);
        new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();

        var repository = new SourceRepository(db, new PassthroughProtectionService());
        var sourceId = repository.Create("Selected source", "http://localhost:9117/api", "key", "query");

        repository.ReplaceSelectedCategoryIds(sourceId, new[] { 2000, 5000, 7000 });
        repository.ReplaceSelectedCategoryIds(sourceId, new[] { 2000, 5000 });

        var selected = repository.GetSelectedCategoryIds(sourceId).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { 2000, 5000 }, selected);
    }

    private static Db CreateDb(TestWorkspace workspace)
    {
        var options = OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        });
        return new Db(options);
    }

    private sealed class PassthroughProtectionService : IApiKeyProtectionService
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;

        public bool TryUnprotect(string protectedText, out string plainText)
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
