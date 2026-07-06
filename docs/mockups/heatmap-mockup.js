// ---- Synthetic recurring-job dataset -------------------------------------
// PROJECTED data is computed from cron-ish generators (stand-in for Cronos).
// HISTORICAL data is synthesized as "actual" past runs with failures + p95,
// to demonstrate the extra color dimension storage data unlocks.
const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const MIN_PER_DAY = 1440;

const JOBS = [
  { id: 'EmailDigest.SendDaily',    cron: '0 8 * * *',       queue: 'default', sub: false, fail: 0.01, gen: () => atDaily(8, 0) },
  { id: 'Billing.GenerateInvoices', cron: '0 9 * * 1-5',     queue: 'billing', sub: false, fail: 0.18, gen: () => weekdays(9, 0) },
  { id: 'Reports.NightlyRollup',    cron: '0 2 * * *',       queue: 'reports', sub: false, fail: 0.30, gen: () => atDaily(2, 0) },
  { id: 'Reports.HourlyAggregate',  cron: '0 * * * *',       queue: 'reports', sub: false, fail: 0.04, gen: () => everyHour(0) },
  { id: 'Cache.WarmTopHour',        cron: '5 * * * *',       queue: 'default', sub: false, fail: 0.00, gen: () => everyHour(5) },
  { id: 'Sync.ShopifyStock',        cron: '*/15 * * * *',    queue: 'sync',    sub: true,  fail: 0.06, gen: () => everyN(15) },
  { id: 'Sync.SapInventory',        cron: '*/15 * * * *',    queue: 'sync',    sub: true,  fail: 0.22, gen: () => everyN(15, 2) },
  { id: 'Heartbeat.Ping',           cron: '*/5 * * * *',     queue: 'default', sub: true,  fail: 0.00, gen: () => everyN(5) },
  { id: 'Cleanup.TempFiles',        cron: '30 3 * * *',      queue: 'maint',   sub: false, fail: 0.02, gen: () => atDaily(3, 30) },
  { id: 'Backup.Database',          cron: '0 1 * * *',       queue: 'maint',   sub: false, fail: 0.08, gen: () => atDaily(1, 0) },
  { id: 'Index.Rebuild',            cron: '0 2 * * 0',       queue: 'maint',   sub: false, fail: 0.40, gen: () => weekly(6, 2, 0) },
  { id: 'Notify.WeeklySummary',     cron: '0 9 * * 1',       queue: 'default', sub: false, fail: 0.00, gen: () => weekly(0, 9, 0) },
  { id: 'Metrics.ScrapeQuarter',    cron: '*/15 * * * *',    queue: 'metrics', sub: true,  fail: 0.01, gen: () => everyN(15, 7) },
  { id: 'Webhook.RetrySweep',       cron: '*/10 * * * *',    queue: 'sync',    sub: true,  fail: 0.12, gen: () => everyN(10) },
  { id: 'Forecast.Recompute',       cron: '0 8,12,16 * * *', queue: 'reports', sub: false, fail: 0.15, gen: () => multiDaily([8,12,16], 0) },
];

function atDaily(h, m)        { return DAYS.map((_, d) => d * MIN_PER_DAY + h * 60 + m); }
function multiDaily(hours, m) { const o=[]; DAYS.forEach((_, d) => hours.forEach(h => o.push(d*MIN_PER_DAY + h*60 + m))); return o; }
function weekdays(h, m)       { return [0,1,2,3,4].map(d => d * MIN_PER_DAY + h * 60 + m); }
function weekly(day, h, m)    { return [day * MIN_PER_DAY + h * 60 + m]; }
function everyHour(m)         { const o=[]; for (let d=0; d<7; d++) for (let h=0; h<24; h++) o.push(d*MIN_PER_DAY + h*60 + m); return o; }
function everyN(n, off=0)     { const o=[]; for (let d=0; d<7; d++) for (let t=off; t<MIN_PER_DAY; t+=n) o.push(d*MIN_PER_DAY + t); return o; }

const NOW_DAY = 3, NOW_HOUR = 14;
const CAPACITY = 12;

// ---- State ---------------------------------------------------------------
let hideSub = false, logScale = false, view = 'punch', source = 'projected', metric = 'fail';
function activeJobs() { return JOBS.filter(j => !(hideSub && j.sub)); }

// Deterministic pseudo-random so the mock is stable.
function rng(seed) { let s = seed % 2147483647; if (s <= 0) s += 2147483646; return () => (s = s * 16807 % 2147483647) / 2147483647; }

// Build per-(day,hour) cells. Each cell: { runs, fails, p95, jobs:Set }.
function computeGrid() {
  const cell = Array.from({ length: 7 }, () => Array.from({ length: 24 }, () => ({ runs: 0, fails: 0, p95: 0, jobs: new Set() })));
  const minute = Array.from({ length: 7 }, () => new Array(MIN_PER_DAY).fill(0));
  for (const j of activeJobs()) {
    const r = rng(hashStr(j.id));
    for (const t of j.gen()) {
      const d = Math.floor(t / MIN_PER_DAY), mod = t % MIN_PER_DAY, h = Math.floor(mod / 60);
      const c = cell[d][h];
      c.runs++; c.jobs.add(j.id); minute[d][mod]++;
      if (source === 'historical') {
        if (r() < j.fail) c.fails++;
        c.p95 = Math.max(c.p95, Math.round(200 + r() * (j.fail > 0.2 ? 9000 : 2500)));
      }
    }
  }
  return { cell, minute };
}
function hashStr(s) { let h = 7; for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0; return h; }
function firesPerDay(j) { return Math.round(j.gen().length / 7); }

// ---- Theme-aware color tables (hex, so contrast is deterministic) --------
// We keep the ramp/fail colors as explicit hex per theme instead of reading
// CSS variables back via getComputedStyle (which doesn't reliably resolve a
// var() set through the `background` shorthand, and was falling back to black).
const RAMP = {
  light: ['#eef1f5', '#cfe8ef', '#8fd3c7', '#4cb3a9', '#2f8f9e', '#1f5f86'],
  dark:  ['#1c2434', '#1f3b4d', '#1f5f6b', '#2f8f8a', '#4cc0a8', '#8fe3c7'],
};
const FAILS = {
  light: { ok: '#198754', warn: '#fd7e14', high: '#e8590c', danger: '#dc3545' },
  dark:  { ok: '#2f9e44', warn: '#fd7e14', high: '#e8590c', danger: '#e03131' },
};
function theme() { return document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light'; }
function rampHex(i) { return RAMP[theme()][i]; }
function failHex(pct) {
  const f = FAILS[theme()];
  if (pct < 8) return f.ok;
  if (pct < 15) return f.warn;
  if (pct < 25) return f.high;
  return f.danger;
}

// ---- Contrast-aware text color (YIQ on hex, per common.js invertColor) ----
// YIQ luminance threshold 186 (proven against mid-tone teal/blue). A faint
// opposite text-shadow keeps borderline cells legible.
function inkFor(hex) {
  let h = hex.replace('#', '');
  if (h.length === 3) h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
  const r = parseInt(h.slice(0, 2), 16), g = parseInt(h.slice(2, 4), 16), b = parseInt(h.slice(4, 6), 16);
  const yiq = r * 0.299 + g * 0.587 + b * 0.114;
  return yiq > 186
    ? { color: '#10141d', shadow: '0 1px 1px rgba(255,255,255,.55)' }
    : { color: '#ffffff', shadow: '0 1px 2px rgba(0,0,0,.55)' };
}

// ---- Tooltip -------------------------------------------------------------
const tip = document.getElementById('tip');
function showTip(html, e) { tip.innerHTML = html; tip.style.display = 'block'; moveTip(e); }
function moveTip(e) {
  const pad = 14; let x = e.clientX + pad, y = e.clientY + pad;
  const r = tip.getBoundingClientRect();
  if (x + r.width > innerWidth) x = e.clientX - r.width - pad;
  if (y + r.height > innerHeight) y = e.clientY - r.height - pad;
  tip.style.left = x + 'px'; tip.style.top = y + 'px';
}
function hideTip() { tip.style.display = 'none'; }

function cellTip(d, h, c) {
  const jobs = [...c.jobs];
  let head = `<div class="t">${DAYS[d]} ${String(h).padStart(2,'0')}:00 — ${c.runs} ${source==='historical'?'runs':'fires'}</div>`;
  if (source === 'historical') {
    const pct = c.runs ? Math.round(c.fails / c.runs * 100) : 0;
    head += `<div class="j" style="color:${pct>=20?'var(--danger)':pct>=8?'var(--warn)':'var(--ok)'}">${c.fails} failed (${pct}%) · p95 ${(c.p95/1000).toFixed(1)}s</div>`;
  }
  return head + `<div class="j">${jobs.slice(0,6).join('<br>')}${jobs.length>6?`<br>+${jobs.length-6} more`:''}</div>`;
}

// failure-rate color (green -> amber -> red), only meaningful for historical
function failColor(pct) { return failHex(pct); }

// ---- Punchcard -----------------------------------------------------------
function renderPunch() {
  const { cell } = computeGrid();
  const hours = document.getElementById('pcHours');
  const labels = document.getElementById('pcRowLabels');
  const body = document.getElementById('pcBody');
  hours.innerHTML = ''; labels.innerHTML = ''; body.innerHTML = '';
  for (let h = 0; h < 24; h++) { const s = document.createElement('div'); s.textContent = h % 3 === 0 ? String(h).padStart(2,'0') : ''; hours.appendChild(s); }

  let max = 0;
  for (let d = 0; d < 7; d++) for (let h = 0; h < 24; h++) max = Math.max(max, cell[d][h].runs);
  const scale = v => { if (v === 0) return 0; const norm = logScale ? Math.log(1+v)/Math.log(1+max) : v/max; return 5 + norm * 17; };

  for (let d = 0; d < 7; d++) {
    const lab = document.createElement('div'); lab.textContent = DAYS[d]; labels.appendChild(lab);
    const row = document.createElement('div'); row.className = 'pc-row';
    for (let h = 0; h < 24; h++) {
      const div = document.createElement('div'); div.className = 'pc-cell';
      if (d === NOW_DAY && h === NOW_HOUR) div.classList.add('now-col');
      const c = cell[d][h];
      if (c.runs > 0) {
        const dot = document.createElement('div'); dot.className = 'pc-dot';
        const sz = scale(c.runs); dot.style.width = sz + 'px'; dot.style.height = sz + 'px';
        if (source === 'historical') dot.style.background = failColor(Math.round(c.fails / c.runs * 100));
        dot.addEventListener('mousemove', e => showTip(cellTip(d, h, c), e));
        dot.addEventListener('mouseleave', hideTip);
        div.appendChild(dot);
      }
      row.appendChild(div);
    }
    body.appendChild(row);
  }
}

// ---- Concurrency curve ---------------------------------------------------
function renderConc() {
  const { minute } = computeGrid();
  const perMin = new Array(MIN_PER_DAY).fill(0);
  for (let d = 0; d < 7; d++) for (let m = 0; m < MIN_PER_DAY; m++) perMin[m] = Math.max(perMin[m], minute[d][m]);

  let peak = 0, peakAt = 0, breaches = 0;
  perMin.forEach((v, m) => { if (v > peak) { peak = v; peakAt = m; } if (v > CAPACITY) breaches++; });

  document.getElementById('ccPeak').textContent = peak;
  document.getElementById('ccCap').textContent = CAPACITY;
  document.getElementById('ccBreaches').textContent = breaches;
  document.getElementById('ccPeakAt').textContent = `${String(Math.floor(peakAt/60)).padStart(2,'0')}:${String(peakAt%60).padStart(2,'0')}`;

  const W = 1100, H = 280, padL = 36, padB = 26, padT = 12, padR = 8;
  const plotW = W - padL - padR, plotH = H - padT - padB;
  const maxY = Math.max(peak, CAPACITY) * 1.15;
  const x = m => padL + (m / MIN_PER_DAY) * plotW;
  const y = v => padT + plotH - (v / maxY) * plotH;

  const BUCKET = 5; let bars = '';
  for (let m = 0; m < MIN_PER_DAY; m += BUCKET) {
    let v = 0; for (let k = m; k < m + BUCKET; k++) v = Math.max(v, perMin[k]);
    if (v === 0) continue;
    const bx = x(m), bw = Math.max(1.5, (plotW / MIN_PER_DAY) * BUCKET - 0.5);
    const over = v > CAPACITY;
    bars += `<rect x="${bx.toFixed(1)}" y="${y(v).toFixed(1)}" width="${bw.toFixed(1)}" height="${(padT+plotH-y(v)).toFixed(1)}" rx="1" fill="${over ? 'var(--danger)' : 'var(--dot)'}" opacity="${over?0.95:0.8}"></rect>`;
  }
  const capY = y(CAPACITY);
  let grid = '';
  for (let h = 0; h <= 24; h += 3) {
    const gx = x(h * 60);
    grid += `<line x1="${gx}" y1="${padT}" x2="${gx}" y2="${padT+plotH}" stroke="var(--grid-line)" stroke-width="1"></line>`;
    grid += `<text x="${gx}" y="${H-8}" font-size="10" text-anchor="middle">${String(h).padStart(2,'0')}:00</text>`;
  }
  let yticks = '';
  for (let i = 0; i <= 4; i++) {
    const vv = Math.round((maxY / 4) * i), gy = y(vv);
    yticks += `<text x="${padL-6}" y="${gy+3}" font-size="10" text-anchor="end">${vv}</text>`;
    yticks += `<line x1="${padL}" y1="${gy}" x2="${W-padR}" y2="${gy}" stroke="var(--grid-line)" stroke-width="0.6"></line>`;
  }
  document.getElementById('ccChart').innerHTML =
    `<svg viewBox="0 0 ${W} ${H}" width="100%" preserveAspectRatio="xMidYMid meet">
      ${yticks}${grid}${bars}
      <line x1="${padL}" y1="${capY}" x2="${W-padR}" y2="${capY}" stroke="var(--accent)" stroke-width="1.5" stroke-dasharray="5 4"></line>
      <text x="${W-padR}" y="${capY-5}" font-size="10" text-anchor="end" style="fill:var(--accent)">capacity ${CAPACITY}</text>
    </svg>`;
}

// ---- Calendar heatmap ----------------------------------------------------
function renderCal() {
  const { cell } = computeGrid();
  const hours = document.getElementById('calHours');
  const labels = document.getElementById('calRowLabels');
  const body = document.getElementById('calBody');
  hours.innerHTML = ''; labels.innerHTML = ''; body.innerHTML = '';
  for (let h = 0; h < 24; h++) { const s = document.createElement('div'); s.textContent = h % 3 === 0 ? String(h).padStart(2,'0') : ''; hours.appendChild(s); }

  const ramps = ['--ramp-0','--ramp-1','--ramp-2','--ramp-3','--ramp-4','--ramp-5'];
  const histColor = source === 'historical' && metric !== 'vol';

  let maxVol = 0, maxDur = 0;
  for (let d=0; d<7; d++) for (let h=0; h<24; h++) { maxVol = Math.max(maxVol, cell[d][h].runs); maxDur = Math.max(maxDur, cell[d][h].p95); }
  const volBucket = v => { if (v === 0) return 0; const n = logScale ? Math.log(1+v)/Math.log(1+maxVol) : v/maxVol; return Math.min(5, 1 + Math.floor(n * 5)); };

  for (let d = 0; d < 7; d++) {
    const lab = document.createElement('div'); lab.textContent = DAYS[d]; labels.appendChild(lab);
    const row = document.createElement('div'); row.className = 'cal-row';
    for (let h = 0; h < 24; h++) {
      const el = document.createElement('div'); el.className = 'cal-cell';
      const c = cell[d][h];
      if (c.runs > 0) {
        let hex;
        if (histColor && metric === 'fail') {
          hex = failHex(Math.round(c.fails / c.runs * 100));
        } else if (histColor && metric === 'dur') {
          hex = rampHex(Math.min(5, 1 + Math.floor((c.p95 / (maxDur || 1)) * 5)));
        } else {
          hex = rampHex(volBucket(c.runs));
        }
        el.style.background = hex;
        el.textContent = c.runs >= 100 ? '99+' : c.runs;
        // contrast: derive ink from the exact hex we just applied
        const ink = inkFor(hex);
        el.style.color = ink.color;
        el.style.textShadow = ink.shadow;
        el.addEventListener('mousemove', e => showTip(cellTip(d, h, c), e));
        el.addEventListener('mouseleave', hideTip);
      } else {
        el.style.background = rampHex(0);
      }
      row.appendChild(el);
    }
    body.appendChild(row);
  }
}

// ---- Table ---------------------------------------------------------------
function renderTable() {
  const tb = document.getElementById('jobTable');
  tb.innerHTML = activeJobs().map(j => {
    const extra = source === 'historical'
      ? `<td style="color:${j.fail>=0.2?'var(--danger)':j.fail>=0.08?'var(--warn)':'var(--ok)'}">${Math.round(j.fail*100)}%</td>`
      : `<td>${firesPerDay(j)}</td>`;
    return `<tr><td>${j.id}</td><td><code>${j.cron}</code></td><td><span class="q">${j.queue}</span></td>${extra}<td>${nextRun(j)}</td></tr>`;
  }).join('');
  document.getElementById('colMetric').textContent = source === 'historical' ? 'Failure rate' : 'Fires / day';
}
function nextRun(j) {
  const nowMin = NOW_DAY * MIN_PER_DAY + NOW_HOUR * 60;
  const future = j.gen().filter(t => t > nowMin).sort((a,b)=>a-b);
  const t = future.length ? future[0] : j.gen()[0];
  const d = Math.floor(t / MIN_PER_DAY), mod = t % MIN_PER_DAY;
  return `${DAYS[d]} ${String(Math.floor(mod/60)).padStart(2,'0')}:${String(mod%60).padStart(2,'0')}`;
}

// ---- Wiring --------------------------------------------------------------
function syncChrome() {
  const hist = source === 'historical';
  document.getElementById('pcFailLegend').style.display = (hist && view === 'punch') ? 'flex' : 'none';
  document.getElementById('metricWrap').style.display = (hist && view === 'cal') ? 'inline-flex' : 'none';
  document.getElementById('srcNote').textContent = hist
    ? 'Historical: cells encode actual past runs. Color = failure rate (red now means something). Needs IStorageMetricsProvider per adapter.'
    : 'Projected: computed purely from cron expressions via Cronos. No storage queries, works even with zero history.';
}
function renderAll() {
  if (view === 'punch') renderPunch();
  else if (view === 'conc') renderConc();
  else renderCal();
  renderTable();
  syncChrome();
}

document.getElementById('viewSeg').addEventListener('click', e => {
  const b = e.target.closest('button'); if (!b) return;
  view = b.dataset.view;
  [...document.querySelectorAll('#viewSeg button')].forEach(x => x.classList.toggle('active', x === b));
  document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
  document.getElementById('view-' + view).classList.add('active');
  renderAll();
});
document.getElementById('sourceSeg').addEventListener('click', e => {
  const b = e.target.closest('button'); if (!b) return;
  source = b.dataset.source;
  [...document.querySelectorAll('#sourceSeg button')].forEach(x => x.classList.toggle('active', x === b));
  renderAll();
});
document.getElementById('metric').addEventListener('change', e => { metric = e.target.value; renderAll(); });
document.getElementById('hideSub').addEventListener('change', e => { hideSub = e.target.checked; renderAll(); });
document.getElementById('logScale').addEventListener('change', e => { logScale = e.target.checked; renderAll(); });
document.getElementById('themeBtn').addEventListener('click', () => {
  const html = document.documentElement;
  html.dataset.theme = html.dataset.theme === 'dark' ? 'light' : 'dark';
  renderAll();
});
window.addEventListener('resize', () => { if (view === 'conc') renderConc(); });

try {
  const tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
  document.getElementById('tzline').textContent = `demo "now" = Thu 14:00 · times in ${tz}`;
} catch {}

renderAll();
