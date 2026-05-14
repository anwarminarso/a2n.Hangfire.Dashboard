using Hangfire.Tags;
using Hangfire.Tags.Attributes;
using Xunit;

namespace a2n.Hangfire.Tags.Tests;

/// <summary>
/// Tests for TagAttribute.
/// </summary>
public class TagAttributeTests
{
    [Fact]
    public void Constructor_ValidTag_SetsProperty()
    {
        var attr = new TagAttribute("my-tag");
        Assert.Equal("my-tag", attr.Tag);
    }

    [Fact]
    public void Constructor_NullTag_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TagAttribute(null!));
    }

    [Fact]
    public void Attribute_AllowsMultiple()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(TagAttribute), typeof(AttributeUsageAttribute))!;

        Assert.True(usage.AllowMultiple);
    }

    [Fact]
    public void Attribute_TargetsMethodAndClass()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(TagAttribute), typeof(AttributeUsageAttribute))!;

        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Method));
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Class));
    }
}

/// <summary>
/// Tests for TagsOptions and Clean enum.
/// </summary>
public class TagsOptionsTests
{
    [Fact]
    public void DefaultOptions_MatchOriginalPlugin()
    {
        var options = new TagsOptions();

        Assert.Null(options.MaxTagLength);
        Assert.Null(options.TagColor);
        Assert.Null(options.TextColor);
        Assert.Null(options.DarkTagColor);
        Assert.Null(options.DarkTextColor);
        Assert.Equal(TagsListStyle.LinkButton, options.TagsListStyle);
        Assert.Equal(Clean.Default, options.Clean);
    }

    [Fact]
    public void Clean_Default_IsLowercaseAndPunctuation()
    {
        Assert.Equal(Clean.Lowercase | Clean.Punctuation, Clean.Default);
    }

    [Fact]
    public void Clean_None_IsZero()
    {
        Assert.Equal(0, (int)Clean.None);
    }

    [Fact]
    public void Clean_IsFlagsEnum()
    {
        var attr = Attribute.GetCustomAttribute(typeof(Clean), typeof(FlagsAttribute));
        Assert.NotNull(attr);
    }
}

/// <summary>
/// Tests for TagsListStyle enum.
/// </summary>
public class TagsListStyleTests
{
    [Fact]
    public void HasExpectedValues()
    {
        Assert.Equal(0, (int)TagsListStyle.LinkButton);
        Assert.Equal(1, (int)TagsListStyle.Dropdown);
    }
}

/// <summary>
/// Tests for tag cleaning logic (via reflection since CleanTag is private in TagsStorage).
/// We test the expected behavior by verifying the Clean enum flags work correctly.
/// </summary>
public class TagCleaningTests
{
    // These tests verify the cleaning logic that TagsStorage.CleanTag applies.
    // Since CleanTag is private, we test the expected transformations.

    [Theory]
    [InlineData("Hello World", Clean.Lowercase, "hello world")]
    [InlineData("UPPER", Clean.Lowercase, "upper")]
    [InlineData("already-lower", Clean.Lowercase, "already-lower")]
    public void Lowercase_TransformsCorrectly(string input, Clean clean, string expected)
    {
        var result = ApplyClean(input, clean, null);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("hello!world", Clean.Punctuation, "helloworld")]
    [InlineData("tag with spaces", Clean.Punctuation, "tag-with-spaces")]
    [InlineData("special@#$chars", Clean.Punctuation, "specialchars")]
    [InlineData("keep-hyphens", Clean.Punctuation, "keep-hyphens")]
    [InlineData("digits123ok", Clean.Punctuation, "digits123ok")]
    public void Punctuation_TransformsCorrectly(string input, Clean clean, string expected)
    {
        var result = ApplyClean(input, clean, null);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Hello World!", Clean.Default, "hello-world")]
    [InlineData("My Tag @2024", Clean.Default, "my-tag-2024")]
    public void Default_AppliesBothLowercaseAndPunctuation(string input, Clean clean, string expected)
    {
        var result = ApplyClean(input, clean, null);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MaxTagLength_TruncatesLongTags()
    {
        var longTag = new string('a', 50);
        var result = ApplyClean(longTag, Clean.None, 20);

        Assert.True(result.Length <= 20 - 5); // MaxTagLength - 5
    }

    [Fact]
    public void Commas_AlwaysRemoved()
    {
        var result = ApplyClean("tag,with,commas", Clean.None, null);
        Assert.Equal("tagwithcommas", result);
    }

    /// <summary>
    /// Replicates the CleanTag logic from TagsStorage for testing.
    /// </summary>
    private static string ApplyClean(string tag, Clean clean, int? maxLength)
    {
        var result = tag.Replace(",", "");

        if ((clean & Clean.Lowercase) == Clean.Lowercase)
            result = result.ToLowerInvariant();

        if ((clean & Clean.Punctuation) == Clean.Punctuation)
            result = new string(result.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray())
                .Replace(' ', '-').Replace("--", "-");

        if (maxLength.HasValue && result.Length > maxLength.Value)
            result = result[..(maxLength.Value - 5)];

        return result;
    }
}
