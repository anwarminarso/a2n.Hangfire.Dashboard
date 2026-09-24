using Bunit;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Components.Pages;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Search timeout and cancellation (issue #43): the limit comes from
/// <see cref="DashboardUIOptions.SearchTimeoutSeconds"/> instead of a hardcoded 5 seconds, can be
/// disabled, and a running search can be cancelled from the page.
/// </summary>
public class SearchTimeoutTests
{
    private readonly JobStorage _storage = new InMemoryStorage();
    private readonly Mock<IStorageQueryProvider> _provider = new();
    private readonly TaskCompletionSource<CancellationToken> _providerCalled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SearchTimeoutTests()
    {
        _provider
            .Setup(p => p.GetJobsWithFilterAsync(
                It.IsAny<JobFilterCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<JobFilterCriteria, int, int, CancellationToken>((_, _, _, ct) =>
            {
                _providerCalled.TrySetResult(ct);
                return WaitUntilCancelled(ct);
            });
    }

    [Fact]
    public void SearchTimeoutSeconds_DefaultsTo30()
    {
        Assert.Equal(30, new DashboardUIOptions().SearchTimeoutSeconds);
    }

    [Fact]
    public async Task SearchAsync_ProviderThrowsNonCancellationException_AfterCancel_ReportsTimedOut()
    {
        // SqlClient surfaces a cancelled command as a SqlException, not an OperationCanceledException.
        using var cts = new CancellationTokenSource();
        _provider
            .Setup(p => p.GetJobsWithFilterAsync(
                It.IsAny<JobFilterCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                cts.Cancel();
                await Task.Yield();
                throw new InvalidOperationException("Operation cancelled by user.");
            });
        var service = new SearchService(_storage, new TagsDataReader(_storage), _provider.Object);

        var result = await service.SearchAsync(new SearchRequest { Query = "SampleJobs" }, cts.Token);

        Assert.True(result.TimedOut);
        Assert.False(result.HasError);
    }

    [Fact]
    public async Task SearchAsync_ProviderThrows_WithoutCancel_ReportsError()
    {
        _provider
            .Setup(p => p.GetJobsWithFilterAsync(
                It.IsAny<JobFilterCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));
        var service = new SearchService(_storage, new TagsDataReader(_storage), _provider.Object);

        var result = await service.SearchAsync(new SearchRequest { Query = "SampleJobs" }, CancellationToken.None);

        Assert.True(result.HasError);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void SearchPage_UsesConfiguredTimeout()
    {
        using var ctx = CreateContext(new DashboardUIOptions { SearchTimeoutSeconds = 2 });
        var cut = ctx.Render<SearchResults>();

        var providerToken = StartSearch(cut);

        cut.WaitForState(() => cut.Markup.Contains("Search Timed Out"), TestTimeouts.RenderWait);
        Assert.Contains("limited to 2 seconds", cut.Markup);
        Assert.True(providerToken.IsCancellationRequested);
    }

    [Fact]
    public void SearchPage_TimeoutDisabled_KeepsSearchingUntilCancelled()
    {
        using var ctx = CreateContext(new DashboardUIOptions { SearchTimeoutSeconds = 0 });
        var cut = ctx.Render<SearchResults>();

        var providerToken = StartSearch(cut);

        Thread.Sleep(TimeSpan.FromSeconds(1.5));
        Assert.False(providerToken.IsCancellationRequested);
        Assert.Contains("Searching...", cut.Markup);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Cancel")).Click();

        cut.WaitForState(() => cut.Markup.Contains("Search Cancelled"), TestTimeouts.RenderWait);
        Assert.True(providerToken.IsCancellationRequested);
        Assert.DoesNotContain("Search Timed Out", cut.Markup);
    }

    [Fact]
    public async Task SearchPage_Dispose_CancelsRunningSearch()
    {
        using var ctx = CreateContext(new DashboardUIOptions { SearchTimeoutSeconds = 0 });
        var cut = ctx.Render<SearchResults>();

        var providerToken = StartSearch(cut);

        await ctx.DisposeComponentsAsync();

        Assert.True(
            SpinWait.SpinUntil(() => providerToken.IsCancellationRequested, TestTimeouts.RenderWait),
            "Disposing the page did not cancel the running search.");
    }

    private Bunit.TestContext CreateContext(DashboardUIOptions options)
    {
        var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton(new SearchService(_storage, new TagsDataReader(_storage), _provider.Object));
        return ctx;
    }

    /// <summary>
    /// Starts a search and waits until it reaches the storage provider, returning the token the
    /// provider received.
    /// </summary>
    private CancellationToken StartSearch(IRenderedComponent<SearchResults> cut)
    {
        cut.Find("input[placeholder^='Search by ID']").Input("SampleJobs");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Search Jobs")).Click();

        Assert.True(_providerCalled.Task.Wait(TestTimeouts.RenderWait), "The search never reached the provider.");
        cut.WaitForState(() => cut.Markup.Contains("Searching..."), TestTimeouts.RenderWait);
        return _providerCalled.Task.Result;
    }

    private static async Task<PagedResult<JobSummaryDto>> WaitUntilCancelled(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return PagedResult<JobSummaryDto>.Empty(1, 20);
    }
}
