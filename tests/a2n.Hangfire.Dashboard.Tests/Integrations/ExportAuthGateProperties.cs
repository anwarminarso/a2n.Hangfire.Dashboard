using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Middleware;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services.Export;
using Microsoft.AspNetCore.Http;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property tests for the export endpoint's authorization gate.
///
/// Feature: integrations-v2-6, Property 17: Export authorization gate
///
/// **Property 17** — for any export request, records SHALL be streamed <b>iff</b> the request passes
/// <c>Dashboard_Authorization</c>; when authorization fails the endpoint SHALL respond with HTTP 401
/// and stream no records. The gate is enforced <em>before</em> any record is streamed, so a failing
/// request can never emit Hangfire data.
///
/// **Validates: Requirements 14.1, 14.2, 17.1, 17.2**
///
/// <para>Approach: the generator varies (a) whether <c>Dashboard_Authorization</c> passes — modeled by
/// a single <see cref="IDashboardAuthorizationFilter"/> whose <c>Authorize</c> returns the generated
/// boolean, exactly what <c>DashboardAuthorization.IsAuthorizedAsync</c> consumes — and (b) the export
/// format (<c>csv</c>/<c>json</c>). A <see cref="DefaultHttpContext"/> is driven through the internal
/// <see cref="ExportEndpoint.TryHandleAsync"/> with a fake <see cref="IStorageQueryProvider"/> that
/// yields a few records, and the response status, body, and headers are asserted.</para>
/// </summary>
public class ExportAuthGateProperties
{
    // ── Authorization filter modeling a deterministic pass / fail decision ──────────────────────

    private sealed class FixedAuthorizationFilter : IDashboardAuthorizationFilter
    {
        private readonly bool _authorized;
        public FixedAuthorizationFilter(bool authorized) => _authorized = authorized;
        public bool Authorize(HttpContext context) => _authorized;
    }

    // ── Fake storage query provider returning a small, fixed dataset ────────────────────────────

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

            var result = new PagedResult<JobSummaryDto>
            {
                Items = items,
                TotalCount = _backing.Count,
                Page = page,
                PageSize = pageSize,
            };
            return Task.FromResult(result);
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

    /// <summary>
    /// Minimal <see cref="IServiceProvider"/> exposing only the query provider; anything else (e.g.
    /// an optional <c>ILoggerFactory</c>) resolves to <c>null</c>, which the endpoint tolerates.
    /// </summary>
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly IStorageQueryProvider _provider;
        public SingleServiceProvider(IStorageQueryProvider provider) => _provider = provider;

        public object GetService(Type serviceType)
            => serviceType == typeof(IStorageQueryProvider) ? _provider : null;
    }

    // ── Generators ──────────────────────────────────────────────────────────────────────────────

    /// <summary>(authorized?, useJson?) — varies the auth decision and the export format.</summary>
    private static Arbitrary<(bool authorized, bool useJson)> ScenarioArb =>
        Arb.From(
            from authorized in Arb.Generate<bool>()
            from useJson in Arb.Generate<bool>()
            select (authorized, useJson));

    private static List<JobSummaryDto> BuildBacking() =>
        Enumerable.Range(0, 5)
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

    private static DefaultHttpContext BuildContext(bool authorized, bool useJson, out MemoryStream body)
    {
        var provider = new FakeQueryProvider(BuildBacking());
        body = new MemoryStream();

        var context = new DefaultHttpContext
        {
            RequestServices = new SingleServiceProvider(provider),
        };
        context.Request.Method = "GET";
        context.Request.Path = new PathString("/export");
        context.Request.QueryString = new QueryString(useJson ? "?format=json" : "?format=csv");
        context.Response.Body = body;

        return context;
    }

    private static DashboardUIOptions BuildOptions(bool authorized)
    {
        var options = new DashboardUIOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new FixedAuthorizationFilter(authorized) },
            AsyncAuthorization = Array.Empty<IDashboardAsyncAuthorizationFilter>(),
        };
        options.Export.Enabled = true;
        options.Export.Path = "/export";
        return options;
    }

    // ── Property ────────────────────────────────────────────────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property Export_StreamsRecords_IffAuthorized()
    {
        return Prop.ForAll(ScenarioArb, scenario =>
        {
            var (authorized, useJson) = scenario;

            var context = BuildContext(authorized, useJson, out var body);
            var options = BuildOptions(authorized);

            var handled = ExportEndpoint.TryHandleAsync(context, options).GetAwaiter().GetResult();

            var bodyLength = body.Length;
            var hasContentDisposition =
                context.Response.Headers.TryGetValue("Content-Disposition", out var disposition)
                && disposition.ToString().Contains("attachment", StringComparison.OrdinalIgnoreCase);

            bool ok;
            if (authorized)
            {
                // 200 + records streamed (non-empty body) + attachment header.
                ok = handled
                     && context.Response.StatusCode == StatusCodes.Status200OK
                     && bodyLength > 0
                     && hasContentDisposition;
            }
            else
            {
                // 401 + no records streamed (empty body) + no attachment header.
                ok = handled
                     && context.Response.StatusCode == StatusCodes.Status401Unauthorized
                     && bodyLength == 0
                     && !hasContentDisposition;
            }

            return ok.Label(
                $"authorized={authorized} useJson={useJson} handled={handled} " +
                $"status={context.Response.StatusCode} bodyLength={bodyLength} " +
                $"attachment={hasContentDisposition}");
        });
    }
}
