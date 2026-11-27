# Stack Trace Source Links

Back to the [README](../../README.md).

When a job fails, the Job Details page shows the exception stack trace. By default the stack trace is plain text. Set `DashboardUIOptions.SourceLink` to turn `... in {path}:line {N}` references into clickable links pointing to your source provider:

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    DashboardTitle = "My Jobs",
    SourceLink = SourceLinkOptions.GitHub("anwarminarso/a2n.Hangfire.Dashboard", branch: "main"),
});
```

## Built-in presets

```csharp
SourceLinkOptions.GitHub("owner/repo");                                        // github.com
SourceLinkOptions.GitLab("group/repo");                                        // gitlab.com
SourceLinkOptions.GitLab("group/repo", host: "git.mycompany.com");             // self-hosted GitLab
SourceLinkOptions.AzureDevOps("org", "project", "repo");                       // dev.azure.com
SourceLinkOptions.Bitbucket("workspace/repo");                                 // bitbucket.org
SourceLinkOptions.Local();                                                     // vscode://file/{path}:{line}
SourceLinkOptions.Local(protocol: "vscode-insiders");                          // VS Code Insiders
SourceLinkOptions.Local(protocol: "cursor");                                   // Cursor
```

## Self-hosted / proprietary providers

Use `UrlPattern` directly with `{path}` and `{line}` placeholders:

```csharp
SourceLink = new SourceLinkOptions
{
    UrlPattern = "https://gitea.mycompany.com/team/repo/src/branch/main/{path}#L{line}",
};
```

## Visual Studio (Windows)

Visual Studio does not ship with a built-in URL protocol handler. To enable links that open files in Visual Studio, install a third-party handler such as [VsHandler](https://marketplace.visualstudio.com/items?itemName=Slvier.VsHandler) and configure:

```csharp
SourceLink = new SourceLinkOptions
{
    UrlPattern = "vs://open?File={absolutePath}&Line={line}",
};
```

For VS Code (cross-platform), the built-in `Local()` preset works out of the box.

## Path normalization

Stack traces often contain absolute paths from the build agent (e.g. `C:\jenkins\workspace\proj\src\Foo.cs`) that don't match the repository layout. Use `WithPathStrip("src")` to strip everything before the `/src/` segment:

```csharp
SourceLink = SourceLinkOptions.GitHub("owner/repo")
    .WithPathStrip("src");
// "C:\jenkins\workspace\proj\src\Models\Order.cs" → "src/Models/Order.cs"
```

For more complex transforms, use `WithPathReplace(pattern, replacement)` (regex) or set `PathTransform` to a custom `Func<string, string>`.

## Privacy note

Linked URLs only work when the viewer has access to the source provider. Private repositories return 404 to unauthenticated users. The dashboard does not embed source content — it only generates outbound links.
