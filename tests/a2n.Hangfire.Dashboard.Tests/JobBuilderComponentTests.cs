using System;
using System.Linq;
using System.Reflection;
using Bunit;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Shared;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.Services;
using RecurringEditorPage = a2n.Hangfire.Dashboard.Components.Pages.RecurringEditor;
using EnqueueJobPage = a2n.Hangfire.Dashboard.Components.Pages.EnqueueJob;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// bunit component tests for the Job Builder composite (<c>Components/Shared/JobBuilder.razor</c>)
/// and its route hosts (<c>Components/Pages/RecurringEditor.razor</c> and
/// <c>Components/Pages/EnqueueJob.razor</c>) — task 16.4.
///
/// Coverage:
///   • Read-only / recurring-admin / custom gating display (Req 4.1–4.5).
///   • Route hosting and navigation (Req 11.1, 11.2, 11.4, 11.5).
///   • Queue control states: editable+default, read-only+precedence, format-template verbatim
///     (Req 13.2, 13.3, 13.4).
///   • Enqueue-mode layout, queue default, success confirmation, failure retention
///     (Req 12.1, 12.2, 12.5, 12.6).
///
/// <para>
/// <b>Setup.</b> A real <see cref="HangfireMonitorService"/> backed by <see cref="InMemoryStorage"/>
/// is registered alongside the <see cref="DashboardUIOptions"/> instance and a
/// <see cref="JobMethodResolver"/>; bunit's <see cref="TestContext"/> supplies a fake
/// <see cref="NavigationManager"/>. The resolver's private discovery cache is pre-seeded by
/// reflection (the same technique <c>MethodPickerComponentTests</c> uses) so the registered-method
/// list is deterministic and we can control each descriptor's <see cref="QueueAttributeInfo"/>.
/// Successful-submit tests seed descriptors that point at real, uniquely-named fixture job types in
/// this test assembly so the service's own (assembly-based) resolution succeeds; failure tests seed
/// a descriptor whose type does not exist so resolution fails.
/// </para>
/// </summary>
public class JobBuilderComponentTests
{
    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Forces a resolver's <c>_registeredMethodsCache</c> to <paramref name="methods"/> so
    /// <c>GetRegisteredMethods()</c> returns exactly that list without a real assembly scan.
    /// </summary>
    private static void SeedCache(JobMethodResolver resolver, params JobMethodDescriptor[] methods)
    {
        var field = typeof(JobMethodResolver).GetField(
            "_registeredMethodsCache",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(resolver, (IReadOnlyList<JobMethodDescriptor>)methods);
    }

    private sealed record Harness(
        TestContext Ctx,
        JobMethodResolver Resolver,
        HangfireMonitorService Service,
        DashboardUIOptions Options);

    private static Harness NewContext(
        bool isReadOnly = false,
        bool recurringAdmin = true,
        bool customMethod = false,
        params JobMethodDescriptor[] registered)
    {
        var storage = new InMemoryStorage();
        var options = new DashboardUIOptions
        {
            IsReadOnly = isReadOnly,
            EnableJobManagement = recurringAdmin,
            AllowArbitraryMethodInvocation = customMethod,
        };
        var resolver = new JobMethodResolver();
        SeedCache(resolver, registered ?? Array.Empty<JobMethodDescriptor>());
        var svc = new HangfireMonitorService(storage, null, options, resolver);

        var ctx = new TestContext();
        ctx.Services.AddSingleton(resolver);
        ctx.Services.AddSingleton(svc);
        ctx.Services.AddSingleton(options);
        return new Harness(ctx, resolver, svc, options);
    }

    /// <summary>Describes a real fixture method into a <see cref="JobMethodDescriptor"/>.</summary>
    private static JobMethodDescriptor Describe(Type type, string method)
        => new JobMethodResolver().Describe(
            type.GetMethod(method) ?? throw new InvalidOperationException($"missing fixture {type.Name}.{method}"));

    /// <summary>A descriptor whose type does not exist, so service-side resolution fails.</summary>
    private static JobMethodDescriptor BogusDescriptor()
        => new JobMethodDescriptor(
            TypeFullName: "Jbc164.NoSuchType",
            MethodName: "Nope",
            DisplayLabel: "Unresolvable method",
            JobParameters: Array.Empty<JobParameterDescriptor>(),
            Queue: new QueueAttributeInfo(false, null, false));

    /// <summary>The single submit button (recurring/enqueue), located by its label text.</summary>
    private static AngleSharp.Dom.IElement SubmitButton(IRenderedFragment cut)
        => cut.FindAll("button").Single(b =>
            b.TextContent.Contains("recurring job", StringComparison.Ordinal)
            || b.TextContent.Contains("Enqueue job", StringComparison.Ordinal));

    private static void SelectRegistered(IRenderedFragment cut, int index)
    {
        // With Custom_Method invocation disabled (the test default) the Registered/Custom toggle is
        // not rendered and the picker is already in Registered mode, so the radio is absent. When the
        // toggle is present (custom enabled), click it first.
        var registeredRadio = cut.FindAll("#method-mode-registered");
        if (registeredRadio.Count > 0)
        {
            registeredRadio[0].Change(true);
        }

        // Open the searchable combobox and click the option at the requested index.
        cut.Find("#registered-method-filter").Focus();
        cut.FindAll(".hf-method-option")
            .Single(o => o.GetAttribute("data-index") == index.ToString())
            .Click();
    }

    // =======================================================================================
    // Gating display (Req 4.1–4.5)
    // =======================================================================================

    [Fact]
    public void ReadOnly_Shows_Banner_And_Disables_Submit()
    {
        var h = NewContext(isReadOnly: true);
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));

        // A persistent, visible read-only indication is shown (Req 4.2).
        Assert.Contains("read-only", cut.Markup, StringComparison.OrdinalIgnoreCase);

        // The mutating submit control is non-interactive (Req 4.1).
        Assert.True(SubmitButton(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void RecurringAdminDisabled_Shows_Banner_And_Disables_Submit()
    {
        var h = NewContext(recurringAdmin: false);
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));

        // A persistent job-management-disabled indication is shown (Req 4.3)...
        Assert.Contains("Job management is", cut.Markup);

        // ...and the create/update control is non-interactive (Req 4.3).
        Assert.True(SubmitButton(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void CustomMethodDisabled_Hides_Toggle_And_Shows_Registered_Selector_Directly()
    {
        var h = NewContext(customMethod: false,
            registered: Describe(typeof(Jbc164_PlainJob), nameof(Jbc164_PlainJob.RunNoArgs)));
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));

        // With custom invocation disabled the Registered/Custom toggle is omitted entirely and only
        // the Registered_Method selector is presented (Req 4.4, 6.1).
        Assert.Empty(cut.FindAll("#method-mode-custom"));
        Assert.Empty(cut.FindAll("#method-mode-registered"));
        Assert.NotNull(cut.Find("#registered-method-filter"));
    }

    [Fact]
    public void CustomMethodEnabled_Flows_To_MethodPicker_As_Enabled()
    {
        var h = NewContext(customMethod: true);
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));

        // CustomMethodEnabled=true makes the Custom option interactive (Req 4.5).
        Assert.False(cut.Find("#method-mode-custom").HasAttribute("disabled"));
        Assert.DoesNotContain("Arbitrary method invocation is disabled", cut.Markup);
    }

    // =======================================================================================
    // Route hosting and navigation (Req 11.1, 11.2, 11.4, 11.5)
    // =======================================================================================

    [Fact]
    public void RecurringEditor_Hosts_JobBuilder_Presenting_All_Recurring_Controls()
    {
        var h = NewContext(registered: Describe(typeof(Jbc164_PlainJob), nameof(Jbc164_PlainJob.RunNoArgs)));
        using var _ = h.Ctx;

        // The create route host renders JobBuilder in Recurring mode as the sole form (Req 11.1).
        var cut = h.Ctx.RenderComponent<RecurringEditorPage>();

        // JobBuilder presents an editable control for each recurring field (Req 11.2):
        Assert.NotNull(cut.Find("#job-builder-id"));            // job identifier
        Assert.NotNull(cut.Find("#registered-method-filter"));  // target method (MethodPicker)
        Assert.Contains("Parameters", cut.Markup);              // Argument_Values (ParameterBuilder)
        Assert.NotNull(cut.Find("#schedule-mode-builder"));     // Cron_Expression (ScheduleBuilder)
        Assert.NotNull(cut.Find("#job-builder-queue"));         // queue
        Assert.NotNull(cut.Find("#job-builder-tz"));            // time zone
    }

    [Fact]
    public void Recurring_SuccessfulSubmit_Navigates_To_Recurring_List()
    {
        var h = NewContext(registered: Describe(typeof(Jbc164_PlainJob), nameof(Jbc164_PlainJob.RunNoArgs)));
        using var _ = h.Ctx;

        var nav = h.Ctx.Services.GetRequiredService<NavigationManager>();

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));

        // Select the resolvable, parameter-less registered method (its descriptor emits a valid []).
        SelectRegistered(cut, 0);

        // Job identifier (Req 11.3).
        cut.Find("#job-builder-id").Input("jbc164-nav-job");

        // A valid cron via manual mode so the schedule state is emitted (Req 10.8).
        cut.Find("#schedule-mode-manual").Change(true);
        cut.Find("#cron-manual").Input("0 0 * * *");

        SubmitButton(cut).Click();

        // On success the operator is navigated to the recurring jobs list (Req 11.4).
        cut.WaitForState(() => nav.Uri.EndsWith("recurring", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.EndsWith("recurring", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Recurring_FailedSubmit_Stays_Shows_Error_And_Retains_Inputs()
    {
        var h = NewContext(registered: BogusDescriptor());
        using var _ = h.Ctx;

        var nav = h.Ctx.Services.GetRequiredService<NavigationManager>();

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));

        SelectRegistered(cut, 0);
        cut.Find("#job-builder-id").Input("jbc164-fail-job");
        cut.Find("#schedule-mode-manual").Change(true);
        cut.Find("#cron-manual").Input("0 0 * * *");

        SubmitButton(cut).Click();

        // The service rejects the unresolvable method; the form stays on the route, shows the error,
        // and retains the operator's entered values (Req 11.5).
        cut.WaitForState(() => cut.FindAll("div.alert-danger").Count > 0, TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("recurring", nav.Uri[nav.BaseUri.Length..], StringComparison.Ordinal);
        Assert.Equal("jbc164-fail-job", cut.Find("#job-builder-id").GetAttribute("value"));
    }

    // =======================================================================================
    // Job ID validation (Issue #11 — mixed-case / dotted identifiers)
    // =======================================================================================

    [Theory]
    [InlineData("IShopifyJob.ShopifyStockSyncFromSapAsync")] // mixed-case + dots, as AddOrUpdate<T> produces
    [InlineData("Nightly_Cleanup-2")]                        // mixed-case + underscore + dash
    [InlineData("nightly-cleanup")]                          // the previously-allowed lowercase form
    public void JobId_Accepts_MixedCase_And_Dotted_Identifiers(string id)
    {
        var h = NewContext();
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));

        cut.Find("#job-builder-id").Input(id);

        // A Hangfire-compatible id is accepted: no invalid styling and no inline validation message
        // (Issue #11). Queue defaults to "default" and the time zone is blank, so neither raises one.
        var input = cut.Find("#job-builder-id");
        Assert.DoesNotContain("is-invalid", input.GetAttribute("class") ?? string.Empty);
        Assert.Empty(cut.FindAll(".invalid-feedback"));
    }

    [Theory]
    [InlineData("bad id")]   // space
    [InlineData("bad/id")]   // slash
    [InlineData("bad:id")]   // colon
    public void JobId_Rejects_Disallowed_Characters(string id)
    {
        var h = NewContext();
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));

        cut.Find("#job-builder-id").Input(id);

        // Characters outside [A-Za-z0-9_.-] are still rejected with inline feedback.
        var input = cut.Find("#job-builder-id");
        Assert.Contains("is-invalid", input.GetAttribute("class") ?? string.Empty);
    }

    [Fact]
    public void JobId_Flags_Existing_Recurring_Id_As_Duplicate_On_Create()
    {
        var h = NewContext(registered: Describe(typeof(Jbc164_PlainJob), nameof(Jbc164_PlainJob.RunNoArgs)));
        using var _ = h.Ctx;

        // Seed an existing recurring job so its id is already taken.
        var seed = h.Service.CreateOrUpdateRecurringJob(new RecurringJobRequest(
            JobId: "jbc164-existing",
            TypeName: typeof(Jbc164_PlainJob).FullName,
            MethodName: nameof(Jbc164_PlainJob.RunNoArgs),
            ParameterJson: "[]",
            Cron: "0 0 * * *",
            Queue: "default",
            TimeZoneId: null,
            IsCustomMethod: false));
        Assert.True(seed.Success, seed.Error);

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));

        // Entering the existing id on a create form flags a duplicate inline — create must not
        // silently overwrite an existing recurring job.
        cut.Find("#job-builder-id").Input("jbc164-existing");

        var input = cut.Find("#job-builder-id");
        Assert.Contains("is-invalid", input.GetAttribute("class") ?? string.Empty);
        Assert.Contains("already exists", cut.Markup);
    }

    // =======================================================================================
    // Queue control states (Req 13.2, 13.3, 13.4)
    // =======================================================================================

    [Fact]
    public void Queue_Is_Editable_With_Default_When_No_QueueAttribute()
    {
        var h = NewContext(registered: Describe(typeof(Jbc164_PlainJob), nameof(Jbc164_PlainJob.RunNoArgs)));
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));
        SelectRegistered(cut, 0);

        var queue = cut.Find("#job-builder-queue");

        // No QueueAttribute → an editable queue with suggestions and a "default" default (Req 13.2).
        Assert.False(queue.HasAttribute("readonly"));
        Assert.Equal("job-builder-queue-list", queue.GetAttribute("list"));
        Assert.Equal(EffectiveQueue.DefaultQueue, queue.GetAttribute("value"));
        Assert.Contains("Defaults to", cut.Markup);
    }

    [Fact]
    public void Queue_Is_ReadOnly_With_Precedence_Notice_When_QueueAttribute_Applies()
    {
        var h = NewContext(registered: Describe(typeof(Jbc164_FixedQueueJob), nameof(Jbc164_FixedQueueJob.Run)));
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));
        SelectRegistered(cut, 0);

        var queue = cut.Find("#job-builder-queue");

        // A QueueAttribute applies → the queue is read-only, pre-filled with the attribute value, and
        // a precedence notice explains it overrides the configured queue (Req 13.3).
        Assert.True(queue.HasAttribute("readonly"));
        Assert.Equal("jbc164-critical", queue.GetAttribute("value"));
        Assert.Contains("takes precedence", cut.Markup);
    }

    [Fact]
    public void Queue_Format_Template_Value_Is_Shown_Verbatim()
    {
        var h = NewContext(registered: Describe(typeof(Jbc164_TemplateQueueJob), nameof(Jbc164_TemplateQueueJob.Run)));
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Recurring));
        SelectRegistered(cut, 0);

        var queue = cut.Find("#job-builder-queue");

        // A format-template QueueAttribute is read-only and its value is shown verbatim (Req 13.4).
        Assert.True(queue.HasAttribute("readonly"));
        Assert.Equal("{0}", queue.GetAttribute("value"));
        Assert.Contains("format template", cut.Markup);
        Assert.Contains("{0}", cut.Markup, StringComparison.Ordinal);
    }

    // =======================================================================================
    // Enqueue-mode layout, queue default, confirmation, failure (Req 12.1, 12.2, 12.5, 12.6)
    // =======================================================================================

    [Fact]
    public void Enqueue_Mode_Hides_Schedule_And_Defaults_Queue_To_Default()
    {
        var h = NewContext();
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Enqueue));

        // The Schedule_Builder is hidden entirely in Enqueue mode (Req 12.1).
        Assert.Empty(cut.FindAll("#schedule-mode-builder"));
        Assert.Empty(cut.FindAll("#cron-manual"));

        // A queue selector over the available queues defaults to "default" (Req 12.2).
        var queue = cut.Find("#job-builder-queue");
        Assert.Equal("select", queue.TagName, ignoreCase: true);
        Assert.Equal(EffectiveQueue.DefaultQueue, queue.GetAttribute("value"));
    }

    [Fact]
    public void EnqueueHostPage_Renders_JobBuilder_In_Enqueue_Mode()
    {
        var h = NewContext();
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<EnqueueJobPage>();

        // The enqueue route hosts JobBuilder in Enqueue mode: no schedule, a queue selector (Req 12.1, 12.2).
        Assert.Empty(cut.FindAll("#schedule-mode-builder"));
        Assert.Equal("select", cut.Find("#job-builder-queue").TagName, ignoreCase: true);
    }

    [Fact]
    public void Enqueue_SuccessfulEnqueue_Shows_JobId_Confirmation()
    {
        var h = NewContext(registered: Describe(typeof(Jbc164_PlainJob), nameof(Jbc164_PlainJob.RunNoArgs)));
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Enqueue));
        SelectRegistered(cut, 0);

        SubmitButton(cut).Click();

        // A successful enqueue shows a confirmation carrying the enqueued job id (Req 12.6).
        cut.WaitForState(
            () => cut.Markup.Contains("Job enqueued successfully", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        Assert.Contains("Job id:", cut.Markup);
    }

    [Fact]
    public void Enqueue_FailedEnqueue_Shows_Error_And_No_Confirmation()
    {
        var h = NewContext(registered: BogusDescriptor());
        using var _ = h.Ctx;

        var cut = h.Ctx.RenderComponent<JobBuilder>(p => p.Add(c => c.Mode, JobBuilderMode.Enqueue));
        SelectRegistered(cut, 0);

        SubmitButton(cut).Click();

        // The unresolvable method makes the service reject the enqueue; the form stays put and shows
        // the error without any success confirmation (Req 12.5).
        cut.WaitForState(() => cut.FindAll("div.alert-danger").Count > 0, TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("Job enqueued successfully", cut.Markup);
    }
}

// --- Fixtures -------------------------------------------------------------------------------
//
// Uniquely "Jbc164_"-prefixed fixture job types resolvable by the JobMethodResolver against the
// loaded test assembly. Methods are only reflected over / shaped, never invoked. The queue-decorated
// types carry class-level QueueAttributes so their descriptors report the queue state under test.

/// <summary>A plain job with a parameter-less method (no QueueAttribute) for success-path tests.</summary>
public sealed class Jbc164_PlainJob
{
    public void RunNoArgs() { }
}

/// <summary>Class carries a fixed (non-template) QueueAttribute (Req 13.3).</summary>
[Queue("jbc164-critical")]
public sealed class Jbc164_FixedQueueJob
{
    public void Run() { }
}

/// <summary>Class carries a format-template QueueAttribute whose value must be shown verbatim (Req 13.4).</summary>
[Queue("{0}")]
public sealed class Jbc164_TemplateQueueJob
{
    public void Run() { }
}
