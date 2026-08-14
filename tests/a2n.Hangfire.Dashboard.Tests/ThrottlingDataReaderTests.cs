using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using Xunit;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for <see cref="ThrottlingDataReader"/> using a real in-memory Hangfire storage seeded
/// with the set/hash structures written by Hangfire.Throttling, so the read paths behave like
/// production.
/// </summary>
public class ThrottlingDataReaderTests
{
    private readonly JobStorage _storage = new InMemoryStorage();

    private ThrottlingDataReader CreateReader() => new(_storage);

    private void Seed(Action<IWriteOnlyTransaction> write)
    {
        using var connection = _storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        write(transaction);
        transaction.Commit();
    }

    private void SeedSemaphore(string id, string max, string description = "", params string[] holders)
        => Seed(tx =>
        {
            tx.AddToSet("sync:set:sm", id);
            tx.SetRangeInHash($"sync:sm:{id}", new Dictionary<string, string> { ["max"] = max, ["d"] = description });
            foreach (var holder in holders)
            {
                tx.AddToSet($"sync:j:sm:{id}", holder);
            }
        });

    private void SeedMutex(string id, params string[] holders)
        => Seed(tx =>
        {
            // The writer stores "{mutexId}/{jobId}" pairs in the registry plus a holder set.
            foreach (var holder in holders)
            {
                tx.AddToSet("sync:set:mx", $"{id}/{holder}");
                tx.AddToSet($"sync:mx:{id}", holder);
            }
        });

    [Fact]
    public void HasThrottlingData_ReturnsFalse_WhenStorageEmpty()
    {
        Assert.False(CreateReader().HasThrottlingData());
    }

    [Fact]
    public void HasThrottlingData_ReturnsTrue_WhenSemaphoreRegistered()
    {
        SeedSemaphore("email-dispatch", "100");

        Assert.True(CreateReader().HasThrottlingData());
    }

    [Fact]
    public void HasThrottlingData_ReturnsTrue_WhenOnlyMutexHeld()
    {
        SeedMutex("report-generation_42", "41201");

        Assert.True(CreateReader().HasThrottlingData());
    }

    [Fact]
    public void GetSemaphores_ReturnsEmpty_WhenNoneRegistered()
    {
        Assert.Empty(CreateReader().GetSemaphores());
    }

    [Fact]
    public void GetSemaphores_ReadsLimitDescriptionAndHolders()
    {
        SeedSemaphore("report-generation", "10", "Reporting concurrency budget", "41201", "41202");

        var semaphore = Assert.Single(CreateReader().GetSemaphores());

        Assert.Equal("report-generation", semaphore.Id);
        Assert.Equal(10, semaphore.MaxCount);
        Assert.Equal("Reporting concurrency budget", semaphore.Description);
        Assert.Equal(new[] { "41201", "41202" }, semaphore.HolderJobIds.OrderBy(x => x));
    }

    [Fact]
    public void GetSemaphores_NormalizesBlankDescriptionToNull()
    {
        SeedSemaphore("email-dispatch", "100", description: "");

        var semaphore = Assert.Single(CreateReader().GetSemaphores());

        Assert.Null(semaphore.Description);
    }

    [Fact]
    public void GetSemaphores_ReportsZeroHolders_WhenSemaphoreIdle()
    {
        SeedSemaphore("email-dispatch", "100");

        var semaphore = Assert.Single(CreateReader().GetSemaphores());

        Assert.Empty(semaphore.HolderJobIds);
    }

    [Fact]
    public void GetSemaphores_ToleratesRegistryEntryWithoutOptionsHash()
    {
        Seed(tx => tx.AddToSet("sync:set:sm", "orphaned"));

        var semaphore = Assert.Single(CreateReader().GetSemaphores());

        Assert.Equal("orphaned", semaphore.Id);
        Assert.Equal(0, semaphore.MaxCount);
        Assert.Null(semaphore.Description);
        Assert.Empty(semaphore.HolderJobIds);
    }

    [Fact]
    public void GetSemaphores_SortsById()
    {
        SeedSemaphore("zebra", "1");
        SeedSemaphore("alpha", "1");
        SeedSemaphore("Middle", "1");

        var ids = CreateReader().GetSemaphores().Select(x => x.Id).ToArray();

        Assert.Equal(new[] { "alpha", "Middle", "zebra" }, ids);
    }

    [Fact]
    public void GetMutexes_ReturnsEmpty_WhenNoneHeld()
    {
        Assert.Empty(CreateReader().GetMutexes());
    }

    [Fact]
    public void GetMutexes_ParsesRegistryPairsIntoIdAndHolder()
    {
        SeedMutex("order-export_customer-4821", "41201");

        var mutex = Assert.Single(CreateReader().GetMutexes());

        Assert.Equal("order-export_customer-4821", mutex.Id);
        Assert.Equal(new[] { "41201" }, mutex.HolderJobIds);
    }

    [Fact]
    public void GetMutexes_GroupsMultipleHoldersOfSameMutex()
    {
        SeedMutex("resource_a", "1", "2");

        var mutex = Assert.Single(CreateReader().GetMutexes());

        Assert.Equal("resource_a", mutex.Id);
        Assert.Equal(new[] { "1", "2" }, mutex.HolderJobIds.OrderBy(x => x));
    }

    [Fact]
    public void GetMutexes_ToleratesBareRegistryEntry_FallingBackToHolderSet()
    {
        Seed(tx =>
        {
            tx.AddToSet("sync:set:mx", "legacy_entry");
            tx.AddToSet("sync:mx:legacy_entry", "42");
        });

        var mutex = Assert.Single(CreateReader().GetMutexes());

        Assert.Equal("legacy_entry", mutex.Id);
        Assert.Equal(new[] { "42" }, mutex.HolderJobIds);
    }

    [Fact]
    public void GetSemaphore_ReturnsNull_WhenNotRegistered()
    {
        Assert.Null(CreateReader().GetSemaphore("missing"));
    }

    [Fact]
    public void GetSemaphore_ReadsSingleSemaphore()
    {
        SeedSemaphore("email-dispatch", "100", "Fleet-wide cap", "7");

        var semaphore = CreateReader().GetSemaphore("email-dispatch");

        Assert.NotNull(semaphore);
        Assert.Equal(100, semaphore.MaxCount);
        Assert.Equal("Fleet-wide cap", semaphore.Description);
        Assert.Equal(new[] { "7" }, semaphore.HolderJobIds);
    }

    [Fact]
    public void GetWindows_ReturnsEmpty_WhenNoneRegistered()
    {
        Assert.Empty(CreateReader().GetWindows());
    }

    // The window payloads below are verbatim from Hangfire.Throttling 1.4.3, captured by
    // registering each window through ThrottlingManager, driving jobs through it, and reading the
    // "obj" hash field back. SQL Server, Redis and in-memory storage all produce these same bytes.

    private void SeedWindow(string setKey, string keyPrefix, string id, string obj, string description = null)
        => Seed(tx =>
        {
            tx.AddToSet(setKey, id);

            var hash = new Dictionary<string, string> { ["obj"] = obj };
            if (description != null) hash["d"] = description;

            tx.SetRangeInHash(keyPrefix + id, hash);
        });

    [Fact]
    public void GetWindows_ReadsFixedWindow_FromObservedPayload()
    {
        // Registered as limit: 5, interval: 1h; three jobs then ran through it.
        SeedWindow("sync:set:fw", "sync:fw:", "probe-fixed",
            "{\"l\":5,\"i\":3600,\"w\":1786359600,\"c\":3}", "probe fixed");

        var window = Assert.Single(CreateReader().GetWindows());

        Assert.Equal("Fixed", window.Type);
        Assert.Equal("probe-fixed", window.Id);
        Assert.Equal("probe fixed", window.Description);
        Assert.Equal(5, window.Limit);
        Assert.Equal(3600, window.IntervalSeconds);
        Assert.Equal(3, window.Counter);
    }

    [Fact]
    public void GetWindows_ReadsSlidingWindow_SummingBucketCounts()
    {
        // Registered as limit: 4, interval: 600s, buckets: 5 — note "b" is the bucket size in
        // seconds (600 / 5), not the bucket count, and "c" is a bucket map rather than a number.
        SeedWindow("sync:set:sw", "sync:sw:", "probe-sliding",
            "{\"l\":4,\"i\":600,\"b\":120,\"t\":1786362360,\"c\":{\"0\":3,\"1\":1}}");

        var window = Assert.Single(CreateReader().GetWindows());

        Assert.Equal("Sliding", window.Type);
        Assert.Equal(4, window.Limit);
        Assert.Equal(600, window.IntervalSeconds);
        Assert.Equal(4, window.Counter);
    }

    [Fact]
    public void GetWindows_ReadsDynamicWindow_SummingNestedBucketCounts()
    {
        // Registered as limit: 3, interval: 600s, buckets: 5. A dynamic window carries no "l" and
        // tracks counts per window format, so "w" is a nested map rather than a timestamp.
        SeedWindow("sync:set:dp", "sync:dp:", "probe-dynamic",
            "{\"i\":600,\"b\":120,\"t\":1786362360,\"maxc\":1000,\"maxs\":3,\"mins\":3,\"w\":{\"probe-dynamic\":{\"0\":3}}}");

        var window = Assert.Single(CreateReader().GetWindows());

        Assert.Equal("Dynamic", window.Type);
        Assert.Null(window.Limit);
        Assert.Equal(600, window.IntervalSeconds);
        Assert.Equal(3, window.Counter);
    }

    [Fact]
    public void GetWindows_ReadsDynamicWindowCapacity_AsLimit()
    {
        // Registered with capacity: 20, minLimit: 2, maxLimit: 8 — the capacity form does store "l".
        SeedWindow("sync:set:dp", "sync:dp:", "probe-dynamic-capacity",
            "{\"l\":20,\"i\":600,\"b\":120,\"maxc\":1000,\"maxs\":2,\"mins\":8}");

        var window = Assert.Single(CreateReader().GetWindows());

        Assert.Equal(20, window.Limit);
    }

    [Theory]
    [InlineData("sync:set:fw", "sync:fw:")]
    [InlineData("sync:set:sw", "sync:sw:")]
    [InlineData("sync:set:dp", "sync:dp:")]
    public void GetWindows_ReportsNoCount_BeforeAnyJobHasRun(string setKey, string keyPrefix)
    {
        // Freshly registered windows carry only their configuration. No count is not a count of
        // zero, so the column should read as unknown rather than as "0 executions".
        var obj = keyPrefix switch
        {
            "sync:fw:" => "{\"l\":5,\"i\":3600}",
            "sync:sw:" => "{\"l\":4,\"i\":600,\"b\":120}",
            _ => "{\"i\":600,\"b\":120,\"maxc\":1000,\"maxs\":3,\"mins\":3}",
        };

        SeedWindow(setKey, keyPrefix, "fresh", obj);

        var window = Assert.Single(CreateReader().GetWindows());

        Assert.Null(window.Counter);
        Assert.NotNull(window.IntervalSeconds);
    }

    [Fact]
    public void GetWindows_ListsWindow_WhenStateIsUnparseable()
    {
        SeedWindow("sync:set:dp", "sync:dp:", "adaptive", "not-json", "Adaptive");

        var window = Assert.Single(CreateReader().GetWindows());

        Assert.Equal("adaptive", window.Id);
        Assert.Equal("Adaptive", window.Description);
        Assert.Null(window.Limit);
        Assert.Null(window.IntervalSeconds);
        Assert.Null(window.Counter);
    }

    [Fact]
    public void GetWindows_ReadsAllThreeKinds_Together()
    {
        SeedWindow("sync:set:fw", "sync:fw:", "reports", "{\"l\":10,\"i\":3600,\"c\":4}", "Hourly report cap");
        SeedWindow("sync:set:sw", "sync:sw:", "api-calls", "{\"l\":100,\"i\":60,\"b\":12}");
        SeedWindow("sync:set:dp", "sync:dp:", "adaptive", "{\"i\":600,\"b\":120,\"maxs\":3,\"mins\":3}");

        var windows = CreateReader().GetWindows();

        Assert.Equal(3, windows.Count);
        Assert.Equal(4, Assert.Single(windows, x => x.Type == "Fixed").Counter);
        Assert.Equal(100, Assert.Single(windows, x => x.Type == "Sliding").Limit);
        Assert.Null(Assert.Single(windows, x => x.Type == "Dynamic").Limit);
    }

    [Fact]
    public void HasThrottlingData_ReturnsTrue_WhenOnlyWindowRegistered()
    {
        Seed(tx => tx.AddToSet("sync:set:fw", "reports"));

        Assert.True(CreateReader().HasThrottlingData());
    }

    [Fact]
    public void GetHolderDetails_FlagsMissingJob_AsOrphaned()
    {
        // Holder sets carry no expiry, so an entry whose job record has already aged out of
        // storage will never be removed by anything — the slot is lost until someone detaches it.
        var holder = Assert.Single(CreateReader().GetHolderDetails(new[] { "999" }));

        Assert.Equal("999", holder.JobId);
        Assert.Null(holder.StateName);
        Assert.True(holder.IsOrphaned);
        Assert.Contains("no longer exists", holder.OrphanReason);
    }

    [Fact]
    public void GetHolderDetails_FlagsProcessingJobOnDeadServer_AsOrphaned()
    {
        var jobId = CreateJob(new FakeState(global::Hangfire.States.ProcessingState.StateName, new Dictionary<string, string>
        {
            ["ServerId"] = "dead-server",
            ["WorkerId"] = "1",
        }));

        var holder = Assert.Single(CreateReader().GetHolderDetails(new[] { jobId }));

        Assert.Equal("Processing", holder.StateName);
        Assert.Equal("dead-server", holder.ServerId);
        Assert.True(holder.IsOrphaned);
        Assert.Contains("heartbeat", holder.OrphanReason);
    }

    [Theory]
    [InlineData("Succeeded", "SucceededAt")]
    [InlineData("Failed", "FailedAt")]
    [InlineData("Deleted", "DeletedAt")]
    public void GetHolderDetails_FlagsStaleFinalState_AsOrphaned(string stateName, string timestampField)
    {
        // A job in a final state is not running and cannot release anything, so a slot it still
        // holds an hour later is leaked — the case that motivated the page's detach button.
        var anHourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        var jobId = CreateJob(new FakeState(stateName, new Dictionary<string, string>
        {
            [timestampField] = anHourAgo.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));

        var holder = Assert.Single(CreateReader().GetHolderDetails(new[] { jobId }));

        Assert.Equal(stateName, holder.StateName);
        Assert.True(holder.IsOrphaned);
        Assert.Contains("never released", holder.OrphanReason);
    }

    [Fact]
    public void GetHolderDetails_DoesNotFlagJustFinishedJob_AsOrphaned()
    {
        // The release and the state transition are separate writes, so a job that succeeded a
        // moment ago may legitimately still appear as a holder. Flagging it would invite an
        // operator to "recover" a slot that was about to be freed anyway.
        var jobId = CreateJob(new FakeState("Succeeded", new Dictionary<string, string>
        {
            ["SucceededAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));

        var holder = Assert.Single(CreateReader().GetHolderDetails(new[] { jobId }));

        Assert.False(holder.IsOrphaned);
        Assert.Null(holder.OrphanReason);
    }

    public static TheoryData<global::Hangfire.States.IState> PendingStates => new()
    {
        // Real states here rather than fakes: Hangfire registers handlers for these two by name
        // and rejects a foreign state that claims them.
        new global::Hangfire.States.EnqueuedState(),
        new global::Hangfire.States.ScheduledState(TimeSpan.FromMinutes(5)),
    };

    [Theory]
    [MemberData(nameof(PendingStates))]
    public void GetHolderDetails_DoesNotFlagPendingJob_AsOrphaned(global::Hangfire.States.IState state)
    {
        // A job waiting to run is neither final nor stranded on a dead server, and it will release
        // its slot when it finishes. Only a job that cannot release is a detach candidate.
        var jobId = CreateJob(state);

        var holder = Assert.Single(CreateReader().GetHolderDetails(new[] { jobId }));

        Assert.Equal(state.Name, holder.StateName);
        Assert.False(holder.IsOrphaned);
        Assert.Null(holder.OrphanReason);
    }

    private string CreateJob(global::Hangfire.States.IState state) =>
        new global::Hangfire.BackgroundJobClient(_storage).Create(
            global::Hangfire.Common.Job.FromExpression(() => Console.WriteLine("noop")),
            state);

    /// <summary>
    /// Built-in state constructors are internal, so states are mimicked by name and serialized
    /// data — which is all the reader ever sees, since it works from stored state data.
    /// </summary>
    private sealed class FakeState : global::Hangfire.States.IState
    {
        private readonly Dictionary<string, string> _data;

        public FakeState(string name, Dictionary<string, string> data)
        {
            Name = name;
            _data = data;
        }

        public string Name { get; }
        public string Reason => null;
        public bool IsFinal => false;
        public bool IgnoreJobLoadException => false;

        public Dictionary<string, string> SerializeData() => _data;
    }
}
