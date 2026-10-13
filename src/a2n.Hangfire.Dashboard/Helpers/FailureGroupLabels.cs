using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Helpers;

/// <summary>
/// How an exception group is named, so the Failed page's Exception filter and the Exception filter
/// on Search label a group the same way, with the short type names of Analytics → Failures and the
/// retry summary.
/// </summary>
internal static class FailureGroupLabels
{
    /// <summary>The short exception type, as <c>Outer → Inner</c> when one exception wraps another.</summary>
    public static string ShortTypeName(FailureGroupDto group)
    {
        var outer = string.IsNullOrEmpty(group.ExceptionType) ? "Unknown" : ExceptionTypeNames.Short(group.ExceptionType);
        return string.IsNullOrEmpty(group.InnerExceptionType)
            ? outer
            : $"{outer} → {ExceptionTypeNames.Short(group.InnerExceptionType)}";
    }

    /// <summary>The full exception type names, for tooltips.</summary>
    public static string FullTypeName(FailureGroupDto group)
    {
        var outer = string.IsNullOrEmpty(group.ExceptionType) ? "Unknown" : group.ExceptionType;
        return string.IsNullOrEmpty(group.InnerExceptionType) ? outer : $"{outer} → {group.InnerExceptionType}";
    }

    /// <summary>
    /// The group's number of failed jobs, with a "+" when the grouping was partial, since older
    /// failures of every group may then not have been read.
    /// </summary>
    public static string CountText(FailureGroupDto group, bool isPartial)
        => group.Count.ToString("N0") + (isPartial ? "+" : "");

    /// <summary>The text cut to <paramref name="length"/> characters, with an ellipsis when cut.</summary>
    public static string Truncate(string text, int length)
        => text.Length > length ? text[..length] + "…" : text;
}
