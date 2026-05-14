namespace Hangfire.Console;

/// <summary>
/// Represents a text color for console output.
/// </summary>
public class ConsoleTextColor
{
    private readonly string _color;

    /// <summary>
    /// Creates a new console text color from a CSS color value.
    /// </summary>
    /// <param name="color">CSS color value (e.g., "#ff0000", "red")</param>
    public ConsoleTextColor(string color)
    {
        _color = color ?? throw new ArgumentNullException(nameof(color));
    }

    /// <inheritdoc />
    public override string ToString() => _color;

    public static readonly ConsoleTextColor Red = new("#ff0000");
    public static readonly ConsoleTextColor Green = new("#00ff00");
    public static readonly ConsoleTextColor Blue = new("#0000ff");
    public static readonly ConsoleTextColor Yellow = new("#ffff00");
    public static readonly ConsoleTextColor Cyan = new("#00ffff");
    public static readonly ConsoleTextColor Magenta = new("#ff00ff");
    public static readonly ConsoleTextColor White = new("#ffffff");
    public static readonly ConsoleTextColor Gray = new("#808080");
    public static readonly ConsoleTextColor DarkRed = new("#8b0000");
    public static readonly ConsoleTextColor DarkGreen = new("#006400");
    public static readonly ConsoleTextColor DarkBlue = new("#00008b");
    public static readonly ConsoleTextColor DarkYellow = new("#b8860b");
    public static readonly ConsoleTextColor DarkCyan = new("#008b8b");
    public static readonly ConsoleTextColor DarkMagenta = new("#8b008b");
}
