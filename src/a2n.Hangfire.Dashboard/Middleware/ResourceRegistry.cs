using System.Reflection;

namespace a2n.Hangfire.Dashboard.Middleware;

internal record EmbeddedResourceInfo(string ResourceName, string ContentType, string ETag);

internal static class ResourceRegistry
{
    private const string ResourcePrefix = "a2n.Hangfire.Dashboard.Content.";

    private static readonly Dictionary<string, EmbeddedResourceInfo> Resources;

    // Known file extensions used to determine where the filename extension begins.
    // Ordered longest-first so multi-dot extensions like ".min.css" are matched before ".css".
    private static readonly string[] KnownExtensions =
    [
        ".bundle.min.js",
        ".umd.min.js",
        ".min.css",
        ".min.js",
        ".css",
        ".js",
        ".woff2",
        ".woff",
        ".ico",
        ".png",
        ".svg",
        ".json",
    ];

    static ResourceRegistry()
    {
        var assembly = typeof(ResourceRegistry).Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "1.0.0";
        var etag = $"\"{version}\"";

        Resources = new Dictionary<string, EmbeddedResourceInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;

            var relativePart = resourceName[ResourcePrefix.Length..];
            var urlPath = ConvertResourceNameToUrlPath(relativePart);

            if (urlPath == null)
                continue;

            var contentType = MimeTypes.GetContentType(urlPath);
            var info = new EmbeddedResourceInfo(resourceName, contentType, etag);

            // Register under the primary path
            Resources[$"/_content/{urlPath}"] = info;

            // MSBuild converts hyphens to underscores in folder names but preserves them in file names.
            // Register an additional entry with underscores converted back to hyphens in path segments
            // so that URL requests using the original folder names (with hyphens) can be resolved.
            var hyphenatedPath = RestoreHyphensInPathSegments(urlPath);
            if (hyphenatedPath != urlPath)
            {
                Resources[$"/_content/{hyphenatedPath}"] = info;
            }
        }
    }

    public static bool TryGetResource(string path, out EmbeddedResourceInfo info)
    {
        return Resources.TryGetValue(path, out info);
    }

    /// <summary>
    /// Converts an embedded resource name suffix (after the prefix) to a URL path.
    /// For example: "lib.bootstrap.css.bootstrap.min.css" → "lib/bootstrap/css/bootstrap.min.css"
    /// 
    /// Strategy: Find the known file extension at the end, then convert all dots in the
    /// remaining prefix portion to path separators.
    /// </summary>
    private static string ConvertResourceNameToUrlPath(string resourceSuffix)
    {
        // Try to match a known extension at the end of the resource name
        foreach (var ext in KnownExtensions)
        {
            if (resourceSuffix.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                // Everything before the extension is the path portion (dots become '/')
                var pathPortion = resourceSuffix[..^ext.Length];

                // The path portion should not be empty (there must be at least a filename)
                if (string.IsNullOrEmpty(pathPortion))
                    continue;

                // All dots in the path portion become path separators.
                var urlPath = pathPortion.Replace('.', '/') + ext;
                return urlPath;
            }
        }

        // Fallback: if no known extension matched, treat the last dot as the extension separator
        var lastDot = resourceSuffix.LastIndexOf('.');
        if (lastDot <= 0)
            return null;

        var fallbackPath = resourceSuffix[..lastDot].Replace('.', '/');
        var fallbackExt = resourceSuffix[lastDot..];
        return fallbackPath + fallbackExt;
    }

    /// <summary>
    /// MSBuild converts hyphens (-) to underscores (_) in folder/path segments of embedded resource names.
    /// This method restores hyphens in directory segments (not in the filename itself, since MSBuild
    /// preserves hyphens in filenames).
    /// 
    /// Example: "lib/bootstrap_icons/bootstrap-icons.min.css" → "lib/bootstrap-icons/bootstrap-icons.min.css"
    /// </summary>
    private static string RestoreHyphensInPathSegments(string urlPath)
    {
        var lastSlash = urlPath.LastIndexOf('/');
        if (lastSlash <= 0)
            return urlPath;

        // Only convert underscores to hyphens in the directory portion (before the last slash)
        var directoryPart = urlPath[..lastSlash].Replace('_', '-');
        var filePart = urlPath[lastSlash..];
        return directoryPart + filePart;
    }
}
