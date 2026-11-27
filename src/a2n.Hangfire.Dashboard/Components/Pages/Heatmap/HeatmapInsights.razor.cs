using Microsoft.AspNetCore.Components;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

/// <summary>The Schedule Heatmap's insight cards: best window, ad-hoc peak, peak concurrency, overlap suggestions.</summary>
public partial class HeatmapInsights
{
    /// <summary>The lowest-load slot, as a day and hour label.</summary>
    [Parameter] public string? BestWindow { get; set; }

    /// <summary>The ad-hoc demand peak value.</summary>
    [Parameter] public string? AdhocPeak { get; set; }

    /// <summary>When the ad-hoc demand peak happens.</summary>
    [Parameter] public string? AdhocWhen { get; set; }

    /// <summary>Whether a metrics provider collects ad-hoc demand (changes the empty-state hint).</summary>
    [Parameter] public bool DemandAvailable { get; set; }

    [Parameter] public int PeakConcurrency { get; set; }

    [Parameter] public string? PeakWhen { get; set; }

    /// <summary>Whether the peak concurrency is above the worker capacity.</summary>
    [Parameter] public bool PeakOver { get; set; }

    [Parameter] public int Capacity { get; set; }

    [Parameter] public int RecommendationCount { get; set; }

    [Parameter] public bool HasHighSeverityRecommendation { get; set; }

    /// <summary>Raised with the view key ("conc" or "rec") when a clickable card is pressed.</summary>
    [Parameter] public EventCallback<string> OnViewRequested { get; set; }
}
