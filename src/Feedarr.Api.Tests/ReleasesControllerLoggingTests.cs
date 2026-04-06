using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Titles;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ReleasesControllerLoggingTests
{
    [Fact]
    public async Task Download_WhenReleaseHasNoDownloadUrl_ReturnsNotFound_AndLogsWarning()
    {
        using var context = new ReleasesControllerTestContext();
        var releaseId = context.CreateRelease(downloadUrl: null);
        var logger = new ListLogger<ReleasesController>();
        var controller = context.CreateController(logger: logger);

        var result = await controller.Download(releaseId, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.True(logger.Contains(LogLevel.Warning, "missing download_url"));
    }

    [Fact]
    public async Task Download_WhenDownloadUrlIsInvalid_ReturnsBadRequest_AndLogsWarning()
    {
        using var context = new ReleasesControllerTestContext();
        var releaseId = context.CreateRelease(downloadUrl: "ftp://indexer.example/file.torrent");
        var logger = new ListLogger<ReleasesController>();
        var controller = context.CreateController(logger: logger);

        var result = await controller.Download(releaseId, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(logger.Contains(LogLevel.Warning, "invalid download_url"));
    }

    [Fact]
    public async Task Download_WhenMagnetLink_ReturnsRedirect_AndLogsInformation()
    {
        using var context = new ReleasesControllerTestContext();
        const string magnet = "magnet:?xt=urn:btih:1234";
        var releaseId = context.CreateRelease(downloadUrl: magnet);
        var logger = new ListLogger<ReleasesController>();
        var controller = context.CreateController(logger: logger);

        var result = await controller.Download(releaseId, CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal(magnet, redirect.Url);
        Assert.True(logger.Contains(LogLevel.Information, "magnet redirect"));
    }

    [Fact]
    public async Task Download_WhenIndexerReturnsError_ReturnsBadGateway_AndLogsWarning()
    {
        using var context = new ReleasesControllerTestContext();
        var releaseId = context.CreateRelease(downloadUrl: "https://indexer.example/download.torrent");
        var logger = new ListLogger<ReleasesController>();
        var controller = context.CreateController(
            handler: new StaticResponseHandler(HttpStatusCode.Forbidden, Encoding.UTF8.GetBytes("forbidden")),
            logger: logger);

        var result = await controller.Download(releaseId, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, objectResult.StatusCode);
        Assert.True(logger.Contains(LogLevel.Warning, "upstream error"));
    }

    [Fact]
    public async Task Download_WhenProxyThrows_ReturnsBadGateway_AndLogsWarning()
    {
        using var context = new ReleasesControllerTestContext();
        var releaseId = context.CreateRelease(downloadUrl: "https://indexer.example/download.torrent");
        var logger = new ListLogger<ReleasesController>();
        var controller = context.CreateController(handler: new ThrowingHandler(), logger: logger);

        var result = await controller.Download(releaseId, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, objectResult.StatusCode);
        Assert.True(logger.Contains(LogLevel.Warning, "proxy failed"));
    }

    [Fact]
    public async Task Download_WhenIndexerReturnsOk_ReturnsFileContentResult_WithProxiedBytes()
    {
        using var context = new ReleasesControllerTestContext();
        var releaseId = context.CreateRelease(downloadUrl: "https://indexer.example/download.torrent");
        var payload = new byte[] { 0x64, 0x33, 0x3A, 0x66, 0x6F, 0x6F }; // minimal torrent-like bytes
        var logger = new ListLogger<ReleasesController>();
        var controller = context.CreateController(
            handler: new StaticResponseHandler(HttpStatusCode.OK, payload),
            logger: logger);

        var result = await controller.Download(releaseId, CancellationToken.None);

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal(payload, fileResult.FileContents);
        Assert.Equal("application/x-bittorrent", fileResult.ContentType);
        Assert.True(logger.Contains(LogLevel.Information, "proxy success"));
    }

    [Fact]
    public void BulkSeen_WhenTooManyIds_ReturnsBadRequest_AndLogsWarning()
    {
        using var context = new ReleasesControllerTestContext();
        var logger = new ListLogger<ReleasesController>();
        var controller = context.CreateController(logger: logger);
        var ids = Enumerable.Range(1, 1001).Select(i => (long)i).ToList();

        var result = controller.BulkSeen(new ReleasesController.BulkSeenDto { Ids = ids });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(logger.Contains(LogLevel.Warning, "too many ids"));
    }

    [Fact]
    public void MarkSeen_WhenReleaseExists_Returns204AndPersistsSeen()
    {
        using var context = new ReleasesControllerTestContext();
        var releaseId = context.CreateRelease(downloadUrl: null);
        var controller = context.CreateController();

        var result = controller.MarkSeen(releaseId);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, context.GetSeen(releaseId));
    }

    [Fact]
    public void MarkSeen_WhenReleaseDoesNotExist_Returns404()
    {
        using var context = new ReleasesControllerTestContext();
        var controller = context.CreateController();

        var result = controller.MarkSeen(999_999L);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void MarkUnseen_AfterSeen_Returns204AndResetsSeenToZero()
    {
        using var context = new ReleasesControllerTestContext();
        var releaseId = context.CreateRelease(downloadUrl: null);
        var controller = context.CreateController();
        controller.MarkSeen(releaseId); // set seen=1 first

        var result = controller.MarkUnseen(releaseId);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, context.GetSeen(releaseId));
    }

    [Fact]
    public void BulkSeen_WhenSeenFalse_MarksReleasesAsUnseen()
    {
        using var context = new ReleasesControllerTestContext();
        var first = context.CreateRelease(downloadUrl: null);
        var second = context.CreateRelease(downloadUrl: null);
        var controller = context.CreateController();

        // Mark both seen first
        controller.BulkSeen(new ReleasesController.BulkSeenDto { Seen = true, Ids = [first, second] });
        Assert.Equal(1, context.GetSeen(first));
        Assert.Equal(1, context.GetSeen(second));

        // Now un-mark
        var result = controller.BulkSeen(new ReleasesController.BulkSeenDto { Seen = false, Ids = [first, second] });

        var ok = Assert.IsType<OkObjectResult>(result);
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(ok.Value));
        Assert.Equal(2, doc.RootElement.GetProperty("updated").GetInt32());
        Assert.Equal(0, context.GetSeen(first));
        Assert.Equal(0, context.GetSeen(second));
    }

    [Fact]
    public void UpdateTitle_WhenReleaseExists_Returns200WithParsedFields()
    {
        using var context = new ReleasesControllerTestContext();
        var releaseId = context.CreateRelease(downloadUrl: null);
        var controller = context.CreateController();

        var result = controller.UpdateTitle(releaseId, new ReleasesController.UpdateTitleDto { Title = "The.Matrix.1999.1080p.BluRay.x264-GROUP" });

        var ok = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var root = doc.RootElement;
        Assert.Equal(releaseId, root.GetProperty("id").GetInt64());
        Assert.Equal(1999, root.GetProperty("year").GetInt32());
        Assert.Equal("movie", root.GetProperty("mediaType").GetString());
    }

    [Fact]
    public void UpdateTitle_WhenReleaseDoesNotExist_Returns404()
    {
        using var context = new ReleasesControllerTestContext();
        var controller = context.CreateController();

        var result = controller.UpdateTitle(999_999L, new ReleasesController.UpdateTitleDto { Title = "Some Title" });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void UpdateTitle_WhenTitleIsEmpty_Returns400()
    {
        using var context = new ReleasesControllerTestContext();
        var releaseId = context.CreateRelease(downloadUrl: null);
        var controller = context.CreateController();

        var result = controller.UpdateTitle(releaseId, new ReleasesController.UpdateTitleDto { Title = "   " });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void BulkSeen_WhenIdsMissing_ReturnsBadRequest_AndLogsWarning()
    {
        using var context = new ReleasesControllerTestContext();
        var logger = new ListLogger<ReleasesController>();
        var controller = context.CreateController(logger: logger);

        var result = controller.BulkSeen(new ReleasesController.BulkSeenDto { Ids = [] });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(logger.Contains(LogLevel.Warning, "ids missing"));
    }

    [Fact]
    public void BulkSeen_WhenValidRequest_UpdatesRows_AndLogsInformation()
    {
        using var context = new ReleasesControllerTestContext();
        var first = context.CreateRelease(downloadUrl: "https://indexer.example/a.torrent");
        var second = context.CreateRelease(downloadUrl: "https://indexer.example/b.torrent");
        var logger = new ListLogger<ReleasesController>();
        var controller = context.CreateController(logger: logger);

        var result = controller.BulkSeen(new ReleasesController.BulkSeenDto
        {
            Seen = true,
            Ids = [first, first, second]
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal(2, doc.RootElement.GetProperty("updated").GetInt32());
        Assert.Equal(1, context.GetSeen(first));
        Assert.Equal(1, context.GetSeen(second));
        Assert.True(logger.Contains(LogLevel.Information, "BulkSeen applied"));
    }

    private sealed class ReleasesControllerTestContext : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly TitleParser _parser = new();

        public ReleasesControllerTestContext()
        {
            _workspace = new TestWorkspace();
            Options = OptionsFactory.Create(new AppOptions
            {
                DataDir = _workspace.DataDir,
                DbFileName = "feedarr.db"
            });

            Db = new Db(Options);
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();
            Releases = new ReleaseRepository(Db, _parser, new UnifiedCategoryResolver());

            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SourceId = conn.ExecuteScalar<long>(
                """
                INSERT INTO sources(name, enabled, torznab_url, api_key, auth_mode, created_at_ts, updated_at_ts)
                VALUES('test', 1, 'http://localhost:9117/api', 'key', 'query', @ts, @ts);
                SELECT last_insert_rowid();
                """,
                new { ts });
        }

        public Db Db { get; }
        public long SourceId { get; }
        public ReleaseRepository Releases { get; }
        public Microsoft.Extensions.Options.IOptions<AppOptions> Options { get; }

        public ReleasesController CreateController(HttpMessageHandler? handler = null, ILogger<ReleasesController>? logger = null)
        {
            var http = new HttpClient(handler ?? new StaticResponseHandler(HttpStatusCode.OK, Encoding.UTF8.GetBytes("torrent")));
            return new ReleasesController(
                Releases,
                Db,
                _parser,
                null!,
                new FixedHttpClientFactory(http),
                logger ?? NullLogger<ReleasesController>.Instance);
        }

        public long CreateRelease(string? downloadUrl)
        {
            using var conn = Db.Open();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return conn.ExecuteScalar<long>(
                """
                INSERT INTO releases(source_id, guid, title, created_at_ts, published_at_ts, unified_category, download_url, seen)
                VALUES(@sourceId, @guid, @title, @ts, @ts, 'Film', @downloadUrl, 0);
                SELECT last_insert_rowid();
                """,
                new
                {
                    sourceId = SourceId,
                    guid = Guid.NewGuid().ToString("N"),
                    title = "Example.Title.1080p",
                    ts,
                    downloadUrl
                });
        }

        public int GetSeen(long releaseId)
        {
            using var conn = Db.Open();
            return conn.ExecuteScalar<int>("SELECT seen FROM releases WHERE id = @id;", new { id = releaseId });
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }
    }

    private sealed class FixedHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FixedHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly byte[] _payload;

        public StaticResponseHandler(HttpStatusCode statusCode, byte[] payload)
        {
            _statusCode = statusCode;
            _payload = payload;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new ByteArrayContent(_payload)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("indexer unreachable");
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        public bool Contains(LogLevel level, string messageFragment)
            => Entries.Any(e => e.Level == level && e.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "feedarr-release-logs-tests", Guid.NewGuid().ToString("N"));
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
