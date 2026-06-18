using System;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for the historical failure-rate tint exposed by
/// <see cref="HeatmapHistoricalCell.FailureRate"/> (and its companion
/// <see cref="HeatmapHistoricalCell.HasData"/> no-data flag).
///
/// **Property 26: Historical failure-rate tint is the failure ratio**
/// **Validates: Requirements 7.6**
///
/// For any historical bucket with a fire count of at least one and a failure count between zero and
/// the fire count, the punchcard tint value equals <c>failureCount / fireCount</c> and lies in
/// <c>[0.0, 1.0]</c>. Buckets with a fire count of zero carry no data (<see cref="HeatmapHistoricalCell.HasData"/>
/// is <c>false</c>) and report a failure rate of <c>0.0</c> rather than a tinted value (Req 7.4, 7.6).
/// </summary>
public class FailureRateTintProperties
{
    /// <summary>Day index within the projection window (0..6).</summary>
    private static Gen<int> DayIndexGen => Gen.Choose(0, 6);

    /// <summary>Clock hour of the bucket (0..23).</summary>
    private static Gen<int> HourGen => Gen.Choose(0, 23);

    /// <summary>A non-negative p95 duration in milliseconds; irrelevant to the failure-rate tint but realistic.</summary>
    private static Gen<double> P95Gen => Gen.Choose(0, 600_000).Select(ms => (double)ms);

    /// <summary>
    /// A populated bucket: a fire count of one or greater paired with a failure count constrained to
    /// <c>[0, fireCount]</c>, honoring the Req 7.3 invariant that failures never exceed fires. Fire
    /// counts span a wide range so the ratio exercises many distinct values.
    /// </summary>
    private static Gen<(long FireCount, long FailureCount)> PopulatedCountsGen =>
        from fire in Gen.Choose(1, 1_000_000)
        from failure in Gen.Choose(0, fire)
        select ((long)fire, (long)failure);

    /// <summary>
    /// **Property 26: Historical failure-rate tint is the failure ratio**
    /// **Validates: Requirements 7.6**
    ///
    /// For a populated bucket (fire count ≥ 1, failure count in <c>[0, fireCount]</c>) the cell
    /// reports <see cref="HeatmapHistoricalCell.HasData"/> = <c>true</c>, its
    /// <see cref="HeatmapHistoricalCell.FailureRate"/> equals <c>failureCount / fireCount</c>, and
    /// that rate is clamped to <c>[0.0, 1.0]</c>.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property FailureRate_OfPopulatedBucket_EqualsRatio_InUnitInterval()
    {
        var arb = Arb.From(
            from day in DayIndexGen
            from hour in HourGen
            from counts in PopulatedCountsGen
            from p95 in P95Gen
            select (day, hour, counts.FireCount, counts.FailureCount, p95));

        return Prop.ForAll(arb, input =>
        {
            var (day, hour, fireCount, failureCount, p95) = input;

            var cell = new HeatmapHistoricalCell(day, hour, fireCount, failureCount, p95);

            if (!cell.HasData)
            {
                return false.Label($"populated bucket (fireCount={fireCount}) reported HasData=false");
            }

            var expected = (double)failureCount / fireCount;

            if (cell.FailureRate != expected)
            {
                return false.Label(
                    $"failure rate {cell.FailureRate} != failureCount/fireCount {expected} " +
                    $"(failureCount={failureCount}, fireCount={fireCount})");
            }

            if (cell.FailureRate < 0.0 || cell.FailureRate > 1.0)
            {
                return false.Label($"failure rate {cell.FailureRate} outside [0.0, 1.0]");
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// **Property 26 (no-data complement): empty buckets carry no failure-rate tint**
    /// **Validates: Requirements 7.6**
    ///
    /// A bucket with a fire count of zero is treated as having no historical data
    /// (<see cref="HeatmapHistoricalCell.HasData"/> = <c>false</c>) and reports a failure rate of
    /// exactly <c>0.0</c> rather than an undefined or tinted value (Req 7.4).
    /// </summary>
    [Property(MaxTest = 200)]
    public Property FailureRate_OfEmptyBucket_IsNoDataZero()
    {
        var arb = Arb.From(
            from day in DayIndexGen
            from hour in HourGen
            from p95 in P95Gen
            select (day, hour, p95));

        return Prop.ForAll(arb, input =>
        {
            var (day, hour, p95) = input;

            var cell = new HeatmapHistoricalCell(day, hour, FireCount: 0, FailureCount: 0, P95Ms: p95);

            if (cell.HasData)
            {
                return false.Label("empty bucket (fireCount=0) reported HasData=true");
            }

            if (cell.FailureRate != 0.0)
            {
                return false.Label($"empty bucket failure rate {cell.FailureRate} != 0.0");
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// **Property 26 (anchors): boundary failure ratios.**
    /// **Validates: Requirements 7.6**
    ///
    /// All-success (0 failures), all-failure (failures == fires), and a representative partial-failure
    /// bucket pin the endpoints and an interior point of the <c>[0.0, 1.0]</c> tint scale.
    /// </summary>
    [Fact]
    public void FailureRate_Boundaries_AreExact()
    {
        var allSuccess = new HeatmapHistoricalCell(0, 0, FireCount: 8, FailureCount: 0, P95Ms: 0);
        Assert.True(allSuccess.HasData);
        Assert.Equal(0.0, allSuccess.FailureRate);

        var allFailure = new HeatmapHistoricalCell(0, 0, FireCount: 8, FailureCount: 8, P95Ms: 0);
        Assert.True(allFailure.HasData);
        Assert.Equal(1.0, allFailure.FailureRate);

        var quarterFailure = new HeatmapHistoricalCell(0, 0, FireCount: 4, FailureCount: 1, P95Ms: 0);
        Assert.True(quarterFailure.HasData);
        Assert.Equal(0.25, quarterFailure.FailureRate);
    }
}
