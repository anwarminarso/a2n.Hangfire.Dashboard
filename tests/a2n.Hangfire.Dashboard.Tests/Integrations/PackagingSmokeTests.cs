#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Smoke / build tests for packaging and dependency boundaries of the v2.6 integrations.
///
/// Feature: integrations-v2-6, Task 11.4
///
/// Asserts:
///   1. The core package references neither the OpenTelemetry nor the RestApi integration package.
///   2. The core package references no third-party Prometheus client library (the exposition
///      formatter is hand-rolled).
///   3. All three integration packages declare TargetFrameworks net8.0/net9.0/net10.0.
///   4. Repository documentation describes each network endpoint's default authorization mode and
///      how to change it.
///
/// Validates: Requirements 4.3, 5.4, 12.1, 12.4, 15.4, 16.3, 17.3
/// </summary>
public class PackagingSmokeTests
{
    private static readonly Assembly CoreAssembly = typeof(global::a2n.Hangfire.Dashboard.DashboardUIOptions).Assembly;

    // --- Check 1: core does not reference the separate integration packages (Req 12.1, 15.4) ---

    [Fact]
    public void CoreAssembly_DoesNotReference_OpenTelemetryIntegrationPackage()
    {
        var referenced = CoreAssembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.DoesNotContain(
            referenced,
            name => string.Equals(name, "a2n.Hangfire.Dashboard.OpenTelemetry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CoreAssembly_DoesNotReference_RestApiIntegrationPackage()
    {
        var referenced = CoreAssembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.DoesNotContain(
            referenced,
            name => string.Equals(name, "a2n.Hangfire.Dashboard.RestApi", StringComparison.OrdinalIgnoreCase));
    }

    // --- Check 2: no third-party Prometheus client dependency (Req 5.4) ---

    [Fact]
    public void CoreAssembly_ReferencesNo_ThirdPartyPrometheusClient()
    {
        var offending = CoreAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.IndexOf("prometheus", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToArray();

        Assert.True(
            offending.Length == 0,
            $"Core assembly must not reference a third-party Prometheus client; found: {string.Join(", ", offending)}");
    }

    // --- Check 3: all three packages target net8.0/net9.0/net10.0 (Req 4.3, 12.4, 16.3) ---

    [Theory]
    [InlineData("src/a2n.Hangfire.Dashboard/a2n.Hangfire.Dashboard.csproj")]
    [InlineData("src/a2n.Hangfire.Dashboard.OpenTelemetry/a2n.Hangfire.Dashboard.OpenTelemetry.csproj")]
    [InlineData("src/a2n.Hangfire.Dashboard.RestApi/a2n.Hangfire.Dashboard.RestApi.csproj")]
    public void Package_DeclaresAllThreeTargetFrameworks(string relativeCsprojPath)
    {
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var csprojPath = Path.Combine(
            repoRoot!,
            relativeCsprojPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(csprojPath), $"Expected csproj at '{csprojPath}'.");

        var csproj = File.ReadAllText(csprojPath);
        var targetFrameworks = ExtractTargetFrameworks(csproj);

        Assert.False(
            string.IsNullOrWhiteSpace(targetFrameworks),
            $"Expected a <TargetFrameworks> element in '{csprojPath}'.");

        Assert.Contains("net8.0", targetFrameworks, StringComparison.Ordinal);
        Assert.Contains("net9.0", targetFrameworks, StringComparison.Ordinal);
        Assert.Contains("net10.0", targetFrameworks, StringComparison.Ordinal);
    }

    // --- Check 4: documentation describes each endpoint's default auth and how to change it (Req 17.3) ---

    [Fact]
    public void Documentation_DescribesEndpointAuthorizationModes()
    {
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var docText = ReadAllRepositoryDocumentation(repoRoot!);

        // Prometheus metrics endpoint default auth = LocalOnly.
        Assert.Contains("metrics", docText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LocalOnly", docText, StringComparison.OrdinalIgnoreCase);

        // Export endpoint gated by dashboard authorization.
        Assert.Contains("export", docText, StringComparison.OrdinalIgnoreCase);

        // REST API JWT bearer requirement.
        Assert.Contains("REST API", docText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JWT", docText, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractTargetFrameworks(string csproj)
    {
        const string open = "<TargetFrameworks>";
        const string close = "</TargetFrameworks>";

        var start = csproj.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += open.Length;
        var end = csproj.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? string.Empty : csproj.Substring(start, end - start);
    }

    /// <summary>
    /// Reads every Markdown documentation file in the repository (README plus <c>docs/**/*.md</c>)
    /// into a single string so the assertions can locate the endpoint-authorization documentation
    /// wherever the author chose to place it.
    /// </summary>
    private static string ReadAllRepositoryDocumentation(string repoRoot)
    {
        var buffer = new System.Text.StringBuilder();

        var readme = Path.Combine(repoRoot, "README.md");
        if (File.Exists(readme))
        {
            buffer.AppendLine(File.ReadAllText(readme));
        }

        var docsDir = Path.Combine(repoRoot, "docs");
        if (Directory.Exists(docsDir))
        {
            foreach (var md in Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories))
            {
                buffer.AppendLine(File.ReadAllText(md));
            }
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Walks up from the test assembly's base directory to find the repository root — the first
    /// ancestor directory that contains the three integration <c>src</c> project folders, falling
    /// back to a <c>.git</c> folder or a <c>.slnx</c>/<c>.sln</c> marker.
    /// </summary>
    private static string? FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var hasCoreProject = File.Exists(Path.Combine(
                current.FullName,
                "src", "a2n.Hangfire.Dashboard", "a2n.Hangfire.Dashboard.csproj"));
            if (hasCoreProject)
            {
                return current.FullName;
            }

            var hasGit = Directory.Exists(Path.Combine(current.FullName, ".git"));
            var hasSolution = current.GetFiles("*.slnx").Length > 0 || current.GetFiles("*.sln").Length > 0;
            if ((hasGit || hasSolution) && Directory.Exists(Path.Combine(current.FullName, "src")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
