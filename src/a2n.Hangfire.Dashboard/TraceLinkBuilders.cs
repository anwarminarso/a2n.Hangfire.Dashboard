#nullable enable
using System;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Ready-made <see cref="DashboardUIOptions.TraceLinkBuilder"/> presets that build deep links into
/// common tracing backends from a job's captured <see cref="TraceLinkContext"/>. Every preset embeds
/// the job's trace-id in the produced URL (Req 3.4). Hosts assign one of these to
/// <see cref="DashboardUIOptions.TraceLinkBuilder"/>, or use <see cref="Template"/> for a custom
/// backend.
/// </summary>
public static class TraceLinkBuilders
{
    /// <summary>
    /// Builds a link into a Grafana Tempo trace view. The <paramref name="baseUrl"/> is the Grafana
    /// root (e.g. <c>https://grafana.example.com</c>); the produced URL opens Grafana Explore with a
    /// TraceQL query for the job's trace-id.
    /// </summary>
    /// <param name="baseUrl">The Grafana base URL. Trailing slashes are handled.</param>
    public static Func<TraceLinkContext, string?> Tempo(string baseUrl)
    {
        var root = NormalizeBaseUrl(baseUrl);
        return ctx =>
        {
            if (ctx is null || string.IsNullOrEmpty(ctx.TraceId))
            {
                return null;
            }

            // Grafana Explore deep link with a Tempo TraceQL query targeting the trace-id.
            var panes =
                "{\"tempo\":{\"datasource\":\"tempo\",\"queries\":[{\"queryType\":\"traceql\",\"query\":\""
                + ctx.TraceId + "\"}]}}";
            return $"{root}/explore?schemaVersion=1&panes={Uri.EscapeDataString(panes)}";
        };
    }

    /// <summary>
    /// Builds a link into the Jaeger UI trace view (<c>{baseUrl}/trace/{traceId}</c>).
    /// </summary>
    /// <param name="baseUrl">The Jaeger UI base URL. Trailing slashes are handled.</param>
    public static Func<TraceLinkContext, string?> Jaeger(string baseUrl)
    {
        var root = NormalizeBaseUrl(baseUrl);
        return ctx =>
        {
            if (ctx is null || string.IsNullOrEmpty(ctx.TraceId))
            {
                return null;
            }

            return $"{root}/trace/{Uri.EscapeDataString(ctx.TraceId)}";
        };
    }

    /// <summary>
    /// Builds a link into the Honeycomb trace view for the given <paramref name="team"/> and
    /// <paramref name="dataset"/> (<c>https://ui.honeycomb.io/{team}/datasets/{dataset}/trace?trace_id={traceId}</c>).
    /// </summary>
    /// <param name="dataset">The Honeycomb dataset slug.</param>
    /// <param name="team">The Honeycomb team slug.</param>
    public static Func<TraceLinkContext, string?> Honeycomb(string dataset, string team)
    {
        return ctx =>
        {
            if (ctx is null || string.IsNullOrEmpty(ctx.TraceId))
            {
                return null;
            }

            return $"https://ui.honeycomb.io/{Uri.EscapeDataString(team ?? string.Empty)}"
                + $"/datasets/{Uri.EscapeDataString(dataset ?? string.Empty)}"
                + $"/trace?trace_id={Uri.EscapeDataString(ctx.TraceId)}";
        };
    }

    /// <summary>
    /// Builds a link from a generic URL template. The literal token <c>{traceId}</c> in
    /// <paramref name="urlTemplate"/> is replaced with the job's trace-id.
    /// </summary>
    /// <param name="urlTemplate">
    /// A URL template containing the <c>{traceId}</c> token, e.g.
    /// <c>https://traces.example.com/view?id={traceId}</c>.
    /// </param>
    public static Func<TraceLinkContext, string?> Template(string urlTemplate)
    {
        return ctx =>
        {
            if (ctx is null || string.IsNullOrEmpty(ctx.TraceId) || string.IsNullOrEmpty(urlTemplate))
            {
                return null;
            }

            return urlTemplate.Replace("{traceId}", ctx.TraceId, StringComparison.Ordinal);
        };
    }

    /// <summary>
    /// Trims trailing slashes from a base URL so path segments can be appended uniformly.
    /// </summary>
    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            return string.Empty;
        }

        return baseUrl.TrimEnd('/');
    }
}
