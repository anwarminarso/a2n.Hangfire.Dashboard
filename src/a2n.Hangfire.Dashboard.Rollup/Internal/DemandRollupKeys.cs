using System.Globalization;

namespace a2n.Hangfire.Dashboard.Rollup.Internal;

/// <summary>Demand rollup key scheme (mirrors <see cref="Services.DemandRollupService"/>).</summary>
internal static class DemandRollupKeys
{
    public const string KeyPrefix = "heatmap:demand:";
    public const string QueuesSetKey = KeyPrefix + "queues";
    public const string WeeksSetKey = KeyPrefix + "weeks";
    public const string StateHashKey = KeyPrefix + "state";
    public const string SucceededWatermarkField = "succeededWatermarkTicks";
    public const string FailedWatermarkField = "failedWatermarkTicks";

    public static string BucketHashKey(long week, string queue)
        => $"{KeyPrefix}b:{week.ToString(CultureInfo.InvariantCulture)}:{queue}";

    public static string FieldName(int dayOfWeek, int hour)
        => $"{dayOfWeek.ToString(CultureInfo.InvariantCulture)}:{hour.ToString(CultureInfo.InvariantCulture)}";

    public static string PackDemandSample(long count, double sumDurationMs)
        => $"{count.ToString(CultureInfo.InvariantCulture)}|{sumDurationMs.ToString("R", CultureInfo.InvariantCulture)}";
}
