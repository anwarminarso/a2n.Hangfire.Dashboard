namespace a2n.Hangfire.Dashboard.Middleware;

internal static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css",
        [".js"] = "application/javascript",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ico"] = "image/x-icon",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".json"] = "application/json",
    };

    public static string GetContentType(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
            return "application/octet-stream";

        return Map.TryGetValue(extension, out var contentType)
            ? contentType
            : "application/octet-stream";
    }
}
