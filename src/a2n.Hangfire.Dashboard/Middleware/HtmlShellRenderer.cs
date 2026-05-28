using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace a2n.Hangfire.Dashboard.Middleware;

/// <summary>
/// Renders the initial HTML document (shell) that bootstraps the Blazor Server application.
/// All resource paths are relative to the configured Path_Prefix.
/// </summary>
internal class HtmlShellRenderer
{
    /// <summary>
    /// Renders the HTML shell document to the response.
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <param name="pathPrefix">The path prefix where the dashboard is mounted</param>
    /// <param name="options">Dashboard UI configuration options</param>
    public async Task RenderAsync(HttpContext context, string pathPrefix, DashboardUIOptions options)
    {
        context.Response.ContentType = "text/html";

        // Generate antiforgery token
        var antiforgeryHtml = string.Empty;
        var antiforgery = context.RequestServices.GetService<IAntiforgery>();
        if (antiforgery != null)
        {
            var tokenSet = antiforgery.GetAndStoreTokens(context);
            antiforgeryHtml = $"""<input name="{tokenSet.FormFieldName}" type="hidden" value="{tokenSet.RequestToken}" />""";
        }

        var title = options?.DashboardTitle ?? "Hangfire Dashboard";

        // Normalize pathPrefix: ensure it starts with / and has no trailing slash
        var prefix = pathPrefix.TrimEnd('/');

        // Resolve favicon: use custom path if configured, otherwise point to host app's root favicon
        var faviconHref = !string.IsNullOrEmpty(options?.FaviconPath)
            ? options.FaviconPath
            : "/favicon.ico";
        var faviconHtml = $"""<link rel="icon" href="{faviconHref}" />""";

        var html = $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{title}</title>
                <base href="{prefix}/" />
                <!-- Bootstrap CSS -->
                <link rel="stylesheet" href="{prefix}/_content/lib/bootstrap/css/bootstrap.min.css" />
                <!-- Bootstrap Icons -->
                <link rel="stylesheet" href="{prefix}/_content/lib/bootstrap-icons/bootstrap-icons.min.css" />
                <!-- Custom overrides (console viewer, charts) -->
                <link rel="stylesheet" href="{prefix}/_content/css/app.css" />
                {faviconHtml}
            </head>
            <body>
                {antiforgeryHtml}
                <script src="{prefix}/_content/js/theme.js"></script>
                <script src="{prefix}/_content/js/nav.js"></script>
                <script src="{prefix}/_content/js/moment.min.js"></script>
                <script src="{prefix}/_content/js/chart.umd.min.js"></script>
                <script src="{prefix}/_content/js/chartjs-adapter-moment.min.js"></script>
                <script src="{prefix}/_content/js/chartjs-plugin-streaming.min.js"></script>
                <script src="{prefix}/_content/js/charts.js"></script>
                <script src="{prefix}/_content/js/search-presets.js"></script>
                <div id="app">Loading...</div>
                <script src="{prefix}/_content/lib/bootstrap/js/bootstrap.bundle.min.js"></script>
                <script src="{prefix}/_framework/blazor.web.js"></script>
            </body>
            </html>
            """;

        await context.Response.WriteAsync(html);
    }
}
