using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.Server;

namespace a2n.Hangfire.Dashboard.Internal;

/// <summary>
/// Pure, side-effect-free helper that maps between the operator-facing view of a job's arguments
/// (the ordered <c>Argument_Values</c> / <c>Parameter_JSON</c> over <c>Job_Parameter</c>s only) and
/// the positional Hangfire <c>Job.Args</c> array (ordered over <em>all</em> declared parameters).
/// </summary>
/// <remarks>
/// <para>
/// Extracting these rules into one place guarantees identical behaviour for the recurring-job and
/// enqueue paths (Requirement 12.4) and makes the logic straightforward to property-test in
/// isolation. See <c>.kiro/specs/job-builder/design.md</c> ("JobArgumentConverter").
/// </para>
/// <para>
/// This file implements parameter classification and the nullable predicate. The
/// JSON validation and serialization members (<c>ValidateParameterJson</c>,
/// <c>ToParameterJson</c>) are added by later tasks.
/// </para>
/// </remarks>
internal static class JobArgumentConverter
{
    /// <summary>
    /// Options used to deserialize <em>structured</em> argument values (enums, arrays, nested
    /// classes) from their <see cref="JsonElement"/> form. Mirrors the case-insensitive, enum-by-name
    /// behaviour Hangfire uses when it reads <c>Args</c> back at activation time.
    /// </summary>
    private static readonly JsonSerializerOptions StructuredOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    /// <summary>
    /// Determines whether <paramref name="p"/> is an <c>Injected_Parameter</c> — one supplied by
    /// Hangfire at execution time rather than by the operator — namely a parameter of type
    /// <see cref="PerformContext"/>, <see cref="System.Threading.CancellationToken"/>, or
    /// <see cref="IJobCancellationToken"/>. Matching is by type, not by name (Requirement 1.2).
    /// </summary>
    /// <param name="p">The declared parameter to classify.</param>
    /// <returns><c>true</c> when the parameter is Hangfire-injected; otherwise <c>false</c>.</returns>
    public static bool IsInjectedParameter(ParameterInfo p)
    {
        var type = p.ParameterType;
        return type == typeof(PerformContext)
            || type == typeof(System.Threading.CancellationToken)
            || type == typeof(IJobCancellationToken);
    }

    /// <summary>
    /// Returns the method's <c>Job_Parameter</c>s — the declared parameters that require an
    /// argument value from the operator — by excluding every <c>Injected_Parameter</c>
    /// (Requirement 1.2). The result preserves the declared parameter order.
    /// </summary>
    /// <param name="m">The target method.</param>
    /// <returns>The declared parameters that are not Hangfire-injected, in declaration order.</returns>
    public static IReadOnlyList<ParameterInfo> JobParameters(MethodInfo m)
    {
        var parameters = m.GetParameters();
        var jobParameters = new List<ParameterInfo>(parameters.Length);

        foreach (var parameter in parameters)
        {
            if (!IsInjectedParameter(parameter))
            {
                jobParameters.Add(parameter);
            }
        }

        return jobParameters;
    }

    /// <summary>
    /// Determines whether <paramref name="declaredType"/> can hold <c>null</c> without conversion —
    /// that is, whether it is a reference type or a <see cref="Nullable{T}"/> value type
    /// (Requirements 1.3, 8.15). This predicate drives empty-value resolution: an empty job-parameter
    /// slot resolves to <c>null</c> when the declared type is nullable, or to <c>default(T)</c> when
    /// it is not.
    /// </summary>
    /// <param name="declaredType">The parameter's declared CLR type.</param>
    /// <returns><c>true</c> when the type is a reference type or <see cref="Nullable{T}"/>; otherwise <c>false</c>.</returns>
    public static bool IsNullableType(Type declaredType)
    {
        ArgumentNullException.ThrowIfNull(declaredType);

        return !declaredType.IsValueType
            || Nullable.GetUnderlyingType(declaredType) is not null;
    }

    /// <summary>
    /// Builds the positional Hangfire <c>Job.Args</c> array for <paramref name="method"/> from the
    /// operator-supplied <c>Job_Parameter</c> values (Requirements 1.1, 1.2, 1.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result is sized to <em>all</em> declared parameters. Each <c>Injected_Parameter</c> slot
    /// holds <c>null</c> so Hangfire fills it at activation time (1.2); each <c>Job_Parameter</c> slot
    /// holds its operator value converted to the declared CLR type (1.1, 1.3).
    /// </para>
    /// <para>
    /// <strong>Empty-value resolution.</strong> A job-parameter value is <em>empty</em> when its
    /// <see cref="JsonElement"/> is JSON <c>null</c> (or <see cref="JsonValueKind.Undefined"/>). An empty
    /// slot is resolved solely from the declared type's nullability and never runs the conversion path:
    /// it becomes <c>null</c> when the declared type <see cref="IsNullableType(Type)">is nullable</see>,
    /// otherwise <c>default(T)</c> (the type's zero value). The empty path is total and never throws.
    /// </para>
    /// <para>
    /// Conversion uses culture-invariant parsing for scalar types and
    /// <see cref="JsonSerializer"/> for structured types (enums, arrays, nested classes), consistent
    /// with how Hangfire reads <c>Args</c> back. A non-empty value that cannot be converted yields a
    /// failed result naming the offending parameter and its expected type (1.4), and the converter
    /// never throws on either path.
    /// </para>
    /// </remarks>
    /// <param name="method">The target method whose declared parameters define the Args layout.</param>
    /// <param name="jobArgValues">
    /// The operator-supplied values ordered over the method's <c>Job_Parameter</c>s only (injected
    /// parameters are not represented here).
    /// </param>
    /// <returns>
    /// A successful <see cref="ArgsBuildResult"/> carrying the positional Args array, or a failed
    /// result identifying the parameter and expected type on a conversion failure.
    /// </returns>
    public static ArgsBuildResult BuildArgs(MethodInfo method, IReadOnlyList<JsonElement> jobArgValues)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(jobArgValues);

        var declared = method.GetParameters();
        var args = new object[declared.Length];
        var jobParamIndex = 0;

        for (var i = 0; i < declared.Length; i++)
        {
            var parameter = declared[i];

            if (IsInjectedParameter(parameter))
            {
                // Injected_Parameter slot: null so Hangfire supplies it at activation (1.2).
                args[i] = null;
                continue;
            }

            var declaredType = parameter.ParameterType;

            // A value may be missing (callers validate counts first; guard defensively) or JSON null —
            // both are the empty case and resolve from nullability, never via conversion.
            var element = jobParamIndex < jobArgValues.Count ? jobArgValues[jobParamIndex] : default;
            jobParamIndex++;

            if (IsEmpty(element))
            {
                args[i] = IsNullableType(declaredType) ? null : CreateDefault(declaredType);
                continue;
            }

            try
            {
                args[i] = ConvertElement(element, declaredType);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or OverflowException
                or InvalidCastException or ArgumentException or NotSupportedException)
            {
                var expectedType = FriendlyTypeName(declaredType);
                var error =
                    $"The value supplied for parameter '{parameter.Name}' could not be converted to {expectedType}.";
                return new ArgsBuildResult(false, null, parameter.Name, expectedType, error);
            }
        }

        return new ArgsBuildResult(true, args, null, null, null);
    }

    /// <summary>
    /// Validates a candidate <c>Parameter_JSON</c> string against <paramref name="method"/>'s
    /// <c>Job_Parameter</c>s (Requirements 2.2–2.7, and the validity notion of 9.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outcome is one of: <see cref="ParameterJsonStatus.Malformed"/> when the string is not
    /// well-formed JSON (2.2); <see cref="ParameterJsonStatus.NotArray"/> when it is well-formed but
    /// its top-level value is not a JSON array (2.3); <see cref="ParameterJsonStatus.CountMismatch"/>,
    /// carrying both the expected and actual counts, when the array length differs from the method's
    /// <c>Job_Parameter</c> count (2.4, 2.5); <see cref="ParameterJsonStatus.ElementTypeError"/>,
    /// naming the offending parameter and its expected type, when an element cannot be converted to
    /// its parameter's declared type (2.6); otherwise <see cref="ParameterJsonStatus.Valid"/>.
    /// </para>
    /// <para>
    /// An empty array is <see cref="ParameterJsonStatus.Valid"/> for a method with zero
    /// <c>Job_Parameter</c>s (2.7). Element convertibility is decided by reusing
    /// <see cref="BuildArgs(MethodInfo, IReadOnlyList{JsonElement})"/>, so empty (JSON <c>null</c>)
    /// elements are always valid via the empty-value rule.
    /// </para>
    /// </remarks>
    /// <param name="json">The candidate Parameter_JSON string.</param>
    /// <param name="method">The selected method whose Job_Parameters define the expected shape.</param>
    /// <returns>A <see cref="ParameterJsonValidation"/> describing the outcome.</returns>
    public static ParameterJsonValidation ValidateParameterJson(string json, MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);

        var expectedCount = JobParameters(method).Count;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json ?? string.Empty);
        }
        catch (JsonException)
        {
            return new ParameterJsonValidation(ParameterJsonStatus.Malformed, expectedCount, 0, null, null);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                return new ParameterJsonValidation(ParameterJsonStatus.NotArray, expectedCount, 0, null, null);
            }

            var elements = new List<JsonElement>(root.GetArrayLength());
            foreach (var element in root.EnumerateArray())
            {
                elements.Add(element);
            }

            if (elements.Count != expectedCount)
            {
                return new ParameterJsonValidation(
                    ParameterJsonStatus.CountMismatch, expectedCount, elements.Count, null, null);
            }

            // Reuse the conversion logic so validity matches what BuildArgs would accept; empty
            // (JSON null) elements are always valid via the empty-value rule (2.7 for the zero-param case).
            var build = BuildArgs(method, elements);
            if (!build.Success)
            {
                return new ParameterJsonValidation(
                    ParameterJsonStatus.ElementTypeError,
                    expectedCount,
                    elements.Count,
                    build.ParameterName,
                    build.ExpectedType);
            }

            return new ParameterJsonValidation(
                ParameterJsonStatus.Valid, expectedCount, elements.Count, null, null);
        }
    }

    /// <summary>
    /// Serializes the operator-supplied <c>Argument_Values</c> (ordered over <paramref name="method"/>'s
    /// <c>Job_Parameter</c>s only) into a canonical <c>Parameter_JSON</c> array string, used for the
    /// read-only JSON mirror and for pre-filling the editor (Requirements 3.1, 9.2, 9.4).
    /// </summary>
    /// <remarks>
    /// The produced array has one element per supplied value, in <c>Job_Parameter</c> order, and uses
    /// the same case-insensitive, enum-by-name serialization options the converter uses when reading
    /// <c>Args</c> back, so the result round-trips through
    /// <see cref="ValidateParameterJson(string, MethodInfo)"/> and
    /// <see cref="BuildArgs(MethodInfo, IReadOnlyList{JsonElement})"/>.
    /// </remarks>
    /// <param name="method">The target method whose Job_Parameters the values correspond to.</param>
    /// <param name="jobArgValues">The values ordered over the method's Job_Parameters only.</param>
    /// <returns>A JSON array string representing the Argument_Values.</returns>
    public static string ToParameterJson(MethodInfo method, IReadOnlyList<object> jobArgValues)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(jobArgValues);

        // Serialize over the supplied Job_Parameter values; each element is written using its runtime
        // type so structured values (enums, arrays, nested classes) round-trip canonically.
        var elements = new object[jobArgValues.Count];
        for (var i = 0; i < jobArgValues.Count; i++)
        {
            elements[i] = jobArgValues[i];
        }

        return JsonSerializer.Serialize(elements, StructuredOptions);
    }

    /// <summary>
    /// Determines whether a job-parameter <see cref="JsonElement"/> represents an <em>empty</em>
    /// value — JSON <c>null</c> or an absent/<see cref="JsonValueKind.Undefined"/> element.
    /// </summary>
    private static bool IsEmpty(JsonElement element) =>
        element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    /// <summary>
    /// Converts a non-empty <see cref="JsonElement"/> to <paramref name="declaredType"/>, using
    /// culture-invariant parsing for scalars and <see cref="JsonSerializer"/> for structured types.
    /// </summary>
    private static object ConvertElement(JsonElement element, Type declaredType)
    {
        var targetType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        return IsScalarType(targetType)
            ? ConvertScalar(element, targetType)
            : element.Deserialize(targetType, StructuredOptions);
    }

    /// <summary>
    /// Converts a scalar value with culture-invariant semantics, tolerating values supplied either as
    /// native JSON tokens or as JSON strings (how blank-able form fields encode their content).
    /// </summary>
    private static object ConvertScalar(JsonElement element, Type targetType)
    {
        if (targetType == typeof(string))
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }

        // Normalize to an invariant string representation so parsing never depends on the host culture.
        var raw = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();

        if (targetType == typeof(bool))
        {
            return element.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? element.GetBoolean()
                : bool.Parse(raw);
        }

        if (targetType == typeof(char))
        {
            return char.Parse(raw);
        }

        if (targetType == typeof(Guid))
        {
            return Guid.Parse(raw, CultureInfo.InvariantCulture);
        }

        if (targetType == typeof(DateTime))
        {
            return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (targetType == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (targetType == typeof(DateOnly))
        {
            return DateOnly.Parse(raw, CultureInfo.InvariantCulture);
        }

        if (targetType == typeof(TimeOnly))
        {
            return TimeOnly.Parse(raw, CultureInfo.InvariantCulture);
        }

        if (targetType == typeof(TimeSpan))
        {
            return TimeSpan.Parse(raw, CultureInfo.InvariantCulture);
        }

        // Remaining scalars are the numeric types: parse culture-invariantly into the declared type.
        // Convert.ChangeType rejects fractional input for integral targets and signals overflow.
        return Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the zero value for a non-nullable value type (e.g. <c>0</c>, <c>false</c>,
    /// <see cref="Guid.Empty"/>, <c>default(DateTime)</c>, the enum's zero member).
    /// </summary>
    private static object CreateDefault(Type declaredType) => Activator.CreateInstance(declaredType);

    /// <summary>
    /// Identifies the scalar types that receive culture-invariant parsing rather than JSON
    /// deserialization. Mirrors the scalar input kinds in the parameter-to-input mapping.
    /// </summary>
    private static bool IsScalarType(Type type) =>
        type == typeof(string)
        || type == typeof(bool)
        || type == typeof(char)
        || type == typeof(byte)
        || type == typeof(sbyte)
        || type == typeof(short)
        || type == typeof(ushort)
        || type == typeof(int)
        || type == typeof(uint)
        || type == typeof(long)
        || type == typeof(ulong)
        || type == typeof(float)
        || type == typeof(double)
        || type == typeof(decimal)
        || type == typeof(Guid)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(DateOnly)
        || type == typeof(TimeOnly)
        || type == typeof(TimeSpan);

    /// <summary>
    /// Produces a human-readable C#-style type name for error messages (e.g. <c>int?</c>,
    /// <c>string[]</c>, <c>List&lt;int&gt;</c>).
    /// </summary>
    private static string FriendlyTypeName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return FriendlyTypeName(underlying) + "?";
        }

        if (type.IsArray)
        {
            return FriendlyTypeName(type.GetElementType()) + "[]";
        }

        if (type.IsGenericType)
        {
            var name = type.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0)
            {
                name = name[..tick];
            }

            var arguments = string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName));
            return $"{name}<{arguments}>";
        }

        return type.Name;
    }
}
