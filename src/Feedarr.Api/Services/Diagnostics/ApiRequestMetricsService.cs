using System.Collections.Concurrent;

namespace Feedarr.Api.Services.Diagnostics;

public sealed class ApiRequestMetricsService
{
    private const int MaxSamplesPerEndpoint = 512;
    private readonly ConcurrentDictionary<string, EndpointWindow> _windows = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string method, string route, int statusCode, long elapsedMs)
    {
        var normalizedMethod = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        var normalizedRoute = NormalizeRoute(route);
        var key = $"{normalizedMethod} {normalizedRoute}";
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var window = _windows.GetOrAdd(key, _ => new EndpointWindow(normalizedMethod, normalizedRoute));
        window.Record(statusCode, Math.Max(0, elapsedMs), nowTs);
    }

    public ApiRequestMetricsSnapshot Snapshot(int top = 20)
    {
        var take = Math.Clamp(top <= 0 ? 20 : top, 1, 100);
        var generatedAtTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var endpointSnapshots = _windows.Values
            .Select(w => w.Snapshot())
            .Where(s => s.WindowCount > 0)
            .OrderByDescending(s => s.P95Ms)
            .ThenByDescending(s => s.WindowCount)
            .Take(take)
            .ToList();

        var totalRequests = _windows.Values.Sum(w => w.TotalRequests);
        var totalErrors = _windows.Values.Sum(w => w.TotalErrors);
        var errorRate = totalRequests > 0
            ? Math.Round((double)totalErrors * 100d / totalRequests, 2)
            : 0.0;

        return new ApiRequestMetricsSnapshot
        {
            GeneratedAtTs = generatedAtTs,
            WindowSize = MaxSamplesPerEndpoint,
            EndpointCount = _windows.Count,
            TotalRequests = totalRequests,
            TotalErrors = totalErrors,
            ErrorRatePercent = errorRate,
            Endpoints = endpointSnapshots
        };
    }

    private static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return "unknown";

        var normalized = route.Trim();
        if (normalized.StartsWith('/'))
            normalized = normalized.TrimStart('/');
        return normalized.ToLowerInvariant();
    }

    public sealed class ApiRequestMetricsSnapshot
    {
        public long GeneratedAtTs { get; set; }
        public int WindowSize { get; set; }
        public int EndpointCount { get; set; }
        public long TotalRequests { get; set; }
        public long TotalErrors { get; set; }
        public double ErrorRatePercent { get; set; }
        public List<EndpointMetricsSnapshot> Endpoints { get; set; } = new();
    }

    public sealed class EndpointMetricsSnapshot
    {
        public string Method { get; set; } = "";
        public string Route { get; set; } = "";
        public long TotalRequests { get; set; }
        public long TotalErrors { get; set; }
        public int WindowCount { get; set; }
        public double ErrorRatePercent { get; set; }
        public long AvgMs { get; set; }
        public long P50Ms { get; set; }
        public long P95Ms { get; set; }
        public long P99Ms { get; set; }
        public long MaxMs { get; set; }
        public long LastSeenAtTs { get; set; }
    }

    private sealed class EndpointWindow
    {
        private readonly object _gate = new();
        private readonly Queue<long> _samples = new();
        private long _totalRequests;
        private long _totalErrors;
        private long _lastSeenAtTs;

        public EndpointWindow(string method, string route)
        {
            Method = method;
            Route = route;
        }

        public string Method { get; }
        public string Route { get; }
        public long TotalRequests => Interlocked.Read(ref _totalRequests);
        public long TotalErrors => Interlocked.Read(ref _totalErrors);

        public void Record(int statusCode, long elapsedMs, long nowTs)
        {
            Interlocked.Increment(ref _totalRequests);
            if (statusCode >= 400)
                Interlocked.Increment(ref _totalErrors);
            Interlocked.Exchange(ref _lastSeenAtTs, nowTs);

            lock (_gate)
            {
                _samples.Enqueue(elapsedMs);
                while (_samples.Count > MaxSamplesPerEndpoint)
                    _samples.Dequeue();
            }
        }

        public EndpointMetricsSnapshot Snapshot()
        {
            long[] copy;
            lock (_gate)
            {
                copy = _samples.ToArray();
            }

            if (copy.Length == 0)
            {
                return new EndpointMetricsSnapshot
                {
                    Method = Method,
                    Route = Route,
                    TotalRequests = TotalRequests,
                    TotalErrors = TotalErrors,
                    WindowCount = 0,
                    ErrorRatePercent = 0,
                    AvgMs = 0,
                    P50Ms = 0,
                    P95Ms = 0,
                    P99Ms = 0,
                    MaxMs = 0,
                    LastSeenAtTs = Interlocked.Read(ref _lastSeenAtTs)
                };
            }

            Array.Sort(copy);
            var total = TotalRequests;
            var errors = TotalErrors;
            var errorRate = total > 0
                ? Math.Round((double)errors * 100d / total, 2)
                : 0.0;

            return new EndpointMetricsSnapshot
            {
                Method = Method,
                Route = Route,
                TotalRequests = total,
                TotalErrors = errors,
                WindowCount = copy.Length,
                ErrorRatePercent = errorRate,
                AvgMs = (long)Math.Round(copy.Average()),
                P50Ms = Percentile(copy, 0.50),
                P95Ms = Percentile(copy, 0.95),
                P99Ms = Percentile(copy, 0.99),
                MaxMs = copy[^1],
                LastSeenAtTs = Interlocked.Read(ref _lastSeenAtTs)
            };
        }

        private static long Percentile(long[] sortedValues, double percentile)
        {
            if (sortedValues.Length == 0) return 0;
            var rank = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
            rank = Math.Clamp(rank, 0, sortedValues.Length - 1);
            return sortedValues[rank];
        }
    }
}
