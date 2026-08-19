using System;
using System.Linq;
using a2n.Hangfire.Dashboard;
using Bunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using HeatmapPage = a2n.Hangfire.Dashboard.Components.Pages.Heatmap.Heatmap;
using HeatmapService = a2n.Hangfire.Dashboard.Services.HeatmapService;
using HangfireMonitorService = a2n.Hangfire.Dashboard.Services.HangfireMonitorService;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// bUnit tests that render the Heatmap page against a <em>populated</em> projected matrix backed by a
/// real <see cref="InMemoryStorage"/> with registered recurring jobs. These close the coverage gap
/// called out in the heatmap backlog (#1): the prior page-level tests rendered with no
/// <c>JobStorage</c> (empty matrix), so the view components were never rendered with bound data — the
/// exact reason a Blazor parameter-binding regression (literal vs. <c>@</c>) could slip through.
///
/// <para>By projecting two recurring jobs (an hourly and a daily cron) over the idealized week, the
/// matrix has populated cells, the server-rendered Planner view renders its <c>day × hour</c> grid,
/// the insights bar computes, and the recurring-job table lists every job — so a binding or wiring
/// fault in the default view/table now fails a test.</para>
/// </summary>
public class HeatmapPagePopulatedTests
{
    private static JobStorage BuildStorageWithRecurringJobs()
    {
        var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);

        // Hourly → one fire every hour → 168 cells over the idealized week.
        manager.AddOrUpdate("hourly-job", Job.FromExpression(() => HeatmapTestJobs.NoOp()), "0 * * * *", new RecurringJobOptions());
        // Daily → one fire per day at midnight → 7 cells (hour 0).
        manager.AddOrUpdate("daily-job", Job.FromExpression(() => HeatmapTestJobs.NoOp()), "0 0 * * *", new RecurringJobOptions());

        return storage;
    }

    private static HeatmapService CreateHeatmapService(JobStorage storage)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton(storage);
        services.AddSingleton(new HangfireMonitorService(storage));

        var options = new DashboardUIOptions();
        options.Heatmap.Enabled = true;
        services.AddSingleton(options);

        return new HeatmapService(services.BuildServiceProvider());
    }

    private static TestContext NewContext(out DashboardUIOptions options)
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var storage = BuildStorageWithRecurringJobs();

        options = new DashboardUIOptions();
        options.Heatmap.Enabled = true;

        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton(CreateHeatmapService(storage));
        ctx.Services.AddSingleton(new HangfireMonitorService(storage));
        return ctx;
    }

    private static IRenderedComponent<HeatmapPage> RenderLoaded(TestContext ctx)
    {
        var cut = ctx.RenderComponent<HeatmapPage>();
        // The page loads its data asynchronously in OnInitializedAsync; wait for the populated render.
        cut.WaitForAssertion(
            () => Assert.Contains("populated cell", cut.Markup),
            timeout: TestTimeouts.RenderWait);
        return cut;
    }

    [Fact]
    public void Page_WithRecurringJobs_RendersPopulatedPlannerGrid_NotEmptyState()
    {
        using var ctx = NewContext(out _);

        var cut = RenderLoaded(ctx);

        // The empty-state must NOT be shown when the matrix has cells (Req 1.7 inverse).
        Assert.DoesNotContain("No recurring schedule to display", cut.Markup);

        // The default Planner view is server-rendered as an accessible day × hour grid: a populated
        // matrix must yield grid cells, proving the view component rendered with bound parameters.
        var gridCells = cut.FindAll("[role=gridcell]");
        Assert.NotEmpty(gridCells);

        // Sanity: the hourly job populates every hour, so the grid is the full 7 × 24 = 168 cells.
        Assert.Equal(168, gridCells.Count);
    }

    [Fact]
    public void Page_WithRecurringJobs_ListsEveryJobInTheRecurringTable()
    {
        using var ctx = NewContext(out _);

        var cut = RenderLoaded(ctx);

        Assert.Contains("hourly-job", cut.Markup);
        Assert.Contains("daily-job", cut.Markup);
    }

    [Fact]
    public void Page_WithRecurringJobs_RendersInsightsBar()
    {
        using var ctx = NewContext(out _);

        var cut = RenderLoaded(ctx);

        // The insights bar (mirroring the v4 mockup) is computed from the loaded matrix/concurrency.
        Assert.Contains("Best window to schedule", cut.Markup);
        Assert.Contains("Peak combined concurrency", cut.Markup);
        Assert.Contains("Overlap suggestions", cut.Markup);
    }

    [Fact]
    public void Page_WithRecurringJobs_OffersDrillIntoPopulatedCells()
    {
        using var ctx = NewContext(out _);

        var cut = RenderLoaded(ctx);

        // Populated cells (contributing job count > 0) are now drilled into by clicking a cell or a
        // recommendation (heatmap.js → OpenCellDrawerAsync); the page surfaces a hint when there are
        // drillable cells rather than the former "Inspect cell" picker (Req 10.1).
        Assert.Contains("Click a populated cell", cut.Markup);
    }

    [Fact]
    public void Page_SwitchingToConcurrencyView_RendersWithoutError()
    {
        using var ctx = NewContext(out _);

        var cut = RenderLoaded(ctx);

        // Switching views must not throw; the Concurrency view header should appear (its Chart.js
        // canvas is a JS no-op under bUnit's loose interop, but the Blazor shell must still render).
        var concurrencyButton = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Concurrency");
        concurrencyButton.Click();

        cut.WaitForAssertion(
            () => Assert.Contains("Concurrency", cut.Markup),
            timeout: TestTimeouts.RenderWait);
    }
}
