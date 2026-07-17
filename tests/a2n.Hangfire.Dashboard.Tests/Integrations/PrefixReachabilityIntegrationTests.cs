using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Integration test (task 11.3) proving the Prometheus metrics endpoint and the CSV/JSON export
/// endpoint are reachable relative to a NON-DEFAULT dashboard <c>Path_Prefix</c> (Req 5.5, 16.1).
///
/// <para>
/// The host is built exactly like a real application would build it: real Hangfire InMemory storage
/// is registered as <see cref="JobStorage"/>, <c>AddHangfireDashboardUI()</c> registers all internal
/// services (including the <c>GenericQueryProvider</c> fallback that lets export stream), and
/// <c>UseHangfireDashboardUI("/ops/hangfire", options)</c> mounts the whole dashboard branch under a
/// non-default prefix. Because both endpoints are handled inside the mapped branch by
/// <c>DashboardMiddleware</c> (before Blazor routing), mounting the branch at <c>/ops/hangfire</c> is
/// what makes them answer at <c>/ops/hangfire/metrics</c> and <c>/ops/hangfire/export</c> — i.e. the
/// prefix-relativity the requirements demand.
/// </para>
///
/// <para>
/// Authorization: Prometheus uses its default <see cref="Services.Prometheus.PrometheusAuthorization.LocalOnly"/>
/// mode. TestServer requests carry no connection <c>RemoteIpAddress</c>, which the LocalOnly gate
/// treats as local, so the scrape passes. The export endpoint is gated by <c>Dashboard_Authorization</c>,
/// so <see cref="DashboardUIOptions.Authorization"/> is set to a filter that authorizes the request.
/// </para>
/// </summary>
public class PrefixReachabilityIntegrationTests
{
    private const string Prefix = "/ops/hangfire";

    /// <summary>Authorization filter that authorizes every request (used to gate the export endpoint).</summary>
    private sealed class AllowAllFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(HttpContext context) => true;
    }

    /// <summary>
    /// Builds an in-memory ASP.NET Core host that mounts the real dashboard (Prometheus + export
    /// enabled) under the non-default <see cref="Prefix"/> using the production
    /// <c>AddHangfireDashboardUI</c> / <c>UseHangfireDashboardUI</c> wiring.
    /// </summary>
    private static async Task<IHost> BuildHostAsync()
    {
        return await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    // Real Hangfire InMemory storage — the dashboard resolves JobStorage from DI.
                    services.AddSingleton<JobStorage>(new InMemoryStorage());

                    // Registers HangfireMonitorService (used by the Prometheus exporter) and the
                    // GenericQueryProvider fallback for IStorageQueryProvider (used by export).
                    services.AddHangfireDashboardUI();
                });
                webBuilder.Configure(app =>
                {
                    var options = new DashboardUIOptions
                    {
                        // Export is gated by Dashboard_Authorization — authorize the TestServer request.
                        Authorization = new IDashboardAuthorizationFilter[] { new AllowAllFilter() },
                        AsyncAuthorization = Array.Empty<IDashboardAsyncAuthorizationFilter>(),
                    };

                    // Prometheus /metrics enabled; LocalOnly (default) passes for loopback TestServer.
                    options.Prometheus.Enabled = true;

                    // CSV / JSON export enabled.
                    options.Export.Enabled = true;

                    app.UseHangfireDashboardUI(Prefix, options);
                });
            })
            .StartAsync();
    }

    [Fact]
    public async Task MetricsEndpoint_IsReachableUnderNonDefaultPrefix()
    {
        // Req 5.5 / 16.1: the metrics endpoint answers relative to the configured Path_Prefix.
        using var host = await BuildHostAsync();
        using var client = host.GetTestServer().CreateClient();

        using var response = await client.GetAsync(Prefix + "/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Prometheus text exposition format 0.0.4 content type (Req 5.2).
        var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
        Assert.Contains("text/plain", contentType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("version=0.0.4", contentType, StringComparison.OrdinalIgnoreCase);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("hangfire_", body);
    }

    [Fact]
    public async Task ExportEndpoint_IsReachableUnderNonDefaultPrefix()
    {
        // Req 5.5 / 16.1: the export endpoint answers relative to the configured Path_Prefix.
        using var host = await BuildHostAsync();
        using var client = host.GetTestServer().CreateClient();

        using var response = await client.GetAsync(Prefix + "/export?format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Content-Disposition attachment header set before streaming (Req 13.5). The provider may
        // return zero records on empty InMemory storage — that is fine, it is still 200 + attachment.
        Assert.NotNull(response.Content.Headers.ContentDisposition);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task Endpoints_AreNotServedWithoutThePrefix()
    {
        // Prefix-relativity: the same paths WITHOUT the dashboard prefix are not served by the
        // dashboard branch (the branch only handles requests under Path_Prefix). With no other
        // handler mapped, these fall through to a 404.
        using var host = await BuildHostAsync();
        using var client = host.GetTestServer().CreateClient();

        using var metricsResponse = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.NotFound, metricsResponse.StatusCode);

        using var exportResponse = await client.GetAsync("/export?format=csv");
        Assert.Equal(HttpStatusCode.NotFound, exportResponse.StatusCode);
    }
}
