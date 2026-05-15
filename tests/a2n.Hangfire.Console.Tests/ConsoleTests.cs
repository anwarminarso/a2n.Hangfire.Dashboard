using Hangfire.Common;
using Hangfire.Console;
using Hangfire.Console.Serialization;
using Xunit;

namespace a2n.Hangfire.Console.Tests;

/// <summary>
/// Tests for ConsoleLine serialization — ensures backward compatibility
/// with the original Hangfire.Console data format in storage.
/// </summary>
public class ConsoleLineSerializationTests
{
    [Fact]
    public void Serialize_TextLine_ProducesCorrectJsonKeys()
    {
        var line = new ConsoleLine
        {
            TimeOffset = 1.234,
            Message = "Hello world"
        };

        var json = JobHelper.ToJson(line);

        // Must use short keys like original Hangfire.Console
        Assert.Contains("\"t\":", json);
        Assert.Contains("\"s\":", json);
        Assert.DoesNotContain("\"TimeOffset\"", json);
        Assert.DoesNotContain("\"Message\"", json);
    }

    [Fact]
    public void Serialize_TextLine_OmitsDefaultValues()
    {
        var line = new ConsoleLine
        {
            TimeOffset = 0.5,
            Message = "test"
        };

        var json = JobHelper.ToJson(line);

        // IsReference=false, TextColor=null, ProgressValue=null should be omitted
        Assert.DoesNotContain("\"r\"", json);
        Assert.DoesNotContain("\"c\"", json);
        Assert.DoesNotContain("\"p\"", json);
        Assert.DoesNotContain("\"n\"", json);
    }

    [Fact]
    public void Serialize_ColoredLine_IncludesColor()
    {
        var line = new ConsoleLine
        {
            TimeOffset = 2.0,
            Message = "colored",
            TextColor = "#ff0000"
        };

        var json = JobHelper.ToJson(line);

        Assert.Contains("\"c\":\"#ff0000\"", json);
    }

    [Fact]
    public void Serialize_ProgressBar_IncludesProgressFields()
    {
        var line = new ConsoleLine
        {
            TimeOffset = 3.0,
            Message = "1",
            ProgressValue = 75.5,
            ProgressName = "Downloading"
        };

        var json = JobHelper.ToJson(line);

        Assert.Contains("\"p\":75.5", json);
        Assert.Contains("\"n\":\"Downloading\"", json);
    }

    [Fact]
    public void Serialize_ReferenceLine_IncludesReferenceFlag()
    {
        var line = new ConsoleLine
        {
            TimeOffset = 1.0,
            Message = "abc123def456",
            IsReference = true
        };

        var json = JobHelper.ToJson(line);

        Assert.Contains("\"r\":true", json);
    }

    [Fact]
    public void Deserialize_OriginalFormat_ParsesCorrectly()
    {
        // This is the exact format the original Hangfire.Console writes
        var json = "{\"t\":1.234,\"s\":\"Hello world\"}";

        var line = JobHelper.FromJson<ConsoleLine>(json);

        Assert.Equal(1.234, line.TimeOffset);
        Assert.Equal("Hello world", line.Message);
        Assert.False(line.IsReference);
        Assert.Null(line.TextColor);
        Assert.Null(line.ProgressValue);
    }

    [Fact]
    public void Deserialize_OriginalFormatWithColor_ParsesCorrectly()
    {
        var json = "{\"t\":2.5,\"s\":\"error msg\",\"c\":\"#ff0000\"}";

        var line = JobHelper.FromJson<ConsoleLine>(json);

        Assert.Equal(2.5, line.TimeOffset);
        Assert.Equal("error msg", line.Message);
        Assert.Equal("#ff0000", line.TextColor);
    }

    [Fact]
    public void Deserialize_OriginalFormatWithProgress_ParsesCorrectly()
    {
        var json = "{\"t\":5.0,\"s\":\"1\",\"p\":50.0,\"n\":\"Upload\"}";

        var line = JobHelper.FromJson<ConsoleLine>(json);

        Assert.Equal(5.0, line.TimeOffset);
        Assert.Equal("1", line.Message);
        Assert.Equal(50.0, line.ProgressValue);
        Assert.Equal("Upload", line.ProgressName);
    }

    [Fact]
    public void Deserialize_OriginalFormatWithReference_ParsesCorrectly()
    {
        var json = "{\"t\":1.0,\"r\":true,\"s\":\"ref-key-123\"}";

        var line = JobHelper.FromJson<ConsoleLine>(json);

        Assert.True(line.IsReference);
        Assert.Equal("ref-key-123", line.Message);
    }

    [Fact]
    public void Roundtrip_AllFields_PreservesData()
    {
        var original = new ConsoleLine
        {
            TimeOffset = 12.345,
            IsReference = true,
            Message = "some-hash-key",
            TextColor = "#00ff00",
            ProgressValue = 99.9,
            ProgressName = "Processing"
        };

        var json = JobHelper.ToJson(original);
        var deserialized = JobHelper.FromJson<ConsoleLine>(json);

        Assert.Equal(original.TimeOffset, deserialized.TimeOffset);
        Assert.Equal(original.IsReference, deserialized.IsReference);
        Assert.Equal(original.Message, deserialized.Message);
        Assert.Equal(original.TextColor, deserialized.TextColor);
        Assert.Equal(original.ProgressValue, deserialized.ProgressValue);
        Assert.Equal(original.ProgressName, deserialized.ProgressName);
    }
}

/// <summary>
/// Tests for ConsoleId — encoding/decoding must match original Hangfire.Console format.
/// </summary>
public class ConsoleIdTests
{
    [Fact]
    public void Constructor_ValidInput_CreatesId()
    {
        var timestamp = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var id = new ConsoleId("123", timestamp);

        Assert.Equal("123", id.JobId);
        Assert.Equal(timestamp, id.DateValue);
    }

    [Fact]
    public void Constructor_NullJobId_Throws()
    {
        var timestamp = DateTime.UtcNow;
        Assert.Throws<ArgumentNullException>(() => new ConsoleId(null!, timestamp));
    }

    [Fact]
    public void Constructor_EmptyJobId_Throws()
    {
        var timestamp = DateTime.UtcNow;
        Assert.Throws<ArgumentNullException>(() => new ConsoleId("", timestamp));
    }

    [Fact]
    public void ToString_ProducesCorrectFormat()
    {
        // 11 hex chars (reversed nibbles of ms timestamp) + jobId
        var timestamp = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var id = new ConsoleId("42", timestamp);
        var str = id.ToString();

        Assert.Equal(11 + 2, str.Length); // 11 hex + "42"
        Assert.EndsWith("42", str);

        // First 11 chars should be hex
        for (var i = 0; i < 11; i++)
        {
            Assert.True(char.IsAsciiHexDigitLower(str[i]) || char.IsDigit(str[i]),
                $"Character at position {i} should be hex: '{str[i]}'");
        }
    }

    [Fact]
    public void Parse_ValidString_ReturnsCorrectId()
    {
        var timestamp = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var original = new ConsoleId("test-job-1", timestamp);
        var str = original.ToString();

        var parsed = ConsoleId.Parse(str);

        Assert.Equal(original.JobId, parsed.JobId);
        Assert.Equal(original.Timestamp, parsed.Timestamp);
    }

    [Fact]
    public void Parse_TooShort_Throws()
    {
        Assert.Throws<ArgumentException>(() => ConsoleId.Parse("short"));
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ConsoleId.Parse(null!));
    }

    [Fact]
    public void GetSetKey_ReturnsCorrectFormat()
    {
        var id = new ConsoleId("job1", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var setKey = id.GetSetKey();

        Assert.StartsWith("console:set:", setKey);
        Assert.EndsWith("job1", setKey);
    }

    [Fact]
    public void GetHashKey_ReturnsCorrectFormat()
    {
        var id = new ConsoleId("job1", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var hashKey = id.GetHashKey();

        Assert.StartsWith("console:hash:", hashKey);
        Assert.EndsWith("job1", hashKey);
    }

    [Fact]
    public void GetOldConsoleKey_ReturnsCorrectFormat()
    {
        var id = new ConsoleId("job1", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var oldKey = id.GetOldConsoleKey();

        Assert.StartsWith("console:", oldKey);
        Assert.DoesNotContain("console:set:", oldKey);
        Assert.DoesNotContain("console:hash:", oldKey);
    }

    [Fact]
    public void Equals_SameValues_ReturnsTrue()
    {
        var ts = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var id1 = new ConsoleId("job1", ts);
        var id2 = new ConsoleId("job1", ts);

        Assert.Equal(id1, id2);
        Assert.Equal(id1.GetHashCode(), id2.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentJobId_ReturnsFalse()
    {
        var ts = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var id1 = new ConsoleId("job1", ts);
        var id2 = new ConsoleId("job2", ts);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ToString_Roundtrip_IsConsistent()
    {
        var ts = new DateTime(2024, 3, 10, 8, 30, 0, DateTimeKind.Utc);
        var id = new ConsoleId("my-job-id", ts);

        // Multiple calls should return same string
        Assert.Equal(id.ToString(), id.ToString());

        // Parse should roundtrip
        var parsed = ConsoleId.Parse(id.ToString());
        Assert.Equal(id.ToString(), parsed.ToString());
    }
}

/// <summary>
/// Tests for ConsoleOptions validation.
/// </summary>
public class ConsoleOptionsTests
{
    [Fact]
    public void DefaultValues_MatchOriginalPlugin()
    {
        var options = new ConsoleOptions();

        Assert.Equal(TimeSpan.FromDays(1), options.ExpireIn);
        Assert.True(options.FollowJobRetentionPolicy);
        Assert.Equal(1000, options.PollInterval);
        Assert.Equal("#0d3163", options.BackgroundColor);
        Assert.Equal("#ffffff", options.TextColor);
        Assert.Equal("#00aad7", options.TimestampColor);
        Assert.Equal(1, options.ProgressBarDecimalDigits);
    }

    [Fact]
    public void Validate_DefaultOptions_DoesNotThrow()
    {
        var options = new ConsoleOptions();
        var ex = Record.Exception(() => options.Validate("test"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_ExpireInTooShort_Throws()
    {
        var options = new ConsoleOptions { ExpireIn = TimeSpan.FromSeconds(30) };
        Assert.Throws<ArgumentException>(() => options.Validate("test"));
    }

    [Fact]
    public void Validate_PollIntervalTooLow_Throws()
    {
        var options = new ConsoleOptions { PollInterval = 50 };
        Assert.Throws<ArgumentException>(() => options.Validate("test"));
    }

    [Fact]
    public void Validate_ProgressBarDecimalDigitsNegative_Throws()
    {
        var options = new ConsoleOptions { ProgressBarDecimalDigits = -1 };
        Assert.Throws<ArgumentException>(() => options.Validate("test"));
    }

    [Fact]
    public void Validate_ProgressBarDecimalDigitsTooHigh_Throws()
    {
        var options = new ConsoleOptions { ProgressBarDecimalDigits = 4 };
        Assert.Throws<ArgumentException>(() => options.Validate("test"));
    }
}

/// <summary>
/// Tests for ConsoleTextColor.
/// </summary>
public class ConsoleTextColorTests
{
    [Fact]
    public void PredefinedColors_HaveCorrectValues()
    {
        Assert.Equal("#ff0000", ConsoleTextColor.Red.ToString());
        Assert.Equal("#00ff00", ConsoleTextColor.Green.ToString());
        Assert.Equal("#0000ff", ConsoleTextColor.Blue.ToString());
        Assert.Equal("#ffff00", ConsoleTextColor.Yellow.ToString());
        Assert.Equal("#00ffff", ConsoleTextColor.Cyan.ToString());
        Assert.Equal("#ff00ff", ConsoleTextColor.Magenta.ToString());
        Assert.Equal("#ffffff", ConsoleTextColor.White.ToString());
        Assert.Equal("#c0c0c0", ConsoleTextColor.Gray.ToString());
        Assert.Equal("#808080", ConsoleTextColor.DarkGray.ToString());
        Assert.Equal("#000000", ConsoleTextColor.Black.ToString());
        Assert.Equal("#000080", ConsoleTextColor.DarkBlue.ToString());
        Assert.Equal("#008000", ConsoleTextColor.DarkGreen.ToString());
        Assert.Equal("#008080", ConsoleTextColor.DarkCyan.ToString());
        Assert.Equal("#800000", ConsoleTextColor.DarkRed.ToString());
        Assert.Equal("#800080", ConsoleTextColor.DarkMagenta.ToString());
        Assert.Equal("#808000", ConsoleTextColor.DarkYellow.ToString());
    }

    [Fact]
    public void Constructor_NullColor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ConsoleTextColor(null!));
    }

    [Fact]
    public void Constructor_CustomColor_ReturnsValue()
    {
        var color = new ConsoleTextColor("rgb(128, 0, 255)");
        Assert.Equal("rgb(128, 0, 255)", color.ToString());
    }
}
