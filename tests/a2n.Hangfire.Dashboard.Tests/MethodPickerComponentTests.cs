using System.Reflection;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Shared;
using a2n.Hangfire.Dashboard.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// bunit component tests for <c>MethodPicker.razor</c> (task 8.2).
///
/// These tests cover the structural and interaction behavior of the Method Picker:
///   • the Registered vs Custom choice with nothing selected by default (Req 6.1);
///   • the registered list of Display_Labels (Req 6.2);
///   • the empty-state when no registered methods exist (Req 6.3);
///   • selection wiring emitting the descriptor via OnMethodSelected (Req 6.4/6.5);
///   • custom-method success emitting the resolved descriptor (Req 6.6);
///   • custom-input error retention on failed validation (Req 6.7/7.2);
///   • the Custom option disabled gating when CustomMethodEnabled is false (Req 4.4/4.5).
///
/// <see cref="JobMethodResolver"/> is a concrete sealed class with no interface, so we register a
/// real instance via DI. To make the *registered list* deterministic (independent of whatever the
/// resolver discovers in the test AppDomain) we pre-seed its private discovery cache through
/// reflection. Custom-method validation runs against the real loaded assemblies, so those tests use
/// a known type in this test assembly (success) and a bogus type name (failure).
/// </summary>
public class MethodPickerComponentTests
{
    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="JobMethodResolver"/> whose registered-method discovery cache is forced
    /// to <paramref name="methods"/>, so <c>GetRegisteredMethods()</c> returns exactly that list
    /// without performing a real assembly scan.
    /// </summary>
    private static JobMethodResolver ResolverWithRegistered(params JobMethodDescriptor[] methods)
    {
        var resolver = new JobMethodResolver();
        var field = typeof(JobMethodResolver).GetField(
            "_registeredMethodsCache",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(resolver, (IReadOnlyList<JobMethodDescriptor>)methods);
        return resolver;
    }

    private static JobMethodDescriptor Descriptor(string typeName, string method, string label)
        => new(
            TypeFullName: typeName,
            MethodName: method,
            DisplayLabel: label,
            JobParameters: Array.Empty<JobParameterDescriptor>(),
            Queue: new QueueAttributeInfo(false, null, false));

    private static TestContext NewContext(JobMethodResolver resolver)
    {
        var ctx = new TestContext();
        ctx.Services.AddSingleton(resolver);
        return ctx;
    }

    // --- 6.1 — nothing selected by default -------------------------------------------------

    [Fact]
    public void Presents_RegisteredAndCustom_Choice_With_Nothing_Selected_By_Default()
    {
        using var ctx = NewContext(ResolverWithRegistered());

        var cut = ctx.RenderComponent<MethodPicker>(p => p
            .Add(c => c.CustomMethodEnabled, true));

        var registered = cut.Find("#method-mode-registered");
        var custom = cut.Find("#method-mode-custom");

        // Both choices are present (Req 6.1).
        Assert.Equal("radio", registered.GetAttribute("type"));
        Assert.Equal("radio", custom.GetAttribute("type"));

        // Neither is checked by default (Req 6.1).
        Assert.False(registered.HasAttribute("checked"));
        Assert.False(custom.HasAttribute("checked"));

        // With no mode chosen, neither the registered list nor the custom inputs are rendered.
        Assert.Empty(cut.FindAll("#registered-method-select"));
        Assert.Empty(cut.FindAll("#custom-type-name"));
    }

    // --- 4.4 / 4.5 — custom option gating --------------------------------------------------

    [Fact]
    public void When_CustomMethod_Disabled_Toggle_Is_Hidden_And_Registered_Selector_Shown_Directly()
    {
        using var ctx = NewContext(ResolverWithRegistered(
            Descriptor("MyApp.Jobs.OrderJobs", "ProcessOrder", "Process order")));

        var cut = ctx.RenderComponent<MethodPicker>(p => p
            .Add(c => c.CustomMethodEnabled, false));

        // The Registered/Custom toggle is not presented at all when custom is disabled (Req 4.4, 6.1).
        Assert.Empty(cut.FindAll("#method-mode-custom"));
        Assert.Empty(cut.FindAll("#method-mode-registered"));

        // The Registered_Method selector is shown directly so the operator can pick a method (Req 4.4).
        Assert.NotNull(cut.Find("#registered-method-select"));
    }

    [Fact]
    public void Custom_Option_Is_Enabled_When_CustomMethodEnabled_Is_True()
    {
        using var ctx = NewContext(ResolverWithRegistered());

        var cut = ctx.RenderComponent<MethodPicker>(p => p
            .Add(c => c.CustomMethodEnabled, true));

        var custom = cut.Find("#method-mode-custom");

        // The Custom radio is interactive (Req 4.5).
        Assert.False(custom.HasAttribute("disabled"));
        Assert.DoesNotContain("Custom method invocation is disabled", cut.Markup);
    }

    // --- 6.2 — registered list of Display_Labels -------------------------------------------

    [Fact]
    public void Registered_Mode_Lists_Discovered_Methods_By_DisplayLabel()
    {
        var methods = new[]
        {
            Descriptor("MyApp.Jobs.OrderJobs", "ProcessOrder", "Process order"),
            Descriptor("MyApp.Jobs.MailJobs", "SendWelcome", "Send welcome email"),
        };
        using var ctx = NewContext(ResolverWithRegistered(methods));

        var cut = ctx.RenderComponent<MethodPicker>(p => p
            .Add(c => c.CustomMethodEnabled, true));

        // Choose the Registered mode.
        cut.Find("#method-mode-registered").Change(true);

        var select = cut.Find("#registered-method-select");
        var optionLabels = select.QuerySelectorAll("option")
            .Select(o => o.TextContent.Trim())
            .ToList();

        // The placeholder plus one entry per discovered method, each by its Display_Label (Req 6.2).
        Assert.Contains("Process order", optionLabels);
        Assert.Contains("Send welcome email", optionLabels);
    }

    // --- 6.3 — empty-state -----------------------------------------------------------------

    [Fact]
    public void Registered_Mode_Shows_Empty_State_When_No_Methods_Available()
    {
        using var ctx = NewContext(ResolverWithRegistered());

        var cut = ctx.RenderComponent<MethodPicker>(p => p
            .Add(c => c.CustomMethodEnabled, true));

        cut.Find("#method-mode-registered").Change(true);

        // No selectable list is rendered (Req 6.3)...
        Assert.Empty(cut.FindAll("#registered-method-select"));

        // ...and an empty-state indication is presented (Req 6.3).
        Assert.Contains("No registered methods are available", cut.Markup);
    }

    // --- 6.4 / 6.5 — selection wiring emits the descriptor ---------------------------------

    [Fact]
    public void Selecting_A_Registered_Method_Emits_Its_Descriptor()
    {
        var methods = new[]
        {
            Descriptor("MyApp.Jobs.OrderJobs", "ProcessOrder", "Process order"),
            Descriptor("MyApp.Jobs.MailJobs", "SendWelcome", "Send welcome email"),
        };
        using var ctx = NewContext(ResolverWithRegistered(methods));

        JobMethodDescriptor emitted = null;
        var cut = ctx.RenderComponent<MethodPicker>(p => p
            .Add(c => c.CustomMethodEnabled, true)
            .Add(c => c.OnMethodSelected, d => emitted = d));

        cut.Find("#method-mode-registered").Change(true);

        // Select the second method by its index value (Req 6.4).
        cut.Find("#registered-method-select").Change("1");

        Assert.NotNull(emitted);
        Assert.Equal("MyApp.Jobs.MailJobs", emitted.TypeFullName);
        Assert.Equal("SendWelcome", emitted.MethodName);
    }

    // --- 6.6 — custom-method success emits the resolved descriptor -------------------------

    [Fact]
    public void Custom_Method_Validation_Success_Emits_Resolved_Descriptor()
    {
        using var ctx = NewContext(ResolverWithRegistered());

        JobMethodDescriptor emitted = null;
        var cut = ctx.RenderComponent<MethodPicker>(p => p
            .Add(c => c.CustomMethodEnabled, true)
            .Add(c => c.OnMethodSelected, d => emitted = d));

        cut.Find("#method-mode-custom").Change(true);

        // A real, uniquely-named public method in this test assembly resolves successfully (Req 6.6).
        cut.Find("#custom-type-name").Input(typeof(MethodPickerSampleJobs).FullName);
        cut.Find("#custom-method-name").Input(nameof(MethodPickerSampleJobs.RunSolo));
        cut.Find("button").Click();

        Assert.NotNull(emitted);
        Assert.Equal(typeof(MethodPickerSampleJobs).FullName, emitted.TypeFullName);
        Assert.Equal(nameof(MethodPickerSampleJobs.RunSolo), emitted.MethodName);
        Assert.Contains("validated and selected", cut.Markup);
    }

    // --- 6.7 / 7.2 — custom-input error retention on failed validation ---------------------

    [Fact]
    public void Custom_Method_Validation_Failure_Shows_Error_And_Retains_Inputs()
    {
        using var ctx = NewContext(ResolverWithRegistered());

        JobMethodDescriptor emitted = null;
        var cut = ctx.RenderComponent<MethodPicker>(p => p
            .Add(c => c.CustomMethodEnabled, true)
            .Add(c => c.OnMethodSelected, d => emitted = d));

        cut.Find("#method-mode-custom").Change(true);

        const string bogusType = "MyApp.DoesNotExist.NoSuchType";
        const string bogusMethod = "Nope";
        cut.Find("#custom-type-name").Input(bogusType);
        cut.Find("#custom-method-name").Input(bogusMethod);
        cut.Find("button").Click();

        // An error indication is shown and no descriptor is emitted (Req 6.7).
        Assert.Null(emitted);
        var alert = cut.Find("div.alert-danger");
        Assert.Contains(bogusType, alert.TextContent);

        // The entered inputs are retained verbatim (Req 6.7, 7.2).
        Assert.Equal(bogusType, cut.Find("#custom-type-name").GetAttribute("value"));
        Assert.Equal(bogusMethod, cut.Find("#custom-method-name").GetAttribute("value"));
    }
}

/// <summary>
/// A uniquely-named helper type with a single public method, used to exercise the Method Picker's
/// successful custom-method validation path against the real loaded assemblies (this test assembly
/// is loaded into the AppDomain the resolver scans).
/// </summary>
public class MethodPickerSampleJobs
{
    public void RunSolo(string name)
    {
        // No body needed; only its reflective shape matters for resolution.
    }
}
