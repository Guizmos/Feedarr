using System.Security.Cryptography;
using System.Text;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Services.Rawg;

public sealed record RawgAvailability(string Status, string? Reason, bool Suspended,
    DateTimeOffset? ObservedAtUtc, DateTimeOffset? NextProbeAtUtc, string Reactivation);

// Process-local availability, shared by typed clients. Only credential fingerprints
// are retained. A monthly reset date is never guessed from the calendar.
public sealed class RawgAuthenticationState : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private sealed class Entry
    {
        internal string Status = "unknown";
        internal string? Reason;
        internal DateTimeOffset? Observed;
        internal DateTimeOffset? LastProbe;
    }
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    internal readonly SemaphoreSlim Requests = new(1, 1);
    internal readonly ProviderResponseCache Responses = new();
    public RawgAuthenticationState(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;
    internal static string Fingerprint(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    private Entry Select(string key)
    {
        var fingerprint = Fingerprint(key);
        if (_entries.TryGetValue(fingerprint, out var entry)) return entry;
        if (_entries.Count >= 32) _entries.Remove(_entries.Keys.First());
        return _entries[fingerprint] = new();
    }
    internal RawgAvailability GetStatus(string? key)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(key) || !_entries.TryGetValue(Fingerprint(key), out var entry))
                return new("unknown", null, false, null, null, "explicit_test");
            return new(entry.Status, entry.Reason, entry.Status is "quota_exhausted" or "authentication_rejected" or "access_denied",
                entry.Observed, entry.LastProbe?.AddSeconds(30), "explicit_test");
        }
    }
    internal bool IsRejected(string key) => GetStatus(key).Suspended;
    internal string? BlockedReason(string key) => GetStatus(key) is { Suspended: true } value ? value.Reason : null;
    internal void Reject(string key) => Failure(key, "rawg_http_unauthorized");
    internal void Failure(string key, string reason)
    {
        lock (_gate)
        {
            var entry = Select(key);
            var status = reason switch {
                "rawg_quota_exhausted" => "quota_exhausted",
                "rawg_http_unauthorized" or "rawg_invalid_api_key" => "authentication_rejected",
                "rawg_http_forbidden" => "access_denied",
                "rawg_http_client_error" => "request_error",
                _ => "temporary_error"
            };
            // A failed recovery probe cannot lift a known suspension.
            if (entry.Status is "quota_exhausted" or "authentication_rejected" or "access_denied" &&
                status is not ("quota_exhausted" or "authentication_rejected" or "access_denied")) return;
            entry.Status = status; entry.Reason = reason; entry.Observed = _clock.GetUtcNow();
        }
    }
    internal void Accept(string key)
    {
        lock (_gate) { var entry = Select(key); entry.Status = "ready"; entry.Reason = null; entry.Observed = _clock.GetUtcNow(); }
    }
    internal bool TryStartProbe(string key)
    {
        lock (_gate)
        {
            var entry = Select(key);
            var now = _clock.GetUtcNow();
            if (entry.LastProbe is { } last && now < last.AddSeconds(30)) return false;
            entry.LastProbe = now;
            return true;
        }
    }
    public void Dispose() { Responses.Dispose(); Requests.Dispose(); }
}
