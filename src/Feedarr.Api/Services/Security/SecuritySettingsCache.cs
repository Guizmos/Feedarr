namespace Feedarr.Api.Services.Security;

public static class SecuritySettingsCache
{
    public const string CacheKey = "settings:security";
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);
}
