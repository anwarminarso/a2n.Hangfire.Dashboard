using System;
using System.Collections.Generic;
using System.Linq;
using a2n.Hangfire.Dashboard.Components.Pages.Heatmap;
using a2n.Hangfire.Dashboard.Models;
using AngleSharp.Dom;
using Bunit;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// bUnit component tests for the Phase 1 heatmap view rendering (task 11.7).
///
/// These assert the requirement-level DOM structure each view renders itself — toolbars/captions,
/// the queue × 24-hour header, empty-state handling, accessibility attributes (aria-labels), and the
/// recurring-job table / long-period banner. The heavy chart drawing is delegated to
/// <c>window.heatmapCharts.*</c> via <see cref="Microsoft.JSInterop.IJSRuntime"/>; with the bUnit
/// JSInterop in <see cref="JSRuntimeMode.Loose"/> those calls are harmless no-ops, so the tests
/// assert only on the component's own rendered markup and parameter-driven branches.
///
///   • Queue×Hour one-row-per-queue + 24-hour header (Req 3.1);
///   • Calendar empty / zero-cell / coloring-mode handling and structure (Req 6.2);
///   • general view rendering structure (Req 15.1);
///   • accessibility attributes — aria-labels on the rendered containers (Req 24.4, 24.5);
///   • empty-state rendering when the matrix is empty;
///   • LongPeriodBanner shows/hides by job ids (Req 9.5, 9.6);
///   • RecurringJobTable rows incl. long-period retention and empty state (Req 9.7, 1.7).
/// </summary>
public class HeatmapPhase1ViewTests
{
    // --- helpers -----------------------------------------------------------------------------

    private static TestContext NewCtx()
    {
        var ctx = new TestContext();
        // Loose mode → the views' heatmapCharts.* interop calls become no-ops.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static ProjectionWindow IdealizedWeek() => new(
        new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),   // Monday
        new DateTimeOffset(2024, 1, 8, 0, 0, 0, TimeSpan.Zero),
        ProjectionWindowKind.IdealizedWeek);

    private static HeatmapCell Cell(string queue, int day, int hour, double value, params string[] jobIds) =>
        new(new CellKey(queue, day, hour), value, jobIds.Length, queue, jobIds);

    private static HeatmapMatrix Matrix(
        IEnumerable<HeatmapCell> cells,
        IReadOnlyList<string> queues,
        LoadMetric metric = LoadMetric.FireCount)
    {
        var list = cells.ToList();
        var dict = list.ToDictionary(c => c.Key);
        var min = list.Count > 0 ? list.Min(c => c.Value) : 0d;
        var max = list.Count > 0 ? list.Max(c => c.Value) : 0d;
        return new HeatmapMatrix(dict, queues, IdealizedWeek(), metric, min, max);
    }

    private static HeatmapMatrix EmptyMatrix() =>
        new(new Dictionary<CellKey, HeatmapCell>(), Array.Empty<string>(), IdealizedWeek(), LoadMetric.FireCount, 0, 0);

    // --- PunchcardView -----------------------------------------------------------------------

    [Fact]
    public void Punchcard_Projected_RendersContainer_AndDominantQueueCaption_WithQueueLegend()
    {
        using var ctx = NewCtx();
        var matrix = Matrix(new[] { Cell("alpha", 0, 9, 3, "j1"), Cell("beta", 1, 10, 2, "j2") },
            new[] { "alpha", "beta" });

        var cut = ctx.RenderComponent<PunchcardView>(p => p
            .Add(c => c.Matrix, matrix)
            .Add(c => c.Source, HeatmapSource.Projected));

        Assert.Single(cut.FindAll(".punchcard-view"));
        Assert.Contains("dominant queue", cut.Markup);
        // The projected legend lists each queue and is hidden from assistive tech (decorative).
        Assert.Single(cut.FindAll("[aria-hidden='true']"));
        Assert.Contains("alpha", cut.Markup);
        Assert.Contains("beta", cut.Markup);
    }

    [Fact]
    public void Punchcard_Historical_RendersFailureRateCaption_AndHidesQueueLegend()
    {
        using var ctx = NewCtx();
        var matrix = Matrix(new[] { Cell("alpha", 0, 9, 3, "j1") }, new[] { "alpha" });

        var cut = ctx.RenderComponent<PunchcardView>(p => p
            .Add(c => c.Matrix, matrix)
            .Add(c => c.Source, HeatmapSource.Historical));

        Assert.Contains("failure rate", cut.Markup);
        // Under the historical source the decorative queue legend is not rendered.
        Assert.Empty(cut.FindAll("[aria-hidden='true']"));
    }

    // --- QueueHourView -----------------------------------------------------------------------

    [Fact]
    public void QueueHour_NullMatrix_RendersEmptyState()
    {
        using var ctx = NewCtx();

        var cut = ctx.RenderComponent<QueueHourView>(p => p.Add(c => c.Matrix, (HeatmapMatrix)null));

        Assert.Contains("No queues to display.", cut.Markup);
        Assert.Single(cut.FindAll("i.bi-grid-3x3"));
        // No grid container is emitted in the empty state.
        Assert.Empty(cut.FindAll("[data-hm-queuehour]"));
    }

    [Fact]
    public void QueueHour_OneRowPerQueue_And24HourHeader_WholeWeekByDefault()
    {
        using var ctx = NewCtx();
        var matrix = Matrix(new[] { Cell("q1", 0, 9, 5, "j1"), Cell("q2", 1, 10, 2, "j2") },
            new[] { "q1", "q2" });

        var cut = ctx.RenderComponent<QueueHourView>(p => p.Add(c => c.Matrix, matrix));

        Assert.Single(cut.FindAll("[data-hm-queuehour]"));
        // One row per visible queue (Req 3.1) and exactly 24 hour columns advertised in the header.
        Assert.Contains("2 queue(s) × 24 hours", cut.Markup);
        // Default selection (no day) sums across the whole week (Req 3.3).
        Assert.Contains("whole week", cut.Markup);
    }

    [Fact]
    public void QueueHour_SelectedDay_LabelsThatDay()
    {
        using var ctx = NewCtx();
        var matrix = Matrix(new[] { Cell("q1", 2, 9, 5, "j1") }, new[] { "q1" });

        // Day index 2 with default Mon..Sun labels → "Wed" (Req 3.2 per-day mode).
        var cut = ctx.RenderComponent<QueueHourView>(p => p
            .Add(c => c.Matrix, matrix)
            .Add(c => c.SelectedDay, 2));

        Assert.Contains("Wed", cut.Markup);
    }

    // --- PerQueueView ------------------------------------------------------------------------

    [Fact]
    public void PerQueue_NoVisibleData_RendersEmptyState()
    {
        using var ctx = NewCtx();

        var cut = ctx.RenderComponent<PerQueueView>(p => p.Add(c => c.Matrix, EmptyMatrix()));

        Assert.Contains("No per-queue schedule to display.", cut.Markup);
        Assert.Empty(cut.FindAll(".heatmap-perqueue"));
    }

    [Fact]
    public void PerQueue_WithData_RendersContainer_WithAccessibleLabel()
    {
        using var ctx = NewCtx();
        var matrix = Matrix(new[] { Cell("q1", 0, 9, 4, "j1") }, new[] { "q1" });

        var cut = ctx.RenderComponent<PerQueueView>(p => p.Add(c => c.Matrix, matrix));

        var container = cut.Find(".heatmap-perqueue");
        // Accessibility attribute on the small-multiples container (Req 24.4, 24.5).
        Assert.Equal("Per-queue schedule small multiples", container.GetAttribute("aria-label"));
    }

    // --- CalendarView ------------------------------------------------------------------------

    [Fact]
    public void Calendar_NullMatrix_RendersEmptyState()
    {
        using var ctx = NewCtx();

        var cut = ctx.RenderComponent<CalendarView>(p => p.Add(c => c.Matrix, (HeatmapMatrix)null));

        Assert.Contains("No schedule to display.", cut.Markup);
        Assert.Single(cut.FindAll("i.bi-calendar3"));
    }

    [Fact]
    public void Calendar_VolumeMode_RendersGrid_WithEmptyAndCurrentHourLegend()
    {
        using var ctx = NewCtx();
        // Mix of zero and non-zero cells: zero cells render at the ramp's empty shade (Req 6.2).
        var matrix = Matrix(new[] { Cell("q1", 0, 9, 5, "j1"), Cell("q1", 1, 10, 0, "j2") },
            new[] { "q1" });

        var cut = ctx.RenderComponent<CalendarView>(p => p.Add(c => c.Matrix, matrix));

        Assert.Contains("Calendar — day × hour", cut.Markup);
        Assert.Contains("Neutral ramp", cut.Markup);
        // The legend distinguishes the empty shade and the current-hour marker (Req 6.2 / 6.5).
        Assert.Single(cut.FindAll(".hf-cal-swatch"));
        Assert.Single(cut.FindAll(".hf-cal-marker"));
    }

    [Fact]
    public void Calendar_FailureMode_WithoutHistorical_AnnouncesCellsShownEmpty()
    {
        using var ctx = NewCtx();
        var matrix = Matrix(new[] { Cell("q1", 0, 9, 5, "j1") }, new[] { "q1" });

        // color-by-failure may stay selected while Historical is inactive; cells then render empty
        // (Req 6.7).
        var cut = ctx.RenderComponent<CalendarView>(p => p
            .Add(c => c.Matrix, matrix)
            .Add(c => c.ColorBy, "failure")
            .Add(c => c.HistoricalActive, false));

        Assert.Contains("needs the Historical source", cut.Markup);
    }

    [Fact]
    public void Calendar_FailureMode_WithHistorical_ShadesByFailureRate()
    {
        using var ctx = NewCtx();
        var matrix = Matrix(new[] { Cell("q1", 0, 9, 5, "j1") }, new[] { "q1" });
        var historical = new[] { new HeatmapHistoricalCell(0, 9, FireCount: 10, FailureCount: 3, P95Ms: 1200) };

        var cut = ctx.RenderComponent<CalendarView>(p => p
            .Add(c => c.Matrix, matrix)
            .Add(c => c.ColorBy, "failure")
            .Add(c => c.HistoricalActive, true)
            .Add(c => c.HistoricalCells, historical));

        Assert.Contains("Shaded by failure rate", cut.Markup);
    }

    // --- ConcurrencyView ---------------------------------------------------------------------

    [Fact]
    public void Concurrency_NoData_RendersEmptyState()
    {
        using var ctx = NewCtx();

        var cut = ctx.RenderComponent<ConcurrencyView>(p => p
            .Add(c => c.Result, (ConcurrencyResult)null)
            .Add(c => c.Capacity, 0));

        Assert.Contains("No concurrency to display.", cut.Markup);
        Assert.Empty(cut.FindAll("canvas"));
    }

    [Fact]
    public void Concurrency_WithData_RendersCanvas_WithAccessibleLabel_AndPeakSummary()
    {
        using var ctx = NewCtx();
        var perSlot = new int[1440];
        perSlot[540] = 3; // 09:00
        var result = new ConcurrencyResult(
            PeakConcurrency: 3,
            PeakMinuteOfDay: 540,
            OverCapacitySlotCount: 2,
            PerQueueSeries: new[] { new QueueConcurrencySeries("q1", perSlot) });

        var cut = ctx.RenderComponent<ConcurrencyView>(p => p
            .Add(c => c.Result, result)
            .Add(c => c.Capacity, 2)
            .Add(c => c.DayLabel, "Mon"));

        var canvas = cut.Find("canvas");
        // Accessibility label describing the chart contents (Req 24.4, 24.5).
        Assert.Contains("Concurrent jobs over the day", canvas.GetAttribute("aria-label"));
        Assert.Contains("09:00", cut.Markup);          // peak time
        Assert.Contains("minute(s) over capacity", cut.Markup);
    }

    [Fact]
    public void Concurrency_Combined_EmitsAdHocBaselineLayer()
    {
        using var ctx = NewCtx();
        var perSlot = new int[1440];
        perSlot[540] = 3; // cron concurrency at 09:00
        var result = new ConcurrencyResult(
            PeakConcurrency: 5,
            PeakMinuteOfDay: 540,
            OverCapacitySlotCount: 1,
            PerQueueSeries: new[] { new QueueConcurrencySeries("q1", perSlot) });

        var baseline = new int[1440];
        baseline[540] = 2; // ad-hoc baseline at 09:00

        var cut = ctx.RenderComponent<ConcurrencyView>(p => p
            .Add(c => c.Result, result)
            .Add(c => c.Capacity, 4)
            .Add(c => c.JobClass, "Combined")
            .Add(c => c.AdHocBaselinePerSlot, (IReadOnlyList<int>)baseline));

        // Req 19.2 — in Combined mode the ad-hoc baseline is emitted as a distinct, populated layer
        // so the JS renderer can stack adhoc + cron and flag over-capacity against the total.
        var adhoc = AdHocLayerOf(ctx);
        Assert.NotEmpty(adhoc);
        Assert.Contains(adhoc, v => v > 0);
    }

    [Theory]
    [InlineData("Cron")]
    [InlineData("Ad-hoc")]
    public void Concurrency_NonCombined_SuppressesAdHocBaselineLayer(string jobClass)
    {
        using var ctx = NewCtx();
        var perSlot = new int[1440];
        perSlot[540] = 3;
        var result = new ConcurrencyResult(
            PeakConcurrency: 3,
            PeakMinuteOfDay: 540,
            OverCapacitySlotCount: 0,
            PerQueueSeries: new[] { new QueueConcurrencySeries("q1", perSlot) });

        var baseline = new int[1440];
        baseline[540] = 2;

        var cut = ctx.RenderComponent<ConcurrencyView>(p => p
            .Add(c => c.Result, result)
            .Add(c => c.Capacity, 4)
            .Add(c => c.JobClass, jobClass)
            .Add(c => c.AdHocBaselinePerSlot, (IReadOnlyList<int>)baseline));

        // Req 19.1/19.2 — outside Combined mode the ad-hoc baseline is suppressed (empty layer),
        // so only the cron contribution is plotted.
        Assert.Empty(AdHocLayerOf(ctx));
    }

    /// <summary>
    /// Reads the <c>adhoc</c> layer of the anonymous model handed to <c>heatmapCharts.renderConcurrency</c>
    /// via the captured JS interop invocation.
    /// </summary>
    private static double[] AdHocLayerOf(TestContext ctx)
    {
        var invocation = ctx.JSInterop.Invocations
            .First(i => i.Identifier == "heatmapCharts.renderConcurrency");
        var model = invocation.Arguments[1];
        var adhoc = model.GetType().GetProperty("adhoc").GetValue(model);
        return ((IEnumerable<double>)adhoc).ToArray();
    }

    // --- LongPeriodBanner --------------------------------------------------------------------

    [Fact]
    public void LongPeriodBanner_NoJobs_RendersNothing()
    {
        using var ctx = NewCtx();

        var cut = ctx.RenderComponent<LongPeriodBanner>(p => p.Add(c => c.JobIds, (IReadOnlyList<string>)null));

        // Req 9.6 — no banner when there are no long-period jobs.
        Assert.Empty(cut.FindAll("[data-hm-long-period-banner]"));
    }

    [Fact]
    public void LongPeriodBanner_WithJobs_ListsEachIdentifier()
    {
        using var ctx = NewCtx();
        var ids = new[] { "monthly-report", "yearly-cleanup" };

        var cut = ctx.RenderComponent<LongPeriodBanner>(p => p.Add(c => c.JobIds, (IReadOnlyList<string>)ids));

        // Req 9.5 — banner lists each long-period job id and is announced as a status region.
        var banner = cut.Find("[data-hm-long-period-banner]");
        Assert.Equal("status", banner.GetAttribute("role"));
        Assert.Contains("2 long-period jobs", cut.Markup);
        Assert.Contains("monthly-report", cut.Markup);
        Assert.Contains("yearly-cleanup", cut.Markup);
    }

    [Fact]
    public void LongPeriodBanner_SingleJob_UsesSingularLabel()
    {
        using var ctx = NewCtx();

        var cut = ctx.RenderComponent<LongPeriodBanner>(p => p
            .Add(c => c.JobIds, (IReadOnlyList<string>)new[] { "monthly-report" }));

        Assert.Contains("1 long-period job ", cut.Markup);
    }

    // --- RecurringJobTable -------------------------------------------------------------------

    [Fact]
    public void RecurringJobTable_NoJobs_RendersEmptyState()
    {
        using var ctx = NewCtx();

        var cut = ctx.RenderComponent<RecurringJobTable>(p => p
            .Add(c => c.Jobs, (IReadOnlyList<RecurringJobSpec>)null));

        // Req 1.7 / 9.1 — empty state rather than an empty grid.
        Assert.Contains("No recurring jobs to project.", cut.Markup);
        Assert.Empty(cut.FindAll("[data-hm-recurring-table]"));
    }

    [Fact]
    public void RecurringJobTable_WithJobs_RendersRows_RetainingLongPeriodWithZeroCells()
    {
        using var ctx = NewCtx();
        var jobs = new[]
        {
            new RecurringJobSpec("job-a", "0 9 * * *", null, "default", TimeSpan.FromMinutes(2), false),
            new RecurringJobSpec("monthly", "0 0 1 * *", "Europe/London", "reports", TimeSpan.FromHours(1), false),
        };
        // job-a contributes one cell; monthly (long-period) contributes none but is still retained.
        var matrix = Matrix(new[] { Cell("default", 0, 9, 1, "job-a") }, new[] { "default" });

        var cut = ctx.RenderComponent<RecurringJobTable>(p => p
            .Add(c => c.Jobs, (IReadOnlyList<RecurringJobSpec>)jobs)
            .Add(c => c.Matrix, matrix)
            .Add(c => c.LongPeriodJobIds, (IReadOnlyList<string>)new[] { "monthly" }));

        Assert.Single(cut.FindAll("[data-hm-recurring-table]"));

        // Table column headers.
        Assert.Contains("Window Cells", cut.Markup);
        Assert.Contains("Time Zone", cut.Markup);

        // One row per recurring job (Req 9.7 retains the zero-cell long-period job).
        var rows = cut.FindAll("tbody tr");
        Assert.Equal(2, rows.Count);

        Assert.Contains("job-a", cut.Markup);
        Assert.Contains("monthly", cut.Markup);
        Assert.Contains("long period", cut.Markup);     // long-period flag badge
        Assert.Contains("Europe/London", cut.Markup);   // configured time zone
        Assert.Contains("UTC", cut.Markup);             // null time zone → UTC label
    }
}
