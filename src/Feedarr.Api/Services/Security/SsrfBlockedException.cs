namespace Feedarr.Api.Services.Security;

/// <summary>A local security refusal, never inferred from an upstream HTTP status.</summary>
public sealed class SsrfBlockedException : HttpRequestException
{
    public const string Code = "ssrf_blocked";
    public const string SafeMessage = "Destination blocked by SSRF protection. Configure App:Security:TrustedHosts or TrustedNetworks for an eligible self-hosted integration.";
    public SsrfBlockedException() : base(SafeMessage) { }

    // SocketsHttpHandler may wrap exceptions raised by ConnectCallback.
    public static bool IsBlocked(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is SsrfBlockedException) return true;
        return false;
    }
}
