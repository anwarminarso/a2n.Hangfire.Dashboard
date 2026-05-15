using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components;
using a2n.Hangfire.Dashboard.Hubs;
using a2n.Hangfire.Dashboard.Middleware;
using a2n.Hangfire.Dashboard.Services;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for registering the Hangfire Dashboard UI (Blazor-based).
/// </summary>
public static class HangfireDashboardUIExtensions
{
    /// <summary>
    /// Adds required services for the Hangfire Dashboard UI.
    /// Registers all internal services: HangfireMonitorService, ConsoleDataReader,
    /// TagsDataReader, SearchService, MetricsBroadcastService, SignalR, and Blazor Server components.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddHangfireDashboardUI(this IServiceCollection services)
    {
        // Register default DashboardUIOptions (can be replaced by UseHangfireDashboardUI)
        services.AddSingleton(new DashboardUIOptions());

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
    /// Maps the Hangfire Dashboard UI at the specified path.
    /// Uses app.Map() to create an isolated branched pipeline so the dashboard
    /// only responds to requests under the specified path prefix.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <param name="pathMatch">The path prefix for the dashboard (default: "/hangfire")</param>
    /// <param name="options">Dashboard UI configuration options (optional)</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseHangfireDashboardUI(
        this IApplicationBuilder app,
        string pathMatch = "/hangfire",
        DashboardUIOptions options = null)
    {
        options ??= app.ApplicationServices.GetService<DashboardUIOptions>() ?? new DashboardUIOptions();

        // Normalize pathMatch to ensure it starts with / and has no trailing slash
        pathMatch = "/" + pathMatch.Trim('/');

        // app.Map creates a branched pipeline that ONLY handles requests starting with pathMatch.
        // Inside the branch, Request.PathBase = original PathBase + pathMatch,
        // and Request.Path = the remainder after stripping pathMatch.
        // This ensures the dashboard doesn't interfere with other routes in the host app.
        app.Map(pathMatch, branch =>
        {
            // DashboardMiddleware handles authorization, antiforgery, and embedded resources (_content/*)
            branch.UseMiddleware<DashboardMiddleware>(options);

            // FrameworkScriptMiddleware serves _framework/blazor.web.js from the static web assets
            // file provider. This is needed because MapRazorComponents registers the _framework
            // endpoint via MapStaticAssets which doesn't work correctly in a branched pipeline.
            branch.UseMiddleware<FrameworkScriptMiddleware>();

            branch.UseRouting();
            branch.UseAntiforgery();

            branch.UseEndpoints(endpoints =>
            {
                // Map DashboardHub SignalR endpoint within the branch
                endpoints.MapHub<DashboardHub>("/hubs/dashboard");

                // Map Blazor components — handles _blazor SignalR circuit and page rendering.
                // Note: _framework/blazor.web.js is served by FrameworkScriptMiddleware above.
                endpoints.MapRazorComponents<DashboardApp>()
                    .AddInteractiveServerRenderMode();
            });
        });

        return app;
    }

    /// <summary>
    /// Maps the Hangfire Dashboard UI middleware pipeline at the specified path,
    /// using an existing Hangfire DashboardOptions for backward compatibility.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <param name="pathMatch">The path prefix for the dashboard</param>
    /// <param name="hangfireOptions">Existing Hangfire DashboardOptions to map from</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseHangfireDashboardUI(
        this IApplicationBuilder app,
        string pathMatch,
        DashboardOptions hangfireOptions)
    {
        var options = DashboardUIOptions.FromDashboardOptions(hangfireOptions);
        return app.UseHangfireDashboardUI(pathMatch, options);
    }
}
