#nullable enable

namespace a2n.Hangfire.Dashboard.Services.Export;

/// <summary>
/// Opt-in configuration for the CSV / JSON job export endpoint served inside the dashboard's
/// branched pipeline (Req 13, 15, 16). Disabled by default; enable either by setting
/// <see cref="Enabled"/> on <see cref="DashboardUIOptions.Export"/> directly, or through the
/// <c>DashboardStorageOptionsBuilder.EnableJobExport</c> opt-in convenience.
/// </summary>
public sealed class ExportOptions
{
    /// <summary>
    /// Whether the job export endpoint is enabled. Opt-in; default <c>false</c> (Req 15.3).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// The endpoint path, relative to the dashboard's configured <c>Path_Prefix</c>.
    /// Default <c>/export</c> (Req 16.2).
    /// </summary>
    public string Path { get; set; } = "/export";

    /// <summary>
    /// Safety cap on the number of records streamed by a single export request. The exporter stops
    /// once this many records have been written. Default <c>100,000</c>.
    /// </summary>
    public int MaxRecords { get; set; } = 100_000;
}
