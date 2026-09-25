using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using a2n.Hangfire.Dashboard.Storage;
using Hangfire.States;

namespace a2n.Hangfire.Dashboard.Helpers;

/// <summary>
/// Computes the fingerprint that groups failed jobs by the kind of failure: the exception type, the
/// first line of the message with run-specific values replaced by placeholders, and the top
/// application frame of the stack trace.
/// </summary>
/// <remarks>
/// <para>
/// The input is always the <em>stored</em> strings — the <c>ExceptionType</c>,
/// <c>ExceptionMessage</c> and <c>ExceptionDetails</c> entries of
/// <see cref="FailedState.SerializeData"/> — never the <see cref="Exception"/> object.
/// <see cref="FailureFingerprintFilter"/> computes the value when a job fails and stores it as a job
/// parameter; a failure recorded before the filter was registered is fingerprinted later from its
/// state data. Because both start from the same strings, both produce the same value.
/// </para>
/// <para>
/// The value is <see cref="VersionPrefix"/> followed by the first 16 lowercase hex characters of the
/// SHA-256 of <c>exceptionType + "\n" + normalizedMessage + "\n" + topFrame</c> (UTF-8). A change to
/// the rules below that alters any fingerprint must bump the prefix, so values stored under the old
/// rules can be recognised with <see cref="IsCurrentVersion"/> instead of silently splitting groups.
/// </para>
/// <para>
/// <b>Message.</b> Only the first line is used, trimmed and capped at <see cref="MaxMessageLength"/>
/// characters. Then, in this order: GUIDs → <c>&lt;guid&gt;</c>; dates and times →
/// <c>&lt;time&gt;</c>; <c>0x…</c> values and hex runs of 16+ characters containing a digit →
/// <c>&lt;hex&gt;</c>; e-mail addresses → <c>&lt;email&gt;</c>; IPv4 addresses with an optional port →
/// <c>&lt;ip&gt;</c>; URL query strings → <c>?&lt;query&gt;</c>; standalone integers and decimals →
/// <c>&lt;n&gt;</c>. GUIDs and timestamps go first because they contain digits the number rule would
/// otherwise take apart. Quoted text is not replaced as a whole — <c>Column 'Email' cannot be null</c>
/// and <c>Column 'Phone' cannot be null</c> are different failures — but the rules still apply inside
/// quotes.
/// </para>
/// <para>
/// <b>Top frame.</b> The first stack frame, after cleaning, that is not in a <c>System.</c>,
/// <c>Microsoft.</c> or <c>Hangfire.</c> namespace; the first frame when every frame is; empty when
/// there are no frames. Cleaning drops the parameter list and the <c> in &lt;path&gt;:line &lt;n&gt;</c>
/// suffix (present or not depending on how Hangfire was configured), drops generic arity, and unwraps
/// compiler-generated names (async state machines, lambdas, local functions) to the method that
/// contains them, so a rebuild or a moved line does not change the fingerprint.
/// </para>
/// </remarks>
public static class FailureFingerprint
{
    /// <summary>
    /// Job parameter under which <see cref="FailureFingerprintFilter"/> stores the fingerprint.
    /// </summary>
    /// <remarks>
    /// The value is stored as the raw string, not JSON-encoded, so SQL can compare it directly. Read
    /// it with <c>IStorageConnection.GetJobParameter</c>; Hangfire's typed
    /// <c>GetJobParameter&lt;T&gt;</c> would try to deserialize it as JSON.
    /// </remarks>
    public const string ParameterName = "FailureFingerprint";

    /// <summary>Prefix of fingerprints computed with the current rules.</summary>
    public const string VersionPrefix = "v1:";

    /// <summary>Length at which the first line of the exception message is cut before normalizing.</summary>
    public const int MaxMessageLength = 500;

    private const int HashHexLength = 16;

    private const string ExceptionTypeKey = "ExceptionType";
    private const string ExceptionMessageKey = "ExceptionMessage";
    private const string ExceptionDetailsKey = "ExceptionDetails";

    private const RegexOptions PatternOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // The input is capped and every pattern is linear, so this only matters if that ever stops being
    // true; a rule that times out is skipped rather than failing the state transition.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // A value glued to a letter or digit is part of a word ("Order2f…", "net8"), so ids, times and
    // addresses are only replaced when nothing alphanumeric touches them. Underscores and other
    // punctuation count as separators here: "backup_2026-09-25" is still a date.
    private const string NotAfterAlnum = @"(?<![\p{L}\p{N}])";
    private const string NotBeforeAlnum = @"(?![\p{L}\p{N}])";

    private const string Hex4 = "[0-9a-fA-F]{4}";

    // 1. GUIDs in any .NET format: hyphenated or the 32-digit "N" form, with or without braces.
    private static readonly Regex GuidPattern = new(
        @"\{?" + NotAfterAlnum + "[0-9a-fA-F]{8}-?" + Hex4 + "-?" + Hex4 + "-?" + Hex4 + "-?[0-9a-fA-F]{12}" + NotBeforeAlnum + @"\}?",
        PatternOptions, MatchTimeout);

    private const string TimeOfDay = @"[0-9]{1,2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]{1,9})?)?";
    private const string AmPm = @"(?:\s?[AaPp][Mm])?";

    // 2. ISO 8601 dates and date-times (fraction, Z or offset); M/d/yyyy, d.M.yyyy and yyyy/MM/dd
    // with an optional time and AM/PM; standalone HH:mm:ss(.fff).
    private static readonly Regex TimestampPattern = new(
        NotAfterAlnum + "(?:"
            + "[0-9]{4}-[0-9]{2}-[0-9]{2}(?:[T ]" + TimeOfDay + @"(?:Z|[+-][0-9]{2}(?::?[0-9]{2})?)?)?"
            + @"|(?:[0-9]{4}/[0-9]{1,2}/[0-9]{1,2}|[0-9]{1,2}/[0-9]{1,2}/[0-9]{4}|[0-9]{1,2}\.[0-9]{1,2}\.[0-9]{4})(?:,? " + TimeOfDay + AmPm + ")?"
            + @"|[0-9]{1,2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?" + AmPm
        + ")" + NotBeforeAlnum,
        PatternOptions, MatchTimeout);

    // 3. 0x-prefixed values (HRESULTs, addresses) and long hex runs (hashes, tokens). The run must
    // contain a digit so that a long word made only of the letters a–f is left alone.
    private static readonly Regex HexPattern = new(
        NotAfterAlnum + "(?:0[xX][0-9a-fA-F]+|(?=[a-fA-F]*[0-9])[0-9a-fA-F]{16,})" + NotBeforeAlnum,
        PatternOptions, MatchTimeout);

    // 4. E-mail addresses. The lookbehind makes a match start at the beginning of the local part, so
    // a long run of address characters without an '@' is scanned once, not once per character.
    private static readonly Regex EmailPattern = new(
        @"(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z0-9.-]*[A-Za-z0-9]",
        PatternOptions, MatchTimeout);

    // 5. IPv4 addresses with an optional port. A fifth dotted part means a version number, not an
    // address, and is left alone.
    private static readonly Regex IpPattern = new(
        @"(?<![\p{L}\p{N}.])[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}(?::[0-9]{1,5})?(?!\.?[\p{L}\p{N}])",
        PatternOptions, MatchTimeout);

    // 6. The query string of an absolute URL. The path is kept: it usually names the endpoint, and
    // the other rules already replace ids in it.
    private static readonly Regex UrlQueryPattern = new(
        @"(?<![A-Za-z0-9+.-])(?<url>[A-Za-z][A-Za-z0-9+.-]*://[^\s?#""']*)\?[^\s#""']*",
        PatternOptions, MatchTimeout);

    // 7. Standalone integers and decimals. Unlike the rules above, an underscore counts as part of a
    // word here, so identifiers such as IX_Orders_2 keep their digits along with v2 and net8. A
    // dotted run such as 1.2.3 is a version and is left alone as a whole.
    private static readonly Regex NumberPattern = new(
        @"(?<![\p{L}\p{N}_.])[0-9]+(?:\.[0-9]+)?(?!\.?[\p{L}\p{N}_])",
        PatternOptions, MatchTimeout);

    private static readonly Regex GenericArityPattern = new(@"`[0-9]+", PatternOptions, MatchTimeout);

    private static readonly Regex GenericParametersPattern = new(@"\[[^\[\]]*\]", PatternOptions, MatchTimeout);

    /// <summary>
    /// Returns true when <paramref name="storedValue"/> was computed with the current rules:
    /// <see cref="VersionPrefix"/> followed by 16 lowercase hex characters. A value from an older
    /// version (or anything else) should be recomputed from the job's state data.
    /// </summary>
    /// <param name="storedValue">The value of the <see cref="ParameterName"/> job parameter.</param>
    public static bool IsCurrentVersion(string storedValue)
    {
        if (storedValue is null
            || storedValue.Length != VersionPrefix.Length + HashHexLength
            || !storedValue.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = VersionPrefix.Length; i < storedValue.Length; i++)
        {
            if (!char.IsAsciiHexDigitLower(storedValue[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Computes the fingerprint of a failure from the stored exception strings. Null inputs count as
    /// empty strings. Never throws.
    /// </summary>
    /// <param name="exceptionType">Full name of the exception type (<c>ExceptionType</c>).</param>
    /// <param name="exceptionMessage">The exception message (<c>ExceptionMessage</c>).</param>
    /// <param name="exceptionDetails">The exception text with its stack trace (<c>ExceptionDetails</c>).</param>
    public static FailureFingerprintResult Compute(string exceptionType, string exceptionMessage, string exceptionDetails)
    {
        var type = exceptionType ?? string.Empty;
        var message = NormalizeMessage(exceptionMessage);
        var topFrame = FindTopFrame(exceptionDetails);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(type + "\n" + message + "\n" + topFrame));
        var fingerprint = VersionPrefix + Convert.ToHexString(hash, 0, HashHexLength / 2).ToLowerInvariant();

        return new FailureFingerprintResult(fingerprint, type, message, topFrame);
    }

    /// <summary>
    /// Computes the fingerprint of a failure from Failed state data — the dictionary returned by
    /// <see cref="FailedState.SerializeData"/> or stored in the job's state history. Missing keys
    /// count as empty strings. Never throws.
    /// </summary>
    /// <param name="stateData">State data with <c>ExceptionType</c>, <c>ExceptionMessage</c> and <c>ExceptionDetails</c>.</param>
    public static FailureFingerprintResult Compute(IDictionary<string, string> stateData)
    {
        return Compute(
            GetValue(stateData, ExceptionTypeKey),
            GetValue(stateData, ExceptionMessageKey),
            GetValue(stateData, ExceptionDetailsKey));
    }

    private static string GetValue(IDictionary<string, string> data, string key)
        => data is not null && data.TryGetValue(key, out var value) ? value : null;

    private static string NormalizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;

        var end = message.AsSpan().IndexOfAny('\r', '\n');
        var text = (end >= 0 ? message[..end] : message).Trim();
        if (text.Length > MaxMessageLength)
        {
            // Don't keep half of a surrogate pair: it would hash and display as U+FFFD.
            var cut = char.IsHighSurrogate(text[MaxMessageLength - 1]) ? MaxMessageLength - 1 : MaxMessageLength;
            text = text[..cut];
        }

        text = Replace(GuidPattern, text, "<guid>");
        text = Replace(TimestampPattern, text, "<time>");
        text = Replace(HexPattern, text, "<hex>");
        text = Replace(EmailPattern, text, "<email>");
        text = Replace(IpPattern, text, "<ip>");
        text = Replace(UrlQueryPattern, text, "${url}?<query>");
        text = Replace(NumberPattern, text, "<n>");
        return text;
    }

    private static string FindTopFrame(string details)
    {
        if (string.IsNullOrEmpty(details)) return string.Empty;

        string firstFrame = null;
        foreach (var line in details.Split('\n'))
        {
            var frame = CleanFrame(line);
            if (frame is null) continue;
            if (!IsFrameworkFrame(frame)) return frame;
            firstFrame ??= frame;
        }
        return firstFrame ?? string.Empty;
    }

    /// <summary>
    /// Reduces a stack-frame line to <c>Namespace.Type.Method</c>, or returns null when the line is
    /// not a frame.
    /// </summary>
    private static string CleanFrame(string line)
    {
        var text = line.TrimStart();
        if (!text.StartsWith("at ", StringComparison.Ordinal)) return null;

        // Cutting at the parameter list also drops " in <path>:line <n>", which follows it.
        var paren = text.IndexOf('(');
        if (paren < 0) return null;
        var method = text[3..paren].Trim();

        // A frame names a dotted member with no spaces. This keeps a message line that happens to
        // start with "at " (e.g. "at least one (1) item is required") from counting as a frame.
        if (method.Length == 0 || method.IndexOf('.') < 0 || method.Any(char.IsWhiteSpace)) return null;

        method = Replace(GenericArityPattern, method, string.Empty);
        method = Replace(GenericParametersPattern, method, string.Empty);
        return UnwrapCompilerGenerated(method);
    }

    /// <summary>
    /// Maps a compiler-generated member to the method it was generated from:
    /// <c>&lt;RunAsync&gt;d__7.MoveNext</c> → <c>RunAsync</c>,
    /// <c>&lt;&gt;c__DisplayClass5_0.&lt;RunAsync&gt;b__0</c> → <c>RunAsync</c>,
    /// <c>&lt;&gt;c.&lt;Main&gt;b__0_0</c> → <c>Main</c>, <c>&lt;Run&gt;g__Helper|3_0</c> → <c>Run</c>.
    /// The generated suffixes carry ordinals that change whenever a lambda is added or moved.
    /// </summary>
    private static string UnwrapCompilerGenerated(string method)
    {
        if (method.IndexOf('<') < 0) return method;

        var kept = new List<string>();
        foreach (var segment in method.Split('.'))
        {
            // Closure and anonymous classes (<>c, <>c__DisplayClass5_0) sit between the type and the
            // generated member; they say nothing about which method failed.
            if (segment.StartsWith("<>", StringComparison.Ordinal)) continue;

            if (segment.StartsWith('<'))
            {
                // <RunAsync>d__7, <Main>b__0_0, <Run>g__Helper|3_0 and nested forms such as
                // <<RunAsync>b__0>d all start with the containing method's name. What follows
                // (MoveNext, the lambda body) belongs to the generated type, so stop here.
                var name = segment.TrimStart('<');
                var close = name.IndexOf('>');
                kept.Add(close >= 0 ? name[..close] : name);
                break;
            }

            kept.Add(segment);
        }
        return string.Join('.', kept);
    }

    private static bool IsFrameworkFrame(string frame)
        => frame.StartsWith("System.", StringComparison.Ordinal)
        || frame.StartsWith("Microsoft.", StringComparison.Ordinal)
        || frame.StartsWith("Hangfire.", StringComparison.Ordinal);

    private static string Replace(Regex pattern, string input, string replacement)
    {
        try
        {
            return pattern.Replace(input, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }
}

/// <summary>
/// A failure fingerprint and the normalized parts it was computed from. The parts are what a group
/// label is built from; <see cref="Fingerprint"/> is what jobs are grouped by.
/// </summary>
/// <param name="Fingerprint">The fingerprint, e.g. <c>v1:3f6c0a1e9b2d4c77</c>.</param>
/// <param name="ExceptionType">The exception type as stored (full name), or empty.</param>
/// <param name="NormalizedMessage">The first line of the message after normalization, or empty.</param>
/// <param name="TopFrame">The top application frame as <c>Namespace.Type.Method</c>, or empty when the trace has no frames.</param>
public sealed record FailureFingerprintResult(
    string Fingerprint,
    string ExceptionType,
    string NormalizedMessage,
    string TopFrame);
