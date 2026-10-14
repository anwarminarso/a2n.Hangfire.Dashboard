using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace a2n.Hangfire.Dashboard.Middleware;

/// <summary>
/// Middleware that serves _framework/blazor.web.js and other framework scripts
/// within a branched (app.Map) pipeline. In a branched pipeline, the standard
/// static file/asset middleware cannot resolve framework scripts because they are
/// registered at the application root level. This middleware uses the WebRootFileProvider
/// and StaticWebAssets file provider to locate and serve these files.
/// </summary>
/// <remarks>
/// On .NET 10 the host only has <c>blazor.web.js</c> when it has a <c>.razor</c> file or sets
/// <c>RequiresAspNetWebAssets</c>; the package can't set that for it. The net10.0 build of the
/// dashboard therefore carries a copy, served only when the host doesn't serve its own, so the
/// dashboard still goes interactive. The host's copy is preferred because it matches its runtime.
/// </remarks>
internal class FrameworkScriptMiddleware
{
    internal const string BlazorWebScriptPath = "/_framework/blazor.web.js";
    internal const string BundledBlazorWebScriptResource = "a2n.Hangfire.Dashboard.Framework.blazor.web.js";

    private readonly RequestDelegate _next;

    public FrameworkScriptMiddleware(RequestDelegate next, IWebHostEnvironment env = null,
        ILogger<FrameworkScriptMiddleware> logger = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        if (env is not null)
            ReportScriptSource(env.WebRootFileProvider, logger ?? NullLogger<FrameworkScriptMiddleware>.Instance);
    }

    /// <summary>The version of the bundled <c>blazor.web.js</c>, or null when this build has none (net8.0, net9.0).</summary>
    internal static string BundledBlazorWebScriptVersion { get; } = typeof(FrameworkScriptMiddleware).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BundledBlazorWebScriptVersion")?.Value;

    internal static bool HasBundledBlazorWebScript { get; } = typeof(FrameworkScriptMiddleware).Assembly
        .GetManifestResourceInfo(BundledBlazorWebScriptResource) is not null;

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

        if (await TryServeBundledBlazorWebScriptAsync(context, path))
            return;

        await _next(context);
    }

    /// <summary>
    /// Says once, at startup, where <c>blazor.web.js</c> will come from: the host, the dashboard's
    /// bundled copy (.NET 10), or nowhere, in which case the dashboard renders but never goes interactive.
    /// </summary>
    internal static void ReportScriptSource(IFileProvider webRoot, ILogger logger)
    {
        if (webRoot?.GetFileInfo(BlazorWebScriptPath.TrimStart('/')) is { Exists: true, IsDirectory: false })
            return;

        if (HasBundledBlazorWebScript)
        {
            logger.LogInformation(
                "The host doesn't serve _framework/blazor.web.js, so the dashboard serves its bundled copy (ASP.NET Core {Version}). " +
                "To serve the host's own copy, which matches its runtime, add <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets> to the host project.",
                BundledBlazorWebScriptVersion);
            return;
        }

        logger.LogWarning(
            "The host doesn't serve _framework/blazor.web.js, so the dashboard pages will render but not respond. " +
            "When running from a build output outside the Development environment, run the published output or call builder.WebHost.UseStaticWebAssets().");
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
        if (fileProvider is null)
            return false;

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

    /// <summary>Serves the bundled <c>blazor.web.js</c> (net10.0 builds only). Returns true if it was served.</summary>
    internal static async Task<bool> TryServeBundledBlazorWebScriptAsync(HttpContext context, string path)
    {
        if (!HasBundledBlazorWebScript || !string.Equals(path, BlazorWebScriptPath, StringComparison.OrdinalIgnoreCase))
            return false;

        await using var stream = typeof(FrameworkScriptMiddleware).Assembly.GetManifestResourceStream(BundledBlazorWebScriptResource);
        if (stream is null)
            return false;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/javascript";
        context.Response.ContentLength = stream.Length;
        context.Response.Headers.CacheControl = "no-cache";
        await stream.CopyToAsync(context.Response.Body);
        return true;
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
