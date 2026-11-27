using Microsoft.AspNetCore.Components;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

/// <summary>The Schedule Heatmap's queue chips. The page owns the selection and reloads on a change.</summary>
public partial class HeatmapQueueFilter
{
    [Parameter, EditorRequired] public IReadOnlyList<string> Queues { get; set; } = Array.Empty<string>();

    [Parameter, EditorRequired] public IReadOnlySet<string> Selected { get; set; } = new HashSet<string>();

    /// <summary>The chip's dot color for a queue.</summary>
    [Parameter, EditorRequired] public Func<string, string> ColorFor { get; set; } = default!;

    /// <summary>The chip's badge for a queue: whether it carries cron jobs, ad-hoc jobs or both.</summary>
    [Parameter, EditorRequired] public Func<string, string> ClassFor { get; set; } = default!;

    [Parameter] public EventCallback<string> OnToggle { get; set; }

    [Parameter] public EventCallback OnSelectAll { get; set; }
}
