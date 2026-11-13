using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.Backup.Configuration;

// Once committed to staging, configuration must remain stable until startup applies it.
// Authentication and read-only recovery remain available through the usual guards.
public sealed class ConfigurationRestoreLockMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ConfigurationRestorePlanStore store)
    {
        if (store.BlocksSync && context.Request.Path.StartsWithSegments("/api") &&
            !RequestForgeryProtection.IsSafeMethod(context.Request.Method) &&
            !WebSessionSecurity.IsAuthEndpoint(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "configuration_restore_restart_required",
                message = "La restauration est confirmée. Redémarrez Feedarr puis vérifiez son application avant de modifier la configuration."
            });
            return;
        }
        await next(context);
    }
}
