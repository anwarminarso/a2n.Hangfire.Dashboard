using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

/// <summary>The Schedule Heatmap's view buttons and the per-view Day, Color by and Concurrency day controls.</summary>
public partial class HeatmapViewSwitcher
{
    [Parameter, EditorRequired] public (string Key, string Label)[] Views { get; set; } = Array.Empty<(string, string)>();

    [Parameter] public string View { get; set; } = "planner";

    [Parameter] public EventCallback<string> OnViewChanged { get; set; }

    /// <summary>Shown as a badge on the Recommendations button.</summary>
    [Parameter] public int RecommendationCount { get; set; }

    [Parameter] public bool ShowDaySelector { get; set; }

    [Parameter] public string[] DayLabels { get; set; } = Array.Empty<string>();

    /// <summary>The Queue × Hour day: -1 for the whole week, otherwise an index into <see cref="DayLabels"/>.</summary>
    [Parameter] public int SelectedDay { get; set; }

    [Parameter] public EventCallback<int> SelectedDayChanged { get; set; }

    [Parameter] public bool ShowColorBy { get; set; }

    [Parameter] public string ColorBy { get; set; } = "volume";

    [Parameter] public EventCallback<string> ColorByChanged { get; set; }

    /// <summary>The Concurrency day: -1 for the worst day, otherwise a window day index.</summary>
    [Parameter] public int ConcurrencyDay { get; set; }

    [Parameter] public EventCallback<int> ConcurrencyDayChanged { get; set; }

    /// <summary>The label of a window day (it depends on the window and the viewer's time zone).</summary>
    [Parameter, EditorRequired] public Func<int, string> DayLabelFor { get; set; } = default!;

    private Task OnSelectedDayChanged(ChangeEventArgs e) => SelectedDayChanged.InvokeAsync(ParseDay(e));

    private Task OnColorByChanged(ChangeEventArgs e) => ColorByChanged.InvokeAsync(e.Value?.ToString() ?? "volume");

    private Task OnConcurrencyDayChanged(ChangeEventArgs e) => ConcurrencyDayChanged.InvokeAsync(ParseDay(e));

    private static int ParseDay(ChangeEventArgs e)
        => int.TryParse(e.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) ? day : -1;
}
