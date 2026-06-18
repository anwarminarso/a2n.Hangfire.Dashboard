using System;
using System.Collections.Generic;

namespace a2n.Hangfire.Dashboard.Models;

/// <summary>
/// The per-cell quantity displayed by the heatmap.
/// </summary>
public enum LoadMetric
{
    /// <summary>The number of fires assigned to a bucket.</summary>
    FireCount,

    /// <summary>The sum, in minutes, of the estimated durations of the fires in a bucket.</summary>
    WorkerMinutes
}

/// <summary>
/// The kind of projection window over which projected fire times are computed.
/// </summary>
public enum ProjectionWindowKind
{
    /// <summary>A single representative seven-day week spanning Monday 00:00 through Sunday 23:59:59.999.</summary>
    IdealizedWeek,

    /// <summary>The seven consecutive calendar days beginning at 00:00 of the current local date.</summary>
    Next7Days
}

/// <summary>
/// A bounded projection interval with an inclusive start instant and an exclusive end instant.
/// </summary>
/// <param name="StartInclusive">The inclusive start instant of the window.</param>
/// <param name="EndExclusive">The exclusive end instant of the window.</param>
/// <param name="Kind">Whether the window represents an idealized week or the next seven days.</param>
public sealed record ProjectionWindow(
    DateTimeOffset StartInclusive,
    DateTimeOffset EndExclusive,
    ProjectionWindowKind Kind);

/// <summary>
/// The origin of the active <see cref="Worker_Capacity"/> value.
/// </summary>
public enum CapacitySource
{
    /// <summary>The capacity was derived from the worker counts reported by the running servers.</summary>
    Detected,

    /// <summary>The capacity was supplied by the operator as a manual override.</summary>
    ManualOverride
}

/// <summary>
/// The resolved worker capacity together with the source that produced it.
/// </summary>
/// <param name="Capacity">The active worker capacity (always at least 1).</param>
/// <param name="Source">Whether the capacity was detected or manually overridden.</param>
public sealed record CapacityResult(int Capacity, CapacitySource Source);

/// <summary>
/// A lightweight, storage-agnostic description of a recurring job consumed by the pure heatmap
/// engines. It deliberately avoids Hangfire's <c>RecurringJobDto</c> so the engines remain trivially
/// testable and decoupled from Hangfire model changes; <c>HeatmapService</c> maps the Hangfire type
/// onto this spec.
/// </summary>
/// <param name="JobId">The recurring job identifier.</param>
/// <param name="CronExpression">The job's cron expression (5- or 6-field, Hangfire/Cronos syntax).</param>
/// <param name="TimeZoneId">
/// The configured IANA or Windows time-zone identifier; <c>null</c> or empty means the schedule is
/// evaluated in Coordinated Universal Time (UTC) (Req 1.4, 8.3).
/// </param>
/// <param name="Queue">The resolved queue name; <c>default</c> when the job's queue is unknown (Req 2.4).</param>
/// <param name="EstimatedDuration">The job's estimated execution duration; treated as at least 1 minute.</param>
/// <param name="EstimatedDurationIsDefault">
/// <c>true</c> when <paramref name="EstimatedDuration"/> was derived from the configured default
/// rather than historical data.
/// </param>
public sealed record RecurringJobSpec(
    string JobId,
    string CronExpression,
    string TimeZoneId,
    string Queue,
    TimeSpan EstimatedDuration,
    bool EstimatedDurationIsDefault);

/// <summary>
/// A single projected execution of a recurring job within the active projection window.
/// </summary>
/// <param name="JobId">The recurring job identifier this fire originates from.</param>
/// <param name="Queue">The queue the fire is attributed to.</param>
/// <param name="FireTimeUtc">The absolute fire instant normalized to UTC.</param>
/// <param name="EstimatedDuration">The fire's estimated execution duration.</param>
public sealed record ProjectedFire(
    string JobId,
    string Queue,
    DateTimeOffset FireTimeUtc,
    TimeSpan EstimatedDuration);

/// <summary>
/// The result of projecting a set of recurring jobs over a window: the in-window fires plus the
/// diagnostic notices the heatmap surfaces to the operator.
/// </summary>
/// <param name="Fires">Every in-window projected fire across all parseable jobs.</param>
/// <param name="UnparseableJobIds">Identifiers of jobs whose cron expression could not be parsed (Req 1.6).</param>
/// <param name="UnknownTimeZoneJobIds">Identifiers of jobs whose configured time-zone id was unrecognized and evaluated in UTC (Req 8.6).</param>
/// <param name="LongPeriodJobIds">Identifiers of jobs whose recurrence period exceeds seven days (Req 9.5).</param>
public sealed record ProjectionResult(
    IReadOnlyList<ProjectedFire> Fires,
    IReadOnlyList<string> UnparseableJobIds,
    IReadOnlyList<string> UnknownTimeZoneJobIds,
    IReadOnlyList<string> LongPeriodJobIds);

/// <summary>
/// The address of a single heatmap bucket within the <c>queue × day × hour</c> matrix.
/// </summary>
/// <param name="Queue">The queue the bucket belongs to.</param>
/// <param name="DayIndex">The zero-based day index within the projection window.</param>
/// <param name="Hour">The clock hour of the bucket, in the range 0..23.</param>
public sealed record CellKey(string Queue, int DayIndex, int Hour);

/// <summary>
/// A single populated (or derived) cell of the heatmap matrix together with the data needed to
/// render and drill into it.
/// </summary>
/// <param name="Key">The cell's <c>queue × day × hour</c> address.</param>
/// <param name="Value">The cell's load value under the active <see cref="LoadMetric"/>.</param>
/// <param name="ContributingJobCount">The number of distinct jobs contributing to the cell.</param>
/// <param name="DominantQueue">The queue contributing the greatest load (alphabetically smallest on ties).</param>
/// <param name="JobIds">The identifiers of the jobs whose fires populate the cell.</param>
public sealed record HeatmapCell(
    CellKey Key,
    double Value,
    int ContributingJobCount,
    string DominantQueue,
    IReadOnlyList<string> JobIds);

/// <summary>
/// The aggregated <c>queue × day × hour</c> heatmap matrix with its display domain.
/// </summary>
/// <param name="Cells">The populated cells keyed by their <see cref="CellKey"/>.</param>
/// <param name="Queues">The distinct queues represented in the matrix.</param>
/// <param name="Window">The projection window the matrix was computed over.</param>
/// <param name="Metric">The load metric used to compute cell values.</param>
/// <param name="Min">The minimum cell value across the matrix (the ramp domain lower bound).</param>
/// <param name="Max">The maximum cell value across the matrix (the ramp domain upper bound).</param>
public sealed record HeatmapMatrix(
    IReadOnlyDictionary<CellKey, HeatmapCell> Cells,
    IReadOnlyList<string> Queues,
    ProjectionWindow Window,
    LoadMetric Metric,
    double Min,
    double Max);

/// <summary>
/// Per-cell historical execution statistics for a single <c>day × hour</c> position, already
/// collapsed across the queue dimension. Supplied to the Punchcard and Calendar views (alongside the
/// aggregated <see cref="HeatmapMatrix"/>) so they can recolor cells by failure rate or p95 duration
/// under the Historical source — data the <see cref="HeatmapMatrix"/> itself does not carry, since
/// <see cref="HeatmapCell"/> only retains a single load <see cref="HeatmapCell.Value"/>.
/// </summary>
/// <param name="DayIndex">The zero-based day index within the projection window (0..6).</param>
/// <param name="Hour">The clock hour of the cell, in the range 0..23.</param>
/// <param name="FireCount">The number of recurring-originated executions in the cell (0 or greater).</param>
/// <param name="FailureCount">The number of those executions that failed (0..<see cref="FireCount"/>).</param>
/// <param name="P95Ms">The 95th-percentile execution duration, in milliseconds, across the cell.</param>
public sealed record HeatmapHistoricalCell(
    int DayIndex,
    int Hour,
    long FireCount,
    long FailureCount,
    double P95Ms)
{
    /// <summary>
    /// <c>true</c> when the cell has at least one historical execution; cells without data are
    /// rendered at the Color_Ramp's empty shade rather than as a zero-failure-rate value (Req 7.4).
    /// </summary>
    public bool HasData => FireCount >= 1;

    /// <summary>
    /// The cell's failure rate on a 0.0 (no failures) … 1.0 (all failed) scale, computed as
    /// <c>FailureCount / FireCount</c> for cells with a fire count of one or greater; cells with no
    /// fires report 0.0 (Req 7.6, design Property 26).
    /// </summary>
    public double FailureRate => FireCount >= 1 ? (double)FailureCount / FireCount : 0d;
}

/// <summary>
/// A single queue's per-slot concurrency series used to render the stacked-by-queue concurrency view.
/// </summary>
/// <param name="Queue">The queue this series belongs to.</param>
/// <param name="ConcurrencyPerSlot">The concurrency value for each one-minute slot of the day.</param>
public sealed record QueueConcurrencySeries(
    string Queue,
    IReadOnlyList<int> ConcurrencyPerSlot);

/// <summary>
/// The result of a duration-aware concurrency analysis over a single day's one-minute slots.
/// </summary>
/// <param name="PeakConcurrency">The maximum concurrency observed across the day.</param>
/// <param name="PeakMinuteOfDay">The earliest minute-of-day at which the peak occurs; <c>null</c> when the day has no fires.</param>
/// <param name="OverCapacitySlotCount">The number of one-minute slots whose concurrency exceeds the worker capacity.</param>
/// <param name="PerQueueSeries">The per-queue stacked concurrency series.</param>
public sealed record ConcurrencyResult(
    int PeakConcurrency,
    int? PeakMinuteOfDay,
    int OverCapacitySlotCount,
    IReadOnlyList<QueueConcurrencySeries> PerQueueSeries);

/// <summary>
/// The severity attached to a stagger recommendation.
/// </summary>
public enum RecommendationSeverity
{
    /// <summary>The detected peak does not exceed worker capacity.</summary>
    Standard,

    /// <summary>The detected peak exceeds worker capacity.</summary>
    High
}

/// <summary>
/// A read-only stagger recommendation produced by the recommendation engine.
/// </summary>
/// <param name="Queue">The queue the recommendation applies to.</param>
/// <param name="PeakMinuteOfDay">The minute-of-day of the detected peak cluster.</param>
/// <param name="Weekdays">The weekdays on which the cluster occurs.</param>
/// <param name="CurrentPeak">The current peak concurrency at the cluster.</param>
/// <param name="StaggeredPeak">The peak concurrency after the simulated stagger.</param>
/// <param name="Severity">Whether the recommendation is standard or high severity.</param>
/// <param name="CollidesWithHighDemand">Whether the suggested window collides with high ad-hoc demand.</param>
/// <param name="SuggestedMinuteOfDay">The suggested alternative minute-of-day; <c>null</c> when none is available.</param>
public sealed record Recommendation(
    string Queue,
    int PeakMinuteOfDay,
    IReadOnlyList<DayOfWeek> Weekdays,
    int CurrentPeak,
    int StaggeredPeak,
    RecommendationSeverity Severity,
    bool CollidesWithHighDemand,
    int? SuggestedMinuteOfDay);

/// <summary>
/// The data source backing a heatmap request.
/// </summary>
public enum HeatmapSource
{
    /// <summary>Fire times projected from cron expressions (storage-agnostic).</summary>
    Projected,

    /// <summary>Historical recurring-job executions read from the metrics provider.</summary>
    Historical
}

/// <summary>
/// A self-contained description of a heatmap request: the data source, projection window, and all of
/// the operator-selected display and analysis controls.
/// </summary>
/// <param name="Source">The data source (projected or historical).</param>
/// <param name="WindowKind">The projection window kind (idealized week or next seven days).</param>
/// <param name="ViewerTimeZoneId">The viewer's time-zone id used to bucket fires; <c>null</c> or empty means UTC.</param>
/// <param name="JobClass">The job-class filter (e.g. Cron, Combined).</param>
/// <param name="LoadMetric">The per-cell load metric.</param>
/// <param name="TopN">The number of top queues to display.</param>
/// <param name="HideSubHourly">Whether to hide sub-hourly (high-frequency) jobs.</param>
/// <param name="LogScale">Whether to apply logarithmic intensity scaling.</param>
/// <param name="LookbackWeeks">The historical/demand lookback span, in weeks.</param>
/// <param name="AggregationStatistic">The demand aggregation statistic (e.g. Average, p95).</param>
/// <param name="ManualCapacity">An optional manual worker-capacity override; <c>null</c> uses the detected capacity.</param>
public sealed record HeatmapQuery(
    HeatmapSource Source,
    ProjectionWindowKind WindowKind,
    string ViewerTimeZoneId,
    string JobClass,
    LoadMetric LoadMetric,
    int TopN,
    bool HideSubHourly,
    bool LogScale,
    int LookbackWeeks,
    string AggregationStatistic,
    int? ManualCapacity);

/// <summary>
/// The result of a heatmap matrix request: the aggregated matrix together with the diagnostic
/// notices surfaced to the operator.
/// </summary>
/// <param name="Matrix">The aggregated heatmap matrix.</param>
/// <param name="UnparseableJobIds">Identifiers of jobs whose cron expression could not be parsed (Req 1.6).</param>
/// <param name="UnknownTimeZoneJobIds">Identifiers of jobs whose time-zone id was unrecognized and evaluated in UTC (Req 8.6).</param>
/// <param name="LongPeriodJobIds">Identifiers of jobs whose recurrence period exceeds seven days (Req 9.5).</param>
/// <param name="HistoricalError">
/// A notice describing a historical-source failure that caused a revert to the projected source;
/// <c>null</c> when no such error occurred (Req 7.5).
/// </param>
public sealed record HeatmapResult(
    HeatmapMatrix Matrix,
    IReadOnlyList<string> UnparseableJobIds,
    IReadOnlyList<string> UnknownTimeZoneJobIds,
    IReadOnlyList<string> LongPeriodJobIds,
    string HistoricalError);

/// <summary>
/// A single recurring job contributing to a drilled-into heatmap cell.
/// </summary>
/// <param name="JobId">The recurring job identifier.</param>
/// <param name="CronExpression">The job's cron expression.</param>
/// <param name="Queue">The queue the job runs on.</param>
/// <param name="EstimatedDuration">The job's estimated execution duration.</param>
/// <param name="NextRunUtc">The job's next projected run instant in UTC; <c>null</c> when none is known.</param>
public sealed record DrillDownJob(
    string JobId,
    string CronExpression,
    string Queue,
    TimeSpan EstimatedDuration,
    DateTimeOffset? NextRunUtc);

/// <summary>
/// The result of drilling into a heatmap cell: the contributing jobs sorted by next run plus an
/// optional error indication when the lookup failed.
/// </summary>
/// <param name="Jobs">The contributing jobs (sorted by next run).</param>
/// <param name="Error">An error message when the drill-down lookup failed; <c>null</c> on success (Req 10.7).</param>
public sealed record DrillDownResult(
    IReadOnlyList<DrillDownJob> Jobs,
    string Error);

/// <summary>
/// The statistic used to summarize each <see cref="DemandProfile"/> slot across the lookback window
/// (the glossary's <c>Aggregation_Statistic</c>, Req 16.4).
/// </summary>
public enum AggregationStatistic
{
    /// <summary>The arithmetic mean of the slot's per-week occurrences within the lookback.</summary>
    Average,

    /// <summary>The 95th percentile of the slot's per-week occurrences within the lookback.</summary>
    P95
}

/// <summary>
/// The address of a single <see cref="DemandProfile"/> slot in <c>queue × day-of-week × hour</c>
/// space. Unlike <see cref="CellKey"/> (whose <c>DayIndex</c> is a position within a projection
/// window), the demand profile is keyed by a calendar day-of-week because the underlying
/// <c>Demand_Rollup</c> aggregates ad-hoc executions per day-of-week and hour.
/// </summary>
/// <param name="Queue">The queue the slot belongs to.</param>
/// <param name="DayOfWeek">The day-of-week of the slot (0 = Sunday … 6 = Saturday, matching <see cref="System.DayOfWeek"/>).</param>
/// <param name="Hour">The clock hour of the slot, in the range 0..23 (UTC, as stored by the rollup).</param>
public sealed record DemandSlotKey(string Queue, int DayOfWeek, int Hour);

/// <summary>
/// A historical, aggregated <c>queue × day-of-week × hour</c> profile of ad-hoc (on-demand) load
/// computed over the <c>Lookback_Window</c> (Req 16.3). Each slot value is summarized using the
/// selected <see cref="AggregationStatistic"/> across the lookback's per-week occurrences of that
/// day-of-week and hour (Req 16.4). The profile also reports the actual historical span available so
/// the UI can indicate when it is shorter than the requested lookback (Req 16.8, 17.4).
/// </summary>
/// <param name="Slots">
/// The per-slot statistic values keyed by <see cref="DemandSlotKey"/>. Slots with no observed ad-hoc
/// demand over the available span are absent (treated as zero by consumers).
/// </param>
/// <param name="Queues">The distinct queues represented in the profile (ascending order).</param>
/// <param name="Metric">The load metric the slot values are expressed in (fire count or worker-minutes).</param>
/// <param name="Statistic">The aggregation statistic applied per slot.</param>
/// <param name="RequestedLookbackWeeks">The lookback span the operator selected, in weeks.</param>
/// <param name="AvailableSpanWeeks">The number of distinct weeks of rollup data actually available within the lookback.</param>
/// <param name="IsSpanReduced"><c>true</c> when <paramref name="AvailableSpanWeeks"/> is less than <paramref name="RequestedLookbackWeeks"/> (Req 16.8, 17.4).</param>
/// <param name="Min">The minimum slot value across the profile (the ramp domain lower bound); 0 when empty.</param>
/// <param name="Max">The maximum slot value across the profile (the ramp domain upper bound); 0 when empty.</param>
public sealed record DemandProfile(
    IReadOnlyDictionary<DemandSlotKey, double> Slots,
    IReadOnlyList<string> Queues,
    LoadMetric Metric,
    AggregationStatistic Statistic,
    int RequestedLookbackWeeks,
    int AvailableSpanWeeks,
    bool IsSpanReduced,
    double Min,
    double Max)
{
    /// <summary>
    /// An empty profile with no slots, reporting a (reduced) zero-week available span. Used when the
    /// rollup is empty or unreadable, or when no metrics provider is registered.
    /// </summary>
    public static DemandProfile Empty(
        LoadMetric metric, AggregationStatistic statistic, int requestedLookbackWeeks)
        => new(
            new Dictionary<DemandSlotKey, double>(),
            Array.Empty<string>(),
            metric,
            statistic,
            requestedLookbackWeeks,
            AvailableSpanWeeks: 0,
            IsSpanReduced: requestedLookbackWeeks > 0,
            Min: 0,
            Max: 0);
}

/// <summary>
/// The contextual metadata written into a CSV export so the file is self-describing (Req 12.4).
/// </summary>
/// <param name="Source">The active data source.</param>
/// <param name="Window">The projection window the matrix was computed over.</param>
/// <param name="ViewerTimeZoneId">The viewer time-zone id used to bucket fires; <c>null</c> or empty means UTC.</param>
/// <param name="Queues">The queue selection represented in the export.</param>
/// <param name="Metric">The load metric used to compute cell values.</param>
public sealed record CsvExportContext(
    HeatmapSource Source,
    ProjectionWindow Window,
    string ViewerTimeZoneId,
    IReadOnlyList<string> Queues,
    LoadMetric Metric);
