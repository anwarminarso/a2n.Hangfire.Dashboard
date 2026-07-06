// ===========================================================================
// Recurring Schedule Heatmap — Mockup v2 (#14)
// Adds the QUEUE dimension (queue x day x hour), per-queue small multiples,
// stacked concurrency-by-queue, and an auto insights strip.
// ===========================================================================
const DAYS = ['Mon','Tue','Wed','Thu','Fri','Sat','Sun'];
const MIN_PER_DAY = 1440;
const NOW_DAY = 3, NOW_HOUR = 14;

// queue -> hue (works on both themes)
const QUEUE_COLOR = {
  default:'#4dabf7', billing:'#f783ac', reports:'#ffa94d',
  sync:'#38d9a9', maint:'#b197fc', metrics:'#ffe066',
};

const JOBS = [
  { id:'EmailDigest.SendDaily',    cron:'0 8 * * *',       queue:'default', sub:false, fail:0.01, gen:()=>atDaily(8,0) },
  { id:'Billing.GenerateInvoices', cron:'0 9 * * 1-5',     queue:'billing', sub:false, fail:0.18, gen:()=>weekdays(9,0) },
  { id:'Billing.DunningSweep',     cron:'0 */4 * * *',     queue:'billing', sub:false, fail:0.09, gen:()=>multiHour([0,4,8,12,16,20],0) },
  { id:'Reports.NightlyRollup',    cron:'0 2 * * *',       queue:'reports', sub:false, fail:0.30, gen:()=>atDaily(2,0) },
  { id:'Reports.HourlyAggregate',  cron:'0 * * * *',       queue:'reports', sub:false, fail:0.04, gen:()=>everyHour(0) },
  { id:'Cache.WarmTopHour',        cron:'5 * * * *',       queue:'default', sub:false, fail:0.00, gen:()=>everyHour(5) },
  { id:'Sync.ShopifyStock',        cron:'*/15 * * * *',    queue:'sync',    sub:true,  fail:0.06, gen:()=>everyN(15) },
  { id:'Sync.SapInventory',        cron:'*/15 * * * *',    queue:'sync',    sub:true,  fail:0.22, gen:()=>everyN(15,2) },
  { id:'Heartbeat.Ping',           cron:'*/5 * * * *',     queue:'default', sub:true,  fail:0.00, gen:()=>everyN(5) },
  { id:'Cleanup.TempFiles',        cron:'30 3 * * *',      queue:'maint',   sub:false, fail:0.02, gen:()=>atDaily(3,30) },
  { id:'Backup.Database',          cron:'0 1 * * *',       queue:'maint',   sub:false, fail:0.08, gen:()=>atDaily(1,0) },
  { id:'Index.Rebuild',            cron:'0 2 * * 0',       queue:'maint',   sub:false, fail:0.40, gen:()=>weekly(6,2,0) },
  { id:'Notify.WeeklySummary',     cron:'0 9 * * 1',       queue:'default', sub:false, fail:0.00, gen:()=>weekly(0,9,0) },
  { id:'Metrics.ScrapeQuarter',    cron:'*/15 * * * *',    queue:'metrics', sub:true,  fail:0.01, gen:()=>everyN(15,7) },
  { id:'Webhook.RetrySweep',       cron:'*/10 * * * *',    queue:'sync',    sub:true,  fail:0.12, gen:()=>everyN(10) },
  { id:'Forecast.Recompute',       cron:'0 8,12,16 * * *', queue:'reports', sub:false, fail:0.15, gen:()=>multiHour([8,12,16],0) },
];

function atDaily(h,m){ return DAYS.map((_,d)=>d*MIN_PER_DAY+h*60+m); }
function weekdays(h,m){ return [0,1,2,3,4].map(d=>d*MIN_PER_DAY+h*60+m); }
function weekly(day,h,m){ return [day*MIN_PER_DAY+h*60+m]; }
function everyHour(m){ const o=[]; for(let d=0;d<7;d++)for(let h=0;h<24;h++)o.push(d*MIN_PER_DAY+h*60+m); return o; }
function multiHour(hs,m){ const o=[]; for(let d=0;d<7;d++)hs.forEach(h=>o.push(d*MIN_PER_DAY+h*60+m)); return o; }
function everyN(n,off=0){ const o=[]; for(let d=0;d<7;d++)for(let t=off;t<MIN_PER_DAY;t+=n)o.push(d*MIN_PER_DAY+t); return o; }

const QUEUES = [...new Set(JOBS.map(j=>j.queue))];

// ---- State ----------------------------------------------------------------
let source='projected', view='punch', hideSub=false, logScale=false;
let selDay=3, cap=12, metric='fail';
let selectedQueues = new Set(QUEUES);

function activeJobs(){ return JOBS.filter(j => selectedQueues.has(j.queue) && !(hideSub && j.sub)); }
function theme(){ return document.documentElement.dataset.theme==='dark'?'dark':'light'; }

const RAMP = {
  light:['#eef1f5','#cfe8ef','#8fd3c7','#4cb3a9','#2f8f9e','#1f5f86'],
  dark: ['#1c2434','#1f3b4d','#1f5f6b','#2f8f8a','#4cc0a8','#8fe3c7'],
};
const FAILS = {
  light:{ ok:'#198754', warn:'#fd7e14', high:'#e8590c', danger:'#dc3545' },
  dark: { ok:'#2f9e44', warn:'#fd7e14', high:'#e8590c', danger:'#e03131' },
};
function rampHex(i){ return RAMP[theme()][Math.max(0,Math.min(5,i))]; }
function failHex(p){ const f=FAILS[theme()]; if(p<8)return f.ok; if(p<15)return f.warn; if(p<25)return f.high; return f.danger; }

// YIQ contrast (per common.js invertColor), threshold 186 + opposite shadow
function inkFor(hex){
  let h=hex.replace('#',''); if(h.length===3)h=h[0]+h[0]+h[1]+h[1]+h[2]+h[2];
  const r=parseInt(h.slice(0,2),16),g=parseInt(h.slice(2,4),16),b=parseInt(h.slice(4,6),16);
  const yiq=r*0.299+g*0.587+b*0.114;
  return yiq>186 ? {color:'#10141d',shadow:'0 1px 1px rgba(255,255,255,.55)'} : {color:'#ffffff',shadow:'0 1px 2px rgba(0,0,0,.55)'};
}
function hashStr(s){ let h=7; for(let i=0;i<s.length;i++)h=(h*31+s.charCodeAt(i))>>>0; return h; }
function rng(seed){ let s=seed%2147483647; if(s<=0)s+=2147483646; return ()=>(s=s*16807%2147483647)/2147483647; }

// ---- Aggregation ----------------------------------------------------------
// cell[q][d][h] = {runs, fails, p95, jobs:Set}; minute[q][d][min] = count
function compute(){
  const cell={}, minute={};
  for(const q of QUEUES){
    cell[q]=Array.from({length:7},()=>Array.from({length:24},()=>({runs:0,fails:0,p95:0,jobs:new Set()})));
    minute[q]=Array.from({length:7},()=>new Array(MIN_PER_DAY).fill(0));
  }
  for(const j of activeJobs()){
    const r=rng(hashStr(j.id));
    for(const t of j.gen()){
      const d=Math.floor(t/MIN_PER_DAY), mod=t%MIN_PER_DAY, h=Math.floor(mod/60);
      const c=cell[j.queue][d][h];
      c.runs++; c.jobs.add(j.id); minute[j.queue][d][mod]++;
      if(source==='historical'){ if(r()<j.fail)c.fails++; c.p95=Math.max(c.p95,Math.round(200+r()*(j.fail>0.2?9000:2500))); }
    }
  }
  return {cell,minute};
}
// merge selected queues into one day×hour grid
function mergeDH(cell){
  const g=Array.from({length:7},()=>Array.from({length:24},()=>({runs:0,fails:0,p95:0,jobs:new Set(),byQ:{}})));
  for(const q of selectedQueues) for(let d=0;d<7;d++) for(let h=0;h<24;h++){
    const s=cell[q][d][h], t=g[d][h];
    t.runs+=s.runs; t.fails+=s.fails; t.p95=Math.max(t.p95,s.p95); s.jobs.forEach(x=>t.jobs.add(x));
    if(s.runs) t.byQ[q]=(t.byQ[q]||0)+s.runs;
  }
  return g;
}
function dominantQueue(byQ){ let best=null,m=-1; for(const q in byQ) if(byQ[q]>m){m=byQ[q];best=q;} return best; }
function firesPerDay(j){ return Math.round(j.gen().length/7); }

// ---- Tooltip --------------------------------------------------------------
const tip=document.getElementById('tip');
function showTip(html,e){ tip.innerHTML=html; tip.style.display='block'; moveTip(e); }
function moveTip(e){ const pad=14; let x=e.clientX+pad,y=e.clientY+pad; const r=tip.getBoundingClientRect();
  if(x+r.width>innerWidth)x=e.clientX-r.width-pad; if(y+r.height>innerHeight)y=e.clientY-r.height-pad;
  tip.style.left=x+'px'; tip.style.top=y+'px'; }
function hideTip(){ tip.style.display='none'; }
function cellTip(label,c){
  const jobs=[...c.jobs];
  let s=`<div class="t">${label} — ${c.runs} ${source==='historical'?'runs':'fires'}</div>`;
  if(source==='historical'&&c.runs){ const p=Math.round(c.fails/c.runs*100);
    s+=`<div class="j" style="color:${p>=20?'var(--danger)':p>=8?'var(--warn)':'var(--ok)'}">${c.fails} failed (${p}%) · p95 ${(c.p95/1000).toFixed(1)}s</div>`; }
  return s+`<div class="j">${jobs.slice(0,7).join('<br>')}${jobs.length>7?`<br>+${jobs.length-7} more`:''}</div>`;
}

// ---- Hours header helper --------------------------------------------------
function fillHours(el){ el.innerHTML=''; for(let h=0;h<24;h++){const s=document.createElement('div'); s.textContent=h%3===0?String(h).padStart(2,'0'):''; el.appendChild(s);} }

// ---- Punchcard ------------------------------------------------------------
function renderPunch(){
  const {cell}=compute(); const g=mergeDH(cell);
  fillHours(document.getElementById('pcHours'));
  const labels=document.getElementById('pcRowLabels'), body=document.getElementById('pcBody');
  labels.innerHTML=''; body.innerHTML='';
  let max=0; for(let d=0;d<7;d++)for(let h=0;h<24;h++)max=Math.max(max,g[d][h].runs);
  const scale=v=>{ if(!v)return 0; const n=logScale?Math.log(1+v)/Math.log(1+max):v/max; return 5+n*17; };
  for(let d=0;d<7;d++){
    const l=document.createElement('div'); l.textContent=DAYS[d]; l.style.textAlign='right'; l.style.paddingRight='6px'; labels.appendChild(l);
    const row=document.createElement('div'); row.className='row';
    for(let h=0;h<24;h++){
      const div=document.createElement('div'); div.className='pc-cell';
      if(d===NOW_DAY&&h===NOW_HOUR)div.classList.add('now-col');
      const c=g[d][h];
      if(c.runs>0){
        const dot=document.createElement('div'); dot.className='pc-dot'; const sz=scale(c.runs);
        dot.style.width=sz+'px'; dot.style.height=sz+'px';
        dot.style.background = source==='historical' ? failHex(Math.round(c.fails/c.runs*100)) : (QUEUE_COLOR[dominantQueue(c.byQ)]||'var(--accent)');
        dot.addEventListener('mousemove',e=>showTip(cellTip(`${DAYS[d]} ${String(h).padStart(2,'0')}:00`,c),e));
        dot.addEventListener('mouseleave',hideTip);
        div.appendChild(dot);
      }
      row.appendChild(div);
    }
    body.appendChild(row);
  }
  // color legend
  const cl=document.getElementById('pcColorLegend');
  if(source==='historical'){
    cl.innerHTML=`<span class="key"><span class="swatch" style="background:${FAILS[theme()].ok}"></span>0%</span>`+
                 `<span class="key"><span class="swatch" style="background:${FAILS[theme()].warn}"></span>~10%</span>`+
                 `<span class="key"><span class="swatch" style="background:${FAILS[theme()].danger}"></span>25%+</span>`;
  } else {
    cl.innerHTML=[...selectedQueues].map(q=>`<span class="key"><span class="dot" style="display:inline-block;width:10px;height:10px;border-radius:50%;background:${QUEUE_COLOR[q]}"></span>${q}</span>`).join('');
  }
}

// ---- Queue × Hour ---------------------------------------------------------
function renderQH(){
  const {cell}=compute();
  fillHours(document.getElementById('qhHours'));
  const labels=document.getElementById('qhRowLabels'), body=document.getElementById('qhBody');
  const qs=[...selectedQueues];
  labels.style.gridTemplateRows=`repeat(${qs.length||1},1fr)`; body.style.gridTemplateRows=`repeat(${qs.length||1},1fr)`;
  labels.innerHTML=''; body.innerHTML='';
  document.getElementById('qhDayLabel').textContent = selDay<0 ? '· whole week' : '· '+DAYS[selDay];

  // per-(queue,hour) counts for the chosen day (or week sum)
  const data={}; let max=0;
  for(const q of qs){ data[q]=new Array(24).fill(0);
    for(let h=0;h<24;h++){ let v=0,fails=0,jobs=new Set();
      if(selDay<0){ for(let d=0;d<7;d++){const c=cell[q][d][h]; v+=c.runs; fails+=c.fails; c.jobs.forEach(x=>jobs.add(x));} }
      else { const c=cell[q][selDay][h]; v=c.runs; fails=c.fails; c.jobs.forEach(x=>jobs.add(x)); }
      data[q][h]={runs:v,fails,jobs}; max=Math.max(max,v);
    }
  }
  const bucket=v=>{ if(!v)return 0; const n=logScale?Math.log(1+v)/Math.log(1+max):v/max; return Math.min(5,1+Math.floor(n*5)); };
  for(const q of qs){
    const l=document.createElement('div'); l.className='ql';
    l.innerHTML=`<span class="dot" style="background:${QUEUE_COLOR[q]}"></span>${q}`; labels.appendChild(l);
    const row=document.createElement('div'); row.className='row';
    for(let h=0;h<24;h++){
      const c=data[q][h]; const el=document.createElement('div'); el.className='hm-cell';
      if(c.runs>0){ const hex=rampHex(bucket(c.runs)); el.style.background=hex; el.textContent=c.runs>=100?'99+':c.runs;
        const ink=inkFor(hex); el.style.color=ink.color; el.style.textShadow=ink.shadow;
        el.addEventListener('mousemove',e=>showTip(cellTip(`${q} · ${selDay<0?'week':DAYS[selDay]} ${String(h).padStart(2,'0')}:00`,c),e));
        el.addEventListener('mouseleave',hideTip);
      } else el.style.background=rampHex(0);
      row.appendChild(el);
    }
    body.appendChild(row);
  }
}

// ---- Per-queue small multiples -------------------------------------------
function renderMulti(){
  const {cell}=compute(); const grid=document.getElementById('multiGrid'); grid.innerHTML='';
  for(const q of selectedQueues){
    let max=0; for(let d=0;d<7;d++)for(let h=0;h<24;h++)max=Math.max(max,cell[q][d][h].runs);
    const card=document.createElement('div'); card.className='mini';
    card.innerHTML=`<h3><span class="dot" style="background:${QUEUE_COLOR[q]}"></span>${q}<span style="margin-left:auto;font-weight:400;color:var(--text-muted);font-size:11px">max ${max}/h</span></h3>`;
    const gw=document.createElement('div'); gw.className='gridwrap';
    const sp=document.createElement('div'); const hours=document.createElement('div'); hours.className='hours';
    for(let h=0;h<24;h++){const s=document.createElement('div'); s.textContent=h%6===0?String(h).padStart(2,'0'):''; hours.appendChild(s);}
    const labels=document.createElement('div'); labels.className='rowlabels'; labels.style.gridTemplateRows='repeat(7,1fr)';
    const body=document.createElement('div'); body.className='body'; body.style.gridTemplateRows='repeat(7,1fr)';
    const bucket=v=>{ if(!v)return 0; const n=logScale?Math.log(1+v)/Math.log(1+max):v/max; return Math.min(5,1+Math.floor(n*5)); };
    for(let d=0;d<7;d++){
      const l=document.createElement('div'); l.textContent=DAYS[d][0]; labels.appendChild(l);
      const row=document.createElement('div'); row.className='row';
      for(let h=0;h<24;h++){ const c=cell[q][d][h]; const el=document.createElement('div'); el.className='hm-cell';
        el.style.background = c.runs? rampHex(bucket(c.runs)) : rampHex(0);
        if(c.runs){ el.addEventListener('mousemove',e=>showTip(cellTip(`${q} · ${DAYS[d]} ${String(h).padStart(2,'0')}:00`,c),e)); el.addEventListener('mouseleave',hideTip); }
        row.appendChild(el);
      }
      body.appendChild(row);
    }
    gw.appendChild(sp); gw.appendChild(hours); gw.appendChild(labels); gw.appendChild(body);
    card.appendChild(gw); grid.appendChild(card);
  }
}

// ---- Concurrency (stacked by queue) --------------------------------------
function renderConc(){
  const {minute}=compute(); const qs=[...selectedQueues];
  // choose worst day = highest single-minute total overlap
  let worstDay=0, worstPeak=-1;
  for(let d=0;d<7;d++){ for(let m=0;m<MIN_PER_DAY;m++){ let tot=0; for(const q of qs)tot+=minute[q][d][m]; if(tot>worstPeak){worstPeak=tot;worstDay=d;} } }
  // per-minute per-queue for worst day
  let peak=0,peakAt=0,breaches=0; const overHours=new Set();
  for(let m=0;m<MIN_PER_DAY;m++){ let tot=0; for(const q of qs)tot+=minute[q][worstDay][m];
    if(tot>peak){peak=tot;peakAt=m;} if(tot>cap){breaches++; overHours.add(Math.floor(m/60));} }

  document.getElementById('ccPeak').textContent=peak;
  document.getElementById('ccCap').textContent=cap;
  document.getElementById('ccBreaches').textContent=breaches;
  document.getElementById('ccPeakAt').textContent=`${DAYS[worstDay]} ${String(Math.floor(peakAt/60)).padStart(2,'0')}:${String(peakAt%60).padStart(2,'0')}`;

  const W=1120,H=300,padL=38,padB=26,padT=12,padR=8, plotW=W-padL-padR, plotH=H-padT-padB;
  const maxY=Math.max(peak,cap)*1.15;
  const x=m=>padL+(m/MIN_PER_DAY)*plotW, y=v=>padT+plotH-(v/maxY)*plotH;
  const BUCKET=5; let bars='';
  for(let m=0;m<MIN_PER_DAY;m+=BUCKET){
    const counts={}; let tot=0;
    for(const q of qs){ let v=0; for(let k=m;k<m+BUCKET;k++)v=Math.max(v,minute[q][worstDay][k]); counts[q]=v; tot+=v; }
    if(tot===0)continue;
    const bx=x(m), bw=Math.max(1.5,(plotW/MIN_PER_DAY)*BUCKET-0.4); let acc=0;
    for(const q of qs){ if(!counts[q])continue; const yTop=y(acc+counts[q]), hgt=y(acc)-y(acc+counts[q]); acc+=counts[q];
      bars+=`<rect x="${bx.toFixed(1)}" y="${yTop.toFixed(1)}" width="${bw.toFixed(1)}" height="${Math.max(0.6,hgt).toFixed(1)}" fill="${QUEUE_COLOR[q]}" opacity="0.92"></rect>`; }
    if(tot>cap) bars+=`<rect x="${(bx-0.5).toFixed(1)}" y="${(y(tot)-2).toFixed(1)}" width="${(bw+1).toFixed(1)}" height="2" fill="var(--danger)"></rect>`;
  }
  const capY=y(cap); let grid='';
  for(let h=0;h<=24;h+=3){ const gx=x(h*60);
    grid+=`<line x1="${gx}" y1="${padT}" x2="${gx}" y2="${padT+plotH}" stroke="var(--grid-line)" stroke-width="1"></line>`;
    grid+=`<text x="${gx}" y="${H-8}" font-size="10" text-anchor="middle">${String(h).padStart(2,'0')}:00</text>`; }
  let yt=''; for(let i=0;i<=4;i++){ const vv=Math.round(maxY/4*i), gy=y(vv);
    yt+=`<text x="${padL-6}" y="${gy+3}" font-size="10" text-anchor="end">${vv}</text>`;
    yt+=`<line x1="${padL}" y1="${gy}" x2="${W-padR}" y2="${gy}" stroke="var(--grid-line)" stroke-width="0.6"></line>`; }
  document.getElementById('ccChart').innerHTML=
    `<svg viewBox="0 0 ${W} ${H}" width="100%" preserveAspectRatio="xMidYMid meet">${yt}${grid}${bars}
      <line x1="${padL}" y1="${capY}" x2="${W-padR}" y2="${capY}" stroke="var(--accent)" stroke-width="1.5" stroke-dasharray="5 4"></line>
      <text x="${W-padR}" y="${capY-5}" font-size="10" text-anchor="end" style="fill:var(--accent)">capacity ${cap}</text></svg>`;
  document.getElementById('ccLegend').innerHTML =
    qs.map(q=>`<span class="key"><span class="swatch" style="background:${QUEUE_COLOR[q]}"></span>${q}</span>`).join('')+
    `<span class="key"><span class="swatch" style="background:var(--accent);height:2px"></span>capacity</span>`+
    `<span class="key" style="color:var(--text-muted)">worst day: <b style="color:var(--text)">${DAYS[worstDay]}</b></span>`;
}

// ---- Calendar -------------------------------------------------------------
function renderCal(){
  const {cell}=compute(); const g=mergeDH(cell);
  fillHours(document.getElementById('calHours'));
  const labels=document.getElementById('calRowLabels'), body=document.getElementById('calBody');
  labels.innerHTML=''; body.innerHTML='';
  const histColor=source==='historical'&&metric!=='vol';
  let maxVol=0,maxDur=0; for(let d=0;d<7;d++)for(let h=0;h<24;h++){maxVol=Math.max(maxVol,g[d][h].runs);maxDur=Math.max(maxDur,g[d][h].p95);}
  const vb=v=>{ if(!v)return 0; const n=logScale?Math.log(1+v)/Math.log(1+maxVol):v/maxVol; return Math.min(5,1+Math.floor(n*5)); };
  for(let d=0;d<7;d++){
    const l=document.createElement('div'); l.textContent=DAYS[d]; l.style.textAlign='right'; l.style.paddingRight='6px'; labels.appendChild(l);
    const row=document.createElement('div'); row.className='row';
    for(let h=0;h<24;h++){
      const c=g[d][h]; const el=document.createElement('div'); el.className='hm-cell';
      if(c.runs>0){
        let hex; if(histColor&&metric==='fail')hex=failHex(Math.round(c.fails/c.runs*100));
        else if(histColor&&metric==='dur')hex=rampHex(Math.min(5,1+Math.floor((c.p95/(maxDur||1))*5)));
        else hex=rampHex(vb(c.runs));
        el.style.background=hex; el.textContent=c.runs>=100?'99+':c.runs;
        const ink=inkFor(hex); el.style.color=ink.color; el.style.textShadow=ink.shadow;
        el.addEventListener('mousemove',e=>showTip(cellTip(`${DAYS[d]} ${String(h).padStart(2,'0')}:00`,c),e));
        el.addEventListener('mouseleave',hideTip);
      } else el.style.background=rampHex(0);
      row.appendChild(el);
    }
    body.appendChild(row);
  }
  document.getElementById('calLegend').style.display = (source==='historical'&&metric==='fail') ? 'none':'flex';
}

// ---- Insights -------------------------------------------------------------
function renderInsights(){
  const {cell,minute}=compute(); const qs=[...selectedQueues];
  let total=0, perQ={};
  for(const q of qs){ perQ[q]=0; for(let d=0;d<7;d++)for(let h=0;h<24;h++){perQ[q]+=cell[q][d][h].runs; total+=cell[q][d][h].runs;} }
  document.getElementById('insTotal').textContent=Math.round(total/7);
  let topQ='—',topV=-1; for(const q in perQ) if(perQ[q]>topV){topV=perQ[q];topQ=q;}
  document.getElementById('insQueue').innerHTML = topV>0
    ? `<span style="color:${QUEUE_COLOR[topQ]}">●</span> ${topQ} <small>${Math.round(topV/7)}/day</small>` : '—';

  let peak=0,peakAt=0,peakDay=0; const overHours=new Set();
  for(let d=0;d<7;d++)for(let m=0;m<MIN_PER_DAY;m++){ let t=0; for(const q of qs)t+=minute[q][d][m];
    if(t>peak){peak=t;peakAt=m;peakDay=d;} if(t>cap)overHours.add(d*24+Math.floor(m/60)); }
  document.getElementById('insPeak').innerHTML=`${peak} <small>${DAYS[peakDay]} ${String(Math.floor(peakAt/60)).padStart(2,'0')}:${String(peakAt%60).padStart(2,'0')}</small>`;
  document.getElementById('insPeakCard').classList.toggle('alert', peak>cap);
  document.getElementById('insOver').innerHTML=`${overHours.size} <small>vs ${cap} workers</small>`;
  document.getElementById('insOverCard').classList.toggle('alert', overHours.size>0);
}

// ---- Table ----------------------------------------------------------------
function renderTable(){
  const tb=document.getElementById('jobTable');
  tb.innerHTML=activeJobs().map(j=>{
    const extra=source==='historical'
      ? `<td style="color:${j.fail>=0.2?'var(--danger)':j.fail>=0.08?'var(--warn)':'var(--ok)'}">${Math.round(j.fail*100)}%</td>`
      : `<td>${firesPerDay(j)}</td>`;
    return `<tr><td>${j.id}</td><td><code>${j.cron}</code></td><td><span class="q" style="background:${QUEUE_COLOR[j.queue]}">${j.queue}</span></td>${extra}<td>${nextRun(j)}</td></tr>`;
  }).join('');
  document.getElementById('colMetric').textContent = source==='historical'?'Failure rate':'Fires / day';
}
function nextRun(j){ const nowMin=NOW_DAY*MIN_PER_DAY+NOW_HOUR*60; const fut=j.gen().filter(t=>t>nowMin).sort((a,b)=>a-b);
  const t=fut.length?fut[0]:j.gen()[0]; const d=Math.floor(t/MIN_PER_DAY),mod=t%MIN_PER_DAY;
  return `${DAYS[d]} ${String(Math.floor(mod/60)).padStart(2,'0')}:${String(mod%60).padStart(2,'0')}`; }

// ---- Chrome / wiring ------------------------------------------------------
function buildChips(){
  const c=document.getElementById('queueChips'); c.innerHTML='';
  for(const q of QUEUES){
    const el=document.createElement('span'); el.className='chip'+(selectedQueues.has(q)?'':' off');
    el.innerHTML=`<span class="dot" style="background:${QUEUE_COLOR[q]}"></span>${q}`;
    el.addEventListener('click',()=>{ if(selectedQueues.has(q))selectedQueues.delete(q); else selectedQueues.add(q);
      if(selectedQueues.size===0)selectedQueues.add(q); buildChips(); renderAll(); });
    c.appendChild(el);
  }
}
function syncChrome(){
  const hist=source==='historical';
  document.getElementById('ctxDayWrap').style.display = view==='qh'?'inline-flex':'none';
  document.getElementById('ctxMetricWrap').style.display = (hist&&view==='cal')?'inline-flex':'none';
  document.getElementById('ctxCapWrap').style.display = (view==='conc')?'inline-flex':'none';
  document.getElementById('srcNote').textContent = hist
    ? 'Historical: cells reflect actual past runs; color can encode failure rate / p95 duration. Requires IStorageMetricsProvider per storage adapter.'
    : 'Projected: every grid (incl. queue × hour) is computed purely from cron via Cronos — no storage needed, works with zero history.';
}
function renderAll(){
  renderInsights();
  if(view==='punch')renderPunch();
  else if(view==='qh')renderQH();
  else if(view==='multi')renderMulti();
  else if(view==='conc')renderConc();
  else renderCal();
  renderTable(); syncChrome();
}

document.getElementById('viewSeg').addEventListener('click',e=>{ const b=e.target.closest('button'); if(!b)return;
  view=b.dataset.view; [...document.querySelectorAll('#viewSeg button')].forEach(x=>x.classList.toggle('active',x===b));
  document.querySelectorAll('.view').forEach(v=>v.classList.remove('active')); document.getElementById('view-'+view).classList.add('active'); renderAll(); });
document.getElementById('sourceSeg').addEventListener('click',e=>{ const b=e.target.closest('button'); if(!b)return;
  source=b.dataset.source; [...document.querySelectorAll('#sourceSeg button')].forEach(x=>x.classList.toggle('active',x===b)); renderAll(); });
document.getElementById('ctxDay').addEventListener('change',e=>{ selDay=e.target.value==='-1'?-1:DAYS.indexOf(e.target.value); renderAll(); });
document.getElementById('ctxMetric').addEventListener('change',e=>{ metric=e.target.value; renderAll(); });
document.getElementById('ctxCap').addEventListener('input',e=>{ cap=Math.max(1,parseInt(e.target.value)||12); renderAll(); });
document.getElementById('hideSub').addEventListener('change',e=>{ hideSub=e.target.checked; renderAll(); });
document.getElementById('logScale').addEventListener('change',e=>{ logScale=e.target.checked; renderAll(); });
document.getElementById('qAll').addEventListener('click',()=>{ selectedQueues=new Set(QUEUES); buildChips(); renderAll(); });
document.getElementById('qNone').addEventListener('click',()=>{ selectedQueues=new Set([QUEUES[0]]); buildChips(); renderAll(); });
document.getElementById('themeBtn').addEventListener('click',()=>{ const h=document.documentElement; h.dataset.theme=h.dataset.theme==='dark'?'light':'dark'; renderAll(); });
window.addEventListener('resize',()=>{ if(view==='conc')renderConc(); });

try{ const tz=Intl.DateTimeFormat().resolvedOptions().timeZone;
  document.getElementById('tzline').textContent=`Scheduling density across queues · day · hour — demo "now" = Thu 14:00 · ${tz}`; }catch{}

buildChips();
renderAll();
