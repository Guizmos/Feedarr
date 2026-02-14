using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Feedarr.Api.Services.Security;

/// <summary>
/// Defense-in-depth: ensures the request was validated by BasicAuthMiddleware.
/// If the middleware is accidentally removed from the pipeline, controllers
/// decorated with [ServiceFilter(typeof(RequireAuthFilter))] will reject the request.
/// </summary>
public sealed class RequireAuthFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.HttpContext.Items.ContainsKey(BasicAuthMiddleware.AuthPassedKey))
        {
            context.Result = new ObjectResult(new { type = "https://tools.ietf.org/html/rfc9110#section-15.5.2", title = "unauthorized", status = 401 })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
