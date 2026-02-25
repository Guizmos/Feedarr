using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;

namespace Feedarr.Api.Filters;

public sealed class ApiErrorNormalizationFilter : IAsyncResultFilter
{
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "error", "message", "title", "detail", "status", "type", "instance"
    };

    // Caches readable public properties per error-object Type.
    // Avoids repeated reflection on every 4xx response (GetProperties allocates a new array each call).
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropCache = new();

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult objectResult && objectResult.Value is not null)
        {
            var statusCode = objectResult.StatusCode ?? context.HttpContext.Response.StatusCode;
            if (statusCode >= StatusCodes.Status400BadRequest &&
                objectResult.Value is not ProblemDetails &&
                TryNormalize(objectResult.Value, statusCode, out var problem))
            {
                context.Result = new ObjectResult(problem) { StatusCode = statusCode };
            }
        }

        await next();
    }

    private static bool TryNormalize(object value, int statusCode, out ProblemDetails problem)
    {
        if (value is string text)
        {
            problem = new ProblemDetails
            {
                Status = statusCode,
                Title = string.IsNullOrWhiteSpace(text) ? ReasonPhrases.GetReasonPhrase(statusCode) : text
            };
            return true;
        }

        var values = ReadProperties(value);
        if (values.Count == 0)
        {
            problem = default!;
            return false;
        }

        var title = FirstNonEmpty(values, "title")
                 ?? FirstNonEmpty(values, "error")
                 ?? FirstNonEmpty(values, "message")
                 ?? ReasonPhrases.GetReasonPhrase(statusCode);

        var detail = FirstNonEmpty(values, "detail")
                  ?? FirstNonEmpty(values, "message");

        problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = string.Equals(detail, title, StringComparison.OrdinalIgnoreCase) ? null : detail
        };

        foreach (var (key, rawValue) in values)
        {
            if (rawValue is null || ReservedKeys.Contains(key))
                continue;

            var extensionKey = char.ToLowerInvariant(key[0]) + key[1..];
            problem.Extensions[extensionKey] = rawValue;
        }

        return true;
    }

    private static Dictionary<string, object?> ReadProperties(object value)
    {
        var type  = value.GetType();
        var props = PropCache.GetOrAdd(type, static t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .Where(p => p.CanRead)
             .ToArray());

        var map = new Dictionary<string, object?>(props.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var prop in props)
            map[prop.Name] = prop.GetValue(value);

        return map;
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
            _ => null
        };
    }
}
