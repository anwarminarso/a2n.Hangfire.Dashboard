using System;
using System.Linq;
using System.Text.Json;
using Bunit;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Shared;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 25: Nested-object parameter stays unset until explicitly instantiated.
//
// For any Job_Parameter mapped to NestedObject, the Parameter_Builder renders NO sub-form and leaves
// the parameter UNSET (its Parameter_JSON element is null, resolving to null) until the Operator
// activates the parameter's Add/Create control; only after activation is the nested object
// instantiated and its sub-form rendered, and clearing it returns the parameter to the unset (null)
// state (Req 8.10).
//
// Approach (bunit): build a JobMethodDescriptor with a single JobParameterDescriptor whose InputKind
// is NestedObject and DeclaredType is a uniquely-named public class with a few writable properties.
// Render ParameterBuilder. Before instantiation, assert the emitted ParameterState's
// ArgumentValues[0] is JSON null, the collapsed "Create" placeholder is present, and NO child
// property inputs are rendered. Click Create: assert the sub-form (one input per writable property)
// renders and the slot becomes a JSON object. Click Clear: assert it returns to JSON null and the
// "Create" placeholder returns. FsCheck varies the number of writable properties and whether a child
// value is set; the core invariant is the unset-until-instantiated behavior.
//
// **Validates: Requirements 8.10**

// --- Uniquely-named nested-class fixtures, varying the number of writable properties --------------
// Each writable property is a scalar (string/int) that maps to a single rendered <input>, so the
// number of child inputs after instantiation equals the writable-property count. The first property
// is always a string named Alpha so an optional child-value edit is a valid entry.

/// <summary>Nested fixture with a single writable property.</summary>
public sealed class NestedObjectUnsetOneProp
{
    public string Alpha { get; set; }
}

/// <summary>Nested fixture with two writable properties.</summary>
public sealed class NestedObjectUnsetTwoProps
{
    public string Alpha { get; set; }
    public int Beta { get; set; }
}

/// <summary>Nested fixture with three writable properties.</summary>
public sealed class NestedObjectUnsetThreeProps
{
    public string Alpha { get; set; }
    public int Beta { get; set; }
    public string Gamma { get; set; }
}

/// <summary>
/// One generated nested-object case: the nested <see cref="NestedType"/>, its expected number of
/// rendered child inputs (= writable property count), and whether the property sets a child value
/// after instantiation. Uniquely named to avoid collisions with sibling test fixtures.
/// </summary>
public sealed class NestedObjectUnsetCase
{
    public Type NestedType { get; init; }
    public int WritablePropertyCount { get; init; }
    public bool SetChildValue { get; init; }

    public override string ToString() =>
        $"{NestedType.Name} (props={WritablePropertyCount}, setChild={SetChildValue})";
}

/// <summary>
/// Property test for nested-object parameter staying unset until explicitly instantiated (Property 25).
///
/// **Validates: Requirements 8.10**
/// </summary>
public class NestedObjectUnsetProperties
{
    private static readonly (Type Type, int Props)[] Fixtures =
    {
        (typeof(NestedObjectUnsetOneProp), 1),
        (typeof(NestedObjectUnsetTwoProps), 2),
        (typeof(NestedObjectUnsetThreeProps), 3),
    };

    private static Arbitrary<NestedObjectUnsetCase> CaseArb =>
        Arb.From(
            from f in Gen.Elements(Fixtures)
            from setChild in Arb.Default.Bool().Generator
            select new NestedObjectUnsetCase
            {
                NestedType = f.Type,
                WritablePropertyCount = f.Props,
                SetChildValue = setChild,
            });

    // Count the rendered scalar child controls — none should exist while the nested object is unset.
    private static int ChildInputCount(IRenderedComponent<ParameterBuilder> cut) =>
        cut.FindAll("input, select, textarea").Count;

    private static bool HasButtonWithText(IRenderedComponent<ParameterBuilder> cut, string text) =>
        cut.FindAll("button").Any(b => b.TextContent.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// For any NestedObject Job_Parameter, the parameter stays unset (JSON null) with no sub-form
    /// rendered until the Create control is activated; activation instantiates the object (JSON
    /// object) and renders its sub-form; clearing returns it to the unset (null) state with the
    /// collapsed placeholder restored (Req 8.10).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property NestedObject_StaysUnset_UntilInstantiated_AndClearsBackToNull()
    {
        return Prop.ForAll(CaseArb, c =>
        {
            using var ctx = new Bunit.TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var parameter = new JobParameterDescriptor(
                Name: "p",
                DeclaredType: c.NestedType,
                InputKind: ParameterInputKind.NestedObject,
                IsRequired: false,
                IsNullable: true,
                Position: 0);

            var method = new JobMethodDescriptor(
                TypeFullName: "T",
                MethodName: "M",
                DisplayLabel: "M",
                JobParameters: new[] { parameter },
                Queue: new QueueAttributeInfo(false, null, false));

            ParameterBuilder.ParameterState last = null;
            var cut = ctx.RenderComponent<ParameterBuilder>(p => p
                .Add(x => x.SelectedMethod, method)
                .Add(x => x.OnParametersChanged, (ParameterBuilder.ParameterState s) => last = s));

            // --- Before instantiation: unset (JSON null), no sub-form, collapsed "Create" placeholder ---
            var beforeNull =
                last is not null &&
                last.ArgumentValues.Count == 1 &&
                last.ArgumentValues[0].ValueKind == JsonValueKind.Null;
            var noChildInputsBefore = ChildInputCount(cut) == 0;
            var createPresentBefore = HasButtonWithText(cut, "Create");

            // --- Activate: click Create -> sub-form renders, slot becomes a JSON object ---
            cut.FindAll("button").First(b => b.TextContent.Contains("Create", StringComparison.OrdinalIgnoreCase)).Click();

            var subFormRendered = ChildInputCount(cut) == c.WritablePropertyCount;
            var objectAfterCreate =
                last is not null &&
                last.ArgumentValues.Count == 1 &&
                last.ArgumentValues[0].ValueKind == JsonValueKind.Object;

            // --- Optionally set a child value; the slot must remain a JSON object ---
            if (c.SetChildValue && ChildInputCount(cut) > 0)
            {
                cut.FindAll("input").First().Change("child-value");
            }

            var stillObject =
                last is not null &&
                last.ArgumentValues[0].ValueKind == JsonValueKind.Object;

            // --- Clear: returns to the unset (null) state with the placeholder restored ---
            var clearPresent = HasButtonWithText(cut, "Clear");
            cut.FindAll("button").First(b => b.TextContent.Contains("Clear", StringComparison.OrdinalIgnoreCase)).Click();

            var nullAfterClear =
                last is not null &&
                last.ArgumentValues[0].ValueKind == JsonValueKind.Null;
            var noChildInputsAfterClear = ChildInputCount(cut) == 0;
            var createReturns = HasButtonWithText(cut, "Create");

            return (beforeNull && noChildInputsBefore && createPresentBefore &&
                    subFormRendered && objectAfterCreate && stillObject &&
                    clearPresent && nullAfterClear && noChildInputsAfterClear && createReturns)
                .Label(
                    $"[{c}] beforeNull={beforeNull}, noChildInputsBefore={noChildInputsBefore}, " +
                    $"createPresentBefore={createPresentBefore}, subFormRendered={subFormRendered}, " +
                    $"objectAfterCreate={objectAfterCreate}, stillObject={stillObject}, " +
                    $"clearPresent={clearPresent}, nullAfterClear={nullAfterClear}, " +
                    $"noChildInputsAfterClear={noChildInputsAfterClear}, createReturns={createReturns}");
        });
    }
}
