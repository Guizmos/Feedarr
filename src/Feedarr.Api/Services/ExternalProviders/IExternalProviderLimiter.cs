namespace Feedarr.Api.Services.ExternalProviders;

public interface IExternalProviderLimiter
{
    Task<T> RunAsync<T>(ProviderKind kind, Func<CancellationToken, Task<T>> action, CancellationToken ct);
    Task RunAsync(ProviderKind kind, Func<CancellationToken, Task> action, CancellationToken ct);
}

public sealed class NoOpExternalProviderLimiter : IExternalProviderLimiter
{
    public static readonly NoOpExternalProviderLimiter Instance = new();

    private NoOpExternalProviderLimiter()
    {
    }

    public Task<T> RunAsync<T>(ProviderKind kind, Func<CancellationToken, Task<T>> action, CancellationToken ct)
        => action(ct);

    public Task RunAsync(ProviderKind kind, Func<CancellationToken, Task> action, CancellationToken ct)
        => action(ct);
}
