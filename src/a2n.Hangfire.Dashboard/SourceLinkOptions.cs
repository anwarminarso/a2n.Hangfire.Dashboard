namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Categorizes the kind of source link being generated. Used by the renderer to pick
/// appropriate iconography and tooltips (e.g., a globe for remote, a desktop for local IDE).
/// </summary>
public enum SourceLinkKind
{
    /// <summary>Hosted source provider (GitHub / GitLab / Azure DevOps / Bitbucket / self-hosted).</summary>
    Remote,
    /// <summary>Local IDE handler such as <c>vscode://</c>. Only meaningful from a developer workstation.</summary>
    Local,
    /// <summary>Custom — kind unknown / user-supplied.</summary>
    Custom,
}

/// <summary>
/// Configures how stack-trace file references are rendered as clickable links on the Job Details page.
/// When unset (null), stack traces are rendered as plain text exactly as before.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="UrlPattern"/> is a string template with two placeholders:
/// <c>{path}</c> (relative file path, forward slashes) and <c>{line}</c> (line number).
/// For <see cref="SourceLinkKind.Local"/> presets, an additional <c>{absolutePath}</c> placeholder
/// is supported (forward-slashed absolute path of the file on disk).
/// </para>
/// <para>
/// Build-time stack traces frequently contain absolute paths from the build agent
/// (e.g., <c>C:\jenkins\workspace\proj\src\Foo.cs</c>) which do not exist in the source repository.
/// Use <see cref="PathTransform"/> or the <see cref="SourceLinkOptionsExtensions.WithPathStrip"/>
/// helper to normalize them.
/// </para>
/// </remarks>
public class SourceLinkOptions
{
    /// <summary>
    /// URL template. Supports placeholders <c>{path}</c>, <c>{line}</c>, and (for local presets) <c>{absolutePath}</c>.
    /// </summary>
    /// <example>
    /// <c>https://github.com/owner/repo/blob/main/{path}#L{line}</c>
    /// </example>
    public string UrlPattern { get; set; }

    /// <summary>
    /// Optional transform applied to the raw file path from the stack trace before substitution into <see cref="UrlPattern"/>.
    /// Use to strip build-agent prefixes, fix path separators, or rebase to a different folder root.
    /// </summary>
    public Func<string, string> PathTransform { get; set; }

    /// <summary>
    /// Categorization used by the renderer for icon/tooltip selection. Defaults to <see cref="SourceLinkKind.Custom"/>.
    /// </summary>
    public SourceLinkKind Kind { get; set; } = SourceLinkKind.Custom;

    // === Presets =============================================================

    /// <summary>
    /// Builds a link configuration for a GitHub repository.
    /// </summary>
    /// <param name="repository"><c>owner/repo</c> identifier (e.g. <c>"anwarminarso/a2n.Hangfire.Dashboard"</c>).</param>
    /// <param name="branch">Branch or tag to link to. Defaults to <c>"main"</c>.</param>
    public static SourceLinkOptions GitHub(string repository, string branch = "main") => new()
    {
        UrlPattern = $"https://github.com/{repository.Trim('/')}/blob/{branch}/{{path}}#L{{line}}",
        Kind = SourceLinkKind.Remote,
    };

    /// <summary>
    /// Builds a link configuration for a GitLab repository (gitlab.com or self-hosted with same URL shape).
    /// </summary>
    /// <param name="repository">Group/project path (e.g. <c>"myteam/myrepo"</c>).</param>
    /// <param name="branch">Branch or tag. Defaults to <c>"main"</c>.</param>
    /// <param name="host">Host name. Defaults to <c>"gitlab.com"</c>.</param>
    public static SourceLinkOptions GitLab(string repository, string branch = "main", string host = "gitlab.com") => new()
    {
        UrlPattern = $"https://{host.Trim('/')}/{repository.Trim('/')}/-/blob/{branch}/{{path}}#L{{line}}",
        Kind = SourceLinkKind.Remote,
    };

    /// <summary>
    /// Builds a link configuration for an Azure DevOps repository.
    /// </summary>
    /// <param name="organization">Organization name.</param>
    /// <param name="project">Project name.</param>
    /// <param name="repository">Repository name.</param>
    /// <param name="branch">Branch (without <c>refs/heads/</c>). Defaults to <c>"main"</c>.</param>
    public static SourceLinkOptions AzureDevOps(string organization, string project, string repository, string branch = "main") => new()
    {
        UrlPattern = $"https://dev.azure.com/{organization}/{project}/_git/{repository}?path=/{{path}}&line={{line}}&version=GB{branch}",
        Kind = SourceLinkKind.Remote,
    };

    /// <summary>
    /// Builds a link configuration for a Bitbucket Cloud repository.
    /// </summary>
    /// <param name="repository"><c>workspace/repo</c> identifier.</param>
    /// <param name="branch">Branch. Defaults to <c>"main"</c>.</param>
    public static SourceLinkOptions Bitbucket(string repository, string branch = "main") => new()
    {
        UrlPattern = $"https://bitbucket.org/{repository.Trim('/')}/src/{branch}/{{path}}#lines-{{line}}",
        Kind = SourceLinkKind.Remote,
    };

    /// <summary>
    /// Builds a link configuration that opens the file in a local IDE via custom URL protocol.
    /// Defaults to <c>vscode://</c> (works out of the box if VS Code is installed and registered).
    /// </summary>
    /// <param name="protocol">
    /// Protocol scheme. Defaults to <c>"vscode"</c>. Other examples:
    /// <list type="bullet">
    ///   <item><c>"vscode-insiders"</c> — VS Code Insiders</item>
    ///   <item><c>"cursor"</c> — Cursor editor</item>
    ///   <item><c>"vs"</c> — Visual Studio (requires a third-party protocol handler such as VsHandler; see README)</item>
    /// </list>
    /// </param>
    /// <remarks>
    /// Only meaningful when the dashboard is accessed from a workstation that has the IDE installed.
    /// Useful for development environments; not for production ops dashboards.
    /// </remarks>
    public static SourceLinkOptions Local(string protocol = "vscode") => new()
    {
        UrlPattern = $"{protocol}://file/{{absolutePath}}:{{line}}",
        Kind = SourceLinkKind.Local,
    };
}

/// <summary>
/// Convenience extensions for fluent <see cref="SourceLinkOptions"/> configuration.
/// </summary>
public static class SourceLinkOptionsExtensions
{
    /// <summary>
    /// Sets <see cref="SourceLinkOptions.PathTransform"/> to strip everything before the
    /// first occurrence of <paramref name="folderName"/> (matched as a path segment),
    /// then normalizes separators to forward slashes.
    /// </summary>
    /// <example>
    /// <c>"C:\jenkins\workspace\proj\src\Foo.cs".WithPathStrip("src")</c> → <c>"src/Foo.cs"</c>
    /// </example>
    public static SourceLinkOptions WithPathStrip(this SourceLinkOptions options, string folderName)
    {
        if (options is null) return null;
        if (string.IsNullOrWhiteSpace(folderName)) return options;

        var marker = folderName.Trim('/', '\\');
        options.PathTransform = path =>
        {
            if (string.IsNullOrEmpty(path)) return path;

            var normalized = path.Replace('\\', '/');
            var segment = "/" + marker + "/";
            var idx = normalized.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                // No segment match — fall back to original (forward-slashed) path.
                return normalized;
            }
            return normalized.Substring(idx + 1); // keep "{folderName}/..."
        };
        return options;
    }

    /// <summary>
    /// Sets <see cref="SourceLinkOptions.PathTransform"/> to a regex replace.
    /// Useful for stripping arbitrary build-agent prefixes.
    /// </summary>
    public static SourceLinkOptions WithPathReplace(this SourceLinkOptions options, string pattern, string replacement)
    {
        if (options is null) return null;
        var regex = new System.Text.RegularExpressions.Regex(pattern);
        options.PathTransform = path => string.IsNullOrEmpty(path)
            ? path
            : regex.Replace(path, replacement ?? string.Empty).Replace('\\', '/');
        return options;
    }
}
