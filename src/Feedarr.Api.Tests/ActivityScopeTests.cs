using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Posters;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ActivityScopeTests
{
    [Theory]
    [InlineData("logs")]
    [InlineData("history")]
    public void PurgingOneScopePreservesEveryRowOfTheOtherScope(string scope)
    {
        using var f = new Fixture();
        var other = scope == "logs" ? "history" : "logs";
        var preserved = Rows(f.Controller.List(500, null, null, null, other));
        var csv = f.Csv.CreateRetroFetchLog();
        Assert.IsType<OkObjectResult>(f.Controller.Purge(scope));
        Assert.Equal("[]", Rows(f.Controller.List(500, null, null, null, scope)));
        Assert.Equal(preserved, Rows(f.Controller.List(500, null, null, null, other)));
        Assert.Equal(scope == "history", File.Exists(Path.Combine(f.Csv.LogsDirPath, csv)));
    }

    [Fact]
    public void ListingScopesApplyBeforeLimitAndComposeWithExistingFilters()
    {
        using var f = new Fixture();
        var latestLog = JsonSerializer.Deserialize<JsonElement>(Rows(f.Controller.List(1, null, null, null, "logs")));
        Assert.Single(latestLog.EnumerateArray()); Assert.Equal("maintenance", latestLog[0].GetProperty("eventType").GetString());
        var log = JsonSerializer.Deserialize<JsonElement>(Rows(f.Controller.List(1, 1, "poster_fetch", "warn", "logs")));
        Assert.Single(log.EnumerateArray()); Assert.Equal("poster log", log[0].GetProperty("message").GetString());
        var history = JsonSerializer.Deserialize<JsonElement>(Rows(f.Controller.List(500, null, "sync", null)));
        Assert.Equal(2, history.GetArrayLength());
        Assert.All(history.EnumerateArray(), row => Assert.Equal("sync", row.GetProperty("eventType").GetString()!.ToLowerInvariant()));
        Assert.Equal("[]", Rows(f.Controller.List(500, null, "sync", null, "logs")));
        Assert.IsType<BadRequestObjectResult>(f.Controller.List(500, null, null, null, "invalid"));
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("history")]
    public void AgeBasedCleanupAlsoPreservesTheOtherScope(string scope)
    {
        using var f = new Fixture();
        var other = scope == "logs" ? "history" : "logs";
        var preserved = Rows(f.Controller.List(500, null, null, null, other));
        Assert.True(f.Repository.PurgeOlderThan(1, scope) > 0);
        Assert.Equal("[]", Rows(f.Controller.List(500, null, null, null, scope)));
        Assert.Equal(preserved, Rows(f.Controller.List(500, null, null, null, other)));
    }

    private static string Rows(IActionResult result) => JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "feedarr-activity-tests", Guid.NewGuid().ToString("N"));
        public ActivityRepository Repository { get; }
        public ActivityController Controller { get; }
        public RetroFetchLogService Csv { get; }
        public Fixture()
        {
            var options = OptionsFactory.Create(new AppOptions { DataDir = root, DbFileName = "activity.db" });
            var db = new Db(options); new MigrationsRunner(db, NullLogger<MigrationsRunner>.Instance).Run();
            Repository = new ActivityRepository(db, new BadgeSignal());
            Csv = new RetroFetchLogService(options, new Environment(root));
            Controller = new ActivityController(Repository, Csv);
            using var c = db.Open();
            c.Execute("""
                INSERT INTO activity_log(source_id,level,event_type,message,data_json,created_at_ts) VALUES
                  (1,'warn','poster_fetch','poster log','{"logFile":"kept.csv"}',1),
                  (2,'info','source','source log','{}',2),
                  (1,'info','maintenance','maintenance log',NULL,3),
                  (1,'info','sync','history one','{"categoryIds":[2000]}',4),
                  (2,'error','SYNC','history two','{}',5);
                """);
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Feedarr.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
