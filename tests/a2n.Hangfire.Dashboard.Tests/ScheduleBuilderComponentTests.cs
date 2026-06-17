using System;
using System.Linq;
using Bunit;
using Xunit;
using ScheduleBuilder = a2n.Hangfire.Dashboard.Components.Shared.ScheduleBuilder;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// bUnit component tests for the Schedule Builder (Components/Shared/ScheduleBuilder.razor).
///
/// Covers:
///   • Req 10.1 — presents a choice between a Cron Builder and a manual cron input.
///   • Req 10.3 — shows a human-readable description for a (manual) cron expression.
///   • Req 10.4 — the manual cron input is limited to a maximum of 100 characters.
///   • Req 10.8 — the current cron expression and validity are emitted via OnScheduleChanged.
///   • Req 10.9 (display) / Req 10.7 — an invalid cron shows an error and no next-occurrence
///     preview, while a valid cron shows a next-occurrence preview.
///
/// ScheduleBuilder has no service dependencies, so a bare TestContext is sufficient.
/// </summary>
public class ScheduleBuilderComponentTests
{
    // -- Req 10.1: Cron Builder vs manual-input choice ------------------------------------------

    [Fact]
    public void Renders_BothBuilderAndManualModeChoices()
    {
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<ScheduleBuilder>();

        // Both mutually-exclusive mode options are present (Req 10.1).
        Assert.NotNull(cut.Find("#schedule-mode-builder"));
        Assert.NotNull(cut.Find("#schedule-mode-manual"));

        // Default mode is the Cron Builder, which renders the per-field selectors.
        Assert.NotNull(cut.Find("#cron-field-minute"));
        Assert.NotNull(cut.Find("#cron-field-dow"));
    }

    [Fact]
    public void SwitchingToManual_RendersManualCronInput()
    {
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<ScheduleBuilder>();

        // No manual input while in builder mode.
        Assert.Empty(cut.FindAll("#cron-manual"));

        // Switch to manual mode (Req 10.1).
        cut.Find("#schedule-mode-manual").Change(true);

        Assert.NotNull(cut.Find("#cron-manual"));
    }

    // -- Req 10.4: manual input max-length of 100 -----------------------------------------------

    [Fact]
    public void ManualCronInput_HasMaxLength100()
    {
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<ScheduleBuilder>();
        cut.Find("#schedule-mode-manual").Change(true);

        var input = cut.Find("#cron-manual");

        Assert.Equal("100", input.GetAttribute("maxlength"));
    }

    // -- Req 10.3: human-readable description for a cron ----------------------------------------

    [Fact]
    public void ManualCron_RendersHumanReadableDescription()
    {
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<ScheduleBuilder>();
        cut.Find("#schedule-mode-manual").Change(true);

        // Enter a well-known daily cron (Req 10.3).
        cut.Find("#cron-manual").Input("0 0 * * *");

        // CronDescriber.Describe("0 0 * * *") => "Every day at 00:00"; assert a description appears.
        Assert.Contains("Every day at", cut.Markup);
    }

    // -- Req 10.9 (display) / Req 10.7: submit-gating display ------------------------------------

    [Fact]
    public void ValidCron_ShowsNextOccurrencePreview_AndNoError()
    {
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<ScheduleBuilder>();
        cut.Find("#schedule-mode-manual").Change(true);

        cut.Find("#cron-manual").Input("0 0 * * *");

        // A valid expression shows a next-occurrence preview (Req 10.6/10.9 display) ...
        Assert.Contains("Next occurrence:", cut.Markup);
        // ... and no invalid-cron error.
        Assert.Empty(cut.FindAll(".text-danger"));
    }

    [Fact]
    public void InvalidCron_ShowsError_AndNoNextOccurrencePreview()
    {
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<ScheduleBuilder>();
        cut.Find("#schedule-mode-manual").Change(true);

        cut.Find("#cron-manual").Input("this is not a cron");

        // An unparseable expression shows an error (Req 10.7) ...
        var error = cut.Find(".text-danger");
        Assert.Contains("invalid", error.TextContent, StringComparison.OrdinalIgnoreCase);

        // ... and no next-occurrence preview.
        Assert.DoesNotContain("Next occurrence:", cut.Markup);
    }

    // -- Req 10.8: validity reported through OnScheduleChanged -----------------------------------

    [Fact]
    public void EmitsScheduleState_WithCorrectValidity_OnManualInput()
    {
        using var ctx = new Bunit.TestContext();

        ScheduleBuilder.ScheduleState captured = null;
        var cut = ctx.RenderComponent<ScheduleBuilder>(parameters => parameters
            .Add(p => p.OnScheduleChanged, (ScheduleBuilder.ScheduleState s) => captured = s));

        cut.Find("#schedule-mode-manual").Change(true);

        // Valid cron => emitted state reports IsValid = true and carries the expression (Req 10.8).
        cut.Find("#cron-manual").Input("0 0 * * *");
        Assert.NotNull(captured);
        Assert.Equal("0 0 * * *", captured.Cron);
        Assert.True(captured.IsValid);

        // Invalid cron => emitted state reports IsValid = false.
        cut.Find("#cron-manual").Input("not a cron");
        Assert.NotNull(captured);
        Assert.Equal("not a cron", captured.Cron);
        Assert.False(captured.IsValid);
    }

    // -- Issue #11: initial schedule state is emitted on load (no interaction required) ----------

    [Fact]
    public void EmitsInitialScheduleState_OnLoad_WithoutInteraction()
    {
        using var ctx = new Bunit.TestContext();

        ScheduleBuilder.ScheduleState captured = null;
        var cut = ctx.RenderComponent<ScheduleBuilder>(parameters => parameters
            .Add(p => p.OnScheduleChanged, (ScheduleBuilder.ScheduleState s) => captured = s));

        // The builder emits its initial (default) schedule state on load so the parent has a cron and
        // its validity immediately — without the operator touching a schedule control (Issue #11).
        Assert.NotNull(captured);
        Assert.False(string.IsNullOrWhiteSpace(captured.Cron));
        Assert.True(captured.IsValid);
    }

    [Fact]
    public void EmitsInitialScheduleState_ForNeverFireCron_AsValid()
    {
        using var ctx = new Bunit.TestContext();

        ScheduleBuilder.ScheduleState captured = null;
        var cut = ctx.RenderComponent<ScheduleBuilder>(parameters => parameters
            .Add(p => p.InitialCron, "0 0 31 2 *")
            .Add(p => p.OnScheduleChanged, (ScheduleBuilder.ScheduleState s) => captured = s));

        // An intentionally unreachable ("never-fire") expression is emitted on load and reported
        // valid (Issue #11): Cronos/Hangfire parse it successfully even though it never occurs.
        Assert.NotNull(captured);
        Assert.Equal("0 0 31 2 *", captured.Cron);
        Assert.True(captured.IsValid);

        // The UI explains there is no upcoming occurrence rather than flagging it as an error.
        Assert.Contains("no upcoming occurrence", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("The cron expression is invalid", cut.Markup);
    }
}
