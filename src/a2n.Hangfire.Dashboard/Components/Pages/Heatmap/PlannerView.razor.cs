using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.AspNetCore.Components;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

/// <summary>
/// Planner / Combined view (task 16.3) — the centerpiece of <c>docs/mockups/heatmap-mockup-v4.html</c>
/// (<c>#view-planner</c>): it overlays the projected cron schedule on top of the ad-hoc
/// <c>Demand_Profile</c> so an operator can slot controllable cron work into the low-demand gaps.
/// </summary>
/// <remarks>
/// <para><b>Behavior (Req 18.1, 18.2, 18.4, 18.5).</b></para>
/// <list type="bullet">
/// <item><b>Combined</b> (<see cref="JobClass"/> = <c>Combined</c>): every <c>day × hour</c> cell's
/// background is shaded monotonically by its <c>Demand_Profile</c> value and the projected cron
/// <see cref="LoadMetric"/> is drawn as an overlaid dot whose size increases monotonically with the
/// cron value (Req 18.1).</item>
/// <item><b>Cron</b> (Req 18.4): the cron overlay dots are rendered with no ad-hoc background
/// shading.</item>
/// <item><b>Ad-hoc</b> (Req 18.5): only the <c>Demand_Profile</c> background shading is rendered,
/// with no cron overlay dots.</item>
/// <item>Each cron dot is colored by the cell's <em>dominant cron queue</em> when the
/// <see cref="HeatmapSource.Projected"/> source is active, or by its <em>failure rate</em> when the
/// <see cref="HeatmapSource.Historical"/> source is active; the active source selects exactly one of
/// the two coloring methods (Req 18.2).</item>
/// <item>Cells classified as a <c>Safe_Window</c> (demand at or below the low-load threshold and zero
/// projected cron load) are ringed, and the recommended best window to schedule (lowest combined
/// load) is badged (Req 18.3, 18.6, via <see cref="PlannerHelpers"/>).</item>
/// </list>
/// <para><b>Rendering approach.</b> Unlike the sibling Punchcard / Calendar / Concurrency views,
/// there is no <c>renderPlanner</c> function in <c>Content/js/heatmap.js</c> (which is locked) and no
/// single Chart.js renderer naturally produces a shaded grid with a sized, separately-colored dot
/// overlaid on every cell. Rather than overlay two independent canvases, this component renders a
/// self-contained, keyboard-focusable DOM grid server-side (Req 24.5, 15.6): the background shade and
/// dot size come from the shared, monotonic, endpoint-normalized <see cref="Intensity"/> mappings
/// (the same helpers the JS renderers use), so the planner stays visually consistent with the other
/// views without any JavaScript. Demand shading is expressed as an alpha-composited fill over the
/// theme-aware card background, so it adapts to the light/dark theme resolved by the dashboard's
/// existing mechanism without independently detecting the OS preference (Req 15.1, 15.2). Because no
/// JS interop is used, the component needs neither <c>IJSRuntime</c> nor <see cref="IAsyncDisposable"/>.
/// </para>
/// <para>The combined/Safe_Window/best-window computation is delegated to the pure, FsCheck-tested
/// <see cref="PlannerHelpers.BuildPlanner"/>; this component only filters its inputs to the active
/// queue selection and projects the result onto the DOM grid — mirroring how the other views take
/// their computed inputs as parameters rather than calling <c>HeatmapService</c> directly.</para>
/// </remarks>
public partial class PlannerView : ComponentBase
{
    /// <summary>The low-load threshold fraction of peak ad-hoc demand below which a slot is a candidate Safe_Window (mirrors the v4 mockup's <c>safeT = maxA * 0.12</c>).</summary>
    private const double SafeWindowFraction = 0.12;

    private const int HoursPerDay = 24;
    private const double MinDotPx = 6d;
    private const double MaxDotPx = 22d;
    private const double MinShadeAlpha = 0.10;
    private const double MaxShadeAlpha = 0.85;

    // Deterministic queue palette mirrored from Content/js/heatmap.js (QUEUE_PALETTE / queueColor) so
    // the dot tints match the legend swatches and the sibling Punchcard view.
    private static readonly string[] QueuePalette =
    {
        "#4dabf7", "#f783ac", "#ffa94d", "#38d9a9", "#b197fc",
        "#ffe066", "#ff8787", "#9775fa", "#74c0fc", "#63e6be"
    };

    private static readonly string[] WeekdayLabels = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    /// <summary>
    /// The projected cron <c>queue × day × hour</c> matrix supplying the overlay dots' size, dominant
    /// queue, and (combined with the demand) the Safe_Window / best-window classification. May be
    /// <c>null</c> (treated as no cron load), e.g. while the Job_Class is <c>Ad-hoc</c>.
    /// </summary>
    [Parameter] public HeatmapMatrix CronMatrix { get; set; }

    /// <summary>
    /// The ad-hoc <c>Demand_Profile</c> supplying the background shading and the Safe_Window
    /// classification. May be <c>null</c> (treated as no demand), e.g. while the Job_Class is
    /// <c>Cron</c> or no metrics provider is registered.
    /// </summary>
    [Parameter] public DemandProfile Demand { get; set; }

    /// <summary>The active projection window; drives the day-index ↔ day-of-week mapping and the row labels.</summary>
    [Parameter, EditorRequired] public ProjectionWindow Window { get; set; }

    /// <summary>The viewer time-zone id used to align the demand profile and locate the current cell; empty means UTC.</summary>
    [Parameter] public string ViewerTimeZoneId { get; set; }

    /// <summary>
    /// The active Job_Class — <c>Cron</c>, <c>Ad-hoc</c>, or <c>Combined</c> (default). Selects which
    /// layers render: shading for <c>Ad-hoc</c>/<c>Combined</c> (Req 18.1, 18.5) and dots for
    /// <c>Cron</c>/<c>Combined</c> (Req 18.1, 18.4).
    /// </summary>
    [Parameter] public string JobClass { get; set; } = "Combined";

    /// <summary>
    /// The active data source. <see cref="HeatmapSource.Projected"/> colors the cron dots by their
    /// dominant cron queue; <see cref="HeatmapSource.Historical"/> colors them by failure rate. The
    /// active source determines which single coloring method is used (Req 18.2).
    /// </summary>
    [Parameter] public HeatmapSource Source { get; set; } = HeatmapSource.Projected;

    /// <summary>Whether to apply logarithmic scaling to the demand shading and cron dot sizing (Req 20.4, 20.5).</summary>
    [Parameter] public bool LogScale { get; set; }

    /// <summary>
    /// The per-cell historical execution statistics (failure/fire counts), collapsed to
    /// <c>day × hour</c> positions. Drives the failure-rate dot coloring under the Historical source
    /// (Req 18.2); the cron matrix itself carries no failure data. May be <c>null</c> or empty.
    /// </summary>
    [Parameter] public IReadOnlyList<HeatmapHistoricalCell> HistoricalCells { get; set; }

    /// <summary>
    /// The queues to include, in display order. When <c>null</c>, every queue present in the cron
    /// matrix / demand profile contributes. Filtering (e.g. Top-N) is applied by the caller, matching
    /// the other heatmap views.
    /// </summary>
    [Parameter] public IReadOnlyList<string> VisibleQueues { get; set; }

    // Computed per parameter set.
    private bool _showShading;
    private bool _showDots;
    private bool _hasData;
    private string[] _dayLabels = WeekdayLabels;
    private PlannerCellView[][] _grid = Array.Empty<PlannerCellView[]>();
    private PlannerCellKey _bestWindow;

    // Per-cell detail lookups for the rich hover tooltip (mirrors the v4 mockup): the demand broken
    // down by queue, and the contributing cron job ids, keyed by (dayIndex, hour).
    private Dictionary<(int Day, int Hour), List<(string Queue, double Value)>> _demandByCell = new();
    private Dictionary<(int Day, int Hour), List<string>> _jobsByCell = new();
    private string _bestWindowLabel;
    private string _caption;
    private IReadOnlyList<string> _legendQueues = Array.Empty<string>();

    private IntensityScale Scale => LogScale ? IntensityScale.Logarithmic : IntensityScale.Linear;

    private bool IsCombined => string.Equals(JobClass, "Combined", StringComparison.OrdinalIgnoreCase);
    private bool IsAdHocOnly => string.Equals(JobClass, "Ad-hoc", StringComparison.OrdinalIgnoreCase);
    private bool IsCronOnly => string.Equals(JobClass, "Cron", StringComparison.OrdinalIgnoreCase);

    private string MetricLabel => CronMatrix?.Metric == LoadMetric.WorkerMinutes ? "worker-minutes" : "fires";

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (Window is null)
        {
            _hasData = false;
            _grid = Array.Empty<PlannerCellView[]>();
            return;
        }

        // Layer gating (Req 18.1, 18.4, 18.5): shading for Ad-hoc/Combined, dots for Cron/Combined.
        _showShading = IsAdHocOnly || IsCombined;
        _showDots = IsCronOnly || IsCombined;

        var tz = HeatmapTime.ResolveTimeZone(ViewerTimeZoneId);

        // Filter both sources to the active queue selection before overlaying them, since
        // PlannerHelpers sums across whatever queues are present in its inputs.
        var cron = FilterMatrix(CronMatrix, VisibleQueues);
        var demand = FilterDemand(Demand, VisibleQueues);

        // Low-load threshold = a fraction of the peak ad-hoc demand across the grid (Req 18.3).
        var threshold = ComputeLowLoadThreshold(demand, Window, tz);

        var planner = PlannerHelpers.BuildPlanner(cron, demand, Window, tz, threshold);
        _bestWindow = planner.BestWindow;

        // Build the per-cell tooltip detail lookups from the (queue-filtered) cron matrix and demand
        // profile, so the hover title can show the per-queue demand split and the contributing jobs.
        BuildTooltipLookups(cron, demand, tz);

        // Display domains for the monotonic intensity mappings (endpoint-normalized, Req 18.1, 20.5).
        double maxDemand = 0d;
        double maxCron = 0d;
        foreach (var cell in planner.Cells.Values)
        {
            if (cell.AdHocDemand > maxDemand)
            {
                maxDemand = cell.AdHocDemand;
            }

            if (cell.CronLoad > maxCron)
            {
                maxCron = cell.CronLoad;
            }
        }

        var dominant = _showDots && Source == HeatmapSource.Projected && cron is not null
            ? MatrixViews.DominantQueuePerCell(cron)
            : new Dictionary<(int DayIndex, int Hour), string>();

        var failureByCell = _showDots && Source == HeatmapSource.Historical
            ? CollapseHistorical()
            : new Dictionary<(int DayIndex, int Hour), HeatmapHistoricalCell>();

        var (nowDay, nowHour) = ResolveNowMarker(tz);

        _dayLabels = BuildDayLabels(Window, tz);
        _grid = BuildGrid(planner, threshold, maxDemand, maxCron, dominant, failureByCell, nowDay, nowHour);
        _bestWindowLabel = BuildBestWindowLabel();
        _caption = BuildCaption();
        _legendQueues = _showDots && Source == HeatmapSource.Projected
            ? (cron?.Queues ?? Array.Empty<string>())
            : Array.Empty<string>();

        // The grid is always fully materialized (7 × 24); "data" means at least one layer has values.
        _hasData = (_showShading && maxDemand > 0d) || (_showDots && maxCron > 0d);
    }

    /// <summary>
    /// Builds the 7 × 24 render grid by projecting each <see cref="PlannerCell"/> onto the DOM model:
    /// the background shade (demand), the dot size (cron) and dot color (dominant queue or failure
    /// rate), and the Safe_Window / best-window / now markers.
    /// </summary>
    private PlannerCellView[][] BuildGrid(
        PlannerResult planner,
        double threshold,
        double maxDemand,
        double maxCron,
        IReadOnlyDictionary<(int DayIndex, int Hour), string> dominant,
        IReadOnlyDictionary<(int DayIndex, int Hour), HeatmapHistoricalCell> failureByCell,
        int nowDay,
        int nowHour)
    {
        var grid = new PlannerCellView[HeatmapTime.WindowDays][];

        for (var day = 0; day < HeatmapTime.WindowDays; day++)
        {
            var row = new PlannerCellView[HoursPerDay];
            for (var hour = 0; hour < HoursPerDay; hour++)
            {
                planner.Cells.TryGetValue(new PlannerCellKey(day, hour), out var cell);
                var demandValue = cell?.AdHocDemand ?? 0d;
                var cronValue = cell?.CronLoad ?? 0d;

                // Background shade (Req 18.1): monotonic alpha over the theme background; zero demand
                // (or shading disabled) leaves the cell at the bare background.
                string background = null;
                if (_showShading && demandValue > 0d && maxDemand > 0d)
                {
                    var t = Intensity.Normalize(demandValue, 0d, maxDemand, Scale);
                    var alpha = MinShadeAlpha + (t * (MaxShadeAlpha - MinShadeAlpha));
                    background = string.Create(
                        CultureInfo.InvariantCulture,
                        $"background:rgba(45,140,150,{alpha:0.###});");
                }

                // Cron overlay dot (Req 18.1, 18.2): size scales monotonically with the cron value;
                // zero cron renders no dot. Color is by dominant queue (Projected) or failure rate
                // (Historical) — exactly one, selected by the active source.
                var hasDot = _showDots && cronValue > 0d && maxCron > 0d;
                double dotSize = 0d;
                string dotColor = null;
                string dotStyle = null;
                if (hasDot)
                {
                    var t = Intensity.Normalize(cronValue, 0d, maxCron, Scale);
                    dotSize = MinDotPx + (t * (MaxDotPx - MinDotPx));
                    dotColor = ResolveDotColor(day, hour, dominant, failureByCell);
                    var px = dotSize.ToString("0.#", CultureInfo.InvariantCulture);
                    dotStyle = string.Create(
                        CultureInfo.InvariantCulture,
                        $"width:{px}px;height:{px}px;background:{dotColor};");
                }

                // Safe_Window classification with the computed threshold (Req 18.3). The current cell
                // never doubles as a safe-window highlight so the "now" marker stays unambiguous.
                var isNow = day == nowDay && hour == nowHour;
                var isSafe = _showShading
                    && !isNow
                    && PlannerHelpers.IsSafeWindow(demandValue, cronValue, threshold);

                var isBest = _bestWindow is not null
                    && _bestWindow.DayIndex == day
                    && _bestWindow.Hour == hour;

                row[hour] = new PlannerCellView(
                    day,
                    hour,
                    demandValue,
                    cronValue,
                    background,
                    hasDot,
                    dotSize,
                    dotColor,
                    dotStyle,
                    isSafe,
                    isBest,
                    isNow,
                    BuildCellAria(day, hour, demandValue, cronValue, isSafe, isBest, isNow),
                    BuildCellTitle(day, hour, demandValue, cronValue, isSafe, isBest, isNow));
            }

            grid[day] = row;
        }

        return grid;
    }

    /// <summary>
    /// Resolves a cron dot's color: under the Projected source the dominant cron queue's palette
    /// color (Req 18.2); under the Historical source the failure-rate color (green → red). When the
    /// dominant queue is unknown, falls back to the first palette entry.
    /// </summary>
    private string ResolveDotColor(
        int day,
        int hour,
        IReadOnlyDictionary<(int DayIndex, int Hour), string> dominant,
        IReadOnlyDictionary<(int DayIndex, int Hour), HeatmapHistoricalCell> failureByCell)
    {
        if (Source == HeatmapSource.Historical)
        {
            var rate = failureByCell.TryGetValue((day, hour), out var stat) && stat.HasData
                ? stat.FailureRate
                : 0d;
            return FailureColorCss(rate);
        }

        return dominant.TryGetValue((day, hour), out var queue)
            ? QueueColorCss(queue)
            : QueuePalette[0];
    }

    /// <summary>
    /// Computes the low-load threshold (Req 18.3) as <see cref="SafeWindowFraction"/> of the peak
    /// ad-hoc demand summed across the active queues over the window's <c>day × hour</c> grid. Returns
    /// 0 when shading is disabled or no demand is present, so a Safe_Window then requires exactly zero
    /// demand.
    /// </summary>
    private double ComputeLowLoadThreshold(DemandProfile demand, ProjectionWindow window, TimeZoneInfo tz)
    {
        if (!_showShading || demand?.Slots is null || demand.Slots.Count == 0)
        {
            return 0d;
        }

        // Sum demand per (dayOfWeek, hour) across queues, then take the peak over the window's days.
        var byDow = new Dictionary<(int DayOfWeek, int Hour), double>();
        foreach (var slot in demand.Slots)
        {
            var key = (slot.Key.DayOfWeek, slot.Key.Hour);
            byDow.TryGetValue(key, out var existing);
            byDow[key] = existing + slot.Value;
        }

        var max = 0d;
        for (var day = 0; day < HeatmapTime.WindowDays; day++)
        {
            var dow = PlannerHelpers.MapDayIndexToDayOfWeek(window, tz, day);
            for (var hour = 0; hour < HoursPerDay; hour++)
            {
                if (byDow.TryGetValue((dow, hour), out var value) && value > max)
                {
                    max = value;
                }
            }
        }

        return max * SafeWindowFraction;
    }

    /// <summary>
    /// Returns a queue-filtered copy of the matrix, or the matrix unchanged when no filter is
    /// supplied. <c>null</c> in, <c>null</c> out.
    /// </summary>
    private static HeatmapMatrix FilterMatrix(HeatmapMatrix matrix, IReadOnlyList<string> visibleQueues)
    {
        if (matrix is null || visibleQueues is null)
        {
            return matrix;
        }

        var allow = new HashSet<string>(visibleQueues, StringComparer.Ordinal);
        var cells = new Dictionary<CellKey, HeatmapCell>();
        foreach (var entry in matrix.Cells)
        {
            if (allow.Contains(entry.Key.Queue))
            {
                cells[entry.Key] = entry.Value;
            }
        }

        var queues = matrix.Queues?.Where(allow.Contains).ToArray() ?? Array.Empty<string>();
        return matrix with { Cells = cells, Queues = queues };
    }

    /// <summary>
    /// Returns a queue-filtered copy of the demand profile, or the profile unchanged when no filter
    /// is supplied. <c>null</c> in, <c>null</c> out.
    /// </summary>
    private static DemandProfile FilterDemand(DemandProfile demand, IReadOnlyList<string> visibleQueues)
    {
        if (demand is null || visibleQueues is null)
        {
            return demand;
        }

        var allow = new HashSet<string>(visibleQueues, StringComparer.Ordinal);
        var slots = new Dictionary<DemandSlotKey, double>();
        foreach (var entry in demand.Slots)
        {
            if (allow.Contains(entry.Key.Queue))
            {
                slots[entry.Key] = entry.Value;
            }
        }

        var queues = demand.Queues?.Where(allow.Contains).ToArray() ?? Array.Empty<string>();
        return demand with { Slots = slots, Queues = queues };
    }

    /// <summary>
    /// Collapses the supplied per-cell historical statistics by their <c>(day, hour)</c> position,
    /// summing fire/failure counts when more than one entry shares a position so the failure rate is
    /// computed over the whole position. Empty when no historical data is supplied.
    /// </summary>
    private Dictionary<(int DayIndex, int Hour), HeatmapHistoricalCell> CollapseHistorical()
    {
        var lookup = new Dictionary<(int DayIndex, int Hour), HeatmapHistoricalCell>();
        if (HistoricalCells is null)
        {
            return lookup;
        }

        foreach (var stat in HistoricalCells)
        {
            if (stat is null)
            {
                continue;
            }

            var key = (stat.DayIndex, stat.Hour);
            lookup[key] = lookup.TryGetValue(key, out var existing)
                ? existing with
                {
                    FireCount = existing.FireCount + stat.FireCount,
                    FailureCount = existing.FailureCount + stat.FailureCount,
                    P95Ms = Math.Max(existing.P95Ms, stat.P95Ms)
                }
                : stat;
        }

        return lookup;
    }

    /// <summary>
    /// Builds one row label per window day: weekday names for the idealized week, calendar dates (in
    /// the viewer time zone) for the next-seven-days window — matching the sibling views.
    /// </summary>
    private static string[] BuildDayLabels(ProjectionWindow window, TimeZoneInfo tz)
    {
        if (window.Kind == ProjectionWindowKind.IdealizedWeek)
        {
            return (string[])WeekdayLabels.Clone();
        }

        var startLocal = HeatmapTime.ToViewerLocal(window.StartInclusive, tz);
        return Enumerable.Range(0, HeatmapTime.WindowDays)
            .Select(i => startLocal.AddDays(i).ToString("ddd d", CultureInfo.InvariantCulture))
            .ToArray();
    }

    /// <summary>
    /// Resolves the current day/hour marker inside the window; returns <c>(-1, -1)</c> when "now"
    /// falls outside the active window so no cell is marked (Req 6.5 / 6.6 parity).
    /// </summary>
    private (int Day, int Hour) ResolveNowMarker(TimeZoneInfo tz)
    {
        var now = DateTimeOffset.UtcNow;
        if (Window is null || !HeatmapTime.IsInWindow(now, Window))
        {
            return (-1, -1);
        }

        var (day, hour) = HeatmapTime.GetBucket(now, tz, Window);
        return day >= 0 && day < HeatmapTime.WindowDays ? (day, hour) : (-1, -1);
    }

    private string BuildBestWindowLabel()
    {
        if (_bestWindow is null || _bestWindow.DayIndex < 0 || _bestWindow.DayIndex >= _dayLabels.Length)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{_dayLabels[_bestWindow.DayIndex]} {_bestWindow.Hour:00}:00");
    }

    private string BuildCaption()
    {
        var metric = MetricLabel;
        var colorNote = Source == HeatmapSource.Historical ? "color by failure rate" : "color by dominant queue";

        if (_showShading && _showDots)
        {
            return $"Background shade = ad-hoc demand per hour; dots = projected cron (size scales with {metric}, {colorNote}).";
        }

        if (_showShading)
        {
            return "Background shade = ad-hoc demand per hour. No cron overlay for the Ad-hoc class.";
        }

        if (_showDots)
        {
            return $"Dots = projected cron (size scales with {metric}, {colorNote}). No ad-hoc shading for the Cron class.";
        }

        return string.Empty;
    }

    /// <summary>
    /// Builds the per-cell tooltip detail lookups: the demand value per queue (from the demand
    /// profile, mapped from day-of-week to the window's day index) and the contributing cron job ids
    /// (from the cron matrix cells), both keyed by <c>(dayIndex, hour)</c>.
    /// </summary>
    private void BuildTooltipLookups(HeatmapMatrix cron, DemandProfile demand, TimeZoneInfo tz)
    {
        _jobsByCell = new Dictionary<(int, int), List<string>>();
        if (cron?.Cells is not null)
        {
            foreach (var cell in cron.Cells.Values)
            {
                if (cell?.JobIds is null || cell.JobIds.Count == 0)
                {
                    continue;
                }

                var key = (cell.Key.DayIndex, cell.Key.Hour);
                if (!_jobsByCell.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    _jobsByCell[key] = list;
                }

                foreach (var id in cell.JobIds)
                {
                    if (!list.Contains(id))
                    {
                        list.Add(id);
                    }
                }
            }

            foreach (var list in _jobsByCell.Values)
            {
                list.Sort(StringComparer.Ordinal);
            }
        }

        _demandByCell = new Dictionary<(int, int), List<(string, double)>>();
        if (demand?.Slots is not null && demand.Slots.Count > 0)
        {
            // Group demand by (dayOfWeek, hour) → list of (queue, value).
            var byDow = new Dictionary<(int Dow, int Hour), List<(string Queue, double Value)>>();
            foreach (var slot in demand.Slots)
            {
                if (slot.Value <= 0)
                {
                    continue;
                }

                var key = (slot.Key.DayOfWeek, slot.Key.Hour);
                if (!byDow.TryGetValue(key, out var list))
                {
                    list = new List<(string, double)>();
                    byDow[key] = list;
                }

                list.Add((slot.Key.Queue, slot.Value));
            }

            // Project onto the window's day indices (each day index maps to one day-of-week).
            for (var day = 0; day < HeatmapTime.WindowDays; day++)
            {
                var dow = PlannerHelpers.MapDayIndexToDayOfWeek(Window, tz, day);
                for (var hour = 0; hour < HoursPerDay; hour++)
                {
                    if (byDow.TryGetValue((dow, hour), out var list))
                    {
                        _demandByCell[(day, hour)] = list.OrderByDescending(x => x.Value).ToList();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Builds the rich multi-line hover title for a cell: the slot, the demand split by queue, and the
    /// contributing cron jobs (mirrors the v4 mockup's floating tooltip). Kept separate from the
    /// concise <see cref="BuildCellAria"/> so screen readers are not read a long job list.
    /// </summary>
    private string BuildCellTitle(
        int day, int hour, double demand, double cron, bool isSafe, bool isBest, bool isNow)
    {
        var dayLabel = day >= 0 && day < _dayLabels.Length ? _dayLabels[day] : $"Day {day + 1}";
        var lines = new List<string> { $"{dayLabel} {hour:00}:00" };

        if (_showShading)
        {
            var split = string.Empty;
            if (_demandByCell.TryGetValue((day, hour), out var queues) && queues.Count > 0)
            {
                var shown = queues.Take(4)
                    .Select(q => string.Create(CultureInfo.InvariantCulture, $"{q.Queue} {q.Value:0.#}"));
                split = " — " + string.Join(", ", shown) + (queues.Count > 4 ? "…" : string.Empty);
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"on-demand {demand:0.#}{split}"));
        }

        if (_showDots)
        {
            var names = string.Empty;
            if (_jobsByCell.TryGetValue((day, hour), out var jobs) && jobs.Count > 0)
            {
                names = " — " + string.Join(", ", jobs.Take(4)) + (jobs.Count > 4 ? "…" : string.Empty);
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"cron {cron:0.#} {MetricLabel}{names}"));
        }

        if (isBest)
        {
            lines.Add("best window to schedule");
        }
        else if (isSafe)
        {
            lines.Add("safe window");
        }

        if (isNow)
        {
            lines.Add("current hour");
        }

        return string.Join("\n", lines);
    }

    private string BuildCellAria(
        int day, int hour, double demand, double cron, bool isSafe, bool isBest, bool isNow)
    {
        var dayLabel = day >= 0 && day < _dayLabels.Length ? _dayLabels[day] : $"Day {day + 1}";
        var parts = new List<string> { $"{dayLabel} {hour:00}:00" };

        if (_showShading)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"on-demand {demand:0.#}"));
        }

        if (_showDots)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"cron {cron:0.#} {MetricLabel}"));
        }

        if (isBest)
        {
            parts.Add("best window to schedule");
        }
        else if (isSafe)
        {
            parts.Add("safe window");
        }

        if (isNow)
        {
            parts.Add("current hour");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Maps a queue name to a deterministic palette color, mirroring
    /// <c>Content/js/heatmap.js</c> (<c>queueColor</c>) and the Punchcard legend.
    /// </summary>
    private static string QueueColorCss(string queue)
    {
        if (string.IsNullOrEmpty(queue))
        {
            return QueuePalette[0];
        }

        uint seed = 7;
        foreach (var ch in queue)
        {
            seed = (seed * 31) + ch;
        }

        return QueuePalette[seed % (uint)QueuePalette.Length];
    }

    /// <summary>
    /// Maps a failure rate (0.0 … 1.0) to a color from green (no failures) through amber to red (all
    /// failed), mirroring the v4 mockup's failure ramp for the Historical dot coloring (Req 18.2).
    /// </summary>
    private static string FailureColorCss(double rate)
    {
        var t = rate < 0d ? 0d : rate > 1d ? 1d : rate;

        // green (#2f9e44) -> amber (#f59f00) -> red (#e03131)
        (int R, int G, int B) lo, hi;
        double local;
        if (t <= 0.5)
        {
            lo = (0x2f, 0x9e, 0x44);
            hi = (0xf5, 0x9f, 0x00);
            local = t / 0.5;
        }
        else
        {
            lo = (0xf5, 0x9f, 0x00);
            hi = (0xe0, 0x31, 0x31);
            local = (t - 0.5) / 0.5;
        }

        var r = (int)Math.Round(lo.R + ((hi.R - lo.R) * local));
        var g = (int)Math.Round(lo.G + ((hi.G - lo.G) * local));
        var b = (int)Math.Round(lo.B + ((hi.B - lo.B) * local));
        return string.Create(CultureInfo.InvariantCulture, $"#{r:x2}{g:x2}{b:x2}");
    }

    /// <summary>Builds the CSS class list for a planner cell, layering the safe / best / now markers.</summary>
    private static string CellClass(PlannerCellView cell)
    {
        var css = "hf-pl-cell";
        if (cell.IsSafe)
        {
            css += " hf-pl-cell-safe";
        }

        if (cell.IsBest)
        {
            css += " hf-pl-cell-best";
        }

        if (cell.IsNow)
        {
            css += " hf-pl-cell-now";
        }

        return css;
    }

    /// <summary>The sparse hour-axis label: shows the hour every three columns, blank otherwise.</summary>
    private static string HourLabel(int hour) =>
        hour % 3 == 0 ? hour.ToString("00", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Markup-facing wrapper over the deterministic queue palette color.</summary>
    private static string QueueColor(string queue) => QueueColorCss(queue);

    /// <summary>The projection of a single planner cell onto the DOM render model.</summary>
    private sealed record PlannerCellView(
        int Day,
        int Hour,
        double Demand,
        double Cron,
        string BackgroundStyle,
        bool HasDot,
        double DotSize,
        string DotColor,
        string DotStyle,
        bool IsSafe,
        bool IsBest,
        bool IsNow,
        string AriaLabel,
        string Title);
}
