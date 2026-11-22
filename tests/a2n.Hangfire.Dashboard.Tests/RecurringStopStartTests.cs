using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;
using Xunit;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Stop/Start of recurring jobs must round-trip jobs whose method takes parameters (Issue #66).
/// </summary>
public class RecurringStopStartTests
{
    private static (InMemoryStorage Storage, HangfireMonitorService Service) NewService()
    {
        var storage = new InMemoryStorage();
        var options = new DashboardUIOptions { IsReadOnly = false, EnableJobManagement = true };
        return (storage, new HangfireMonitorService(storage, null, options, new JobMethodResolver()));
    }

    private static RecurringJobDto GetRecurring(JobStorage storage, string id)
    {
        using var connection = storage.GetConnection();
        return connection.GetRecurringJobs().FirstOrDefault(j => j.Id == id);
    }

    [Fact]
    public void StopThenStart_CancellationTokenMethod_IsRestored()
    {
        var (storage, svc) = NewService();
        new RecurringJobManager(storage).AddOrUpdate<StopStartFixtureJob>(
            "email-sending", j => j.ExecuteAsync(CancellationToken.None), "*/5 * * * *");

        svc.StopRecurringJob("email-sending");
        Assert.Null(GetRecurring(storage, "email-sending"));

        svc.StartRecurringJob("email-sending");

        var restored = GetRecurring(storage, "email-sending");
        Assert.NotNull(restored);
        Assert.Equal("*/5 * * * *", restored.Cron);
        Assert.Equal(nameof(StopStartFixtureJob.ExecuteAsync), restored.Job.Method.Name);
        Assert.DoesNotContain("email-sending", svc.GetStoppedRecurringJobIds());
    }

    [Fact]
    public void StopThenStart_PreservesArgumentValues()
    {
        var (storage, svc) = NewService();
        new RecurringJobManager(storage).AddOrUpdate<StopStartFixtureJob>(
            "report", j => j.Run("monthly", 42), "0 1 * * *");

        svc.StopRecurringJob("report");
        svc.StartRecurringJob("report");

        var restored = GetRecurring(storage, "report");
        Assert.NotNull(restored);
        Assert.Equal(new object[] { "monthly", 42 }, restored.Job.Args.ToArray());
    }

    [Fact]
    public void Start_LegacyEntryWithoutPayload_RebuildsWithDefaultArguments()
    {
        var (storage, svc) = NewService();

        // A stopped-job entry written by a version before Issue #66: names only, no "Job" payload.
        using (var connection = storage.GetConnection())
        using (var transaction = connection.CreateWriteTransaction())
        {
            transaction.SetRangeInHash("recurring:stopped:legacy", new Dictionary<string, string>
            {
                ["Cron"] = "0 2 * * *",
                ["Queue"] = "default",
                ["TimeZoneId"] = "UTC",
                ["TypeName"] = typeof(StopStartFixtureJob).FullName,
                ["MethodName"] = nameof(StopStartFixtureJob.ExecuteAsync),
                ["StoppedAt"] = DateTime.UtcNow.ToString("O"),
            });
            transaction.AddToSet("recurring:stopped", "legacy");
            transaction.Commit();
        }

        svc.StartRecurringJob("legacy");

        var restored = GetRecurring(storage, "legacy");
        Assert.NotNull(restored);
        Assert.Single(restored.Job.Args);
        Assert.DoesNotContain("legacy", svc.GetStoppedRecurringJobIds());
    }

    [Fact]
    public void Start_UnresolvableType_ThrowsAndKeepsStoppedEntry()
    {
        var (storage, svc) = NewService();
        using (var connection = storage.GetConnection())
        using (var transaction = connection.CreateWriteTransaction())
        {
            transaction.SetRangeInHash("recurring:stopped:missing", new Dictionary<string, string>
            {
                ["Cron"] = "0 2 * * *",
                ["TypeName"] = "Does.Not.Exist",
                ["MethodName"] = "Run",
            });
            transaction.AddToSet("recurring:stopped", "missing");
            transaction.Commit();
        }

        Assert.Throws<InvalidOperationException>(() => svc.StartRecurringJob("missing"));
        Assert.Contains("missing", svc.GetStoppedRecurringJobIds());
    }

    // ── Repeated Stop/Start (issue #98) ──

    private static void AddFixtureJob(JobStorage storage, string id)
        => new RecurringJobManager(storage).AddOrUpdate<StopStartFixtureJob>(
            id, j => j.ExecuteAsync(CancellationToken.None), Cron.Never());

    /// <summary>
    /// The storage state a second Stop left before issue #98: the job is out of the scheduler, its id
    /// is in the stopped set, and its hash holds the config next to the "_deleted" marker of Start.
    /// </summary>
    internal static void SeedHiddenStoppedJob(JobStorage storage, string id)
    {
        using var connection = storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash($"recurring:stopped:{id}", new Dictionary<string, string>
        {
            ["Cron"] = "0 3 * * *",
            ["Queue"] = "default",
            ["TimeZoneId"] = "UTC",
            ["TypeName"] = typeof(StopStartFixtureJob).FullName,
            ["MethodName"] = nameof(StopStartFixtureJob.ExecuteAsync),
            ["StoppedAt"] = DateTime.UtcNow.ToString("O"),
            ["_deleted"] = "true",
        });
        transaction.AddToSet("recurring:stopped", id);
        transaction.Commit();
    }

    [Fact]
    public void StopStartStop_RemainsVisibleAsStopped()
    {
        var (storage, svc) = NewService();
        AddFixtureJob(storage, "second-stop");

        svc.StopRecurringJob("second-stop");
        svc.StartRecurringJob("second-stop");
        svc.StopRecurringJob("second-stop");

        Assert.Null(GetRecurring(storage, "second-stop"));
        Assert.Contains("second-stop", svc.GetStoppedRecurringJobIds());

        var config = svc.GetStoppedJobConfig("second-stop");
        Assert.NotNull(config);
        Assert.False(config.ContainsKey("_deleted"));
        Assert.Equal("0 0 31 2 *", config["Cron"]);
    }

    [Fact]
    public void StopStartStopStart_RestoresTheJobAgain()
    {
        var (storage, svc) = NewService();
        AddFixtureJob(storage, "cycle");

        svc.StopRecurringJob("cycle");
        svc.StartRecurringJob("cycle");
        svc.StopRecurringJob("cycle");
        svc.StartRecurringJob("cycle");

        Assert.NotNull(GetRecurring(storage, "cycle"));
        Assert.DoesNotContain("cycle", svc.GetStoppedRecurringJobIds());
    }

    [Fact]
    public void Start_RemovesTheStoppedEntry()
    {
        var (storage, svc) = NewService();
        AddFixtureJob(storage, "cleanup");

        svc.StopRecurringJob("cleanup");
        svc.StartRecurringJob("cleanup");

        var config = svc.GetStoppedJobConfig("cleanup");
        Assert.True(config is null || config.Count == 0, "Start should remove the stopped-job hash.");
    }

    [Fact]
    public void Stop_ClearsTheMarkerAnEarlierVersionLeft()
    {
        var (storage, svc) = NewService();
        AddFixtureJob(storage, "upgraded");
        // Stop then Start by a version before #98: the hash keeps the old config and the marker.
        SeedHiddenStoppedJob(storage, "upgraded");
        using (var connection = storage.GetConnection())
        using (var transaction = connection.CreateWriteTransaction())
        {
            transaction.RemoveFromSet("recurring:stopped", "upgraded");
            transaction.Commit();
        }

        svc.StopRecurringJob("upgraded");

        var config = svc.GetStoppedJobConfig("upgraded");
        Assert.False(config.ContainsKey("_deleted"));
        Assert.Equal("0 0 31 2 *", config["Cron"]);
        Assert.Contains("upgraded", svc.GetStoppedRecurringJobIds());
    }

    [Fact]
    public void Start_RestoresAJobAnEarlierVersionHid()
    {
        var (storage, svc) = NewService();
        SeedHiddenStoppedJob(storage, "hidden");

        svc.StartRecurringJob("hidden");

        var restored = GetRecurring(storage, "hidden");
        Assert.NotNull(restored);
        Assert.Equal("0 3 * * *", restored.Cron);
        Assert.DoesNotContain("hidden", svc.GetStoppedRecurringJobIds());
        var config = svc.GetStoppedJobConfig("hidden");
        Assert.True(config is null || config.Count == 0);
    }
}

/// <summary>Recurring-job targets for <see cref="RecurringStopStartTests"/>; never invoked.</summary>
public sealed class StopStartFixtureJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Run(string period, int count) { }
}
