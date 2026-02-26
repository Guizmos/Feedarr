using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Microsoft.Extensions.Caching.Memory;

namespace Feedarr.Api.Services.Security;

public sealed class SetupStateService
{
    private const string SetupCompletedCacheKey = "setup:completed:v1";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);

    private readonly SettingsRepository _settings;
    private readonly IMemoryCache _cache;

    public SetupStateService(SettingsRepository settings, IMemoryCache cache)
    {
        _settings = settings;
        _cache = cache;
    }

    public bool IsSetupCompleted()
    {
        return _cache.GetOrCreate(SetupCompletedCacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            var ui = _settings.GetUi(new UiSettings());
            return ui.OnboardingDone;
        });
    }

    public void MarkSetupCompleted()
    {
        var ui = _settings.GetUi(new UiSettings());
        if (ui.OnboardingDone)
        {
            _cache.Set(SetupCompletedCacheKey, true, CacheDuration);
            return;
        }

        ui.OnboardingDone = true;
        _settings.SaveUi(ui);
        _cache.Set(SetupCompletedCacheKey, true, CacheDuration);
    }

    public void ResetSetupCompleted()
    {
        var ui = _settings.GetUi(new UiSettings());
        if (ui.OnboardingDone)
        {
            ui.OnboardingDone = false;
            _settings.SaveUi(ui);
        }

        _cache.Set(SetupCompletedCacheKey, false, CacheDuration);
    }
}
