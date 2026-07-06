// ===========================================================================
// Recurring Schedule Heatmap — Mockup v3 (design ceiling, #14)
// Adds: drill-down drawer, overlap recommendations, duration-aware concurrency,
// worker-minutes load metric, per-job timezone, projection window (ideal/next7)
// with long-period handling, capacity-from-servers, top-N queues, CSV export.
// ===========================================================================
const DAYS = ['Mon','Tue','Wed','Thu','Fri','Sat','Sun'];
const MIN_PER_DAY = 1440, MIN_PER_WEEK = 10080;
const NOW_DAY = 3, NOW_HOUR = 14;
const QUEUE_COLOR = { default:'#4dabf7', billing:'#f783ac', reports:'#ffa94d', sync:'#38d9a9', maint:'#b197fc', metrics:'#ffe066' };

// gen(): minutes-of-week in the JOB's own timezone. durMin: typical p95 minutes.
const JOBS = [
  { id:'EmailDigest.SendDaily',    cron:'0 8 * * *',       queue:'default', sub:false, fail:0.01, durMin:2,  tz:0,    gen:()=>atDaily(8,0) },
  { id:'Billing.GenerateInvoices', cron:'0 9 * * 1-5',     queue:'billing', sub:false, fail:0.18, durMin:14, tz:-300, gen:()=>weekdays(9,0) },
  { id:'Billing.DunningSweep',     cron:'0 */4 * * *',     queue:'billing', sub:false, fail:0.09, durMin:6,  tz:0,    gen:()=>multiHour([0,4,8,12,16,20],0) },
  { id:'Reports.NightlyRollup',    cron:'0 2 * * *',       queue:'reports', sub:false, fail:0.30, durMin:22, tz:0,    gen:()=>atDaily(2,0) },
  { id:'Reports.HourlyAggregate',  cron:'0 * * * *',       queue:'reports', sub:false, fail:0.04, durMin:3,  tz:0,    gen:()=>everyHour(0) },
  { id:'Reports.NightlyExport',    cron:'0 2 * * *',       queue:'reports', sub:false, fail:0.05, durMin:18, tz:0,    gen:()=>atDaily(2,0) },
  { id:'Cache.WarmTopHour',        cron:'5 * * * *',       queue:'default', sub:false, fail:0.00, durMin:1,  tz:0,    gen:()=>everyHour(5) },
  { id:'Sync.ShopifyStock',        cron:'*/15 * * * *',    queue:'sync',    sub:true,  fail:0.06, durMin:2,  tz:0,    gen:()=>everyN(15) },
  { id:'Sync.SapInventory',        cron:'*/15 * * * *',    queue:'sync',    sub:true,  fail:0.22, durMin:4,  tz:0,    gen:()=>everyN(15,2) },
  { id:'Heartbeat.Ping',           cron:'*/5 * * * *',     queue:'default', sub:true,  fail:0.00, durMin:1,  tz:0,    gen:()=>everyN(5) },
  { id:'Cleanup.TempFiles',        cron:'30 3 * * *',      queue:'maint',   sub:false, fail:0.02, durMin:5,  tz:0,    gen:()=>atDaily(3,30) },
  { id:'Backup.Database',          cron:'0 2 * * *',       queue:'maint',   sub:false, fail:0.08, durMin:35, tz:0,    gen:()=>atDaily(2,0) },
  { id:'Index.Rebuild',            cron:'0 2 * * 0',       queue:'maint',   sub:false, fail:0.40, durMin:40, tz:0,    gen:()=>weekly(6,2,0) },
  { id:'Notify.WeeklySummary',     cron:'0 9 * * 1',       queue:'default', sub:false, fail:0.00, durMin:2,  tz:0,    gen:()=>weekly(0,9,0) },
  { id:'Metrics.ScrapeQuarter',    cron:'*/15 * * * *',    queue:'metrics', sub:true,  fail:0.01, durMin:1,  tz:0,    gen:()=>everyN(15,7) },
  { id:'Webhook.RetrySweep',       cron:'*/10 * * * *',    queue:'sync',    sub:true,  fail:0.12, durMin:2,  tz:0,    gen:()=>everyN(10) },
  { id:'Forecast.Recompute',       cron:'0 8,12,16 * * *', queue:'reports', sub:false, fail:0.15, durMin:9,  tz:0,    gen:()=>multiHour([8,12,16],0) },
  // long-period jobs (not representable in an idealized 7-day week)
  { id:'Billing.MonthlyClose',     cron:'0 6 1 * *',       queue:'billing', sub:false, fail:0.10, durMin:25, tz:0, period:'monthly', gen:()=>[], next7:()=>[((NOW_DAY+2)%7)*MIN_PER_DAY+6*60] },
  { id:'Archive.YearlyPurge',      cron:'0 4 1 1 *',       queue:'maint',   sub:false, fail:0.05, durMin:90, tz:0, period:'yearly',  gen:()=>[], next7:()=>[] },
];
const JOB_BY_ID = Object.fromEntries(JOBS.map(j=>[j.id,j]));

function atDaily(h,m){ return DAYS.map((_,d)=>d*MIN_PER_DAY+h*60+m); }
function weekdays(h,m){ return [0,1,2,3,4].map(d=>d*MIN_PER_DAY+h*60+m); }
function weekly(day,h,m){ return [day*MIN_PER_DAY+h*60+m]; }
function everyHour(m){ const o=[]; for(let d=0;d<7;d++)for(let h=0;h<24;h++)o.push(d*MIN_PER_DAY+h*60+m); return o; }
function multiHour(hs,m){ const o=[]; for(let d=0;d<7;d++)hs.forEach(h=>o.push(d*MIN_PER_DAY+h*60+m)); return o; }
function everyN(n,off=0){ const o=[]; for(let d=0;d<7;d++)for(let t=off;t<MIN_PER_DAY;t+=n)o.push(d*MIN_PER_DAY+t); return o; }

const QUEUES = [...new Set(JOBS.map(j=>j.queue))];

// ---- State ----------------------------------------------------------------
let source='projected', view='punch', hideSub=false, logScale=false;
let selDay=3, cap=12, metric='count', windowMode='ideal', viewTz=420, topN=0;
let selectedQueues=new Set(QUEUES);

function theme(){ return document.documentElement.dataset.theme==='dark'?'dark':'light'; }
const RAMP={ light:['#eef1f5','#cfe8ef','#8fd3c7','#4cb3a9','#2f8f9e','#1f5f86'], dark:['#1c2434','#1f3b4d','#1f5f6b','#2f8f8a','#4cc0a8','#8fe3c7'] };
const FAILS={ light:{ok:'#198754',warn:'#fd7e14',high:'#e8590c',danger:'#dc3545'}, dark:{ok:'#2f9e44',warn:'#fd7e14',high:'#e8590c',danger:'#e03131'} };
function rampHex(i){ return RAMP[theme()][Math.max(0,Math.min(5,i))]; }
function failHex(p){ const f=FAILS[theme()]; if(p<8)return f.ok; if(p<15)return f.warn; if(p<25)return f.high; return f.danger; }
function inkFor(hex){ let h=hex.replace('#',''); if(h.length===3)h=h[0]+h[0]+h[1]+h[1]+h[2]+h[2];
  const r=parseInt(h.slice(0,2),16),g=parseInt(h.slice(2,4),16),b=parseInt(h.slice(4,6),16); const yiq=r*0.299+g*0.587+b*0.114;
  return yiq>186?{color:'#10141d',shadow:'0 1px 1px rgba(255,255,255,.55)'}:{color:'#ffffff',shadow:'0 1px 2px rgba(0,0,0,.55)'}; }
function hashStr(s){ let h=7; for(let i=0;i<s.length;i++)h=(h*31+s.charCodeAt(i))>>>0; return h; }
function rng(seed){ let s=seed%2147483647; if(s<=0)s+=2147483646; return ()=>(s=s*16807%2147483647)/2147483647; }

// queues actually shown (selected + top-N by load)
function visibleQueues(){
  let qs=QUEUES.filter(q=>selectedQueues.has(q));
  if(topN>0 && qs.length>topN){
    const load={}; for(const q of qs)load[q]=0;
    for(const j of JOBS){ if(!qs.includes(j.queue))continue; if(hideSub&&j.sub)continue; load[j.queue]+=jobFires(j).length*(metric==='load'?j.durMin:1); }
    qs=qs.sort((a,b)=>load[b]-load[a]).slice(0,topN);
  }
  return qs;
}
function activeJobs(){ const qs=visibleQueues(); return JOBS.filter(j=>qs.includes(j.queue)&&!(hideSub&&j.sub)); }

// fire times shifted into the viewer timezone, honoring window mode
function jobFires(j){
  let base;
  if(j.period){ base = windowMode==='next7' ? (j.next7?j.next7():[]) : []; }
  else base=j.gen();
  const shift=viewTz-(j.tz||0);
  return base.map(m=>((m+shift)%MIN_PER_WEEK+MIN_PER_WEEK)%MIN_PER_WEEK);
}

// ---- Aggregation: cell[q][d][h] = {runs, load, fails, p95, jobs:Set} -------
function compute(){
  const cell={};
  for(const q of QUEUES) cell[q]=Array.from({length:7},()=>Array.from({length:24},()=>({runs:0,load:0,fails:0,p95:0,jobs:new Set()})));
  for(const j of activeJobs()){
    const r=rng(hashStr(j.id));
    for(const t of jobFires(j)){
      const d=Math.floor(t/MIN_PER_DAY),mod=t%MIN_PER_DAY,h=Math.floor(mod/60),c=cell[j.queue][d][h];
      c.runs++; c.load+=j.durMin; c.jobs.add(j.id);
      if(source==='historical'){ if(r()<j.fail)c.fails++; c.p95=Math.max(c.p95,Math.round(200+r()*(j.fail>0.2?9000:2500))); }
    }
  }
  return cell;
}
function cellVal(c){ return metric==='load'?c.load:c.runs; }
function mergeDH(cell){
  const qs=visibleQueues();
  const g=Array.from({length:7},()=>Array.from({length:24},()=>({runs:0,load:0,fails:0,p95:0,jobs:new Set(),byQ:{}})));
  for(const q of qs)for(let d=0;d<7;d++)for(let h=0;h<24;h++){ const s=cell[q][d][h],t=g[d][h];
    t.runs+=s.runs; t.load+=s.load; t.fails+=s.fails; t.p95=Math.max(t.p95,s.p95); s.jobs.forEach(x=>t.jobs.add(x));
    if(cellVal(s))t.byQ[q]=(t.byQ[q]||0)+cellVal(s); }
  return g;
}
function dominantQueue(byQ){ let b=null,m=-1; for(const q in byQ)if(byQ[q]>m){m=byQ[q];b=q;} return b; }
function rowOrder(){ if(windowMode!=='next7')return [0,1,2,3,4,5,6]; const o=[]; for(let i=0;i<7;i++)o.push((NOW_DAY+i)%7); return o; }

// ---- duration-aware concurrency for a given day ---------------------------
function concurrencyForDay(cell,d){
  const qs=visibleQueues(); const perQ={}; for(const q of qs)perQ[q]=new Array(MIN_PER_DAY).fill(0);
  for(const j of activeJobs()){ const q=j.queue; if(!qs.includes(q))continue;
    for(const t of jobFires(j)){ if(Math.floor(t/MIN_PER_DAY)!==d)continue; const start=t%MIN_PER_DAY, end=Math.min(MIN_PER_DAY,start+Math.max(1,j.durMin));
      for(let m=start;m<end;m++)perQ[q][m]++; } }
  return perQ;
}
function totalAt(perQ,m){ let s=0; for(const q in perQ)s+=perQ[q][m]; return s; }

// ---- Tooltip + drawer -----------------------------------------------------
const tip=document.getElementById('tip');
function showTip(html,e){ tip.innerHTML=html; tip.style.display='block'; moveTip(e); }
function moveTip(e){ const p=14; let x=e.clientX+p,y=e.clientY+p; const r=tip.getBoundingClientRect();
  if(x+r.width>innerWidth)x=e.clientX-r.width-p; if(y+r.height>innerHeight)y=e.clientY-r.height-p; tip.style.left=x+'px'; tip.style.top=y+'px'; }
function hideTip(){ tip.style.display='none'; }
function cellTip(label,c){ const jobs=[...c.jobs]; let s=`<div class="t">${label} — ${c.runs} ${source==='historical'?'runs':'fires'}${metric==='load'?` · ${c.load} wk-min`:''}</div>`;
  if(source==='historical'&&c.runs){ const p=Math.round(c.fails/c.runs*100); s+=`<div class="j" style="color:${p>=20?'var(--danger)':p>=8?'var(--warn)':'var(--ok)'}">${c.fails} failed (${p}%) · p95 ${(c.p95/1000).toFixed(1)}s</div>`; }
  return s+`<div class="j">${jobs.slice(0,7).join('<br>')}${jobs.length>7?`<br>+${jobs.length-7} more`:''}</div><div class="j" style="margin-top:4px;color:var(--accent)">click to drill in →</div>`; }

function openDrawer(title,sub,jobIds){
  document.getElementById('drawerTitle').textContent=title;
  document.getElementById('drawerSub').textContent=sub;
  const dc=document.getElementById('drawerContent');
  if(!jobIds.length){ dc.innerHTML=`<div class="jm">No jobs in this slot.</div>`; }
  else dc.innerHTML=jobIds.map(id=>{ const j=JOB_BY_ID[id]; return `<div class="jobrow">
      <div class="jn"><span class="dot" style="width:10px;height:10px;border-radius:50%;background:${QUEUE_COLOR[j.queue]}"></span>${j.id}</div>
      <div class="jm"><code>${j.cron}</code> · ${j.queue} · ~${j.durMin}m${j.tz?` · tz ${fmtTz(j.tz)}`:''}</div>
      <div class="jm">next: ${nextRun(j)}</div>
      <div class="ja"><button class="btn btn-sm btn-accent" onclick="mockEdit('${j.id}')">Edit schedule</button>
        <button class="btn btn-sm" onclick="mockEdit('${j.id}',1)">View executions</button></div></div>`; }).join('');
  document.getElementById('drawer').classList.add('open'); document.getElementById('scrim').classList.add('open');
}
function closeDrawer(){ document.getElementById('drawer').classList.remove('open'); document.getElementById('scrim').classList.remove('open'); }
window.mockEdit=function(id,exec){ const j=JOB_BY_ID[id];
  alert(`(mockup) Would open the ${exec?'execution history':'ScheduleBuilder (cron editor)'} for:\n\n${id}\ncron: ${j.cron}\nqueue: ${j.queue}`); };

function fillHours(el){ el.innerHTML=''; for(let h=0;h<24;h++){const s=document.createElement('div'); s.textContent=h%3===0?String(h).padStart(2,'0'):''; el.appendChild(s);} }
function fmtTz(off){ const s=off<0?'-':'+'; const a=Math.abs(off); return `${s}${String(Math.floor(a/60)).padStart(2,'0')}:${String(a%60).padStart(2,'0')}`; }

// ---- Punchcard ------------------------------------------------------------
function renderPunch(){
  const cell=compute(),g=mergeDH(cell); fillHours(document.getElementById('pcHours'));
  const labels=document.getElementById('pcRowLabels'),body=document.getElementById('pcBody'); labels.innerHTML='';body.innerHTML='';
  let max=0; for(let d=0;d<7;d++)for(let h=0;h<24;h++)max=Math.max(max,cellVal(g[d][h]));
  const scale=v=>{ if(!v)return 0; const n=logScale?Math.log(1+v)/Math.log(1+max):v/max; return 5+n*17; };
  for(const d of rowOrder()){
    const l=document.createElement('div'); l.textContent=DAYS[d]; l.style.textAlign='right'; l.style.paddingRight='6px'; labels.appendChild(l);
    const row=document.createElement('div'); row.className='row';
    for(let h=0;h<24;h++){ const div=document.createElement('div'); div.className='pc-cell'; if(d===NOW_DAY&&h===NOW_HOUR)div.classList.add('now');
      const c=g[d][h];
      if(cellVal(c)>0){ const dot=document.createElement('div'); dot.className='pc-dot'; const sz=scale(cellVal(c)); dot.style.width=sz+'px'; dot.style.height=sz+'px';
        dot.style.background=source==='historical'?failHex(Math.round(c.fails/Math.max(1,c.runs)*100)):(QUEUE_COLOR[dominantQueue(c.byQ)]||'var(--accent)'); div.appendChild(dot); }
      div.addEventListener('mousemove',e=>{ if(c.runs)showTip(cellTip(`${DAYS[d]} ${String(h).padStart(2,'0')}:00`,c),e); });
      div.addEventListener('mouseleave',hideTip);
      div.addEventListener('click',()=>{ if(c.runs)openDrawer(`${DAYS[d]} ${String(h).padStart(2,'0')}:00`,`${c.runs} fires across ${Object.keys(c.byQ).length} queue(s)`,[...c.jobs]); });
      row.appendChild(div);
    }
    body.appendChild(row);
  }
  const cl=document.getElementById('pcColorLegend');
  cl.innerHTML = source==='historical'
    ? `<span class="key"><span class="swatch" style="background:${FAILS[theme()].ok}"></span>0%</span><span class="key"><span class="swatch" style="background:${FAILS[theme()].warn}"></span>~10%</span><span class="key"><span class="swatch" style="background:${FAILS[theme()].danger}"></span>25%+</span>`
    : visibleQueues().map(q=>`<span class="key"><span style="display:inline-block;width:10px;height:10px;border-radius:50%;background:${QUEUE_COLOR[q]}"></span>${q}</span>`).join('');
}

// ---- Queue × Hour ---------------------------------------------------------
function renderQH(){
  const cell=compute(); fillHours(document.getElementById('qhHours'));
  const labels=document.getElementById('qhRowLabels'),body=document.getElementById('qhBody'); const qs=visibleQueues();
  labels.style.gridTemplateRows=`repeat(${qs.length||1},1fr)`; body.style.gridTemplateRows=`repeat(${qs.length||1},1fr)`; labels.innerHTML='';body.innerHTML='';
  document.getElementById('qhDayLabel').textContent=selDay<0?'· whole week':'· '+DAYS[selDay];
  const data={}; let max=0;
  for(const q of qs){ data[q]=[]; for(let h=0;h<24;h++){ let runs=0,load=0,fails=0,jobs=new Set();
    if(selDay<0){ for(let d=0;d<7;d++){const c=cell[q][d][h]; runs+=c.runs;load+=c.load;fails+=c.fails;c.jobs.forEach(x=>jobs.add(x));} }
    else { const c=cell[q][selDay][h]; runs=c.runs;load=c.load;fails=c.fails;c.jobs.forEach(x=>jobs.add(x)); }
    const v=metric==='load'?load:runs; data[q][h]={runs,load,fails,jobs,v}; max=Math.max(max,v);
  } }
  const bucket=v=>{ if(!v)return 0; const n=logScale?Math.log(1+v)/Math.log(1+max):v/max; return Math.min(5,1+Math.floor(n*5)); };
  for(const q of qs){ const l=document.createElement('div'); l.className='ql'; l.innerHTML=`<span class="dot" style="background:${QUEUE_COLOR[q]}"></span>${q}`; labels.appendChild(l);
    const row=document.createElement('div'); row.className='row';
    for(let h=0;h<24;h++){ const c=data[q][h]; const el=document.createElement('div'); el.className='hm-cell';
      if(c.v>0){ const hex=rampHex(bucket(c.v)); el.style.background=hex; el.textContent=c.v>=100?'99+':c.v; const ink=inkFor(hex); el.style.color=ink.color; el.style.textShadow=ink.shadow;
        el.addEventListener('mousemove',e=>showTip(cellTip(`${q} · ${selDay<0?'week':DAYS[selDay]} ${String(h).padStart(2,'0')}:00`,c),e)); el.addEventListener('mouseleave',hideTip);
        el.addEventListener('click',()=>openDrawer(`${q} · ${selDay<0?'whole week':DAYS[selDay]} ${String(h).padStart(2,'0')}:00`,`${c.runs} fires`,[...c.jobs]));
      } else el.style.background=rampHex(0);
      row.appendChild(el);
    }
    body.appendChild(row);
  }
}

// ---- Per-queue small multiples -------------------------------------------
function renderMulti(){
  const cell=compute(); const grid=document.getElementById('multiGrid'); grid.innerHTML='';
  for(const q of visibleQueues()){
    let max=0; for(let d=0;d<7;d++)for(let h=0;h<24;h++)max=Math.max(max,cellVal(cell[q][d][h]));
    const card=document.createElement('div'); card.className='mini';
    card.innerHTML=`<h3><span class="dot" style="background:${QUEUE_COLOR[q]}"></span>${q}<span style="margin-left:auto;font-weight:400;color:var(--text-muted);font-size:11px">max ${max}</span></h3>`;
    const gw=document.createElement('div'); gw.className='gridwrap';
    const sp=document.createElement('div'); const hours=document.createElement('div'); hours.className='hours';
    for(let h=0;h<24;h++){const s=document.createElement('div'); s.textContent=h%6===0?String(h).padStart(2,'0'):''; hours.appendChild(s);}
    const labels=document.createElement('div'); labels.className='rowlabels'; labels.style.gridTemplateRows='repeat(7,1fr)';
    const body=document.createElement('div'); body.className='body'; body.style.gridTemplateRows='repeat(7,1fr)';
    const bucket=v=>{ if(!v)return 0; const n=logScale?Math.log(1+v)/Math.log(1+max):v/max; return Math.min(5,1+Math.floor(n*5)); };
    for(const d of rowOrder()){ const l=document.createElement('div'); l.textContent=DAYS[d][0]; labels.appendChild(l);
      const row=document.createElement('div'); row.className='row';
      for(let h=0;h<24;h++){ const c=cell[q][d][h]; const el=document.createElement('div'); el.className='hm-cell'; el.style.background=cellVal(c)?rampHex(bucket(cellVal(c))):rampHex(0);
        if(c.runs){ el.addEventListener('mousemove',e=>showTip(cellTip(`${q} · ${DAYS[d]} ${String(h).padStart(2,'0')}:00`,c),e)); el.addEventListener('mouseleave',hideTip);
          el.addEventListener('click',()=>openDrawer(`${q} · ${DAYS[d]} ${String(h).padStart(2,'0')}:00`,`${c.runs} fires`,[...c.jobs])); }
        row.appendChild(el);
      }
      body.appendChild(row);
    }
    gw.appendChild(sp);gw.appendChild(hours);gw.appendChild(labels);gw.appendChild(body); card.appendChild(gw); grid.appendChild(card);
  }
}

// ---- Concurrency (duration-aware, stacked) --------------------------------
function renderConc(){
  const cell=compute(); const qs=visibleQueues();
  let worstDay=0,worstPeak=-1,worstPerQ=null;
  for(let d=0;d<7;d++){ const perQ=concurrencyForDay(cell,d); let p=0; for(let m=0;m<MIN_PER_DAY;m++)p=Math.max(p,totalAt(perQ,m)); if(p>worstPeak){worstPeak=p;worstDay=d;worstPerQ=perQ;} }
  const perQ=worstPerQ||concurrencyForDay(cell,worstDay);
  let peak=0,peakAt=0,breaches=0; for(let m=0;m<MIN_PER_DAY;m++){ const t=totalAt(perQ,m); if(t>peak){peak=t;peakAt=m;} if(t>cap)breaches++; }
  document.getElementById('ccPeak').textContent=peak; document.getElementById('ccCap').textContent=cap;
  document.getElementById('ccBreaches').textContent=breaches;
  document.getElementById('ccPeakAt').textContent=`${DAYS[worstDay]} ${String(Math.floor(peakAt/60)).padStart(2,'0')}:${String(peakAt%60).padStart(2,'0')}`;
  const W=1120,H=300,padL=38,padT=12,padB=26,padR=8,plotW=W-padL-padR,plotH=H-padT-padB,maxY=Math.max(peak,cap)*1.15;
  const x=m=>padL+(m/MIN_PER_DAY)*plotW, y=v=>padT+plotH-(v/maxY)*plotH; const B=5; let bars='';
  for(let m=0;m<MIN_PER_DAY;m+=B){ const counts={}; let tot=0; for(const q of qs){ let v=0; for(let k=m;k<m+B;k++)v=Math.max(v,perQ[q][k]); counts[q]=v; tot+=v; }
    if(!tot)continue; const bx=x(m),bw=Math.max(1.5,(plotW/MIN_PER_DAY)*B-0.4); let acc=0;
    for(const q of qs){ if(!counts[q])continue; const yt=y(acc+counts[q]),hg=y(acc)-y(acc+counts[q]); acc+=counts[q]; bars+=`<rect x="${bx.toFixed(1)}" y="${yt.toFixed(1)}" width="${bw.toFixed(1)}" height="${Math.max(0.6,hg).toFixed(1)}" fill="${QUEUE_COLOR[q]}" opacity="0.92"></rect>`; }
    if(tot>cap)bars+=`<rect x="${(bx-0.5).toFixed(1)}" y="${(y(tot)-2).toFixed(1)}" width="${(bw+1).toFixed(1)}" height="2" fill="var(--danger)"></rect>`;
  }
  const capY=y(cap); let grid=''; for(let h=0;h<=24;h+=3){ const gx=x(h*60); grid+=`<line x1="${gx}" y1="${padT}" x2="${gx}" y2="${padT+plotH}" stroke="var(--grid-line)"></line><text x="${gx}" y="${H-8}" font-size="10" text-anchor="middle">${String(h).padStart(2,'0')}:00</text>`; }
  let yt=''; for(let i=0;i<=4;i++){ const vv=Math.round(maxY/4*i),gy=y(vv); yt+=`<text x="${padL-6}" y="${gy+3}" font-size="10" text-anchor="end">${vv}</text><line x1="${padL}" y1="${gy}" x2="${W-padR}" y2="${gy}" stroke="var(--grid-line)" stroke-width="0.6"></line>`; }
  document.getElementById('ccChart').innerHTML=`<svg viewBox="0 0 ${W} ${H}" width="100%" preserveAspectRatio="xMidYMid meet">${yt}${grid}${bars}<line x1="${padL}" y1="${capY}" x2="${W-padR}" y2="${capY}" stroke="var(--accent)" stroke-width="1.5" stroke-dasharray="5 4"></line><text x="${W-padR}" y="${capY-5}" font-size="10" text-anchor="end" style="fill:var(--accent)">capacity ${cap}</text></svg>`;
  document.getElementById('ccLegend').innerHTML=qs.map(q=>`<span class="key"><span class="swatch" style="background:${QUEUE_COLOR[q]}"></span>${q}</span>`).join('')+`<span class="key"><span class="swatch" style="background:var(--accent);height:2px"></span>capacity</span><span class="key" style="color:var(--text-muted)">worst day: <b style="color:var(--text)">${DAYS[worstDay]}</b></span>`;
}

// ---- Calendar -------------------------------------------------------------
function renderCal(){
  const cell=compute(),g=mergeDH(cell); fillHours(document.getElementById('calHours'));
  const labels=document.getElementById('calRowLabels'),body=document.getElementById('calBody'); labels.innerHTML='';body.innerHTML='';
  const ctxColor=document.getElementById('ctxColor').value; const histColor=source==='historical'&&ctxColor!=='vol';
  let maxVol=0,maxDur=0; for(let d=0;d<7;d++)for(let h=0;h<24;h++){maxVol=Math.max(maxVol,cellVal(g[d][h]));maxDur=Math.max(maxDur,g[d][h].p95);}
  const vb=v=>{ if(!v)return 0; const n=logScale?Math.log(1+v)/Math.log(1+maxVol):v/maxVol; return Math.min(5,1+Math.floor(n*5)); };
  for(const d of rowOrder()){ const l=document.createElement('div'); l.textContent=DAYS[d]; l.style.textAlign='right'; l.style.paddingRight='6px'; labels.appendChild(l);
    const row=document.createElement('div'); row.className='row';
    for(let h=0;h<24;h++){ const c=g[d][h]; const el=document.createElement('div'); el.className='hm-cell'; if(d===NOW_DAY&&h===NOW_HOUR)el.classList.add('now');
      if(cellVal(c)>0){ let hex; if(histColor&&ctxColor==='fail')hex=failHex(Math.round(c.fails/Math.max(1,c.runs)*100)); else if(histColor&&ctxColor==='dur')hex=rampHex(Math.min(5,1+Math.floor((c.p95/(maxDur||1))*5))); else hex=rampHex(vb(cellVal(c)));
        el.style.background=hex; el.textContent=cellVal(c)>=100?'99+':cellVal(c); const ink=inkFor(hex); el.style.color=ink.color; el.style.textShadow=ink.shadow;
        el.addEventListener('mousemove',e=>showTip(cellTip(`${DAYS[d]} ${String(h).padStart(2,'0')}:00`,c),e)); el.addEventListener('mouseleave',hideTip);
        el.addEventListener('click',()=>openDrawer(`${DAYS[d]} ${String(h).padStart(2,'0')}:00`,`${c.runs} fires`,[...c.jobs]));
      } else el.style.background=rampHex(0);
      row.appendChild(el);
    }
    body.appendChild(row);
  }
  document.getElementById('calLegend').style.display=(source==='historical'&&ctxColor==='fail')?'none':'flex';
}

// ---- Recommendations (duration-aware overlap detection + real simulation)--
// Peak concurrency of a set of intervals via an event sweep (ends before
// starts at equal time, so back-to-back jobs don't count as overlapping).
function peakConc(starts,durs){
  const ev=[]; for(let i=0;i<starts.length;i++){ ev.push([starts[i],1]); ev.push([starts[i]+Math.max(1,durs[i]),-1]); }
  ev.sort((a,b)=>a[0]-b[0]||a[1]-b[1]); let cur=0,mx=0; for(const e of ev){ cur+=e[1]; if(cur>mx)mx=cur; } return mx;
}
// duration intervals for one (day, queue)
function intervalsForDayQueue(d,q){
  const out=[]; for(const j of activeJobs()){ if(j.queue!==q||j.period)continue;
    for(const t of jobFires(j)){ if(Math.floor(t/MIN_PER_DAY)!==d)continue; const s=t%MIN_PER_DAY; out.push({s,e:Math.min(MIN_PER_DAY,s+Math.max(1,j.durMin)),dur:Math.max(1,j.durMin),id:j.id}); } }
  return out;
}
function daysLabel(set){ const a=[...set].sort((x,y)=>x-y);
  if(a.length===7)return 'every day';
  if(a.length===5&&a.every(x=>x<5))return 'weekdays';
  if(a.length===2&&a.includes(5)&&a.includes(6))return 'weekends';
  return a.map(d=>DAYS[d]).join(', ');
}
function buildRecommendations(){
  const map={};
  for(let d=0;d<7;d++)for(const q of visibleQueues()){
    const ivs=intervalsForDayQueue(d,q); if(ivs.length<3)continue;
    // minute concurrency from real intervals -> find the peak minute
    const mc=new Array(MIN_PER_DAY).fill(0); for(const iv of ivs)for(let m=iv.s;m<iv.e;m++)mc[m]++;
    let peak=0,peakMin=0; for(let m=0;m<MIN_PER_DAY;m++)if(mc[m]>peak){peak=mc[m];peakMin=m;}
    if(peak<3)continue;
    const contrib=ivs.filter(iv=>iv.s<=peakMin&&peakMin<iv.e); if(contrib.length<3)continue;
    // simulate: spread the contributing jobs' starts evenly across the hour
    const durs=contrib.map(c=>c.dur), N=contrib.length, W=60;
    const after=peakConc(contrib.map((_,i)=>Math.round(i*W/N)),durs);
    const ids=contrib.map(c=>c.id).sort();
    const key=`${q}|${peakMin}|${ids.join(',')}`;
    (map[key]=map[key]||{queue:q,peakMin,contrib,peak,after,days:new Set()}).days.add(d);
  }
  let recs=Object.values(map).filter(r=>r.after<r.peak); // only when staggering actually helps
  recs.forEach(r=>r.sev = r.peak>cap?'high':(r.peak>=Math.max(3,cap*0.6)?'med':'low'));
  recs.sort((a,b)=>(b.peak-b.after)-(a.peak-a.after)||b.peak-a.peak);
  return recs;
}
function renderRec(){
  const recs=buildRecommendations(); const list=document.getElementById('recList');
  const high=recs.filter(r=>r.sev==='high').length;
  document.getElementById('recBadge').textContent=recs.length;
  document.getElementById('recBadge').style.display=recs.length?'inline-block':'none';
  document.getElementById('recBadge').style.background=high?'var(--danger)':'var(--warn)';
  if(!recs.length){ list.innerHTML=`<div class="jm" style="color:var(--text-muted)">No duration-overlap hotspots (≥3 jobs running together) in the current selection. 🎉</div>`; return; }
  list.innerHTML=recs.map(r=>{ const time=`${String(Math.floor(r.peakMin/60)).padStart(2,'0')}:${String(r.peakMin%60).padStart(2,'0')}`;
    const cut=r.peak-r.after; const overCap=r.peak>cap;
    return `<div class="rec ${r.sev==='high'?'high':''}">
      <div class="rh"><span class="sev ${r.sev==='low'?'med':r.sev}">${r.sev==='high'?'high':'medium'}</span>
        <span class="when"><span style="display:inline-block;width:9px;height:9px;border-radius:50%;background:${QUEUE_COLOR[r.queue]};margin-right:6px"></span>${r.queue} · peak ~${time} · <span style="color:var(--text-muted);font-weight:400">${daysLabel(r.days)}</span></span></div>
      <div class="body2"><b>${r.peak} jobs run concurrently</b> here${overCap?` — over the ${cap}-worker capacity`:''} (counting each job's p95 duration). Staggering their starts across the hour drops the peak to <span class="delta">~${r.after}</span> <span style="color:var(--text-muted)">(−${cut})</span>.</div>
      <div class="acts">${r.contrib.slice(0,5).map(c=>`<button class="btn btn-sm" onclick="mockEdit('${c.id}')">${c.id}</button>`).join('')}${r.contrib.length>5?`<span class="pill">+${r.contrib.length-5}</span>`:''}
        <button class="btn btn-sm btn-accent" onclick="mockStagger('${r.queue}','${time}')">Auto-stagger ${r.contrib.length} jobs</button></div></div>`; }).join('');
}
window.mockStagger=function(q,time){ alert(`(mockup) Would compute staggered cron expressions for the cluster on "${q}" near ${time}, simulate the new concurrency curve, then open an audit-logged confirm dialog.`); };

// ---- Insights -------------------------------------------------------------
function renderInsights(){
  const cell=compute(); const qs=visibleQueues(); let total=0,perQ={};
  for(const q of qs){ perQ[q]=0; for(let d=0;d<7;d++)for(let h=0;h<24;h++){ perQ[q]+=cellVal(cell[q][d][h]); total+=cellVal(cell[q][d][h]); } }
  document.getElementById('insTotal').innerHTML=`${Math.round(total/7)} <small>${metric==='load'?'wk-min':'fires'}</small>`;
  let tq='—',tv=-1; for(const q in perQ)if(perQ[q]>tv){tv=perQ[q];tq=q;}
  document.getElementById('insQueue').innerHTML=tv>0?`<span style="color:${QUEUE_COLOR[tq]}">●</span> ${tq} <small>${Math.round(tv/7)}/day</small>`:'—';
  // peak concurrency over week
  let peak=0,peakAt=0,peakDay=0; const overHours=new Set();
  for(let d=0;d<7;d++){ const perC=concurrencyForDay(cell,d); for(let m=0;m<MIN_PER_DAY;m++){ const t=totalAt(perC,m); if(t>peak){peak=t;peakAt=m;peakDay=d;} if(t>cap)overHours.add(d*24+Math.floor(m/60)); } }
  document.getElementById('insPeak').innerHTML=`${peak} <small>${DAYS[peakDay]} ${String(Math.floor(peakAt/60)).padStart(2,'0')}:${String(peakAt%60).padStart(2,'0')}</small>`;
  document.getElementById('insPeakCard').classList.toggle('alert',peak>cap);
  document.getElementById('insOver').innerHTML=`${overHours.size} <small>vs ${cap} workers</small>`;
  document.getElementById('insOverCard').classList.toggle('alert',overHours.size>0);
  const recs=buildRecommendations(); document.getElementById('insRec').innerHTML=`${recs.length} <small>${recs.length?'click to view':'all clear'}</small>`;
  document.getElementById('insRecCard').classList.toggle('alert',recs.some(r=>r.sev==='high'));
}

// ---- Table + long-period note --------------------------------------------
function renderTable(){
  const tb=document.getElementById('jobTable');
  tb.innerHTML=activeJobs().concat(JOBS.filter(j=>j.period&&visibleQueues().includes(j.queue)&&!(hideSub&&j.sub)&&!activeJobs().includes(j))).map(j=>{
    const load=Math.round(jobFires(j).length*(metric==='load'?j.durMin:1)/7);
    const valCell=source==='historical'?`<td style="color:${j.fail>=0.2?'var(--danger)':j.fail>=0.08?'var(--warn)':'var(--ok)'}">${Math.round(j.fail*100)}%</td>`:`<td>${load}${j.period?'<span class="pill">'+j.period+'</span>':''}</td>`;
    return `<tr><td>${j.id}${j.period?'<span class="pill">long-period</span>':''}</td><td><code>${j.cron}</code></td><td><span class="q" style="background:${QUEUE_COLOR[j.queue]}">${j.queue}</span></td><td>${fmtTz(j.tz||0)}</td>${valCell}<td>${nextRun(j)}</td></tr>`;
  }).join('');
  document.getElementById('colMetric').textContent=source==='historical'?'Failure rate':(metric==='load'?'Wk-min/day':'Fires/day');
  // long-period banner
  const lp=JOBS.filter(j=>j.period&&visibleQueues().includes(j.queue)&&!(hideSub&&j.sub));
  const note=document.getElementById('longNote');
  if(lp.length&&windowMode==='ideal'){ note.style.display='block'; note.innerHTML=`<b>${lp.length} long-period job(s)</b> (${lp.map(j=>j.id).join(', ')}) can't be represented in an idealized week. Switch <b>Window → Next 7 days</b> to see ones that fire soon.`; }
  else if(lp.length&&windowMode==='next7'){ const firing=lp.filter(j=>jobFires(j).length); note.style.display='block'; note.innerHTML=firing.length?`<b>${firing.length} long-period job</b> fires within the next 7 days: ${firing.map(j=>j.id).join(', ')}.`:`No long-period jobs fire in the next 7 days (${lp.length} exist but fall outside the window).`; }
  else note.style.display='none';
}
function nextRun(j){ const f=(j.period?(j.next7?j.next7():[]):j.gen()); if(!f.length)return j.period?`(every ${j.period.replace('ly','')})`:'—';
  const nowMin=NOW_DAY*MIN_PER_DAY+NOW_HOUR*60; const shift=viewTz-(j.tz||0); const sh=f.map(m=>((m+shift)%MIN_PER_WEEK+MIN_PER_WEEK)%MIN_PER_WEEK).sort((a,b)=>a-b);
  const t=sh.find(x=>x>nowMin)??sh[0]; const d=Math.floor(t/MIN_PER_DAY),mod=t%MIN_PER_DAY; return `${DAYS[d]} ${String(Math.floor(mod/60)).padStart(2,'0')}:${String(mod%60).padStart(2,'0')}`; }

// ---- Export ---------------------------------------------------------------
function exportCSV(){
  const cell=compute(); let csv='',name='';
  if(view==='qh'){ name='queue-hour'; csv='queue,'+Array.from({length:24},(_,h)=>String(h).padStart(2,'0')).join(',')+'\n';
    for(const q of visibleQueues()){ const r=[q]; for(let h=0;h<24;h++){ let v=0; if(selDay<0){for(let d=0;d<7;d++)v+=cellVal(cell[q][d][h]);}else v=cellVal(cell[q][selDay][h]); r.push(v); } csv+=r.join(',')+'\n'; } }
  else { name='day-hour'; const g=mergeDH(cell); csv='day,'+Array.from({length:24},(_,h)=>String(h).padStart(2,'0')).join(',')+'\n';
    for(const d of rowOrder()){ const r=[DAYS[d]]; for(let h=0;h<24;h++)r.push(cellVal(g[d][h])); csv+=r.join(',')+'\n'; } }
  const blob=new Blob([csv],{type:'text/csv'}); const a=document.createElement('a'); a.href=URL.createObjectURL(blob);
  a.download=`heatmap-${name}-${metric}-${source}.csv`; a.click(); URL.revokeObjectURL(a.href);
}

// ---- Chrome / wiring ------------------------------------------------------
function buildChips(){ const c=document.getElementById('queueChips'); c.innerHTML='';
  for(const q of QUEUES){ const el=document.createElement('span'); el.className='chip'+(selectedQueues.has(q)?'':' off'); el.innerHTML=`<span class="dot" style="background:${QUEUE_COLOR[q]}"></span>${q}`;
    el.addEventListener('click',()=>{ if(selectedQueues.has(q))selectedQueues.delete(q); else selectedQueues.add(q); if(!selectedQueues.size)selectedQueues.add(q); buildChips(); renderAll(); }); c.appendChild(el); } }
function syncChrome(){ const hist=source==='historical';
  document.getElementById('ctxDayWrap').style.display=view==='qh'?'inline-flex':'none';
  document.getElementById('ctxColorWrap').style.display=(hist&&view==='cal')?'inline-flex':'none';
  document.getElementById('ctxCapWrap').style.display=(view==='conc')?'inline-flex':'none';
  document.getElementById('srcNote').textContent=hist
    ? 'Historical: actual past runs; color encodes failure rate / p95. Needs IStorageMetricsProvider per adapter.'
    : 'Projected: computed from cron via Cronos over the chosen window, honoring each job\'s timezone. No storage required.';
}
function renderAll(){ renderInsights();
  if(view==='punch')renderPunch(); else if(view==='qh')renderQH(); else if(view==='multi')renderMulti(); else if(view==='conc')renderConc(); else if(view==='rec')renderRec(); else renderCal();
  renderRec(); renderTable(); syncChrome(); }

document.getElementById('viewSeg').addEventListener('click',e=>{ const b=e.target.closest('button'); if(!b)return; view=b.dataset.view;
  [...document.querySelectorAll('#viewSeg button')].forEach(x=>x.classList.toggle('active',x===b)); document.querySelectorAll('.view').forEach(v=>v.classList.remove('active')); document.getElementById('view-'+view).classList.add('active'); renderAll(); });
document.getElementById('sourceSeg').addEventListener('click',e=>{ const b=e.target.closest('button'); if(!b)return; source=b.dataset.source; [...document.querySelectorAll('#sourceSeg button')].forEach(x=>x.classList.toggle('active',x===b)); renderAll(); });
document.getElementById('windowSel').addEventListener('change',e=>{ windowMode=e.target.value; renderAll(); });
document.getElementById('tzSel').addEventListener('change',e=>{ viewTz=+e.target.value; renderAll(); });
document.getElementById('metricSel').addEventListener('change',e=>{ metric=e.target.value; renderAll(); });
document.getElementById('topN').addEventListener('change',e=>{ topN=+e.target.value; renderAll(); });
document.getElementById('ctxDay').addEventListener('change',e=>{ selDay=e.target.value==='-1'?-1:DAYS.indexOf(e.target.value); renderAll(); });
document.getElementById('ctxColor').addEventListener('change',renderAll);
document.getElementById('ctxCap').addEventListener('input',e=>{ cap=Math.max(1,parseInt(e.target.value)||12); renderAll(); });
document.getElementById('hideSub').addEventListener('change',e=>{ hideSub=e.target.checked; renderAll(); });
document.getElementById('logScale').addEventListener('change',e=>{ logScale=e.target.checked; renderAll(); });
document.getElementById('qAll').addEventListener('click',()=>{ selectedQueues=new Set(QUEUES); buildChips(); renderAll(); });
document.getElementById('qNone').addEventListener('click',()=>{ selectedQueues=new Set([QUEUES[0]]); buildChips(); renderAll(); });
document.getElementById('themeBtn').addEventListener('click',()=>{ const h=document.documentElement; h.dataset.theme=h.dataset.theme==='dark'?'light':'dark'; renderAll(); });
document.getElementById('exportBtn').addEventListener('click',exportCSV);
document.getElementById('drawerClose').addEventListener('click',closeDrawer);
document.getElementById('scrim').addEventListener('click',closeDrawer);
document.getElementById('insRecCard').addEventListener('click',()=>{ document.querySelector('#viewSeg button[data-view="rec"]').click(); });
document.getElementById('insPeakCard').addEventListener('click',()=>{ document.querySelector('#viewSeg button[data-view="conc"]').click(); });
document.getElementById('insOverCard').addEventListener('click',()=>{ document.querySelector('#viewSeg button[data-view="conc"]').click(); });
window.addEventListener('resize',()=>{ if(view==='conc')renderConc(); });

try{ const tz=Intl.DateTimeFormat().resolvedOptions().timeZone; document.getElementById('tzline').textContent=`Queue × day × hour · drill-down · overlap recommendations · duration-aware load · demo "now" = Thu 14:00 · ${tz}`; }catch{}
buildChips(); renderAll();
