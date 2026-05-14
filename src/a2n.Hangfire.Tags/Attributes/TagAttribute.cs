namespace Hangfire.Tags.Attributes;

/// <summary>
/// Specifies a tag for a job method. Multiple tags can be applied.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public class TagAttribute : Attribute
{
    /// <summary>
    /// Gets the tag value.
    /// </summary>
    public string Tag { get; }

    /// <summary>
    /// Creates a new tag attribute with the specified tag value.
    /// </summary>
    /// <param name="tag">The tag value</param>
    public TagAttribute(string tag)
    {
        Tag = tag ?? throw new ArgumentNullException(nameof(tag));
    }
}
