namespace Hangfire.Tags;

/// <summary>
/// Specifies how tags should be cleaned before storing.
/// </summary>
[Flags]
public enum Clean
{
    /// <summary>
    /// No cleaning (only commas are removed).
    /// </summary>
    None = 0,

    /// <summary>
    /// Convert to lowercase.
    /// </summary>
    Lowercase = 1,

    /// <summary>
    /// Remove punctuation (keep only letters, digits, spaces, hyphens).
    /// </summary>
    Punctuation = 2,

    /// <summary>
    /// Default cleaning: lowercase + punctuation removal.
    /// </summary>
    Default = Lowercase | Punctuation
}
