using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Bunit;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Components.Shared;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 14: Form input value capture and invalid-input preservation.
//
// For any Job_Parameter and any value VALID for its type, applying the value through the rendered
// input updates the Argument_Values to reflect the entered value (Req 8.13). For any NON-EMPTY
// value INVALID for its type (a fractional value in an integer input, an out-of-range numeric, a
// malformed Guid, or syntactically invalid JSON), the entry is rejected with an identifying
// indication and the PRIOR valid value in Argument_Values is left unchanged (Req 8.14).
//
// Approach (a) from the task: the property drives ParameterBuilder via bunit. For each generated
// case it builds a JobMethodDescriptor carrying a single JobParameterDescriptor of a chosen
// ParameterInputKind, renders the component, sets a generated VALID value via the rendered control
// and asserts the emitted ParameterState reflects it, then sets a matching NON-EMPTY INVALID value
// and asserts the emitted state preserves the prior valid value, marks the result invalid, and the
// component shows an invalid-input indication. Representative kinds: Integer, Guid, Json.
//
// **Validates: Requirements 8.13, 8.14**

/// <summary>
/// One generated form-input case: a parameter of <see cref="Kind"/>/<see cref="DeclaredType"/>, a
/// value that is valid for the type, a matching non-empty value that is invalid for the type, a
/// predicate verifying the captured element equals the valid value, and the CSS selector of the
/// rendered control. Uniquely named to avoid collisions with sibling test fixtures.
/// </summary>
public sealed class FormInputCaptureCase
{
    public ParameterInputKind Kind { get; init; }
    public Type DeclaredType { get; init; }
    public bool IsNullable { get; init; }
    public string ValidRaw { get; init; }
    public string InvalidRaw { get; init; }
    public Func<JsonElement, bool> MatchesValid { get; init; }
    public string ControlSelector { get; init; }

    public override string ToString() =>
        $"{Kind}: valid='{ValidRaw}' invalid='{InvalidRaw}'";
}

/// <summary>
/// Property test for form input value capture and invalid-input preservation (Property 14).
///
/// **Validates: Requirements 8.13, 8.14**
/// </summary>
public class FormInputCaptureProperties
{
    // Canonicalize a JSON token so structurally-equal values compare equal regardless of spacing.
    private static string Normalize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    // --- Per-type case generators ------------------------------------------------------------

    // Integer (declared type int): any int is valid; invalid forms are a fractional value, an
    // out-of-range value (> int.MaxValue), or a non-numeric string — each rejected by the
    // component's integer conversion (Convert.ChangeType throwing Format/Overflow) (Req 8.3, 8.14).
    private static Gen<FormInputCaptureCase> IntegerGen()
    {
        var invalidGen = Gen.OneOf(new[]
        {
            Gen.Choose(1, 9999).Select(n => $"{n}.5"),
            Gen.Choose(1, 1_000_000).Select(n => ((long)int.MaxValue + n).ToString(CultureInfo.InvariantCulture)),
            Gen.Constant("not-a-number"),
        });

        return from v in Arb.Default.Int32().Generator
               from inv in invalidGen
               select new FormInputCaptureCase
               {
                   Kind = ParameterInputKind.Integer,
                   DeclaredType = typeof(int),
                   IsNullable = false,
                   ValidRaw = v.ToString(CultureInfo.InvariantCulture),
                   InvalidRaw = inv,
                   MatchesValid = e => e.ValueKind == JsonValueKind.Number && e.GetInt64() == v,
                   ControlSelector = "input",
               };
    }

    // Guid (declared type Guid): a generated Guid's canonical string is valid; corrupting the first
    // hex digit to a non-hex character yields a malformed Guid that the component rejects (Req 8.6,
    // 8.14).
    private static Gen<FormInputCaptureCase> GuidGen()
    {
        return from g in Arb.Default.Guid().Generator
               let valid = g.ToString()
               select new FormInputCaptureCase
               {
                   Kind = ParameterInputKind.Guid,
                   DeclaredType = typeof(Guid),
                   IsNullable = false,
                   ValidRaw = valid,
                   InvalidRaw = "z" + valid.Substring(1), // 'z' is not a hex digit -> Guid.TryParse fails
                   MatchesValid = e => e.ValueKind == JsonValueKind.String && e.GetString() == valid,
                   ControlSelector = "input",
               };
    }

    // Json (declared type object -> Json input): any well-formed JSON token is valid; the invalid
    // forms are syntactically malformed JSON the component rejects via JsonDocument.Parse (Req 8.12,
    // 8.14).
    private static Gen<FormInputCaptureCase> JsonGen()
    {
        var validGen = Gen.Elements("123", "\"hello\"", "true", "[1,2,3]", "{\"a\":1}", "-4.5");
        var invalidGen = Gen.Elements("{", "[1,2", "{\"a\":}", "nul", "{,}", "}");

        return from valid in validGen
               from invalid in invalidGen
               select new FormInputCaptureCase
               {
                   Kind = ParameterInputKind.Json,
                   DeclaredType = typeof(object),
                   IsNullable = true,
                   ValidRaw = valid,
                   InvalidRaw = invalid,
                   MatchesValid = e => Normalize(e.GetRawText()) == Normalize(valid),
                   ControlSelector = "textarea",
               };
    }

    private static Arbitrary<FormInputCaptureCase> CaseArb =>
        Arb.From(Gen.OneOf(new[] { IntegerGen(), GuidGen(), JsonGen() }));

    /// <summary>
    /// For any Job_Parameter and a value valid for its type, applying it updates Argument_Values to
    /// that value (Req 8.13); a subsequent non-empty invalid value is rejected with an identifying
    /// indication, leaving the prior valid value in Argument_Values unchanged (Req 8.14).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property FormInput_CapturesValidValue_AndPreservesPriorValueOnInvalidEntry()
    {
        return Prop.ForAll(CaseArb, c =>
        {
            using var ctx = new Bunit.TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var parameter = new JobParameterDescriptor(
                Name: "p",
                DeclaredType: c.DeclaredType,
                InputKind: c.Kind,
                IsRequired: false,
                IsNullable: c.IsNullable,
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

            // --- 8.13: a value valid for the type updates Argument_Values to reflect it ---
            cut.Find(c.ControlSelector).Change(c.ValidRaw);

            var capturedValid =
                last is not null &&
                last.ArgumentValues.Count == 1 &&
                last.IsValid &&
                c.MatchesValid(last.ArgumentValues[0]);

            // The prior valid value as it now stands in Argument_Values.
            var priorRaw = last is { ArgumentValues.Count: 1 } ? last.ArgumentValues[0].GetRawText() : null;

            // --- 8.14: a non-empty invalid value is rejected and the prior value is preserved ---
            cut.Find(c.ControlSelector).Change(c.InvalidRaw);

            var indicationShown = cut.FindAll(".is-invalid").Count > 0;
            var rejected = last is not null && !last.IsValid;
            var preserved =
                last is not null &&
                last.ArgumentValues.Count == 1 &&
                last.ArgumentValues[0].GetRawText() == priorRaw;

            return (capturedValid && indicationShown && rejected && preserved)
                .Label(
                    $"[{c}] capturedValid={capturedValid}, indicationShown={indicationShown}, " +
                    $"rejected={rejected}, preserved={preserved}, " +
                    $"prior='{priorRaw}', now='{(last is { ArgumentValues.Count: 1 } ? last.ArgumentValues[0].GetRawText() : "<none>")}'");
        });
    }
}
