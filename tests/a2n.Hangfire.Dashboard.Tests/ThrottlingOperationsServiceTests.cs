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
        => new(_storage, audit ?? CreateAudit(), _options);

    private ThrottlingDataReader CreateReader() => new(_storage, _options);

    private void Seed(Action<IWriteOnlyTransaction> write)
    {
        using var connection = _storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        write(transaction);
        transaction.Commit();
    }

    private void SeedSemaphore(params string[] holders)
        => Seed(tx =>
        {
            tx.AddToSet("sync:set:sm", "email-dispatch");
            tx.SetRangeInHash("sync:sm:email-dispatch", new Dictionary<string, string> { ["max"] = "100", ["d"] = "" });
            foreach (var holder in holders)
            {
                tx.AddToSet("sync:j:sm:email-dispatch", holder);
            }
        });

    private void SeedMutex(string id, string holder)
        => Seed(tx =>
        {
            tx.AddToSet("sync:set:mx", $"{id}/{holder}");
            tx.AddToSet($"sync:mx:{id}", holder);
        });

    [Fact]
    public void DetachFromSemaphore_RemovesOnlyTheGivenHolder()
    {
        SeedSemaphore("41201", "41202");

        Assert.True(CreateService().DetachFromSemaphore("email-dispatch", "41201"));

        var semaphore = Assert.Single(CreateReader().GetSemaphores());
        Assert.Equal(new[] { "41202" }, semaphore.HolderJobIds);
    }

    [Fact]
    public void DetachFromSemaphore_WritesAuditEntry()
    {
        SeedSemaphore("41201");
        var audit = CreateAudit();

        CreateService(audit).DetachFromSemaphore("email-dispatch", "41201");

        var entry = Assert.Single(audit.Query(new AuditLogFilter(), 0, 10));
        Assert.Equal(AuditAction.ThrottlingSemaphoreDetached, entry.Action);
        Assert.Equal("email-dispatch", entry.Target);
        Assert.Contains("41201", entry.Reason);
    }

    [Fact]
    public void DetachFromSemaphore_IsIdempotent_WhenJobIsNotAHolder()
    {
        SeedSemaphore("41201");

        Assert.False(CreateService().DetachFromSemaphore("email-dispatch", "999"));

        var semaphore = Assert.Single(CreateReader().GetSemaphores());
        Assert.Equal(new[] { "41201" }, semaphore.HolderJobIds);
    }

    [Fact]
    public void DetachFromSemaphore_WritesNoAuditEntry_WhenNothingChanged()
    {
        // RemoveFromSet succeeds whether or not the entry was there, so without a check the audit
        // log would record a detach that freed nothing — and the operator would be told a slot was
        // recovered when it was already free.
        SeedSemaphore("41201");
        var audit = CreateAudit();

        CreateService(audit).DetachFromSemaphore("email-dispatch", "999");

        Assert.Empty(audit.Query(new AuditLogFilter(), 0, 10));
    }

    [Fact]
    public void DetachFromSemaphore_MatchesHolder_WhenIdCasingDiffers()
    {
        // Hangfire.Throttling lowercases ids on write, so an id arriving from a route or link with
        // different casing must still resolve to the stored key.
        SeedSemaphore("41201");

        Assert.True(CreateService().DetachFromSemaphore("Email-Dispatch", "41201"));
        Assert.Empty(Assert.Single(CreateReader().GetSemaphores()).HolderJobIds);
    }

    [Fact]
    public void Detach_DoesNothing_WhenDashboardIsReadOnly()
    {
        SeedSemaphore("41201");
        SeedMutex("resource_a", "1");
        _options.IsReadOnly = true;

        var audit = CreateAudit();
        var service = CreateService(audit);

        Assert.False(service.DetachFromSemaphore("email-dispatch", "41201"));
        Assert.False(service.DetachFromMutex("resource_a", "1"));

        Assert.Equal(new[] { "41201" }, Assert.Single(CreateReader().GetSemaphores()).HolderJobIds);
        Assert.Single(CreateReader().GetMutexes());
        Assert.Empty(audit.Query(new AuditLogFilter(), 0, 10));
    }

    [Fact]
    public void DetachFromMutex_RemovesRegistryPairAndHolder()
    {
        SeedMutex("resource_a", "41201");

        Assert.True(CreateService().DetachFromMutex("resource_a", "41201"));

        Assert.Empty(CreateReader().GetMutexes());

        using var connection = _storage.GetConnection();
        var storageConnection = Assert.IsAssignableFrom<JobStorageConnection>(connection);
        Assert.Equal(0, storageConnection.GetSetCount("sync:set:mx"));
        Assert.Equal(0, storageConnection.GetSetCount("sync:mx:resource_a"));
    }

    [Fact]
    public void DetachFromMutex_LeavesOtherMutexesIntact()
    {
        SeedMutex("resource_a", "1");
        SeedMutex("resource_b", "2");

        CreateService().DetachFromMutex("resource_a", "1");

        var remaining = Assert.Single(CreateReader().GetMutexes());
        Assert.Equal("resource_b", remaining.Id);
    }

    [Fact]
    public void DetachFromMutex_WritesAuditEntry()
    {
        SeedMutex("resource_a", "41201");
        var audit = CreateAudit();

        CreateService(audit).DetachFromMutex("resource_a", "41201");

        var entry = Assert.Single(audit.Query(new AuditLogFilter(), 0, 10));
        Assert.Equal(AuditAction.ThrottlingMutexDetached, entry.Action);
        Assert.Equal("resource_a", entry.Target);
    }

    [Fact]
    public void DetachFromMutex_WritesNoAuditEntry_WhenNothingChanged()
    {
        SeedMutex("resource_a", "1");
        var audit = CreateAudit();

        Assert.False(CreateService(audit).DetachFromMutex("resource_a", "999"));

        Assert.Empty(audit.Query(new AuditLogFilter(), 0, 10));
        Assert.Single(CreateReader().GetMutexes());
    }

    [Fact]
    public void DetachFromMutex_ReleasesHolder_RecordedOnlyInHolderSet()
    {
        // Tolerate a registry entry without its "/jobId" suffix, which GetMutexes already handles.
        Seed(tx =>
        {
            tx.AddToSet("sync:set:mx", "legacy_entry");
            tx.AddToSet("sync:mx:legacy_entry", "42");
        });

        Assert.True(CreateService().DetachFromMutex("legacy_entry", "42"));
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
