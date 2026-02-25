namespace Feedarr.Api.Middleware;

/// <summary>
/// Propagates a correlation ID through the request/response lifecycle.
///
/// Behaviour:
///   - If the incoming request carries an X-Correlation-ID header, that value
///     is preserved as-is (supports upstream reverse proxies that inject IDs).
///   - Otherwise a new compact GUID (32 hex chars, no hyphens) is generated.
///   - The ID is always echoed back in the response via X-Correlation-ID.
///   - The ID is stored in HttpContext.Items[CorrelationIdKey] for access by
///     controllers and other middleware.
///   - A logging scope {CorrelationId} is opened on the request logger so the
///     ID appears in all log entries emitted during the request (works with any
///     ILogger backend that respects scopes, including the default console logger
///     in structured-output mode).
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string CorrelationIdKey = "X-Correlation-ID";
    private const string HeaderName = "X-Correlation-ID";
    private const int MaxIdLength = 128; // guard against oversized upstream IDs

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ILoggerFactory loggerFactory)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = !string.IsNullOrWhiteSpace(incoming) && incoming.Length <= MaxIdLength
            ? incoming
            : Guid.NewGuid().ToString("N"); // 32 lower-case hex chars, no hyphens

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
        var log = loggerFactory.CreateLogger("Feedarr.Request");
        using (log.BeginScope(new Dictionary<string, object>
               {
                   ["CorrelationId"] = correlationId
               }))
        {
            await _next(context);
        }
    }
}
