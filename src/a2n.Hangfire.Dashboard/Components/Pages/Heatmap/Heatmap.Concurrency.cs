using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

public partial class Heatmap
{
    /// <summary>
    /// Recomputes only the concurrency series for the chosen day (worst day when <c>_concDay</c> is
    /// −1), without reloading the whole matrix — invoked when the operator changes the Day selector
    /// on the Concurrency view.
    /// </summary>
    private async Task RecomputeConcurrencyAsync()
    {
        if (_result is null)
        {
            return;
        }

        var query = BuildQuery();
        var tz = HeatmapTime.ResolveTimeZone(_viewerTzId);
        var window = _result.Matrix.Window;
        var queueFilter = QueueFilterList();

        _concurrencyDay = _concDay >= 0
            ? Math.Clamp(_concDay, 0, HeatmapTime.WindowDays - 1)
            : await HeatmapSvc.GetWorstConcurrencyDayAsync(query, _capacity.Capacity, CancellationToken.None, queueFilter);
        _concurrencyBaseline = BuildConcurrencyBaseline(query, window, tz, _concurrencyDay);
        _concurrency = await HeatmapSvc.GetConcurrencyAsync(
            query, _concurrencyDay, _capacity.Capacity, _concurrencyBaseline, CancellationToken.None, queueFilter);

        ComputeInsights(window, tz);
        StateHasChanged();
    }

    /// <summary>
    /// Builds the per-minute ad-hoc concurrency baseline for a single window day from the
    /// Demand_Profile (Combined class only, Req 19.1). Returns <c>null</c> outside Combined or when no
    /// demand is available, so the Concurrency view plots the cron contribution alone.
    /// </summary>
    private IReadOnlyList<int> BuildConcurrencyBaseline(
        HeatmapQuery query, ProjectionWindow window, TimeZoneInfo tz, int dayIndex)
    {
        var workerMinutes = TryGetWorkerMinutesDemand(query);
        if (workerMinutes is null)
        {
            return null;
        }

        return BaselineForDay(workerMinutes, window, tz, dayIndex);
    }

    /// <summary>
    /// Builds a week-representative ad-hoc baseline for the demand-aware recommendations (Req 19.4):
    /// the per-minute maximum across the window's seven days, i.e. the worst-case ad-hoc demand at
    /// each minute-of-day. The recommendation engine takes a single per-day baseline while clusters
    /// span weekdays, so this conservative aggregate flags a collision when <em>any</em> day carries
    /// high demand at the cluster's peak minute. Returns <c>null</c> outside Combined or when no
    /// demand is available (cron-only recommendations).
    /// </summary>
    private IReadOnlyList<int> BuildWeekAdHocBaseline(HeatmapQuery query, ProjectionWindow window, TimeZoneInfo tz)
    {
        var workerMinutes = TryGetWorkerMinutesDemand(query);
        if (workerMinutes is null)
        {
            return null;
        }

        var week = new int[1440];
        for (var day = 0; day < HeatmapTime.WindowDays; day++)
        {
            var dayBaseline = BaselineForDay(workerMinutes, window, tz, day);
            for (var minute = 0; minute < week.Length; minute++)
            {
                if (dayBaseline[minute] > week[minute])
                {
                    week[minute] = dayBaseline[minute];
                }
            }
        }

        return week;
    }

    /// <summary>
    /// Fetches the demand profile in Worker-minutes (Combined class only) so each hour's value ÷ 60
    /// approximates the average concurrent ad-hoc jobs that hour. Returns <c>null</c> when not Combined,
    /// no demand provider, or the profile is empty.
    /// </summary>
    private DemandProfile TryGetWorkerMinutesDemand(HeatmapQuery query)
    {
        if (!IsCombinedClass || !_demandAvailable)
        {
            return null;
        }

        var workerMinutes = HeatmapSvc.GetDemandProfile(query with { LoadMetric = LoadMetric.WorkerMinutes });
        return workerMinutes?.Slots is { Count: > 0 } ? workerMinutes : null;
    }

    /// <summary>
    /// Projects a Worker-minutes demand profile onto a single window day's 1,440 one-minute baseline:
    /// each hour's summed worker-minutes ÷ 60 (rounded) fills that hour's 60 slots, approximating the
    /// average ad-hoc concurrency that hour.
    /// </summary>
    private static int[] BaselineForDay(DemandProfile workerMinutes, ProjectionWindow window, TimeZoneInfo tz, int dayIndex)
    {
        var baseline = new int[1440];
        if (workerMinutes?.Slots is null || workerMinutes.Slots.Count == 0)
        {
            return baseline;
        }

        var dayOfWeek = PlannerHelpers.MapDayIndexToDayOfWeek(window, tz, dayIndex);

        var perHour = new double[24];
        foreach (var slot in workerMinutes.Slots)
        {
            if (slot.Key.DayOfWeek == dayOfWeek && slot.Key.Hour is >= 0 and < 24)
            {
                perHour[slot.Key.Hour] += slot.Value;
            }
        }

        for (var hour = 0; hour < 24; hour++)
        {
            var concurrent = (int)Math.Round(perHour[hour] / 60d, MidpointRounding.AwayFromZero);
            if (concurrent < 0)
            {
                concurrent = 0;
            }

            for (var minute = hour * 60; minute < (hour * 60) + 60; minute++)
            {
                baseline[minute] = concurrent;
            }
        }

        return baseline;
    }

    /// <summary>The human-readable label of the day the Concurrency view is analyzing.</summary>
    private string ConcurrencyDayLabel() => DayLabelFor(_concurrencyDay);

    /// <summary>
    /// The day-name label for a window day index: a weekday name for the idealized week, or a calendar
    /// date (in the viewer time zone) for the next-seven-days window — matching the views.
    /// </summary>
    private string DayLabelFor(int dayIndex)
    {
        var day = Math.Clamp(dayIndex, 0, 6);
        if (_result is not null && _result.Matrix.Window.Kind == ProjectionWindowKind.Next7Days)
        {
            var tz = HeatmapTime.ResolveTimeZone(_viewerTzId);
            var startLocal = HeatmapTime.ToViewerLocal(_result.Matrix.Window.StartInclusive, tz);
            return startLocal.AddDays(day).ToString("ddd d", System.Globalization.CultureInfo.InvariantCulture);
        }

        return DayLabels[day];
    }

    /// <summary>A "{day} HH:00" label for a window day index and clock hour.</summary>
    private string DayHourLabel(int dayIndex, int hour) => $"{DayLabelFor(dayIndex)} {hour:00}:00";
}
