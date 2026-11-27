namespace a2n.Hangfire.Dashboard.Helpers;

/// <summary>
/// Exception type names as the dashboard shows them: Analytics → Failures, the retry summary on
/// Job Details and the Exception filter on the Failed page all use the same short form.
/// </summary>
internal static class ExceptionTypeNames
{
    /// <summary>
    /// The type name without its namespace (<c>System.TimeoutException</c> → <c>TimeoutException</c>).
    /// A name without a namespace, or ending in a dot, is returned as is, and so are null and empty.
    /// </summary>
    public static string Short(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return fullName;
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < fullName.Length - 1 ? fullName[(lastDot + 1)..] : fullName;
    }
}
