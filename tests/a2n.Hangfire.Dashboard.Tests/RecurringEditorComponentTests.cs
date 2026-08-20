using System;
using System.Linq;
using Bunit;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;
using RecurringEditor = a2n.Hangfire.Dashboard.Components.Pages.RecurringEditor;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// bunit component tests for <c>Components/Pages/RecurringEditor.razor</c>, now a thin host for the
/// shared <c>JobBuilder</c> running in Recurring mode (Req 11.1).
///
/// These tests validate the Phase-1 recurring requirements against the JobBuilder-hosted UI and the
/// now-working edit pre-fill (the Method Picker pre-selects the existing job's method on edit, which
/// renders the Parameter Builder and makes the stored-args pre-fill effective):
///
///   • <b>Parameter_JSON input capacity (Req 2.1)</b> — once a method is selected and the Parameter
///     Builder is switched to JSON mode, its editable JSON textarea has no maxlength below 10,000
///     and a 10,000+ character value round-trips into the bound model and back to the input.
///
///   • <b>Edit field population (Req 3.1, 3.3)</b> — when editing an existing recurring job (seeded
///     into real <see cref="InMemoryStorage"/> via the service), the method is pre-selected (the
///     Parameter Builder renders its form), the Parameter_JSON pre-fill matches the stored
///     <c>Args</c> positionally, and the job id / cron / queue / time-zone controls reflect the
///     stored definition.
///
///   • <b>Argument-less job (Req 3.2)</b> — a method with zero Job_Parameters yields an empty
///     Parameter_JSON array (<c>[]</c>).
///
/// <para>
/// <b>Setup.</b> The host page <c>@inject</c>s <see cref="HangfireMonitorService"/>; the hosted
/// JobBuilder additionally injects <see cref="JobMethodResolver"/>, <c>NavigationManager</c>, and
/// <see cref="DashboardUIOptions"/>. We register a real service backed by Hangfire.InMemory storage
/// (with <c>EnableJobManagement = true</c>) plus the <em>same</em> <see cref="JobMethodResolver"/>
/// instance passed to the service so discovery and resolution are consistent. The fixture job type
/// lives in this test assembly, so the resolver resolves it via the loaded-assembly (custom) path;
/// the Method Picker therefore pre-selects it on edit even though it is not attribute-discovered.
/// bunit's <see cref="TestContext"/> supplies the fake NavigationManager.
/// </para>
///
/// <para>
/// <b>Not feasibly testable here (documented):</b> Req 3.4 (absent fields left empty) and Req 3.5
/// (uninterpretable stored Args) are not reachable through a realistic InMemory round trip —
/// Hangfire always materializes a cron / queue / time zone and round-trips Args through its own
/// JSON serializer — so those branches are covered by the service-layer tests rather than here.
/// </para>
/// </summary>
public class RecurringEditorComponentTests
{
    // --- Helpers ---------------------------------------------------------------------------

    private static (TestContext ctx, HangfireMonitorService svc, DashboardUIOptions opts) NewContext()
    {
        var storage = new InMemoryStorage();
        var options = new DashboardUIOptions
        {
            IsReadOnly = false,
            EnableJobManagement = true,
        };

        // Reuse a single resolver instance for both the service and the component DI so resolution
        // (service side) and pre-selection (Method Picker side) are consistent.
        var resolver = new JobMethodResolver();
        var svc = new HangfireMonitorService(storage, null, options, resolver);

        var ctx = new TestContext();
        ctx.Services.AddSingleton(resolver);
        ctx.Services.AddSingleton(svc);
        ctx.Services.AddSingleton(options);
        return (ctx, svc, options);
    }

    private static RecurringJobRequest Request(
        string jobId,
        string method,
        string parameterJson,
        string cron = "0 0 * * *",
        string queue = "default",
        string timeZoneId = null) =>
        new RecurringJobRequest(
            JobId: jobId,
            TypeName: typeof(RecurringEditorFixtureJob).FullName,
            MethodName: method,
            ParameterJson: parameterJson,
            Cron: cron,
            Queue: queue,
            TimeZoneId: timeZoneId,
            IsCustomMethod: false);

    /// <summary>Locates the Parameter Builder's "JSON" mode toggle button.</summary>
    private static AngleSharp.Dom.IElement JsonModeButton(IRenderedFragment cut)
        => cut.FindAll("button").Single(b => b.TextContent.Trim() == "JSON");

    /// <summary>The Parameter Builder's editable JSON textarea (only present in JSON mode).</summary>
    private static AngleSharp.Dom.IElement ParameterJsonTextarea(IRenderedFragment cut)
        => cut.FindAll("textarea.form-control").Single();

    // --- Req 2.1 — Parameter_JSON input capacity (>= 10,000 chars) -------------------------

    [Fact]
    public void ParameterJson_JsonTextarea_Has_No_MaxLength_Below_10000()
    {
        var (ctx, svc, _) = NewContext();
        using var _ctx = ctx;

        // Seed (and edit) a job so its method is pre-selected and the Parameter Builder renders.
        var seed = svc.CreateOrUpdateRecurringJob(Request(
            jobId: "recurring-editor-capacity",
            method: nameof(RecurringEditorFixtureJob.DoWork),
            parameterJson: "[\"hello\", 7]"));
        Assert.True(seed.Success, seed.Error);

        var cut = ctx.RenderComponent<RecurringEditor>(p => p
            .Add(c => c.JobId, "recurring-editor-capacity"));

        // Switch the Parameter Builder to JSON mode so its editable textarea is rendered.
        JsonModeButton(cut).Click();

        var textarea = ParameterJsonTextarea(cut);

        // The JSON textarea imposes no maxlength, so its capacity is effectively unbounded; if a
        // maxlength were ever added it must remain >= 10,000 to satisfy Req 2.1.
        var maxLength = textarea.GetAttribute("maxlength");
        Assert.True(
            maxLength is null || (int.TryParse(maxLength, out var max) && max >= 10000),
            $"Parameter_JSON textarea must accept >= 10,000 characters; found maxlength='{maxLength}'.");
    }

    [Fact]
    public void ParameterJson_JsonTextarea_Retains_A_10000_Plus_Char_Value()
    {
        var (ctx, svc, _) = NewContext();
        using var _ctx = ctx;

        var seed = svc.CreateOrUpdateRecurringJob(Request(
            jobId: "recurring-editor-capacity-retain",
            method: nameof(RecurringEditorFixtureJob.DoWork),
            parameterJson: "[\"hello\", 7]"));
        Assert.True(seed.Success, seed.Error);

        var cut = ctx.RenderComponent<RecurringEditor>(p => p
            .Add(c => c.JobId, "recurring-editor-capacity-retain"));

        JsonModeButton(cut).Click();

        // A valid JSON array whose single string element pushes the whole value past 10,000 chars.
        var longElement = new string('a', 10_050);
        var longJson = "[\"" + longElement + "\"]";
        Assert.True(longJson.Length > 10_000);

        // Set the bound value and confirm it round-trips into the model and back to the input.
        ParameterJsonTextarea(cut).Change(longJson);

        var retained = ParameterJsonTextarea(cut).GetAttribute("value");
        Assert.Equal(longJson, retained);
        Assert.True(retained.Length > 10_000);
    }

    // --- Req 3.3 / 3.1 — edit pre-selects the method and populates fields/arguments ---------

    [Fact]
    public void Edit_Populates_JobId_Cron_Queue_TimeZone_From_Existing_Definition()
    {
        var (ctx, svc, _) = NewContext();
        using var _ctx = ctx;

        // Pick a concrete, resolvable, non-UTC system time zone so the round trip is deterministic.
        var tz = TimeZoneInfo.GetSystemTimeZones().First(z => z.Id != "UTC");

        var seed = svc.CreateOrUpdateRecurringJob(Request(
            jobId: "recurring-editor-populate",
            method: nameof(RecurringEditorFixtureJob.DoWork),
            parameterJson: "[\"hello\", 7]",
            cron: "0 0 * * *",
            queue: "alpha",
            timeZoneId: tz.Id));
        Assert.True(seed.Success, seed.Error);

        var cut = ctx.RenderComponent<RecurringEditor>(p => p
            .Add(c => c.JobId, "recurring-editor-populate"));

        // Job ID (bound, disabled in edit) reflects the existing id (Req 3.3).
        Assert.Equal("recurring-editor-populate", cut.Find("#job-builder-id").GetAttribute("value"));

        // The method is pre-selected: the Parameter Builder renders its form (the parameter inputs
        // for DoWork(string label, int count)) rather than the "select a method" placeholder (Req 3.3).
        Assert.DoesNotContain("Select a method to enter its parameters", cut.Markup);

        // Cron populated from the stored definition; ScheduleBuilder shows it in Manual mode (Req 3.3).
        Assert.Equal("0 0 * * *", cut.Find("#cron-manual").GetAttribute("value"));

        // Queue populated from the stored definition (Req 3.3).
        Assert.Equal("alpha", cut.Find("#job-builder-queue").GetAttribute("value"));

        // Time zone populated from the stored definition (Req 3.3).
        Assert.Equal(tz.Id, cut.Find("#job-builder-tz").GetAttribute("value"));
    }

    [Fact]
    public void Edit_Populates_ParameterJson_Positionally_From_Stored_Args()
    {
        var (ctx, svc, _) = NewContext();
        using var _ctx = ctx;

        var seed = svc.CreateOrUpdateRecurringJob(Request(
            jobId: "recurring-editor-args",
            method: nameof(RecurringEditorFixtureJob.DoWork),
            parameterJson: "[\"hello\", 7]"));
        Assert.True(seed.Success, seed.Error);

        var cut = ctx.RenderComponent<RecurringEditor>(p => p
            .Add(c => c.JobId, "recurring-editor-args"));

        // Switch to JSON mode to read the canonical Parameter_JSON the form pre-filled from Args.
        JsonModeButton(cut).Click();

        // The Parameter_JSON is populated positionally one-to-one from the stored Args (Req 3.1):
        // two stored values -> a two-element JSON array.
        Assert.Equal("[\"hello\",7]", ParameterJsonTextarea(cut).GetAttribute("value"));
    }

    // --- Req 3.2 — argument-less job shows an empty Parameter_JSON array --------------------

    [Fact]
    public void Edit_With_No_Stored_Args_Shows_Empty_Json_Array()
    {
        var (ctx, svc, _) = NewContext();
        using var _ctx = ctx;

        // Seed a job whose target method has zero Job_Parameters, so the stored Args are empty.
        var seed = svc.CreateOrUpdateRecurringJob(Request(
            jobId: "recurring-editor-noargs",
            method: nameof(RecurringEditorFixtureJob.DoNothing),
            parameterJson: "[]"));
        Assert.True(seed.Success, seed.Error);

        var cut = ctx.RenderComponent<RecurringEditor>(p => p
            .Add(c => c.JobId, "recurring-editor-noargs"));

        // The method is pre-selected; a zero-parameter method shows the "takes no parameters" form.
        Assert.DoesNotContain("Select a method to enter its parameters", cut.Markup);

        // Switch to JSON mode: an absent/empty Args yields an empty Parameter_JSON array "[]" (Req 3.2).
        JsonModeButton(cut).Click();
        Assert.Equal("[]", ParameterJsonTextarea(cut).GetAttribute("value"));
    }

    // --- Issue #11 — editing a never-fire job and saving without touching the schedule -------

    [Fact]
    public void Edit_Submit_Without_Touching_Schedule_Succeeds_For_NeverFire_Cron()
    {
        var (ctx, svc, opts) = NewContext();
        using var _ctx = ctx;

        // The fixture method is discovered via the loaded-assembly (custom) path, so allow custom
        // invocation — this test isolates the schedule-state behavior, not the custom-method gate.
        opts.AllowArbitraryMethodInvocation = true;

        // Seed a job registered with an intentionally unreachable ("never-fire") cron — the pattern
        // operators use to keep a job manual-trigger-only (Issue #11).
        var seed = svc.CreateOrUpdateRecurringJob(Request(
            jobId: "recurring-editor-neverfire",
            method: nameof(RecurringEditorFixtureJob.DoNothing),
            parameterJson: "[]",
            cron: "0 0 31 2 *"));
        Assert.True(seed.Success, seed.Error);

        var nav = ctx.Services.GetRequiredService<NavigationManager>();

        var cut = ctx.RenderComponent<RecurringEditor>(p => p
            .Add(c => c.JobId, "recurring-editor-neverfire"));

        // The schedule shows the never-fire cron, pre-filled in Manual mode.
        Assert.Equal("0 0 31 2 *", cut.Find("#cron-manual").GetAttribute("value"));

        // Click "Update recurring job" WITHOUT touching the schedule. Before the fix the schedule
        // state was never emitted on load, so the submit failed with "A valid cron expression is
        // required"; now the loaded cron is emitted on init and the edit succeeds (Issue #11).
        cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Update recurring job", StringComparison.Ordinal))
            .Click();

        // Success navigates back to the recurring jobs list. If a submission error is shown instead,
        // surface it to make the failure actionable.
        try
        {
            cut.WaitForState(() => nav.Uri.EndsWith("recurring", StringComparison.Ordinal), TestTimeouts.RenderWait);
        }
        catch (Exception)
        {
            var error = cut.FindAll("div.alert-danger");
            Assert.Fail(error.Count > 0
                ? $"Submit did not navigate; error shown: {error[0].TextContent.Trim()}"
                : "Submit did not navigate and no error alert was rendered.");
        }
        Assert.EndsWith("recurring", nav.Uri, StringComparison.Ordinal);
    }
}

/// <summary>
/// Uniquely-named public fixture whose methods are valid Job_Parameter targets resolvable by the
/// <see cref="JobMethodResolver"/> against the loaded test assembly. The methods are never invoked —
/// the resolver only reflects over them and the converter shapes their <c>Args</c>.
/// </summary>
public sealed class RecurringEditorFixtureJob
{
    public void DoWork(string label, int count) { }

    public void DoNothing() { }
}
