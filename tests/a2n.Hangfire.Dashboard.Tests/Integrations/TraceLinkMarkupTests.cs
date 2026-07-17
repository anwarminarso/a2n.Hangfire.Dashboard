using System;
using System.Collections.Generic;
using Bunit;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using DetailsPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Details;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Unit test for the distributed-trace link markup rendered on the Job Details page
/// (<c>Components/Pages/Jobs/Details.razor</c>).
///
/// Feature: integrations-v2-6, Task 3.5 — link markup.
///
/// The Job Details page renders a "View distributed trace →" link when a
/// <see cref="DashboardUIOptions.TraceLinkBuilder"/> is configured AND the job carries a parseable
/// W3C <c>otel.traceparent</c> parameter. This test asserts the rendered anchor opens the trace in a
/// new tab safely — carrying <c>target="_blank"</c> and <c>rel="noopener"</c> — and that its
/// <c>href</c> equals the builder's output (Req 3.5).
///
/// <para>
/// <b>Setup.</b> A real <see cref="HangfireMonitorService"/> is registered over a mocked
/// <see cref="JobStorage"/> whose <c>IMonitoringApi.JobDetails</c> returns a crafted
/// <see cref="JobDetailsDto"/> with a non-empty <c>History</c> and a valid W3C traceparent in
/// <c>Properties["otel.traceparent"]</c>. A <see cref="DashboardUIOptions"/> with a configured
/// <c>TraceLinkBuilder</c> is registered, plus the services the child components of the page need
/// to render (<see cref="TagsDataReader"/>, <see cref="JobGraphService"/>).
/// </para>
/// </summary>
public class TraceLinkMarkupTests
{
    private const string ValidTraceparent =
        "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    private const string ExpectedTraceId = "0af7651916cd43dd8448eb211c80319c";

    private static JobDetailsDto CraftJobDetails() => new()
    {
        Job = null,
        CreatedAt = DateTime.UtcNow.AddMinutes(-5),
        Properties = new Dictionary<string, string>
        {
            ["otel.traceparent"] = ValidTraceparent,
        },
        History = new List<StateHistoryDto>
        {
            new()
            {
                StateName = "Succeeded",
                Reason = "Job completed",
                CreatedAt = DateTime.UtcNow.AddMinutes(-4),
                Data = new Dictionary<string, string>(),
            },
        },
    };

    private static TestContext NewContext(DashboardUIOptions options)
    {
        // Mock the monitoring API so GetJobDetails returns our crafted DTO.
        var monitoringApi = new Mock<IMonitoringApi>();
        monitoringApi.Setup(m => m.JobDetails(It.IsAny<string>())).Returns(CraftJobDetails());

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(monitoringApi.Object);
        // GetReadOnlyConnection() is left unmocked (returns null): TagsDataReader treats a non
        // JobStorageConnection as "no tags", so the TagsViewer child renders nothing.

        var monitor = new HangfireMonitorService(storage.Object, null, options, new JobMethodResolver());

        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton(monitor);
        ctx.Services.AddSingleton(new TagsDataReader(storage.Object));
        ctx.Services.AddSingleton(new JobGraphService(monitor));
        return ctx;
    }

    [Fact]
    public void TraceLink_Opens_In_New_Tab_With_Noopener_And_Builder_Href()
    {
        var options = new DashboardUIOptions
        {
            TraceLinkBuilder = context => "https://trace/" + context.TraceId,
        };
        var expectedHref = "https://trace/" + ExpectedTraceId;

        using var ctx = NewContext(options);

        var cut = ctx.RenderComponent<DetailsPage>(parameters =>
            parameters.Add(p => p.JobId, "123"));

        // The trace link is the only anchor rendered by the page for this fixture.
        var link = cut.Find("a[target]");

        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("noopener", link.GetAttribute("rel"));
        Assert.Equal(expectedHref, link.GetAttribute("href"));
    }
}
