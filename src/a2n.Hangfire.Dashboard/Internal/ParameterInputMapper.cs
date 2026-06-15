using System;

namespace a2n.Hangfire.Dashboard.Internal;

/// <summary>
/// Pure, testable mapping from a declared CLR <see cref="Type"/> to the
/// <see cref="ParameterInputKind"/> the Parameter Builder should render for that parameter
/// (Req 8.1–8.12). The <em>mapping</em> is the unit under test; the actual Bootstrap control
/// rendering is verified separately with bunit.
/// </summary>
/// <remarks>
/// <para>
/// Mapping rules (see design.md, "ParameterInputKind" table):
/// <list type="bullet">
///   <item><c>string</c> → <see cref="ParameterInputKind.Text"/></item>
///   <item>integral (<c>byte/sbyte/short/ushort/int/uint/long/ulong</c>) → <see cref="ParameterInputKind.Integer"/></item>
///   <item>floating (<c>float/double/decimal</c>) → <see cref="ParameterInputKind.Float"/></item>
///   <item><c>DateOnly</c> → <see cref="ParameterInputKind.Date"/>, <c>TimeOnly</c> → <see cref="ParameterInputKind.Time"/>,
///         <c>DateTime/DateTimeOffset</c> → <see cref="ParameterInputKind.DateTime"/></item>
///   <item><c>Guid</c> → <see cref="ParameterInputKind.Guid"/></item>
///   <item><c>bool</c> → <see cref="ParameterInputKind.Bool"/>, <c>bool?</c> → <see cref="ParameterInputKind.NullableBool"/></item>
///   <item>enum → <see cref="ParameterInputKind.EnumSingle"/>, <c>[Flags]</c> enum → <see cref="ParameterInputKind.EnumFlags"/></item>
///   <item>array of a supported scalar (<c>T[]</c>) → <see cref="ParameterInputKind.ScalarArray"/></item>
///   <item>public class within nesting depth ≤ 5 → <see cref="ParameterInputKind.NestedObject"/></item>
///   <item>anything else (depth &gt; 5, or an unsupported type) → <see cref="ParameterInputKind.Json"/></item>
/// </list>
/// </para>
/// <para>
/// For nullable value types the <see cref="Nullable{T}"/> wrapper is unwrapped before mapping, so a
/// <c>Nullable&lt;T&gt;</c> maps the same as its underlying <c>T</c> — with the sole exception of
/// <c>bool?</c>, which maps to <see cref="ParameterInputKind.NullableBool"/> (its tri-state control).
/// </para>
/// </remarks>
internal static class ParameterInputMapper
{
    /// <summary>Maximum nesting depth for which a public class renders as a sub-form (Req 8.10, 8.11).</summary>
    internal const int MaxNestedDepth = 5;

    /// <summary>
    /// Maps a declared parameter (or nested-property) <paramref name="type"/> to its
    /// <see cref="ParameterInputKind"/>.
    /// </summary>
    /// <param name="type">The declared CLR type to classify.</param>
    /// <param name="depth">
    /// The 1-based nesting depth at which <paramref name="type"/> appears. Top-level parameters are
    /// at depth 1; the Parameter Builder increments the depth as it recurses into a nested class's
    /// public writable properties. A public class at depth &gt; <see cref="MaxNestedDepth"/> falls
    /// back to a JSON input rather than a further nested form (Req 8.11).
    /// </param>
    /// <returns>The <see cref="ParameterInputKind"/> to render for <paramref name="type"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    public static ParameterInputKind Map(Type type, int depth = 1)
    {
        ArgumentNullException.ThrowIfNull(type);

        // bool? has a dedicated tri-state control; do NOT unwrap it like other nullable value types.
        if (type == typeof(bool?))
        {
            return ParameterInputKind.NullableBool;
        }

        // Unwrap Nullable<T> so a nullable value type maps the same as its underlying type.
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string))
        {
            return ParameterInputKind.Text;
        }

        if (type == typeof(bool))
        {
            return ParameterInputKind.Bool;
        }

        if (IsIntegral(type))
        {
            return ParameterInputKind.Integer;
        }

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return ParameterInputKind.Float;
        }

        if (type == typeof(DateOnly))
        {
            return ParameterInputKind.Date;
        }

        if (type == typeof(TimeOnly))
        {
            return ParameterInputKind.Time;
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return ParameterInputKind.DateTime;
        }

        if (type == typeof(Guid))
        {
            return ParameterInputKind.Guid;
        }

        if (type.IsEnum)
        {
            return type.IsDefined(typeof(FlagsAttribute), inherit: false)
                ? ParameterInputKind.EnumFlags
                : ParameterInputKind.EnumSingle;
        }

        // Any array is handled here and never falls through to the nested-object path.
        // Only a rank-1 array of a supported scalar element maps to ScalarArray; every other
        // array (multi-dimensional arrays such as int[,], or rank-1 arrays of non-scalar
        // elements) maps to Json (Req 8.12).
        if (type.IsArray)
        {
            if (type.GetArrayRank() == 1)
            {
                var element = type.GetElementType();
                if (element is not null && IsSupportedScalar(element))
                {
                    return ParameterInputKind.ScalarArray;
                }
            }

            return ParameterInputKind.Json;
        }

        // A public, instantiable class within the nesting-depth budget renders a nested sub-form;
        // beyond the budget (or for any other unsupported type) we fall back to a JSON input.
        if (IsNestedObjectCandidate(type) && depth <= MaxNestedDepth)
        {
            return ParameterInputKind.NestedObject;
        }

        return ParameterInputKind.Json;
    }

    /// <summary>
    /// True for the integral CLR types: <c>byte/sbyte/short/ushort/int/uint/long/ulong</c> (Req 8.3).
    /// </summary>
    private static bool IsIntegral(Type type) =>
        type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong);

    /// <summary>
    /// A class is a nested-object candidate when it is a concrete, instantiable, publicly visible
    /// reference type. <c>string</c> and arrays are excluded earlier; abstract/static classes are
    /// not candidates because they cannot be instantiated by the nested-object editor (Req 8.10).
    /// </summary>
    private static bool IsNestedObjectCandidate(Type type) =>
        type.IsClass && type.IsVisible && !type.IsAbstract;

    /// <summary>
    /// True when <paramref name="elementType"/> is one of the supported scalar types that a
    /// <see cref="ParameterInputKind.ScalarArray"/> control can edit (Req 8.9): the scalar leaf
    /// kinds (text, integer, float, date/time/datetime, guid, bool/bool?, enum/flags). Composite
    /// kinds (nested object, JSON, or arrays themselves) are not supported scalars.
    /// </summary>
    private static bool IsSupportedScalar(Type elementType)
    {
        return Map(elementType) switch
        {
            ParameterInputKind.Text or
            ParameterInputKind.Integer or
            ParameterInputKind.Float or
            ParameterInputKind.Date or
            ParameterInputKind.Time or
            ParameterInputKind.DateTime or
            ParameterInputKind.Guid or
            ParameterInputKind.Bool or
            ParameterInputKind.NullableBool or
            ParameterInputKind.EnumSingle or
            ParameterInputKind.EnumFlags => true,
            _ => false,
        };
    }
}
