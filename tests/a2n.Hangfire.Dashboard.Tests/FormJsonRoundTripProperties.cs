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

// Feature: job-builder, Property 15: Form/JSON round trip and live mirror.
//
// This property drives ParameterBuilder via bunit using a SelectedMethod whose Job_Parameters are
// scalar kinds (int / string) so values round-trip cleanly. It asserts three behaviors:
//
//   • Live mirror (Req 9.2): while in Form mode, the read-only Parameter_JSON mirror (the <pre>
//     block) reflects the current Argument_Values entered through the form controls.
//   • Form → JSON serialization (Req 9.4): switching to JSON mode initializes the editable
//     <textarea> by serializing the current Argument_Values into the canonical Parameter_JSON.
//   • JSON → Form round trip (Req 9.5): switching back to Form mode while the JSON is valid
//     repopulates the form so the resulting Argument_Values equal the JSON content — i.e. a
//     Form → JSON → Form round trip preserves the values.
//
// **Validates: Requirements 9.2, 9.4, 9.5**

/// <summary>
/// One generated scalar parameter for the round-trip method: its <see cref="Kind"/> /
/// <see cref="DeclaredType"/>, a raw value valid for the type, and the canonical JSON token that the
/// value serializes to (a JSON number for integers, a JSON string for text). Uniquely named to
/// avoid collisions with sibling test fixtures.
/// </summary>
public sealed class RoundTripParamSpec
{
    public ParameterInputKind Kind { get; init; }
    public Type DeclaredType { get; init; }
    public bool IsNullable { get; init; }
    public string Raw { get; init; }
    public string CanonicalJson { get; init; }

    public override string ToString() => $"{Kind}({DeclaredType.Name})='{Raw}'";
}

/// <summary>
/// Property test for the Form/JSON round trip and the live read-only mirror (Property 15).
///
/// **Validates: Requirements 9.2, 9.4, 9.5**
/// </summary>
public class FormJsonRoundTripProperties
{
    // Canonicalize a JSON token so structurally-equal arrays compare equal regardless of spacing or
    // indentation (the mirror is pretty-printed, the editor is compact).
    private static string Normalize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    // --- Per-type parameter generators -------------------------------------------------------

    // An int parameter: any Int32 is a valid value; it serializes to a JSON number.
    private static Gen<RoundTripParamSpec> IntegerSpec()
    {
        return from v in Arb.Default.Int32().Generator
               let raw = v.ToString(CultureInfo.InvariantCulture)
               select new RoundTripParamSpec
               {
                   Kind = ParameterInputKind.Integer,
                   DeclaredType = typeof(int),
                   IsNullable = false,
                   Raw = raw,
                   CanonicalJson = raw, // a JSON number token
               };
    }

    // A string parameter: a non-empty alphanumeric value (never blank, so it is not treated as the
    // empty/null case) that serializes to a JSON string.
    private static Gen<RoundTripParamSpec> TextSpec()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var charGen = Gen.Elements(alphabet.ToCharArray());

        return from len in Gen.Choose(1, 15)
               from chars in Gen.ArrayOf(len, charGen)
               let s = new string(chars)
               select new RoundTripParamSpec
               {
                   Kind = ParameterInputKind.Text,
                   DeclaredType = typeof(string),
                   IsNullable = true,
                   Raw = s,
                   CanonicalJson = JsonSerializer.Serialize(s),
               };
    }

    // A method with 1–4 scalar Job_Parameters, each an int or a string.
    private static Arbitrary<List<RoundTripParamSpec>> MethodArb =>
        Arb.From(
            from n in Gen.Choose(1, 4)
            from specs in Gen.ListOf(n, Gen.OneOf(new[] { IntegerSpec(), TextSpec() }))
            select specs.ToList());

    private static JobMethodDescriptor BuildMethod(IReadOnlyList<RoundTripParamSpec> specs)
    {
        var parameters = specs
            .Select((s, i) => new JobParameterDescriptor(
                Name: $"p{i}",
                DeclaredType: s.DeclaredType,
                InputKind: s.Kind,
                IsRequired: false,
                IsNullable: s.IsNullable,
                Position: i))
            .ToArray();

        return new JobMethodDescriptor(
            TypeFullName: "T",
            MethodName: "M",
            DisplayLabel: "M",
            JobParameters: parameters,
            Queue: new QueueAttributeInfo(false, null, false));
    }

    /// <summary>
    /// In Form mode the read-only mirror reflects the entered Argument_Values (9.2); switching to
    /// JSON serializes those values into the editable textarea (9.4); switching back to Form while
    /// the JSON is valid repopulates the form so a Form → JSON → Form round trip preserves the
    /// values (9.5).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property FormJson_RoundTrip_PreservesValues_AndMirrorReflectsForm()
    {
        return Prop.ForAll(MethodArb, specs =>
        {
            using var ctx = new Bunit.TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var method = BuildMethod(specs);

            ParameterBuilder.ParameterState last = null;
            var cut = ctx.RenderComponent<ParameterBuilder>(p => p
                .Add(x => x.SelectedMethod, method)
                .Add(x => x.OnParametersChanged, (ParameterBuilder.ParameterState s) => last = s));

            // --- Enter a value into each form control, in parameter order (Form mode default). ---
            var inputs = cut.FindAll("input");
            if (inputs.Count != specs.Count)
            {
                return false.Label($"expected {specs.Count} inputs, found {inputs.Count}");
            }

            for (var i = 0; i < specs.Count; i++)
            {
                // Re-query each time: every Change triggers a re-render of the component.
                cut.FindAll("input")[i].Change(specs[i].Raw);
            }

            // The canonical Parameter_JSON that the entered values should produce.
            var expectedJson = "[" + string.Join(",", specs.Select(s => s.CanonicalJson)) + "]";

            // --- 9.2: the read-only <pre> mirror reflects the current Argument_Values. ---
            var mirror = cut.Find("pre").TextContent;
            var mirrorReflects = Normalize(mirror) == Normalize(expectedJson)
                && last is not null
                && Normalize(last.ParameterJson) == Normalize(expectedJson)
                && last.IsValid;

            // Snapshot the Form-mode Argument_Values before toggling.
            var formJson = last is null ? "[]" : last.ParameterJson;
            var formValues = last?.ArgumentValues.Select(e => e.GetRawText()).ToList() ?? new List<string>();

            // --- 9.4: switch Form → JSON; the textarea is seeded with the serialized values. ---
            // The btn-group has two buttons: [0] Form, [1] JSON.
            cut.FindAll("button")[1].Click();

            var editor = cut.Find("textarea").GetAttribute("value");
            var jsonSeeded = editor is not null && Normalize(editor) == Normalize(formJson);

            // --- 9.5: switch JSON → Form; valid JSON repopulates so the values round-trip. ---
            cut.FindAll("button")[0].Click();

            var roundTripValues = last?.ArgumentValues.Select(e => e.GetRawText()).ToList() ?? new List<string>();
            var roundTrips =
                last is not null &&
                last.IsValid &&
                roundTripValues.Count == formValues.Count &&
                Normalize(last.ParameterJson) == Normalize(formJson);

            return (mirrorReflects && jsonSeeded && roundTrips)
                .Label(
                    $"[{string.Join(", ", specs)}] mirrorReflects={mirrorReflects}, jsonSeeded={jsonSeeded}, " +
                    $"roundTrips={roundTrips}; expected={expectedJson}, mirror={mirror}, editor={editor}, " +
                    $"formJson={formJson}, afterRoundTrip={(last is null ? "<none>" : last.ParameterJson)}");
        });
    }
}
