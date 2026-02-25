using System.Text.RegularExpressions;

namespace Feedarr.Api.Middleware;

/// <summary>
/// Propagates a correlation ID through the request/response lifecycle.
///
/// Behaviour:
///   - If the incoming request carries an X-Correlation-ID header whose value
///     passes the whitelist ([A-Za-z0-9._-], ≤128 chars), that value is preserved.
///   - Values that contain control characters or non-whitelisted chars are
///     stripped; if the sanitized result is empty a new GUID is generated instead.
///   - The ID is always echoed back in the response via X-Correlation-ID.
///   - The ID is stored in HttpContext.Items[CorrelationIdKey] for access by
///     controllers and other middleware.
///   - A logging scope {CorrelationId} is opened on the constructor-injected
///     ILogger so the ID appears in all log entries emitted during the request.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string CorrelationIdKey = "X-Correlation-ID";
    private const string HeaderName      = "X-Correlation-ID";
    private const int    MaxIdLength     = 128; // guard against oversized upstream IDs

    // Whitelist: alphanumeric + dot, underscore, hyphen — safe for log files and HTTP headers.
    private static readonly Regex SafeChars =
        new(@"[^A-Za-z0-9._\-]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private readonly RequestDelegate           _next;
    private readonly ILogger<CorrelationIdMiddleware> _log;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> log)
    {
        _next = next;
        _log  = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context.Request.Headers[HeaderName].FirstOrDefault());

        // Make it available to controllers and downstream middleware.
        context.Items[CorrelationIdKey] = correlationId;

        // Echo back so clients (UI, scripts, reverse proxies) can correlate logs.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.TryAdd(HeaderName, correlationId);
            return Task.CompletedTask;
        });

        // Open a logging scope so the correlation ID appears in every log entry
        // emitted by any ILogger resolved from DI during this request.
        using (_log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context);
        }
    }

    private static string ResolveCorrelationId(string? incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
            return NewId();

        // Truncate first so the regex only scans a bounded string.
        if (incoming.Length > MaxIdLength)
            incoming = incoming[..MaxIdLength];

        var sanitized = SafeChars.Replace(incoming, string.Empty);
        return string.IsNullOrWhiteSpace(sanitized) ? NewId() : sanitized;
    }

    private static string NewId() => Guid.NewGuid().ToString("N"); // 32 lower-case hex chars
}
