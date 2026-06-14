using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 24: Empty non-nullable parameter resolves to default(T).
//
// For any Job_Parameter whose declared type is NOT nullable (e.g. int, long, bool, double,
// DateTime, Guid, an enum, or a non-nullable struct), when its input is left empty (a JSON null
// element), building the Args array yields default(T) for that declared type (0, false,
// default(DateTime), Guid.Empty, the enum's zero member, etc.) at that parameter's position —
// never null and never a conversion error.
//
// **Validates: Requirements 1.3, 8.15**

/// <summary>
/// An enum used to confirm that an empty non-nullable enum parameter resolves to the enum's zero
/// member (default(T) for enums). Uniquely named to avoid collision with fixtures in sibling
/// test files in the same wave.
/// </summary>
public enum EmptyNonNullableDefaultProps_Color
{
    Red = 0,
    Green = 1,
    Blue = 2,
}

/// <summary>
/// A non-nullable, multi-field value type used to confirm that an empty struct parameter resolves
/// to its all-zero default value rather than null. Uniquely named per the design's per-file
/// fixture-naming guidance.
/// </summary>
public struct EmptyNonNullableDefaultProps_Point
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Label { get; set; }
}

/// <summary>
/// Fixture exposing one single-parameter method per non-nullable declared type. Each method has
/// exactly one Job_Parameter so that supplying a single empty (JSON null) value exercises the
/// empty-value resolution path for that type at Args position 0. Methods are never invoked — they
/// are only reflected over by <see cref="JobArgumentConverter.BuildArgs(MethodInfo, IReadOnlyList{JsonElement})"/>.
/// </summary>
public sealed class EmptyNonNullableDefaultProps_Target
{
    public void TakeInt(int value) { }
    public void TakeLong(long value) { }
    public void TakeShort(short value) { }
    public void TakeByte(byte value) { }
    public void TakeBool(bool value) { }
    public void TakeDouble(double value) { }
    public void TakeFloat(float value) { }
    public void TakeDecimal(decimal value) { }
    public void TakeChar(char value) { }
    public void TakeDateTime(DateTime value) { }
    public void TakeDateTimeOffset(DateTimeOffset value) { }
    public void TakeTimeSpan(TimeSpan value) { }
    public void TakeGuid(Guid value) { }
    public void TakeEnum(EmptyNonNullableDefaultProps_Color value) { }
    public void TakeStruct(EmptyNonNullableDefaultProps_Point value) { }
}

/// <summary>
/// Property test for empty non-nullable parameter resolving to default(T) (Property 24).
///
/// **Validates: Requirements 1.3, 8.15**
/// </summary>
public class EmptyNonNullableDefaultProperties
{
    /// <summary>
    /// The single-Job_Parameter methods under test, each declaring a distinct non-nullable type.
    /// </summary>
    private static readonly MethodInfo[] NonNullableMethods =
        typeof(EmptyNonNullableDefaultProps_Target)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("Take", StringComparison.Ordinal))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>A JSON <c>null</c> element — the canonical encoding of an empty/blank input.</summary>
    private static JsonElement JsonNull => JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>Generates one of the non-nullable single-parameter methods to exercise.</summary>
    private static Arbitrary<MethodInfo> MethodArb =>
        Arb.From(Gen.Elements(NonNullableMethods));

    [Property(MaxTest = 100)]
    public Property EmptyValue_ForNonNullableParameter_ResolvesToDefaultOfThatType()
    {
        return Prop.ForAll(MethodArb, method =>
        {
            var declaredType = method.GetParameters()[0].ParameterType;

            // Sanity guard: this property only concerns non-nullable declared types.
            if (JobArgumentConverter.IsNullableType(declaredType))
            {
                return false.Label($"{declaredType.Name} should not be treated as nullable by the converter");
            }

            // Supply a single EMPTY value (JSON null) for the method's single Job_Parameter.
            var result = JobArgumentConverter.BuildArgs(method, new[] { JsonNull });

            // The empty path is total: it must never fail and never throw (Req 1.3, 8.15).
            if (!result.Success)
            {
                return false.Label(
                    $"[{declaredType.Name}] empty value must not be a conversion error, got: {result.Error}");
            }

            if (result.Args is null || result.Args.Length != 1)
            {
                return false.Label($"[{declaredType.Name}] expected a single-element Args array");
            }

            var slot = result.Args[0];

            // Never null for a non-nullable slot (Req 8.15).
            if (slot is null)
            {
                return false.Label($"[{declaredType.Name}] non-nullable empty slot resolved to null");
            }

            // The slot must equal default(T) — the type's zero value (0, false, Guid.Empty,
            // default(DateTime), the enum's zero member, an all-default struct, etc.).
            var expected = Activator.CreateInstance(declaredType);
            return slot.Equals(expected)
                .Label($"[{declaredType.Name}] expected default(T)={expected} but got {slot}");
        });
    }
}
