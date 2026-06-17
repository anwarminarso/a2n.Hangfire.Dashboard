using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.AspNetCore.Http;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for <see cref="AuditLogService"/> actor attribution (#2) and trimming (#4), using a real
/// in-memory Hangfire storage so the set/hash primitives behave like production.
/// </summary>
public class AuditLogServiceTests
{
    private readonly JobStorage _storage = new InMemoryStorage();
    private readonly DashboardUIOptions _options = new();

    private AuditLogService Create(AuditActorAccessor actor = null, IHttpContextAccessor http = null)
        => new(_storage, _options, http, actor, null);

    private IReadOnlyList<AuditLogEntry> AllEntries(AuditLogService svc)
        => svc.Query(new AuditLogFilter(), 0, 1000);

    [Fact]
    public void Log_UsesCircuitActor_WhenHttpContextNull()
    {
        var actor = new AuditActorAccessor();
        actor.Set("alice@example.com", "10.0.0.5");
        var svc = Create(actor, http: null);

        svc.Log(AuditAction.QueuePaused, target: "default");

        var entry = Assert.Single(AllEntries(svc));
        Assert.Equal("alice@example.com", entry.User);
        Assert.Equal("10.0.0.5", entry.ClientIp);
    }

    [Fact]
    public void Log_FallsBackToSystem_WhenNoActorAndNoHttpContext()
    {
        var svc = Create(actor: null, http: null);

        svc.Log(AuditAction.MaintenanceEnabled);

        var entry = Assert.Single(AllEntries(svc));
        Assert.Equal("(system)", entry.User);
    }

    [Fact]
    public void Log_ExplicitActor_OverridesAmbient()
    {
        var actor = new AuditActorAccessor();
        actor.Set("circuit-user", null);
        var svc = Create(actor);

        svc.Log(AuditAction.JobDeleted, target: "job-1", actor: "explicit-user");

        var entry = Assert.Single(AllEntries(svc));
        Assert.Equal("explicit-user", entry.User);
    }

    [Fact]
    public void Log_PersistsActionTargetReasonMetadata()
    {
        var svc = Create();
        svc.Log(AuditAction.QueuePaused, target: "emails", reason: "deploy",
            metadata: new Dictionary<string, string> { ["count"] = "3" });

        var entry = Assert.Single(AllEntries(svc));
        Assert.Equal(AuditAction.QueuePaused, entry.Action);
        Assert.Equal("emails", entry.Target);
        Assert.Equal("deploy", entry.Reason);
        Assert.Equal("3", entry.Metadata["count"]);
    }

    [Fact]
    public void Query_NewestFirst()
    {
        var svc = Create();
        svc.Log(AuditAction.QueuePaused, target: "q1");
        System.Threading.Thread.Sleep(5);
        svc.Log(AuditAction.QueueResumed, target: "q2");

        var entries = AllEntries(svc);
        Assert.Equal(2, entries.Count);
        Assert.Equal(AuditAction.QueueResumed, entries[0].Action); // most recent first
    }

    [Fact]
    public void Query_FiltersByActionPrefixAndTarget()
    {
        var svc = Create();
        svc.Log(AuditAction.QueuePaused, target: "alpha");
        svc.Log(AuditAction.JobDeleted, target: "beta");

        var queueOnly = svc.Query(new AuditLogFilter { ActionPrefix = "queue." }, 0, 100);
        Assert.Single(queueOnly);
        Assert.Equal(AuditAction.QueuePaused, queueOnly[0].Action);

        var betaOnly = svc.Query(new AuditLogFilter { Target = "beta" }, 0, 100);
        Assert.Single(betaOnly);
        Assert.Equal(AuditAction.JobDeleted, betaOnly[0].Action);
    }

    [Fact]
    public void TrimAsync_EnforcesMaxEntries()
    {
        _options.AuditLog.MaxEntries = 10;
        var svc = Create();

        for (var i = 0; i < 25; i++)
            svc.Log(AuditAction.QueuePaused, target: $"q{i}");

        svc.TrimAsync();

        var remaining = svc.Query(new AuditLogFilter(), 0, 1000);
        Assert.True(remaining.Count <= 10, $"expected ≤10 after trim, got {remaining.Count}");
    }

    [Fact]
    public void Disabled_DoesNotRecord()
    {
        _options.AuditLog.Enabled = false;
        var svc = Create();

        svc.Log(AuditAction.QueuePaused, target: "x");

        Assert.Empty(AllEntries(svc));
    }

    // --- QueryPage: numbered-pager support (filtered total + page slice) --------------------

    [Fact]
    public void QueryPage_ReturnsFilteredTotal_AndPageSlice()
    {
        var svc = Create();
        for (var i = 0; i < 5; i++)
        {
            svc.Log(AuditAction.QueuePaused, target: $"q{i}");
            System.Threading.Thread.Sleep(2);
        }

        var first = svc.QueryPage(new AuditLogFilter(), from: 0, count: 2);
        Assert.Equal(5, first.TotalCount);   // total across all pages
        Assert.Equal(2, first.Items.Count);  // page slice honours count

        var last = svc.QueryPage(new AuditLogFilter(), from: 4, count: 2);
        Assert.Equal(5, last.TotalCount);
        Assert.Single(last.Items);           // only the remaining entry on the last page
    }

    [Fact]
    public void QueryPage_TotalCount_ReflectsFilter()
    {
        var svc = Create();
        svc.Log(AuditAction.QueuePaused, target: "alpha");
        svc.Log(AuditAction.JobDeleted, target: "beta");
        svc.Log(AuditAction.QueueResumed, target: "gamma");

        var queueOnly = svc.QueryPage(new AuditLogFilter { ActionPrefix = "queue." }, from: 0, count: 10);
        Assert.Equal(2, queueOnly.TotalCount);
        Assert.Equal(2, queueOnly.Items.Count);
    }

    [Fact]
    public void QueryPage_NewestFirst()
    {
        var svc = Create();
        svc.Log(AuditAction.QueuePaused, target: "q1");
        System.Threading.Thread.Sleep(5);
        svc.Log(AuditAction.QueueResumed, target: "q2");

        var page = svc.QueryPage(new AuditLogFilter(), from: 0, count: 10);
        Assert.Equal(AuditAction.QueueResumed, page.Items[0].Action);
    }
}
