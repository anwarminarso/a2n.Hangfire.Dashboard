using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using a2n.Hangfire.Dashboard.Services.Prometheus;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property test for independent enablement of the core-hosted integrations
/// (<see cref="PrometheusMetricsEndpoint"/> and <see cref="ExportEndpoint"/>).
///
/// Feature: integrations-v2-6, Property 18: Independent enablement
///
/// **Property 18: Independent enablement** — for any subset of the integrations selected to be
/// enabled, each integration's endpoint SHALL be present (its <c>TryHandleAsync</c> handles a request
/// to its own path) <b>iff</b> that integration is enabled, and absent otherwise. Enabling one
/// integration SHALL NOT cause another to respond.
///
/// **Validates: Requirements 15.1, 15.2**
///
/// <para>Scoping note: only the two <em>core-hosted</em> integrations are exercised here —
/// Prometheus (metrics endpoint) and Export (export endpoint). The OpenTelemetry job filters and the
/// REST API live in SEPARATE packages that this core test project does not reference; their
/// independent opt-in is covered by tasks 2.6 and 10.8 respectively.</para>
///
/// <para>Construction: the generator emits an on/off toggle for each core-hosted integration. For the
/// toggled subset the test builds a single <see cref="DashboardUIOptions"/> with
/// <c>Prometheus.Enabled</c> and <c>Export.Enabled</c> set accordingly, then drives the internal
/// endpoints via a <see cref="DefaultHttpContext"/> whose <c>RequestServices</c> provides a
/// <see cref="HangfireMonitorService"/> (backed by a mocked <see cref="IMonitoringApi"/>) and a fake
/// <see cref="IStorageQueryProvider"/> so a handled request can proceed. The connection remote IP is
/// loopback so the Prometheus <c>LocalOnly</c> mode passes, and dashboard authorization is configured
/// to pass so the export gate is not the thing under test.</para>
/// </summary>
public class IndependentEnablementProperties
{
    private const string MetricsPath = "/metrics";
    private const string ExportPath = "/export";

    // ── Fakes / stubs ───────────────────────────────────────────────────────────────────────────

    /// <summary>Deterministic dashboard-authorization filter (always passes here).</summary>
    private sealed class AllowFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(HttpContext context) => true;
    }

    /// <summary>Fake query provider returning a small, fixed dataset for the export path.</summary>
    private sealed class FakeQueryProvider : IStorageQueryProvider
    {
        private readonly IReadOnlyList<JobSummaryDto> _backing;
        public FakeQueryProvider(IReadOnlyList<JobSummaryDto> backing) => _backing = backing;

        public Task<PagedResult<JobSummaryDto>> GetJobsWithFilterAsync(
            JobFilterCriteria criteria, int page, int pageSize, CancellationToken ct)
        {
            var skip = (page - 1) * pageSize;
            var items = skip >= _backing.Count
                ? Array.Empty<JobSummaryDto>()
                : _backing.Skip(skip).Take(pageSize).ToArray();

            return Task.FromResult(new PagedResult<JobSummaryDto>
            {
                Items = items,
                TotalCount = _backing.Count,
                Page = page,
                PageSize = pageSize,
            });
        }

        public Task<PagedResult<JobSummaryDto>> GetJobsByTagAsync(string tag, int page, int pageSize, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<TagCountDto>> GetTagCloudAsync(CancellationToken ct)
            => throw new NotImplementedException();

        public Task<PagedResult<JobSummaryDto>> GetJobsByStateAsync(string stateName, int page, int pageSize, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private static List<JobSummaryDto> BuildBacking() =>
        Enumerable.Range(0, 3)
            .Select(i => new JobSummaryDto
            {
                JobId = "job-" + i.ToString("D3"),
                JobName = "Namespace.Type.Method",
                State = "Succeeded",
                Queue = "default",
                CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                LastStateChange = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i + 1),
                DurationMs = i * 1.5,
                LatencyMs = i * 0.25,
            })
            .ToList();

    private static IServiceProvider BuildServices()
    {
        // HangfireMonitorService backed by a mocked IMonitoringApi so the Prometheus exporter can
        // collect real metric text without a live storage.
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

        var connection = new Mock<JobStorageConnection>();

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(api.Object);
        storage.Setup(s => s.GetConnection()).Returns(connection.Object);
        storage.Setup(s => s.GetReadOnlyConnection()).Returns(connection.Object);

        var monitor = new HangfireMonitorService(storage.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(monitor);
        services.AddSingleton<IStorageQueryProvider>(new FakeQueryProvider(BuildBacking()));
        return services.BuildServiceProvider();
    }

    private static DashboardUIOptions BuildOptions(bool prometheusEnabled, bool exportEnabled)
    {
        var options = new DashboardUIOptions
        {
            // Deterministically-passing dashboard authorization so the export gate is not the
            // variable under test (independent enablement is).
            Authorization = new IDashboardAuthorizationFilter[] { new AllowFilter() },
            AsyncAuthorization = Array.Empty<IDashboardAsyncAuthorizationFilter>(),
        };

        options.Prometheus.Enabled = prometheusEnabled;
        options.Prometheus.Path = MetricsPath;
        options.Prometheus.AuthorizationMode = PrometheusAuthorization.LocalOnly;

        options.Export.Enabled = exportEnabled;
        options.Export.Path = ExportPath;

        return options;
    }

    private static DefaultHttpContext BuildContext(string path, string queryString)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = BuildServices(),
        };
        ctx.Request.Method = "GET";
        ctx.Request.Path = new PathString(path);
        if (!string.IsNullOrEmpty(queryString))
            ctx.Request.QueryString = new QueryString(queryString);

        // Loopback remote so the Prometheus LocalOnly mode passes.
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    // ── Generator: an on/off toggle per core-hosted integration ──────────────────────────────────

    /// <summary>(prometheusEnabled, exportEnabled) over the full {on,off}² subset.</summary>
    private static Arbitrary<(bool prometheus, bool export)> ToggleArb =>
        Arb.From(
            from prometheus in Arb.Generate<bool>()
            from export in Arb.Generate<bool>()
            select (prometheus, export));

    // ── Property ─────────────────────────────────────────────────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property EachEndpoint_Present_IffEnabled_AndIndependent()
    {
        return Prop.ForAll(ToggleArb, toggle =>
        {
            var (prometheusEnabled, exportEnabled) = toggle;
            var options = BuildOptions(prometheusEnabled, exportEnabled);

            // 1) Prometheus endpoint on its own path → handled iff Prometheus is enabled.
            var metricsCtx = BuildContext(MetricsPath, queryString: null);
            var metricsHandled = PrometheusMetricsEndpoint
                .TryHandleAsync(metricsCtx, options).GetAwaiter().GetResult();

            if (metricsHandled != prometheusEnabled)
                return false.Label(
                    $"Prometheus@/metrics handled={metricsHandled}, expected {prometheusEnabled} " +
                    $"(prometheus={prometheusEnabled}, export={exportEnabled})");

            // 2) Export endpoint on its own path → handled iff Export is enabled.
            var exportCtx = BuildContext(ExportPath, queryString: "?format=csv");
            var exportHandled = ExportEndpoint
                .TryHandleAsync(exportCtx, options).GetAwaiter().GetResult();

            if (exportHandled != exportEnabled)
                return false.Label(
                    $"Export@/export handled={exportHandled}, expected {exportEnabled} " +
                    $"(prometheus={prometheusEnabled}, export={exportEnabled})");

            // 3) Independence: neither endpoint responds to the OTHER integration's path,
            //    regardless of enablement. This confirms enabling one does not cause the other
            //    (nor itself on a foreign path) to be handled.
            var prometheusOnExportPath = PrometheusMetricsEndpoint
                .TryHandleAsync(BuildContext(ExportPath, null), options).GetAwaiter().GetResult();
            if (prometheusOnExportPath)
                return false.Label(
                    $"Prometheus endpoint handled the export path (leaked cross-integration) " +
                    $"(prometheus={prometheusEnabled}, export={exportEnabled})");

            var exportOnMetricsPath = ExportEndpoint
                .TryHandleAsync(BuildContext(MetricsPath, null), options).GetAwaiter().GetResult();
            if (exportOnMetricsPath)
                return false.Label(
                    $"Export endpoint handled the metrics path (leaked cross-integration) " +
                    $"(prometheus={prometheusEnabled}, export={exportEnabled})");

            return true.ToProperty();
        });
    }
}
