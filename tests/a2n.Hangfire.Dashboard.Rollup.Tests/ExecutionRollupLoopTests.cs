using Hangfire;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace a2n.Hangfire.Dashboard.Rollup.Tests;

/// <summary>
/// The collector's poll loop (issue #64): a fixed cadence that backs off while a pass fails or takes
/// the whole interval, instead of starting the next pass straight away, and that can be turned off.
/// </summary>
public class ExecutionRollupLoopTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    [Fact]
    public void A_healthy_pass_waits_for_what_is_left_of_the_interval()
    {
        var next = ExecutionRollupCollector.NextDelay(Interval, MaxBackoff, Interval, healthy: true,
            elapsed: TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(40), next);
    }

    [Fact]
    public void A_failed_pass_doubles_from_the_interval_up_to_the_cap()
    {
        // The first pass follows the 10 s initial delay, which is shorter than the interval.
        var delay = TimeSpan.FromSeconds(10);
        var seen = new List<TimeSpan>();
        for (var i = 0; i < 4; i++)
        {
            delay = ExecutionRollupCollector.NextDelay(Interval, MaxBackoff, delay, healthy: false, TimeSpan.Zero);
            seen.Add(delay);
        }

        Assert.Equal(new[] { 120, 240, 300, 300 }, seen.Select(d => (int)d.TotalSeconds));
    }

    [Fact]
    public void The_interval_is_never_slowed_below_itself_when_the_cap_is_shorter()
    {
        var next = ExecutionRollupCollector.NextDelay(Interval, TimeSpan.FromSeconds(30), Interval,
            healthy: false, elapsed: TimeSpan.FromSeconds(90));

        Assert.Equal(Interval, next);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(60, true)]
    public async Task Start_runs_the_loop_only_while_the_poll_interval_is_positive(int seconds, bool expectRunning)
    {
        var options = new DashboardUIOptions();
        options.Polling.RollupPollInterval = TimeSpan.FromSeconds(seconds);
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<JobStorage>(new InMemoryStorage());
        using var collector = new ExecutionRollupCollector(services.BuildServiceProvider());

        await collector.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(expectRunning, collector.IsRunning);
        }
        finally
        {
            await collector.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
