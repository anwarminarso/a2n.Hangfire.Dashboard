# The Blazor script (`_framework/blazor.web.js`)

Back to the [README](../../README.md).

The dashboard's pages go interactive through the Blazor script `_framework/blazor.web.js`. If the dashboard renders but nothing reacts to clicks, a 404 on that script is the usual cause.

- **.NET 8 and .NET 9:** the script is part of the framework. Nothing to do.
- **.NET 10:** the script is a static web asset from the `Microsoft.AspNetCore.App.Internal.Assets` package. The SDK only restores that package for a host that has a `.razor` file or sets `RequiresAspNetWebAssets`, so a Web API or SPA host has no script of its own. A NuGet package can't set the property for its host, because NuGet doesn't read a package's props during restore. The net10.0 build of the dashboard therefore carries a copy of the script, taken from ASP.NET Core 10.0.12, and serves it only when the host doesn't serve its own. It logs this once at startup, at Information. To serve the host's own copy instead, which always matches its runtime, add this to the host `.csproj`:

  ```xml
  <PropertyGroup>
    <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
  </PropertyGroup>
  ```

  A host referencing the dashboard through `<ProjectReference>` gets its script the same way.

When running from a build output (`dotnet run`, not a `dotnet publish` folder), ASP.NET Core reads the static web assets manifest only in the `Development` environment. In another environment, run the published output or call `builder.WebHost.UseStaticWebAssets()`. On .NET 8 and .NET 9 the dashboard logs a Warning at startup when it finds no script to serve.
