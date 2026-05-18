using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace a2n.Hangfire.Dashboard.Middleware;

/// <summary>
/// Middleware that serves _framework/blazor.web.js and other framework scripts
/// within a branched (app.Map) pipeline. In a branched pipeline, the standard
/// static file/asset middleware cannot resolve framework scripts because they are
/// registered at the application root level. This middleware uses the WebRootFileProvider
/// and StaticWebAssets file provider to locate and serve these files.
/// </summary>
internal class FrameworkScriptMiddleware
{
    private readonly RequestDelegate _next;

    public FrameworkScriptMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Only handle _framework requests
        if (!path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (await TryServeFrameworkFileAsync(context, path))
            return;

        await _next(context);
    }

    /// <summary>
    /// Attempts to serve a framework file (e.g., _framework/blazor.web.js) from the
    /// web host environment's file providers. Returns true if the file was served.
    /// </summary>
    internal static async Task<bool> TryServeFrameworkFileAsync(HttpContext context, string path)
    {
        var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();

        // The WebRootFileProvider in development includes StaticWebAssets file provider
        // which can resolve _framework/blazor.web.js from the NuGet package cache
        var fileProvider = env.WebRootFileProvider;
        var fileInfo = fileProvider.GetFileInfo(path);

        if (fileInfo.Exists && !fileInfo.IsDirectory)
        {
            await ServeFileAsync(context, fileInfo, path);
            return true;
        }

        // Fallback: try without leading slash
        var relativePath = path.TrimStart('/');
        fileInfo = fileProvider.GetFileInfo(relativePath);

        if (fileInfo.Exists && !fileInfo.IsDirectory)
        {
            await ServeFileAsync(context, fileInfo, path);
            return true;
        }

        return false;
    }

    private static async Task ServeFileAsync(HttpContext context, IFileInfo fileInfo, string path)
    {
        var contentTypeProvider = new FileExtensionContentTypeProvider();
        if (!contentTypeProvider.TryGetContentType(path, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = fileInfo.Length;
        context.Response.Headers.CacheControl = "no-cache";

        await using var stream = fileInfo.CreateReadStream();
        await stream.CopyToAsync(context.Response.Body);
    }
}
