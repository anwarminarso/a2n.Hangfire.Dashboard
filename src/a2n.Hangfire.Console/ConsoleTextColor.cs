namespace Hangfire.Console;

/// <summary>
/// Text color values for console output.
/// </summary>
public class ConsoleTextColor
{
    private readonly string _color;

    /// <summary>
    /// Creates a new console text color from a CSS color value.
    /// </summary>
    /// <param name="color">CSS color value (e.g., "#ff0000", "red", "rgb(128, 0, 255)")</param>
    public ConsoleTextColor(string color)
    {
        _color = color ?? throw new ArgumentNullException(nameof(color));
    }

    /// <inheritdoc />
    public override string ToString() => _color;

    /// <summary>
    /// Implicitly converts <see cref="ConsoleTextColor"/> to <see cref="string"/>.
    /// </summary>
    public static implicit operator string?(ConsoleTextColor? color) => color?._color;

    /// <summary>The color black.</summary>
    public static readonly ConsoleTextColor Black = new("#000000");

    /// <summary>The color dark blue.</summary>
    public static readonly ConsoleTextColor DarkBlue = new("#000080");

    /// <summary>The color dark green.</summary>
    public static readonly ConsoleTextColor DarkGreen = new("#008000");

    /// <summary>The color dark cyan (dark blue-green).</summary>
    public static readonly ConsoleTextColor DarkCyan = new("#008080");

    /// <summary>The color dark red.</summary>
    public static readonly ConsoleTextColor DarkRed = new("#800000");

    /// <summary>The color dark magenta (dark purplish-red).</summary>
    public static readonly ConsoleTextColor DarkMagenta = new("#800080");

    /// <summary>The color dark yellow (ochre).</summary>
    public static readonly ConsoleTextColor DarkYellow = new("#808000");

    /// <summary>The color gray.</summary>
    public static readonly ConsoleTextColor Gray = new("#c0c0c0");

    /// <summary>The color dark gray.</summary>
    public static readonly ConsoleTextColor DarkGray = new("#808080");

    /// <summary>The color blue.</summary>
    public static readonly ConsoleTextColor Blue = new("#0000ff");

    /// <summary>The color green.</summary>
    public static readonly ConsoleTextColor Green = new("#00ff00");

    /// <summary>The color cyan (blue-green).</summary>
    public static readonly ConsoleTextColor Cyan = new("#00ffff");

    /// <summary>The color red.</summary>
    public static readonly ConsoleTextColor Red = new("#ff0000");

    /// <summary>The color magenta (purplish-red).</summary>
    public static readonly ConsoleTextColor Magenta = new("#ff00ff");

    /// <summary>The color yellow.</summary>
    public static readonly ConsoleTextColor Yellow = new("#ffff00");

    /// <summary>The color white.</summary>
    public static readonly ConsoleTextColor White = new("#ffffff");
}
