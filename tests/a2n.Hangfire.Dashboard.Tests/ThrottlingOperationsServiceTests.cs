using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for <see cref="ThrottlingOperationsService"/> against real in-memory Hangfire storage.
/// Detach writes mirror the Hangfire.Throttling package's own storage behavior.
/// </summary>
public class ThrottlingOperationsServiceTests
{
    private readonly JobStorage _storage = new InMemoryStorage();
    private readonly DashboardUIOptions _options = new();

    private AuditLogService CreateAudit() => new(_storage, _options, null, null, null);

    private ThrottlingOperationsService CreateService(AuditLogService audit = null)
        => new(_storage, audit ?? CreateAudit());

    private ThrottlingDataReader CreateReader() => new(_storage);

    private void Seed(Action<IWriteOnlyTransaction> write)
    {
        using var connection = _storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        write(transaction);
        transaction.Commit();
    }

    [Fact]
    public void DetachFromSemaphore_RemovesOnlyTheGivenHolder()
    {
        Seed(tx =>
        {
            tx.AddToSet("sync:set:sm", "globallimiter");
            tx.SetRangeInHash("sync:sm:globallimiter", new Dictionary<string, string> { ["max"] = "100", ["d"] = "" });
            tx.AddToSet("sync:j:sm:globallimiter", "13538");
            tx.AddToSet("sync:j:sm:globallimiter", "13558");
        });

        CreateService().DetachFromSemaphore("globallimiter", "13538");

        var semaphore = Assert.Single(CreateReader().GetSemaphores());
        Assert.Equal(new[] { "13558" }, semaphore.HolderJobIds);
    }

    [Fact]
    public void DetachFromSemaphore_WritesAuditEntry()
    {
        var audit = CreateAudit();

        CreateService(audit).DetachFromSemaphore("globallimiter", "13538");

        var entry = Assert.Single(audit.Query(new AuditLogFilter(), 0, 10));
        Assert.Equal(AuditAction.ThrottlingSemaphoreDetached, entry.Action);
        Assert.Equal("globallimiter", entry.Target);
        Assert.Contains("13538", entry.Reason);
    }

    [Fact]
    public void DetachFromSemaphore_IsIdempotent_WhenJobIsNotAHolder()
    {
        CreateService().DetachFromSemaphore("globallimiter", "999");
    }

    [Fact]
    public void DetachFromMutex_RemovesRegistryPairAndHolder()
    {
        Seed(tx =>
        {
            tx.AddToSet("sync:set:mx", "resource_a/13538");
            tx.AddToSet("sync:mx:resource_a", "13538");
        });

        CreateService().DetachFromMutex("resource_a", "13538");

        Assert.Empty(CreateReader().GetMutexes());

        using var connection = _storage.GetConnection();
        var storageConnection = Assert.IsAssignableFrom<JobStorageConnection>(connection);
        Assert.Equal(0, storageConnection.GetSetCount("sync:set:mx"));
        Assert.Equal(0, storageConnection.GetSetCount("sync:mx:resource_a"));
    }

    [Fact]
    public void DetachFromMutex_LeavesOtherMutexesIntact()
    {
        Seed(tx =>
        {
            tx.AddToSet("sync:set:mx", "resource_a/1");
            tx.AddToSet("sync:mx:resource_a", "1");
            tx.AddToSet("sync:set:mx", "resource_b/2");
            tx.AddToSet("sync:mx:resource_b", "2");
        });

        CreateService().DetachFromMutex("resource_a", "1");

        var remaining = Assert.Single(CreateReader().GetMutexes());
        Assert.Equal("resource_b", remaining.Id);
    }

    [Fact]
    public void DetachFromMutex_WritesAuditEntry()
    {
        var audit = CreateAudit();

        CreateService(audit).DetachFromMutex("resource_a", "13538");

        var entry = Assert.Single(audit.Query(new AuditLogFilter(), 0, 10));
        Assert.Equal(AuditAction.ThrottlingMutexDetached, entry.Action);
        Assert.Equal("resource_a", entry.Target);
    }

    [Fact]
    public void Detach_IgnoresBlankArguments()
    {
        var audit = CreateAudit();
        var service = CreateService(audit);

        service.DetachFromSemaphore(null, "1");
        service.DetachFromSemaphore("s", "");
        service.DetachFromMutex("", "1");
        service.DetachFromMutex("m", null);

        Assert.Empty(audit.Query(new AuditLogFilter(), 0, 10));
    }
}
