using System;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 2: Argument type-conversion round trip.
//
// For any Job_Parameter and any value valid for its declared type, building the Args array yields
// an entry that, when read back, equals the value converted to that declared type. This exercises
// the scalar/structured conversion path of JobArgumentConverter.BuildArgs across several declared
// types (int, long, double, decimal, bool, string, Guid, DateTime, enum): a value is generated per
// type, serialized to a JsonElement, passed to BuildArgs on a single-parameter method of that type,
// and Args[0] is asserted to equal the value converted to the declared type.
//
// **Validates: Requirements 1.3**

/// <summary>
/// Enum fixture used by the round-trip property. Uniquely named to avoid collision with fixtures in
/// sibling test files of the same wave.
/// </summary>
public enum ArgConversionRoundTrip_Color
{
    Red,
    Green,
    Blue,
    Amber,
}

/// <summary>
/// Fixture exposing single-Job_Parameter methods, one per declared scalar/enum type under test.
/// Uniquely named (prefixed) to avoid collision with fixtures in other test files in the same wave.
/// Methods are never invoked — only reflected over by the converter.
/// </summary>
public sealed class ArgConversionRoundTrip_Target
{
    public void TakeInt(int value) { }
    public void TakeLong(long value) { }
    public void TakeDouble(double value) { }
    public void TakeDecimal(decimal value) { }
    public void TakeBool(bool value) { }
    public void TakeString(string value) { }
    public void TakeGuid(Guid value) { }
    public void TakeDateTime(DateTime value) { }
    public void TakeEnum(ArgConversionRoundTrip_Color value) { }
}

/// <summary>
/// A single generated round-trip scenario: the single-parameter method to build Args for, the
/// argument value serialized to a <see cref="JsonElement"/>, and the value the resulting
/// <c>Args[0]</c> entry is expected to equal once converted to the declared type.
/// </summary>
public sealed class ArgConversionScenario
{
    public string MethodName { get; init; }
    public JsonElement Arg { get; init; }
    public object Expected { get; init; }
    public string Description { get; init; }

    public override string ToString() => Description;
}

/// <summary>
/// Property test for argument type-conversion round trip (Property 2).
///
/// **Validates: Requirements 1.3**
/// </summary>
public class ArgConversionRoundTripProperties
{
    private static MethodInfo Method(string name) =>
        typeof(ArgConversionRoundTrip_Target).GetMethod(name)
            ?? throw new InvalidOperationException($"Fixture method '{name}' not found.");

    // Serialize a value using its declared type so the JsonElement matches what an operator's
    // form/JSON input would produce for that parameter type.
    private static JsonElement ToElement<T>(T value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    // --- Per-type scenario generators -----------------------------------------------------------

    private static Gen<ArgConversionScenario> IntGen =>
        from n in Arb.Default.Int32().Generator
        select new ArgConversionScenario
        {
            MethodName = nameof(ArgConversionRoundTrip_Target.TakeInt),
            Arg = ToElement(n),
            Expected = n,
            Description = $"int {n.ToString(CultureInfo.InvariantCulture)}",
        };

    private static Gen<ArgConversionScenario> LongGen =>
        from n in Arb.Default.Int64().Generator
        select new ArgConversionScenario
        {
            MethodName = nameof(ArgConversionRoundTrip_Target.TakeLong),
            Arg = ToElement(n),
            Expected = n,
            Description = $"long {n.ToString(CultureInfo.InvariantCulture)}",
        };

    private static Gen<ArgConversionScenario> DoubleGen =>
        // NormalFloat excludes NaN/Infinity, which System.Text.Json rejects by default and which are
        // not valid operator inputs for a double parameter.
        from nf in Arb.Default.NormalFloat().Generator
        let d = nf.Get
        select new ArgConversionScenario
        {
            MethodName = nameof(ArgConversionRoundTrip_Target.TakeDouble),
            Arg = ToElement(d),
            Expected = d,
            Description = $"double {d.ToString("R", CultureInfo.InvariantCulture)}",
        };

    private static Gen<ArgConversionScenario> DecimalGen =>
        from m in Arb.Default.Decimal().Generator
        select new ArgConversionScenario
        {
            MethodName = nameof(ArgConversionRoundTrip_Target.TakeDecimal),
            Arg = ToElement(m),
            Expected = m,
            Description = $"decimal {m.ToString(CultureInfo.InvariantCulture)}",
        };

    private static Gen<ArgConversionScenario> BoolGen =>
        from b in Arb.Default.Bool().Generator
        select new ArgConversionScenario
        {
            MethodName = nameof(ArgConversionRoundTrip_Target.TakeBool),
            Arg = ToElement(b),
            Expected = b,
            Description = $"bool {b}",
        };

    private static Gen<ArgConversionScenario> StringGen =>
        // A null string is the "empty" case (resolves to null via the empty-value rule) which is a
        // different property; constrain to non-null values to exercise the conversion path.
        from s in Arb.Default.String().Generator
        let value = s ?? string.Empty
        select new ArgConversionScenario
        {
            MethodName = nameof(ArgConversionRoundTrip_Target.TakeString),
            Arg = ToElement(value),
            Expected = value,
            Description = $"string \"{value}\"",
        };

    private static Gen<ArgConversionScenario> GuidGen =>
        from g in Arb.Default.Guid().Generator
        select new ArgConversionScenario
        {
            MethodName = nameof(ArgConversionRoundTrip_Target.TakeGuid),
            Arg = ToElement(g),
            Expected = g,
            Description = $"Guid {g}",
        };

    private static Gen<ArgConversionScenario> DateTimeGen =>
        from dt in Arb.Default.DateTime().Generator
        select new ArgConversionScenario
        {
            MethodName = nameof(ArgConversionRoundTrip_Target.TakeDateTime),
            Arg = ToElement(dt),
            Expected = dt,
            Description = $"DateTime {dt:O}",
        };

    private static Gen<ArgConversionScenario> EnumGen =>
        from c in Gen.Elements(
            ArgConversionRoundTrip_Color.Red,
            ArgConversionRoundTrip_Color.Green,
            ArgConversionRoundTrip_Color.Blue,
            ArgConversionRoundTrip_Color.Amber)
        select new ArgConversionScenario
        {
            MethodName = nameof(ArgConversionRoundTrip_Target.TakeEnum),
            Arg = ToElement(c),
            Expected = c,
            Description = $"enum {c}",
        };

    private static Arbitrary<ArgConversionScenario> ScenarioArb =>
        Arb.From(Gen.OneOf(new[]
        {
            IntGen,
            LongGen,
            DoubleGen,
            DecimalGen,
            BoolGen,
            StringGen,
            GuidGen,
            DateTimeGen,
            EnumGen,
        }));

    [Property(MaxTest = 100)]
    public Property BuildArgs_ConvertsEachValueToItsDeclaredType()
    {
        return Prop.ForAll(ScenarioArb, sc =>
        {
            var method = Method(sc.MethodName);
            var result = JobArgumentConverter.BuildArgs(method, new[] { sc.Arg });

            if (!result.Success)
                return false.Label($"[{sc.Description}] expected Success but got failure: {result.Error}");

            if (result.Args is null || result.Args.Length != 1)
                return false.Label(
                    $"[{sc.Description}] expected a single-element Args array but got " +
                    $"length {result.Args?.Length.ToString() ?? "<null>"}");

            var actual = result.Args[0];

            if (!Equals(actual, sc.Expected))
                return false.Label(
                    $"[{sc.Description}] round trip mismatch: Args[0] = '{actual}' " +
                    $"({actual?.GetType().Name ?? "null"}) != expected '{sc.Expected}' " +
                    $"({sc.Expected?.GetType().Name ?? "null"})");

            return true.ToProperty();
        });
    }
}
