using System.Linq;
using Bunit;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard.Components.Pages;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Clearing the <c>args:</c> or content badge on the Search page clears the matching filter-panel
/// input too, and the value stays cleared when another filter changes
/// (<see href="https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/44">Issue #44</see>).
/// </summary>
public class FilterBadgeSyncTests
{
    private const string ContentInput = "input[placeholder^='Search in stack trace']";

    [Fact]
    public void ClearingArgumentsBadge_ClearsInput_AndStaysCleared()
    {
        using var ctx = CreateContext();
        var cut = RenderWithPanelOpen(ctx);

        cut.Find("#search-arguments").Input("order-42");
        cut.WaitForAssertion(() => Assert.Contains("args: order-42", cut.Markup), TestTimeouts.RenderWait);

        cut.Find("button[aria-label='Clear arguments search']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("", cut.Find("#search-arguments").GetAttribute("value") ?? "");
            Assert.DoesNotContain("args: order-42", cut.Markup);
        }, TestTimeouts.RenderWait);

        // Changing any other filter used to send the stale value back up.
        cut.Find("#state-Succeeded").Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("args: order-42", cut.Markup);
            Assert.Equal("", cut.Find("#search-arguments").GetAttribute("value") ?? "");
        }, TestTimeouts.RenderWait);
    }

    [Fact]
    public void ClearingContentBadge_ClearsInput_AndStaysCleared()
    {
        using var ctx = CreateContext();
        var cut = RenderWithPanelOpen(ctx);

        cut.Find("#search-stacktrace").Change(true);
        cut.Find(ContentInput).Input("timeout");
        cut.WaitForAssertion(() =>
            Assert.Single(cut.FindAll("button[aria-label='Clear content search']")), TestTimeouts.RenderWait);

        cut.Find("button[aria-label='Clear content search']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("", cut.Find(ContentInput).GetAttribute("value") ?? "");
            Assert.Empty(cut.FindAll("button[aria-label='Clear content search']"));
        }, TestTimeouts.RenderWait);

        cut.Find("#state-Succeeded").Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("button[aria-label='Clear content search']"));
            Assert.Equal("", cut.Find(ContentInput).GetAttribute("value") ?? "");
        }, TestTimeouts.RenderWait);
    }

    [Fact]
    public void TypingIsNotRewrittenByTheParentsNormalizedValue()
    {
        using var ctx = CreateContext();
        var cut = RenderWithPanelOpen(ctx);

        // The request sent up trims the arguments text and sends short content text as null;
        // neither may be copied back into the inputs while typing.
        cut.Find("#search-arguments").Input("order ");
        cut.Find(ContentInput).Input("ti");
        cut.Find("#state-Succeeded").Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("order ", cut.Find("#search-arguments").GetAttribute("value"));
            Assert.Equal("ti", cut.Find(ContentInput).GetAttribute("value"));
        }, TestTimeouts.RenderWait);
    }

    private static TestContext CreateContext()
    {
        JobStorage storage = new InMemoryStorage();
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new DashboardUIOptions());
        ctx.Services.AddSingleton(new SearchService(storage, new TagsDataReader(storage)));
        return ctx;
    }

    private static IRenderedComponent<SearchResults> RenderWithPanelOpen(TestContext ctx)
    {
        var cut = ctx.Render<SearchResults>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced Filters")).Click();
        cut.WaitForState(() => cut.FindAll("#search-arguments").Count == 1, TestTimeouts.RenderWait);
        return cut;
    }
}
