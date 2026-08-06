using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Rollup.Internal;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace a2n.Hangfire.Dashboard.Rollup.Tests;

/// <summary>
/// Unit coverage for the resumable-scan state machine behind issue #29. A poll is bounded, so a pass
/// that stops early must remember the range it covered instead of advancing the watermark past the
/// executions it never reached.
/// </summary>
public class ScanWindowTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    [Fact]
    public void Drained_pass_collapses_to_a_single_watermark()
    {
        var window = new ScanWindow(ScanCheckpoint.Collapsed(1_000), recordCap: 10);

        Assert.Equal(ScanAction.Record, window.Classify(3_000));
        window.OnRecorded(3_000);
        Assert.Equal(ScanAction.Record, window.Classify(2_000));
        window.OnRecorded(2_000);
        // An entry at or below the watermark proves everything above it has now been covered.
        Assert.Equal(ScanAction.StopDrained, window.Classify(1_000));

        var result = window.Complete(drained: true);

        Assert.Equal(3_000, result.Checkpoint.WatermarkTicks);
        Assert.False(result.Checkpoint.HasGap);
        Assert.False(result.DataDropped);
        Assert.Equal(2, result.Recorded);
    }

    [Fact]
    public void Capped_pass_keeps_the_watermark_and_records_the_covered_range()
    {
        var window = new ScanWindow(ScanCheckpoint.Collapsed(1_000), recordCap: 2);

        window.OnRecorded(9_000);
        window.OnRecorded(8_000);
        // Budget spent while entries newer than the watermark remain.
        Assert.Equal(ScanAction.StopExhausted, window.Classify(7_000));

        var result = window.Complete(drained: false);

        // The watermark must not move: everything in (1000, 8000) is still to be aggregated.
        Assert.Equal(1_000, result.Checkpoint.WatermarkTicks);
        Assert.Equal(8_000, result.Checkpoint.CoveredFloorTicks);
        Assert.Equal(9_000, result.Checkpoint.CoveredCeilingTicks);
        Assert.True(result.Checkpoint.HasGap);
        Assert.False(result.DataDropped);
    }

    [Fact]
    public void Budget_is_soft_at_the_tail_so_a_tick_is_never_split()
    {
        var window = new ScanWindow(ScanCheckpoint.Collapsed(1_000), recordCap: 2);

        window.OnRecorded(9_000);
        window.OnRecorded(8_000);

        // Storages that truncate timestamps can hand out several executions on the same tick. Cutting
        // between them would either lose them (the floor claims them as covered) or replay them on the
        // next poll, so the pass finishes the tick it stopped on.
        Assert.Equal(ScanAction.Record, window.Classify(8_000));
        window.OnRecorded(8_000);
        Assert.Equal(ScanAction.Record, window.Classify(8_000));
        window.OnRecorded(8_000);
        Assert.Equal(ScanAction.StopExhausted, window.Classify(7_999));

        var result = window.Complete(drained: false);

        Assert.Equal(4, result.Recorded);
        Assert.Equal(8_000, result.Checkpoint.CoveredFloorTicks);
    }

    [Fact]
    public void Resuming_pass_steps_over_the_covered_range_without_spending_budget()
    {
        // A previous pass covered [8000, 9000] and left (1000, 8000) pending.
        var window = new ScanWindow(new ScanCheckpoint(1_000, 8_000, 9_000), recordCap: 2);

        // Arrivals since that pass are above the ceiling and get aggregated.
        Assert.Equal(ScanAction.Record, window.Classify(9_500));
        window.OnRecorded(9_500);

        // The covered range is skipped, and skipping is free.
        Assert.Equal(ScanAction.Skip, window.Classify(9_000));
        Assert.Equal(ScanAction.Skip, window.Classify(8_500));
        Assert.Equal(ScanAction.Skip, window.Classify(8_000));

        // Draining continues below the old floor.
        Assert.Equal(ScanAction.Record, window.Classify(7_000));
        window.OnRecorded(7_000);
        Assert.Equal(ScanAction.StopExhausted, window.Classify(6_000));

        var result = window.Complete(drained: false);

        Assert.Equal(2, result.Recorded);
        Assert.Equal(1_000, result.Checkpoint.WatermarkTicks);
        // Contiguous through the skipped range: [7000, 9500] is covered.
        Assert.Equal(7_000, result.Checkpoint.CoveredFloorTicks);
        Assert.Equal(9_500, result.Checkpoint.CoveredCeilingTicks);
        Assert.False(result.DataDropped);
    }

    [Fact]
    public void Resuming_pass_that_reaches_the_watermark_closes_the_gap()
    {
        var window = new ScanWindow(new ScanCheckpoint(1_000, 8_000, 9_000), recordCap: 100);

        Assert.Equal(ScanAction.Skip, window.Classify(8_500));
        Assert.Equal(ScanAction.Record, window.Classify(5_000));
        window.OnRecorded(5_000);
        Assert.Equal(ScanAction.StopDrained, window.Classify(1_000));

        var result = window.Complete(drained: true);

        // The ceiling of the previously covered range becomes the new watermark even though this pass
        // never saw an entry that high.
        Assert.Equal(9_000, result.Checkpoint.WatermarkTicks);
        Assert.False(result.Checkpoint.HasGap);
        Assert.False(result.DataDropped);
    }

    [Fact]
    public void Two_disjoint_ranges_are_merged_and_reported_as_dropped()
    {
        var window = new ScanWindow(new ScanCheckpoint(1_000, 8_000, 9_000), recordCap: 2);

        // Enough arrived since the last pass to spend the whole budget before reaching the covered
        // range, so (9000, 20000) was never scanned and cannot be reached again.
        window.OnRecorded(30_000);
        window.OnRecorded(20_000);
        Assert.Equal(ScanAction.StopExhausted, window.Classify(19_000));

        var result = window.Complete(drained: false);

        Assert.True(result.DataDropped);
        // The pending gap is unchanged, and the two covered ranges collapse into one so the additive
        // counters are never replayed.
        Assert.Equal(1_000, result.Checkpoint.WatermarkTicks);
        Assert.Equal(8_000, result.Checkpoint.CoveredFloorTicks);
        Assert.Equal(30_000, result.Checkpoint.CoveredCeilingTicks);
    }

    [Fact]
    public void A_pass_that_records_nothing_leaves_the_checkpoint_untouched()
    {
        var start = new ScanCheckpoint(1_000, 8_000, 9_000);
        var window = new ScanWindow(start, recordCap: 10);

        // Mirrors a read failure before the first usable entry: no progress, no loss.
        var result = window.Complete(drained: false);

        Assert.Equal(start, result.Checkpoint);
        Assert.Equal(0, result.Recorded);
        Assert.False(result.DataDropped);
    }

    [Fact]
    public void A_floor_at_or_below_the_watermark_is_not_a_gap()
    {
        // How state written before the covered-range fields existed reads back.
        var checkpoint = new ScanCheckpoint(5_000, 0, 0);
        Assert.False(checkpoint.HasGap);
        Assert.Equal(TimeSpan.Zero, checkpoint.PendingSpan);

        // A stale floor that the watermark has since passed carries no information either.
        var stale = new ScanCheckpoint(5_000, 4_000, 9_000);
        Assert.False(stale.HasGap);
        Assert.Equal(0, stale.CoveredFloorTicks);
        Assert.Equal(0, stale.CoveredCeilingTicks);

        Assert.Equal(TimeSpan.FromSeconds(1), new ScanCheckpoint(0, Second, Second * 2).PendingSpan);
    }
}

/// <summary>
/// End-to-end coverage for issue #29: a burst larger than one poll's cap must be aggregated in full
/// across the following polls, with every execution counted exactly once.
/// </summary>
public class ExecutionRollupBurstTests
{
    /// <summary>Matches <c>ExecutionRollupCollector.MaxJobsPerPoll</c>.</summary>
    private const int CapPerPoll = 2000;

    [Fact]
    public void A_burst_larger_than_the_per_poll_cap_is_aggregated_across_polls()
    {
        const int burst = 5000;
        var harness = new CollectorHarness();
        harness.AddSucceededBurst(count: burst);

        harness.Poll();
        Assert.Equal(CapPerPoll, harness.TotalSucceeded());
        Assert.True(harness.Checkpoint().HasGap);

        harness.Poll();
        Assert.Equal(CapPerPoll * 2, harness.TotalSucceeded());
        Assert.True(harness.Checkpoint().HasGap);

        harness.Poll();
        // The whole burst, counted once each: the old single-watermark collector stopped at 2000.
        Assert.Equal(burst, harness.TotalSucceeded());

        var checkpoint = harness.Checkpoint();
        Assert.False(checkpoint.HasGap);
        Assert.Equal(harness.NewestTicks, checkpoint.WatermarkTicks);

        // A further poll with nothing new must not re-count anything.
        harness.Poll();
        Assert.Equal(burst, harness.TotalSucceeded());
    }

    [Fact]
    public void Arrivals_during_a_drain_are_aggregated_without_replaying_the_covered_range()
    {
        var harness = new CollectorHarness();
        harness.AddSucceededBurst(count: 3000);

        harness.Poll();
        Assert.Equal(CapPerPoll, harness.TotalSucceeded());

        // New executions land above the range the capped pass covered while a backlog is still pending.
        harness.AddSucceededBurst(count: 100);
        harness.Poll();

        // 100 new plus the 1000 that were left pending, and nothing counted twice.
        Assert.Equal(3100, harness.TotalSucceeded());
        Assert.False(harness.Checkpoint().HasGap);
    }

    [Fact]
    public void Skipping_a_covered_range_costs_page_reads_but_no_job_lookups()
    {
        var harness = new CollectorHarness();
        harness.AddSucceededBurst(count: 2500);

        harness.Poll();
        var readsAfterFirstPoll = harness.Api.SucceededPageReads;

        harness.Poll();

        // The resuming pass pages back over the covered range, which costs one read per page and no
        // per-job parameter lookups, so the record budget is spent on the pending gap instead.
        Assert.True(harness.Api.SucceededPageReads > readsAfterFirstPoll);
        Assert.Equal(2500, harness.TotalSucceeded());
    }

    /// <summary>
    /// Drives <see cref="ExecutionRollupCollector"/> against a stub monitoring API while the rollup
    /// itself is persisted in real Hangfire storage primitives.
    /// </summary>
    private sealed class CollectorHarness
    {
        private readonly InMemoryStorage _storage = new();
        private readonly MetricsRollupStore _store = new();
        private readonly ExecutionRollupCollector _collector;
        private readonly DateTime _baseTime = DateTime.UtcNow.AddHours(-6);
        private int _issued;

        public CollectorHarness()
        {
            Api = new FakeMonitoringApi();

            var services = new ServiceCollection();
            services.AddSingleton<JobStorage>(new FakeStorage(_storage, Api));
            _collector = new ExecutionRollupCollector(services.BuildServiceProvider());

            // Start from a known watermark instead of letting the first poll seed it from the clock.
            using var connection = _storage.GetConnection();
            _store.SeedWatermarks(connection, _baseTime.Ticks);
        }

        public FakeMonitoringApi Api { get; }

        /// <summary>Timestamp of the newest execution handed to the collector.</summary>
        public long NewestTicks { get; private set; }

        public void AddSucceededBurst(int count)
        {
            for (var i = 0; i < count; i++)
            {
                _issued++;
                var succeededAt = _baseTime.AddSeconds(_issued);
                NewestTicks = succeededAt.Ticks;

                // The monitoring API lists newest first.
                Api.Succeeded.Insert(0, new KeyValuePair<string, SucceededJobDto>(
                    $"job-{_issued}",
                    new SucceededJobDto { SucceededAt = succeededAt, TotalDuration = 100 }));
            }
        }

        public void Poll() => _collector.PollOnce(CancellationToken.None);

        public ScanCheckpoint Checkpoint()
        {
            using var connection = _storage.GetConnection();
            return _store.ReadCheckpoints(connection).Succeeded;
        }

        public long TotalSucceeded()
        {
            using var connection = _storage.GetConnection();
            return _store
                .ReadThroughput(connection, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddHours(1),
                    MetricsInterval.OneHour)
                .Sum(p => p.Succeeded);
        }
    }

    private sealed class FakeStorage : JobStorage
    {
        private readonly JobStorage _inner;
        private readonly IMonitoringApi _api;

        public FakeStorage(JobStorage inner, IMonitoringApi api)
        {
            _inner = inner;
            _api = api;
        }

        public override IMonitoringApi GetMonitoringApi() => _api;

        public override IStorageConnection GetConnection() => _inner.GetConnection();
    }

    private sealed class FakeMonitoringApi : IMonitoringApi
    {
        public List<KeyValuePair<string, SucceededJobDto>> Succeeded { get; } = new();

        public int SucceededPageReads { get; private set; }

        public JobList<SucceededJobDto> SucceededJobs(int from, int count)
        {
            SucceededPageReads++;
            return new JobList<SucceededJobDto>(Succeeded.Skip(from).Take(count));
        }

        public JobList<FailedJobDto> FailedJobs(int from, int count)
            => new(Array.Empty<KeyValuePair<string, FailedJobDto>>());

        public IList<QueueWithTopEnqueuedJobsDto> Queues() => throw new NotSupportedException();
        public IList<ServerDto> Servers() => throw new NotSupportedException();
        public JobDetailsDto JobDetails(string jobId) => throw new NotSupportedException();
        public StatisticsDto GetStatistics() => throw new NotSupportedException();
        public JobList<EnqueuedJobDto> EnqueuedJobs(string queue, int from, int perPage) => throw new NotSupportedException();
        public JobList<FetchedJobDto> FetchedJobs(string queue, int from, int perPage) => throw new NotSupportedException();
        public JobList<ProcessingJobDto> ProcessingJobs(int from, int count) => throw new NotSupportedException();
        public JobList<ScheduledJobDto> ScheduledJobs(int from, int count) => throw new NotSupportedException();
        public JobList<DeletedJobDto> DeletedJobs(int from, int count) => throw new NotSupportedException();
        public long ScheduledCount() => throw new NotSupportedException();
        public long EnqueuedCount(string queue) => throw new NotSupportedException();
        public long FetchedCount(string queue) => throw new NotSupportedException();
        public long FailedCount() => throw new NotSupportedException();
        public long ProcessingCount() => throw new NotSupportedException();
        public long SucceededListCount() => Succeeded.Count;
        public long DeletedListCount() => throw new NotSupportedException();
        public IDictionary<DateTime, long> SucceededByDatesCount() => throw new NotSupportedException();
        public IDictionary<DateTime, long> FailedByDatesCount() => throw new NotSupportedException();
        public IDictionary<DateTime, long> HourlySucceededJobs() => throw new NotSupportedException();
        public IDictionary<DateTime, long> HourlyFailedJobs() => throw new NotSupportedException();
    }
}
