using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;

namespace a2n.Hangfire.Dashboard.Helpers;

/// <summary>
/// Extracts a human-readable job name from Hangfire job data.
/// Priority chain (same as original Hangfire dashboard):
/// 1. JobDisplayNameAttribute (custom display name with format support)
/// 2. System.ComponentModel.DisplayNameAttribute
/// 3. Type.Method fallback
/// 4. InvocationData raw strings (when assembly not available)
/// </summary>
public static class JobNameHelper
{
    /// <summary>
    /// Gets a display name from a Job object, or falls back to InvocationData if Job is null.
    /// </summary>
    public static string GetDisplayName(Job job, InvocationData invocationData)
    {
        // Best case: Job is resolved, check for display name attributes
        if (job is not null)
        {
            // 1. Check JobDisplayNameAttribute — directly on the method, then (defensive) on the
            //    interface method it implements. A job stored against a concrete implementation of an
            //    interface job contract carries no method-level attribute, because .NET does not
            //    inherit interface-member attributes onto implementing methods. Falling back to the
            //    interface keeps the display name rendering for such jobs (and legacy data), matching
            //    how the job should be stored (against the interface) for DI-dispatched scenarios.
            var jobDisplayNameAttr = job.Method?.GetCustomAttribute<JobDisplayNameAttribute>()
                                     ?? FindInterfaceAttribute<JobDisplayNameAttribute>(job.Type, job.Method);
            if (jobDisplayNameAttr != null)
            {
                try
                {
                    // Delegate to the attribute's own Format(): this matches the original Hangfire
                    // dashboard (HtmlHelper.JobName) exactly, honoring ResourceType localization in
                    // addition to {0}/{1} argument placeholders. The DashboardContext parameter is
                    // unused by Format(), so passing null is safe.
                    return jobDisplayNameAttr.Format(null, job);
                }
                catch
                {
                    // Fallback to raw display name on any error (mirrors original behaviour).
                    return jobDisplayNameAttr.DisplayName;
                }
            }

            // 2. Check System.ComponentModel.DisplayNameAttribute (direct, then interface fallback).
            var displayNameAttr = job.Method?.GetCustomAttribute<System.ComponentModel.DisplayNameAttribute>()
                                  ?? FindInterfaceAttribute<System.ComponentModel.DisplayNameAttribute>(job.Type, job.Method);
            if (displayNameAttr != null && !string.IsNullOrEmpty(displayNameAttr.DisplayName))
            {
                try
                {
                    return string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        displayNameAttr.DisplayName,
                        job.Args?.ToArray() ?? Array.Empty<object>());
                }
                catch (FormatException)
                {
                    return displayNameAttr.DisplayName;
                }
            }

            // 3. Type.Method fallback
            return $"{job.Type.Name}.{job.Method.Name}";
        }

        // 4. Fallback: extract from InvocationData (raw strings, assembly may not be loaded)
        if (invocationData is not null)
            return ExtractFromInvocationData(invocationData.Type, invocationData.Method);

        return "(unknown)";
    }

    /// <summary>
    /// Looks for an attribute of type <typeparamref name="T"/> on the interface method that
    /// <paramref name="method"/> implements on <paramref name="type"/>. Returns the first match, or
    /// <c>null</c> when <paramref name="type"/> is itself an interface, implements no matching
    /// interface method, or the interface method is not decorated.
    /// </summary>
    /// <remarks>
    /// Used as a defensive fallback for display-name resolution: interface-member attributes are not
    /// inherited by implementing methods in .NET, so a job stored against a concrete implementation
    /// would otherwise lose the interface contract's <c>[JobDisplayName]</c>.
    /// </remarks>
    private static T FindInterfaceAttribute<T>(Type type, MethodInfo method) where T : Attribute
    {
        if (type is null || method is null || type.IsInterface)
            return null;

        foreach (var iface in type.GetInterfaces())
        {
            InterfaceMapping map;
            try
            {
                map = type.GetInterfaceMap(iface);
            }
            catch
            {
                // Some type/interface combinations don't support mapping; skip them.
                continue;
            }

            for (var i = 0; i < map.TargetMethods.Length; i++)
            {
                var target = map.TargetMethods[i];
                if (target.MetadataToken == method.MetadataToken
                    && EqualityComparer<Module>.Default.Equals(target.Module, method.Module))
                {
                    var attr = map.InterfaceMethods[i].GetCustomAttribute<T>();
                    if (attr != null)
                        return attr;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// <c>Type.Method</c>, ignoring display-name attributes, as search results name a job: from the
    /// job when its type is loaded, otherwise from the invocation data, so a job whose assembly the
    /// dashboard doesn't reference still has a name (issue #70). Null when neither is available.
    /// </summary>
    internal static string GetTypeAndMethod(Job job, InvocationData invocationData)
    {
        var (typeName, methodName) = GetTypeAndMethodNames(job, invocationData);
        if (typeName is null)
            return null;
        return string.IsNullOrWhiteSpace(methodName) ? typeName : $"{typeName}.{methodName}";
    }

    /// <summary>
    /// The job's class name (as <see cref="Type.Name"/> gives it) and method name, from the job or,
    /// when its type isn't loaded, from the invocation data. Nulls when neither is available.
    /// </summary>
    internal static (string TypeName, string MethodName) GetTypeAndMethodNames(Job job, InvocationData invocationData)
    {
        if (job?.Type is not null && job.Method is not null)
            return (job.Type.Name, job.Method.Name);

        if (invocationData is null || string.IsNullOrWhiteSpace(invocationData.Type))
            return (null, null);

        return (ClassNameOf(invocationData.Type), invocationData.Method);
    }

    /// <summary>
    /// Extracts class name and method from raw InvocationData type/method strings.
    /// Type format: "Namespace.ClassName, AssemblyName" or "Namespace.ClassName"
    /// </summary>
    private static string ExtractFromInvocationData(string type, string method)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "(unknown)";

        var className = ClassNameOf(type);

        if (!string.IsNullOrWhiteSpace(method))
            return $"{className}.{method}";

        return className;
    }

    private static readonly char[] TypeNameEnd = { ',', '[' };
    private static readonly char[] TypeNameSeparators = { '.', '+' };

    /// <summary>
    /// The class name in an assembly-qualified type name, as <see cref="Type.Name"/> gives it:
    /// <c>"Ns.Outer+Inner`1[[System.String, mscorlib]], App"</c> → <c>"Inner`1"</c>. The assembly and
    /// generic arguments are cut first, since they contain commas and dots of their own.
    /// </summary>
    private static string ClassNameOf(string type)
    {
        var end = type.IndexOfAny(TypeNameEnd);
        var fullTypeName = (end > 0 ? type[..end] : type).Trim();
        var start = fullTypeName.LastIndexOfAny(TypeNameSeparators);
        return start >= 0 ? fullTypeName[(start + 1)..] : fullTypeName;
    }
}
