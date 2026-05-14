namespace Hangfire.Tags;

/// <summary>
/// Defines how tags are displayed in the dashboard.
/// </summary>
public enum TagsListStyle
{
    /// <summary>
    /// Shows a list of clickable tags in the dashboard.
    /// </summary>
    LinkButton,

    /// <summary>
    /// Shows a dropdown list of tags with a search field.
    /// </summary>
    Dropdown
}
