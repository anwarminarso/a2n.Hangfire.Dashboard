using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components;
using a2n.Hangfire.Dashboard.Hubs;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Middleware;
using a2n.Hangfire.Dashboard.Services;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// Uses GenericQueryProvider as fallback when no storage adapter is configured.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddHangfireDashboardUI(this IServiceCollection services)
    {
        return services.AddHangfireDashboardUI(configure: null);
    }

    /// <summary>
    /// Adds required services for the Hangfire Dashboard UI with storage adapter configuration.
    /// Registers all internal services and allows configuring a storage adapter for optimized
    /// queries and analytics via the <see cref="DashboardStorageOptionsBuilder"/>.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Action to configure storage adapter (optional). When null, GenericQueryProvider is used as fallback.</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddHangfireDashboardUI(
        this IServiceCollection services,
        Action<DashboardStorageOptionsBuilder> configure)
    {
        // Register default DashboardUIOptions (can be replaced by UseHangfireDashboardUI)
        services.AddSingleton(new DashboardUIOptions());

        services.AddHttpContextAccessor();

        // Register Blazor Server interactive components.
        // AddRazorComponents() is idempotent — safe to call even if host app already called it.
        // AddInteractiveServerComponents() registers the render mode provider required by
        // .AddInteractiveServerRenderMode() in the endpoint mapping.
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Note: We do NOT call AddSignalR() here because the host app may already register it
        // with custom options (e.g., AddJsonProtocol). SignalR services are additive and
        // the host app is responsible for calling AddSignalR() if it uses SignalR elsewhere.
        // If the host app does NOT use SignalR, AddInteractiveServerComponents() above
        // already registers the necessary SignalR services for Blazor Server circuits.

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
            var queryProvider = sp.GetService<IStorageQueryProvider>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<SearchService>();
            return new SearchService(storage, tagsReader, queryProvider, logger);
        });

        services.AddHostedService<MetricsBroadcastService>();

        // Storage adapter configuration
        var builder = new DashboardStorageOptionsBuilder(services);
        configure?.Invoke(builder);

        // If no query provider was registered, use GenericQueryProvider fallback
        if (!builder.HasQueryProvider)
        {
            services.AddScoped<IStorageQueryProvider>(sp =>
            {
                var storage = sp.GetRequiredService<JobStorage>();
                var tagsReader = sp.GetRequiredService<TagsDataReader>();
                return new GenericQueryProvider(storage, tagsReader);
            });
        }

        // Register AnalyticsService (checks IStorageMetricsProvider availability at runtime)
        services.AddScoped<AnalyticsService>();

        // Register AnalyticsBroadcastService only if metrics provider is available
        if (builder.HasMetricsProvider)
        {
            services.AddHostedService<AnalyticsBroadcastService>();
        }

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

        // Update the DI-registered singleton so Blazor components receive the same options
        var registered = app.ApplicationServices.GetService<DashboardUIOptions>();
        if (registered != null && !ReferenceEquals(registered, options))
        {
            registered.DashboardTitle = options.DashboardTitle;
            registered.AppPath = options.AppPath;
            registered.StatsPollingInterval = options.StatsPollingInterval;
            registered.IsReadOnly = options.IsReadOnly;
            registered.DefaultRecordsPerPage = options.DefaultRecordsPerPage;
            registered.DefaultTheme = options.DefaultTheme;
            registered.Authorization = options.Authorization;
        }

        // Normalize pathMatch to ensure it starts with / and has no trailing slash
        pathMatch = "/" + pathMatch.Trim('/');

        // app.Map creates a branched pipeline that ONLY handles requests starting with pathMatch.
        // Inside the branch, Request.PathBase = original PathBase + pathMatch,
        // and Request.Path = the remainder after stripping pathMatch.
        // This ensures the dashboard doesn't interfere with other routes in the host app.
        app.Map(pathMatch, branch =>
        {
            // WebSockets must be enabled in the branch for Blazor Server SignalR circuit.
            // The host app may not have UseWebSockets() or it may be registered after this point.
            branch.UseWebSockets();

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
