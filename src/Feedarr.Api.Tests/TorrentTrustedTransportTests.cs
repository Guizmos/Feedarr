using System.Net;
using System.Net.Sockets;
using System.Text;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Options;
using Feedarr.Api.Services;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Diagnostics;
using Feedarr.Api.Services.Resilience;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Torznab;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class TorrentTrustedTransportTests
{
    [Theory]
    [InlineData("host")]
    [InlineData("network")]
    [InlineData("public")]
    public async Task RssAndTorrentShareAuthorizationOnRealLocalHttp(string approval)
    {
        await using var rig = new Rig(approval);
        var release = Assert.Single((await rig.Rss.FetchLatestAsync(rig.Url + "/rss", "query", "fixture", 10, default)).items);
        Assert.Equal(rig.Url + "/torrent", release.DownloadUrl);
        var file = Assert.IsType<FileStreamResult>(await rig.Download(release.DownloadUrl!));
        using var reader = new StreamReader(file.FileStream);
        Assert.Equal("fixture-torrent", await reader.ReadToEndAsync());
        Assert.Equal(2, rig.Hits);
        Assert.All(rig.Sockets.Addresses, ip => Assert.Equal(rig.Address, ip));
    }

    [Fact]
    public async Task UnapprovedPrivateSourceNeverReachesLocalServer()
    {
        await using var rig = new Rig("none");
        await Assert.ThrowsAsync<SsrfBlockedException>(() => rig.Rss.FetchLatestAsync(rig.Url + "/rss", "query", "fixture", 10, default));
        Assert.Equal(502, Assert.IsType<ObjectResult>(await rig.Download("/torrent")).StatusCode);
        Assert.Equal(SsrfBlockedException.Code, rig.Controller.HttpContext.Items[DownloadDiagnostics.FailureCodeKey]);
        Assert.Equal(0, rig.Hits);
        Assert.Empty(rig.Sockets.Addresses);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("address")]
    [InlineData("rebind")]
    public async Task RedirectsReapplyPolicyBeforeAnyDestinationTraffic(string change)
    {
        await using var rig = new Rig("host");
        rig.Redirect = change switch
        {
            "host" => $"http://unapproved.test:{rig.Port}/torrent",
            "port" => $"http://nas.test:{rig.Port + 1}/torrent",
            "address" => "http://169.254.169.254/latest/meta-data",
            _ => "/torrent"
        };
        rig.RebindOnRedirect = change == "rebind";
        var result = await rig.Download("/redirect");
        if (change == "path")
        {
            var file = Assert.IsType<FileStreamResult>(result);
            await file.FileStream.DisposeAsync();
            Assert.Equal(2, rig.Hits);
        }
        else
        {
            Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal(SsrfBlockedException.Code, rig.Controller.HttpContext.Items[DownloadDiagnostics.FailureCodeKey]);
            Assert.Equal(1, rig.Hits);
            Assert.Single(rig.Sockets.Addresses);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "feedarr-torrent-" + Guid.NewGuid().ToString("N"));
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly Task _server;
        private readonly HttpClient _http;
        private readonly FixtureDns _dns;
        private readonly Db _db;
        public readonly MappedSockets Sockets;
        public readonly IPAddress Address;
        public readonly TorznabClient Rss;
        public ReleasesController Controller { get; private set; } = null!;
        public int Hits;
        public string? Redirect;
        public bool RebindOnRedirect;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        private readonly string _host;
        public string Url => $"http://{_host}:{Port}";

        public Rig(string approval)
        {
            _host = approval == "public" ? "93.184.216.34" : approval == "network" ? "172.20.0.2" : "nas.test";
            _listener.Start();
            Address = IPAddress.Parse(approval == "public" ? "93.184.216.34" : "172.20.0.2");
            var policy = new OutboundDestinationPolicy(new OutboundSecurityOptions
            {
                TrustedHosts = approval == "host" ? new() { ["fixture"] = $"nas.test:{Port}" } : new(),
                TrustedNetworks = approval == "network" ? ["172.20.0.0/24"] : []
            });
            var dns = _dns = new FixtureDns(Address);
            Sockets = new MappedSockets((IPEndPoint)_listener.LocalEndpoint);
            var connector = new ValidatedOutboundConnector(policy, dns, Sockets);
            var transport = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
                ConnectCallback = (ctx, ct) => connector.ConnectAsync(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, OutboundDestinationContext.TrustedIntegration, ct) };
            _http = new HttpClient(new ProtocolDowngradeRedirectHandler(policy, OutboundDestinationContext.TrustedIntegration)
            { InnerHandler = new SsrfGuardHandler(policy, dns, OutboundDestinationContext.TrustedIntegration) { InnerHandler = transport } }) { Timeout = TimeSpan.FromSeconds(5) };
            Rss = new TorznabClient(_http, new TorznabRssParser(), NullLogger<TorznabClient>.Instance, policy);
            var options = OptionsFactory.Create(new AppOptions { DataDir = _directory, DbFileName = "fixture.db" });
            _db = new Db(options);
            new MigrationsRunner(_db, NullLogger<MigrationsRunner>.Instance).Run();
            var releases = new ReleaseRepository(_db, new TitleParser(), new UnifiedCategoryResolver());
            Controller = new ReleasesController(releases, _db, new TitleParser(), null!, new Factory(_http), NullLogger<ReleasesController>.Instance, policy)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
            _server = Serve();
        }
        public Task<IActionResult> Download(string path)
        {
            using var c = _db.Open();
            c.Execute("INSERT OR IGNORE INTO sources(id,name,enabled,torznab_url,api_key,auth_mode,created_at_ts,updated_at_ts) VALUES(1,'fixture',1,@url,'fixture','query',1,1)", new { url = Url });
            var id = c.ExecuteScalar<long>("INSERT INTO releases(source_id,guid,title,download_url,created_at_ts) VALUES(1,@guid,'fixture',@url,1); SELECT last_insert_rowid()", new { guid = Guid.NewGuid().ToString(), url = path.StartsWith("http://", StringComparison.Ordinal) ? path : Url + path });
            return Controller.Download(id, _stop.Token);
        }
        private async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var line = await reader.ReadLineAsync(_stop.Token);
                    var path = line?.Split(' ')[1] ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    Interlocked.Increment(ref Hits);
                    var redirect = path.StartsWith("/redirect") ? Redirect : null;
                    if (redirect is not null && RebindOnRedirect) _dns.Address = IPAddress.Loopback;
                    var body = path.StartsWith("/rss")
                        ? $"<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><item><title>Fixture</title><guid>fixture</guid><enclosure url=\"{Url}/torrent\" type=\"application/x-bittorrent\" length=\"15\" /></item></channel></rss>"
                        : "fixture-torrent";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(redirect is null ? "200 OK" : "302 Found")}\r\n{(redirect is null ? "" : $"Location: {redirect}\r\n")}Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            _http.Dispose(); _stop.Cancel(); await _server; _listener.Stop(); _stop.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
    }
    private sealed class FixtureDns(IPAddress address) : IOutboundDnsResolver
    {
        public IPAddress Address = address;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.TryParse(host, out var literal) ? literal : Address });
    }
    // Test-only mapping: real TCP/HTTP, but no packet ever goes to the simulated IP.
    private sealed class MappedSockets(IPEndPoint local) : IOutboundSocketConnector
    {
        public readonly List<IPAddress> Addresses = [];
        public async ValueTask<Stream> ConnectAsync(IPEndPoint validated, CancellationToken ct)
        {
            Addresses.Add(validated.Address);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(local, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    { public HttpClient CreateClient(string name) { Assert.Equal("torrent-download", name); return client; } }
}
