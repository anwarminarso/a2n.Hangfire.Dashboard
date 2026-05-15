using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components;
using a2n.Hangfire.Dashboard.Hubs;
using a2n.Hangfire.Dashboard.Services;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for registering the alternate Hangfire Dashboard (Blazor-based).
/// </summary>
public static class HangfireAlternateDashboardExtensions
{
    /// <summary>
    /// Adds required services for the alternate Hangfire Dashboard.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="options">Dashboard configuration options (optional)</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddHangfireAlternateDashboard(
        this IServiceCollection services,
        AlternateDashboardOptions options = null)
    {
        options ??= new AlternateDashboardOptions();

        services.AddSingleton(options);

        services.AddHttpContextAccessor();

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.AddSignalR();

        services.AddScoped<HangfireMonitorService>(sp =>
        {
            var storage = sp.GetRequiredService<JobStorage>();
            return new HangfireMonitorService(storage);
        });

        services.AddScoped<ConsoleDataReader>(sp =>
        {
            var storage = sp.GetRequiredService<JobStorage>();
            return new ConsoleDataReader(storage);
        });

        services.AddScoped<TagsDataReader>(sp =>
        {
            var storage = sp.GetRequiredService<JobStorage>();
            return new TagsDataReader(storage);
        });

        services.AddScoped<SearchService>(sp =>
        {
            var storage = sp.GetRequiredService<JobStorage>();
            var tagsReader = sp.GetRequiredService<TagsDataReader>();
            return new SearchService(storage, tagsReader);
        });

        services.AddHostedService<MetricsBroadcastService>();

        return services;
    }

    /// <summary>
    /// Adds required services for the alternate Hangfire Dashboard,
    /// using an existing Hangfire DashboardOptions for backward compatibility.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="hangfireOptions">Existing Hangfire DashboardOptions to map from</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddHangfireAlternateDashboard(
        this IServiceCollection services,
        DashboardOptions hangfireOptions)
    {
        var options = AlternateDashboardOptions.FromDashboardOptions(hangfireOptions);
        return services.AddHangfireAlternateDashboard(options);
    }

    /// <summary>
    /// Maps the alternate Hangfire Dashboard.
    /// Replaces app.UseHangfireDashboard() from the built-in dashboard.
    /// </summary>
    /// <param name="app">The web application</param>
    /// <param name="pathPrefix">The path prefix for the dashboard (default: "/")</param>
    /// <returns>The web application for chaining</returns>
    public static WebApplication UseHangfireAlternateDashboard(
        this WebApplication app,
        string pathPrefix = "/")
    {
        // Normalize path prefix
        pathPrefix = "/" + pathPrefix.Trim('/');
        if (pathPrefix == "/") pathPrefix = "";

        if (!string.IsNullOrEmpty(pathPrefix))
        {
            app.UsePathBase(pathPrefix);
        }

        app.UseStaticFiles();
        app.UseAntiforgery();

        // Authorization middleware for the dashboard
        var options = app.Services.GetRequiredService<AlternateDashboardOptions>();
        if (options.Authorization.Any())
        {
            app.Use(async (context, next) =>
            {
                // Only apply auth to dashboard routes (skip static files already served above)
                var path = context.Request.Path.Value ?? "";
                if (path.StartsWith("/hubs/") || path.StartsWith("/_blazor") || path == "/_framework/blazor.web.js"
                    || path.StartsWith("/_content/") || path.StartsWith("/css/") || path.StartsWith("/js/"))
                {
                    await next();
                    return;
                }

                foreach (var filter in options.Authorization)
                {
                    if (!filter.Authorize(context))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                await next();
            });
        }

        // SignalR hub for realtime metrics
        app.MapHub<DashboardHub>("/hubs/dashboard");

        // Blazor components
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }
}
