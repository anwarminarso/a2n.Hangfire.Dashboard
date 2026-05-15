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
    /// Maps the Hangfire Dashboard UI middleware pipeline at the specified path.
    /// Creates a branched pipeline that handles all dashboard requests including
    /// embedded resources, Blazor Server, SignalR, authorization, and antiforgery.
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

        app.Map(pathMatch, branch =>
        {
            // Register the DashboardMiddleware on the branched pipeline
            // It handles authorization, antiforgery, embedded resources, and routing
            branch.UseMiddleware<DashboardMiddleware>(options);

            // Static files must be enabled in the branch for _framework/blazor.web.js
            // (served from the ASP.NET Core shared framework assembly)
            branch.UseStaticFiles();

            branch.UseRouting();
            branch.UseAntiforgery();

            branch.UseEndpoints(endpoints =>
            {
                // Map DashboardHub SignalR endpoint within the branch
                endpoints.MapHub<DashboardHub>("/hubs/dashboard");

                // Map Blazor components (serves _framework/blazor.web.js, handles Blazor rendering,
                // and sets up the _blazor SignalR endpoint for interactive server components)
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
