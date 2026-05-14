namespace Hangfire.Console;

/// <summary>
/// Represents a progress bar that can be updated.
/// </summary>
public interface IProgressBar
{
    /// <summary>
    /// Sets the value of the progress bar (0-100).
    /// </summary>
    /// <param name="value">Progress value</param>
    void SetValue(double value);

    /// <summary>
    /// Sets the value and color of the progress bar.
    /// </summary>
    /// <param name="value">Progress value (0-100)</param>
    /// <param name="color">Color for the progress bar</param>
    void SetValue(double value, ConsoleTextColor color);
}
