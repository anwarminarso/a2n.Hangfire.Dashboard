using System.Globalization;
using a2n.Hangfire.Dashboard.Rollup.Internal;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Rollup;

/// <summary>
/// Persists and reads metrics rollup data via Hangfire storage-agnostic Hash/Set primitives.
/// </summary>
public sealed partial class MetricsRollupStore
{
    private const string KeyPrefix = "dashboard:rollup:";
    private const string StateHashKey = KeyPrefix + "state";
    private const string WeeksSetKey = KeyPrefix + "weeks";
    private const string ThroughputHashKey = KeyPrefix + "throughput";
    private const string StatesHashKey = KeyPrefix + "states";
    private const string DurationHashKey = KeyPrefix + "duration";
    private const string LatencyHashKey = KeyPrefix + "latency";
    private const string ScheduleHashKey = KeyPrefix + "schedule";
    private const string ExceptionsHashKey = KeyPrefix + "exceptions";
    private const string RetryHashKey = KeyPrefix + "retry";
    private const string VolumeHashKey = KeyPrefix + "volume";
    private const string HourlyHashKey = KeyPrefix + "hourly";
    private const string QueueTpHashKey = KeyPrefix + "qtp";
    private const string SlowestHashKey = KeyPrefix + "slowest";
    private const string RecurringHashKey = KeyPrefix + "recurring";

    /// <summary>How many recent executions are retained per recurring job.</summary>
    private const int MaxRecurringExecutions = 20;

    /// <summary>Suffix of the field that marks an entity prefix inside a duration-style hash.</summary>
    private const string CountSuffix = ":count";

    private const string SucceededWatermarkField = "succeededWatermarkTicks";
    private const string FailedWatermarkField = "failedWatermarkTicks";

    // Boundaries of the range a capped pass already covered, so the executions it could not reach are
    // drained by later polls instead of being skipped past (issue #29). Absent for state written by
    // earlier versions, which parses back as "no gap open".
    private const string SucceededCoveredFloorField = "succeededCoveredFloorTicks";
    private const string SucceededCoveredCeilingField = "succeededCoveredCeilingTicks";
    private const string FailedCoveredFloorField = "failedCoveredFloorTicks";
    private const string FailedCoveredCeilingField = "failedCoveredCeilingTicks";

    private readonly ILogger<MetricsRollupStore> _logger;

    public MetricsRollupStore(ILogger<MetricsRollupStore>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MetricsRollupStore>.Instance;
    }

    public (long Succeeded, long Failed, bool HasState) ReadWatermarks(IStorageConnection connection)
    {
        var (succeeded, failed, hasState) = ReadCheckpoints(connection);
        return (succeeded.WatermarkTicks, failed.WatermarkTicks, hasState);
    }

    /// <summary>
    /// Reads the full scan position of both state lists, including the range a capped pass covered.
    /// </summary>
    internal (ScanCheckpoint Succeeded, ScanCheckpoint Failed, bool HasState) ReadCheckpoints(
        IStorageConnection connection)
    {
        var state = SafeReadHash(connection, StateHashKey);
        if (state == null || state.Count == 0)
            return (default, default, false);

        return (
            new ScanCheckpoint(
                ParseTicks(state, SucceededWatermarkField),
                ParseTicks(state, SucceededCoveredFloorField),
                ParseTicks(state, SucceededCoveredCeilingField)),
            new ScanCheckpoint(
                ParseTicks(state, FailedWatermarkField),
                ParseTicks(state, FailedCoveredFloorField),
                ParseTicks(state, FailedCoveredCeilingField)),
            true);
    }

    public void Commit(
        IStorageConnection connection,
        long succeededWatermark,
        long failedWatermark,
        RollupAccumulator accumulator,
        long currentWeek)
        => Commit(
            connection,
            ScanCheckpoint.Collapsed(succeededWatermark),
            ScanCheckpoint.Collapsed(failedWatermark),
            accumulator,
            currentWeek);

    internal void Commit(
        IStorageConnection connection,
        ScanCheckpoint succeeded,
        ScanCheckpoint failed,
        RollupAccumulator accumulator,
        long currentWeek)
    {
        var demandHashUpdates = BuildDemandHashUpdates(connection, accumulator);
        var metricsUpdates = BuildMetricsHashUpdates(connection, accumulator);

        var minKeepWeek = currentWeek - RollupTime.RetentionWeeks + 1;
        var knownWeeks = SafeReadSet(connection, WeeksSetKey);
        var knownDemandWeeks = SafeReadSet(connection, DemandRollupKeys.WeeksSetKey);
        var knownDemandQueues = SafeReadSet(connection, DemandRollupKeys.QueuesSetKey);

        var weeksToTrim = knownWeeks
            .Select(w => long.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : long.MaxValue)
            .Where(w => w < minKeepWeek)
            .ToList();

        using var transaction = connection.CreateWriteTransaction();

        foreach (var update in demandHashUpdates)
            transaction.SetRangeInHash(update.Key, update.Value);

        foreach (var queue in accumulator.TouchedQueues)
            transaction.AddToSet(DemandRollupKeys.QueuesSetKey, queue);

        foreach (var week in accumulator.TouchedWeeks)
            transaction.AddToSet(DemandRollupKeys.WeeksSetKey, week.ToString(CultureInfo.InvariantCulture));

        foreach (var update in metricsUpdates)
            transaction.SetRangeInHash(update.Key, update.Value);

        foreach (var week in accumulator.TouchedWeeks)
            transaction.AddToSet(WeeksSetKey, week.ToString(CultureInfo.InvariantCulture));

        foreach (var week in weeksToTrim)
        {
            foreach (var queue in accumulator.TouchedQueues.Union(knownDemandQueues))
            {
                transaction.RemoveHash($"{ScheduleHashKey}:{week}:{queue}");
                transaction.RemoveHash(DemandRollupKeys.BucketHashKey(week, queue));
            }

            transaction.RemoveFromSet(WeeksSetKey, week.ToString(CultureInfo.InvariantCulture));
            transaction.RemoveFromSet(DemandRollupKeys.WeeksSetKey, week.ToString(CultureInfo.InvariantCulture));
        }

        transaction.SetRangeInHash(StateHashKey, new[]
        {
            new KeyValuePair<string, string>(SucceededWatermarkField, Ticks(succeeded.WatermarkTicks)),
            new KeyValuePair<string, string>(SucceededCoveredFloorField, Ticks(succeeded.CoveredFloorTicks)),
            new KeyValuePair<string, string>(SucceededCoveredCeilingField, Ticks(succeeded.CoveredCeilingTicks)),
            new KeyValuePair<string, string>(FailedWatermarkField, Ticks(failed.WatermarkTicks)),
            new KeyValuePair<string, string>(FailedCoveredFloorField, Ticks(failed.CoveredFloorTicks)),
            new KeyValuePair<string, string>(FailedCoveredCeilingField, Ticks(failed.CoveredCeilingTicks)),
        });

        // The demand rollup is maintained by this collector or by DemandRollupService, never both, and
        // that service only understands the plain watermark — mirror it so a switch between adapters
        // resumes from a sane position.
        transaction.SetRangeInHash(DemandRollupKeys.StateHashKey, new[]
        {
            new KeyValuePair<string, string>(DemandRollupKeys.SucceededWatermarkField, Ticks(succeeded.WatermarkTicks)),
            new KeyValuePair<string, string>(DemandRollupKeys.FailedWatermarkField, Ticks(failed.WatermarkTicks)),
        });

        transaction.Commit();
    }

    private static string Ticks(long value) => value.ToString(CultureInfo.InvariantCulture);

    public void SeedWatermarks(IStorageConnection connection, long nowTicks)
    {
        using var transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(StateHashKey, new[]
        {
            new KeyValuePair<string, string>(SucceededWatermarkField, nowTicks.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>(FailedWatermarkField, nowTicks.ToString(CultureInfo.InvariantCulture)),
        });
        transaction.SetRangeInHash(DemandRollupKeys.StateHashKey, new[]
        {
            new KeyValuePair<string, string>(DemandRollupKeys.SucceededWatermarkField, nowTicks.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>(DemandRollupKeys.FailedWatermarkField, nowTicks.ToString(CultureInfo.InvariantCulture)),
        });
        transaction.Commit();
    }
}
