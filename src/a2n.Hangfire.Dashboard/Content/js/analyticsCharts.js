// Analytics Dashboard Charts — Chart.js interop for analytics pages
// Follows the same pattern as window.dashboardCharts in charts.js

(function () {
    'use strict';

    // Disable chartjs-plugin-streaming globally by default.
    // Only the Home page realtime chart explicitly enables it via scale type: 'realtime'.
    // This prevents the plugin from throwing errors on pages without charts.
    if (window.Chart && Chart.defaults && Chart.defaults.plugins && Chart.defaults.plugins.streaming !== undefined) {
        Chart.defaults.plugins.streaming = false;
    }

    // Theme color palettes
    var PALETTES = {
        light: {
            primary: '#0d6efd',
            success: '#198754',
            danger: '#dc3545',
            warning: '#ffc107',
            info: '#0dcaf0',
            gridColor: '#e5e5e5',
            textColor: '#212529',
            tooltipBg: '#fff',
            tooltipText: '#212529'
        },
        dark: {
            primary: '#6ea8fe',
            success: '#75b798',
            danger: '#ea868f',
            warning: '#ffda6a',
            info: '#6edff6',
            gridColor: '#5f5f5f',
            textColor: '#dee2e6',
            tooltipBg: '#2b3035',
            tooltipText: '#dee2e6'
        }
    };

    // Ordered color sequences for multi-dataset charts
    var SERIES_COLORS = {
        light: ['#0d6efd', '#198754', '#dc3545', '#ffc107', '#0dcaf0', '#6f42c1', '#fd7e14', '#20c997', '#d63384', '#0dcaf0'],
        dark: ['#6ea8fe', '#75b798', '#ea868f', '#ffda6a', '#6edff6', '#b197fc', '#feb272', '#79dfc1', '#e685b5', '#6edff6']
    };

    // Chart instance registry keyed by canvas ID
    var chartInstances = new Map();

    function getTheme() {
        return document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'dark' : 'light';
    }

    function getPalette() {
        return PALETTES[getTheme()];
    }

    function getSeriesColors() {
        return SERIES_COLORS[getTheme()];
    }

    function getDefaultOptions(palette) {
        return {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                streaming: false,
                legend: {
                    labels: { color: palette.textColor }
                },
                tooltip: {
                    backgroundColor: palette.tooltipBg,
                    titleColor: palette.tooltipText,
                    bodyColor: palette.tooltipText,
                    borderColor: palette.gridColor,
                    borderWidth: 1
                }
            },
            scales: {
                x: {
                    grid: { color: palette.gridColor },
                    ticks: { color: palette.textColor }
                },
                y: {
                    grid: { color: palette.gridColor },
                    ticks: { color: palette.textColor, beginAtZero: true }
                }
            }
        };
    }

    function mergeOptions(defaults, custom) {
        if (!custom) return defaults;
        var result = JSON.parse(JSON.stringify(defaults));
        for (var key in custom) {
            if (custom.hasOwnProperty(key)) {
                if (typeof custom[key] === 'object' && custom[key] !== null && !Array.isArray(custom[key]) &&
                    typeof result[key] === 'object' && result[key] !== null) {
                    result[key] = mergeOptions(result[key], custom[key]);
                } else {
                    result[key] = custom[key];
                }
            }
        }
        return result;
    }

    /**
     * Reapply theme colors to all active chart instances.
     */
    function reapplyThemeToAll() {
        var palette = getPalette();
        var seriesColors = getSeriesColors();

        chartInstances.forEach(function (chart, canvasId) {
            if (!chart || !chart.options) return;

            // Update scale colors
            if (chart.options.scales) {
                Object.keys(chart.options.scales).forEach(function (scaleKey) {
                    var scale = chart.options.scales[scaleKey];
                    if (scale.grid) scale.grid.color = palette.gridColor;
                    if (scale.ticks) scale.ticks.color = palette.textColor;
                });
            }

            // Update legend and tooltip colors
            if (chart.options.plugins) {
                if (chart.options.plugins.legend && chart.options.plugins.legend.labels) {
                    chart.options.plugins.legend.labels.color = palette.textColor;
                }
                if (chart.options.plugins.tooltip) {
                    chart.options.plugins.tooltip.backgroundColor = palette.tooltipBg;
                    chart.options.plugins.tooltip.titleColor = palette.tooltipText;
                    chart.options.plugins.tooltip.bodyColor = palette.tooltipText;
                    chart.options.plugins.tooltip.borderColor = palette.gridColor;
                }
            }

            // Update dataset colors based on chart metadata
            var meta = chart._analyticsChartMeta;
            if (meta && meta.type === 'throughput') {
                // Throughput: succeeded=success, failed=danger, deleted=gridColor
                chart.data.datasets.forEach(function (ds, i) {
                    if (i === 0) { ds.borderColor = palette.success; ds.backgroundColor = hexToRgba(palette.success, 0.3); }
                    if (i === 1) { ds.borderColor = palette.danger; ds.backgroundColor = hexToRgba(palette.danger, 0.3); }
                    if (i === 2) { ds.borderColor = palette.gridColor; ds.backgroundColor = hexToRgba(palette.gridColor, 0.3); }
                });
            } else if (meta && meta.type === 'percentile') {
                // Percentile: p50=primary, p95=warning, p99=danger
                chart.data.datasets.forEach(function (ds, i) {
                    if (i === 0) { ds.borderColor = palette.primary; ds.backgroundColor = hexToRgba(palette.primary, 0.1); }
                    if (i === 1) { ds.borderColor = palette.warning; ds.backgroundColor = hexToRgba(palette.warning, 0.1); }
                    if (i === 2) { ds.borderColor = palette.danger; ds.backgroundColor = hexToRgba(palette.danger, 0.1); }
                });
            } else if (meta && meta.type === 'doughnut') {
                chart.data.datasets.forEach(function (ds) {
                    ds.backgroundColor = seriesColors.slice(0, ds.data.length);
                });
            } else if (meta && meta.type === 'horizontalBar') {
                chart.data.datasets.forEach(function (ds) {
                    ds.backgroundColor = palette.primary;
                    ds.borderColor = palette.primary;
                });
            } else if (meta && meta.type === 'multiLine') {
                chart.data.datasets.forEach(function (ds, i) {
                    var color = seriesColors[i % seriesColors.length];
                    ds.borderColor = color;
                    ds.backgroundColor = hexToRgba(color, 0.1);
                });
            }

            chart.update('none');
        });
    }

    function hexToRgba(hex, alpha) {
        if (!hex) return 'rgba(0,0,0,' + alpha + ')';
        hex = hex.replace('#', '');
        if (hex.length === 3) {
            hex = hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2];
        }
        var r = parseInt(hex.substring(0, 2), 16);
        var g = parseInt(hex.substring(2, 4), 16);
        var b = parseInt(hex.substring(4, 6), 16);
        return 'rgba(' + r + ',' + g + ',' + b + ',' + alpha + ')';
    }

    // ========================================================================
    // Public API: window.analyticsCharts
    // ========================================================================

    window.analyticsCharts = {

        /**
         * Generic chart renderer. Creates or replaces a Chart.js instance on the given canvas.
         * @param {string} canvasId - The canvas element ID
         * @param {string} type - Chart.js chart type (line, bar, doughnut, etc.)
         * @param {object} data - Chart.js data object (labels, datasets)
         * @param {object} options - Chart.js options (merged with theme defaults)
         * @returns {boolean} true if chart was created successfully
         */
        renderChart: function (canvasId, type, data, options) {
            var canvas = document.getElementById(canvasId);
            if (!canvas) return false;

            // Destroy existing chart on this canvas
            this.destroyChart(canvasId);

            var palette = getPalette();
            var defaults = getDefaultOptions(palette);

            // For doughnut/pie, remove scales
            if (type === 'doughnut' || type === 'pie') {
                delete defaults.scales;
            }

            var mergedOptions = mergeOptions(defaults, options || {});

            var chart = new Chart(canvas, {
                type: type,
                data: data,
                options: mergedOptions
            });

            chartInstances.set(canvasId, chart);
            return true;
        },

        /**
         * Update data on an existing chart without destroying/recreating it.
         * @param {string} canvasId - The canvas element ID
         * @param {object} data - New Chart.js data object (labels, datasets)
         */
        updateChartData: function (canvasId, data) {
            var chart = chartInstances.get(canvasId);
            if (!chart) return;

            chart.data.labels = data.labels || [];
            chart.data.datasets = data.datasets || [];
            chart.update();
        },

        /**
         * Destroy a single chart instance and remove from registry.
         * @param {string} canvasId - The canvas element ID
         */
        destroyChart: function (canvasId) {
            var chart = chartInstances.get(canvasId);
            if (chart) {
                chart.destroy();
                chartInstances.delete(canvasId);
            }
        },

        /**
         * Destroy all tracked chart instances and clear the registry.
         */
        destroyAll: function () {
            chartInstances.forEach(function (chart) {
                if (chart) chart.destroy();
            });
            chartInstances.clear();
        },

        /**
         * Render a throughput stacked area chart (succeeded/failed/deleted over time).
         * @param {string} canvasId - The canvas element ID
         * @param {string[]} labels - Time labels for x-axis
         * @param {number[]} succeeded - Succeeded counts per interval
         * @param {number[]} failed - Failed counts per interval
         * @param {number[]} deleted - Deleted counts per interval
         */
        renderThroughputChart: function (canvasId, labels, succeeded, failed, deleted) {
            var canvas = document.getElementById(canvasId);
            if (!canvas) return;

            this.destroyChart(canvasId);

            var palette = getPalette();
            var defaults = getDefaultOptions(palette);

            var data = {
                labels: labels,
                datasets: [
                    {
                        label: 'Succeeded',
                        data: succeeded,
                        borderColor: palette.success,
                        backgroundColor: hexToRgba(palette.success, 0.3),
                        borderWidth: 2,
                        fill: true,
                        tension: 0.3
                    },
                    {
                        label: 'Failed',
                        data: failed,
                        borderColor: palette.danger,
                        backgroundColor: hexToRgba(palette.danger, 0.3),
                        borderWidth: 2,
                        fill: true,
                        tension: 0.3
                    },
                    {
                        label: 'Deleted',
                        data: deleted,
                        borderColor: palette.gridColor,
                        backgroundColor: hexToRgba(palette.gridColor, 0.3),
                        borderWidth: 2,
                        fill: true,
                        tension: 0.3
                    }
                ]
            };

            var options = mergeOptions(defaults, {
                plugins: {
                    legend: { display: true, position: 'top', labels: { color: palette.textColor } },
                    tooltip: { mode: 'index', intersect: false }
                },
                scales: {
                    x: {
                        grid: { color: palette.gridColor },
                        ticks: { color: palette.textColor, maxRotation: 45 }
                    },
                    y: {
                        grid: { color: palette.gridColor },
                        ticks: { color: palette.textColor, beginAtZero: true, precision: 0 },
                        stacked: true
                    }
                },
                interaction: { mode: 'index', intersect: false }
            });

            var chart = new Chart(canvas, {
                type: 'line',
                data: data,
                options: options
            });

            chart._analyticsChartMeta = { type: 'throughput' };
            chartInstances.set(canvasId, chart);
        },

        /**
         * Render a percentile line chart (p50/p95/p99 over time).
         * @param {string} canvasId - The canvas element ID
         * @param {string[]} labels - Time labels for x-axis
         * @param {number[]} p50 - P50 values per interval
         * @param {number[]} p95 - P95 values per interval
         * @param {number[]} p99 - P99 values per interval
         */
        renderPercentileChart: function (canvasId, labels, p50, p95, p99) {
            var canvas = document.getElementById(canvasId);
            if (!canvas) return;

            this.destroyChart(canvasId);

            var palette = getPalette();
            var defaults = getDefaultOptions(palette);

            var data = {
                labels: labels,
                datasets: [
                    {
                        label: 'P50',
                        data: p50,
                        borderColor: palette.primary,
                        backgroundColor: hexToRgba(palette.primary, 0.1),
                        borderWidth: 2,
                        fill: false,
                        tension: 0.3,
                        pointRadius: 2
                    },
                    {
                        label: 'P95',
                        data: p95,
                        borderColor: palette.warning,
                        backgroundColor: hexToRgba(palette.warning, 0.1),
                        borderWidth: 2,
                        fill: false,
                        tension: 0.3,
                        pointRadius: 2
                    },
                    {
                        label: 'P99',
                        data: p99,
                        borderColor: palette.danger,
                        backgroundColor: hexToRgba(palette.danger, 0.1),
                        borderWidth: 2,
                        fill: false,
                        tension: 0.3,
                        pointRadius: 2
                    }
                ]
            };

            var options = mergeOptions(defaults, {
                plugins: {
                    legend: { display: true, position: 'top', labels: { color: palette.textColor } },
                    tooltip: { mode: 'index', intersect: false }
                },
                scales: {
                    x: {
                        grid: { color: palette.gridColor },
                        ticks: { color: palette.textColor, maxRotation: 45 }
                    },
                    y: {
                        grid: { color: palette.gridColor },
                        ticks: { color: palette.textColor, beginAtZero: true },
                        title: { display: true, text: 'ms', color: palette.textColor }
                    }
                },
                interaction: { mode: 'index', intersect: false }
            });

            var chart = new Chart(canvas, {
                type: 'line',
                data: data,
                options: options
            });

            chart._analyticsChartMeta = { type: 'percentile' };
            chartInstances.set(canvasId, chart);
        },

        /**
         * Render a doughnut chart (e.g., top exceptions breakdown).
         * @param {string} canvasId - The canvas element ID
         * @param {string[]} labels - Segment labels
         * @param {number[]} values - Segment values
         */
        renderDoughnutChart: function (canvasId, labels, values) {
            var canvas = document.getElementById(canvasId);
            if (!canvas) return;

            this.destroyChart(canvasId);

            var palette = getPalette();
            var seriesColors = getSeriesColors();

            var data = {
                labels: labels,
                datasets: [{
                    data: values,
                    backgroundColor: seriesColors.slice(0, values.length),
                    borderWidth: 2,
                    borderColor: palette.tooltipBg
                }]
            };

            var options = {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: {
                        display: true,
                        position: 'right',
                        labels: { color: palette.textColor, padding: 12 }
                    },
                    tooltip: {
                        backgroundColor: palette.tooltipBg,
                        titleColor: palette.tooltipText,
                        bodyColor: palette.tooltipText,
                        borderColor: palette.gridColor,
                        borderWidth: 1
                    }
                }
            };

            var chart = new Chart(canvas, {
                type: 'doughnut',
                data: data,
                options: options
            });

            chart._analyticsChartMeta = { type: 'doughnut' };
            chartInstances.set(canvasId, chart);
        },

        /**
         * Render a horizontal bar chart (e.g., top job types, failure rate by type).
         * @param {string} canvasId - The canvas element ID
         * @param {string[]} labels - Category labels (y-axis)
         * @param {number[]} values - Values per category
         */
        renderHorizontalBarChart: function (canvasId, labels, values) {
            var canvas = document.getElementById(canvasId);
            if (!canvas) return;

            this.destroyChart(canvasId);

            var palette = getPalette();

            var data = {
                labels: labels,
                datasets: [{
                    data: values,
                    backgroundColor: palette.primary,
                    borderColor: palette.primary,
                    borderWidth: 1,
                    borderRadius: 3
                }]
            };

            var options = {
                responsive: true,
                maintainAspectRatio: false,
                indexAxis: 'y',
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        backgroundColor: palette.tooltipBg,
                        titleColor: palette.tooltipText,
                        bodyColor: palette.tooltipText,
                        borderColor: palette.gridColor,
                        borderWidth: 1
                    }
                },
                scales: {
                    x: {
                        grid: { color: palette.gridColor },
                        ticks: { color: palette.textColor, beginAtZero: true, precision: 0 }
                    },
                    y: {
                        grid: { display: false },
                        ticks: { color: palette.textColor }
                    }
                }
            };

            var chart = new Chart(canvas, {
                type: 'bar',
                data: data,
                options: options
            });

            chart._analyticsChartMeta = { type: 'horizontalBar' };
            chartInstances.set(canvasId, chart);
        },

        /**
         * Render a multi-line chart (e.g., queue throughput with one line per queue).
         * @param {string} canvasId - The canvas element ID
         * @param {string[]} labels - Time labels for x-axis
         * @param {Array<{label: string, data: number[]}>} datasets - Array of dataset objects with label and data
         */
        renderMultiLineChart: function (canvasId, labels, datasets) {
            var canvas = document.getElementById(canvasId);
            if (!canvas) return;

            this.destroyChart(canvasId);

            var palette = getPalette();
            var seriesColors = getSeriesColors();
            var defaults = getDefaultOptions(palette);

            var chartDatasets = datasets.map(function (ds, i) {
                var color = seriesColors[i % seriesColors.length];
                return {
                    label: ds.label,
                    data: ds.data,
                    borderColor: color,
                    backgroundColor: hexToRgba(color, 0.1),
                    borderWidth: 2,
                    fill: false,
                    tension: 0.3,
                    pointRadius: 2
                };
            });

            var data = {
                labels: labels,
                datasets: chartDatasets
            };

            var options = mergeOptions(defaults, {
                plugins: {
                    legend: { display: true, position: 'top', labels: { color: palette.textColor } },
                    tooltip: { mode: 'index', intersect: false }
                },
                scales: {
                    x: {
                        grid: { color: palette.gridColor },
                        ticks: { color: palette.textColor, maxRotation: 45 }
                    },
                    y: {
                        grid: { color: palette.gridColor },
                        ticks: { color: palette.textColor, beginAtZero: true, precision: 0 }
                    }
                },
                interaction: { mode: 'index', intersect: false }
            });

            var chart = new Chart(canvas, {
                type: 'line',
                data: data,
                options: options
            });

            chart._analyticsChartMeta = { type: 'multiLine' };
            chartInstances.set(canvasId, chart);
        }
    };

    // ========================================================================
    // MutationObserver: watch for theme changes and reapply colors
    // ========================================================================

    var currentTheme = getTheme();

    var themeObserver = new MutationObserver(function (mutations) {
        mutations.forEach(function (mutation) {
            if (mutation.attributeName === 'data-bs-theme') {
                var newTheme = getTheme();
                if (newTheme !== currentTheme) {
                    currentTheme = newTheme;
                    reapplyThemeToAll();
                }
            }
        });
    });

    themeObserver.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ['data-bs-theme']
    });

})();
