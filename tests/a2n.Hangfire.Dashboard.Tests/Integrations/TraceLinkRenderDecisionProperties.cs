using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Shared;
using a2n.Hangfire.Dashboard.Services;
using DetailsPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Details;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property test for the Job Details distributed-trace link render decision.
///
/// Feature: integrations-v2-6, Property 4: Trace-link render decision.
///
/// **Property 4: Trace-link render decision** — for any combination of (a
/// <see cref="DashboardUIOptions.TraceLinkBuilder"/> configured or not) and (a stored
/// <c>otel.traceparent</c> that is present-and-parseable, present-but-malformed, or absent), the Job
/// Details page renders the "View distributed trace →" link **iff** a builder is configured AND a
/// parseable traceparent is present; otherwise it omits the link.
///
/// The page (<c>Components/Pages/Jobs/Details.razor</c>) is rendered with bUnit against a real
/// <see cref="HangfireMonitorService"/> whose <see cref="JobStorage"/> dependency is mocked so
/// <c>GetJobDetails</c> returns a crafted <see cref="JobDetailsDto"/>. The heavier child components
/// (Tags, dependency graph, console, labels, confirm modal, relative-time) are replaced with bUnit
/// stubs so the render exercises only the page's own trace-link logic.
///
/// **Validates: Requirements 3.1, 3.2, 3.3**
/// </summary>
public class TraceLinkRenderDecisionProperties
{
    private const string TraceParentKey = "otel.traceparent";
    private const string LinkText = "View distributed trace";

    /// <summary>Which flavour of stored traceparent the scenario exercises.</summary>
    private enum TraceParentKind { Valid, Malformed, Absent }

    private static readonly char[] LowercaseHex = "0123456789abcdef".ToCharArray();

    /// <summary>Clearly non-parseable traceparent values (all fail <c>W3CTraceParent.TryParse</c>).</summary>
    private static readonly string[] MalformedValues =
    {
        "not-a-traceparent",
        "00-xyz",
        "zzzzzzzz",
        // Correct length/shape but all-zero trace-id → invalid per the W3C grammar.
        "00-00000000000000000000000000000000-0000000000000001-01",
        // Reserved version ff → invalid.
        "ff-1234567890abcdef1234567890abcdef-1234567890abcdef-01",
        "0000-1111-2222",
    };

    private static Gen<string> HexStringGen(int length) =>
        Gen.ArrayOf(length, Gen.Elements(LowercaseHex))
            .Select(chars => new string(chars))
            .Where(s => s.Any(c => c != '0'));

    /// <summary>A valid, parseable W3C traceparent: version 00, non-zero ids, sampled flag.</summary>
    private static Gen<string> ValidTraceParentGen =>
        from traceId in HexStringGen(32)
        from parentId in HexStringGen(16)
        select $"00-{traceId}-{parentId}-01";

    private sealed record Scenario(bool HasBuilder, TraceParentKind Kind, string StoredValue)
    {
        // The link is rendered iff a builder is configured AND a parseable traceparent is present.
        public bool ExpectLink => HasBuilder && Kind == TraceParentKind.Valid;
    }

    private static Arbitrary<Scenario> ScenarioArb =>
        Arb.From(
            from hasBuilder in Gen.Elements(true, false)
            from kind in Gen.Elements(TraceParentKind.Valid, TraceParentKind.Malformed, TraceParentKind.Absent)
            from validTp in ValidTraceParentGen
            from malformed in Gen.Elements(MalformedValues)
            select new Scenario(
                hasBuilder,
                kind,
                kind switch
                {
                    TraceParentKind.Valid => validTp,
                    TraceParentKind.Malformed => malformed,
                    _ => null,
                }));

    [Property(MaxTest = 100)]
    public Property RendersTraceLink_IffBuilderConfiguredAndTraceparentParseable()
    {
        return Prop.ForAll(ScenarioArb, scenario =>
        {
            var rendered = RenderAndDetectLink(scenario);
            return (rendered == scenario.ExpectLink)
                .Label($"hasBuilder={scenario.HasBuilder}, kind={scenario.Kind}, " +
                       $"stored='{scenario.StoredValue}', expected={scenario.ExpectLink}, rendered={rendered}");
        });
    }

    /// <summary>
    /// Renders the Job Details page for the given scenario and reports whether the distributed-trace
    /// link is present in the produced markup.
    /// </summary>
    private static bool RenderAndDetectLink(Scenario scenario)
    {
        var properties = new Dictionary<string, string>();
        if (scenario.Kind != TraceParentKind.Absent)
        {
            properties[TraceParentKey] = scenario.StoredValue;
        }

        var jobDetails = new JobDetailsDto
        {
            Job = null,
            CreatedAt = DateTime.UtcNow,
            Properties = properties,
            History = new List<StateHistoryDto>
            {
                new StateHistoryDto
                {
                    StateName = "Succeeded",
                    Reason = null,
                    CreatedAt = DateTime.UtcNow,
                    Data = new Dictionary<string, string>(),
                },
            },
        };

        var monitoringApi = new Mock<IMonitoringApi>();
        monitoringApi.Setup(m => m.JobDetails(It.IsAny<string>())).Returns(jobDetails);

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(monitoringApi.Object);

        var options = new DashboardUIOptions();
        if (scenario.HasBuilder)
        {
            options.TraceLinkBuilder = TraceLinkBuilders.Template("https://traces.example.com/view?id={traceId}");
        }

        var monitor = new HangfireMonitorService(storage.Object, audit: null, options: options, resolver: null);

        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(monitor);
        ctx.Services.AddSingleton(options);

        // Replace child components that carry their own service/JS dependencies with inert stubs so
        // the render exercises only the page's own trace-link decision.
        ctx.ComponentFactories.AddStub<TagsViewer>();
        ctx.ComponentFactories.AddStub<JobGraphViewer>();
        ctx.ComponentFactories.AddStub<ConsoleViewer>();
        ctx.ComponentFactories.AddStub<ServerLabel>();
        ctx.ComponentFactories.AddStub<WorkerLabel>();
        ctx.ComponentFactories.AddStub<RelativeTime>();
        ctx.ComponentFactories.AddStub<ConfirmModal>();

        var cut = ctx.RenderComponent<DetailsPage>(p => p.Add(c => c.JobId, "job-1"));

        // OnInitializedAsync loads the job on a background task; wait until the page is past its
        // loading state before inspecting the markup.
        cut.WaitForState(
            () => cut.Markup.Contains("State History", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        return cut.Markup.Contains(LinkText, StringComparison.Ordinal);
    }
}
