using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Middleware;
using a2n.Hangfire.Dashboard.Services;
using a2n.Hangfire.Dashboard.Services.Prometheus;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property test for the Prometheus metrics endpoint authorization gate
/// (<see cref="PrometheusMetricsEndpoint"/>).
///
/// Feature: integrations-v2-6, Property 10: Metrics endpoint authorization gate
///
/// **Property 10: Metrics endpoint authorization gate** — for any request to the metrics endpoint,
/// the response body contains metric values **iff** the request passes the configured metrics
/// authorization; when it fails, the endpoint responds with HTTP 401 and an empty body containing
/// no metric values.
///
/// **Validates: Requirements 8.1, 8.3, 17.1, 17.2**
///
/// <para>Construction: the test builds a <see cref="DefaultHttpContext"/> with a
/// <see cref="MemoryStream"/> response body, connection local/remote IPs that control locality,
/// <c>Request.Path</c> set to the configured Prometheus path, and <c>RequestServices</c> pointing at
/// a <see cref="ServiceCollection"/> that registers a <see cref="HangfireMonitorService"/> backed by
/// a mocked <see cref="IMonitoringApi"/> (so <c>CollectAsync</c> succeeds and emits real metric text).
/// The generator varies the <see cref="PrometheusAuthorization"/> mode and the request/config
/// characteristics that determine whether authorization passes; the expected pass/fail outcome is
/// computed by mirroring the endpoint's <c>IsAuthorizedAsync</c> logic.</para>
/// </summary>
public class PrometheusAuthGateProperties
{
    // Fixed addresses used to control the LocalOnly locality decision deterministically.
    private static readonly IPAddress LocalIp = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress RemoteEqualToLocal = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress RemoteLoopback = IPAddress.Loopback;
    private static readonly IPAddress RemoteDifferent = IPAddress.Parse("203.0.113.9");

    private const string MetricsPath = "/metrics";

    /// <summary>How the remote IP relates to the local IP for the LocalOnly authorization mode.</summary>
    public enum RemoteKind
    {
        Loopback,        // 127.0.0.1 → local → PASS
        EqualToLocal,    // equals the local IP → PASS
        Different        // a distinct remote IP → FAIL
    }

    /// <summary>How the custom scraper-authorization filter set is configured.</summary>
    public enum CustomKind
    {
        NullSet,   // ScraperAuthorization == null → DENY
        EmptySet,  // ScraperAuthorization == [] → DENY
        Filters    // one or more filters → PASS iff all authorize
    }

    /// <summary>A generated authorization scenario for the metrics endpoint.</summary>
    public sealed record Scenario(
        PrometheusAuthorization Mode,
        RemoteKind Remote,
        bool DashboardAuthorized,
        CustomKind Custom,
        bool[] CustomResults);

    private static Gen<Scenario> ScenarioGen =>
        from mode in Gen.Elements(
            PrometheusAuthorization.LocalOnly,
            PrometheusAuthorization.RequireDashboardAuth,
            PrometheusAuthorization.Custom)
        from remote in Gen.Elements(RemoteKind.Loopback, RemoteKind.EqualToLocal, RemoteKind.Different)
        from dashboardAuthorized in Arb.Generate<bool>()
        from customKind in Gen.Elements(CustomKind.NullSet, CustomKind.EmptySet, CustomKind.Filters)
        from filterCount in Gen.Choose(1, 3)
        from filterResults in Gen.ArrayOf(filterCount, Arb.Generate<bool>())
        select new Scenario(mode, remote, dashboardAuthorized, customKind, filterResults);

    private static Arbitrary<Scenario> ScenarioArb => Arb.From(ScenarioGen);

    /// <summary>A deterministic authorization filter returning a fixed result.</summary>
    private sealed class PredicateFilter : IDashboardAuthorizationFilter
    {
        private readonly bool _result;
        public PredicateFilter(bool result) => _result = result;
        public bool Authorize(HttpContext context) => _result;
    }

    private static IServiceProvider BuildServices()
    {
        var stats = new StatisticsDto
        {
            Succeeded = 42,
            Enqueued = 3,
            Scheduled = 1,
            Processing = 2,
            Failed = 0,
            Deleted = 5,
        };

        var api = new Mock<IMonitoringApi>();
        api.Setup(m => m.GetStatistics()).Returns(stats);
        api.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>
        {
            new() { Name = "default", Length = 3 },
        });
        api.Setup(m => m.Servers()).Returns(new List<ServerDto>
        {
            new()
            {
                Name = "server-0",
                WorkersCount = 10,
                StartedAt = DateTime.UtcNow,
                Heartbeat = DateTime.UtcNow,
                Queues = new List<string>(),
            },
        });

        // Loose connection mock: GetRecurringJobCount storage extension resolves to 0 without throwing.
        var connection = new Mock<JobStorageConnection>();

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(api.Object);
        storage.Setup(s => s.GetConnection()).Returns(connection.Object);
        storage.Setup(s => s.GetReadOnlyConnection()).Returns(connection.Object);

        var monitor = new HangfireMonitorService(storage.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(monitor);
        return services.BuildServiceProvider();
    }

    private static IPAddress RemoteAddress(RemoteKind kind) => kind switch
    {
        RemoteKind.Loopback => RemoteLoopback,
        RemoteKind.EqualToLocal => RemoteEqualToLocal,
        _ => RemoteDifferent,
    };

    private static DashboardUIOptions BuildOptions(Scenario scenario)
    {
        var options = new DashboardUIOptions();
        options.Prometheus.Enabled = true;
        options.Prometheus.Path = MetricsPath;
        options.Prometheus.AuthorizationMode = scenario.Mode;

        switch (scenario.Mode)
        {
            case PrometheusAuthorization.RequireDashboardAuth:
                // Override the default (LocalRequestsOnly) filter with a deterministic one.
                options.Authorization = new IDashboardAuthorizationFilter[]
                {
                    new PredicateFilter(scenario.DashboardAuthorized),
                };
                options.AsyncAuthorization = Array.Empty<IDashboardAsyncAuthorizationFilter>();
                break;

            case PrometheusAuthorization.Custom:
                options.Prometheus.ScraperAuthorization = scenario.Custom switch
                {
                    CustomKind.NullSet => null,
                    CustomKind.EmptySet => Array.Empty<IDashboardAuthorizationFilter>(),
                    _ => scenario.CustomResults
                        .Select(r => (IDashboardAuthorizationFilter)new PredicateFilter(r))
                        .ToArray(),
                };
                break;
        }

        return options;
    }

    /// <summary>Mirrors the endpoint's authorization decision to compute the expected outcome.</summary>
    private static bool ExpectedAuthorized(Scenario scenario) => scenario.Mode switch
    {
        PrometheusAuthorization.LocalOnly => scenario.Remote != RemoteKind.Different,
        PrometheusAuthorization.RequireDashboardAuth => scenario.DashboardAuthorized,
        PrometheusAuthorization.Custom => scenario.Custom == CustomKind.Filters
                                          && scenario.CustomResults.Length > 0
                                          && scenario.CustomResults.All(r => r),
        _ => false,
    };

    [Property(MaxTest = 100)]
    public Property MetricsEndpoint_EmitsMetrics_IffAuthorized()
    {
        return Prop.ForAll(ScenarioArb, scenario =>
        {
            var options = BuildOptions(scenario);
            var expected = ExpectedAuthorized(scenario);

            var ctx = new DefaultHttpContext
            {
                RequestServices = BuildServices(),
            };
            ctx.Request.Path = MetricsPath;
            ctx.Connection.LocalIpAddress = LocalIp;
            ctx.Connection.RemoteIpAddress = RemoteAddress(scenario.Remote);

            using var body = new MemoryStream();
            ctx.Response.Body = body;

            var handled = PrometheusMetricsEndpoint.TryHandleAsync(ctx, options).GetAwaiter().GetResult();

            var text = Encoding.UTF8.GetString(body.ToArray());
            var status = ctx.Response.StatusCode;

            // The endpoint is enabled and the path matches, so the request is always handled.
            if (!handled)
                return false.Label($"mode={scenario.Mode}: request was not handled");

            if (expected)
            {
                // Authorized → 200 with a non-empty body containing metric values.
                if (status != StatusCodes.Status200OK)
                    return false.Label($"mode={scenario.Mode} expected authorized: status={status}, expected 200");

                if (!text.Contains("hangfire_"))
                    return false.Label($"mode={scenario.Mode} expected authorized: body has no metric values. Body='{text}'");

                return true.ToProperty();
            }
            else
            {
                // Not authorized → 401 with an empty body and no metric values.
                if (status != StatusCodes.Status401Unauthorized)
                    return false.Label($"mode={scenario.Mode} expected unauthorized: status={status}, expected 401");

                if (text.Length != 0)
                    return false.Label($"mode={scenario.Mode} expected unauthorized: body not empty. Body='{text}'");

                if (text.Contains("hangfire_"))
                    return false.Label($"mode={scenario.Mode} expected unauthorized: body leaked metric values");

                return true.ToProperty();
            }
        });
    }
}
