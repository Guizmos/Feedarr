namespace Feedarr.Api.Services.Security;

internal static class ConfigurationSetupRoutes
{
    internal static bool Allows(HttpRequest request)
    {
        const string root="/api/setup/configuration-restore/";
        var path=request.Path.Value??"";
        if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))return false;
        var tail=path[root.Length..];
        if(HttpMethods.IsGet(request.Method))return tail=="state";
        if(HttpMethods.IsPost(request.Method) && tail is "prepare" or "complete")return true;
        var parts=tail.Split('/');
        return Guid.TryParseExact(parts[0],"N",out _) &&
            (HttpMethods.IsDelete(request.Method) && parts.Length==1 ||
             HttpMethods.IsPost(request.Method) && parts.Length==2 && parts[1]=="confirm");
    }
}
