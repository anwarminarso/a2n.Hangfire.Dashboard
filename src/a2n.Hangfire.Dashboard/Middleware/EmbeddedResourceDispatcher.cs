using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace a2n.Hangfire.Dashboard.Middleware;

internal static class EmbeddedResourceDispatcher
{
    private static readonly Assembly ResourceAssembly = typeof(EmbeddedResourceDispatcher).Assembly;

    public static async Task ServeResourceAsync(HttpContext context, string resourcePath)
    {
        if (!ResourceRegistry.TryGetResource(resourcePath, out var info))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Check If-None-Match for conditional response (304 Not Modified)
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == info.ETag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        await using var stream = ResourceAssembly.GetManifestResourceStream(info.ResourceName);
        if (stream == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = info.ContentType;
        context.Response.Headers.CacheControl = "max-age=31536000";
        context.Response.Headers.ETag = info.ETag;

        await stream.CopyToAsync(context.Response.Body);
    }
}
