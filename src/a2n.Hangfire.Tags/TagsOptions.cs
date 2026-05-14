namespace Hangfire.Tags;

/// <summary>
/// Configuration options for tags.
/// </summary>
public class TagsOptions
{
    /// <summary>
    /// Sets the maximum length for a tag.
    /// </summary>
    public int? MaxTagLength { get; set; }

    /// <summary>
    /// The background color of the tags in the dashboard (light mode).
    /// </summary>
    public string TagColor { get; set; }

    /// <summary>
    /// The text color of the tags in the dashboard (light mode).
    /// </summary>
    public string TextColor { get; set; }

    /// <summary>
    /// The background color of the tags in the dashboard (dark mode).
    /// </summary>
    public string DarkTagColor { get; set; }

    /// <summary>
    /// The text color of the tags in the dashboard (dark mode).
    /// </summary>
    public string DarkTextColor { get; set; }

    /// <summary>
    /// How to show tags in the dashboard.
    /// </summary>
    public TagsListStyle TagsListStyle { get; set; } = TagsListStyle.LinkButton;

    /// <summary>
    /// Specifies how tags should be cleaned before storing.
    /// </summary>
    public Clean Clean { get; set; } = Clean.Default;
}
