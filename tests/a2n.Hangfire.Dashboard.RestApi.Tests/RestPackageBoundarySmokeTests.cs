using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace a2n.Hangfire.Dashboard.RestApi.Tests;

/// <summary>
/// Smoke tests for the REST API package boundaries.
///
/// Feature: integrations-v2-6, task 10.8.
///
/// <para>
/// Validates the packaging/opt-in guarantees of the read-only REST API package
/// (<c>a2n.Hangfire.Dashboard.RestApi</c>):
/// </para>
/// <list type="number">
///   <item>
///     The REST API package is separate and NOT referenced by the core dashboard package: the core
///     assembly <c>a2n.Hangfire.Dashboard</c> does not reference the REST API package (nor the
///     OpenTelemetry package) — Req 12.1.
///   </item>
///   <item>
///     The opt-in registration call registers the expected services: after
///     <see cref="RestApiDashboardExtensions.AddHangfireDashboardRestApi"/> a
///     <see cref="RestApiOptions"/> singleton is resolvable — Req 12.2 (opt-in).
///   </item>
///   <item>
///     When the endpoint group is NOT mapped, the dashboard exposes no <c>/api/v1</c> endpoint:
///     a host that registers the services but never calls
///     <see cref="RestApiDashboardExtensions.MapHangfireDashboardRestApi"/> returns HTTP 404 for
///     <c>{prefix}/api/v1/jobs</c> — Req 12.3.
///   </item>
/// </list>
///
/// **Requirements: 12.1, 12.3**
/// </summary>
public class RestPackageBoundarySmokeTests
{
    private const string CoreAssemblyName = "a2n.Hangfire.Dashboard";
    private const string RestApiAssemblyName = "a2n.Hangfire.Dashboard.RestApi";
    private const string OpenTelemetryAssemblyName = "a2n.Hangfire.Dashboard.OpenTelemetry";

    // ── 1. The REST API package is separate and not referenced by core (Req 12.1) ────────────

    [Fact]
    public void CoreAssembly_DoesNotReference_RestApiPackage()
    {
        // The core dashboard assembly, obtained through a core public type.
        var coreAssembly = typeof(global::a2n.Hangfire.Dashboard.DashboardUIOptions).Assembly;

        Assert.Equal(CoreAssemblyName, coreAssembly.GetName().Name);

        var referencedNames = coreAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain(RestApiAssemblyName, referencedNames);
    }

    [Fact]
    public void CoreAssembly_DoesNotReference_OpenTelemetryPackage()
    {
        var coreAssembly = typeof(global::a2n.Hangfire.Dashboard.DashboardUIOptions).Assembly;

        var referencedNames = coreAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain(OpenTelemetryAssemblyName, referencedNames);
    }

    // ── 2. Opt-in registration registers the expected services (Req 12.2 opt-in) ──────────────

    [Fact]
    public void AddHangfireDashboardRestApi_RegistersRestApiOptionsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddHangfireDashboardRestApi();

        using var provider = services.BuildServiceProvider();

        var options = provider.GetService<RestApiOptions>();
        Assert.NotNull(options);

        // Registered as a singleton: the same instance is returned on repeated resolution.
        Assert.Same(options, provider.GetService<RestApiOptions>());
    }

    // ── 3. When unmapped, no /api/v1 endpoint exists (Req 12.3) ───────────────────────────────

    [Fact]
    public async Task WhenNotMapped_ApiEndpointReturns404()
    {
        const string pathPrefix = "/hangfire";

        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();

                    // Opt in to the REST API SERVICES but deliberately do NOT map the endpoint group.
                    services.AddHangfireDashboardRestApi();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        // A single unrelated endpoint so routing is active but /api/v1 stays unmapped.
                        endpoints.MapGet("/", () => "ok");
                    });
                });
            })
            .StartAsync();

        var client = host.GetTestServer().CreateClient();

        using var response = await client.GetAsync($"{pathPrefix}/api/v1/jobs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await host.StopAsync();
    }
}
