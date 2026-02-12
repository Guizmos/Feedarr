using System.Diagnostics;
using Feedarr.Api.Services.Diagnostics;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Feedarr.Api.Filters;

public sealed class ApiRequestMetricsFilter : IAsyncActionFilter
{
    private readonly ApiRequestMetricsService _metrics;

    public ApiRequestMetricsFilter(ApiRequestMetricsService metrics)
    {
        _metrics = metrics;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        var route = context.ActionDescriptor.AttributeRouteInfo?.Template
                    ?? context.HttpContext.Request.Path.Value
                    ?? "unknown";

        if (route.Equals("api/system/perf", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var executed = await next();
            var statusCode = executed.HttpContext.Response.StatusCode;
            if (executed.Exception is not null && !executed.ExceptionHandled)
                statusCode = StatusCodes.Status500InternalServerError;

            _metrics.Record(method, route, statusCode, sw.ElapsedMilliseconds);
        }
        catch
        {
            _metrics.Record(method, route, StatusCodes.Status500InternalServerError, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
