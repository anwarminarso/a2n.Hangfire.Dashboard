// Chart.js integration for Hangfire Dashboard
// Uses lightweight canvas-based rendering

window.dashboardCharts = {
    _realtimeChart: null,
    _historyChart: null,
    _realtimeData: { succeeded: [], failed: [] },

    initRealtimeChart: function (canvasId) {
        var canvas = document.getElementById(canvasId);
        if (!canvas) return;

        var ctx = canvas.getContext('2d');
        this._realtimeCtx = ctx;
        this._realtimeCanvas = canvas;
        this._drawRealtime();
    },

    updateRealtimeChart: function (succeeded, failed) {
        var now = Date.now();
        this._realtimeData.succeeded.push({ x: now, y: succeeded });
        this._realtimeData.failed.push({ x: now, y: failed });

        // Keep last 60 seconds
        var cutoff = now - 60000;
        this._realtimeData.succeeded = this._realtimeData.succeeded.filter(p => p.x > cutoff);
        this._realtimeData.failed = this._realtimeData.failed.filter(p => p.x > cutoff);

        this._drawRealtime();
    },

    _drawRealtime: function () {
        var canvas = this._realtimeCanvas;
        var ctx = this._realtimeCtx;
        if (!canvas || !ctx) return;

        var w = canvas.width = canvas.offsetWidth * (window.devicePixelRatio || 1);
        var h = canvas.height = canvas.offsetHeight * (window.devicePixelRatio || 1);
        ctx.scale(window.devicePixelRatio || 1, window.devicePixelRatio || 1);

        var dw = canvas.offsetWidth;
        var dh = canvas.offsetHeight;

        ctx.clearRect(0, 0, dw, dh);

        var data = this._realtimeData;
        if (data.succeeded.length < 2) return;

        var now = Date.now();
        var maxVal = Math.max(
            Math.max(...data.succeeded.map(p => p.y), 1),
            Math.max(...data.failed.map(p => p.y), 1)
        );

        // Draw succeeded (green)
        this._drawLine(ctx, data.succeeded, now, dw, dh, maxVal, 'rgba(16, 185, 129, 0.6)', 'rgba(16, 185, 129, 0.1)');
        // Draw failed (red)
        this._drawLine(ctx, data.failed, now, dw, dh, maxVal, 'rgba(239, 68, 68, 0.8)', 'rgba(239, 68, 68, 0.1)');
    },

    _drawLine: function (ctx, points, now, w, h, maxVal, strokeColor, fillColor) {
        if (points.length < 2) return;

        ctx.beginPath();
        var padding = 4;
        var plotH = h - padding * 2;
        var plotW = w - padding * 2;

        for (var i = 0; i < points.length; i++) {
            var x = padding + ((points[i].x - (now - 60000)) / 60000) * plotW;
            var y = padding + plotH - (points[i].y / maxVal) * plotH;
            if (i === 0) ctx.moveTo(x, y);
            else ctx.lineTo(x, y);
        }

        ctx.strokeStyle = strokeColor;
        ctx.lineWidth = 2;
        ctx.stroke();

        // Fill area
        var lastPoint = points[points.length - 1];
        var lastX = padding + ((lastPoint.x - (now - 60000)) / 60000) * plotW;
        ctx.lineTo(lastX, padding + plotH);
        ctx.lineTo(padding + ((points[0].x - (now - 60000)) / 60000) * plotW, padding + plotH);
        ctx.closePath();
        ctx.fillStyle = fillColor;
        ctx.fill();
    },

    initHistoryChart: function (canvasId, succeededData, failedData) {
        var canvas = document.getElementById(canvasId);
        if (!canvas) return;

        var ctx = canvas.getContext('2d');
        var w = canvas.width = canvas.offsetWidth * (window.devicePixelRatio || 1);
        var h = canvas.height = canvas.offsetHeight * (window.devicePixelRatio || 1);
        ctx.scale(window.devicePixelRatio || 1, window.devicePixelRatio || 1);

        var dw = canvas.offsetWidth;
        var dh = canvas.offsetHeight;
        var padding = 4;
        var plotH = dh - padding * 2;
        var plotW = dw - padding * 2;

        if (!succeededData || succeededData.length === 0) return;

        var maxVal = Math.max(
            Math.max(...succeededData, 1),
            Math.max(...failedData, 1)
        );

        // Draw succeeded
        ctx.beginPath();
        for (var i = 0; i < succeededData.length; i++) {
            var x = padding + (i / (succeededData.length - 1)) * plotW;
            var y = padding + plotH - (succeededData[i] / maxVal) * plotH;
            if (i === 0) ctx.moveTo(x, y);
            else ctx.lineTo(x, y);
        }
        ctx.strokeStyle = 'rgba(16, 185, 129, 0.7)';
        ctx.lineWidth = 2;
        ctx.stroke();

        // Fill
        ctx.lineTo(padding + plotW, padding + plotH);
        ctx.lineTo(padding, padding + plotH);
        ctx.closePath();
        ctx.fillStyle = 'rgba(16, 185, 129, 0.08)';
        ctx.fill();

        // Draw failed
        if (failedData.some(v => v > 0)) {
            ctx.beginPath();
            for (var i = 0; i < failedData.length; i++) {
                var x = padding + (i / (failedData.length - 1)) * plotW;
                var y = padding + plotH - (failedData[i] / maxVal) * plotH;
                if (i === 0) ctx.moveTo(x, y);
                else ctx.lineTo(x, y);
            }
            ctx.strokeStyle = 'rgba(239, 68, 68, 0.7)';
            ctx.lineWidth = 2;
            ctx.stroke();
        }
    }
};
