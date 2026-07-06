using a2n.Hangfire.Dashboard.Rollup.Internal;
using Xunit;

namespace a2n.Hangfire.Dashboard.Rollup.Tests;

public class RollupMathTests
{
    [Fact]
    public void ContinuousPercentile_matches_linear_interpolation()
    {
        var values = new[] { 10d, 20d, 30d, 40d, 50d };
        var p95 = RollupMath.ContinuousPercentile(values, 0.95);
        Assert.InRange(p95, 47d, 49d);
    }

    [Fact]
    public void MergeReservoir_caps_sample_count()
    {
        var list = new List<double>();
        for (var i = 1; i <= 300; i++)
            list = RollupMath.MergeReservoir(list, i, cap: 50);

        Assert.Equal(50, list.Count);
    }

    [Fact]
    public void DayIndexMondayZero_starts_on_monday()
    {
        var monday = new DateTime(2024, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, RollupTime.DayIndexMondayZero(monday));
        Assert.Equal(6, RollupTime.DayIndexMondayZero(monday.AddDays(6)));
    }
}
