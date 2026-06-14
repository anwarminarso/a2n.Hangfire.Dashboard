using System.Linq;
using AngleSharp.Dom;
using a2n.Hangfire.Dashboard.Components.Shared;
using a2n.Hangfire.Dashboard.Models;
using Bunit;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// bunit component tests for <c>ParameterBuilder.razor</c> (task 11.7).
///
/// These tests cover two behaviors that are best verified against the rendered DOM:
///   • The Form/JSON mode toggle: two mutually exclusive modes with Form active by default and a
///     read-only Parameter_JSON mirror visible (Req 9.1); switching to JSON reveals the editable
///     textarea and hides the dynamic form, and switching back to Form restores the form (Req 9.3).
///   • Nested-object behavior: a NestedObject parameter renders a collapsed placeholder with a
///     Create button and NO sub-form until activated; after Create the sub-form renders; after
///     Clear it returns to unset (Req 8.10).
///
/// The fixtures here are uniquely named to avoid colliding with other ParameterBuilder fixtures
/// (e.g. task 11.6's nested-object property fixture).
/// </summary>
public class ParameterBuilderModeAndNestedTests
{
    // --- Helpers ---------------------------------------------------------------------------

    private static TestContext NewContext()
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static JobParameterDescriptor Param(
        string name, Type type, ParameterInputKind kind, int position,
        bool required = false, bool nullable = true)
        => new(name, type, kind, required, nullable, position);

    private static JobMethodDescriptor Method(params JobParameterDescriptor[] parameters)
        => new(
            TypeFullName: "ModeAndNestedFixtures.Jobs",
            MethodName: "Run",
            DisplayLabel: "Run",
            JobParameters: parameters,
            Queue: new QueueAttributeInfo(false, null, false));

    private static IElement[] ModeButtons(IRenderedComponent<ParameterBuilder> cut)
        => cut.Find("[aria-label='Parameter input mode']")
            .QuerySelectorAll("button")
            .ToArray();

    // --- 9.1 — Form is the default mode with a read-only mirror ----------------------------

    [Fact]
    public void Defaults_To_Form_Mode_With_ReadOnly_Mirror_And_No_Editor()
    {
        using var ctx = NewContext();

        var method = Method(Param("name", typeof(string), ParameterInputKind.Text, 0));

        var cut = ctx.RenderComponent<ParameterBuilder>(p => p
            .Add(c => c.SelectedMethod, method));

        var buttons = ModeButtons(cut);
        Assert.Equal(2, buttons.Length);

        var formButton = buttons[0];
        var jsonButton = buttons[1];

        // Form is active by default and JSON is inactive (Req 9.1). The active button carries the
        // solid "btn-primary" class while the inactive one carries "btn-outline-primary".
        Assert.Contains("btn-primary", formButton.ClassName);
        Assert.DoesNotContain("btn-primary", jsonButton.ClassName); // outline only

        // Form mode shows the dynamic form (a text input) plus the read-only JSON mirror (Req 9.3).
        Assert.NotEmpty(cut.FindAll("input[type='text']"));
        Assert.NotEmpty(cut.FindAll("pre"));

        // The editable JSON textarea is NOT present while in Form mode (Req 9.3).
        Assert.Empty(cut.FindAll("textarea"));
    }

    // --- 9.3 — JSON mode shows the textarea and hides the form -----------------------------

    [Fact]
    public void Switching_To_Json_Shows_Editable_Textarea_And_Hides_Form()
    {
        using var ctx = NewContext();

        var method = Method(Param("name", typeof(string), ParameterInputKind.Text, 0));

        var cut = ctx.RenderComponent<ParameterBuilder>(p => p
            .Add(c => c.SelectedMethod, method));

        // Click the JSON mode button.
        ModeButtons(cut)[1].Click();

        // JSON mode reveals the editable textarea (Req 9.3)...
        Assert.NotEmpty(cut.FindAll("textarea"));

        // ...and hides the dynamic form inputs and the read-only mirror (Req 9.3).
        Assert.Empty(cut.FindAll("input[type='text']"));
        Assert.Empty(cut.FindAll("pre"));

        // The JSON button is now the active mode.
        var buttons = ModeButtons(cut);
        Assert.DoesNotContain("btn-primary", buttons[0].ClassName); // Form now outline
        Assert.Contains("btn-primary", buttons[1].ClassName);       // JSON now solid
    }

    // --- 9.3 — switching back to Form restores the form ------------------------------------

    [Fact]
    public void Switching_Back_To_Form_Restores_The_Form()
    {
        using var ctx = NewContext();

        var method = Method(Param("name", typeof(string), ParameterInputKind.Text, 0));

        var cut = ctx.RenderComponent<ParameterBuilder>(p => p
            .Add(c => c.SelectedMethod, method));

        // Form -> JSON -> Form.
        ModeButtons(cut)[1].Click();
        Assert.NotEmpty(cut.FindAll("textarea"));

        ModeButtons(cut)[0].Click();

        // The dynamic form and the read-only mirror return; the editable textarea is gone (Req 9.3).
        Assert.NotEmpty(cut.FindAll("input[type='text']"));
        Assert.NotEmpty(cut.FindAll("pre"));
        Assert.Empty(cut.FindAll("textarea"));

        var buttons = ModeButtons(cut);
        Assert.Contains("btn-primary", buttons[0].ClassName);       // Form active again
        Assert.DoesNotContain("btn-primary", buttons[1].ClassName); // JSON back to outline
    }

    // --- 8.10 — nested-object Create-then-Clear lifecycle ----------------------------------

    [Fact]
    public void NestedObject_Renders_Collapsed_Placeholder_With_No_SubForm_Until_Created()
    {
        using var ctx = NewContext();

        var method = Method(
            Param("settings", typeof(ModeAndNestedSettingsFixture), ParameterInputKind.NestedObject, 0));

        var cut = ctx.RenderComponent<ParameterBuilder>(p => p
            .Add(c => c.SelectedMethod, method));

        // The collapsed placeholder is shown with a Create button (Req 8.10)...
        Assert.Contains("Not set (null)", cut.Markup);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Create"));

        // ...and NO sub-form is rendered: the nested property names are absent from the DOM.
        Assert.DoesNotContain(nameof(ModeAndNestedSettingsFixture.Label), cut.Markup);
        Assert.DoesNotContain(nameof(ModeAndNestedSettingsFixture.Count), cut.Markup);
    }

    [Fact]
    public void NestedObject_Create_Renders_SubForm_Then_Clear_Returns_To_Unset()
    {
        using var ctx = NewContext();

        var method = Method(
            Param("settings", typeof(ModeAndNestedSettingsFixture), ParameterInputKind.NestedObject, 0));

        var cut = ctx.RenderComponent<ParameterBuilder>(p => p
            .Add(c => c.SelectedMethod, method));

        // Activate the nested object (Req 8.10).
        cut.FindAll("button").First(b => b.TextContent.Contains("Create")).Click();

        // The sub-form now renders the nested property fields, and a Clear control appears.
        Assert.Contains(nameof(ModeAndNestedSettingsFixture.Label), cut.Markup);
        Assert.Contains(nameof(ModeAndNestedSettingsFixture.Count), cut.Markup);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Clear"));
        Assert.DoesNotContain("Not set (null)", cut.Markup);

        // Clearing returns the parameter to unset: the placeholder and Create button come back, and
        // the sub-form disappears (Req 8.10).
        cut.FindAll("button").First(b => b.TextContent.Contains("Clear")).Click();

        Assert.Contains("Not set (null)", cut.Markup);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Create"));
        Assert.DoesNotContain(nameof(ModeAndNestedSettingsFixture.Label), cut.Markup);
        Assert.DoesNotContain(nameof(ModeAndNestedSettingsFixture.Count), cut.Markup);
    }
}

/// <summary>
/// A uniquely-named public class used as a NestedObject parameter type for the nested-object
/// lifecycle tests. Its public writable properties drive the rendered sub-form. The property names
/// are deliberately distinct so the tests can assert their presence/absence in the DOM.
/// </summary>
public class ModeAndNestedSettingsFixture
{
    public string Label { get; set; }

    public int Count { get; set; }
}
