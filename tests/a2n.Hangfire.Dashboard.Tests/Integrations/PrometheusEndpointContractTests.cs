using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Middleware;
using a2n.Hangfire.Dashboard.Services;
using a2n.Hangfire.Dashboard.Services.Prometheus;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Unit tests for the Prometheus metrics endpoint contracts
/// (<see cref="PrometheusMetricsEndpoint"/> and <see cref="PrometheusOptions"/>).
///
/// Feature: integrations-v2-6, Task 6.4 — metrics endpoint contracts.
///
/// Covers:
/// <list type="bullet">
///   <item><description>Content-Type header exact value on a successful (authorized) response (Req 5.2).</description></item>
///   <item><description>Default authorization mode is <see cref="PrometheusAuthorization.LocalOnly"/> and it
///   denies a non-local request (401) while allowing a loopback request (200) (Req 8.2).</description></item>
///   <item><description>Configuring dedicated scraper authorization leaves the dashboard-page auth filters
///   unchanged (Req 8.4).</description></item>
///   <item><description>Weakening auth requires an explicit option — the unconfigured default is
///   <see cref="PrometheusAuthorization.LocalOnly"/>, not anonymous (Req 17.4).</description></item>
/// </list>
///
/// _Requirements: 5.2, 8.2, 8.4, 17.4_
/// </summary>
public class PrometheusEndpointContractTests
{
    private const string ExpectedContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>
    /// Builds an <see cref="HttpContext"/> whose request targets the metrics path, whose
    /// <see cref="HttpContext.RequestServices"/> can render metrics (a
    /// <see cref="HangfireMonitorService"/> backed by a mocked <see cref="IMonitoringApi"/>), and
    /// whose response body is captured in a <see cref="MemoryStream"/>.
    /// </summary>
    private static DefaultHttpContext BuildContext(
        IPAddress? remoteIp,
        IPAddress? localIp,
        out MemoryStream body,
        string path = "/metrics")
    {
        var stats = new StatisticsDto
        {
            Succeeded = 10,
            Enqueued = 2,
            Scheduled = 1,
            Processing = 0,
            Failed = 0,
            Deleted = 0,
        };

        var api = new Mock<IMonitoringApi>();
        api.Setup(m => m.GetStatistics()).Returns(stats);
        api.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>
        {
            new() { Name = "default", Length = 2 },
        });
        api.Setup(m => m.Servers()).Returns(new List<ServerDto>());

        // Loose JobStorageConnection mock: the GetRecurringJobCount storage extension resolves to
        // 0 on a loose mock without throwing.
        var connection = new Mock<JobStorageConnection>();

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(api.Object);
        storage.Setup(s => s.GetConnection()).Returns(connection.Object);
        storage.Setup(s => s.GetReadOnlyConnection()).Returns(connection.Object);

        var monitor = new HangfireMonitorService(storage.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(monitor);
        var sp = services.BuildServiceProvider();

        body = new MemoryStream();

        var context = new DefaultHttpContext();
        context.RequestServices = sp;
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = body;
        context.Connection.RemoteIpAddress = remoteIp;
        context.Connection.LocalIpAddress = localIp;

        return context;
    }

    private static DashboardUIOptions EnabledOptions(
        PrometheusAuthorization mode = PrometheusAuthorization.LocalOnly,
        IEnumerable<IDashboardAuthorizationFilter>? scraperAuth = null)
    {
        var options = new DashboardUIOptions();
        options.Prometheus.Enabled = true;
        options.Prometheus.AuthorizationMode = mode;
        options.Prometheus.ScraperAuthorization = scraperAuth;
        return options;
    }

    // ----------------------------------------------------------------------------------------
    // 1. Content-Type header exact value on a successful (authorized) response (Req 5.2).
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AuthorizedResponse_SetsExactPrometheusContentType()
    {
        // Loopback request → allowed under the default LocalOnly mode.
        var context = BuildContext(IPAddress.Loopback, localIp: null, out var body);
        var options = EnabledOptions();

        var handled = await PrometheusMetricsEndpoint.TryHandleAsync(context, options);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(ExpectedContentType, context.Response.ContentType);

        // Body actually rendered metric content.
        Assert.True(body.Length > 0);
    }

    // ----------------------------------------------------------------------------------------
    // 2. Default LocalOnly denies non-local (401), allows loopback (200) (Req 8.2).
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task DefaultLocalOnly_DeniesNonLocalRequest_With401AndEmptyBody()
    {
        // Remote (non-loopback) address distinct from the local address → denied.
        var remote = IPAddress.Parse("203.0.113.5");
        var local = IPAddress.Parse("10.0.0.1");
        var context = BuildContext(remote, local, out var body);
        var options = EnabledOptions(); // default AuthorizationMode = LocalOnly

        var handled = await PrometheusMetricsEndpoint.TryHandleAsync(context, options);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        // 401 with no body — no metric values emitted.
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public async Task DefaultLocalOnly_AllowsLoopbackRequest_With200()
    {
        var context = BuildContext(IPAddress.Loopback, localIp: null, out var body);
        var options = EnabledOptions(); // default AuthorizationMode = LocalOnly

        var handled = await PrometheusMetricsEndpoint.TryHandleAsync(context, options);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(body.Length > 0);
    }

    // ----------------------------------------------------------------------------------------
    // 3. Scraper-auth configuration leaves dashboard-page auth filters unchanged (Req 8.4).
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void ConfiguringScraperAuth_DoesNotChangeDashboardPageAuthFilters()
    {
        var options = new DashboardUIOptions();

        // Capture the dashboard-page auth filters before configuring scraper auth.
        var originalAuthorization = options.Authorization;
        var originalAsyncAuthorization = options.AsyncAuthorization;

        // Configure dedicated scraper authorization independently.
        var scraperFilters = new IDashboardAuthorizationFilter[] { new AllowAllScraperFilter() };
        options.Prometheus.AuthorizationMode = PrometheusAuthorization.Custom;
        options.Prometheus.ScraperAuthorization = scraperFilters;

        // The scraper config is stored on the Prometheus options only...
        Assert.Equal(PrometheusAuthorization.Custom, options.Prometheus.AuthorizationMode);
        Assert.Same(scraperFilters, options.Prometheus.ScraperAuthorization);

        // ...and the dashboard-page auth filters are entirely unaffected.
        Assert.Same(originalAuthorization, options.Authorization);
        Assert.Same(originalAsyncAuthorization, options.AsyncAuthorization);
    }

    [Fact]
    public async Task CustomScraperAuth_GatesTheEndpoint_IndependentlyOfDashboardAuth()
    {
        // A remote request that the dashboard-page LocalOnly filter would deny is allowed here
        // solely because the dedicated scraper filter authorizes it — proving independence.
        var context = BuildContext(IPAddress.Parse("203.0.113.9"), IPAddress.Parse("10.0.0.1"), out var body);
        var options = EnabledOptions(
            PrometheusAuthorization.Custom,
            new IDashboardAuthorizationFilter[] { new AllowAllScraperFilter() });

        var handled = await PrometheusMetricsEndpoint.TryHandleAsync(context, options);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(body.Length > 0);
    }

    // ----------------------------------------------------------------------------------------
    // 4. Weakening auth requires an explicit option — default is LocalOnly, not anonymous (Req 17.4).
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void DefaultAuthorizationMode_IsLocalOnly()
    {
        Assert.Equal(PrometheusAuthorization.LocalOnly, new PrometheusOptions().AuthorizationMode);
        Assert.Equal(PrometheusAuthorization.LocalOnly, new DashboardUIOptions().Prometheus.AuthorizationMode);
    }

    /// <summary>A trivial scraper filter that authorizes every request.</summary>
    private sealed class AllowAllScraperFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(HttpContext context) => true;
    }
}
