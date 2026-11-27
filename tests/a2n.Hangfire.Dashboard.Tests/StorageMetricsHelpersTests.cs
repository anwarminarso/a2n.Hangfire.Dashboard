using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Storage;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

public class StorageMetricsHelpersTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("stats:succeeded:2026-11-02-13", "succeeded", 2026, 11, 2, 13)]
    [InlineData("stats:failed:2026-11-02", "failed", 2026, 11, 2, 0)]
    public void TryParseCounterKey_ReadsHourlyAndDailyKeys(string key, string category, int y, int m, int d, int h)
    {
        Assert.True(StorageMetricsHelpers.TryParseCounterKey(key, out var parsedCategory, out var timestamp));
        Assert.Equal(category, parsedCategory);
        Assert.Equal(new DateTimeOffset(y, m, d, h, 0, 0, TimeSpan.Zero), timestamp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stats:succeeded")]
    [InlineData("stats:succeeded:yesterday")]
    [InlineData("stats:succeeded:2026-11-02-13-05")]
    public void TryParseCounterKey_RejectsOtherKeys(string? key)
    {
        Assert.False(StorageMetricsHelpers.TryParseCounterKey(key, out _, out _));
    }

    [Theory]
    [InlineData(MetricsInterval.OneMinute, 14, 37)]
    [InlineData(MetricsInterval.FiveMinutes, 14, 35)]
    [InlineData(MetricsInterval.FifteenMinutes, 14, 30)]
    [InlineData(MetricsInterval.OneHour, 14, 0)]
    [InlineData(MetricsInterval.OneDay, 0, 0)]
    public void TruncateToInterval_StartsTheBucket(MetricsInterval interval, int hour, int minute)
    {
        var timestamp = new DateTimeOffset(2026, 11, 2, 14, 37, 42, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, hour, minute, 0, TimeSpan.Zero),
            StorageMetricsHelpers.TruncateToInterval(timestamp, interval));
    }

    [Fact]
    public void AggregateThroughput_SumsPerBucketWithinTheRange()
    {
        var rows = new (string?, long)[]
        {
            ("stats:succeeded:2026-11-02-13", 5),
            ("stats:failed:2026-11-02-13", 2),
            ("stats:deleted:2026-11-02-14", 1),
            ("stats:succeeded:2026-11-02-14", 3),
            ("stats:succeeded:2026-10-31-23", 100), // before the range
            ("stats:succeeded:2026-11-03-00", 100), // the range end is exclusive
            ("stats:other:2026-11-02-13", 100),     // unknown category
            ("not-a-counter", 100),
            (null, 100),
        };

        var points = StorageMetricsHelpers.AggregateThroughput(rows, From, To, MetricsInterval.OneDay);

        var point = Assert.Single(points);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), point.Timestamp);
        Assert.Equal(8, point.Succeeded);
        Assert.Equal(2, point.Failed);
        Assert.Equal(1, point.Deleted);
    }

    [Fact]
    public void AggregateThroughput_OrdersBucketsByTime()
    {
        var rows = new (string?, long)[]
        {
            ("stats:succeeded:2026-11-02-14", 1),
            ("stats:succeeded:2026-11-02-13", 1),
        };

        var points = StorageMetricsHelpers.AggregateThroughput(rows, From, To, MetricsInterval.OneHour);

        Assert.Equal(new[] { 13, 14 }, points.Select(p => p.Timestamp.Hour));
    }

    [Fact]
    public void BuildStateTransitions_GroupsByBucketAndState()
    {
        var bucket = new DateTime(2026, 11, 2, 13, 0, 0, DateTimeKind.Unspecified);
        var rows = new (DateTime, string?, long)[]
        {
            (bucket, "Enqueued", 4),
            (bucket, "Succeeded", 3),
            (bucket, "Awaiting", 9), // not charted
            (bucket.AddHours(-1), "Failed", 2),
        };

        var points = StorageMetricsHelpers.BuildStateTransitions(rows);

        Assert.Equal(2, points.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero), points[0].Timestamp);
        Assert.Equal(2, points[0].Failed);
        Assert.Equal(4, points[1].Enqueued);
        Assert.Equal(3, points[1].Succeeded);
        Assert.Equal(0, points[1].Processing);
    }

    [Theory]
    [InlineData("{\"WorkerCount\":20,\"Queues\":[\"default\"]}", 20)]
    [InlineData("{\"Queues\":[\"default\"]}", 0)]
    [InlineData("not json", 0)]
    [InlineData("{\"WorkerCount\":\"many\"}", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ParseWorkerCount_ReadsTheWorkerCountOrZero(string? data, int expected)
    {
        Assert.Equal(expected, StorageMetricsHelpers.ParseWorkerCount(data));
    }
}
