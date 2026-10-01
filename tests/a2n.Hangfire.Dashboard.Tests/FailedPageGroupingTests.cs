using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using FailedPage = a2n.Hangfire.Dashboard.Components.Pages.Jobs.Failed;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// The Exception filter on the Failed page (#53): read-only grouping, loaded only when asked for.
/// </summary>
public class FailedPageGroupingTests : IDisposable
{
    private const string LockedKey = "v1:0123456789abcdef";
    private const string TimeoutKey = "v1:fedcba9876543210";

    private static readonly Job OrderJob = Job.FromExpression(() => FailureGroupingTests.OrderJobs.Run());

    private readonly BunitContext _ctx = new();
    private readonly Mock<IStorageQueryProvider> _provider = new();
    private readonly List<int> _requestedJobCounts = new();
    private FailureGroupsResult _groups;

    public FailedPageGroupingTests()
    {
        var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage, new JobFilterCollection());
        for (var i = 0; i < 3; i++)
            client.Create(OrderJob, new FailedState(new InvalidOperationException($"Order {i} is locked")));

        _groups = new FailureGroupsResult
        {
            Groups = new[]
            {
                new FailureGroupDto
                {
                    Fingerprint = LockedKey, Count = 42, StoredCount = 42, IsLabeled = true,
                    ExceptionType = "Contoso.Data.SaveChangesFailedException",
                    InnerExceptionType = "Microsoft.Data.SqlClient.SqlException",
                    NormalizedMessage = "Cannot insert duplicate key in object 'dbo.Orders'. The duplicate key value is (<n>).",
                    TopFrame = "Contoso.Jobs.OrderJob.Run",
                },
                new FailureGroupDto
                {
                    Fingerprint = TimeoutKey, Count = 7, StoredCount = 7, IsLabeled = true,
                    ExceptionType = "System.TimeoutException", InnerExceptionType = "",
                    NormalizedMessage = "The operation has timed out.", TopFrame = "Contoso.Jobs.SyncJob.Run",
                },
                new FailureGroupDto { Fingerprint = "v1:1111111111111111", Count = 1, StoredCount = 1 },
            },
        };

        _provider.SetupGet(p => p.SupportsFailureGrouping).Returns(true);
        _provider.Setup(p => p.GetFailureGroupsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _groups);
        _provider.Setup(p => p.GetFailureGroupJobsAsync(It.IsAny<FailureGroupDto>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FailureGroupDto group, int count, CancellationToken _) =>
            {
                _requestedJobCounts.Add(count);
                return Enumerable.Range(1, (int)Math.Min(count, group.Count))
                    .Select(i => new JobSummaryDto
                    {
                        JobId = (1000 - i).ToString(),
                        JobName = "OrderJobs.Run",
                        State = "Failed",
                        LastStateChange = DateTime.UtcNow.AddMinutes(-i),
                        ExceptionType = group.ExceptionType,
                        ExceptionMessage = "Cannot insert duplicate key",
                        InvocationData = InvocationData.SerializeJob(OrderJob),
                    })
                    .ToList();
            });

        var options = new DashboardUIOptions();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(options);
        _ctx.Services.AddSingleton(new HangfireMonitorService(storage, null, options, new JobMethodResolver()));
        _ctx.Services.AddSingleton(_provider.Object);
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<FailedPage> RenderPage()
    {
        var cut = _ctx.Render<FailedPage>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0, TestTimeouts.RenderWait);
        return cut;
    }

    private void Navigate(string uri) => _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);

    // ClickAsync waits for the page's handler to finish, so the next lookup sees its final render.
    private static async Task OpenFilter(IRenderedComponent<FailedPage> cut)
    {
        await cut.Find("#exception-filter").ClickAsync(new MouseEventArgs());
        cut.WaitForState(() => cut.FindAll(".hf-exception-option").Count > 0, TestTimeouts.RenderWait);
    }

    private static async Task SelectGroup(IRenderedComponent<FailedPage> cut, int index)
    {
        await OpenFilter(cut);
        await cut.FindAll(".hf-exception-option")[index].ClickAsync(new MouseEventArgs());
        cut.WaitForState(() => cut.FindAll("[data-testid=group-total]").Count > 0 && GroupRowCount(cut) > 0, TestTimeouts.RenderWait);
    }

    private static int GroupRowCount(IRenderedComponent<FailedPage> cut)
        => cut.FindAll("tbody tr:not(.hf-row-detail)").Count;

    private void VerifyNoGrouping()
        => _provider.Verify(p => p.GetFailureGroupsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public void DefaultView_IsTheFailedList_AndRunsNoGroupingQuery()
    {
        var cut = RenderPage();

        Assert.Equal("All exceptions", cut.Find("#exception-filter").TextContent.Trim());
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Requeue"));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Delete"));
        Assert.Equal(3, cut.FindAll("tbody input[type=checkbox]").Count);
        Assert.NotEmpty(cut.FindAll("th button[title='Sort by Failed']"));
        VerifyNoGrouping();
    }

    [Fact]
    public async Task Focus_LoadsTheGroupsOnce()
    {
        var cut = RenderPage();

        await cut.Find("#exception-filter").FocusAsync(new FocusEventArgs());
        await cut.Find("#exception-filter").ClickAsync(new MouseEventArgs());
        cut.WaitForState(() => cut.FindAll(".hf-exception-option").Count > 0, TestTimeouts.RenderWait);

        _provider.Verify(p => p.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, It.IsAny<CancellationToken>()), Times.Once);
        // Only labeled groups are listed; the rest are summarized.
        Assert.Equal(2, cut.FindAll(".hf-exception-option").Count);
        Assert.Contains("1 more exception with fewer failures", cut.Markup);
    }

    [Fact]
    public async Task Entry_ShowsOuterAndInnerShortNames_WithFullNamesInTheTooltip()
    {
        var cut = RenderPage();
        await OpenFilter(cut);

        var entry = cut.FindAll(".hf-exception-option")[0];
        Assert.Contains("SaveChangesFailedException → SqlException", entry.TextContent);
        Assert.Equal("Contoso.Data.SaveChangesFailedException → Microsoft.Data.SqlClient.SqlException", entry.GetAttribute("title"));
        Assert.Contains("The duplicate key value is (&lt;n&gt;).", entry.InnerHtml);
        Assert.Contains("Contoso.Jobs.OrderJob.Run", entry.TextContent);
        Assert.Contains("42", entry.TextContent);

        var single = cut.FindAll(".hf-exception-option")[1];
        Assert.DoesNotContain("→", single.TextContent);
        Assert.Contains("TimeoutException", single.TextContent);
    }

    [Fact]
    public async Task SelectingAGroup_ShowsItsTotal_AndItsNewestTenJobs_WithoutActions()
    {
        var cut = RenderPage();

        await SelectGroup(cut, 0);

        Assert.Equal("42 failed jobs", cut.Find("[data-testid=group-total]").TextContent.Trim());
        Assert.Equal(10, GroupRowCount(cut));
        Assert.Equal(new[] { 10 }, _requestedJobCounts);
        Assert.Empty(cut.FindAll("tbody input[type=checkbox]"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Requeue"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Delete"));
        Assert.Equal(10, cut.FindAll("tbody td.hf-job-cell").Count);
        Assert.Contains("SaveChangesFailedException → SqlException", cut.Find("#exception-filter").TextContent);
        Assert.EndsWith($"jobs/failed?exception={LockedKey}", _ctx.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task PageSize_ChangesHowManyJobsOfTheGroupAreShown()
    {
        var cut = RenderPage();
        await SelectGroup(cut, 0);

        await cut.Find("select[aria-label='Items per page']").ChangeAsync(new ChangeEventArgs { Value = "20" });
        cut.WaitForState(() => GroupRowCount(cut) == 20, TestTimeouts.RenderWait);

        Assert.Equal(new[] { 10, 20 }, _requestedJobCounts);
        Assert.Empty(cut.FindAll("nav .pagination"));
    }

    [Fact]
    public async Task SelectingAllExceptions_ReturnsToTheList()
    {
        var cut = RenderPage();
        await SelectGroup(cut, 1);

        await OpenFilter(cut);
        await cut.FindAll(".hf-exception-filter [role=option]").First(o => o.TextContent.Trim() == "All exceptions").ClickAsync(new MouseEventArgs());

        cut.WaitForState(() => cut.FindAll("tbody input[type=checkbox]").Count == 3, TestTimeouts.RenderWait);
        Assert.Empty(cut.FindAll("[data-testid=group-total]"));
    }

    [Fact]
    public async Task PartialResult_SaysSo_AndShowsCountsAsLowerBounds()
    {
        _groups.UnfingerprintedCount = 1500;
        _groups.FallbackCount = JobFilterCriteria.CountLimit;
        var cut = RenderPage();

        await SelectGroup(cut, 0);

        var notice = cut.Find("[data-testid=partial-notice]").TextContent;
        Assert.Contains("latest 1,000 of", notice);
        Assert.Contains("1,500", notice);
        Assert.Contains("UseDashboardFailureFingerprintFilter()", notice);
        Assert.Equal("42+ failed jobs", cut.Find("[data-testid=group-total]").TextContent.Trim());
    }

    [Fact]
    public async Task CompleteResult_HasNoNotice()
    {
        var cut = RenderPage();
        await OpenFilter(cut);

        Assert.Empty(cut.FindAll("[data-testid=partial-notice]"));
    }

    [Fact]
    public void ExceptionInTheQueryString_OpensThatGroup()
    {
        Navigate($"jobs/failed?exception={TimeoutKey}");

        var cut = _ctx.Render<FailedPage>();
        cut.WaitForState(() => cut.FindAll("[data-testid=group-total]").Count > 0 && GroupRowCount(cut) > 0, TestTimeouts.RenderWait);

        Assert.Equal("7 failed jobs", cut.Find("[data-testid=group-total]").TextContent.Trim());
        Assert.Equal(7, GroupRowCount(cut));
    }

    [Theory]
    [InlineData("V1:0123456789ABCDEF")]
    [InlineData("v0:0123456789abcdef")]
    [InlineData("not-an-exception")]
    public void InvalidExceptionInTheQueryString_IsIgnored(string value)
    {
        Navigate($"jobs/failed?exception={Uri.EscapeDataString(value)}");

        var cut = RenderPage();

        Assert.Equal(3, cut.FindAll("tbody input[type=checkbox]").Count);
        Assert.Equal("All exceptions", cut.Find("#exception-filter").TextContent.Trim());
        VerifyNoGrouping();
    }

    [Fact]
    public void ProviderWithoutGrouping_HidesTheFilter()
    {
        _provider.SetupGet(p => p.SupportsFailureGrouping).Returns(false);
        Navigate($"jobs/failed?exception={LockedKey}");

        var cut = RenderPage();

        Assert.Empty(cut.FindAll("#exception-filter"));
        Assert.Equal(3, cut.FindAll("tbody input[type=checkbox]").Count);
        VerifyNoGrouping();
    }

    [Fact]
    public async Task GroupingTimeout_IsReported_AndTheListKeepsWorking()
    {
        _ctx.Services.GetRequiredService<DashboardUIOptions>().SearchTimeoutSeconds = 1;
        _provider.Setup(p => p.GetFailureGroupsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return _groups;
            });
        var cut = RenderPage();

        await cut.Find("#exception-filter").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Contains("took longer than 1 second ", cut.Markup), TestTimeouts.RenderWait);
        Assert.Equal(3, cut.FindAll("tbody input[type=checkbox]").Count);
    }
}
