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
    /// Extracts class name and method from raw InvocationData type/method strings.
    /// Type format: "Namespace.ClassName, AssemblyName" or "Namespace.ClassName"
    /// </summary>
    private static string ExtractFromInvocationData(string type, string method)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "(unknown)";

        // Strip assembly info: "Namespace.Class, Assembly" → "Namespace.Class"
        var commaIdx = type.IndexOf(',');
        var fullTypeName = commaIdx > 0 ? type[..commaIdx].Trim() : type.Trim();

        // Get just the class name (last segment after dot)
        var dotIdx = fullTypeName.LastIndexOf('.');
        var className = dotIdx > 0 ? fullTypeName[(dotIdx + 1)..] : fullTypeName;

        if (!string.IsNullOrWhiteSpace(method))
            return $"{className}.{method}";

        return className;
    }
}
