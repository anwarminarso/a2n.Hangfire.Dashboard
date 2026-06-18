using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for <see cref="ScheduleAggregator.Aggregate"/> bucketing across Daylight Saving
/// Time transitions.
///
/// **Property 8: DST transitions resolve to exactly one deterministic bucket**
/// **Validates: Requirements 8.7**
///
/// For any fire whose converted local time falls within a Daylight Saving Time gap (spring-forward)
/// or overlap (fall-back), the aggregator assigns it to exactly one one-hour bucket determined by
/// its absolute (UTC-equivalent) instant, and repeating the assignment for the same input always
/// yields the same bucket.
/// </summary>
public class DstBucketingProperties
{
    /// <summary>The fixed number of days every projection window spans.</summary>
    private const int WindowDays = 7;

    /// <summary>
    /// Real DST-observing viewer zones (resolved through the project's own cross-platform resolver
    /// so the host can resolve them by IANA id on Linux/macOS or by the translated Windows id)
    /// paired with the absolute UTC instants at which each zone changes its offset. Each entry lets
    /// the generator anchor a window on a known spring-forward gap or fall-back overlap so fires can
    /// be placed right inside the transition. Empty when the host resolves no DST zones — in which
    /// case the property skips gracefully.
    /// </summary>
    private static readonly (TimeZoneInfo Zone, DateTimeOffset TransitionUtc)[] DstScenarios =
        BuildDstScenarios();

    private static (TimeZoneInfo Zone, DateTimeOffset TransitionUtc)[] BuildDstScenarios()
    {
        var zones = new List<TimeZoneInfo>();
        foreach (var id in new[] { "America/New_York", "Europe/London", "Australia/Sydney" })
        {
            if (HeatmapTime.TryResolveTimeZone(id, out var tz) && !zones.Contains(tz))
            {
                zones.Add(tz);
            }
        }

        var scenarios = new List<(TimeZoneInfo, DateTimeOffset)>();

        // Scan a multi-year span at hourly resolution and record every instant at which the zone's
        // UTC offset changes — these are the spring-forward / fall-back transitions. Anchoring near
        // them guarantees the generated fires actually land in DST gaps and overlaps rather than
        // relying on a rare random window happening to straddle a transition.
        var start = new DateTimeOffset(2018, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        foreach (var tz in zones)
        {
            var prevOffset = tz.GetUtcOffset(start);
            for (var t = start.AddHours(1); t < end; t = t.AddHours(1))
            {
                var offset = tz.GetUtcOffset(t);
                if (offset != prevOffset)
                {
                    scenarios.Add((tz, t));
                    prevOffset = offset;
                }
            }
        }

        return scenarios.ToArray();
    }

    /// <summary>A handful of distinct, non-whitespace queue names (avoids the default-queue rule).</summary>
    private static Gen<string> QueueGen => Gen.Elements("alpha", "queue-1", "Reports", "zeta");

    private static Gen<ProjectionWindowKind> KindGen =>
        Gen.Elements(ProjectionWindowKind.IdealizedWeek, ProjectionWindowKind.Next7Days);

    /// <summary>
    /// **Property 8: DST transitions resolve to exactly one deterministic bucket**
    /// **Validates: Requirements 8.7**
    ///
    /// A single fire is placed within &#177;2 hours of a real DST transition (so its converted local
    /// time lands in the spring-forward gap or the fall-back overlap) inside a window anchored on
    /// that transition, then aggregated under the Fire-count metric. The test asserts:
    /// (a) the fire produces exactly one populated cell — i.e. it is assigned to exactly one bucket;
    /// (b) determinism — aggregating the same fire again yields the identical <see cref="CellKey"/>;
    /// (c) the bucket equals the one derived from the absolute instant via
    /// <see cref="HeatmapTime.GetBucket"/>, which resolves the bucket from the UTC-equivalent instant.
    /// When the host can resolve no DST zones, the property passes trivially (graceful skip).
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Dst_ResolvesToOneDeterministicBucket()
    {
        if (DstScenarios.Length == 0)
        {
            // Graceful skip: no DST-observing zone is resolvable on this host.
            return true.ToProperty().Label("skipped: no DST time zones resolvable on host");
        }

        var arb = Arb.From(
            from scenario in Gen.Elements(DstScenarios)
            from kind in KindGen
            from offsetMinutes in Gen.Choose(-120, 120)
            from second in Gen.Choose(0, 59)
            from queue in QueueGen
            select (scenario, kind, offsetMinutes, second, queue));

        return Prop.ForAll(arb, input =>
        {
            var (scenario, kind, offsetMinutes, second, queue) = input;
            var (zone, transitionUtc) = scenario;

            // Anchor a real-zone window on the transition so the transition (and the fires near it)
            // fall inside the half-open [start, end) window.
            var window = HeatmapTime.BuildWindow(kind, transitionUtc, zone);

            // Place the fire near the transition, then clamp into the window so its day index stays
            // in [0, 6]. The fire's local time therefore lands in the DST gap or overlap.
            var candidate = transitionUtc.AddMinutes(offsetMinutes).AddSeconds(second);
            var minInstant = window.StartInclusive;
            var maxInstant = window.EndExclusive.AddMinutes(-1);
            var clamped = candidate < minInstant ? minInstant
                        : candidate > maxInstant ? maxInstant
                        : candidate;
            var fireInstantUtc = clamped.ToUniversalTime();

            var fire = new ProjectedFire(
                JobId: "job-1",
                Queue: queue,
                FireTimeUtc: fireInstantUtc,
                EstimatedDuration: TimeSpan.FromMinutes(1));

            var matrix = ScheduleAggregator.Aggregate(
                new[] { fire }, LoadMetric.FireCount, zone, window);

            // (a) Exactly one populated cell => the fire is assigned to exactly one bucket (Req 8.7).
            if (matrix.Cells.Count != 1)
            {
                return false.Label(
                    $"expected exactly 1 cell but got {matrix.Cells.Count} " +
                    $"(zone={zone.Id}, fireUtc={fireInstantUtc:o}, transitionUtc={transitionUtc:o})");
            }

            var key = matrix.Cells.Keys.Single();

            // (b) Determinism: repeating the same assignment yields the identical bucket (Req 8.7).
            var matrixAgain = ScheduleAggregator.Aggregate(
                new[] { fire }, LoadMetric.FireCount, zone, window);
            var keyAgain = matrixAgain.Cells.Keys.Single();
            if (key != keyAgain)
            {
                return false.Label(
                    $"non-deterministic bucket: first={key} second={keyAgain} " +
                    $"(zone={zone.Id}, fireUtc={fireInstantUtc:o})");
            }

            // (c) The bucket matches the one derived from the absolute (UTC-equivalent) instant.
            var (expectedDay, expectedHour) = HeatmapTime.GetBucket(fireInstantUtc, zone, window);
            if (key.Queue != queue || key.DayIndex != expectedDay || key.Hour != expectedHour)
            {
                return false.Label(
                    $"bucket mismatch: expected (queue={queue}, day={expectedDay}, hour={expectedHour}) " +
                    $"but got (queue={key.Queue}, day={key.DayIndex}, hour={key.Hour}); " +
                    $"zone={zone.Id} fireUtc={fireInstantUtc:o} transitionUtc={transitionUtc:o}");
            }

            // The day index for an in-window fire is always within [0, 6].
            if (key.DayIndex < 0 || key.DayIndex > WindowDays - 1)
            {
                return false.Label($"day index out of range: {key.DayIndex} (zone={zone.Id})");
            }

            // GetBucket itself must be deterministic for the same absolute instant.
            var (day2, hour2) = HeatmapTime.GetBucket(fireInstantUtc, zone, window);
            if (day2 != expectedDay || hour2 != expectedHour)
            {
                return false.Label(
                    $"GetBucket non-deterministic: ({expectedDay},{expectedHour}) vs ({day2},{hour2}) " +
                    $"(zone={zone.Id}, fireUtc={fireInstantUtc:o})");
            }

            return true.ToProperty();
        });
    }
}
