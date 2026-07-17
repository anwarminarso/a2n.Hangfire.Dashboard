#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Smoke test for the sample Grafana dashboard shipped in the repository
/// (<c>docs/grafana/hangfire-dashboard.json</c>).
///
/// Feature: integrations-v2-6, Task 7.2
///
/// Asserts the file exists and parses as valid JSON with an object root, and that a couple
/// of the exposed metric expressions are present in the panels.
///
/// Validates: Requirements 6.9
/// </summary>
public class GrafanaSampleSmokeTests
{
    private const string RelativePath = "docs/grafana/hangfire-dashboard.json";

    [Fact]
    public void GrafanaSample_Exists_And_IsValidJson()
    {
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var jsonPath = Path.Combine(repoRoot!, "docs", "grafana", "hangfire-dashboard.json");
        Assert.True(File.Exists(jsonPath), $"Expected Grafana sample dashboard at '{jsonPath}'.");

        var json = File.ReadAllText(jsonPath);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

        // Sanity check: the sample should reference the exposed Prometheus metric families.
        Assert.Contains("hangfire_jobs_total", json, StringComparison.Ordinal);
        Assert.Contains("hangfire_job_duration_seconds", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up from the test assembly's base directory to find the repository root — the first
    /// ancestor directory that contains the expected <c>docs/grafana/hangfire-dashboard.json</c>
    /// file. Falls back to recognizing a repo root by a <c>.git</c> folder or a <c>.slnx</c>/<c>.sln</c>
    /// file so the test remains robust regardless of the build output layout.
    /// </summary>
    private static string? FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            {
                return current.FullName;
            }

            var hasGit = Directory.Exists(Path.Combine(current.FullName, ".git"));
            var hasSolution = current.GetFiles("*.slnx").Length > 0 || current.GetFiles("*.sln").Length > 0;
            if ((hasGit || hasSolution) && Directory.Exists(Path.Combine(current.FullName, "docs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
