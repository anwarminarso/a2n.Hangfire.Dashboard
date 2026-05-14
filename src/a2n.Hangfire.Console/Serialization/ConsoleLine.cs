using System.Text.Json.Serialization;

namespace Hangfire.Console.Serialization;

/// <summary>
/// Represents a single console line stored in Hangfire storage.
/// JSON format must be identical to the original Hangfire.Console for backward compatibility.
/// </summary>
internal class ConsoleLine
{
    /// <summary>
    /// Time offset since console timestamp in fractional seconds.
    /// </summary>
    [JsonPropertyName("t")]
    public double TimeOffset { get; set; }

    /// <summary>
    /// True if Message is a Hash reference (for long messages).
    /// </summary>
    [JsonPropertyName("r")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsReference { get; set; }

    /// <summary>
    /// Message text, or message reference key, or progress bar id.
    /// </summary>
    [JsonPropertyName("s")]
    public string Message { get; set; } = "";

    /// <summary>
    /// Text color for this message (CSS color value).
    /// </summary>
    [JsonPropertyName("c")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextColor { get; set; }

    /// <summary>
    /// Value update for a progress bar (0-100).
    /// </summary>
    [JsonPropertyName("p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ProgressValue { get; set; }

    /// <summary>
    /// Optional name for a progress bar.
    /// </summary>
    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProgressName { get; set; }
}
