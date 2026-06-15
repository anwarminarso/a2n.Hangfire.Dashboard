using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Shared;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 16: Invalid JSON blocks the switch to Form mode.
//
// For any Parameter_JSON that is not valid for the selected method, attempting to switch from JSON
// mode to Form mode keeps JSON mode active, preserves the entered Parameter_JSON content unchanged,
// and surfaces a validation error indicating which constraint failed (Req 9.6).
//
// "Valid for the selected method" means: well-formed JSON AND a top-level array AND the right
// element count AND every required parameter present AND each value matching its parameter's
// declared type. This property generates INVALID candidates across each failing constraint:
//   • malformed JSON                  (not well-formed)
//   • non-array top-level             (well-formed but not an array)
//   • wrong element count             (array of the wrong length)
//   • type-incompatible element       (e.g. a string/decimal/bool where an int is required)
//   • missing required parameter      (a null where a required value is expected)
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

    // A single-element array whose element is null where the required parameter is expected.
    private static Gen<InvalidJsonCase> RequiredMissingGen() =>
        Gen.Constant("[null]")
            .Select(s => new InvalidJsonCase { Constraint = "required-missing", Raw = s });

    private static Arbitrary<InvalidJsonCase> CaseArb =>
        Arb.From(Gen.OneOf(new[]
        {
            MalformedGen(),
            NonArrayGen(),
            WrongCountGen(),
            TypeErrorGen(),
            RequiredMissingGen(),
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
}
