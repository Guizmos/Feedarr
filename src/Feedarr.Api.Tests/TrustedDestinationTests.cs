using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Feedarr.Api.Services.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Tests;

public sealed class TrustedDestinationTests
{
    private static OutboundDestinationPolicy Policy() => new(new OutboundSecurityOptions
    {
        TrustedHosts = new() { ["Prowlarr"] = "prowlarr:9696" },
        TrustedNetworks = ["10.50.0.0/24"]
    });

    [Theory]
    [InlineData("prowlarr", 9696, "172.20.0.2", true)]
    [InlineData("prowlarr", 9697, "172.20.0.2", false)]
    [InlineData("other.prowlarr", 9696, "172.20.0.2", false)]
    [InlineData("PROWLARR", 9696, "172.20.0.2", true)]
    [InlineData("other", 1234, "10.50.0.255", true)]
    [InlineData("other", 1234, "10.50.1.0", false)]
    [InlineData("other", 80, "8.8.8.8", true)]
    [InlineData("prowlarr", 9696, "127.0.0.1", false)]
    [InlineData("prowlarr", 9696, "169.254.169.254", false)]
    [InlineData("prowlarr", 9696, "100.100.100.200", false)]
    [InlineData("prowlarr", 9696, "168.63.129.16", false)]
    [InlineData("metadata.google.internal.", 9696, "8.8.8.8", false)]
    [InlineData("prowlarr", 9696, "::ffff:127.0.0.1", false)]
    [InlineData("prowlarr", 9696, "::ffff:172.20.0.2", true)]
    [InlineData("prowlarr", 9696, "fd00::1", false)]
    [InlineData("prowlarr", 9696, "64:ff9b::a32:1", false)]
    [InlineData("prowlarr", 9696, "2002:0a00:0001::1", false)]
    public void PolicyEnforcesScope(string host, int port, string ip, bool allowed)
    {
        var policy = Policy();
        var address = IPAddress.Parse(ip);
        Assert.Equal(allowed, policy.Allows(host, port, address, OutboundDestinationContext.TrustedIntegration));
        if (ip.Contains("172.20") || ip.StartsWith("10.50"))
            Assert.False(policy.Allows(host, port, address, OutboundDestinationContext.Strict));
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("10.0.0.0/7")]
    [InlineData("172.0.0.0/8")]
    [InlineData("192.168.1.1/24")]
    [InlineData("127.0.0.0/8")]
    [InlineData("fd00::/8")]
    [InlineData("10.0.0.0/33")]
    [InlineData("invalid")]
    public void InvalidNetworksFailValidation(string cidr) => Assert.True(
        new OutboundSecurityOptionsValidator().Validate(null,
            new() { TrustedNetworks = [cidr] }).Failed);

    [Theory]
    [InlineData("prowlarr")]
    [InlineData("http://prowlarr:9696")]
    [InlineData("*.local:80")]
    [InlineData("user@prowlarr:80")]
    [InlineData("prowlarr:80/path")]
    [InlineData("prowlarr:0")]
    [InlineData("127.0.0.1:80")]
    [InlineData("169.254.169.254:80")]
    public void InvalidHostsFailValidation(string host) => Assert.True(
        new OutboundSecurityOptionsValidator().Validate(null,
            new() { TrustedHosts = new() { ["test"] = host } }).Failed);

    [Fact]
    public void ConfigurationBindsWithoutEntryLimit()
    {
        var data = new Dictionary<string, string?> { ["App:Security:TrustedHosts:Prowlarr"] = "prowlarr:9696" };
        for (var i = 0; i < 512; i++) data[$"App:Security:TrustedNetworks:{i}"] = "10.50.0.0/24";
        var options = new ConfigurationBuilder().AddInMemoryCollection(data).Build()
            .GetSection("App:Security").Get<OutboundSecurityOptions>()!;
        Assert.Equal(512, options.TrustedNetworks.Count);
        Assert.True(new OutboundSecurityOptionsValidator().Validate(null, options).Succeeded);
        Assert.False(new OutboundDestinationPolicy(new OutboundSecurityOptions())
            .Allows("prowlarr", 9696, IPAddress.Parse("172.20.0.2"), OutboundDestinationContext.TrustedIntegration));
    }

    [Fact]
    public async Task RebindingCannotChangeConnectedAddressAndNewConnectionsAreRevalidated()
    {
        var dns = new FakeDns([IPAddress.Parse("8.8.8.8")], [IPAddress.Parse("127.0.0.1")]);
        var sockets = new FakeSockets();
        var connector = new ValidatedOutboundConnector(Policy(), dns, sockets);
        using var stream = await connector.ConnectAsync("public.test", 80, OutboundDestinationContext.Strict, default);
        Assert.Equal("8.8.8.8", Assert.Single(sockets.Endpoints).Address.ToString());
        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
            await connector.ConnectAsync("public.test", 80, OutboundDestinationContext.Strict, default));
        Assert.Single(sockets.Endpoints);
    }

    [Fact]
    public async Task MixedDnsAnswersNeverConnectAndStrictDoesNotInheritTrust()
    {
        foreach (var context in Enum.GetValues<OutboundDestinationContext>())
        {
            var sockets = new FakeSockets();
            var connector = new ValidatedOutboundConnector(Policy(),
                new FakeDns([IPAddress.Parse("172.20.0.2"), IPAddress.Parse("169.254.169.254")]), sockets);
            await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
                await connector.ConnectAsync("prowlarr", 9696, context, default));
            Assert.Empty(sockets.Endpoints);
        }
    }

    [Fact]
    public async Task RetryRevalidatesEveryNewConnection()
    {
        var sockets = new FakeSockets();
        var connector = new ValidatedOutboundConnector(Policy(),
            new FakeDns([IPAddress.Parse("8.8.8.8")], [IPAddress.Parse("127.0.0.1")]), sockets);
        using var retry = new TransientHttpRetryHandler { InnerHandler = new ConnectionHandler(connector) };
        using var invoker = new HttpMessageInvoker(retry);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://public.test"), default));
        Assert.Single(sockets.Endpoints);
    }

    [Theory]
    [InlineData("http://prowlarr:9697/next", false)]
    [InlineData("http://other:9696/next", false)]
    [InlineData("http://prowlarr:9696/next", true)]
    public async Task RedirectRequiresSameHostAndPort(string target, bool followed)
    {
        var inner = new RedirectHandler(target);
        using var handler = new ProtocolDowngradeRedirectHandler(Policy(), OutboundDestinationContext.TrustedIntegration)
        { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);
        if (followed)
        {
            using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://prowlarr:9696/start"), default);
        }
        else
        {
            await Assert.ThrowsAsync<SsrfBlockedException>(() => invoker.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "http://prowlarr:9696/start"), default));
        }
        Assert.Equal(followed ? 2 : 1, inner.Calls);
    }

    [Fact]
    public async Task PreflightCannotAuthorizeReboundConnection()
    {
        var dns = new FakeDns([IPAddress.Parse("8.8.8.8")], [IPAddress.Loopback]);
        var sockets = new FakeSockets();
        var connector = new ValidatedOutboundConnector(Policy(), dns, sockets);
        using var guard = new SsrfGuardHandler(Policy(), dns, OutboundDestinationContext.Strict)
        { InnerHandler = new ConnectionHandler(connector) };
        using var invoker = new HttpMessageInvoker(guard);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://public.test"), default));
        Assert.Empty(sockets.Endpoints);
    }

    [Fact]
    public async Task TrustedConnectionDoesNotAuthorizeStrictConnection()
    {
        var sockets = new FakeSockets();
        var connector = new ValidatedOutboundConnector(Policy(), new FakeDns([IPAddress.Parse("172.20.0.2")]), sockets);
        using var stream = await connector.ConnectAsync("prowlarr", 9696, OutboundDestinationContext.TrustedIntegration, default);
        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
            await connector.ConnectAsync("prowlarr", 9696, OutboundDestinationContext.Strict, default));
        Assert.Single(sockets.Endpoints);
    }

    [Fact]
    public async Task InvalidConfigurationFailsAtHostStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddOptions<OutboundSecurityOptions>()
            .Configure(o => o.TrustedNetworks = ["0.0.0.0/0"]).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<OutboundSecurityOptions>, OutboundSecurityOptionsValidator>();
        using var host = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public void PolicyResolvesThroughDependencyInjection()
    {
        var services = new ServiceCollection();
        services.AddOptions<OutboundSecurityOptions>();
        services.AddSingleton<OutboundDestinationPolicy>();
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<OutboundDestinationPolicy>());
    }

    [Fact]
    public async Task RealTransportPoolsOnlyValidatedConnectionAndRevalidatesAfterClose()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            for (var i = 0; i < 2; i++)
            {
                string? line;
                do { line = await reader.ReadLineAsync(timeout.Token); }
                while (!string.IsNullOrEmpty(line));
                var connection = i == 1 ? "close" : "keep-alive";
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: {connection}\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token);
            }
        }, timeout.Token);
        var sockets = new LocalTestSockets(endpoint);
        var connector = new ValidatedOutboundConnector(Policy(),
            new FakeDns([IPAddress.Parse("8.8.8.8")], [IPAddress.Loopback]), sockets);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = (context, ct) => connector.ConnectAsync(context.DnsEndPoint.Host,
                context.DnsEndPoint.Port, OutboundDestinationContext.Strict, ct)
        };
        using var http = new HttpClient(handler);
        using var first = await http.GetAsync("http://public.test/first", timeout.Token);
        using var second = await http.GetAsync("http://public.test/second", timeout.Token);
        await server;
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("http://public.test/third", timeout.Token));
        Assert.Equal(1, sockets.Calls);
    }

    // Test-only connector maps the validated endpoint to a local scripted HTTP server.
    private sealed class LocalTestSockets(IPEndPoint endpoint) : IOutboundSocketConnector
    {
        public int Calls;
        public async ValueTask<Stream> ConnectAsync(IPEndPoint validatedEndpoint, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(IPAddress.Parse("8.8.8.8"), validatedEndpoint.Address);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(endpoint, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    }

    private sealed class FakeDns(params IPAddress[][] answers) : IOutboundDnsResolver
    {
        private int _calls;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(answers[Math.Min(_calls++, answers.Length - 1)]);
    }
    private sealed class FakeSockets : IOutboundSocketConnector
    {
        public List<IPEndPoint> Endpoints { get; } = [];
        public ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
        { Endpoints.Add(endpoint); return ValueTask.FromResult<Stream>(new MemoryStream()); }
    }
    private sealed class ConnectionHandler(ValidatedOutboundConnector connector) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var stream = await connector.ConnectAsync(request.RequestUri!.Host, request.RequestUri.Port, OutboundDestinationContext.Strict, ct);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }
    private sealed class RedirectHandler(string target) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(++Calls == 1 ? HttpStatusCode.Found : HttpStatusCode.OK);
            if (Calls == 1) response.Headers.Location = new Uri(target);
            return Task.FromResult(response);
        }
    }
}
