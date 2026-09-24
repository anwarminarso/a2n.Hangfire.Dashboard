using System.Collections;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Hangfire.Common;
using Hangfire.Storage;

namespace a2n.Hangfire.Dashboard.Helpers;

/// <summary>
/// Renders a job's invocation as a C#-like method call with its arguments, mirroring the original
/// Hangfire dashboard (<c>Hangfire.Dashboard.JobMethodCallRenderer</c>).
/// <para>
/// That renderer is <c>internal</c> to Hangfire.Core and therefore cannot be reused, so the
/// behaviour is reproduced here: type-aware argument formatting (enum, numeric, bool, char, string,
/// <see cref="TimeSpan"/>/<see cref="DateTime"/>, <see cref="CancellationToken"/>, JSON fallback),
/// per-argument parameter-name tooltips, collection expansion, a size cap, and argument-per-line
/// wrapping for long calls. Span classes use this dashboard's <c>code-*</c> naming.
/// </para>
/// <para>
/// Unlike the original, a job whose <see cref="Job"/> could not be resolved (the dashboard host does
/// not reference the job's assembly) still renders a best-effort call built from the raw
/// <see cref="InvocationData"/> instead of showing nothing.
/// </para>
/// </summary>
public static class JobArgumentsRenderer
{
    /// <summary>
    /// Arguments serialized to more than this many characters are replaced with a placeholder
    /// instead of being rendered. Mirrors <c>JobMethodCallRenderer.MaxArgumentToRenderSize</c>.
    /// </summary>
    public const int MaxArgumentToRenderSize = 4096;

    /// <summary>
    /// Once the rendered arguments exceed this combined length, each argument is placed on its own
    /// line. Mirrors the original renderer's <c>splitStringMinLength</c>.
    /// </summary>
    private const int SplitStringMinLength = 100;

    private static readonly Regex GenericArityPattern = new(@"`\d+", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
    private static readonly string[] EmptyArguments = Array.Empty<string>();

    // ─── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the full method-call snippet (HTML) for the job details page, including the job id
    /// comment, the <c>using</c> directive, the activation line and the argument list.
    /// </summary>
    /// <param name="job">The resolved job, or <c>null</c> when the target method is unavailable.</param>
    /// <param name="invocationData">Raw invocation data; used for the original argument payload and
    /// as the fallback source when <paramref name="job"/> is <c>null</c>.</param>
    /// <param name="jobId">The job identifier, rendered as a leading comment.</param>
    public static string RenderMethodCall(Job job, InvocationData invocationData, string jobId)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrEmpty(jobId))
        {
            builder.Append(Span("code-comment", "// Id: #" + Encode(jobId)));
            builder.AppendLine();
        }

        if (job is null)
        {
            AppendUnresolvedCall(builder, invocationData);
            return builder.ToString();
        }

        AppendResolvedCall(builder, job, invocationData);
        return builder.ToString();
    }

    /// <summary>
    /// Renders just the comma-separated argument list (HTML), without the enclosing parentheses and
    /// without line wrapping. Suitable for inline use next to a job name.
    /// </summary>
    public static string RenderArguments(Job job, InvocationData invocationData)
    {
        if (job is null)
            return string.Join(", ", GetRawArguments(null, invocationData).Select(a => Span("code-string", Encode(a))));

        var parameters = job.Method?.GetParameters() ?? Array.Empty<ParameterInfo>();
        var rendered = RenderArgumentValues(job, invocationData, parameters, out _);
        return string.Join(", ", rendered);
    }

    /// <summary>
    /// Returns the job's arguments as plain text in their stored (JSON) form — the same text the
    /// <c>args:</c> search matches against — or an empty string when the job takes no arguments.
    /// </summary>
    public static string GetArgumentsText(Job job, InvocationData invocationData)
    {
        var raw = GetRawArguments(job, invocationData);

        // A JSON null element deserializes to a null string. The rendered call prints it as the
        // null keyword, so the text form has to as well — otherwise a list row shows an empty slot.
        return raw.Count == 0 ? "" : string.Join(", ", raw.Select(a => a ?? "null"));
    }

    /// <summary>
    /// Returns a compact parenthesised argument summary for job list rows, e.g.
    /// <c>("user@example.com", 42)</c>, truncated to <paramref name="maxLength"/>. Returns an empty
    /// string when the job takes no arguments.
    /// </summary>
    public static string GetArgumentsSummary(Job job, InvocationData invocationData, int maxLength = 96)
        => SummarizeArgumentsText(GetArgumentsText(job, invocationData), maxLength);

    /// <summary>
    /// Formats argument text already obtained from <see cref="GetArgumentsText"/> as a compact
    /// parenthesised summary, so callers that need both forms only read the arguments once.
    /// </summary>
    public static string SummarizeArgumentsText(string argumentsText, int maxLength = 96)
    {
        // Whitespace-only text would collapse to nothing below, leaving an empty "()".
        if (string.IsNullOrWhiteSpace(argumentsText))
            return "";

        // Collapse newlines/tabs so multi-line JSON payloads stay on a single row.
        var text = WhitespacePattern.Replace(argumentsText, " ").Trim();

        if (maxLength > 0 && text.Length > maxLength)
            text = text[..maxLength] + "…";

        return "(" + text + ")";
    }

    // ─── Rendering: resolved job ───────────────────────────────────────────────

    private static void AppendResolvedCall(StringBuilder builder, Job job, InvocationData invocationData)
    {
        if (!string.IsNullOrEmpty(job.Type.Namespace))
        {
            builder.Append(Span("code-keyword", "using"));
            builder.Append(' ');
            builder.Append(Span("code-namespace", Encode(job.Type.Namespace)));
            builder.Append(';');
            builder.AppendLine();
            builder.AppendLine();
        }

        string serviceName = null;

        if (!job.Method.IsStatic)
        {
            serviceName = GetServiceName(job.Type);

            builder.Append(Span("code-keyword", "var"));
            builder.Append($" {Encode(serviceName)} = Activate&lt;{Span("code-type", Encode(FormatTypeName(job.Type)))}&gt;();");
            builder.AppendLine();
        }

        if (IsAwaitable(job.Method))
        {
            builder.Append(Span("code-keyword", "await"));
            builder.Append(' ');
        }

        builder.Append(!job.Method.IsStatic
            ? Encode(serviceName)
            : Span("code-type", Encode(FormatTypeName(job.Type))));

        builder.Append('.');
        builder.Append(Span("code-method", Encode(job.Method.Name)));

        if (job.Method.IsGenericMethod)
        {
            var genericArgumentTypes = job.Method.GetGenericArguments()
                .Select(x => Span("code-type", Encode(FormatTypeName(x))))
                .ToArray();

            builder.Append($"&lt;{string.Join(", ", genericArgumentTypes)}&gt;");
        }

        builder.Append('(');

        var parameters = job.Method.GetParameters();
        var renderedArguments = RenderArgumentValues(job, invocationData, parameters, out var totalLength);

        AppendArgumentList(builder, renderedArguments, parameters.Select(p => p.Name).ToArray(), totalLength);

        builder.Append(");");
    }

    /// <summary>
    /// Renders each argument using a type-aware renderer, falling back to <c>&lt;NO VALUE&gt;</c>
    /// when the stored payload has fewer arguments than the method has parameters.
    /// </summary>
    private static List<string> RenderArgumentValues(
        Job job, InvocationData invocationData, ParameterInfo[] parameters, out int totalLength)
    {
        var arguments = GetRawArguments(job, invocationData);
        var rendered = new List<string>(parameters.Length);
        totalLength = 0;

        for (var i = 0; i < parameters.Length; i++)
        {
            if (i >= arguments.Count)
            {
                rendered.Add(Encode("<NO VALUE>"));
                continue;
            }

            var parameter = parameters[i];
            var argument = arguments[i];

            if (argument != null && argument.Length > MaxArgumentToRenderSize)
            {
                rendered.Add(Encode("<VALUE IS TOO BIG>"));
                continue;
            }

            var renderedArgument = RenderArgument(parameter.ParameterType, argument);
            rendered.Add(renderedArgument);
            totalLength += renderedArgument.Length;
        }

        return rendered;
    }

    private static string RenderArgument(Type parameterType, string argument)
    {
        var enumerableArgument = GetEnumerableGenericArgument(parameterType);

        object argumentValue;
        var isJson = true;

        try
        {
            argumentValue = SerializationHelper.Deserialize(argument, parameterType, SerializationOption.User);
        }
        catch (Exception)
        {
            // Arguments stored by older Hangfire versions use TypeConverter rather than JSON; show
            // the raw value as-is in that case.
            argumentValue = argument;
            isJson = false;
        }

        // A non-JSON payload is shown verbatim, so it must not be walked as a collection — the raw
        // string would otherwise be enumerated character by character.
        if (enumerableArgument is null || argumentValue is null || !isJson
            || argumentValue is not IEnumerable enumerable)
        {
            var renderer = ArgumentRenderer.GetRenderer(parameterType);
            return renderer.Render(isJson, argumentValue?.ToString(), argument);
        }

        var renderedItems = new List<string>();
        foreach (var item in enumerable)
        {
            var renderer = ArgumentRenderer.GetRenderer(enumerableArgument);
            renderedItems.Add(renderer.Render(
                isJson,
                item?.ToString(),
                SerializationHelper.Serialize(item, SerializationOption.User)));
        }

        return string.Format(
            "{0}{1} {{ {2} }}",
            Span("code-keyword", "new"),
            parameterType.IsArray ? " []" : "",
            string.Join(", ", renderedItems));
    }

    // ─── Rendering: unresolved job (assembly not available to the dashboard) ───

    /// <summary>
    /// Renders a best-effort call from raw <see cref="InvocationData"/> when the target method could
    /// not be resolved. Parameter names are unavailable, so the declared parameter types are used as
    /// tooltips instead.
    /// </summary>
    private static void AppendUnresolvedCall(StringBuilder builder, InvocationData invocationData)
    {
        if (invocationData is null)
        {
            builder.Append($"<em>{Encode("Can not find the target method.")}</em>");
            return;
        }

        builder.Append(Span("code-comment",
            Encode("// Can not find the target method — the dashboard does not reference the job's assembly.")));
        builder.AppendLine();

        var (namespaceName, typeName) = SplitTypeName(invocationData.Type);

        if (!string.IsNullOrEmpty(namespaceName))
        {
            builder.Append(Span("code-keyword", "using"));
            builder.Append(' ');
            builder.Append(Span("code-namespace", Encode(namespaceName)));
            builder.Append(';');
            builder.AppendLine();
        }

        builder.AppendLine();

        builder.Append(Span("code-type", Encode(typeName)));
        builder.Append('.');
        builder.Append(Span("code-method", Encode(invocationData.Method ?? "")));
        builder.Append('(');

        var arguments = GetRawArguments(null, invocationData);
        var parameterTypes = DeserializeStringArray(invocationData.ParameterTypes);

        var rendered = new List<string>(arguments.Count);
        var titles = new List<string>(arguments.Count);
        var totalLength = 0;

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var value = argument != null && argument.Length > MaxArgumentToRenderSize
                ? Encode("<VALUE IS TOO BIG>")
                : Span("code-string", Encode(argument ?? "null"));

            rendered.Add(value);
            totalLength += value.Length;
            titles.Add(i < parameterTypes.Count ? SimpleTypeName(parameterTypes[i]) : null);
        }

        AppendArgumentList(builder, rendered, titles, totalLength);

        builder.Append(");");
    }

    // ─── Shared helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Appends the rendered arguments, wrapping one per line once they are long enough combined, and
    /// attaching <paramref name="titles"/> as hover tooltips.
    /// </summary>
    private static void AppendArgumentList(
        StringBuilder builder, IReadOnlyList<string> rendered, IReadOnlyList<string> titles, int totalLength)
    {
        for (var i = 0; i < rendered.Count; i++)
        {
            var tooltipPosition = "top";

            if (totalLength > SplitStringMinLength)
            {
                builder.AppendLine();
                builder.Append("    ");
                tooltipPosition = "left";
            }
            else if (i > 0)
            {
                builder.Append(' ');
            }

            var title = i < titles.Count ? titles[i] : null;
            if (!string.IsNullOrEmpty(title))
                builder.Append($"<span title=\"{Encode(title)}\" data-bs-placement=\"{tooltipPosition}\">");
            else
                builder.Append("<span>");

            builder.Append(rendered[i]);
            builder.Append("</span>");

            if (i < rendered.Count - 1)
                builder.Append(',');
        }
    }

    /// <summary>
    /// Returns the job's arguments in their stored form. The original
    /// <see cref="InvocationData.Arguments"/> payload is preferred (it is the exact stored text);
    /// otherwise the arguments are re-serialized from the resolved job.
    /// </summary>
    private static IReadOnlyList<string> GetRawArguments(Job job, InvocationData invocationData)
    {
        var fromInvocationData = DeserializeStringArray(invocationData?.Arguments);
        if (fromInvocationData.Count > 0)
            return fromInvocationData;

        if (job is null)
            return EmptyArguments;

        try
        {
#pragma warning disable 618 // Job.Arguments is the only way to get the serialized form from a Job.
            return job.Arguments ?? EmptyArguments;
#pragma warning restore 618
        }
        catch (Exception)
        {
            return EmptyArguments;
        }
    }

    private static IReadOnlyList<string> DeserializeStringArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return EmptyArguments;

        try
        {
            return SerializationHelper.Deserialize<string[]>(json) ?? EmptyArguments;
        }
        catch (Exception)
        {
            return EmptyArguments;
        }
    }

    /// <summary>
    /// Derives the local variable name used for the activated service, matching the original
    /// dashboard: the type name without generic arity, camel-cased, with the leading <c>I</c>
    /// stripped for interfaces.
    /// </summary>
    private static string GetServiceName(Type type)
    {
        var name = GenericArityPattern.Replace(type.Name, "");

        if (name.Length == 0)
            return "service";

        if (type.IsInterface && name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]))
            name = name[1..];

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Formats a type the way it would be written in C#: no namespace, nested types joined with a
    /// dot, generic arguments spelled out. Equivalent to Hangfire's internal
    /// <c>ToGenericTypeString()</c>.
    /// </summary>
    private static string FormatTypeName(Type type)
    {
        if (type is null)
            return "";

        if (type.IsArray)
            return FormatTypeName(type.GetElementType()) + "[]";

        var name = NameWithoutNamespace(type);

        if (!type.IsGenericType)
            return name;

        var arguments = type.GetGenericArguments().Select(FormatTypeName);
        return $"{GenericArityPattern.Replace(name, "")}<{string.Join(", ", arguments)}>";
    }

    private static string NameWithoutNamespace(Type type)
    {
        var fullName = type.FullName;
        if (string.IsNullOrEmpty(fullName))
            return type.Name;

        if (!string.IsNullOrEmpty(type.Namespace) && fullName.StartsWith(type.Namespace + ".", StringComparison.Ordinal))
            fullName = fullName[(type.Namespace.Length + 1)..];

        // Nested types are separated by '+' in reflection names.
        return fullName.Replace('+', '.');
    }

    /// <summary>
    /// Splits an <see cref="InvocationData.Type"/> string
    /// ("Namespace.Class, Assembly") into its namespace and class name.
    /// </summary>
    private static (string Namespace, string TypeName) SplitTypeName(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return ("", "(unknown)");

        var commaIdx = type.IndexOf(',');
        var fullTypeName = (commaIdx > 0 ? type[..commaIdx] : type).Trim();

        var dotIdx = fullTypeName.LastIndexOf('.');
        return dotIdx > 0
            ? (fullTypeName[..dotIdx], fullTypeName[(dotIdx + 1)..])
            : ("", fullTypeName);
    }

    private static string SimpleTypeName(string type)
    {
        var (_, typeName) = SplitTypeName(type);
        return typeName;
    }

    /// <summary>
    /// Returns true when the method is <c>async</c> or returns a task-like value, so the call should
    /// be prefixed with <c>await</c>. Mirrors Hangfire's internal <c>IsTaskLike()</c> check.
    /// </summary>
    private static bool IsAwaitable(MethodInfo method)
    {
        if (method is null)
            return false;

        if (method.GetCustomAttribute<AsyncStateMachineAttribute>() != null)
            return true;

        var returnType = method.ReturnType;

        if (returnType.IsPrimitive)
            return false;

        if (typeof(Task).IsAssignableFrom(returnType))
            return true;

        return returnType.FullName != null
               && returnType.FullName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal);
    }

    private static Type GetEnumerableGenericArgument(Type type)
    {
        if (type == typeof(string))
            return null;

        return type.GetInterfaces()
            .Concat(type.IsInterface ? new[] { type } : Array.Empty<Type>())
            .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(x => x.GetGenericArguments()[0])
            .FirstOrDefault();
    }

    private static string Span(string cssClass, string value) => $"<span class=\"{cssClass}\">{value}</span>";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    // ─── Type-aware argument renderers ────────────────────────────────────────

    /// <summary>
    /// Renders a single argument value according to its declared type, reproducing the original
    /// dashboard's <c>ArgumentRenderer</c>.
    /// </summary>
    private sealed class ArgumentRenderer
    {
        private string _enclosingString;
        private Type _deserializationType;
        private Func<string, string> _valueRenderer;

        private ArgumentRenderer()
        {
            _enclosingString = "\"";
            _valueRenderer = value => value is null ? Span("code-keyword", "null") : Span("code-string", value);
        }

        public string Render(bool isJson, string deserializedValue, string rawValue)
        {
            if (rawValue is null)
                return Span("code-keyword", "null");

            var builder = new StringBuilder();

            if (_deserializationType != null)
            {
                builder.Append(isJson ? "FromJson" : "Deserialize");
                builder.Append("&lt;")
                    .Append(Span("code-type", Encode(_deserializationType.Name)))
                    .Append("&gt;")
                    .Append('(');
                builder.Append(Span("code-string", Encode("\"" + rawValue.Replace("\"", "\\\"") + "\"")));
                builder.Append(')');
            }
            else
            {
                if (deserializedValue != null)
                    builder.Append(_enclosingString);

                builder.Append(_valueRenderer(Encode(deserializedValue)));

                if (deserializedValue != null)
                    builder.Append(_enclosingString);
            }

            return builder.ToString();
        }

        public static ArgumentRenderer GetRenderer(Type type)
        {
            if (type.IsEnum)
            {
                return new ArgumentRenderer
                {
                    _enclosingString = string.Empty,
                    _valueRenderer = value => $"{Span("code-type", Encode(type.Name))}.{value}"
                };
            }

            if (IsNumericType(type))
            {
                return new ArgumentRenderer
                {
                    _enclosingString = string.Empty,
                    _valueRenderer = value => Span("code-number", value)
                };
            }

            if (type == typeof(bool))
            {
                return new ArgumentRenderer
                {
                    _enclosingString = string.Empty,
                    _valueRenderer = value => Span("code-keyword", value.ToLowerInvariant())
                };
            }

            if (type == typeof(char))
                return new ArgumentRenderer { _enclosingString = "'" };

            if (type == typeof(string) || type == typeof(object))
                return new ArgumentRenderer { _enclosingString = "\"" };

            if (type == typeof(TimeSpan) || type == typeof(DateTime) || type == typeof(DateTimeOffset))
            {
                return new ArgumentRenderer
                {
                    _enclosingString = string.Empty,
                    _valueRenderer = value =>
                        $"{Span("code-type", Encode(type.Name))}.Parse({Span("code-string", $"\"{value}\"")})"
                };
            }

            if (type == typeof(CancellationToken))
            {
                return new ArgumentRenderer
                {
                    _enclosingString = string.Empty,
                    _valueRenderer = _ => $"{Span("code-type", nameof(CancellationToken))}.None"
                };
            }

            return new ArgumentRenderer { _deserializationType = type };
        }

        private static bool IsNumericType(Type type)
        {
            if (type is null)
                return false;

            if (type == typeof(byte) || type == typeof(sbyte)
                || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint)
                || type == typeof(long) || type == typeof(ulong)
                || type == typeof(float) || type == typeof(double)
                || type == typeof(decimal))
            {
                return true;
            }

            var underlying = Nullable.GetUnderlyingType(type);
            return underlying != null && IsNumericType(underlying);
        }
    }
}
