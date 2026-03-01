using Feedarr.Api.Data.Repositories;

namespace Feedarr.Api.Services.ExternalProviders;

public sealed class ExternalProvidersBootstrapService : IHostedService
{
    private readonly ExternalProviderInstanceRepository _instances;
    private readonly ILogger<ExternalProvidersBootstrapService> _log;

    public ExternalProvidersBootstrapService(
        ExternalProviderInstanceRepository instances,
        ILogger<ExternalProvidersBootstrapService> log)
    {
        _instances = instances;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _instances.UpsertFromLegacyDefaultsAsync(cancellationToken).ConfigureAwait(false);
        await _instances.SeedFreeProvidersAsync(cancellationToken).ConfigureAwait(false);
        _log.LogInformation("External providers bootstrap completed");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
