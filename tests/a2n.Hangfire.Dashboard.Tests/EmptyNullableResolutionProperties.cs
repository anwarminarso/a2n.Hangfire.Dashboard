using System;
using System.Reflection;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 23: Empty nullable parameter resolves to null.
//
// For any Job_Parameter whose declared type is nullable (a reference type or Nullable<T>), when its
// input is left empty (a JSON null element), building the Args array via
// JobArgumentConverter.BuildArgs yields null at that parameter's position — never default(T) and
// never a conversion error.
//
// **Validates: Requirements 1.3, 8.15**

/// <summary>
/// A reference-type Job_Parameter used to confirm that an empty value for a (nullable) class
/// parameter resolves to <c>null</c>. Uniquely named to avoid collision with fixtures in sibling
/// test files. Never instantiated — only reflected over as a parameter type.
/// </summary>
public sealed class EmptyNullableRes_Payload
{
    public string Name { get; init; }
    public int Count { get; init; }
}

/// <summary>An enum used to confirm that an empty value for a <c>Nullable&lt;enum&gt;</c> resolves to <c>null</c>.</summary>
public enum EmptyNullableRes_Color
{
    Red,
    Green,
    Blue,
}

/// <summary>
/// Fixture exposing single-parameter methods whose declared parameter type IS nullable — covering
/// reference types (<see cref="string"/>, a class) and <see cref="Nullable{T}"/> value types
/// (<c>int?</c>, <c>bool?</c>, <c>DateTime?</c>, <c>Guid?</c>, <c>enum?</c>). Uniquely named to avoid
/// collisions across the wave. Methods are never invoked — only reflected over by the converter.
/// </summary>
public sealed class EmptyNullableRes_Target
{
    public void TakeString(string value) { }
    public void TakeClass(EmptyNullableRes_Payload value) { }
    public void TakeNullableInt(int? value) { }
    public void TakeNullableBool(bool? value) { }
    public void TakeNullableDateTime(DateTime? value) { }
    public void TakeNullableGuid(Guid? value) { }
    public void TakeNullableEnum(EmptyNullableRes_Color? value) { }
}

/// <summary>
/// Property test for empty nullable parameter resolving to null (Property 23).
///
/// **Validates: Requirements 1.3, 8.15**
/// </summary>
public class EmptyNullableResolutionProperties
{
    /// <summary>The names of the single-nullable-parameter fixture methods to exercise.</summary>
    private static readonly string[] NullableMethodNames =
    {
        nameof(EmptyNullableRes_Target.TakeString),
        nameof(EmptyNullableRes_Target.TakeClass),
        nameof(EmptyNullableRes_Target.TakeNullableInt),
        nameof(EmptyNullableRes_Target.TakeNullableBool),
        nameof(EmptyNullableRes_Target.TakeNullableDateTime),
        nameof(EmptyNullableRes_Target.TakeNullableGuid),
        nameof(EmptyNullableRes_Target.TakeNullableEnum),
    };

    /// <summary>A JSON <c>null</c> element representing an empty operator input.</summary>
    private static JsonElement JsonNull => JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>Resolves a fixture method by name.</summary>
    private static MethodInfo Method(string name) =>
        typeof(EmptyNullableRes_Target).GetMethod(name, BindingFlags.Public | BindingFlags.Instance);

    /// <summary>Generates one of the single-nullable-parameter fixture methods.</summary>
    private static Arbitrary<MethodInfo> NullableMethodArb =>
        Arb.From(Gen.Elements(NullableMethodNames).Select(Method));

    [Property(MaxTest = 100)]
    public Property EmptyValue_ForNullableParameter_ResolvesToNull()
    {
        return Prop.ForAll(NullableMethodArb, method =>
        {
            var declaredType = method.GetParameters()[0].ParameterType;

            // Precondition sanity: the fixture parameter must actually be nullable.
            if (!JobArgumentConverter.IsNullableType(declaredType))
                return false.Label($"[{method.Name}] declared type {declaredType.Name} is not nullable");

            // Supply a single EMPTY value (JSON null) for the single Job_Parameter.
            var result = JobArgumentConverter.BuildArgs(method, new[] { JsonNull });

            if (!result.Success)
                return false.Label($"[{method.Name}] expected success on the empty path but got error: {result.Error}");
            if (result.Args is null)
                return false.Label($"[{method.Name}] success but Args was null");
            if (result.Args.Length != 1)
                return false.Label($"[{method.Name}] expected a single-slot Args but got length {result.Args.Length}");
            if (result.Args[0] is not null)
                return false.Label($"[{method.Name}] expected Args[0] == null but got '{result.Args[0]}'");

            return true.ToProperty();
        });
    }
}
