using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Unit tests for SearchService.DetectSearchMode logic.
/// Validates: Requirements 2.1, 3.5, 4.1, 5.1, 6.1, 6.3
/// </summary>
public class SearchModeDetectionTests
{
    [Theory]
    [InlineData("123", SearchMode.Id, "123")]
    [InlineData("1", SearchMode.Id, "1")]
    [InlineData("99999999999999999999", SearchMode.Id, "99999999999999999999")] // 20 digits
    [InlineData("00000", SearchMode.Id, "00000")]
    public void DetectSearchMode_AllDigits_ReturnsIdMode(string query, SearchMode expectedMode, string expectedNormalized)
    {
        var (mode, normalized) = SearchService.DetectSearchMode(query);
        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Fact]
    public void DetectSearchMode_DigitsExceeding20Chars_ReturnsNameMode()
    {
        // 21 digits - too long for ID, treated as Name
        var query = "123456789012345678901";
        var (mode, _) = SearchService.DetectSearchMode(query);
        Assert.Equal(SearchMode.Name, mode);
    }

    [Theory]
    [InlineData("queue:default", SearchMode.Queue, "default")]
    [InlineData("Queue:critical", SearchMode.Queue, "critical")]
    [InlineData("QUEUE:high-priority", SearchMode.Queue, "high-priority")]
    [InlineData("queue:  spaced  ", SearchMode.Queue, "spaced")]
    public void DetectSearchMode_QueuePrefix_ReturnsQueueMode(string query, SearchMode expectedMode, string expectedNormalized)
    {
        var (mode, normalized) = SearchService.DetectSearchMode(query);
        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Theory]
    [InlineData("tag:urgent", SearchMode.Tag, "urgent")]
    [InlineData("Tag:my-tag", SearchMode.Tag, "my-tag")]
    [InlineData("TAG:IMPORTANT", SearchMode.Tag, "IMPORTANT")]
    public void DetectSearchMode_TagPrefix_ReturnsTagMode(string query, SearchMode expectedMode, string expectedNormalized)
    {
        var (mode, normalized) = SearchService.DetectSearchMode(query);
        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Theory]
    [InlineData("exception:NullReferenceException", SearchMode.Exception, "NullReferenceException")]
    [InlineData("Exception:timeout", SearchMode.Exception, "timeout")]
    [InlineData("EXCEPTION:Object reference", SearchMode.Exception, "Object reference")]
    public void DetectSearchMode_ExceptionPrefix_ReturnsExceptionMode(string query, SearchMode expectedMode, string expectedNormalized)
    {
        var (mode, normalized) = SearchService.DetectSearchMode(query);
        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Theory]
    [InlineData("MyJobClass", SearchMode.Name, "MyJobClass")]
    [InlineData("SendEmail", SearchMode.Name, "SendEmail")]
    [InlineData("ab", SearchMode.Name, "ab")]
    [InlineData("123abc", SearchMode.Name, "123abc")] // Contains non-digits
    public void DetectSearchMode_TextQuery_ReturnsNameMode(string query, SearchMode expectedMode, string expectedNormalized)
    {
        var (mode, normalized) = SearchService.DetectSearchMode(query);
        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void DetectSearchMode_EmptyOrWhitespace_ReturnsAuto(string query)
    {
        var (mode, normalized) = SearchService.DetectSearchMode(query);
        Assert.Equal(SearchMode.Auto, mode);
        Assert.Equal("", normalized);
    }

    [Fact]
    public void DetectSearchMode_SingleCharText_ReturnsAutoWithQuery()
    {
        // Less than 2 chars for Name mode
        var (mode, normalized) = SearchService.DetectSearchMode("a");
        Assert.Equal(SearchMode.Auto, mode);
        Assert.Equal("a", normalized);
    }

    [Fact]
    public void DetectSearchMode_QueryExceeding200Chars_TruncatesTo200()
    {
        var longQuery = new string('a', 250);
        var (mode, normalized) = SearchService.DetectSearchMode(longQuery);
        Assert.Equal(SearchMode.Name, mode);
        Assert.Equal(200, normalized.Length);
    }

    [Theory]
    [InlineData("queue:", SearchMode.Auto, "")]
    [InlineData("queue:   ", SearchMode.Auto, "")]
    [InlineData("tag:", SearchMode.Auto, "")]
    [InlineData("tag:   ", SearchMode.Auto, "")]
    [InlineData("exception:", SearchMode.Auto, "")]
    [InlineData("exception:   ", SearchMode.Auto, "")]
    public void DetectSearchMode_PrefixWithEmptyValue_ReturnsAuto(string query, SearchMode expectedMode, string expectedNormalized)
    {
        var (mode, normalized) = SearchService.DetectSearchMode(query);
        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Fact]
    public void DetectSearchMode_WhitespaceAroundQuery_TrimsCorrectly()
    {
        var (mode, normalized) = SearchService.DetectSearchMode("  MyJob  ");
        Assert.Equal(SearchMode.Name, mode);
        Assert.Equal("MyJob", normalized);
    }

    [Fact]
    public void DetectSearchMode_WhitespaceAroundDigits_TrimsAndDetectsId()
    {
        var (mode, normalized) = SearchService.DetectSearchMode("  42  ");
        Assert.Equal(SearchMode.Id, mode);
        Assert.Equal("42", normalized);
    }

    [Fact]
    public void DetectSearchMode_LongDigitString_ExactlyMaxIdLength_ReturnsId()
    {
        // Exactly 20 digits
        var query = "12345678901234567890";
        Assert.Equal(20, query.Length);
        var (mode, _) = SearchService.DetectSearchMode(query);
        Assert.Equal(SearchMode.Id, mode);
    }

    [Fact]
    public void DetectSearchMode_QueuePrefixCaseInsensitive()
    {
        var variations = new[] { "queue:", "Queue:", "QUEUE:", "qUeUe:" };
        foreach (var prefix in variations)
        {
            var (mode, _) = SearchService.DetectSearchMode(prefix + "test");
            Assert.Equal(SearchMode.Queue, mode);
        }
    }

    [Fact]
    public void DetectSearchMode_TagPrefixCaseInsensitive()
    {
        var variations = new[] { "tag:", "Tag:", "TAG:", "tAg:" };
        foreach (var prefix in variations)
        {
            var (mode, _) = SearchService.DetectSearchMode(prefix + "test");
            Assert.Equal(SearchMode.Tag, mode);
        }
    }

    [Fact]
    public void DetectSearchMode_ExceptionPrefixCaseInsensitive()
    {
        var variations = new[] { "exception:", "Exception:", "EXCEPTION:", "eXcEpTiOn:" };
        foreach (var prefix in variations)
        {
            var (mode, _) = SearchService.DetectSearchMode(prefix + "test");
            Assert.Equal(SearchMode.Exception, mode);
        }
    }
}
