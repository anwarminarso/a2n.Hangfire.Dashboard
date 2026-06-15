using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 13: Parameter-form control mapping.
//
// For any declared Job_Parameter type, ParameterInputMapper.Map(Type, depth) returns the
// ParameterInputKind dictated by the design's mapping table:
//   string                                   -> Text
//   integral (byte/sbyte/short/ushort/        -> Integer
//             int/uint/long/ulong)
//   floating (float/double/decimal)          -> Float
//   DateOnly / TimeOnly / DateTime,DateTimeOffset -> Date / Time / DateTime
//   Guid                                     -> Guid
//   bool / bool?                             -> Bool / NullableBool
//   enum / [Flags] enum                      -> EnumSingle / EnumFlags
//   T[] of a supported scalar                -> ScalarArray
//   public class within nesting depth <= 5   -> NestedObject
//   depth > 5, or any unsupported type       -> Json
// In addition, a Nullable<T> (other than bool?) maps the same as its underlying T.
//
// **Validates: Requirements 8.1, 8.2, 8.3, 8.4, 8.5, 8.6, 8.7, 8.8, 8.9, 8.10, 8.11, 8.12**

/// <summary>A non-flags enum fixture. Uniquely named to avoid collision with sibling test files.</summary>
public enum ParamInputMap_Color
{
    Red,
    Green,
    Blue,
}

/// <summary>A <c>[Flags]</c> enum fixture used to confirm the EnumFlags mapping.</summary>
[Flags]
public enum ParamInputMap_Perm
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4,
}

/// <summary>
/// A public, concrete, instantiable class used to confirm the NestedObject mapping (within the
/// depth budget) and the depth &gt; 5 fall-back to JSON. Uniquely named to avoid collisions.
/// Never instantiated — only reflected over as a parameter type.
/// </summary>
public sealed class ParamInputMap_Nested
{
    public string Name { get; init; }
    public int Count { get; init; }
}

/// <summary>An interface — not a concrete class — which is therefore an unsupported type → JSON.</summary>
public interface IParamInputMap_Unsupported
{
    int Value { get; }
}

/// <summary>
/// A single mapping case: a declared type observed at a nesting <see cref="Depth"/>, with the
/// <see cref="Expected"/> kind from the design table.
/// </summary>
public sealed class ParamInputMapCase
{
    public Type Type { get; init; }
    public int Depth { get; init; }
    public ParameterInputKind Expected { get; init; }
    public string Description { get; init; }

    public override string ToString() => Description;
}

/// <summary>
/// Property test for parameter-form control mapping (Property 13).
///
/// **Validates: Requirements 8.1, 8.2, 8.3, 8.4, 8.5, 8.6, 8.7, 8.8, 8.9, 8.10, 8.11, 8.12**
/// </summary>
public class ParameterInputMapperProperties
{
    private static ParamInputMapCase Case(Type type, ParameterInputKind expected, int depth = 1, string note = null) =>
        new()
        {
            Type = type,
            Depth = depth,
            Expected = expected,
            Description = $"{type.Name}{(depth == 1 ? "" : $" @depth {depth}")} -> {expected}{(note is null ? "" : $" ({note})")}",
        };

    /// <summary>
    /// The representative table of (Type, depth, expectedKind) cases covering every row of the
    /// design's ParameterInputKind table (Req 8.2–8.12) plus the Nullable&lt;T&gt; rule.
    /// </summary>
    private static readonly ParamInputMapCase[] Cases =
    {
        // 8.2 string -> Text
        Case(typeof(string), ParameterInputKind.Text),

        // 8.3 integral -> Integer (all eight integral CLR types)
        Case(typeof(byte), ParameterInputKind.Integer),
        Case(typeof(sbyte), ParameterInputKind.Integer),
        Case(typeof(short), ParameterInputKind.Integer),
        Case(typeof(ushort), ParameterInputKind.Integer),
        Case(typeof(int), ParameterInputKind.Integer),
        Case(typeof(uint), ParameterInputKind.Integer),
        Case(typeof(long), ParameterInputKind.Integer),
        Case(typeof(ulong), ParameterInputKind.Integer),

        // 8.4 floating -> Float
        Case(typeof(float), ParameterInputKind.Float),
        Case(typeof(double), ParameterInputKind.Float),
        Case(typeof(decimal), ParameterInputKind.Float),

        // 8.5 date/time/datetime
        Case(typeof(DateOnly), ParameterInputKind.Date),
        Case(typeof(TimeOnly), ParameterInputKind.Time),
        Case(typeof(DateTime), ParameterInputKind.DateTime),
        Case(typeof(DateTimeOffset), ParameterInputKind.DateTime),

        // 8.6 Guid
        Case(typeof(Guid), ParameterInputKind.Guid),

        // 8.7 bool / bool?
        Case(typeof(bool), ParameterInputKind.Bool),
        Case(typeof(bool?), ParameterInputKind.NullableBool),

        // 8.8 enum / [Flags] enum
        Case(typeof(ParamInputMap_Color), ParameterInputKind.EnumSingle),
        Case(typeof(ParamInputMap_Perm), ParameterInputKind.EnumFlags),

        // 8.9 scalar array -> ScalarArray (across several scalar element kinds)
        Case(typeof(int[]), ParameterInputKind.ScalarArray),
        Case(typeof(string[]), ParameterInputKind.ScalarArray),
        Case(typeof(double[]), ParameterInputKind.ScalarArray),
        Case(typeof(Guid[]), ParameterInputKind.ScalarArray),
        Case(typeof(bool[]), ParameterInputKind.ScalarArray),
        Case(typeof(DateTime[]), ParameterInputKind.ScalarArray),
        Case(typeof(ParamInputMap_Color[]), ParameterInputKind.ScalarArray),

        // 8.10/8.11 public class within depth <= 5 -> NestedObject
        Case(typeof(ParamInputMap_Nested), ParameterInputKind.NestedObject, depth: 1),
        Case(typeof(ParamInputMap_Nested), ParameterInputKind.NestedObject, depth: 5, note: "depth boundary"),

        // 8.11 public class beyond depth 5 -> Json
        Case(typeof(ParamInputMap_Nested), ParameterInputKind.Json, depth: 6, note: "depth > 5"),

        // 8.12 unsupported types -> Json
        Case(typeof(IParamInputMap_Unsupported), ParameterInputKind.Json, note: "interface"),
        Case(typeof(int[,]), ParameterInputKind.Json, note: "multi-dim array"),
        Case(typeof(ParamInputMap_Nested[]), ParameterInputKind.Json, note: "array of non-scalar"),

        // Nullable<T> (other than bool?) maps like its underlying T.
        Case(typeof(int?), ParameterInputKind.Integer, note: "Nullable<int> == int"),
        Case(typeof(long?), ParameterInputKind.Integer, note: "Nullable<long> == long"),
        Case(typeof(double?), ParameterInputKind.Float, note: "Nullable<double> == double"),
        Case(typeof(decimal?), ParameterInputKind.Float, note: "Nullable<decimal> == decimal"),
        Case(typeof(DateOnly?), ParameterInputKind.Date, note: "Nullable<DateOnly> == DateOnly"),
        Case(typeof(TimeOnly?), ParameterInputKind.Time, note: "Nullable<TimeOnly> == TimeOnly"),
        Case(typeof(DateTime?), ParameterInputKind.DateTime, note: "Nullable<DateTime> == DateTime"),
        Case(typeof(DateTimeOffset?), ParameterInputKind.DateTime, note: "Nullable<DateTimeOffset> == DateTimeOffset"),
        Case(typeof(Guid?), ParameterInputKind.Guid, note: "Nullable<Guid> == Guid"),
        Case(typeof(ParamInputMap_Color?), ParameterInputKind.EnumSingle, note: "Nullable<enum> == enum"),
        Case(typeof(ParamInputMap_Perm?), ParameterInputKind.EnumFlags, note: "Nullable<[Flags] enum> == [Flags] enum"),
    };

    private static Arbitrary<ParamInputMapCase> CaseArb => Arb.From(Gen.Elements(Cases));

    /// <summary>
    /// Each declared type maps to the expected <see cref="ParameterInputKind"/> per the design
    /// table, including the Nullable&lt;T&gt; rule (Req 8.2–8.12).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Map_ProducesExpectedKind_ForEachDeclaredType()
    {
        return Prop.ForAll(CaseArb, c =>
        {
            var actual = ParameterInputMapper.Map(c.Type, c.Depth);
            return (actual == c.Expected)
                .Label($"[{c.Description}] but got {actual}");
        });
    }

    /// <summary>
    /// Depth boundary for a public-class parameter (Req 8.10, 8.11): at depth ≤ 5 it renders as a
    /// NestedObject sub-form; at depth &gt; 5 it falls back to a JSON input. Generates depths across
    /// the boundary to confirm the transition is exactly at depth 5.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Map_NestedClass_FallsBackToJson_BeyondDepthFive()
    {
        var depthArb = Arb.From(Gen.Choose(1, 12));

        return Prop.ForAll(depthArb, depth =>
        {
            var actual = ParameterInputMapper.Map(typeof(ParamInputMap_Nested), depth);
            var expected = depth <= ParameterInputMapper.MaxNestedDepth
                ? ParameterInputKind.NestedObject
                : ParameterInputKind.Json;

            return (actual == expected)
                .Label($"depth {depth}: expected {expected} but got {actual}");
        });
    }
}
