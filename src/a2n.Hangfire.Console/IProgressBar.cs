namespace Hangfire.Console;

/// <summary>
/// Represents a progress bar that can be updated.
/// </summary>
public interface IProgressBar
{
    /// <summary>
    /// Updates a value of a progress bar.
    /// </summary>
    /// <param name="value">New value (0-100)</param>
    void SetValue(int value);

    /// <summary>
    /// Updates a value of a progress bar.
    /// </summary>
    /// <param name="value">New value (0-100)</param>
    void SetValue(double value);
}
