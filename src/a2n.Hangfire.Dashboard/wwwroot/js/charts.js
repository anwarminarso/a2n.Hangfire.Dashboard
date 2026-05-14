// Hangfire Dashboard Charts — uses Chart.js 3.9.1 + chartjs-plugin-streaming 2.0.0 + Moment.js
// Mirrors the original Hangfire dashboard chart rendering exactly.

(function () {
    'use strict';

    var COLORS = {
        light: {
            cartesianColor: '#e5e5e5',
            failed: { backgroundColor: '#D55251', borderColor: null },
            deleted: { backgroundColor: '#919191', borderColor: null },
            succeeded: { backgroundColor: '#6FCD6D', borderColor: '#62B35F' }
        },
        dark: {
            cartesianColor: '#5f5f5f',
            failed: { backgroundColor: 'rgba(215, 58, 74, 0.4)', borderColor: null },
            deleted: { backgroundColor: 'rgba(204, 204, 204, 0.4)', borderColor: null },
            succeeded: { backgroundColor: 'rgba(87, 171, 90, 0.4)', borderColor: 'rgba(87, 171, 90, 1)' }
        }
    };

    function getColorScheme() {
        return document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'dark' : 'light';
    }

    function changeDatasetColorScheme(newScheme) {
        var colors = COLORS[newScheme];
        this._chart.data.datasets[0].backgroundColor = colors.succeeded.backgroundColor;
        this._chart.data.datasets[0].borderColor = colors.succeeded.borderColor;
        this._chart.data.datasets[1].backgroundColor = colors.deleted.backgroundColor;
        this._chart.data.datasets[1].borderColor = colors.deleted.borderColor;
        this._chart.data.datasets[2].backgroundColor = colors.failed.backgroundColor;
        this._chart.data.datasets[2].borderColor = colors.failed.borderColor;
        this._chart.options.scales.x.grid.color = colors.cartesianColor;
        this._chart.options.scales.y.grid.color = colors.cartesianColor;
        this._chart.update();
    }

    var colorScheme = getColorScheme();

    window.dashboardCharts = {
        _realtimeChart: null,
        _historyChart: null,
        _succeeded: null,
        _failed: null,
        _deleted: null,
        _pollInterval: 3000,

        initRealtimeChart: function (canvasId) {
            var canvas = document.getElementById(canvasId);
            if (!canvas) return;

            if (this._realtimeChart) {
                this._realtimeChart.destroy();
                this._realtimeChart = null;
            }

            colorScheme = getColorScheme();
            var colors = COLORS[colorScheme];

            this._realtimeChart = new Chart(canvas, {
                type: 'line',
                data: {
                    datasets: [
                        {
                            label: 'Succeeded',
                            borderColor: colors.succeeded.borderColor,
                            backgroundColor: colors.succeeded.backgroundColor,
                            borderWidth: 2,
                            data: []
                        },
                        {
                            label: 'Deleted',
                            borderColor: colors.deleted.borderColor,
                            backgroundColor: colors.deleted.backgroundColor,
                            borderWidth: 2,
                            data: []
                        },
                        {
                            label: 'Failed',
                            borderColor: colors.failed.borderColor,
                            backgroundColor: colors.failed.backgroundColor,
                            borderWidth: 2,
                            data: []
                        }
                    ]
                },
                options: {
                    scales: {
                        x: {
                            type: 'realtime',
                            realtime: {
                                duration: 60000,
                                delay: this._pollInterval
                            },
                            time: {
                                unit: 'second',
                                tooltipFormat: 'LL LTS',
                                displayFormats: { second: 'LTS', minute: 'LTS' }
                            },
                            grid: { color: colors.cartesianColor },
                            ticks: { maxRotation: 0 }
                        },
                        y: {
                            grid: { color: colors.cartesianColor },
                            ticks: { beginAtZero: true, precision: 0, min: 0, maxTicksLimit: 6, suggestedMax: 10 },
                            stacked: true,
                            min: 0,
                            suggestedMax: 10
                        }
                    },
                    reponsive: true,
                    elements: { line: { tension: 0 }, point: { radius: 0 } },
                    animation: { duration: 0 },
                    hover: { animationDuration: 0 },
                    plugins: {
                        legend: { display: false },
                        tooltip: { mode: 'index', intersect: false }
                    }
                }
            });

            this._realtimeChart.changeDatasetColorScheme = changeDatasetColorScheme.bind({ _chart: this._realtimeChart });
        },

        updateRealtimeChart: function (succeeded, failed, deleted) {
            if (!this._realtimeChart) return;

            var now = Date.now();

            this._realtimeChart.data.datasets[0].data.push({ x: now, y: succeeded });
            this._realtimeChart.data.datasets[1].data.push({ x: now, y: deleted });
            this._realtimeChart.data.datasets[2].data.push({ x: now, y: failed });

            this._realtimeChart.update();
        },

        initHistoryChart: function (canvasId, succeededData, failedData) {
            var canvas = document.getElementById(canvasId);
            if (!canvas) return;

            if (this._historyChart) {
                this._historyChart.destroy();
                this._historyChart = null;
            }

            colorScheme = getColorScheme();
            var colors = COLORS[colorScheme];

            // Convert array data to {x, y} format with timestamps
            var now = Date.now();
            var hoursCount = succeededData ? succeededData.length : 24;
            var succeededPoints = [];
            var failedPoints = [];
            var deletedPoints = [];

            for (var i = 0; i < hoursCount; i++) {
                var timestamp = now - (hoursCount - 1 - i) * 3600000;
                succeededPoints.push({ x: timestamp, y: succeededData ? succeededData[i] : 0 });
                failedPoints.push({ x: timestamp, y: failedData ? failedData[i] : 0 });
                deletedPoints.push({ x: timestamp, y: 0 });
            }

            this._historyChart = new Chart(canvas, {
                type: 'line',
                data: {
                    datasets: [
                        {
                            label: 'Succeeded',
                            borderColor: colors.succeeded.borderColor,
                            backgroundColor: colors.succeeded.backgroundColor,
                            borderWidth: 2,
                            data: succeededPoints
                        },
                        {
                            label: 'Deleted',
                            borderColor: colors.deleted.borderColor,
                            backgroundColor: colors.deleted.backgroundColor,
                            borderWidth: 2,
                            data: deletedPoints
                        },
                        {
                            label: 'Failed',
                            borderColor: colors.failed.borderColor,
                            backgroundColor: colors.failed.backgroundColor,
                            borderWidth: 2,
                            data: failedPoints
                        }
                    ]
                },
                options: {
                    scales: {
                        x: {
                            type: 'time',
                            time: {
                                unit: 'hour',
                                tooltipFormat: 'LLL',
                                displayFormats: { hour: 'LT', day: 'll' }
                            },
                            grid: { color: colors.cartesianColor },
                            ticks: { maxRotation: 0 }
                        },
                        y: {
                            grid: { color: colors.cartesianColor },
                            ticks: { beginAtZero: true, precision: 0, maxTicksLimit: 6 },
                            stacked: true,
                            min: 0,
                            suggestedMax: 10
                        }
                    },
                    elements: { line: { tension: 0 }, point: { radius: 0 } },
                    plugins: {
                        legend: { display: false },
                        tooltip: { mode: 'index', intersect: false },
                        streaming: false
                    }
                }
            });

            this._historyChart.changeDatasetColorScheme = changeDatasetColorScheme.bind({ _chart: this._historyChart });
        },

        destroyCharts: function () {
            if (this._realtimeChart) {
                this._realtimeChart.destroy();
                this._realtimeChart = null;
            }
            if (this._historyChart) {
                this._historyChart.destroy();
                this._historyChart = null;
            }
        }
    };

    // Listen for theme changes
    var observer = new MutationObserver(function (mutations) {
        mutations.forEach(function (mutation) {
            if (mutation.attributeName === 'data-bs-theme') {
                var newScheme = getColorScheme();
                if (newScheme !== colorScheme) {
                    colorScheme = newScheme;
                    if (window.dashboardCharts._realtimeChart) {
                        window.dashboardCharts._realtimeChart.changeDatasetColorScheme(newScheme);
                    }
                    if (window.dashboardCharts._historyChart) {
                        window.dashboardCharts._historyChart.changeDatasetColorScheme(newScheme);
                    }
                }
            }
        });
    });
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme'] });
})();
