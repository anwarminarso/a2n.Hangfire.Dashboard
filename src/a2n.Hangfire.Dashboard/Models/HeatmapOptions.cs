using System;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Configuration for the Recurring Schedule Heatmap page. Mirrors <see cref="AuditLogOptions"/> and
/// <see cref="QueueOperationsOptions"/>: it gates the page (<see cref="Enabled"/>) and supplies the
/// default selections shown when the page first loads, plus caching and historical query timeouts.
/// </summary>
/// <remarks>
/// Defaults follow the design's "Data Models" section: Fire-count load metric, Average aggregation
/// statistic, 4-week lookback, an Idealized-week projection window, a default estimated duration of
/// at least one minute, a 60-second aggregation cache TTL, and a 10-second historical query timeout.
/// </remarks>
public class HeatmapOptions
{
    /// <summary>
    /// Whether the heatmap page and its navigation entry are available. When false the route and
    /// nav item are hidden. Default: true. (Req 22.3)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The default Job_Class selection. When a metrics provider is registered the page may prefer
    /// "Combined"; otherwise "Cron" is the storage-agnostic default. (Req 22.5)
    /// </summary>
    public string DefaultJobClass { get; set; } = "Cron";

    /// <summary>
    /// The default load metric used for cell values. Default: <see cref="LoadMetric.FireCount"/>. (Req 22.5)
    /// </summary>
    public LoadMetric DefaultLoadMetric { get; set; } = LoadMetric.FireCount;

    /// <summary>
    /// The default aggregation statistic for the Demand Profile lookback ("Average" or "p95").
    /// Default: "Average". (Req 22.5)
    /// </summary>
    public string DefaultAggregationStatistic { get; set; } = "Average";

    /// <summary>
    /// The default Demand Profile lookback span, in weeks (1, 4, or 8). Default: 4. (Req 22.5)
    /// </summary>
    public int DefaultLookbackWeeks { get; set; } = 4;

    /// <summary>
    /// The default projection window. Default: <see cref="ProjectionWindowKind.IdealizedWeek"/>. (Req 22.5)
    /// </summary>
    public ProjectionWindowKind DefaultProjectionWindow { get; set; } = ProjectionWindowKind.IdealizedWeek;

    /// <summary>
    /// The default estimated job duration used when no historical p95 is available. Must be at least
    /// one minute; values below one minute are treated as one minute by the duration resolver.
    /// Default: 1 minute. (Req 21.3)
    /// </summary>
    public TimeSpan DefaultEstimatedDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Time-to-live, in seconds, for the cached aggregation keyed by (source, window, viewer tz).
    /// Default: 60. (Req 13.5)
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 60;

    /// <summary>
    /// Timeout, in seconds, for a historical metrics query before reverting to the Projected source
    /// with a dismissible notice. Default: 10. (Req 7.5)
    /// </summary>
    public int HistoricalQueryTimeoutSeconds { get; set; } = 10;
}
