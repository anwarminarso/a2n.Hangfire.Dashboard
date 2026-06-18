using System;
using System.Collections.Generic;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Shared;
using a2n.Hangfire.Dashboard.Models;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// bUnit component tests for the Drill-Down Drawer (Components/Shared/DrillDownDrawer.razor),
/// covering the requirement-level gating and error handling (task 20.3):
///
///   • Req 10.4 — when EnableJobManagement is on and IsReadOnly is off, each listed job exposes an
///     "Edit schedule" action;
///   • Req 10.5 — when EnableJobManagement is off OR IsReadOnly is on, all schedule-editing actions
///     are hidden;
///   • Req 10.6 — a "View executions" action is always available for every listed job;
///   • Req 10.2 — a visible cell with no contributing jobs and no error never opens the drawer
///     (no offcanvas is rendered);
///   • Req 10.7 — when the lookup failed (DrillDownResult.Error is set), the drawer opens and
///     surfaces an error indication.
///
/// The drawer @injects <see cref="DashboardUIOptions"/> and reuses <c>ScheduleBuilder</c> for the
/// edit action; <c>ScheduleBuilder</c> has no service dependencies, so registering the options is
/// sufficient. JSInterop runs in Loose mode for harmless no-op interop.
/// </summary>
public class DrillDownDrawerTests
{
    private static TestContext NewContext(bool enableJobManagement, bool isReadOnly)
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var options = new DashboardUIOptions
        {
            EnableJobManagement = enableJobManagement,
            IsReadOnly = isReadOnly,
        };
        ctx.Services.AddSingleton(options);
        return ctx;
    }

    private static DrillDownJob Job(string id = "job-a", string queue = "default") =>
        new(id, "0 9 * * *", queue, TimeSpan.FromMinutes(2), new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero));

    private static DrillDownResult ResultWith(params DrillDownJob[] jobs) =>
        new(jobs, null);

    // -- Req 10.4: edit action visible when job management on and not read-only -----------------

    [Fact]
    public void EditAction_Visible_WhenJobManagementEnabled_AndNotReadOnly()
    {
        using var ctx = NewContext(enableJobManagement: true, isReadOnly: false);

        var cut = ctx.RenderComponent<DrillDownDrawer>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.Result, ResultWith(Job())));

        Assert.Contains("Edit schedule", cut.Markup);
    }

    // -- Req 10.5: edit action hidden when job management off, or read-only ---------------------

    [Fact]
    public void EditAction_Hidden_WhenJobManagementDisabled()
    {
        using var ctx = NewContext(enableJobManagement: false, isReadOnly: false);

        var cut = ctx.RenderComponent<DrillDownDrawer>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.Result, ResultWith(Job())));

        // The drawer opens (a job is present) but no schedule-editing action is offered (Req 10.5).
        Assert.NotEmpty(cut.FindAll(".offcanvas"));
        Assert.DoesNotContain("Edit schedule", cut.Markup);
    }

    [Fact]
    public void EditAction_Hidden_WhenReadOnly()
    {
        using var ctx = NewContext(enableJobManagement: true, isReadOnly: true);

        var cut = ctx.RenderComponent<DrillDownDrawer>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.Result, ResultWith(Job())));

        Assert.NotEmpty(cut.FindAll(".offcanvas"));
        Assert.DoesNotContain("Edit schedule", cut.Markup);
    }

    [Fact]
    public void EditAction_Hidden_WhenJobManagementDisabled_AndReadOnly()
    {
        using var ctx = NewContext(enableJobManagement: false, isReadOnly: true);

        var cut = ctx.RenderComponent<DrillDownDrawer>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.Result, ResultWith(Job())));

        Assert.NotEmpty(cut.FindAll(".offcanvas"));
        Assert.DoesNotContain("Edit schedule", cut.Markup);
    }

    // -- Req 10.6: view-executions action always available --------------------------------------

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ViewExecutionsAction_AlwaysAvailable_RegardlessOfGating(bool enableJobManagement, bool isReadOnly)
    {
        using var ctx = NewContext(enableJobManagement, isReadOnly);

        var cut = ctx.RenderComponent<DrillDownDrawer>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.Result, ResultWith(Job())));

        // Req 10.6 — "View executions" is offered for the listed job in every gating combination.
        Assert.Contains("View executions", cut.Markup);
    }

    // -- Req 10.2: never open for an empty cell -------------------------------------------------

    [Fact]
    public void EmptyCell_Visible_NoJobsNoError_DoesNotOpenDrawer()
    {
        using var ctx = NewContext(enableJobManagement: true, isReadOnly: false);

        var cut = ctx.RenderComponent<DrillDownDrawer>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.Result, new DrillDownResult(Array.Empty<DrillDownJob>(), null)));

        // Req 10.2 — a visible cell with no contributing jobs and no error renders nothing.
        Assert.Empty(cut.FindAll(".offcanvas"));
        Assert.Empty(cut.FindAll(".offcanvas-backdrop"));
    }

    [Fact]
    public void NullResult_Visible_DoesNotOpenDrawer()
    {
        using var ctx = NewContext(enableJobManagement: true, isReadOnly: false);

        var cut = ctx.RenderComponent<DrillDownDrawer>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.Result, (DrillDownResult)null));

        Assert.Empty(cut.FindAll(".offcanvas"));
    }

    [Fact]
    public void JobsPresent_ButNotVisible_DoesNotOpenDrawer()
    {
        using var ctx = NewContext(enableJobManagement: true, isReadOnly: false);

        var cut = ctx.RenderComponent<DrillDownDrawer>(p => p
            .Add(c => c.Visible, false)
            .Add(c => c.Result, ResultWith(Job())));

        // The parent has not requested the drawer open, so nothing renders.
        Assert.Empty(cut.FindAll(".offcanvas"));
    }

    // -- Req 10.7: load error opens the drawer and surfaces an error indication ------------------

    [Fact]
    public void LoadError_OpensDrawer_AndRendersErrorIndication()
    {
        using var ctx = NewContext(enableJobManagement: true, isReadOnly: false);

        var cut = ctx.RenderComponent<DrillDownDrawer>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.Result, new DrillDownResult(Array.Empty<DrillDownJob>(), "boom: query failed")));

        // Req 10.7 — the drawer opens for the error and renders an error alert with the message.
        Assert.NotEmpty(cut.FindAll(".offcanvas"));
        var alert = cut.Find(".alert-danger");
        Assert.Equal("alert", alert.GetAttribute("role"));
        Assert.Contains("Couldn't load the jobs for this cell.", cut.Markup);
        Assert.Contains("boom: query failed", cut.Markup);

        // The error indication replaces the job list — no edit/view actions are shown.
        Assert.DoesNotContain("View executions", cut.Markup);
        Assert.DoesNotContain("Edit schedule", cut.Markup);
    }
}
