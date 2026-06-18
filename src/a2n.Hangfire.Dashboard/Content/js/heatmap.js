// Recurring Schedule Heatmap — Chart.js / DOM renderers (heatmap.js)
// ===========================================================================
// Rendering layer for the Recurring Schedule Heatmap feature.
//
//  - Punchcard bubbles, Calendar / Queue×Hour / Per-queue shading (focusable
//    DOM grids), and a stacked concurrency chart with a capacity reference
//    line (Chart.js).
//  - Color-ramp + log/linear intensity scaling and WCAG 4.5:1 contrast label
//    color selection mirror the pure C# helper in Heatmap/Intensity.cs.
//  - Reads the EFFECTIVE theme already resolved + persisted by the dashboard's
//    existing theme.js mechanism (the `data-bs-theme` attribute on <html>).
//    It does NOT independently detect the OS/browser color-scheme (Req 15.2,
//    15.7) — that resolution (including Auto and localStorage) is owned by
//    theme.js, exactly like analyticsCharts.js.
//  - Hover/focus tooltips, keyboard navigation, and responsive layout
//    (Req 15.1, 15.4, 15.6, 24.4, 24.5).
//
// Public API: window.heatmapCharts (see bottom of file).
// ===========================================================================

(function () {
    'use strict';

    // -----------------------------------------------------------------------
    // Color ramps + failure palette (theme-aware), mirroring Heatmap/Intensity.cs
    // and the agreed v4 mockup. Index 0 is the empty/minimum shade, the last
    // index is the maximum-intensity shade.
    // -----------------------------------------------------------------------
    var RAMP = {
        light: ['#eef1f5', '#cfe8ef', '#8fd3c7', '#4cb3a9', '#2f8f9e', '#1f5f86'],
        dark: ['#1c2434', '#1f3b4d', '#1f5f6b', '#2f8f8a', '#4cc0a8', '#8fe3c7']
    };
    var RAMP_SIZE = 6;

    // Failure-rate palette (percent thresholds) for the Historical source.
    var FAILS = {
        light: { ok: '#198754', warn: '#fd7e14', high: '#e8590c', danger: '#dc3545' },
        dark: { ok: '#2f9e44', warn: '#fd7e14', high: '#e8590c', danger: '#e03131' }
    };

    // Stable fallback queue colors derived from the queue name when the caller
    // does not supply an explicit color.
    var QUEUE_PALETTE = ['#4dabf7', '#f783ac', '#ffa94d', '#38d9a9', '#b197fc', '#ffe066', '#ff8787', '#9775fa', '#74c0fc', '#63e6be'];

    // -----------------------------------------------------------------------
    // Theme — read the effective theme resolved by theme.js (Req 15.2/15.7).
    // -----------------------------------------------------------------------
    function getEffectiveTheme() {
        // theme.js writes the resolved (Light/Dark) value to data-bs-theme on
        // <html>, having already handled Auto + OS detection + persistence.
        // When the attribute is missing/ambiguous we fall back to light
        // (Auto resolves to Light per Req 15.7).
        return document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'dark' : 'light';
    }

    function ramp() {
        return RAMP[getEffectiveTheme()];
    }

    function failPalette() {
        return FAILS[getEffectiveTheme()];
    }

    // -----------------------------------------------------------------------
    // Intensity mapping (mirror of Heatmap/Intensity.cs) — monotonic and
    // endpoint-normalized under both linear and logarithmic scales (Req 20.4,
    // 20.5, 3.4, 3.5, 6.1, 6.2).
    // -----------------------------------------------------------------------
    function isFiniteNum(v) {
        return typeof v === 'number' && isFinite(v);
    }

    function clamp01(v) {
        if (v < 0) return 0;
        return v > 1 ? 1 : v;
    }

    // Normalize a value onto [0,1] across [min,max]. min==max (or invalid) -> 0.
    function normalize(value, min, max, logScale) {
        if (!isFiniteNum(min) || !isFiniteNum(max) || max <= min) {
            return 0;
        }
        if (!isFiniteNum(value)) {
            return isNaN(value) ? 0 : (value > max ? 1 : 0);
        }
        if (value <= min) return 0;
        if (value >= max) return 1;

        if (logScale) {
            var num = Math.log(1 + (value - min));
            var den = Math.log(1 + (max - min));
            return den > 0 ? clamp01(num / den) : 0;
        }
        return clamp01((value - min) / (max - min));
    }

    // Map a value to a discrete ramp index in [0, size-1].
    function rampIndex(value, min, max, logScale, size) {
        var n = size || RAMP_SIZE;
        if (n < 1) n = 1;
        if (n === 1) return 0;
        var t = normalize(value, min, max, logScale);
        var idx = Math.round(t * (n - 1));
        if (idx < 0) idx = 0;
        else if (idx > n - 1) idx = n - 1;
        return idx;
    }

    // Ramp hex for a value under the active theme.
    function rampHex(value, min, max, logScale) {
        return ramp()[rampIndex(value, min, max, logScale, RAMP_SIZE)];
    }

    // Bubble radius: area-proportional encoding (radius scales with sqrt(t)).
    // Zero (or at/below domain min) -> 0 (no bubble) (Req 3.5).
    function bubbleRadius(value, min, max, maxRadius, logScale) {
        if (maxRadius <= 0 || value <= 0) return 0;
        var t = normalize(value, min, max, logScale);
        return maxRadius * Math.sqrt(t);
    }

    // -----------------------------------------------------------------------
    // WCAG 2.x relative luminance / contrast (mirror of Intensity.cs) used to
    // pick a label color meeting the 4.5:1 threshold (Req 15.3, 15.5).
    // -----------------------------------------------------------------------
    function parseHex(hex) {
        if (!hex) return { r: 0, g: 0, b: 0 };
        var h = hex.replace('#', '');
        if (h.length === 3) h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
        return {
            r: parseInt(h.substring(0, 2), 16),
            g: parseInt(h.substring(2, 4), 16),
            b: parseInt(h.substring(4, 6), 16)
        };
    }

    function linearizeChannel(c) {
        c = c / 255;
        return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
    }

    function relativeLuminance(hex) {
        var c = parseHex(hex);
        return 0.2126 * linearizeChannel(c.r) + 0.7152 * linearizeChannel(c.g) + 0.0722 * linearizeChannel(c.b);
    }

    function contrastRatio(hexA, hexB) {
        var la = relativeLuminance(hexA);
        var lb = relativeLuminance(hexB);
        var hi = Math.max(la, lb);
        var lo = Math.min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    // Pick the label color (black or white) with the greater contrast against
    // the background. Choosing the better of pure black/white guarantees
    // >= ~4.58:1 for any background, always meeting the 4.5:1 threshold.
    function pickLabelColor(backgroundHex) {
        var cb = contrastRatio('#000000', backgroundHex);
        var cw = contrastRatio('#ffffff', backgroundHex);
        return cb >= cw ? '#000000' : '#ffffff';
    }

    // Failure-rate (0..100 percent) to a palette color (Historical source).
    function failHex(percent) {
        var f = failPalette();
        if (percent < 8) return f.ok;
        if (percent < 15) return f.warn;
        if (percent < 25) return f.high;
        return f.danger;
    }

    // Deterministic fallback color for a queue name.
    function queueColor(name) {
        if (!name) return QUEUE_PALETTE[0];
        var seed = 7;
        for (var i = 0; i < name.length; i++) {
            seed = (seed * 31 + name.charCodeAt(i)) >>> 0;
        }
        return QUEUE_PALETTE[seed % QUEUE_PALETTE.length];
    }

    // Resolve the fill color for a cell given the active color mode.
    function cellFill(cell, model) {
        var mode = model.colorMode || 'ramp';
        if (mode === 'failure') {
            // Historical failure-rate tint; cells with no fires use the empty shade.
            if (cell.fireCount && cell.fireCount > 0) {
                var pct = Math.round((cell.failureRate != null ? cell.failureRate : 0) * 100);
                return failHex(pct);
            }
            return ramp()[0];
        }
        // ramp / duration: monotonic shade over [min,max]; zero -> empty shade.
        if (!cell.value || cell.value <= 0) {
            return ramp()[0];
        }
        return rampHex(cell.value, model.min, model.max, !!model.logScale);
    }

    // -----------------------------------------------------------------------
    // Tooltip — a single shared floating element, shown on hover AND focus
    // (Req 15.6). Created lazily and reused.
    // -----------------------------------------------------------------------
    var tipEl = null;

    function ensureTip() {
        if (tipEl) return tipEl;
        tipEl = document.createElement('div');
        tipEl.className = 'heatmap-tip';
        tipEl.setAttribute('role', 'tooltip');
        tipEl.style.cssText = [
            'position:fixed', 'z-index:1080', 'display:none', 'pointer-events:none',
            'max-width:280px', 'padding:6px 9px', 'border-radius:6px', 'font-size:12px',
            'line-height:1.35', 'box-shadow:0 2px 10px rgba(0,0,0,.25)'
        ].join(';');
        document.body.appendChild(tipEl);
        return tipEl;
    }

    function styleTipForTheme() {
        var el = ensureTip();
        if (getEffectiveTheme() === 'dark') {
            el.style.background = '#2b3035';
            el.style.color = '#dee2e6';
            el.style.border = '1px solid #5f5f5f';
        } else {
            el.style.background = '#ffffff';
            el.style.color = '#212529';
            el.style.border = '1px solid #e5e5e5';
        }
    }

    function showTip(html, x, y) {
        var el = ensureTip();
        styleTipForTheme();
        el.innerHTML = html;
        el.style.display = 'block';
        positionTip(x, y);
    }

    function positionTip(x, y) {
        var el = ensureTip();
        var pad = 14;
        var r = el.getBoundingClientRect();
        var left = x + pad;
        var top = y + pad;
        if (left + r.width > window.innerWidth) left = x - r.width - pad;
        if (top + r.height > window.innerHeight) top = y - r.height - pad;
        if (left < 0) left = pad;
        if (top < 0) top = pad;
        el.style.left = left + 'px';
        el.style.top = top + 'px';
    }

    function hideTip() {
        if (tipEl) tipEl.style.display = 'none';
    }

    // Build the default accessible tooltip / aria text for a cell.
    function cellTooltipHtml(cell, model) {
        if (cell.tooltip) return cell.tooltip;
        var queue = cell.queue || cell.dominantQueue || '';
        var hourLabel = String(cell.hour).padStart(2, '0') + ':00';
        var dayLabel = cell.dayLabel != null ? cell.dayLabel : '';
        var metric = model.metricLabel || 'value';
        var valueText = formatValue(cell.value);
        var head = (dayLabel ? dayLabel + ' ' : '') + hourLabel + (queue ? ' · ' + escapeHtml(queue) : '');
        var body = escapeHtml(metric) + ': <b>' + valueText + '</b>';
        if (model.colorMode === 'failure' && cell.fireCount > 0) {
            body += ' · failure ' + Math.round((cell.failureRate || 0) * 100) + '%';
        }
        return '<div style="font-weight:600;margin-bottom:2px">' + head + '</div><div>' + body + '</div>';
    }

    function cellAriaLabel(cell, model) {
        var queue = cell.queue || cell.dominantQueue || '';
        var hourLabel = String(cell.hour).padStart(2, '0') + ':00';
        var dayLabel = cell.dayLabel != null ? cell.dayLabel : '';
        var metric = model.metricLabel || 'value';
        var parts = [];
        if (queue) parts.push('queue ' + queue);
        if (dayLabel) parts.push(dayLabel);
        parts.push(hourLabel);
        parts.push(metric + ' ' + formatValue(cell.value));
        // Under the failure color mode the cell is shaded by its failure rate, so announce that rate
        // explicitly for assistive technologies (the displayed value above is the fire count).
        if (model.colorMode === 'failure' && cell.fireCount > 0) {
            parts.push('failure rate ' + Math.round((cell.failureRate || 0) * 100) + '%');
        }
        return parts.join(', ');
    }

    function formatValue(v) {
        if (v == null || isNaN(v)) return '0';
        if (v >= 10) return String(Math.round(v));
        if (v < 1 && v > 0) return v.toFixed(1);
        return String(Math.round(v));
    }

    function escapeHtml(s) {
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    // -----------------------------------------------------------------------
    // Keyboard navigation across a grid of focusable cells (Req 24.5).
    // Implements a roving tabindex with arrow-key / Home / End movement.
    // -----------------------------------------------------------------------
    function wireGridKeyboard(gridEl, cols) {
        var cells = Array.prototype.slice.call(gridEl.querySelectorAll('[data-hm-cell]'));
        if (!cells.length) return;
        cells.forEach(function (c, i) {
            c.setAttribute('tabindex', i === 0 ? '0' : '-1');
        });

        function focusAt(i) {
            if (i < 0) i = 0;
            if (i > cells.length - 1) i = cells.length - 1;
            cells.forEach(function (c) { c.setAttribute('tabindex', '-1'); });
            cells[i].setAttribute('tabindex', '0');
            cells[i].focus();
        }

        gridEl.addEventListener('keydown', function (e) {
            var idx = cells.indexOf(document.activeElement);
            if (idx < 0) return;
            var handled = true;
            switch (e.key) {
                case 'ArrowRight': focusAt(idx + 1); break;
                case 'ArrowLeft': focusAt(idx - 1); break;
                case 'ArrowDown': focusAt(idx + cols); break;
                case 'ArrowUp': focusAt(idx - cols); break;
                case 'Home': focusAt(idx - (idx % cols)); break;
                case 'End': focusAt(idx - (idx % cols) + (cols - 1)); break;
                default: handled = false;
            }
            if (handled) e.preventDefault();
        });
    }

    // Attach hover + focus tooltip behavior and aria-label to a focusable cell.
    function wireCellTip(el, cell, model) {
        var html = cellTooltipHtml(cell, model);
        el.setAttribute('role', 'gridcell');
        el.setAttribute('aria-label', cellAriaLabel(cell, model));
        el.addEventListener('mousemove', function (e) { showTip(html, e.clientX, e.clientY); });
        el.addEventListener('mouseleave', hideTip);
        el.addEventListener('focus', function () {
            var r = el.getBoundingClientRect();
            showTip(html, r.left + r.width / 2, r.top + r.height / 2);
        });
        el.addEventListener('blur', hideTip);
    }

    // -----------------------------------------------------------------------
    // Shared grid scaffolding helpers (responsive: wrapped in an overflow-x
    // container so the 24-hour grid stays legible on small viewports — Req 24.4).
    // -----------------------------------------------------------------------
    function clearContainer(container) {
        while (container.firstChild) container.removeChild(container.firstChild);
    }

    function makeHourHeader(hours, labelColWidth) {
        var header = document.createElement('div');
        header.style.cssText = 'display:grid;grid-template-columns:' + labelColWidth + 'px repeat(' + hours.length +
            ',minmax(18px,1fr));gap:2px;margin-bottom:2px';
        header.setAttribute('aria-hidden', 'true');
        var corner = document.createElement('div');
        header.appendChild(corner);
        hours.forEach(function (h) {
            var c = document.createElement('div');
            c.style.cssText = 'font-size:10px;text-align:center;color:var(--bs-secondary-color,#6c757d)';
            c.textContent = (h % 3 === 0) ? String(h).padStart(2, '0') : '';
            header.appendChild(c);
        });
        return header;
    }

    // -----------------------------------------------------------------------
    // PUNCHCARD — day × hour bubble matrix (Req 3.4, 3.5, 3.6, 7.6).
    // model: { days:[label], dayIndices:[n], hours:[0..23], cells:[{day,hour,value,
    //          dominantQueue, dominantQueueColor, failureRate, fireCount, tooltip}],
    //          min, max, logScale, source:'projected'|'historical', metricLabel,
    //          nowDay, nowHour }
    // -----------------------------------------------------------------------
    function renderPunchcard(container, model) {
        var hours = model.hours || range24();
        var labelW = 44;
        var maxRadius = 11;

        var grid = document.createElement('div');
        grid.setAttribute('role', 'grid');
        grid.setAttribute('aria-label', 'Punchcard: scheduling density by day and hour');

        grid.appendChild(makeHourHeader(hours, labelW));

        var cellLookup = indexCells(model.cells);

        model.days.forEach(function (dayLabel, rowIdx) {
            var dayIndex = model.dayIndices ? model.dayIndices[rowIdx] : rowIdx;
            var row = document.createElement('div');
            row.setAttribute('role', 'row');
            row.style.cssText = 'display:grid;grid-template-columns:' + labelW + 'px repeat(' + hours.length +
                ',minmax(18px,1fr));gap:2px;margin-bottom:2px;align-items:center';

            var lbl = document.createElement('div');
            lbl.setAttribute('role', 'rowheader');
            lbl.style.cssText = 'font-size:11px;text-align:right;padding-right:6px;color:var(--bs-body-color)';
            lbl.textContent = dayLabel;
            row.appendChild(lbl);

            hours.forEach(function (h) {
                var data = cellLookup[dayIndex + ':' + h] || { day: dayIndex, hour: h, value: 0 };
                var cell = mergeCell(data, dayIndex, h, dayLabel);
                var el = document.createElement('div');
                el.setAttribute('data-hm-cell', '');
                el.style.cssText = 'position:relative;aspect-ratio:1/1;min-height:18px;display:flex;' +
                    'align-items:center;justify-content:center;border-radius:3px;outline-offset:1px';
                el.style.background = 'transparent';

                if (model.nowDay === dayIndex && model.nowHour === h) {
                    el.style.boxShadow = 'inset 0 0 0 2px var(--bs-primary,#0d6efd)';
                }

                if (cell.value > 0) {
                    var r = bubbleRadius(cell.value, model.min, model.max, maxRadius, !!model.logScale);
                    var sz = Math.max(4, r * 2);
                    var dot = document.createElement('div');
                    var color;
                    if (model.source === 'historical') {
                        color = failHex(Math.round((cell.failureRate || 0) * 100));
                    } else {
                        color = cell.dominantQueueColor || queueColor(cell.dominantQueue || cell.queue);
                    }
                    dot.style.cssText = 'width:' + sz.toFixed(1) + 'px;height:' + sz.toFixed(1) +
                        'px;border-radius:50%;background:' + color + ';box-shadow:0 0 0 1px rgba(0,0,0,.2)';
                    el.appendChild(dot);
                }

                wireCellTip(el, cell, model);
                row.appendChild(el);
            });

            grid.appendChild(row);
        });

        var wrapper = mountResponsive(container, grid);
        wireGridKeyboard(grid, hours.length);
        return wrapper;
    }

    // -----------------------------------------------------------------------
    // CALENDAR — 7 × 24 shaded matrix with neutral ramp, empty shade for zero,
    // current-cell marker, and color-by modes (Req 6.1, 6.2, 6.3, 6.4, 6.5).
    // model adds: colorMode:'ramp'|'failure'|'duration'
    // -----------------------------------------------------------------------
    function renderCalendar(container, model) {
        return renderShadedGrid(container, model, {
            ariaLabel: 'Calendar heatmap: load by day and hour',
            showLabels: true,
            marker: true
        });
    }

    // -----------------------------------------------------------------------
    // QUEUE × HOUR — one row per visible queue × 24 hour columns, shaded with
    // numeric labels (Req 3.1, 3.2, 3.3).
    // model: { rows:[{queue,isAdHoc,queueColor,cells:[{hour,value,fireCount,
    //          failureRate,tooltip}]}], hours, min, max, logScale, colorMode,
    //          metricLabel, dayLabel }
    // -----------------------------------------------------------------------
    function renderQueueHour(container, model) {
        var hours = model.hours || range24();
        var labelW = 120;

        var grid = document.createElement('div');
        grid.setAttribute('role', 'grid');
        grid.setAttribute('aria-label', 'Queue by hour heatmap');
        grid.appendChild(makeHourHeader(hours, labelW));

        (model.rows || []).forEach(function (rowModel) {
            var cellLookup = {};
            (rowModel.cells || []).forEach(function (c) { cellLookup[c.hour] = c; });

            var row = document.createElement('div');
            row.setAttribute('role', 'row');
            row.style.cssText = 'display:grid;grid-template-columns:' + labelW + 'px repeat(' + hours.length +
                ',minmax(20px,1fr));gap:2px;margin-bottom:2px;align-items:stretch';

            var lbl = document.createElement('div');
            lbl.setAttribute('role', 'rowheader');
            lbl.style.cssText = 'font-size:11px;display:flex;align-items:center;gap:5px;color:var(--bs-body-color);overflow:hidden';
            var dot = document.createElement('span');
            dot.style.cssText = 'flex:0 0 auto;width:9px;height:9px;border-radius:50%;background:' +
                (rowModel.queueColor || queueColor(rowModel.queue));
            lbl.appendChild(dot);
            var name = document.createElement('span');
            name.style.cssText = 'overflow:hidden;text-overflow:ellipsis;white-space:nowrap';
            name.textContent = rowModel.queue;
            lbl.appendChild(name);
            if (rowModel.isAdHoc != null) {
                var tag = document.createElement('span');
                tag.style.cssText = 'flex:0 0 auto;font-size:9px;opacity:.6';
                tag.textContent = rowModel.isAdHoc ? 'ad-hoc' : 'cron';
                lbl.appendChild(tag);
            }
            row.appendChild(lbl);

            hours.forEach(function (h) {
                var data = cellLookup[h] || { hour: h, value: 0 };
                var cell = mergeCell(data, null, h, model.dayLabel);
                cell.queue = rowModel.queue;
                var el = document.createElement('div');
                el.setAttribute('data-hm-cell', '');
                el.style.cssText = 'min-height:20px;display:flex;align-items:center;justify-content:center;' +
                    'border-radius:3px;font-size:10px;outline-offset:1px';
                var fill = cellFill(cell, model);
                el.style.background = fill;
                if (cell.value > 0) {
                    el.textContent = formatValue(cell.value);
                    el.style.color = pickLabelColor(fill);
                }
                wireCellTip(el, cell, model);
                row.appendChild(el);
            });

            grid.appendChild(row);
        });

        var wrapper = mountResponsive(container, grid);
        wireGridKeyboard(grid, hours.length);
        return wrapper;
    }

    // -----------------------------------------------------------------------
    // PER-QUEUE small multiples — one day × hour heatmap per visible queue,
    // all sharing a single global ramp domain [globalMin, globalMax] (Req 3.7).
    // model: { queues:[{queue,isAdHoc,queueColor,days,dayIndices,cells:[{day,hour,
    //          value}], max}], globalMin, globalMax, logScale, hours, metricLabel }
    // -----------------------------------------------------------------------
    function renderPerQueue(container, model) {
        var hours = model.hours || range24();
        clearContainer(container);

        var grid = document.createElement('div');
        grid.style.cssText = 'display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:12px';

        (model.queues || []).forEach(function (q) {
            var card = document.createElement('div');
            card.style.cssText = 'border:1px solid var(--bs-border-color,#dee2e6);border-radius:6px;padding:8px';

            var title = document.createElement('div');
            title.style.cssText = 'display:flex;align-items:center;gap:6px;font-size:12px;margin-bottom:6px;color:var(--bs-body-color)';
            var dot = document.createElement('span');
            dot.style.cssText = 'width:9px;height:9px;border-radius:50%;background:' + (q.queueColor || queueColor(q.queue));
            title.appendChild(dot);
            var name = document.createElement('strong');
            name.textContent = q.queue;
            title.appendChild(name);
            if (q.isAdHoc != null) {
                var tag = document.createElement('span');
                tag.style.cssText = 'font-size:9px;opacity:.6';
                tag.textContent = q.isAdHoc ? 'ad-hoc' : 'cron';
                title.appendChild(tag);
            }
            card.appendChild(title);

            // Render a compact shaded grid sharing the global ramp domain.
            var subModel = {
                days: q.days,
                dayIndices: q.dayIndices,
                hours: hours,
                cells: (q.cells || []).map(function (c) {
                    return { day: c.day, hour: c.hour, value: c.value, queue: q.queue };
                }),
                min: model.globalMin,
                max: model.globalMax,
                logScale: model.logScale,
                colorMode: 'ramp',
                metricLabel: model.metricLabel
            };
            var inner = document.createElement('div');
            card.appendChild(inner);
            renderShadedGrid(inner, subModel, {
                ariaLabel: 'Heatmap for queue ' + q.queue,
                showLabels: true,
                marker: false,
                compact: true,
                showValueLabels: false
            });

            grid.appendChild(card);
        });

        container.appendChild(grid);
        return grid;
    }

    // Generic shaded day × hour grid used by Calendar + Per-queue.
    function renderShadedGrid(container, model, opts) {
        opts = opts || {};
        var hours = model.hours || range24();
        var labelW = opts.compact ? 16 : 44;
        var minH = opts.compact ? 12 : 22;

        var grid = document.createElement('div');
        grid.setAttribute('role', 'grid');
        grid.setAttribute('aria-label', opts.ariaLabel || 'Heatmap');

        if (!opts.compact) {
            grid.appendChild(makeHourHeader(hours, labelW));
        }

        var cellLookup = indexCells(model.cells);

        model.days.forEach(function (dayLabel, rowIdx) {
            var dayIndex = model.dayIndices ? model.dayIndices[rowIdx] : rowIdx;
            var row = document.createElement('div');
            row.setAttribute('role', 'row');
            row.style.cssText = 'display:grid;grid-template-columns:' + labelW + 'px repeat(' + hours.length +
                ',minmax(' + (opts.compact ? 8 : 20) + 'px,1fr));gap:2px;margin-bottom:2px';

            var lbl = document.createElement('div');
            lbl.setAttribute('role', 'rowheader');
            lbl.style.cssText = 'font-size:' + (opts.compact ? 9 : 11) + 'px;text-align:right;padding-right:' +
                (opts.compact ? 3 : 6) + 'px;display:flex;align-items:center;justify-content:flex-end;color:var(--bs-body-color)';
            lbl.textContent = opts.compact ? String(dayLabel).charAt(0) : dayLabel;
            row.appendChild(lbl);

            hours.forEach(function (h) {
                var data = cellLookup[dayIndex + ':' + h] || { day: dayIndex, hour: h, value: 0 };
                var cell = mergeCell(data, dayIndex, h, dayLabel);
                var el = document.createElement('div');
                el.setAttribute('data-hm-cell', '');
                el.style.cssText = 'min-height:' + minH + 'px;display:flex;align-items:center;justify-content:center;' +
                    'border-radius:3px;font-size:10px;outline-offset:1px';
                var fill = cellFill(cell, model);
                el.style.background = fill;

                if (opts.marker && model.nowDay === dayIndex && model.nowHour === h) {
                    el.style.boxShadow = 'inset 0 0 0 2px var(--bs-primary,#0d6efd)';
                }

                if (opts.showValueLabels !== false && !opts.compact && cell.value > 0) {
                    el.textContent = formatValue(cell.value);
                    el.style.color = pickLabelColor(fill);
                }

                wireCellTip(el, cell, model);
                row.appendChild(el);
            });

            grid.appendChild(row);
        });

        var wrapper = mountResponsive(container, grid);
        wireGridKeyboard(grid, hours.length);
        return wrapper;
    }

    // -----------------------------------------------------------------------
    // CONCURRENCY — stacked-by-layer concurrency over a day with a capacity
    // reference line; over-capacity buckets flagged (Req 4.8, 4.9, 19.2).
    // Uses Chart.js (stacked bar) + a custom capacity-line plugin (no extra libs).
    // model: { labels:[...], adhoc:[...], cron:[...], capacity:N,
    //          peak, peakMinute, worstDayLabel }
    // -----------------------------------------------------------------------
    function renderConcurrency(canvasId, model) {
        var canvas = document.getElementById(canvasId);
        if (!canvas || typeof Chart === 'undefined') return false;

        destroyChart(canvasId);

        var theme = getEffectiveTheme();
        var textColor = theme === 'dark' ? '#dee2e6' : '#212529';
        var gridColor = theme === 'dark' ? '#5f5f5f' : '#e5e5e5';
        var cronColor = theme === 'dark' ? '#6ea8fe' : '#0d6efd';
        var adhocColor = theme === 'dark' ? '#adb5bd' : '#868e96';
        var capColor = theme === 'dark' ? '#ffda6a' : '#d63384';
        var dangerColor = theme === 'dark' ? '#ea868f' : '#dc3545';
        var capacity = model.capacity || 0;

        var labels = model.labels || [];
        var adhoc = model.adhoc || [];
        var cron = model.cron || [];

        // Flag over-capacity buckets with a distinct border (Req 4.9).
        var cronBorder = labels.map(function (_, i) {
            var total = (adhoc[i] || 0) + (cron[i] || 0);
            return total > capacity ? dangerColor : 'transparent';
        });

        var datasets = [];
        if (adhoc.length) {
            datasets.push({
                label: 'Ad-hoc baseline',
                data: adhoc,
                backgroundColor: hexToRgba(adhocColor, 0.55),
                borderWidth: 0,
                stack: 'concurrency'
            });
        }
        datasets.push({
            label: 'Cron',
            data: cron,
            backgroundColor: hexToRgba(cronColor, 0.9),
            borderColor: cronBorder,
            borderWidth: { top: 2, left: 0, right: 0, bottom: 0 },
            stack: 'concurrency'
        });

        var capacityLinePlugin = {
            id: 'heatmapCapacityLine',
            afterDatasetsDraw: function (chart) {
                if (!capacity || capacity <= 0) return;
                var yScale = chart.scales.y;
                var area = chart.chartArea;
                if (!yScale || !area) return;
                var y = yScale.getPixelForValue(capacity);
                var ctx = chart.ctx;
                ctx.save();
                ctx.beginPath();
                ctx.setLineDash([5, 4]);
                ctx.lineWidth = 1.5;
                ctx.strokeStyle = capColor;
                ctx.moveTo(area.left, y);
                ctx.lineTo(area.right, y);
                ctx.stroke();
                ctx.setLineDash([]);
                ctx.fillStyle = capColor;
                ctx.font = '10px sans-serif';
                ctx.textAlign = 'right';
                ctx.fillText('capacity ' + capacity, area.right - 4, y - 4);
                ctx.restore();
            }
        };

        var chart = new Chart(canvas, {
            type: 'bar',
            data: { labels: labels, datasets: datasets },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    streaming: false,
                    legend: { display: true, position: 'top', labels: { color: textColor } },
                    tooltip: {
                        mode: 'index',
                        intersect: false,
                        callbacks: {
                            footer: function (items) {
                                var total = items.reduce(function (a, it) { return a + (it.parsed.y || 0); }, 0);
                                var note = capacity && total > capacity ? '  ⚠ over capacity' : '';
                                return 'total: ' + total.toFixed(1) + note;
                            }
                        }
                    }
                },
                scales: {
                    x: {
                        stacked: true,
                        grid: { color: gridColor },
                        ticks: { color: textColor, maxRotation: 0, autoSkip: true }
                    },
                    y: {
                        stacked: true,
                        beginAtZero: true,
                        grid: { color: gridColor },
                        ticks: { color: textColor },
                        title: { display: true, text: 'concurrent jobs', color: textColor }
                    }
                },
                interaction: { mode: 'index', intersect: false }
            },
            plugins: [capacityLinePlugin]
        });

        chart._heatmapMeta = { type: 'concurrency' };
        chartInstances.set(canvasId, chart);
        return true;
    }

    function hexToRgba(hex, alpha) {
        var c = parseHex(hex);
        return 'rgba(' + c.r + ',' + c.g + ',' + c.b + ',' + alpha + ')';
    }

    // -----------------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------------
    function range24() {
        var a = [];
        for (var h = 0; h < 24; h++) a.push(h);
        return a;
    }

    function indexCells(cells) {
        var map = {};
        (cells || []).forEach(function (c) {
            map[c.day + ':' + c.hour] = c;
        });
        return map;
    }

    function mergeCell(data, dayIndex, hour, dayLabel) {
        return {
            day: data.day != null ? data.day : dayIndex,
            hour: data.hour != null ? data.hour : hour,
            value: data.value || 0,
            queue: data.queue,
            dominantQueue: data.dominantQueue,
            dominantQueueColor: data.dominantQueueColor,
            failureRate: data.failureRate,
            fireCount: data.fireCount,
            jobIds: data.jobIds,
            contributingJobCount: data.contributingJobCount,
            tooltip: data.tooltip,
            dayLabel: data.dayLabel != null ? data.dayLabel : dayLabel
        };
    }

    // Mount a grid inside a responsive (overflow-x) wrapper for small viewports.
    function mountResponsive(container, grid) {
        clearContainer(container);
        var wrapper = document.createElement('div');
        wrapper.style.cssText = 'overflow-x:auto;width:100%';
        var inner = document.createElement('div');
        inner.style.cssText = 'min-width:520px';
        inner.appendChild(grid);
        wrapper.appendChild(inner);
        container.appendChild(wrapper);
        return wrapper;
    }

    // -----------------------------------------------------------------------
    // Instance registry + re-render on theme change (Req 15.4, 15.5).
    // -----------------------------------------------------------------------
    var chartInstances = new Map();   // canvasId -> Chart
    var renderRegistry = new Map();   // containerId -> { fn, model }

    function destroyChart(canvasId) {
        var chart = chartInstances.get(canvasId);
        if (chart) {
            chart.destroy();
            chartInstances.delete(canvasId);
        }
    }

    function resolveContainer(target) {
        if (!target) return null;
        return typeof target === 'string' ? document.getElementById(target) : target;
    }

    // Wrap a DOM renderer so it registers for theme-change re-rendering.
    function registerAndRender(targetId, fn, model) {
        var container = resolveContainer(targetId);
        if (!container) return false;
        renderRegistry.set(container.id || targetId, { container: container, fn: fn, model: model });
        fn(container, model);
        return true;
    }

    function reRenderAllForTheme() {
        styleTipForTheme();
        // Re-render DOM grids (recomputes ramp shades + label contrast).
        renderRegistry.forEach(function (entry) {
            if (entry.container && document.body.contains(entry.container)) {
                entry.fn(entry.container, entry.model);
            }
        });
        // Re-render Chart.js concurrency charts with theme colors.
        chartInstances.forEach(function (chart, canvasId) {
            var meta = chart._heatmapMeta;
            var reg = renderRegistry.get(canvasId);
            if (meta && meta.type === 'concurrency' && reg) {
                reg.fn(canvasId, reg.model);
            }
        });
    }

    var currentTheme = getEffectiveTheme();
    var themeObserver = new MutationObserver(function (mutations) {
        mutations.forEach(function (m) {
            if (m.attributeName === 'data-bs-theme') {
                var t = getEffectiveTheme();
                if (t !== currentTheme) {
                    currentTheme = t;
                    reRenderAllForTheme();
                }
            }
        });
    });
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme'] });

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------
    window.heatmapCharts = {
        // View renderers (DOM grids — focusable, accessible).
        renderPunchcard: function (target, model) {
            return registerAndRender(target, renderPunchcard, model);
        },
        renderCalendar: function (target, model) {
            return registerAndRender(target, renderCalendar, model);
        },
        renderQueueHour: function (target, model) {
            return registerAndRender(target, renderQueueHour, model);
        },
        renderPerQueue: function (target, model) {
            return registerAndRender(target, renderPerQueue, model);
        },

        // Concurrency time-series (Chart.js).
        renderConcurrency: function (canvasId, model) {
            renderRegistry.set(canvasId, { container: null, fn: renderConcurrency, model: model });
            return renderConcurrency(canvasId, model);
        },

        // Cleanup.
        destroy: function (id) {
            destroyChart(id);
            renderRegistry.delete(id);
            var c = resolveContainer(id);
            if (c) clearContainer(c);
        },
        destroyAll: function () {
            chartInstances.forEach(function (chart) { if (chart) chart.destroy(); });
            chartInstances.clear();
            renderRegistry.clear();
        },

        // Exposed pure helpers (mirror Heatmap/Intensity.cs) — useful for the
        // Blazor views, legends, and tests.
        getEffectiveTheme: getEffectiveTheme,
        normalize: normalize,
        rampIndex: rampIndex,
        rampHex: rampHex,
        bubbleRadius: bubbleRadius,
        relativeLuminance: relativeLuminance,
        contrastRatio: contrastRatio,
        pickLabelColor: pickLabelColor,
        failHex: failHex,
        queueColor: queueColor
    };

})();
