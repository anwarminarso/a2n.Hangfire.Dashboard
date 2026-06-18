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
/// Property test for <see cref="TopN.SelectTopQueues"/> Top-N queue selection.
///
/// **Property 17: Top-N selects the highest-load queues with an ascending-name tie-break**
/// **Validates: Requirements 13.3**
///
/// Top-N selection returns the <c>min(N, queueCount)</c> highest-total queues, ordered by
/// descending total load and, where totals tie (including at the <c>N</c>th position), by ascending
/// queue name (Ordinal).
/// </summary>
public class TopNSelectionProperties
{
    /// <summary>
    /// A fixed alphabet of queue names whose lexical (Ordinal) order does not correlate with the
    /// generated load totals, so the ascending-name tie-break is genuinely exercised. Includes an
    /// uppercase name ("Zulu") so the Ordinal ordering (uppercase sorts before lowercase) is tested.
    /// </summary>
    private static readonly string[] QueuePool =
    {
        "aardvark", "alpha", "bravo", "charlie", "default",
        "delta", "echo", "foxtrot", "golf", "Zulu",
    };

    /// <summary>The number of one-minute offsets inside a seven-day window.</summary>
    private const int WindowMinutes = 7 * 24 * 60;

    /// <summary>Floating-point tolerance for load comparisons.</summary>
    private const double Tolerance = 1e-9;

    /// <summary>A deterministic UTC window so each fire lands in-window at its chosen minute offset.</summary>
    private static readonly ProjectionWindow Window = HeatmapTime.BuildWindow(
        ProjectionWindowKind.IdealizedWeek,
        new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        TimeZoneInfo.Utc);

    /// <summary>
    /// Generates a per-queue fire count for every name in <see cref="QueuePool"/>. Counts are drawn
    /// from a deliberately small range so many queues share the same total — forcing frequent ties,
    /// including ties straddling the <c>N</c>th selection position. A count of zero means the queue
    /// is absent (contributes no fires and therefore no populated cells).
    /// </summary>
    private static Gen<int[]> CountsGen =>
        Gen.ArrayOf(QueuePool.Length, Gen.Choose(0, 4));

    /// <summary>
    /// **Property 17: Top-N selects the highest-load queues with an ascending-name tie-break**
    /// **Validates: Requirements 13.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property TopN_SelectsHighestLoadQueues_WithAscendingNameTieBreak()
    {
        var arb = Arb.From(
            from counts in CountsGen
            from n in Gen.Choose(1, QueuePool.Length + 5)
            select (counts, n));

        return Prop.ForAll(arb, input =>
        {
            var (counts, n) = input;

            // Build fires across several queues. Under the Fire-count metric each fire contributes
            // exactly 1, so the total load of a queue equals its generated fire count — giving exact,
            // controllable ties. Each fire occupies a distinct minute offset inside the window.
            var fires = new List<ProjectedFire>();
            var minuteOffset = 0;
            var expectedTotals = new Dictionary<string, double>(StringComparer.Ordinal);

            for (var q = 0; q < QueuePool.Length; q++)
            {
                var count = counts[q];
                if (count <= 0)
                {
                    continue;
                }

                var queue = QueuePool[q];
                expectedTotals[queue] = count;

                for (var f = 0; f < count; f++)
                {
                    fires.Add(new ProjectedFire(
                        JobId: $"{queue}-{f}",
                        Queue: queue,
                        FireTimeUtc: Window.StartInclusive.AddMinutes(minuteOffset % WindowMinutes),
                        EstimatedDuration: TimeSpan.FromMinutes(1)));
                    minuteOffset++;
                }
            }

            var matrix = ScheduleAggregator.Aggregate(fires, LoadMetric.FireCount, TimeZoneInfo.Utc, Window);

            var queueCount = expectedTotals.Count;

            // Independent oracle: order queues by descending total load, then ascending Ordinal name,
            // then take min(n, queueCount).
            var expected = expectedTotals
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(Math.Min(n, queueCount))
                .Select(pair => pair.Key)
                .ToList();

            // Exercise the generated n plus a battery of fixed n values on the same matrix so that
            // small n (forcing a tie-break at the Nth position) and n > queueCount are always covered.
            var nValues = new List<int> { n, 1, 2, 3, queueCount, queueCount + 1, QueuePool.Length + 10 }
                .Where(v => v >= 1)
                .Distinct();

            foreach (var nv in nValues)
            {
                var actual = TopN.SelectTopQueues(matrix, nv);

                var oracle = expectedTotals
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(Math.Min(nv, queueCount))
                    .Select(pair => pair.Key)
                    .ToList();

                // Size: exactly min(nv, queueCount).
                if (actual.Count != oracle.Count)
                {
                    return false.Label(
                        $"size mismatch for n={nv}: actual={actual.Count} expected={oracle.Count} " +
                        $"(queueCount={queueCount})");
                }

                // Order and membership: selection equals the oracle exactly (descending load,
                // ascending-name tie-break — including at the Nth position).
                if (!actual.SequenceEqual(oracle, StringComparer.Ordinal))
                {
                    return false.Label(
                        $"selection mismatch for n={nv}: actual=[{string.Join(",", actual)}] " +
                        $"expected=[{string.Join(",", oracle)}]");
                }

                // Cross-check: every selected queue's total is >= every unselected queue's total
                // (the selection really is the highest-load set), with ties broken by name.
                var selected = new HashSet<string>(actual, StringComparer.Ordinal);
                foreach (var sel in actual)
                {
                    var selTotal = expectedTotals[sel];
                    foreach (var other in expectedTotals)
                    {
                        if (selected.Contains(other.Key))
                        {
                            continue;
                        }

                        // An unselected queue must not outrank a selected one: either it has strictly
                        // less load, or equal load but a lexically larger name.
                        var unselectedOutranks =
                            other.Value > selTotal + Tolerance ||
                            (Math.Abs(other.Value - selTotal) <= Tolerance &&
                             string.CompareOrdinal(other.Key, sel) < 0);

                        if (unselectedOutranks)
                        {
                            return false.Label(
                                $"unselected queue '{other.Key}' (total={other.Value}) outranks " +
                                $"selected '{sel}' (total={selTotal}) for n={nv}");
                        }
                    }
                }
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// Example: a small n forces an ascending-name tie-break at the Nth position. Three queues tie
    /// on total load; requesting the top 2 must return the two alphabetically smallest names.
    /// </summary>
    [Fact]
    public void SmallN_TieAtNthPosition_BreaksByAscendingName()
    {
        // queues "charlie", "bravo", "alpha" each fire twice (total load 2); "delta" fires three
        // times (total load 3). Top-3 = delta, then the two smallest names among the tie.
        var fires = new List<ProjectedFire>();
        var minute = 0;

        void Add(string queue, int times)
        {
            for (var i = 0; i < times; i++)
            {
                fires.Add(new ProjectedFire(
                    JobId: $"{queue}-{i}",
                    Queue: queue,
                    FireTimeUtc: Window.StartInclusive.AddMinutes(minute++),
                    EstimatedDuration: TimeSpan.FromMinutes(1)));
            }
        }

        Add("charlie", 2);
        Add("bravo", 2);
        Add("alpha", 2);
        Add("delta", 3);

        var matrix = ScheduleAggregator.Aggregate(fires, LoadMetric.FireCount, TimeZoneInfo.Utc, Window);

        // delta has the highest load; the remaining slot of a top-2 selection goes to the
        // alphabetically smallest tied queue ("alpha").
        Assert.Equal(new[] { "delta", "alpha" }, TopN.SelectTopQueues(matrix, 2));

        // Top-3 takes delta then the two smallest tied names.
        Assert.Equal(new[] { "delta", "alpha", "bravo" }, TopN.SelectTopQueues(matrix, 3));
    }

    /// <summary>
    /// Example: n greater than the queue count returns every queue (in selection order).
    /// </summary>
    [Fact]
    public void NGreaterThanQueueCount_ReturnsAllQueues()
    {
        var fires = new List<ProjectedFire>
        {
            new("a-0", "alpha", Window.StartInclusive, TimeSpan.FromMinutes(1)),
            new("b-0", "bravo", Window.StartInclusive.AddMinutes(1), TimeSpan.FromMinutes(1)),
            new("b-1", "bravo", Window.StartInclusive.AddMinutes(2), TimeSpan.FromMinutes(1)),
        };

        var matrix = ScheduleAggregator.Aggregate(fires, LoadMetric.FireCount, TimeZoneInfo.Utc, Window);

        // bravo (load 2) outranks alpha (load 1); requesting more than the 2 queues returns both.
        Assert.Equal(new[] { "bravo", "alpha" }, TopN.SelectTopQueues(matrix, 50));
    }
}
