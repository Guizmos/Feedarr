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
