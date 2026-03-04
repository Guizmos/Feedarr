using Feedarr.Api.Data;
using Feedarr.Api.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class DbTests
{
    [Fact]
    public void GetOptionsSnapshot_UsesDefaultCacheAndPooling()
    {
        using var workspace = new TestWorkspace();
        var db = new Db(OptionsFactory.Create(new AppOptions
        {
            DataDir = workspace.DataDir,
            DbFileName = "feedarr.db"
        }));

        db.EnsureWalMode();
        var snapshot = db.GetOptionsSnapshot();

        Assert.True(snapshot.Pooling);
        Assert.Equal(Microsoft.Data.Sqlite.SqliteCacheMode.Default, snapshot.Cache);
        Assert.Equal(Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate, snapshot.Mode);
        Assert.Equal("wal", snapshot.JournalMode);
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
