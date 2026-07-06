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
/// Property tests for the shared color-ramp domain used by the per-queue small-multiples view.
///
/// **Property 11: Per-queue small multiples share a single global ramp domain**
/// **Validates: Requirements 3.7**
///
/// For any set of visible queues and their cells, the color-ramp scale domain used for every
/// small-multiple is <c>[global minimum cell value, global maximum cell value]</c> across all
/// visible queues, so equal values render at equal intensity in every small-multiple.
///
/// There is no dedicated "global domain across visible queues" helper in the source: the global
/// domain is exposed directly by <see cref="HeatmapMatrix.Min"/> / <see cref="HeatmapMatrix.Max"/>
/// produced by <see cref="ScheduleAggregator.Aggregate"/> across all visible queues, and the
/// per-queue renderers shade every small-multiple through <see cref="Intensity"/> with that single
/// shared domain. This property pins both facts:
/// <list type="number">
/// <item>the matrix domain equals the union of every visible queue's value range
/// (<c>Min</c> = the smallest cell value across all queues, <c>Max</c> = the largest); and</item>
/// <item>mapping any value through the shared <c>[Min, Max]</c> domain yields the same intensity
/// regardless of which queue's small-multiple it appears in, so equal values render at equal
/// intensity everywhere.</item>
/// </list>
/// </summary>
public class PerQueueRampDomainProperties
{
    /// <summary>The candidate queues fires are distributed across, so the matrix spans several visible queues.</summary>
    private static readonly string[] Queues = { "alpha", "bravo", "charlie", "delta" };

    /// <summary>Viewer time zones under test: UTC plus a couple of fixed offsets to vary bucketing.</summary>
    private static readonly TimeZoneInfo[] TimeZones =
    {
        TimeZoneInfo.Utc,
        TimeZoneInfo.CreateCustomTimeZone("Test+05:30", new TimeSpan(5, 30, 0), "Test +05:30", "Test +05:30"),
        TimeZoneInfo.CreateCustomTimeZone("Test-08:00", new TimeSpan(-8, 0, 0), "Test -08:00", "Test -08:00"),
    };

    /// <summary>The number of one-minute offsets inside a seven-day window.</summary>
    private const int WindowMinutes = 7 * 24 * 60;

    private static Gen<TimeZoneInfo> TimeZoneGen => Gen.Elements(TimeZones);

    private static Gen<LoadMetric> MetricGen => Gen.Elements(LoadMetric.FireCount, LoadMetric.WorkerMinutes);

    private static Gen<IntensityScale> ScaleGen => Gen.Elements(IntensityScale.Linear, IntensityScale.Logarithmic);

    private static Gen<ProjectionWindowKind> KindGen =>
        Gen.Elements(ProjectionWindowKind.IdealizedWeek, ProjectionWindowKind.Next7Days);

    /// <summary>Base "now" UTC instants spread across ~30 years at one-minute resolution.</summary>
    private static Gen<DateTimeOffset> BaseNowGen =>
        Gen.Choose(0, 16_000_000)
            .Select(minutes => new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    /// <summary>
    /// A single fire descriptor: a queue, a one-minute offset into the window, and an
    /// estimated-duration in whole minutes (so Worker-minutes values collide across queues, which
    /// is exactly what makes the equal-intensity property meaningful).
    /// </summary>
    private static Gen<(string Queue, int MinuteOffset, int DurationMinutes)> FireDescGen =>
        from queue in Gen.Elements(Queues)
        from offset in Gen.Choose(0, WindowMinutes - 1)
        from durationMinutes in Gen.Choose(1, 10)
        select (queue, offset, durationMinutes);

    /// <summary>
    /// **Property 11: Per-queue small multiples share a single global ramp domain**
    /// **Validates: Requirements 3.7**
    ///
    /// Across a matrix built from fires spanning multiple visible queues, the matrix's
    /// <c>[Min, Max]</c> domain equals the union of every queue's per-queue value range, and any
    /// value mapped through that shared domain produces the same intensity (ramp index, normalized
    /// value, and bubble area) irrespective of the queue whose small-multiple it belongs to — so
    /// equal values render at equal intensity in every small-multiple.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property PerQueueSmallMultiples_ShareSingleGlobalRampDomain()
    {
        var arb = Arb.From(
            from baseNow in BaseNowGen
            from kind in KindGen
            from tz in TimeZoneGen
            from metric in MetricGen
            from scale in ScaleGen
            from count in Gen.Choose(0, 60)
            from descs in Gen.ArrayOf(count, FireDescGen)
            select (baseNow, kind, tz, metric, scale, descs));

        return Prop.ForAll(arb, input =>
        {
            var (baseNow, kind, tz, metric, scale, descs) = input;

            var window = HeatmapTime.BuildWindow(kind, baseNow, TimeZoneInfo.Utc);

            var fires = descs
                .Select((d, i) => new ProjectedFire(
                    JobId: $"job-{i}",
                    Queue: d.Queue,
                    FireTimeUtc: window.StartInclusive.AddMinutes(d.MinuteOffset),
                    EstimatedDuration: TimeSpan.FromMinutes(d.DurationMinutes)))
                .ToList();

            var matrix = ScheduleAggregator.Aggregate(fires, metric, tz, window);
            var cells = matrix.Cells.Values.ToList();

            // (1) The shared ramp domain spans all visible queues.
            if (cells.Count == 0)
            {
                // No visible queues / cells: the domain collapses to the deterministic [0, 0].
                if (matrix.Min != 0d || matrix.Max != 0d)
                {
                    return false.Label($"empty matrix domain not [0,0]: [{matrix.Min}, {matrix.Max}]");
                }
            }
            else
            {
                var globalMin = cells.Min(c => c.Value);
                var globalMax = cells.Max(c => c.Value);

                if (matrix.Min != globalMin || matrix.Max != globalMax)
                {
                    return false.Label(
                        $"matrix domain != global cell range: matrix=[{matrix.Min}, {matrix.Max}] " +
                        $"cells=[{globalMin}, {globalMax}]");
                }

                // The global domain must equal the union of each visible queue's own value range:
                // Min = the smallest per-queue minimum, Max = the largest per-queue maximum (Req 3.7).
                var perQueue = cells
                    .GroupBy(c => c.Key.Queue, StringComparer.Ordinal)
                    .Select(g => (Min: g.Min(c => c.Value), Max: g.Max(c => c.Value)))
                    .ToList();

                var unionMin = perQueue.Min(q => q.Min);
                var unionMax = perQueue.Max(q => q.Max);

                if (matrix.Min != unionMin || matrix.Max != unionMax)
                {
                    return false.Label(
                        $"shared domain not the union over visible queues: matrix=[{matrix.Min}, {matrix.Max}] " +
                        $"union=[{unionMin}, {unionMax}]");
                }
            }

            // (2) Equal values render at equal intensity in every small-multiple. Because every
            // small-multiple is shaded through the single shared [Min, Max] domain, two cells with
            // the same value map to the same ramp index / normalized intensity / bubble area no
            // matter which queue they belong to.
            foreach (var byValue in cells.GroupBy(c => c.Value))
            {
                var representative = byValue.First();
                var expectedIndex = Intensity.RampIndex(representative.Value, matrix.Min, matrix.Max, scale);
                var expectedNorm = Intensity.Normalize(representative.Value, matrix.Min, matrix.Max, scale);
                var expectedArea = Intensity.BubbleArea(representative.Value, matrix.Min, matrix.Max, 100d, scale);

                foreach (var cell in byValue)
                {
                    var index = Intensity.RampIndex(cell.Value, matrix.Min, matrix.Max, scale);
                    var norm = Intensity.Normalize(cell.Value, matrix.Min, matrix.Max, scale);
                    var area = Intensity.BubbleArea(cell.Value, matrix.Min, matrix.Max, 100d, scale);

                    if (index != expectedIndex || norm != expectedNorm || area != expectedArea)
                    {
                        return false.Label(
                            $"equal value {cell.Value} rendered differently across queues " +
                            $"('{representative.Key.Queue}' vs '{cell.Key.Queue}'): " +
                            $"index {expectedIndex} vs {index}, norm {expectedNorm} vs {norm}, " +
                            $"area {expectedArea} vs {area}");
                    }
                }
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// **Property 11 (anchor): equal values across two queues map to the same ramp shade.**
    /// **Validates: Requirements 3.7**
    ///
    /// A hand-built matrix where queue <c>alpha</c> and queue <c>bravo</c> each have a cell of the
    /// same value but different per-queue ranges. Under a single shared domain both equal-valued
    /// cells resolve to the same ramp index; a (rejected) per-queue domain would shade them
    /// differently.
    /// </summary>
    [Fact]
    public void EqualValues_AcrossQueues_ShareTheSameShade_UnderGlobalDomain()
    {
        var start = new DateTimeOffset(2024, 3, 4, 0, 0, 0, TimeSpan.Zero);
        var window = new ProjectionWindow(start, start.AddDays(7), ProjectionWindowKind.IdealizedWeek);

        // alpha: one fire in hour 0 (value 1), three fires in hour 1 (value 3).
        // bravo: one fire in hour 0 (value 1) only — its per-queue range is [1, 1].
        var fires = new List<ProjectedFire>
        {
            new("a1", "alpha", start.AddHours(0).AddMinutes(0), TimeSpan.FromMinutes(1)),
            new("a2", "alpha", start.AddHours(1).AddMinutes(0), TimeSpan.FromMinutes(1)),
            new("a3", "alpha", start.AddHours(1).AddMinutes(1), TimeSpan.FromMinutes(1)),
            new("a4", "alpha", start.AddHours(1).AddMinutes(2), TimeSpan.FromMinutes(1)),
            new("b1", "bravo", start.AddHours(0).AddMinutes(0), TimeSpan.FromMinutes(1)),
        };

        var matrix = ScheduleAggregator.Aggregate(fires, LoadMetric.FireCount, TimeZoneInfo.Utc, window);

        // Global domain spans both queues: min value 1, max value 3.
        Assert.Equal(1d, matrix.Min);
        Assert.Equal(3d, matrix.Max);

        var alphaHour0 = matrix.Cells[new CellKey("alpha", 0, 0)];
        var bravoHour0 = matrix.Cells[new CellKey("bravo", 0, 0)];

        Assert.Equal(1d, alphaHour0.Value);
        Assert.Equal(1d, bravoHour0.Value);

        // Equal values (1) in different queues render at the same shade under the shared domain.
        var alphaIndex = Intensity.RampIndex(alphaHour0.Value, matrix.Min, matrix.Max);
        var bravoIndex = Intensity.RampIndex(bravoHour0.Value, matrix.Min, matrix.Max);
        Assert.Equal(alphaIndex, bravoIndex);
    }
}
