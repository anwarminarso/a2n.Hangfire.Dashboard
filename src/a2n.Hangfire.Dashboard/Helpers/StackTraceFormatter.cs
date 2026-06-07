using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace a2n.Hangfire.Dashboard.Helpers;

/// <summary>
/// Renders a .NET stack trace as syntax-highlighted HTML.
/// When a <see cref="SourceLinkOptions"/> instance is supplied, file references
/// (<c>... in {path}:line {N}</c>) are wrapped in <c>&lt;a&gt;</c> tags pointing to the configured provider.
/// </summary>
public static class StackTraceFormatter
{
    private static readonly Regex MethodRegex = new(
        @"(at\s+)([\w\.]+)\.([\w`\[\]]+)\(([^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex FileLineRegex = new(
        @"\s+in\s+(.+?):line\s+(\d+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Formats a stack trace string into HTML with method/file highlighting and optional source links.
    /// </summary>
    /// <param name="stackTrace">Raw stack trace text.</param>
    /// <param name="sourceLink">Optional source-link configuration. When null, paths render as plain spans.</param>
    public static string Format(string stackTrace, SourceLinkOptions sourceLink = null)
    {
        if (string.IsNullOrEmpty(stackTrace)) return string.Empty;

        var encoded = WebUtility.HtmlEncode(stackTrace);

        // Highlight method names: at Namespace.Class.Method(args)
        encoded = MethodRegex.Replace(encoded,
            "$1<span class=\"hf-st-namespace\">$2</span>.<span class=\"hf-st-method\">$3</span>(<span class=\"hf-st-params\">$4</span>)");

        // Highlight (and optionally link) file:line references.
        encoded = FileLineRegex.Replace(encoded, m =>
        {
            // The MethodRegex above HTML-encodes the full trace before this point, so $1/$2 already
            // contain entity-encoded characters. We decode for the purpose of building a URL, then
            // re-encode any literal user-visible portion.
            var rawPath = WebUtility.HtmlDecode(m.Groups[1].Value);
            var rawLine = m.Groups[2].Value;

            var pathHtml = m.Groups[1].Value; // already encoded
            var lineHtml = rawLine;           // digits — encoding-safe

            var url = TryBuildUrl(rawPath, rawLine, sourceLink);
            if (string.IsNullOrEmpty(url))
            {
                return $" in <span class=\"hf-st-file\">{pathHtml}</span>:line <span class=\"hf-st-line\">{lineHtml}</span>";
            }

            var encodedUrl = WebUtility.HtmlEncode(url);
            var iconClass = sourceLink.Kind == SourceLinkKind.Local
                ? "bi-window-stack"
                : "bi-box-arrow-up-right";
            var tooltip = sourceLink.Kind == SourceLinkKind.Local
                ? "Open in local IDE"
                : "Open in source repository";

            // Anchor wraps the whole "<file>:line <N>" run so the user can click anywhere on the path.
            return $" in <a href=\"{encodedUrl}\" target=\"_blank\" rel=\"noopener noreferrer\" "
                 + $"class=\"hf-st-file hf-st-link\" title=\"{tooltip}\">"
                 + $"{pathHtml}<span class=\"hf-st-link-icon\"><i class=\"bi {iconClass}\"></i></span>"
                 + $"</a>:line <span class=\"hf-st-line\">{lineHtml}</span>";
        });

        return encoded;
    }

    private static string TryBuildUrl(string rawPath, string rawLine, SourceLinkOptions options)
    {
        if (options is null || string.IsNullOrWhiteSpace(options.UrlPattern)) return null;
        if (string.IsNullOrWhiteSpace(rawPath)) return null;

        var absolute = NormalizeSeparators(rawPath);
        var transformed = options.PathTransform is null ? absolute : SafeTransform(options.PathTransform, rawPath);
        if (string.IsNullOrEmpty(transformed)) transformed = absolute;
        transformed = NormalizeSeparators(transformed);

        var relative = transformed.TrimStart('/');
        var url = options.UrlPattern
            .Replace("{path}", Uri.EscapeDataString(relative).Replace("%2F", "/"))
            .Replace("{absolutePath}", Uri.EscapeDataString(absolute).Replace("%2F", "/"))
            .Replace("{line}", rawLine ?? "1");

        return url;
    }

    private static string NormalizeSeparators(string path)
        => string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');

    private static string SafeTransform(Func<string, string> transform, string input)
    {
        try { return transform(input); }
        catch { return input; }
    }
}
