using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Components.Pages;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// The Exception filter in the Search filter panel and its badge (#53): the panel offers the groups
/// that can match, loads them only when it opens, and stays in sync with the badge as #44 made the
/// other filters do (see <see cref="FilterBadgeSyncTests"/>).
/// </summary>
public class SearchExceptionFilterTests : IDisposable
{
    private const string StoredKey = "v1:0123456789abcdef";
    private const string ComputedOnlyKey = "v1:fedcba9876543210";
    private const string UnlabeledKey = "v1:1111111111111111";
    private const string RequeuedKey = "v1:2222222222222222";

    private readonly BunitContext _ctx = new();
    private readonly Mock<IStorageQueryProvider> _provider = new();
    private readonly List<JobFilterCriteria> _searches = new();
    private readonly DashboardUIOptions _options = new();
    private FailureGroupsResult _groups;

    public SearchExceptionFilterTests()
    {
        // As the SQL Server and PostgreSQL adapters return it.
        _groups = new FailureGroupsResult
        {
            UsesStoredFingerprints = true,
            Groups = new[]
            {
                new FailureGroupDto
                {
                    Fingerprint = StoredKey, Count = 42, StoredCount = 40, ComputedCount = 2, IsLabeled = true,
                    ExceptionType = "Contoso.Data.SaveChangesFailedException",
                    InnerExceptionType = "Microsoft.Data.SqlClient.SqlException",
                    NormalizedMessage = "Cannot insert duplicate key in object 'dbo.Orders'.",
                    TopFrame = "Contoso.Jobs.OrderJob.Run",
                },
                // Failed before the filter was registered: Search can't find these.
                new FailureGroupDto
                {
                    Fingerprint = ComputedOnlyKey, Count = 7, ComputedCount = 7, IsLabeled = true,
                    ExceptionType = "System.TimeoutException", InnerExceptionType = "",
                    NormalizedMessage = "The operation has timed out.", TopFrame = "Contoso.Jobs.SyncJob.Run",
                },
                new FailureGroupDto { Fingerprint = UnlabeledKey, Count = 1, StoredCount = 1 },
            },
        };

        _provider.SetupGet(p => p.SupportsFailureGrouping).Returns(true);
        _provider.Setup(p => p.GetFailureGroupsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _groups);
        _provider.Setup(p => p.GetJobsWithFilterAsync(It.IsAny<JobFilterCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((JobFilterCriteria c, int _, int _, CancellationToken _) => _searches.Add(c))
            .ReturnsAsync(PagedResult<JobSummaryDto>.Empty(1, 20));

        JobStorage storage = new InMemoryStorage();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(_options);
        _ctx.Services.AddSingleton(_provider.Object);
        _ctx.Services.AddSingleton(new SearchService(storage, new TagsDataReader(storage), _provider.Object));
    }

    public void Dispose() => _ctx.Dispose();

    private void Navigate(string uri) => _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);

    private IRenderedComponent<SearchResults> RenderWithPanelOpen()
    {
        var cut = _ctx.Render<SearchResults>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced Filters")).Click();
        cut.WaitForState(() => cut.FindAll("[data-testid=exception-filter]").Count == 1
            && !cut.Find("[data-testid=exception-filter]").TextContent.Contains("Loading"), TestTimeouts.RenderWait);
        return cut;
    }

    private static AngleSharp.Dom.IElement Select(IRenderedComponent<SearchResults> cut) => cut.Find("[data-testid=exception-filter]");

    private static List<string> OptionValues(IRenderedComponent<SearchResults> cut)
        => Select(cut).QuerySelectorAll("option").Select(o => o.GetAttribute("value")).ToList();

    private static string Hint(IRenderedComponent<SearchResults> cut) => cut.Find("[data-testid=exception-filter-hint]").TextContent;

    [Fact]
    public void Groups_AreLoadedOnlyWhenThePanelOpens()
    {
        var cut = _ctx.Render<SearchResults>();
        _provider.Verify(p => p.GetFailureGroupsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced Filters")).Click();

        cut.WaitForAssertion(() => _provider.Verify(
            p => p.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, It.IsAny<CancellationToken>()), Times.Once), TestTimeouts.RenderWait);
    }

    [Fact]
    public void ThePanel_ListsOnlyLabeledGroupsWithAStoredFingerprint()
    {
        var cut = RenderWithPanelOpen();

        // Not the group known only from the fallback, nor the unlabeled one.
        Assert.Equal(new[] { "", StoredKey }, OptionValues(cut));
        var option = Select(cut).QuerySelectorAll("option")[1];
        // The Failed page's parts, on one line: short types, message, top frame and count.
        Assert.Equal(
            "SaveChangesFailedException → SqlException · Cannot insert duplicate key in object 'dbo.Orders'. · Contoso.Jobs.OrderJob.Run (42 failed)",
            option.TextContent);
        Assert.Contains("Contoso.Data.SaveChangesFailedException → Microsoft.Data.SqlClient.SqlException", option.GetAttribute("title"));
        Assert.False(Select(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void WithoutStoredFingerprints_TheSelectIsDisabled_WithAHintAboutTheFilter()
    {
        _groups = new FailureGroupsResult
        {
            UsesStoredFingerprints = true,
            Groups = _groups.Groups.Where(g => g.StoredCount == 0).ToList(),
        };

        var cut = RenderWithPanelOpen();

        Assert.True(Select(cut).HasAttribute("disabled"));
        Assert.Contains("UseDashboardFailureFingerprintFilter()", Hint(cut));
        Assert.Contains("No failed job has a stored exception fingerprint", Hint(cut));
    }

    [Fact]
    public void OnTheGenericProvider_LabeledGroupsAreOffered_SinceStoredAndComputedCantBeToldApart()
    {
        _groups = new FailureGroupsResult { UsesStoredFingerprints = false, Groups = _groups.Groups };

        var cut = RenderWithPanelOpen();

        Assert.Equal(new[] { "", StoredKey, ComputedOnlyKey }, OptionValues(cut));
        Assert.DoesNotContain("No failed job", Hint(cut));
    }

    [Fact]
    public void AFingerprintFromTheUrl_ThatIsNotListed_StillShowsAsSelected_AndIsSearched()
    {
        // Every failed job of that group was requeued since, but Search still finds them.
        Navigate($"search?exception={RequeuedKey}");

        var cut = RenderWithPanelOpen();

        Assert.Equal(RequeuedKey, Select(cut).GetAttribute("value"));
        Assert.Contains(RequeuedKey, OptionValues(cut));
        Assert.Single(cut.FindAll("button[aria-label='Clear exception filter']"));
        // The link from the Failed page runs the search straight away.
        cut.WaitForAssertion(() => Assert.Equal(RequeuedKey, Assert.Single(_searches).FailureFingerprint), TestTimeouts.RenderWait);
    }

    [Theory]
    [InlineData("V1:0123456789ABCDEF")]
    [InlineData("not-a-fingerprint")]
    public void AMalformedFingerprintInTheUrl_IsIgnored(string value)
    {
        Navigate($"search?exception={Uri.EscapeDataString(value)}");

        var cut = RenderWithPanelOpen();

        Assert.Equal("", Select(cut).GetAttribute("value") ?? "");
        Assert.Empty(cut.FindAll("button[aria-label='Clear exception filter']"));
        Assert.Empty(_searches);
    }

    [Fact]
    public void ClearingTheBadge_ClearsThePanel_AndStaysCleared()
    {
        var cut = RenderWithPanelOpen();

        Select(cut).Change(StoredKey);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button[aria-label='Clear exception filter']")), TestTimeouts.RenderWait);
        Assert.Contains(StoredKey, cut.Find("button[aria-label='Clear exception filter']").ParentElement.TextContent);

        cut.Find("button[aria-label='Clear exception filter']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("", Select(cut).GetAttribute("value") ?? "");
            Assert.Empty(cut.FindAll("button[aria-label='Clear exception filter']"));
        }, TestTimeouts.RenderWait);

        // Changing any other filter must not send the cleared value back up.
        cut.Find("#state-Succeeded").Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("button[aria-label='Clear exception filter']"));
            Assert.Equal("", Select(cut).GetAttribute("value") ?? "");
        }, TestTimeouts.RenderWait);
    }

    [Fact]
    public void ASelectedException_IsSearched_AndShownInTheResultSummary()
    {
        var cut = RenderWithPanelOpen();
        Select(cut).Change(StoredKey);

        cut.FindAll("button").First(b => b.TextContent.Contains("Search Jobs")).Click();

        cut.WaitForAssertion(() => Assert.Equal(StoredKey, Assert.Single(_searches).FailureFingerprint), TestTimeouts.RenderWait);
        // The removable badge above, and the read-only one in the result summary.
        cut.WaitForAssertion(() => Assert.Equal(2,
            cut.FindAll("span.badge").Count(b => b.TextContent.Contains($"exception: {StoredKey}"))), TestTimeouts.RenderWait);
    }

    [Fact]
    public void ALoadThatTimesOut_IsReported_AndTheRestOfThePanelKeepsWorking()
    {
        _options.SearchTimeoutSeconds = 1;
        _provider.Setup(p => p.GetFailureGroupsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return _groups;
            });

        var cut = _ctx.Render<SearchResults>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced Filters")).Click();

        cut.WaitForAssertion(() => Assert.Contains("took longer than 1 second ", Hint(cut)), TestTimeouts.RenderWait);
        Assert.True(Select(cut).HasAttribute("disabled"));
        Assert.Single(cut.FindAll("#search-arguments"));
    }

    [Fact]
    public void AProviderWithoutGrouping_HidesTheFilter()
    {
        _provider.SetupGet(p => p.SupportsFailureGrouping).Returns(false);

        var cut = _ctx.Render<SearchResults>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced Filters")).Click();
        cut.WaitForState(() => cut.FindAll("#search-arguments").Count == 1, TestTimeouts.RenderWait);

        Assert.Empty(cut.FindAll("[data-testid=exception-filter]"));
        _provider.Verify(p => p.GetFailureGroupsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
