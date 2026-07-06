using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Builds the heatmap's <c>Demand_Profile</c> — a historical, aggregated
/// <c>queue × day-of-week × hour</c> view of ad-hoc (on-demand) load — from the persisted
/// <c>Demand_Rollup</c> maintained by <see cref="DemandRollupService"/> (task 15.1).
/// </summary>
/// <remarks>
/// <para><b>What it does.</b> For the operator-selected <c>Lookback_Window</c> (1, 4, or 8 weeks,
/// Req 16.5) it reads the rollup's per-(queue, week) hashes, gathers each slot's per-week
/// occurrences within the lookback, and summarizes them with the selected
/// <see cref="AggregationStatistic"/> — the arithmetic mean for <c>Average</c> or the 95th percentile
/// for <c>p95</c> (Req 16.3, 16.4). The statistic is computed over the slot's value in <em>every</em>
/// available week of the lookback, so a week in which the slot saw no ad-hoc demand counts as a zero
/// occurrence rather than being skipped.</para>
///
/// <para><b>Reduced spans (Req 16.8, 17.4).</b> Because the rollup grows forward from first run and
/// applies bounded retention, fewer weeks than requested may be available. The provider reports the
/// actual available span (<see cref="DemandProfile.AvailableSpanWeeks"/>) and flags
/// <see cref="DemandProfile.IsSpanReduced"/>; it aggregates only over the weeks that exist and never
/// fabricates or extrapolates beyond the available data.</para>
///
/// <para><b>Percentile definition.</b> <c>p95</c> uses the continuous (linear-interpolation)
/// percentile, matching <c>PERCENTILE_CONT(0.95)</c> used by the SQL Server / PostgreSQL metrics
/// adapters so the demand statistic is consistent with the historical-source statistics.</para>
///
/// <para><b>Native UTC coordinates.</b> The rollup buckets executions by their UTC day-of-week and
/// UTC hour, so the profile is produced in that native coordinate space. Shifting the profile into
/// the viewer's time zone and onto a projection-window day index is the responsibility of the
/// consuming view/orchestration layer (tasks 15.4 / 16.x), not this provider.</para>
///
/// <para><b>Storage-agnostic, graceful degradation.</b> Following the existing
/// <see cref="AnalyticsService"/> / <see cref="DemandRollupService"/> conventions, this provider takes
/// an <see cref="IServiceProvider"/>, resolves <see cref="JobStorage"/> and
/// <see cref="IStorageMetricsProvider"/> optionally, exposes <see cref="IsAvailable"/> mirroring the
/// rollup's registration gate, and never throws for storage failures — it returns an empty profile
/// instead. The rollup is read through the storage-agnostic connection API, so it works on any
/// Hangfire storage without a schema change (Req 17.3).</para>
///
/// <para>The core aggregation is exposed as the pure, side-effect-free
/// <see cref="ComputeProfile(IEnumerable{DemandRollupSample}, IEnumerable{long}, long, int, AggregationStatistic, LoadMetric)"/>
/// helper so it can be exercised directly by property tests (Property 25).</para>
///
/// <para>Validates Requirements 16.3, 16.4, 16.5, 16.8, and 17.4.</para>
/// </remarks>
public class DemandProfileProvider
{
    // ─── Storage key scheme (mirrors DemandRollupService, Req 17.3) ─────────────
    // Kept in sync with DemandRollupService's private scheme; the rollup writer owns the format and
    // this reader mirrors it. Any change to the rollup key layout must be reflected here.
    private const string KeyPrefix = "heatmap:demand:";
    private const string QueuesSetKey = KeyPrefix + "queues";
    private const string WeeksSetKey = KeyPrefix + "weeks";

    private readonly JobStorage _storage;
    private readonly IStorageMetricsProvider _metricsProvider;
    private readonly ILogger<DemandProfileProvider> _logger;

    /// <summary>
    /// Indicates whether the demand profile can be built (both a metrics provider — the gate that
    /// lights up the ad-hoc/combined features — and job storage are registered). When false the
    /// provider always returns <see cref="DemandProfile.Empty"/> (graceful degradation, Req 16.7).
    /// </summary>
    public bool IsAvailable => _metricsProvider != null && _storage != null;

    public DemandProfileProvider(IServiceProvider serviceProvider)
    {
        if (serviceProvider is null)
        {
            throw new ArgumentNullException(nameof(serviceProvider));
        }

        // Resolve optionally — null when nothing is registered (graceful degradation).
        _storage = serviceProvider.GetService<JobStorage>();
        _metricsProvider = serviceProvider.GetService<IStorageMetricsProvider>();
        _logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<DemandProfileProvider>()
                  ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DemandProfileProvider>.Instance;
    }

    /// <summary>
    /// Reads the <c>Demand_Rollup</c> from storage and builds the <see cref="DemandProfile"/> for the
    /// requested lookback, statistic, and load metric. Never throws — a missing provider/storage or
    /// any storage failure yields an empty profile (Req 16.7), and reduced spans are reported rather
    /// than padded (Req 16.8, 17.4).
    /// </summary>
    /// <param name="lookbackWeeks">The selected lookback span in weeks (1/4/8; clamped to ≥ 1).</param>
    /// <param name="statistic">The aggregation statistic to apply per slot (Average or p95).</param>
    /// <param name="metric">The load metric the slot values are expressed in.</param>
    public DemandProfile GetProfile(
        int lookbackWeeks,
        AggregationStatistic statistic,
        LoadMetric metric)
    {
        var requested = ClampLookback(lookbackWeeks);

        if (!IsAvailable)
        {
            return DemandProfile.Empty(metric, statistic, requested);
        }

        IStorageConnection connection = null;
        try
        {
            connection = _storage.GetConnection();
            if (connection == null)
            {
                return DemandProfile.Empty(metric, statistic, requested);
            }

            var currentWeek = WeekIndex(DateTime.UtcNow.Ticks);
            var availableWeeks = ReadAvailableWeeks(connection);
            var queues = ReadQueues(connection);

            // The lookback selects the most recent `requested` weeks (inclusive of the current,
            // possibly partial, week). Only weeks the rollup actually retained within that range
            // contribute — we never fabricate data for weeks that predate first run or were trimmed.
            var minKeepWeek = currentWeek - requested + 1;
            var effectiveWeeks = availableWeeks
                .Where(w => w >= minKeepWeek && w <= currentWeek)
                .OrderBy(w => w)
                .ToList();

            var samples = ReadSamples(connection, effectiveWeeks, queues);

            return ComputeProfile(samples, effectiveWeeks, currentWeek, requested, statistic, metric);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build the demand profile; returning an empty profile.");
            return DemandProfile.Empty(metric, statistic, requested);
        }
        finally
        {
            connection?.Dispose();
        }
    }

    // ─── Pure aggregation (Property 25) ─────────────────────────────────────────

    /// <summary>
    /// Pure aggregation of rollup samples into a <see cref="DemandProfile"/>. For each slot it builds
    /// one value per <em>available</em> week within the lookback (weeks present in
    /// <paramref name="availableWeeks"/> that fall in the most recent <paramref name="lookbackWeeks"/>
    /// weeks ending at <paramref name="currentWeek"/>), treating a week with no sample for the slot as
    /// a zero occurrence, then summarizes the values with the selected statistic (Req 16.3, 16.4). The
    /// available span is the count of those weeks; the span is flagged reduced when fewer than
    /// requested (Req 16.8, 17.4). No data is fabricated beyond the available weeks.
    /// </summary>
    /// <param name="samples">The per-(week, queue, day-of-week, hour) rollup samples.</param>
    /// <param name="availableWeeks">Every week index present in the rollup (the profile uses only those within the lookback).</param>
    /// <param name="currentWeek">The current week index (the inclusive upper bound of the lookback range).</param>
    /// <param name="lookbackWeeks">The selected lookback span in weeks (clamped to ≥ 1).</param>
    /// <param name="statistic">The aggregation statistic to apply per slot.</param>
    /// <param name="metric">The load metric the slot values are expressed in.</param>
    public static DemandProfile ComputeProfile(
        IEnumerable<DemandRollupSample> samples,
        IEnumerable<long> availableWeeks,
        long currentWeek,
        int lookbackWeeks,
        AggregationStatistic statistic,
        LoadMetric metric)
    {
        var requested = lookbackWeeks < 1 ? 1 : lookbackWeeks;
        var minKeepWeek = currentWeek - requested + 1;

        // Distinct weeks within the lookback range that the rollup actually retained.
        var effectiveWeeks = (availableWeeks ?? Enumerable.Empty<long>())
            .Where(w => w >= minKeepWeek && w <= currentWeek)
            .Distinct()
            .OrderBy(w => w)
            .ToList();

        var availableSpan = effectiveWeeks.Count;
        var isSpanReduced = availableSpan < requested;

        if (availableSpan == 0)
        {
            // No retained data in the window — report the (reduced) zero span without aggregating.
            return new DemandProfile(
                new Dictionary<DemandSlotKey, double>(),
                Array.Empty<string>(),
                metric,
                statistic,
                requested,
                AvailableSpanWeeks: 0,
                IsSpanReduced: true,
                Min: 0,
                Max: 0);
        }

        var effectiveWeekSet = new HashSet<long>(effectiveWeeks);

        // Index the per-week value of every (slot, week) within the lookback.
        // perSlotWeekValue[slot][week] = the slot's metric value for that week.
        var perSlot = new Dictionary<DemandSlotKey, Dictionary<long, double>>();

        foreach (var sample in samples ?? Enumerable.Empty<DemandRollupSample>())
        {
            if (!effectiveWeekSet.Contains(sample.Week))
            {
                continue;
            }

            var queue = string.IsNullOrEmpty(sample.Queue) ? ScheduleAggregator.DefaultQueue : sample.Queue;
            var key = new DemandSlotKey(queue, sample.DayOfWeek, sample.Hour);
            var value = MetricValue(sample, metric);

            if (!perSlot.TryGetValue(key, out var byWeek))
            {
                byWeek = new Dictionary<long, double>();
                perSlot[key] = byWeek;
            }

            // Multiple raw samples for the same (slot, week) are summed into that week's value.
            byWeek[sample.Week] = byWeek.TryGetValue(sample.Week, out var existing)
                ? existing + value
                : value;
        }

        var slots = new Dictionary<DemandSlotKey, double>(perSlot.Count);
        var queues = new SortedSet<string>(StringComparer.Ordinal);
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;

        foreach (var entry in perSlot)
        {
            // Build one value per effective week — zero for weeks the slot was absent (Property 25).
            var values = new double[availableSpan];
            for (var i = 0; i < availableSpan; i++)
            {
                values[i] = entry.Value.TryGetValue(effectiveWeeks[i], out var v) ? v : 0d;
            }

            var stat = statistic == AggregationStatistic.P95
                ? ContinuousPercentile(values, 0.95)
                : Average(values);

            slots[entry.Key] = stat;
            queues.Add(entry.Key.Queue);

            if (stat < min) min = stat;
            if (stat > max) max = stat;
        }

        if (slots.Count == 0)
        {
            min = 0;
            max = 0;
        }

        return new DemandProfile(
            slots,
            queues.ToList(),
            metric,
            statistic,
            requested,
            availableSpan,
            isSpanReduced,
            min,
            max);
    }

    // ─── Viewer-time-zone alignment (Req 8.2 parity for demand) ─────────────────

    /// <summary>
    /// Rotates a <see cref="DemandProfile"/> from its native UTC <c>day-of-week × hour</c> coordinates
    /// into the viewer's local coordinates so the demand shading aligns with the projected cron matrix
    /// (which is already bucketed in viewer-local time by <see cref="ScheduleAggregator"/>). The rollup
    /// stores executions by their UTC day-of-week and UTC hour; without this shift a viewer in a
    /// non-UTC zone would see the demand bands at the wrong clock hour (and, near midnight, the wrong
    /// day).
    /// </summary>
    /// <remarks>
    /// <para>The profile is a <c>day-of-week × hour</c> aggregate with no concrete date, so there is no
    /// single instant to convert; the rotation therefore applies a whole-hour offset
    /// (<paramref name="viewerOffset"/> rounded to the nearest hour) and wraps the day-of-week when the
    /// hour crosses a day boundary. Half-hour and 45-minute zones (e.g. India +05:30, Nepal +05:45) are
    /// rounded to the nearest hour — acceptable for an hourly heatmap and deterministic. Because the
    /// 168 <c>(dow, hour)</c> slots rotate uniformly the mapping is a bijection per queue, so the slot
    /// count, queue set, and Min/Max are preserved; a UTC viewer (zero offset) is returned unchanged.</para>
    /// </remarks>
    /// <param name="profile">The demand profile in native UTC coordinates.</param>
    /// <param name="viewerOffset">The viewer time zone's UTC offset (rounded to whole hours).</param>
    /// <returns>The profile rekeyed into viewer-local <c>day-of-week × hour</c> coordinates.</returns>
    public static DemandProfile ShiftToViewerLocal(DemandProfile profile, TimeSpan viewerOffset)
    {
        if (profile?.Slots is null || profile.Slots.Count == 0)
        {
            return profile;
        }

        var offsetHours = (int)Math.Round(viewerOffset.TotalHours, MidpointRounding.AwayFromZero);
        if (offsetHours == 0)
        {
            return profile;
        }

        var shifted = new Dictionary<DemandSlotKey, double>(profile.Slots.Count);
        foreach (var kv in profile.Slots)
        {
            var total = kv.Key.Hour + offsetHours;
            var localHour = ((total % 24) + 24) % 24;
            var dayShift = (int)Math.Floor(total / 24d);
            var localDow = ((kv.Key.DayOfWeek + dayShift) % 7 + 7) % 7;

            var newKey = new DemandSlotKey(kv.Key.Queue, localDow, localHour);
            shifted[newKey] = shifted.TryGetValue(newKey, out var existing) ? existing + kv.Value : kv.Value;
        }

        // The rotation only rekeys slots, so the value distribution (and thus Min/Max) and the queue
        // set are unchanged.
        return profile with { Slots = shifted };
    }

    // ─── Storage reads (storage-agnostic, all guarded) ──────────────────────────

    private List<long> ReadAvailableWeeks(IStorageConnection connection)
    {
        var raw = SafeReadSet(connection, WeeksSetKey);
        var weeks = new List<long>(raw.Count);
        foreach (var item in raw)
        {
            if (long.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w))
            {
                weeks.Add(w);
            }
        }

        return weeks;
    }

    private List<string> ReadQueues(IStorageConnection connection)
        => SafeReadSet(connection, QueuesSetKey).ToList();

    private List<DemandRollupSample> ReadSamples(
        IStorageConnection connection, IReadOnlyList<long> weeks, IReadOnlyList<string> queues)
    {
        var samples = new List<DemandRollupSample>();

        foreach (var week in weeks)
        {
            foreach (var queue in queues)
            {
                var hash = SafeReadHash(connection, BucketHashKey(week, queue));
                if (hash == null || hash.Count == 0)
                {
                    continue;
                }

                foreach (var field in hash)
                {
                    if (!TryParseField(field.Key, out var dayOfWeek, out var hour))
                    {
                        continue;
                    }

                    var (count, sumMs) = ParseSample(field.Value);
                    if (count <= 0 && sumMs <= 0)
                    {
                        continue;
                    }

                    samples.Add(new DemandRollupSample(week, queue, dayOfWeek, hour, count, sumMs));
                }
            }
        }

        return samples;
    }

    private Dictionary<string, string> SafeReadHash(IStorageConnection connection, string key)
    {
        try
        {
            return connection.GetAllEntriesFromHash(key);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read demand rollup hash {Key}.", key);
            return null;
        }
    }

    private HashSet<string> SafeReadSet(IStorageConnection connection, string key)
    {
        try
        {
            return connection.GetAllItemsFromSet(key) ?? new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read demand rollup set {Key}.", key);
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    // ─── Statistics helpers ─────────────────────────────────────────────────────

    private static double MetricValue(DemandRollupSample sample, LoadMetric metric)
        => metric == LoadMetric.WorkerMinutes
            ? sample.SumDurationMs / 60000d   // ms → minutes
            : sample.Count;

    private static double Average(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0d;
        }

        var sum = 0d;
        for (var i = 0; i < values.Count; i++)
        {
            sum += values[i];
        }

        return sum / values.Count;
    }

    /// <summary>
    /// The continuous (linear-interpolation) percentile, matching SQL <c>PERCENTILE_CONT(p)</c> used
    /// by the metrics adapters: with <c>n</c> sorted values the rank is <c>p·(n−1)</c> and the result
    /// is linearly interpolated between the two nearest ranks. Returns 0 for an empty input.
    /// </summary>
    private static double ContinuousPercentile(double[] values, double p)
    {
        if (values == null || values.Length == 0)
        {
            return 0d;
        }

        if (values.Length == 1)
        {
            return values[0];
        }

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);

        if (lo == hi)
        {
            return sorted[lo];
        }

        var frac = rank - lo;
        return sorted[lo] + (frac * (sorted[hi] - sorted[lo]));
    }

    // ─── Key & sample parsing (mirrors DemandRollupService) ─────────────────────

    /// <summary>Builds the per-(queue, week) hash key (Req 17.3); mirrors <see cref="DemandRollupService"/>.</summary>
    private static string BucketHashKey(long week, string queue)
        => $"{KeyPrefix}b:{week.ToString(CultureInfo.InvariantCulture)}:{queue}";

    /// <summary>Parses a <c>{dayOfWeek}:{hour}</c> hash field name.</summary>
    private static bool TryParseField(string field, out int dayOfWeek, out int hour)
    {
        dayOfWeek = 0;
        hour = 0;
        if (string.IsNullOrEmpty(field))
        {
            return false;
        }

        var sep = field.IndexOf(':');
        if (sep <= 0 || sep >= field.Length - 1)
        {
            return false;
        }

        return int.TryParse(field.Substring(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out dayOfWeek)
               && int.TryParse(field.Substring(sep + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out hour);
    }

    /// <summary>Parses a packed <c>count|sumDurationMs</c> sample; tolerant of malformed values (mirrors the writer).</summary>
    private static (long Count, double SumDurationMs) ParseSample(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return (0, 0d);
        }

        var sep = raw.IndexOf('|');
        if (sep < 0)
        {
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var only)
                ? (only, 0d)
                : (0, 0d);
        }

        long.TryParse(raw.Substring(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count);
        double.TryParse(raw.Substring(sep + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var sumMs);
        return (count, sumMs);
    }

    /// <summary>
    /// Computes the week index of a UTC tick count as whole weeks since the Unix epoch — identical to
    /// <see cref="DemandRollupService"/> so reads align exactly with writes.
    /// </summary>
    private static long WeekIndex(long utcTicks)
    {
        var daysSinceEpoch = (utcTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerDay;
        return (long)Math.Floor(daysSinceEpoch / 7d);
    }

    private static int ClampLookback(int lookbackWeeks)
        // The UI offers 1/4/8 (Req 16.5); accept any positive value, flooring to one week.
        => lookbackWeeks < 1 ? 1 : lookbackWeeks;

    /// <summary>
    /// A single rollup sample read back from the <c>Demand_Rollup</c>: the ad-hoc execution
    /// <see cref="Count"/> and summed duration (<see cref="SumDurationMs"/>) for one
    /// <c>(week, queue, day-of-week, hour)</c> coordinate. Public so property tests can drive
    /// <see cref="ComputeProfile"/> directly.
    /// </summary>
    /// <param name="Week">The whole-weeks-since-epoch week index.</param>
    /// <param name="Queue">The queue the sample belongs to.</param>
    /// <param name="DayOfWeek">The UTC day-of-week (0 = Sunday … 6 = Saturday).</param>
    /// <param name="Hour">The UTC clock hour (0..23).</param>
    /// <param name="Count">The number of ad-hoc executions in the bucket.</param>
    /// <param name="SumDurationMs">The summed execution duration of the bucket, in milliseconds.</param>
    public readonly record struct DemandRollupSample(
        long Week,
        string Queue,
        int DayOfWeek,
        int Hour,
        long Count,
        double SumDurationMs);
}
