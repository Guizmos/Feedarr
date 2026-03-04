using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;

namespace Feedarr.Api.Services.ExternalProviders;

public sealed class ExternalProviderLimiter : IExternalProviderLimiter
{
    private readonly IReadOnlyDictionary<ProviderKind, SemaphoreSlim> _semaphores;
    private readonly IReadOnlyDictionary<ProviderKind, int> _limits;

    public ExternalProviderLimiter(
        SettingsRepository settings,
        ILogger<ExternalProviderLimiter> log)
    {
        var maintenance = settings.GetMaintenance(new MaintenanceSettings());
        var mode = NormalizeMode(maintenance.ProviderRateLimitMode);
        var manual = maintenance.ProviderConcurrencyManual ?? new ProviderConcurrencyManualSettings();

        _limits = new Dictionary<ProviderKind, int>
        {
            [ProviderKind.Tmdb] = mode == "manual" ? Math.Clamp(manual.Tmdb, 1, 3) : 2,
            [ProviderKind.Igdb] = mode == "manual" ? Math.Clamp(manual.Igdb, 1, 2) : 1,
            [ProviderKind.Fanart] = mode == "manual" ? Math.Clamp(manual.Fanart, 1, 2) : 1,
            [ProviderKind.TvMaze] = mode == "manual" ? Math.Clamp(manual.Tvmaze, 1, 2) : 1,
            [ProviderKind.Others] = mode == "manual" ? Math.Clamp(manual.Others, 1, 2) : 1,
        };

        _semaphores = _limits.ToDictionary(
            pair => pair.Key,
            pair => new SemaphoreSlim(pair.Value, pair.Value));

        log.LogInformation(
            "External provider limiter configured mode={Mode} tmdb={Tmdb} igdb={Igdb} fanart={Fanart} tvmaze={TvMaze} others={Others}",
            mode,
            _limits[ProviderKind.Tmdb],
            _limits[ProviderKind.Igdb],
            _limits[ProviderKind.Fanart],
            _limits[ProviderKind.TvMaze],
            _limits[ProviderKind.Others]);
    }

    public async Task<T> RunAsync<T>(ProviderKind kind, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var semaphore = GetSemaphore(kind);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task RunAsync(ProviderKind kind, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        var semaphore = GetSemaphore(kind);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action(ct).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    internal int ResolveLimit(ProviderKind kind)
        => _limits.TryGetValue(kind, out var limit) ? limit : 1;

    private SemaphoreSlim GetSemaphore(ProviderKind kind)
        => _semaphores.TryGetValue(kind, out var semaphore)
            ? semaphore
            : _semaphores[ProviderKind.Others];

    private static string NormalizeMode(string? value)
        => string.Equals(value?.Trim(), "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto";
}
