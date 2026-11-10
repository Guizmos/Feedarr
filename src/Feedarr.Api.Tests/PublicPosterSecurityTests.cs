using System.Net;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Resilience;
using Feedarr.Api.Services.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Feedarr.Api.Tests;

public sealed class PublicPosterSecurityTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.1.5")]
    [InlineData("169.254.169.254")]
    [InlineData("fd00::1")]
    public async Task PublicProvider_RejectsPrivateDns_EvenWithIntegrationException(string ip)
    {
        using var rig = new TransportRig(new DnsStub(ip));
        await Assert.ThrowsAsync<SsrfBlockedException>(() => rig.Client.GetAsync("https://images.test/cover"));
        Assert.Equal(0, rig.Transport.Calls);
        Assert.Empty(rig.Sockets.Addresses);
    }

    [Theory]
    [InlineData("https://127.0.0.1/cover")]
    [InlineData("https://[::1]/cover")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://public.test/cover")]
    [InlineData("https://user:password@images.test/cover")]
    public async Task PublicProvider_RejectsUnsafeUrls(string url)
    {
        using var rig = new TransportRig(new DnsStub("127.0.0.1"));
        await Assert.ThrowsAnyAsync<Exception>(() => rig.Client.GetAsync(url));
        Assert.Equal(0, rig.Transport.Calls);
    }

    [Fact]
    public async Task MalformedUrl_IsNotSent()
    {
        using var rig = new TransportRig(new DnsStub("8.8.8.8"));
        await Assert.ThrowsAnyAsync<Exception>(() => rig.Client.GetAsync("https://[not-an-ip/cover"));
        Assert.Equal(0, rig.Transport.Calls);
    }

    [Fact]
    public async Task ConnectionRevalidatesDns_AfterSuccessfulPreflight()
    {
        using var rig = new TransportRig(new DnsStub("8.8.8.8", "127.0.0.1"));
        await Assert.ThrowsAsync<SsrfBlockedException>(() => rig.Client.GetAsync("https://images.test/cover"));
        Assert.Empty(rig.Sockets.Addresses);
    }

    [Theory]
    [InlineData("https://127.0.0.1/next", false)]
    [InlineData("https://[::1]/next", false)]
    [InlineData("https://internal.test/next", false)]
    [InlineData("http://cdn.test/next", false)]
    [InlineData("file:///tmp/image", false)]
    [InlineData("https://cdn.test/next", true)]
    public async Task RedirectsAreRevalidated_AndPublicCdnIsSupported(string target, bool allowed)
    {
        using var rig = new TransportRig(new DnsStub("8.8.8.8"));
        rig.Transport.Redirect = target;
        rig.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-only-token");
        if (allowed)
        {
            var bytes = await rig.Client.GetByteArrayAsync("https://images.test/cover");
            Assert.Equal(PosterTestImage.Create(), bytes);
            Assert.Equal(2, rig.Transport.Calls);
            Assert.False(rig.Transport.RedirectHadAuthorization);
        }
        else
        {
            await Assert.ThrowsAsync<SsrfBlockedException>(() => rig.Client.GetAsync("https://images.test/cover"));
            Assert.Equal(1, rig.Transport.Calls);
        }
    }

    [Theory]
    [InlineData("https://image.tmdb.org/t/p/w500/cover.jpg")]
    [InlineData("https://media.rawg.io/cover.jpg")]
    [InlineData("https://images.igdb.com/igdb/image/upload/t_cover_big/cover.jpg")]
    [InlineData("https://covers.openlibrary.org/b/id/1-L.jpg")]
    public async Task PublicImage_CanBePublished_Unchanged(string url)
    {
        using var rig = new TransportRig(new DnsStub("8.8.8.8"));
        var bytes = await rig.Client.GetByteArrayAsync(url);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            await PosterImageWriter.WriteAsync(path, bytes, default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Single(rig.Sockets.Addresses);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidImage_DoesNotCreateOrReplacePublishedFile(bool corrupt)
    {
        using var rig = new TransportRig(new DnsStub("8.8.8.8"));
        rig.Transport.Payload = corrupt ? PosterTestImage.Create()[..40] : "<html>not an image</html>"u8.ToArray();
        var bytes = await rig.Client.GetByteArrayAsync("https://images.test/cover");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => PosterImageWriter.WriteAsync(path, bytes, default));
            Assert.False(File.Exists(path));
            var original = PosterTestImage.Create();
            await File.WriteAllBytesAsync(path, original);
            await Assert.ThrowsAsync<InvalidDataException>(() => PosterImageWriter.WriteAsync(path, bytes, default));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailureOrTimeout_DoesNotPublish(bool timeout)
    {
        using var rig = new TransportRig(new DnsStub("8.8.8.8"));
        rig.Transport.Failure = timeout ? new TaskCanceledException("Simulated timeout") : new HttpRequestException("Simulated network failure");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var bytes = await rig.Client.GetByteArrayAsync("https://images.test/cover");
            await PosterImageWriter.WriteAsync(path, bytes, default);
        });
        Assert.False(File.Exists(path));
    }

    private sealed class TransportRig : IDisposable
    {
        private readonly ServiceProvider _services;
        internal SocketStub Sockets { get; } = new();
        internal TransportStub Transport { get; }
        internal HttpClient Client { get; }
        internal TransportRig(IOutboundDnsResolver dns)
        {
            var policy = new OutboundDestinationPolicy(new OutboundSecurityOptions
            { TrustedNetworks = ["10.0.0.0/8", "192.168.0.0/16"] });
            var connector = new ValidatedOutboundConnector(policy, dns, Sockets);
            Transport = new TransportStub(connector);
            var services = new ServiceCollection();
            services.AddSingleton(policy);
            services.AddSingleton(dns);
            services.AddSingleton(connector);
            services.AddHttpClient("public-posters").UsePublicProviderTransport()
                .ConfigurePrimaryHttpMessageHandler(() => Transport);
            _services = services.BuildServiceProvider();
            Client = _services.GetRequiredService<IHttpClientFactory>().CreateClient("public-posters");
        }
        public void Dispose() { Client.Dispose(); _services.Dispose(); }
    }

    private sealed class DnsStub(params string[] answers) : IOutboundDnsResolver
    {
        private int _calls;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            var answer = host == "internal.test" ? "192.168.1.5" :
                IPAddress.TryParse(host, out var literal) ? literal.ToString() :
                answers[Math.Min(_calls++, answers.Length - 1)];
            return Task.FromResult(new[] { IPAddress.Parse(answer) });
        }
    }
    private sealed class SocketStub : IOutboundSocketConnector
    {
        internal List<IPAddress> Addresses { get; } = [];
        public ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
        { Addresses.Add(endpoint.Address); return ValueTask.FromResult<Stream>(new MemoryStream()); }
    }
    private sealed class TransportStub(ValidatedOutboundConnector connector) : HttpMessageHandler
    {
        internal int Calls;
        internal string? Redirect;
        internal Exception? Failure;
        internal byte[]? Payload;
        internal bool RedirectHadAuthorization;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            using var connection = await connector.ConnectAsync(uri.IdnHost, uri.Port, OutboundDestinationContext.Strict, ct);
            Calls++;
            if (Calls > 1) RedirectHadAuthorization = request.Headers.Authorization is not null;
            if (Failure is not null) throw Failure;
            if (Redirect is not null && Calls == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri(Redirect);
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload ?? PosterTestImage.Create()) };
        }
    }
}
