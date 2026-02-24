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
    public void PatchMappings_UpsertDelete_AndReadActiveIds()
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

        var active = repository.GetActiveCategoryIds(sourceId).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { 2020, 5050 }, active);

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

        var remaining = repository.GetActiveCategoryIds(sourceId).ToArray();
        Assert.Single(remaining);
        Assert.Equal(5050, remaining[0]);
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
