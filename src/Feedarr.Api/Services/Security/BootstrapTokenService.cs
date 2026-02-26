using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Feedarr.Api.Services.Security;

public sealed class BootstrapTokenService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
    private readonly IMemoryCache _cache;

    public BootstrapTokenService(IMemoryCache cache)
    {
        _cache = cache;
    }

    public int ExpiresInSeconds => (int)TokenLifetime.TotalSeconds;

    public string IssueToken()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _cache.Set(Key(token), true, TokenLifetime);
        return token;
    }

    public bool IsValid(string? token)
    {
        var trimmed = (token ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        return _cache.TryGetValue(Key(trimmed), out _);
    }

    private static string Key(string token) => $"bootstrap-token:{token}";
}
