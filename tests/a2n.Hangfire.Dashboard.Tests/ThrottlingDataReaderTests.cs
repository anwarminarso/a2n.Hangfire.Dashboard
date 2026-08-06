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
        SeedSemaphore("globallimiter", "100");

        Assert.True(CreateReader().HasThrottlingData());
    }

    [Fact]
    public void HasThrottlingData_ReturnsTrue_WhenOnlyMutexHeld()
    {
        SeedMutex("recalculate_42", "13538");

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
        SeedSemaphore("recalculateinventory", "10", "Inventory concurrency budget", "13538", "13558");

        var semaphore = Assert.Single(CreateReader().GetSemaphores());

        Assert.Equal("recalculateinventory", semaphore.Id);
        Assert.Equal(10, semaphore.MaxCount);
        Assert.Equal("Inventory concurrency budget", semaphore.Description);
        Assert.Equal(new[] { "13538", "13558" }, semaphore.HolderJobIds.OrderBy(x => x));
    }

    [Fact]
    public void GetSemaphores_NormalizesBlankDescriptionToNull()
    {
        SeedSemaphore("globallimiter", "100", description: "");

        var semaphore = Assert.Single(CreateReader().GetSemaphores());

        Assert.Null(semaphore.Description);
    }

    [Fact]
    public void GetSemaphores_ReportsZeroHolders_WhenSemaphoreIdle()
    {
        SeedSemaphore("globallimiter", "100");

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
        SeedMutex("repricezerodollarticket_3ec979f5", "13538");

        var mutex = Assert.Single(CreateReader().GetMutexes());

        Assert.Equal("repricezerodollarticket_3ec979f5", mutex.Id);
        Assert.Equal(new[] { "13538" }, mutex.HolderJobIds);
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
        SeedSemaphore("globallimiter", "100", "Fleet-wide cap", "7");

        var semaphore = CreateReader().GetSemaphore("globallimiter");

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

    [Fact]
    public void GetWindows_ReadsAllThreeKinds_WithStateAndDescription()
    {
        Seed(tx =>
        {
            tx.AddToSet("sync:set:fw", "reports");
            tx.SetRangeInHash("sync:fw:reports", new Dictionary<string, string>
            {
                ["obj"] = "{\"Limit\":10,\"IntervalInSeconds\":3600,\"ActiveWindow\":123,\"Counter\":4}",
                ["d"] = "Hourly report cap",
            });

            tx.AddToSet("sync:set:sw", "api-calls");
            tx.SetRangeInHash("sync:sw:api-calls", new Dictionary<string, string>
            {
                ["obj"] = "{\"Limit\":100,\"IntervalInSeconds\":60}",
            });

            tx.AddToSet("sync:set:dp", "adaptive");
            tx.SetRangeInHash("sync:dp:adaptive", new Dictionary<string, string> { ["obj"] = "not-json" });
        });

        var windows = CreateReader().GetWindows();

        Assert.Equal(3, windows.Count);

        var fixedWindow = Assert.Single(windows, x => x.Type == "Fixed");
        Assert.Equal("reports", fixedWindow.Id);
        Assert.Equal("Hourly report cap", fixedWindow.Description);
        Assert.Equal(10, fixedWindow.Limit);
        Assert.Equal(3600, fixedWindow.IntervalSeconds);
        Assert.Equal(4, fixedWindow.Counter);

        var slidingWindow = Assert.Single(windows, x => x.Type == "Sliding");
        Assert.Equal(100, slidingWindow.Limit);
        Assert.Null(slidingWindow.Counter);

        var dynamicWindow = Assert.Single(windows, x => x.Type == "Dynamic");
        Assert.Equal("adaptive", dynamicWindow.Id);
        Assert.Null(dynamicWindow.Limit);
    }

    [Fact]
    public void HasThrottlingData_ReturnsTrue_WhenOnlyWindowRegistered()
    {
        Seed(tx => tx.AddToSet("sync:set:fw", "reports"));

        Assert.True(CreateReader().HasThrottlingData());
    }

    [Fact]
    public void GetHolderDetails_ReportsUnknownState_ForMissingJobs()
    {
        var holder = Assert.Single(CreateReader().GetHolderDetails(new[] { "999" }));

        Assert.Equal("999", holder.JobId);
        Assert.Null(holder.StateName);
        Assert.False(holder.IsOrphaned);
    }

    [Fact]
    public void GetHolderDetails_FlagsProcessingJobOnDeadServer_AsOrphaned()
    {
        var jobId = new global::Hangfire.BackgroundJobClient(_storage).Create(
            global::Hangfire.Common.Job.FromExpression(() => Console.WriteLine("noop")),
            new FakeProcessingState("dead-server"));

        var holder = Assert.Single(CreateReader().GetHolderDetails(new[] { jobId }));

        Assert.Equal("Processing", holder.StateName);
        Assert.Equal("dead-server", holder.ServerId);
        Assert.True(holder.IsOrphaned);
    }

    /// <summary>
    /// ProcessingState's constructor is internal, so the Processing state is mimicked with the
    /// same name and serialized data — the reader only ever sees the stored state data.
    /// </summary>
    private sealed class FakeProcessingState : global::Hangfire.States.IState
    {
        private readonly string _serverId;

        public FakeProcessingState(string serverId) => _serverId = serverId;

        public string Name => global::Hangfire.States.ProcessingState.StateName;
        public string Reason => null;
        public bool IsFinal => false;
        public bool IgnoreJobLoadException => false;

        public Dictionary<string, string> SerializeData() => new()
        {
            ["ServerId"] = _serverId,
            ["WorkerId"] = "1",
        };
    }
}
