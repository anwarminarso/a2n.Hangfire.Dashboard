using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Shared;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 16: Invalid JSON blocks the switch to Form mode.
//
// For any Parameter_JSON that is *structurally* invalid for the selected method, attempting to
// switch from JSON mode to Form mode keeps JSON mode active, preserves the entered Parameter_JSON
// content unchanged, and surfaces a validation error indicating which constraint failed (Req 9.6).
//
// "Structurally valid for the selected method" means: well-formed JSON AND a top-level array AND
// the right element count AND each *present* value matching its parameter's declared type. This
// property generates INVALID candidates across each failing structural constraint:
//   • malformed JSON                  (not well-formed)
//   • non-array top-level             (well-formed but not an array)
//   • wrong element count             (array of the wrong length)
//   • type-incompatible element       (e.g. a string/decimal/bool where an int is required)
//
// A missing *required* parameter (a JSON null in a required slot) is intentionally NOT one of these
// candidates: leaving a required field blank while switching views must not block the switch — see
// RequiredMissingDoesNotBlockSwitch_ButStillFailsSubmitValidity below, which locks in that a blank
// required value round-trips into Form mode yet still marks the overall state invalid (bug fix:
// switching JSON → Form used to reject a blank required field with "Parameter 'x' is required.",
// even though the operator was still mid-edit and hadn't attempted to submit anything).
//
// Approach (bunit): render ParameterBuilder with a SelectedMethod carrying a single required int
// Job_Parameter. Click the JSON toggle to enter JSON mode, set the textarea to a generated invalid
// JSON, then click the Form toggle. Assert the JSON editor textarea is still present (still in JSON
// mode), its content is unchanged, and a .text-danger error indication is shown.
//
// **Validates: Requirements 9.6**

/// <summary>
/// One generated invalid-JSON case: the raw Parameter_JSON to enter and the failing constraint it
/// exercises (for diagnostic labeling). Every candidate is invalid for a one-parameter method whose
/// single Job_Parameter is a required <see cref="int"/>.
/// </summary>
public sealed class InvalidJsonCase
{
    public string Constraint { get; init; }
    public string Raw { get; init; }

    public override string ToString() => $"{Constraint}: '{Raw}'";
}

/// <summary>
/// Property test for invalid JSON blocking the switch to Form mode (Property 16).
///
/// **Validates: Requirements 9.6**
/// </summary>
public class InvalidJsonBlocksFormProperties
{
    // Malformed JSON: not well-formed, rejected by the parser.
    private static Gen<InvalidJsonCase> MalformedGen() =>
        Gen.Elements("[", "{", "[1,", "[1 2]", "{\"a\":}", "nul", "]")
            .Select(s => new InvalidJsonCase { Constraint = "malformed", Raw = s });

    // Well-formed JSON whose top-level value is not an array.
    private static Gen<InvalidJsonCase> NonArrayGen() =>
        Gen.Elements("5", "\"hello\"", "true", "null", "{}", "{\"a\":1}")
            .Select(s => new InvalidJsonCase { Constraint = "non-array", Raw = s });

    // A top-level array whose element count differs from the method's single Job_Parameter.
    private static Gen<InvalidJsonCase> WrongCountGen() =>
        Gen.Elements("[]", "[1,2]", "[1,2,3]", "[1,2,3,4]")
            .Select(s => new InvalidJsonCase { Constraint = "count-mismatch", Raw = s });

    // A single-element array whose element is type-incompatible with the required int parameter.
    private static Gen<InvalidJsonCase> TypeErrorGen() =>
        Gen.Elements("[\"abc\"]", "[\"x\"]", "[true]", "[1.5]", "[\"3.14\"]", "[[1]]")
            .Select(s => new InvalidJsonCase { Constraint = "type-error", Raw = s });

    private static Arbitrary<InvalidJsonCase> CaseArb =>
        Arb.From(Gen.OneOf(new[]
        {
            MalformedGen(),
            NonArrayGen(),
            WrongCountGen(),
            TypeErrorGen(),
        }));

    // A method with exactly one required int Job_Parameter, so every generated candidate above is
    // invalid for it across one of the failing constraints.
    private static JobMethodDescriptor OneRequiredIntMethod()
    {
        var parameter = new JobParameterDescriptor(
            Name: "count",
            DeclaredType: typeof(int),
            InputKind: ParameterInputKind.Integer,
            IsRequired: true,
            IsNullable: false,
            Position: 0);

        return new JobMethodDescriptor(
            TypeFullName: "T",
            MethodName: "M",
            DisplayLabel: "M",
            JobParameters: new[] { parameter },
            Queue: new QueueAttributeInfo(false, null, false));
    }

    /// <summary>
    /// For any Parameter_JSON invalid for the selected method, switching JSON→Form keeps JSON mode
    /// active (the textarea remains), preserves the entered content unchanged, and shows a
    /// constraint-specific error (Req 9.6).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property InvalidJson_BlocksSwitchToForm_PreservesContent_AndShowsError()
    {
        return Prop.ForAll(CaseArb, c =>
        {
            using var ctx = new Bunit.TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var method = OneRequiredIntMethod();

            var cut = ctx.RenderComponent<ParameterBuilder>(p => p
                .Add(x => x.SelectedMethod, method));

            // The toggle buttons live in a btn-group: [0] = Form, [1] = JSON.
            var toggleButtons = cut.FindAll(".btn-group button");
            var enteredJsonMode = toggleButtons.Count == 2;
            if (enteredJsonMode)
            {
                toggleButtons[1].Click(); // switch to JSON mode
            }

            // The editable JSON textarea is now present; set it to the generated invalid JSON.
            var hadTextarea = cut.FindAll("textarea").Count == 1;
            if (hadTextarea)
            {
                cut.Find("textarea").Change(c.Raw);
            }

            // Attempt to switch back to Form mode — this must be blocked by the invalid JSON.
            cut.FindAll(".btn-group button")[0].Click();

            // 1) Still in JSON mode: the editable textarea is still present.
            var textareas = cut.FindAll("textarea");
            var stillJsonMode = textareas.Count == 1;

            // 2) The entered content is preserved unchanged.
            var contentPreserved = stillJsonMode &&
                string.Equals(textareas[0].GetAttribute("value"), c.Raw, StringComparison.Ordinal);

            // 3) A constraint-specific error indication is shown.
            var errorShown = cut.FindAll(".text-danger").Count > 0;

            return (enteredJsonMode && hadTextarea && stillJsonMode && contentPreserved && errorShown)
                .Label(
                    $"[{c}] enteredJsonMode={enteredJsonMode}, hadTextarea={hadTextarea}, " +
                    $"stillJsonMode={stillJsonMode}, contentPreserved={contentPreserved}, " +
                    $"errorShown={errorShown}, " +
                    $"value='{(stillJsonMode ? textareas[0].GetAttribute("value") : "<none>")}'");
        });
    }

    /// <summary>
    /// Bug fix regression test: a JSON array that is structurally well-formed but leaves a required
    /// parameter blank (JSON null) must NOT block the switch from JSON mode to Form mode. The
    /// operator is allowed to move between views while a required field is still unset — only
    /// submitting is gated on required-ness (Req 8.15), not viewing the form. This locks in the fix
    /// for the reported bug: editing/creating a recurring job whose method has parameters, switching
    /// to JSON and back to Form incorrectly rejected the switch with "Parameter 'x' is required."
    /// even though the JSON itself was well-formed and the operator hadn't attempted to submit.
    /// </summary>
    [Fact]
    public void RequiredMissing_DoesNotBlockSwitchToForm_ButLeavesOverallStateInvalid()
    {
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var method = OneRequiredIntMethod();
        ParameterBuilder.ParameterState last = null;

        var cut = ctx.RenderComponent<ParameterBuilder>(p => p
            .Add(x => x.SelectedMethod, method)
            .Add(x => x.OnParametersChanged, (ParameterBuilder.ParameterState s) => last = s));

        // Switch to JSON mode and set the single required int parameter to null (blank).
        cut.FindAll(".btn-group button")[1].Click();
        cut.Find("textarea").Change("[null]");

        // Switch back to Form mode — this must succeed despite the required value being blank.
        cut.FindAll(".btn-group button")[0].Click();

        var textareas = cut.FindAll("textarea");
        Assert.Empty(textareas); // no longer in JSON mode: the switch was not blocked

        // No switch-blocking error message ("Parameter 'x' is required.") was surfaced. The
        // required marker on the field label ("*", text-danger) and the Form mode's own blank-
        // required-value affordance ("This value is required.", text-warning) are expected and are
        // not the switch-blocking error this asserts against.
        var switchBlockingError = cut.FindAll(".text-danger").Any(e => e.TextContent.Contains("is required."));
        Assert.False(switchBlockingError);

        // The overall ParameterState is still correctly reported as invalid, so the parent
        // JobBuilder still blocks submission until the operator fills the field in.
        Assert.NotNull(last);
        Assert.False(last.IsValid);
    }
}
