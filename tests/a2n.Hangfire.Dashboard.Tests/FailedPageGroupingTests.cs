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
/// The Exception filter on the Failed page (#53): grouping, loaded only when asked for.
/// </summary>
public class FailedPageGroupingTests : IDisposable
{
    private const string LockedKey = "v1:0123456789abcdef";
    private const string TimeoutKey = "v1:fedcba9876543210";

    private static readonly Job OrderJob = Job.FromExpression(() => FailureGroupingTests.OrderJobs.Run());

    private readonly BunitContext _ctx = new();
    private readonly BackgroundJobClient _client;
    private readonly Mock<IStorageQueryProvider> _provider = new();
    private readonly List<int> _requestedJobCounts = new();
    private FailureGroupsResult _groups;

    public FailedPageGroupingTests()
    {
        var storage = new InMemoryStorage();
        _client = new BackgroundJobClient(storage, new JobFilterCollection());
        for (var i = 0; i < 3; i++)
            AddFailedJob();

        // As the SQL Server and PostgreSQL adapters return it.
        _groups = new FailureGroupsResult
        {
            UsesStoredFingerprints = true,
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
        _ctx.Services.AddDashboardPolling();
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

    private void AddFailedJob()
        => _client.Create(OrderJob, new FailedState(new InvalidOperationException("Order 7 is locked")));

    private int GroupingCalls()
        => _provider.Invocations.Count(i => i.Method.Name == nameof(IStorageQueryProvider.GetFailureGroupsAsync));

    private static Task KeyDown(IRenderedComponent<FailedPage> cut, string key)
        => cut.Find("#exception-filter").KeyDownAsync(new KeyboardEventArgs { Key = key });

    private static string ActiveDescendant(IRenderedComponent<FailedPage> cut)
        => cut.Find("#exception-filter").GetAttribute("aria-activedescendant");

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
    public async Task SelectingAGroup_ShowsItsTotal_AndItsNewestTenJobs()
    {
        var cut = RenderPage();

        await SelectGroup(cut, 0);

        Assert.Equal("42 failed jobs", cut.Find("[data-testid=group-total]").TextContent.Trim());
        Assert.Equal(10, GroupRowCount(cut));
        Assert.Equal(new[] { 10 }, _requestedJobCounts);
        // The actions are covered by FailedPageGroupActionsTests.
        Assert.Equal(10, cut.FindAll("tbody input[type=checkbox]").Count);
        Assert.Single(cut.FindAll("[data-testid=requeue-all]"));
        Assert.Equal(10, cut.FindAll("tbody td.hf-job-cell").Count);
        Assert.Contains("SaveChangesFailedException → SqlException", cut.Find("#exception-filter").TextContent);
        Assert.EndsWith($"jobs/failed?exception={LockedKey}", _ctx.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task AGroup_LinksToASearchForAllItsJobs()
    {
        var cut = RenderPage();
        Assert.Empty(cut.FindAll("[data-testid=search-exception]"));

        await SelectGroup(cut, 1);

        var link = cut.Find("[data-testid=search-exception]");
        Assert.Equal($"search?exception={TimeoutKey}", link.GetAttribute("href"));
        Assert.Contains("Search all jobs with this exception", link.TextContent);
        // Its failures have stored fingerprints, so Search can find them.
        Assert.Empty(cut.FindAll("[data-testid=search-exception-hint]"));
    }

    [Fact]
    public async Task AGroupWithoutStoredFingerprints_SaysSearchMayNotFindIt()
    {
        // Every failure recorded before the filter was registered: the Search panel leaves the group
        // out, and the link says why a search may come back empty.
        var timeout = _groups.Groups[1];
        timeout.StoredCount = 0;
        timeout.ComputedCount = timeout.Count;
        var cut = RenderPage();

        await SelectGroup(cut, 1);

        Assert.Single(cut.FindAll("[data-testid=search-exception]"));
        Assert.Contains("UseDashboardFailureFingerprintFilter()", cut.Find("[data-testid=search-exception-hint]").TextContent);
    }

    [Fact]
    public async Task OnTheGenericProvider_TheSearchHintIsNotShown()
    {
        // It never reads stored fingerprints, so StoredCount says nothing about what Search finds.
        _groups.UsesStoredFingerprints = false;
        foreach (var group in _groups.Groups)
        {
            group.ComputedCount = group.Count;
            group.StoredCount = 0;
        }
        var cut = RenderPage();

        await SelectGroup(cut, 1);

        Assert.Single(cut.FindAll("[data-testid=search-exception]"));
        Assert.Empty(cut.FindAll("[data-testid=search-exception-hint]"));
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

        // Formatted as the page does, in the current culture ("1.000" in en-ID, for example).
        var notice = cut.Find("[data-testid=partial-notice]").TextContent;
        Assert.Contains($"latest {JobFilterCriteria.CountLimit:N0} of", notice);
        Assert.Contains($"{1500:N0}", notice);
        Assert.Contains("UseDashboardFailureFingerprintFilter()", notice);
        Assert.Equal("42+ failed jobs", cut.Find("[data-testid=group-total]").TextContent.Trim());
    }

    [Fact]
    public async Task PartialResult_WithoutStoredFingerprints_DoesNotSuggestTheFilter()
    {
        // The generic provider (Redis and others) fingerprints every job it reads, with or without the
        // filter, so registering it wouldn't make the counts exact.
        _groups.UsesStoredFingerprints = false;
        _groups.UnfingerprintedCount = 1500;
        _groups.FallbackCount = JobFilterCriteria.CountLimit;
        var cut = RenderPage();

        await SelectGroup(cut, 0);

        var notice = cut.Find("[data-testid=partial-notice]").TextContent;
        Assert.Contains($"latest {JobFilterCriteria.CountLimit:N0} of", notice);
        Assert.Contains($"{1500:N0} failed jobs, so counts are lower bounds.", System.Text.RegularExpressions.Regex.Replace(notice, @"\s+", " "));
        Assert.DoesNotContain("UseDashboardFailureFingerprintFilter", notice);
        Assert.DoesNotContain("not grouped when they failed", notice);
    }

    [Fact]
    public async Task Keyboard_ArrowsMoveTheActiveOption_AndEnterSelectsIt()
    {
        var cut = RenderPage();
        await cut.Find("#exception-filter").FocusAsync(new FocusEventArgs());

        await KeyDown(cut, "ArrowDown");
        cut.WaitForState(() => cut.FindAll(".hf-exception-option").Count > 0, TestTimeouts.RenderWait);
        Assert.Equal("true", cut.Find("#exception-filter").GetAttribute("aria-expanded"));
        // Opens on the current selection, "All exceptions".
        Assert.Equal("exception-option-0", ActiveDescendant(cut));

        await KeyDown(cut, "ArrowDown");
        Assert.Equal("exception-option-1", ActiveDescendant(cut));
        Assert.Contains("hf-option-active", cut.Find("#exception-option-1").ClassName);
        Assert.DoesNotContain("hf-option-active", cut.Find("#exception-option-0").ClassName);

        await KeyDown(cut, "End");
        Assert.Equal("exception-option-2", ActiveDescendant(cut));
        await KeyDown(cut, "ArrowDown");
        Assert.Equal("exception-option-2", ActiveDescendant(cut));
        await KeyDown(cut, "Home");
        Assert.Equal("exception-option-0", ActiveDescendant(cut));
        await KeyDown(cut, "ArrowUp");
        Assert.Equal("exception-option-0", ActiveDescendant(cut));
        _ctx.JSInterop.VerifyInvoke("hfListbox.scrollIntoView", calledTimes: 6);

        await KeyDown(cut, "ArrowDown");
        await KeyDown(cut, "Enter");
        cut.WaitForState(() => cut.FindAll("[data-testid=group-total]").Count > 0 && GroupRowCount(cut) > 0, TestTimeouts.RenderWait);

        Assert.Equal("42 failed jobs", cut.Find("[data-testid=group-total]").TextContent.Trim());
        Assert.Equal("false", cut.Find("#exception-filter").GetAttribute("aria-expanded"));
        Assert.Null(ActiveDescendant(cut));
    }

    [Fact]
    public async Task Keyboard_SpaceSelects_EscapeClosesWithoutSelecting()
    {
        var cut = RenderPage();

        await KeyDown(cut, "Enter");
        cut.WaitForState(() => cut.FindAll(".hf-exception-option").Count > 0, TestTimeouts.RenderWait);
        await KeyDown(cut, "ArrowDown");
        await KeyDown(cut, "Escape");

        Assert.Empty(cut.FindAll("[role=listbox]"));
        Assert.Equal("All exceptions", cut.Find("#exception-filter").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=group-total]"));

        // Reopens on the current selection, then Space picks the second group.
        await KeyDown(cut, " ");
        Assert.Equal("exception-option-0", ActiveDescendant(cut));
        await KeyDown(cut, "ArrowDown");
        await KeyDown(cut, "ArrowDown");
        await KeyDown(cut, " ");
        cut.WaitForState(() => cut.FindAll("[data-testid=group-total]").Count > 0 && GroupRowCount(cut) > 0, TestTimeouts.RenderWait);

        Assert.Equal("7 failed jobs", cut.Find("[data-testid=group-total]").TextContent.Trim());

        // Reopening starts on the selected group.
        await KeyDown(cut, "ArrowDown");
        Assert.Equal("exception-option-2", ActiveDescendant(cut));
        Assert.Equal("true", cut.Find("#exception-option-2").GetAttribute("aria-selected"));
    }

    [Fact]
    public async Task Keyboard_TabAway_ClosesTheList()
    {
        var cut = RenderPage();
        await KeyDown(cut, "ArrowDown");
        cut.WaitForState(() => cut.FindAll(".hf-exception-option").Count > 0, TestTimeouts.RenderWait);

        await cut.Find("#exception-filter").FocusOutAsync(new FocusEventArgs());

        Assert.Empty(cut.FindAll("[role=listbox]"));
        Assert.Equal(3, cut.FindAll("tbody input[type=checkbox]").Count);
    }

    [Fact]
    public async Task Listbox_HoldsOnlyOptions_StatusLinesSitOutsideIt()
    {
        var release = new TaskCompletionSource<FailureGroupsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.Setup(p => p.GetFailureGroupsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(release.Task);
        var cut = RenderPage();

        // Not awaited yet: the handler waits for the grouping result, which is released below.
        var click = cut.Find("#exception-filter").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Contains("Loading exceptions", cut.Find("[role=status]").TextContent), TestTimeouts.RenderWait);
        Assert.All(cut.Find("[role=listbox]").Children, c => Assert.Equal("option", c.GetAttribute("role")));
        Assert.Equal("combobox", cut.Find("#exception-filter").GetAttribute("role"));
        Assert.Equal("exception-filter-list", cut.Find("#exception-filter").GetAttribute("aria-controls"));
        // nav.js cancels the page scroll for the list's keys on elements with this attribute.
        Assert.True(cut.Find("#exception-filter").HasAttribute("data-hf-listbox-keys"));
        // Otherwise Chromium makes the scrolling list a Tab stop, and Tab would lose focus to <body>.
        Assert.Equal("-1", cut.Find(".hf-exception-menu").GetAttribute("tabindex"));

        release.SetResult(_groups);
        await click;
        cut.WaitForState(() => cut.FindAll(".hf-exception-option").Count > 0, TestTimeouts.RenderWait);

        var listbox = cut.Find("[role=listbox]");
        Assert.Equal(3, listbox.Children.Length);
        Assert.All(listbox.Children, c => Assert.Equal("option", c.GetAttribute("role")));
        Assert.Contains("1 more exception with fewer failures not listed.", cut.Find("[role=status]").TextContent);
        Assert.DoesNotContain("more exception", listbox.TextContent);
    }

    [Fact]
    public async Task Refresh_OnlyWhileAGroupIsShown_OtherwiseGroupsAreReloadedOnTheNextOpen()
    {
        // The page checks the failed count every 5 seconds, so this waits for two of those checks.
        var refreshWait = TimeSpan.FromSeconds(15);
        var cut = RenderPage();
        await SelectGroup(cut, 0);
        Assert.Equal(1, GroupingCalls());

        // A new failure while a group is shown refreshes the groups and the group's jobs.
        AddFailedJob();
        cut.WaitForState(() => GroupingCalls() == 2, refreshWait);

        await OpenFilter(cut);
        await cut.FindAll(".hf-exception-filter [role=option]").First(o => o.TextContent.Trim() == "All exceptions").ClickAsync(new MouseEventArgs());
        cut.WaitForState(() => cut.FindAll("tbody input[type=checkbox]").Count == 4, TestTimeouts.RenderWait);

        // Back on "All exceptions" with the list closed, a new failure updates the list only.
        AddFailedJob();
        cut.WaitForState(() => cut.FindAll("tbody input[type=checkbox]").Count == 5, refreshWait);
        // A refresh would start right after the list updates; give it time to reach the provider.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(2, GroupingCalls());

        // The dropped groups are read again when the filter is next opened.
        await OpenFilter(cut);
        Assert.Equal(3, GroupingCalls());
    }

    [Fact]
    public async Task Refresh_WhileTheListIsOpen_KeepsTheActiveOptionOnTheSameGroup()
    {
        var cut = RenderPage();
        await KeyDown(cut, "ArrowDown");
        cut.WaitForState(() => cut.FindAll(".hf-exception-option").Count > 0, TestTimeouts.RenderWait);
        await KeyDown(cut, "ArrowDown");
        Assert.Equal("exception-option-1", ActiveDescendant(cut));
        Assert.Contains("SaveChangesFailedException", cut.Find("#exception-option-1").TextContent);

        // The next read returns the two labeled groups the other way round.
        var reordered = _groups.Groups.ToArray();
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
        _groups = new FailureGroupsResult { UsesStoredFingerprints = true, Groups = reordered };
        AddFailedJob();
        cut.WaitForState(() => GroupingCalls() == 2, TimeSpan.FromSeconds(15));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("exception-option-2", ActiveDescendant(cut));
            Assert.Contains("SaveChangesFailedException", cut.Find("#exception-option-2").TextContent);
            Assert.Contains("hf-option-active", cut.Find("#exception-option-2").ClassName);
        }, TestTimeouts.RenderWait);
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
