using System.Security.Cryptography;
using System.Text;

namespace Feedarr.Api.Services.Security;

/// <summary>
/// Issues short-lived, single-use bootstrap tokens for the initial security setup flow.
///
/// Tokens are stored as SHA-256 hashes (never plaintext) with an expiry and a used flag.
/// <see cref="TryConsume"/> atomically validates and invalidates a token so concurrent
/// requests cannot both succeed with the same token.
/// </summary>
public sealed class BootstrapTokenService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    public enum TokenStatus
    {
        Missing,
        Unknown,
        Expired,
        Used,
        Valid
    }

    private sealed class TokenEntry
    {
        public DateTime ExpiresAt { get; init; }
        public bool Used { get; set; }
    }

    // keyed by SHA-256(token) in hex — plaintext tokens never stored
    private readonly Dictionary<string, TokenEntry> _tokens = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public int ExpiresInSeconds => (int)TokenLifetime.TotalSeconds;

    /// <summary>Issues a new single-use token. Any previous tokens remain valid until consumed or expired.</summary>
    public string IssueToken()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        lock (_lock)
        {
            PurgeExpired_Locked();
            _tokens[HashToken(token)] = new TokenEntry { ExpiresAt = DateTime.UtcNow.Add(TokenLifetime) };
        }
        return token;
    }

    /// <summary>
    /// Returns true if the token is valid (not expired, not yet used).
    /// Does NOT consume the token — use <see cref="TryConsume"/> for that.
    /// </summary>
    public bool IsValid(string? token)
    {
        return GetStatus(token) == TokenStatus.Valid;
    }

    /// <summary>
    /// Atomically validates AND marks the token as used (single-use guarantee).
    /// Returns true only on the first valid call; subsequent calls with the same token return false.
    /// </summary>
    public bool TryConsume(string? token)
    {
        var trimmed = Trim(token);
        if (trimmed is null) return false;

        lock (_lock)
        {
            if (!TryGetValid_Locked(trimmed, out var entry))
                return false;

            entry!.Used = true;
            return true;
        }
    }

    public TokenStatus GetStatus(string? token)
    {
        var trimmed = Trim(token);
        if (trimmed is null)
            return TokenStatus.Missing;

        lock (_lock)
        {
            if (!_tokens.TryGetValue(HashToken(trimmed), out var entry))
                return TokenStatus.Unknown;

            if (entry.Used)
                return TokenStatus.Used;

            if (entry.ExpiresAt <= DateTime.UtcNow)
                return TokenStatus.Expired;

            return TokenStatus.Valid;
        }
    }

    /// <summary>
    /// Forcibly invalidates all outstanding tokens.
    /// Call this after security settings are saved successfully.
    /// </summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _tokens.Clear();
        }
    }

    // -----------------------------------------------------------------------

    private bool TryGetValid_Locked(string trimmedToken, out TokenEntry? entry)
    {
        if (!_tokens.TryGetValue(HashToken(trimmedToken), out entry))
            return false;

        if (entry.Used || entry.ExpiresAt <= DateTime.UtcNow)
            return false;

        return true;
    }

    private static string? Trim(string? token)
    {
        var t = (token ?? "").Trim();
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private void PurgeExpired_Locked()
    {
        var now = DateTime.UtcNow;
        var expired = _tokens
            .Where(kv => kv.Value.ExpiresAt < now)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expired)
            _tokens.Remove(key);
    }
}
