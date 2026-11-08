using System.Net;
using System.Net.Sockets;

namespace Feedarr.Api.Services.Security;

public interface IOutboundDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public interface IOutboundSocketConnector
{
    ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken);
}

internal sealed class OutboundDnsResolver : IOutboundDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

internal sealed class OutboundSocketConnector : IOutboundSocketConnector
{
    public async ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}

public sealed class ValidatedOutboundConnector(
    OutboundDestinationPolicy policy, IOutboundDnsResolver dns, IOutboundSocketConnector sockets)
{
    public async ValueTask<Stream> ConnectAsync(string host, int port,
        OutboundDestinationContext context, CancellationToken cancellationToken)
    {
        var addresses = await dns.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(ip => !policy.Allows(host, port, ip, context)))
            throw new SsrfBlockedException();
        // Never give the hostname to the socket API: only the validated snapshot is used.
        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await sockets.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false); }
            catch (SocketException ex) { lastError = ex; }
        }
        throw new HttpRequestException("Unable to connect to validated destination", lastError);
    }
}
