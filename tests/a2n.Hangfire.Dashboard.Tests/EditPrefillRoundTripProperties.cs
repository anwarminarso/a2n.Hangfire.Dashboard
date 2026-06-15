using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 7: Edit pre-fill argument round trip.
//
// For any recurring job whose stored Args holds N values, opening the editor produces a
// Parameter_JSON array of exactly N elements whose elements correspond positionally and one-to-one
// to the stored Args values.
//
// This validates the value-level serialization round-trip contract: for a generated object[] Args of
// N JSON-serializable values (strings, ints, bools, null), serializing and reparsing yields an array
// of exactly N elements corresponding one-to-one and positionally to the stored Args. The separate
// exclusion of Hangfire-injected parameters (and the IntPtr-crash fix from issue #10) is covered by
// Issue10EditPrefillTests, which exercises JobArgumentConverter.ToParameterJsonFromArgs directly.
//
// Approach chosen: logic-level serialization round trip (the simpler, acceptable option per the
// task), exercising the same System.Text.Json.JsonSerializer.Serialize(args) call the editor relies
// on rather than driving the component through bunit.
//
// **Validates: Requirements 3.1**

/// <summary>
/// One stored <c>Args</c> element together with the typed expectation its serialized JSON element
/// must satisfy. The generated <see cref="Value"/> is boxed into an <c>object[]</c> exactly as
/// Hangfire stores a job's positional arguments.
/// </summary>
public sealed class EditPrefillArg
{
    /// <summary>The boxed stored argument value (string, int, bool, or <c>null</c>).</summary>
    public object Value { get; init; }

    /// <summary>A short description used in counterexample labels.</summary>
    public string Description { get; init; }

    /// <summary>
    /// Asserts the serialized-then-reparsed <see cref="JsonElement"/> at this position matches the
    /// original stored value one-to-one.
    /// </summary>
    public bool Matches(JsonElement element) => Value switch
    {
        null => element.ValueKind == JsonValueKind.Null,
        string s => element.ValueKind == JsonValueKind.String && element.GetString() == s,
        bool b => element.ValueKind == (b ? JsonValueKind.True : JsonValueKind.False),
        int n => element.ValueKind == JsonValueKind.Number
                 && element.TryGetInt32(out var actual) && actual == n,
        _ => false,
    };

    public override string ToString() => Description;
}

/// <summary>
/// Property test for edit pre-fill argument round trip (Property 7).
///
/// **Validates: Requirements 3.1**
/// </summary>
public class EditPrefillRoundTripProperties
{
    // Generates a single stored-arg value of a varied JSON-serializable type: string, int, bool, or
    // null. Null models both a blank operator value and a Hangfire-injected parameter slot, which the
    // editor stores positionally in Args.
    private static Gen<EditPrefillArg> ArgGen
    {
        get
        {
            var stringGen =
                from s in Arb.Default.String().Generator
                let value = s ?? string.Empty
                select new EditPrefillArg { Value = value, Description = $"string \"{value}\"" };

            var intGen =
                from n in Arb.Default.Int32().Generator
                select new EditPrefillArg
                {
                    Value = n,
                    Description = $"int {n.ToString(CultureInfo.InvariantCulture)}",
                };

            var boolGen =
                from b in Arb.Default.Bool().Generator
                select new EditPrefillArg { Value = b, Description = $"bool {b}" };

            var nullGen = Gen.Constant(new EditPrefillArg { Value = null, Description = "null" });

            return Gen.OneOf(stringGen, intGen, boolGen, nullGen);
        }
    }

    // An N-element list of stored args (N in 0..20), modeling a recurring job's positional Args of
    // arbitrary length, including the empty case.
    private static Arbitrary<EditPrefillArg[]> ArgsArb =>
        Arb.From(Gen.Choose(0, 20).SelectMany(n => Gen.ArrayOf(n, ArgGen)));

    // Mirrors the value-level serialization the editor relies on (Req 3.1): System.Text.Json
    // serialize over the (already injected-filtered) Args values.
    private static string PrefillAsEditor(object[] args) => JsonSerializer.Serialize(args);

    [Property(MaxTest = 100)]
    public Property Prefill_ProducesNElementArray_MatchingStoredArgsPositionally()
    {
        return Prop.ForAll(ArgsArb, generated =>
        {
            var storedArgs = generated.Select(a => a.Value).ToArray();
            var json = PrefillAsEditor(storedArgs);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // The pre-filled Parameter_JSON must be a JSON array (Req 3.1).
            if (root.ValueKind != JsonValueKind.Array)
            {
                return false.Label(
                    $"expected a JSON array but got {root.ValueKind} for {generated.Length} stored args");
            }

            var elements = root.EnumerateArray().ToArray();

            // Exactly N elements — one-to-one with the stored Args (Req 3.1).
            if (elements.Length != generated.Length)
            {
                return false.Label(
                    $"expected {generated.Length} elements but got {elements.Length}: {json}");
            }

            // Each element corresponds positionally and one-to-one to the stored Args value (Req 3.1).
            for (var i = 0; i < generated.Length; i++)
            {
                if (!generated[i].Matches(elements[i]))
                {
                    return false.Label(
                        $"element {i} mismatch: stored [{generated[i]}] did not round-trip to " +
                        $"'{elements[i].GetRawText()}' in {json}");
                }
            }

            return true.ToProperty();
        });
    }
}
