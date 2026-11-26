using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Igdb;

// Shared across typed clients. Bounded two-minute response cache; credentials and
// query scope are part of the key. Never caches a decision or incomplete evidence.
public sealed class IgdbMatchingState : IDisposable
{
    internal ProviderResponseCache Responses { get; } = new();
    public void Dispose() => Responses.Dispose();
}
