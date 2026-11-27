using System;

namespace a2n.Hangfire.Dashboard.Models;

/// <summary>
/// Predefined time range presets for analytics queries.
/// </summary>
public enum TimeRangePreset
{
    Last1h,
    Last6h,
    Last24h,
    Last7d,
    Last30d,
    Custom
}

/// <summary>
/// Represents a selected time range with computed From/To values.
/// Used by TimeRangeSelector to communicate the selected range to analytics pages.
/// </summary>
public class TimeRangeSelection
{
    public TimeRangePreset Preset { get; set; } = TimeRangePreset.Last24h;
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Creates a TimeRangeSelection from a preset (computes From/To based on current time).
    /// </summary>
    public static TimeRangeSelection FromPreset(TimeRangePreset preset)
    {
        var now = DateTimeOffset.UtcNow;
        var from = preset switch
        {
            TimeRangePreset.Last1h => now.AddHours(-1),
            TimeRangePreset.Last6h => now.AddHours(-6),
            TimeRangePreset.Last24h => now.AddHours(-24),
            TimeRangePreset.Last7d => now.AddDays(-7),
            TimeRangePreset.Last30d => now.AddDays(-30),
            _ => now.AddHours(-24)
        };

        return new TimeRangeSelection
        {
            Preset = preset,
            From = from,
            To = now
        };
    }

    /// <summary>
    /// Creates a TimeRangeSelection from custom date range.
    /// </summary>
    public static TimeRangeSelection FromCustom(DateTimeOffset from, DateTimeOffset to)
    {
        return new TimeRangeSelection
        {
            Preset = TimeRangePreset.Custom,
            From = from,
            To = to
        };
    }

    /// <summary>
    /// Maximum allowed span for custom date ranges (90 days).
    /// </summary>
    public static readonly TimeSpan MaxCustomSpan = TimeSpan.FromDays(90);
}
