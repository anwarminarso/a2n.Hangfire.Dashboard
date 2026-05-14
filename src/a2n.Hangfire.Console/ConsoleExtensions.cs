using Hangfire.Console.Server;
using Hangfire.Server;

namespace Hangfire.Console;

/// <summary>
/// Provides extension methods for writing to console from jobs.
/// API-compatible with the original Hangfire.Console package.
/// </summary>
public static class ConsoleExtensions
{
    /// <summary>
    /// Sets text color for next console lines.
    /// </summary>
    public static void SetTextColor(this PerformContext context, ConsoleTextColor color)
    {
        ArgumentNullException.ThrowIfNull(color);
        var ctx = ConsoleContext.FromPerformContext(context);
        if (ctx is not null) ctx.TextColor = color;
    }

    /// <summary>
    /// Resets text color for next console lines.
    /// </summary>
    public static void ResetTextColor(this PerformContext context)
    {
        var ctx = ConsoleContext.FromPerformContext(context);
        if (ctx is not null) ctx.TextColor = null;
    }

    /// <summary>
    /// Adds a string to console.
    /// </summary>
    public static void WriteLine(this PerformContext context, string value)
    {
        ConsoleContext.FromPerformContext(context)?.WriteLine(value, null);
    }

    /// <summary>
    /// Adds a string to console with specified color.
    /// </summary>
    public static void WriteLine(this PerformContext context, ConsoleTextColor color, string value)
    {
        ConsoleContext.FromPerformContext(context)?.WriteLine(value, color);
    }

    /// <summary>
    /// Adds an empty line to console.
    /// </summary>
    public static void WriteLine(this PerformContext context)
        => WriteLine(context, "");

    /// <summary>
    /// Adds a value to console.
    /// </summary>
    public static void WriteLine(this PerformContext context, object value)
        => WriteLine(context, value?.ToString() ?? "");

    /// <summary>
    /// Adds a formatted string to console.
    /// </summary>
    public static void WriteLine(this PerformContext context, string format, object arg0)
        => WriteLine(context, string.Format(format, arg0));

    /// <summary>
    /// Adds a formatted string to console.
    /// </summary>
    public static void WriteLine(this PerformContext context, string format, object arg0, object arg1)
        => WriteLine(context, string.Format(format, arg0, arg1));

    /// <summary>
    /// Adds a formatted string to console.
    /// </summary>
    public static void WriteLine(this PerformContext context, string format, object arg0, object arg1, object arg2)
        => WriteLine(context, string.Format(format, arg0, arg1, arg2));

    /// <summary>
    /// Adds a formatted string to console.
    /// </summary>
    public static void WriteLine(this PerformContext context, string format, params object[] args)
        => WriteLine(context, string.Format(format, args));

    /// <summary>
    /// Adds an updateable progress bar to console.
    /// </summary>
    public static IProgressBar WriteProgressBar(this PerformContext context, int value = 0, ConsoleTextColor? color = null)
    {
        return ConsoleContext.FromPerformContext(context)?.WriteProgressBar(null, value, color)
               ?? new NoOpProgressBar();
    }

    /// <summary>
    /// Adds an updateable named progress bar to console.
    /// </summary>
    public static IProgressBar WriteProgressBar(this PerformContext context, string? name, double value = 0, ConsoleTextColor? color = null)
    {
        return ConsoleContext.FromPerformContext(context)?.WriteProgressBar(name, value, color)
               ?? new NoOpProgressBar();
    }
}

internal class NoOpProgressBar : IProgressBar
{
    public void SetValue(double value) { }
    public void SetValue(double value, ConsoleTextColor color) { }
}
