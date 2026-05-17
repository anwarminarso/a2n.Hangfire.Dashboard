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
            // 1. Check JobDisplayNameAttribute
            var jobDisplayNameAttr = job.Method?.GetCustomAttribute<JobDisplayNameAttribute>();
            if (jobDisplayNameAttr != null)
            {
                try
                {
                    // Format supports {0}, {1}, etc. placeholders for job arguments
                    return string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        jobDisplayNameAttr.DisplayName,
                        job.Args?.ToArray() ?? Array.Empty<object>());
                }
                catch (FormatException)
                {
                    // If format fails, return raw display name
                    return jobDisplayNameAttr.DisplayName;
                }
                catch
                {
                    // Fallback to raw display name on any error
                    return jobDisplayNameAttr.DisplayName;
                }
            }

            // 2. Check System.ComponentModel.DisplayNameAttribute
            var displayNameAttr = job.Method?.GetCustomAttribute<System.ComponentModel.DisplayNameAttribute>();
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
