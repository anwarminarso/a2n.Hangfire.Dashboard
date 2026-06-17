// ===========================================================================
// Recurring Schedule Heatmap — Mockup v4 (superset of v3 + cron/ad-hoc overlay)
// Restores v3 controls (Load metric, Hide sub-hourly, Log, Projected/Historical,
// Window, View TZ) and views (Punchcard, Per-queue, Calendar), and adds the
// two-class model: CRON (recurring) + AD-HOC (on-demand demand profile).
// ===========================================================================
const DAYS=['Mon','Tue','Wed','Thu','Fri','Sat','Sun'];
const MIN_PER_DAY=1440, MIN_PER_WEEK=10080, NOW_DAY=3, NOW_HOUR=14;
const QUEUE_COLOR={ default:'#4dabf7', billing:'#f783ac', reports:'#ffa94d', sync:'#38d9a9', maint:'#b197fc', metrics:'#ffe066', vector:'#ff8787', email:'#9775fa' };

// CRON (recurring) jobs — projectable + historical
const CRON=[
  { id:'EmailDigest.SendDaily',    cron:'0 8 * * *',       queue:'default', sub:false, dur:2,  tz:0,    fail:0.01, gen:()=>atDaily(8,0) },
  { id:'Billing.GenerateInvoices', cron:'0 9 * * 1-5',     queue:'billing', sub:false, dur:14, tz:-300, fail:0.18, gen:()=>weekdays(9,0) },
  { id:'Reports.NightlyRollup',    cron:'0 2 * * *',       queue:'reports', sub:false, dur:22, tz:0,    fail:0.30, gen:()=>atDaily(2,0) },
  { id:'Reports.NightlyExport',    cron:'0 2 * * *',       queue:'reports', sub:false, dur:18, tz:0,    fail:0.05, gen:()=>atDaily(2,0) },
  { id:'Reports.HourlyAggregate',  cron:'0 * * * *',       queue:'reports', sub:false, dur:3,  tz:0,    fail:0.04, gen:()=>everyHour(0) },
  { id:'Backup.Database',          cron:'0 2 * * *',       queue:'maint',   sub:false, dur:35, tz:0,    fail:0.08, gen:()=>atDaily(2,0) },
  { id:'Cleanup.TempFiles',        cron:'30 3 * * *',      queue:'maint',   sub:false, dur:5,  tz:0,    fail:0.02, gen:()=>atDaily(3,30) },
  { id:'Sync.ShopifyStock',        cron:'*/15 * * * *',    queue:'sync',    sub:true,  dur:2,  tz:0,    fail:0.06, gen:()=>everyN(15) },
  { id:'Sync.SapInventory',        cron:'*/15 * * * *',    queue:'sync',    sub:true,  dur:4,  tz:0,    fail:0.22, gen:()=>everyN(15,2) },
  { id:'Heartbeat.Ping',           cron:'*/5 * * * *',     queue:'default', sub:true,  dur:1,  tz:0,    fail:0.00, gen:()=>everyN(5) },
  { id:'Metrics.Scrape',           cron:'*/15 * * * *',    queue:'metrics', sub:true,  dur:1,  tz:0,    fail:0.01, gen:()=>everyN(15,7) },
  { id:'Webhook.RetrySweep',       cron:'*/10 * * * *',    queue:'sync',    sub:true,  dur:2,  tz:0,    fail:0.12, gen:()=>everyN(10) },
  { id:'Forecast.Recompute',       cron:'0 8,12,16 * * *', queue:'reports', sub:false, dur:9,  tz:0,    fail:0.15, gen:()=>multiHour([8,12,16],0) },
  { id:'Notify.WeeklySummary',     cron:'0 9 * * 1',       queue:'default', sub:false, dur:2,  tz:0,    fail:0.00, gen:()=>weekly(0,9,0) },
];
function atDaily(h,m){return DAYS.map((_,d)=>d*MIN_PER_DAY+h*60+m);}
function weekdays(h,m){return [0,1,2,3,4].map(d=>d*MIN_PER_DAY+h*60+m);}
function weekly(day,h,m){return [day*MIN_PER_DAY+h*60+m];}
function everyHour(m){const o=[];for(let d=0;d<7;d++)for(let h=0;h<24;h++)o.push(d*MIN_PER_DAY+h*60+m);return o;}
function multiHour(hs,m){const o=[];for(let d=0;d<7;d++)hs.forEach(h=>o.push(d*MIN_PER_DAY+h*60+m));return o;}
function everyN(n,off=0){const o=[];for(let d=0;d<7;d++)for(let t=off;t<MIN_PER_DAY;t+=n)o.push(d*MIN_PER_DAY+t);return o;}

// AD-HOC (on-demand) demand profile per queue × day × hour -> {avg, p95}
const ADHOC=[
  { id:'Vectorize.UploadedDocs', queue:'vector',  dur:6, base:7, shape:'business',    burst:2.6 },
  { id:'Email.BulkReport',       queue:'email',   dur:3, base:5, shape:'officehours', burst:3.0 },
  { id:'Reports.RealtimeGen',    queue:'reports', dur:8, base:3, shape:'business',    burst:2.2 },
];
function hourFactor(s,h){ if(s==='business')return h>=8&&h<=19?1:(h>=6&&h<=22?0.35:0.08); if(s==='officehours')return (h>=9&&h<=11)||(h>=14&&h<=16)?1:(h>=8&&h<=18?0.4:0.06); return 0.5; }
function dayFactor(d){return d<5?1:0.25;}
function adhocCell(src,d,h){ const avg=src.base*hourFactor(src.shape,h)*dayFactor(d); return {avg,p95:avg*src.burst}; }

const CRON_QUEUES=[...new Set(CRON.map(j=>j.queue))];
const ADHOC_QUEUES=[...new Set(ADHOC.map(j=>j.queue))];
const ALL_QUEUES=[...new Set([...CRON_QUEUES,...ADHOC_QUEUES])];

// ---- State ----
let cls='combined', source='projected', view='planner';
let metric='count', hideSub=false, logScale=false, windowMode='ideal', viewTz=420;
let agg='avg', lookback='4w', cap=12, selDay=3, colorBy='vol';
let selectedQueues=new Set(ALL_QUEUES);

function theme(){return document.documentElement.dataset.theme==='dark'?'dark':'light';}
const RAMP={light:['#eef1f5','#cfe8ef','#8fd3c7','#4cb3a9','#2f8f9e','#1f5f86'],dark:['#1c2434','#1f3b4d','#1f5f6b','#2f8f8a','#4cc0a8','#8fe3c7']};
const FAILS={light:{ok:'#198754',warn:'#fd7e14',high:'#e8590c',danger:'#dc3545'},dark:{ok:'#2f9e44',warn:'#fd7e14',high:'#e8590c',danger:'#e03131'}};
function rampHex(i){return RAMP[theme()][Math.max(0,Math.min(5,Math.round(i)))];}
function failHex(p){const f=FAILS[theme()];if(p<8)return f.ok;if(p<15)return f.warn;if(p<25)return f.high;return f.danger;}
function inkFor(hex){let h=hex.replace('#','');if(h.length===3)h=h[0]+h[0]+h[1]+h[1]+h[2]+h[2];const r=parseInt(h.slice(0,2),16),g=parseInt(h.slice(2,4),16),b=parseInt(h.slice(4,6),16);const y=r*.299+g*.587+b*.114;return y>186?{color:'#10141d',shadow:'0 1px 1px rgba(255,255,255,.55)'}:{color:'#fff',shadow:'0 1px 2px rgba(0,0,0,.55)'};}

const showCron=()=>cls==='cron'||cls==='combined';
const showAdhoc=()=>cls==='adhoc'||cls==='combined';
function cronQueues(){return CRON_QUEUES.filter(q=>selectedQueues.has(q));}
function adhocQueues(){return ADHOC_QUEUES.filter(q=>selectedQueues.has(q));}
function cronJobs(){return CRON.filter(j=>selectedQueues.has(j.queue)&&!(hideSub&&j.sub));}
function rowOrder(){if(windowMode!=='next7')return [0,1,2,3,4,5,6];const o=[];for(let i=0;i<7;i++)o.push((NOW_DAY+i)%7);return o;}
function jobFires(j){const shift=viewTz-(j.tz||0);return j.gen().map(m=>((m+shift)%MIN_PER_WEEK+MIN_PER_WEEK)%MIN_PER_WEEK);}

// ---- cron aggregation ----
function cronGrid(){
  const g={}; for(const q of CRON_QUEUES)g[q]=Array.from({length:7},()=>Array.from({length:24},()=>({fires:0,load:0,fails:0,jobs:new Set()})));
  const r0=()=>0;
  for(const j of cronJobs()){ let seed=7; for(let i=0;i<j.id.length;i++)seed=(seed*31+j.id.charCodeAt(i))>>>0; let s=seed%2147483647||1; const rnd=()=>(s=s*16807%2147483647)/2147483647;
    for(const t of jobFires(j)){ const d=Math.floor(t/MIN_PER_DAY),h=Math.floor((t%MIN_PER_DAY)/60),c=g[j.queue][d][h]; c.fires++; c.load+=j.dur; c.jobs.add(j.id); if(source==='historical'&&rnd()<j.fail)c.fails++; } }
  return g;
}
function cronVal(c){return metric==='load'?c.load:c.fires;}
function cronCellAll(g,d,h){let fires=0,load=0,fails=0,jobs=new Set(),byQ={};for(const q of cronQueues()){const c=g[q][d][h];fires+=c.fires;load+=c.load;fails+=c.fails;c.jobs.forEach(x=>jobs.add(x));const v=cronVal(c);if(v)byQ[q]=(byQ[q]||0)+v;}return {fires,load,fails,jobs,byQ,val:metric==='load'?load:fires};}
function domQ(byQ){let b=null,m=-1;for(const q in byQ)if(byQ[q]>m){m=byQ[q];b=q;}return b;}

// ---- ad-hoc demand ----
function adhocVal(d,h){let v=0;for(const s of ADHOC){if(!selectedQueues.has(s.queue))continue;v+=adhocCell(s,d,h)[agg];}return v;}
function adhocByQueue(d,h){const o={};for(const s of ADHOC){if(!selectedQueues.has(s.queue))continue;o[s.queue]=(o[s.queue]||0)+adhocCell(s,d,h)[agg];}return o;}

// ---- tooltip ----
const tip=document.getElementById('tip');
function showTip(html,e){tip.innerHTML=html;tip.style.display='block';moveTip(e);}
function moveTip(e){const p=14;let x=e.clientX+p,y=e.clientY+p;const r=tip.getBoundingClientRect();if(x+r.width>innerWidth)x=e.clientX-r.width-p;if(y+r.height>innerHeight)y=e.clientY-r.height-p;tip.style.left=x+'px';tip.style.top=y+'px';}
function hideTip(){tip.style.display='none';}
function fillHours(el){el.innerHTML='';for(let h=0;h<24;h++){const s=document.createElement('div');s.textContent=h%3===0?String(h).padStart(2,'0'):'';el.appendChild(s);}}

// ---- PLANNER ----
function renderPlanner(){
  const g=cronGrid(); fillHours(document.getElementById('plHours'));
  const labels=document.getElementById('plRowLabels'),body=document.getElementById('plBody');labels.innerHTML='';body.innerHTML='';
  let maxA=0,maxC=0;for(let d=0;d<7;d++)for(let h=0;h<24;h++){maxA=Math.max(maxA,adhocVal(d,h));maxC=Math.max(maxC,cronCellAll(g,d,h).val);}
  const safeT=maxA*0.12;
  for(const d of rowOrder()){
    const l=document.createElement('div');l.textContent=DAYS[d];l.style.textAlign='right';l.style.paddingRight='6px';labels.appendChild(l);
    const row=document.createElement('div');row.className='row';
    for(let h=0;h<24;h++){
      const cell=document.createElement('div');cell.className='pl-cell';
      const av=adhocVal(d,h),cc=cronCellAll(g,d,h);
      if(showAdhoc()&&maxA>0&&av>0){const n=logScale?Math.log(1+av)/Math.log(1+maxA):av/maxA;cell.style.background=rampHex(1+n*4);}else cell.style.background=rampHex(0);
      if(showAdhoc()&&av<=safeT&&cc.val===0&&!(d===NOW_DAY&&h===NOW_HOUR))cell.classList.add('safe');
      if(d===NOW_DAY&&h===NOW_HOUR)cell.classList.add('now');
      if(showCron()&&cc.val>0&&maxC>0){const dot=document.createElement('div');dot.className='pl-dot';const n=logScale?Math.log(1+cc.val)/Math.log(1+maxC):cc.val/maxC;const sz=5+n*15;dot.style.width=sz+'px';dot.style.height=sz+'px';dot.style.background=source==='historical'?failHex(Math.round(cc.fails/Math.max(1,cc.fires)*100)):(QUEUE_COLOR[domQ(cc.byQ)]||'var(--accent)');dot.style.boxShadow='0 0 0 1.5px rgba(0,0,0,.25)';cell.appendChild(dot);}
      cell.addEventListener('mousemove',e=>showTip(plTip(d,h,av,cc),e));cell.addEventListener('mouseleave',hideTip);
      row.appendChild(cell);
    }
    body.appendChild(row);
  }
  const ql=document.getElementById('plQueueLegend');const qs=showCron()?cronQueues():[];
  ql.innerHTML=qs.length?'· cron: '+qs.map(q=>`<span style="display:inline-block;width:9px;height:9px;border-radius:50%;background:${QUEUE_COLOR[q]};margin:0 3px"></span>${q}`).join(' '):'';
}
function plTip(d,h,av,cc){let s=`<div class="t">${DAYS[d]} ${String(h).padStart(2,'0')}:00</div>`;
  if(showAdhoc()){const bq=adhocByQueue(d,h);const parts=Object.entries(bq).filter(([,v])=>v>0.05).map(([q,v])=>`${q} ${v.toFixed(1)}`).join(', ');s+=`<div class="j">on-demand (${agg}): <b style="color:var(--text)">${av.toFixed(1)}</b>/h${parts?` — ${parts}`:''}</div>`;}
  if(showCron()){s+=`<div class="j">cron: ${cc.fires} fires${metric==='load'?` · ${cc.load} wk-min`:''}${cc.fires?` — ${[...cc.jobs].slice(0,4).join(', ')}${cc.jobs.size>4?'…':''}`:''}</div>`;}
  if(showAdhoc()&&showCron()&&cc.val===0&&av<0.5)s+=`<div class="j" style="color:var(--safe)">✓ safe window to schedule</div>`;
  return s;}

// ---- PUNCHCARD ----
function renderPunch(){
  const g=cronGrid();fillHours(document.getElementById('pcHours'));
  const labels=document.getElementById('pcRowLabels'),body=document.getElementById('pcBody');labels.innerHTML='';body.innerHTML='';
  // punchcard shows cron if cron-class else ad-hoc magnitude
  let max=0;for(let d=0;d<7;d++)for(let h=0;h<24;h++){const v=showCron()?cronCellAll(g,d,h).val:adhocVal(d,h);max=Math.max(max,v);}
  const scale=v=>{if(!v)return 0;const n=logScale?Math.log(1+v)/Math.log(1+max):v/max;return 5+n*17;};
  for(const d of rowOrder()){
    const l=document.createElement('div');l.textContent=DAYS[d];l.style.textAlign='right';l.style.paddingRight='6px';labels.appendChild(l);
    const row=document.createElement('div');row.className='row';
    for(let h=0;h<24;h++){const div=document.createElement('div');div.className='pc-cell';if(d===NOW_DAY&&h===NOW_HOUR)div.classList.add('now');
      const cc=cronCellAll(g,d,h);const v=showCron()?cc.val:adhocVal(d,h);
      if(v>0){const dot=document.createElement('div');dot.className='pc-dot';const sz=scale(v);dot.style.width=sz+'px';dot.style.height=sz+'px';
        dot.style.background=showCron()?(source==='historical'?failHex(Math.round(cc.fails/Math.max(1,cc.fires)*100)):(QUEUE_COLOR[domQ(cc.byQ)]||'var(--accent)')):'var(--text-muted)';div.appendChild(dot);}
      div.addEventListener('mousemove',e=>showTip(plTip(d,h,adhocVal(d,h),cc),e));div.addEventListener('mouseleave',hideTip);
      row.appendChild(div);}
    body.appendChild(row);
  }
  const cl=document.getElementById('pcColorLegend');
  cl.innerHTML=showCron()?(source==='historical'?`<span class="key"><span class="swatch" style="background:${FAILS[theme()].ok}"></span>0%</span><span class="key"><span class="swatch" style="background:${FAILS[theme()].danger}"></span>25%+</span>`:cronQueues().map(q=>`<span class="key"><span style="display:inline-block;width:10px;height:10px;border-radius:50%;background:${QUEUE_COLOR[q]}"></span>${q}</span>`).join('')):'<span class="key">on-demand magnitude</span>';
}

// ---- QUEUE x HOUR ----
function renderQH(){
  const g=cronGrid();fillHours(document.getElementById('qhHours'));
  const labels=document.getElementById('qhRowLabels'),body=document.getElementById('qhBody');
  const qs=[...(showCron()?cronQueues():[]),...(showAdhoc()?adhocQueues():[])].filter((q,i,a)=>a.indexOf(q)===i);
  labels.style.gridTemplateRows=`repeat(${qs.length||1},1fr)`;body.style.gridTemplateRows=`repeat(${qs.length||1},1fr)`;labels.innerHTML='';body.innerHTML='';
  document.getElementById('qhDayLabel').textContent=selDay<0?'· whole week':'· '+DAYS[selDay];
  const data={};let max=0;
  for(const q of qs){data[q]=[];const isAd=ADHOC_QUEUES.includes(q)&&!CRON_QUEUES.includes(q)||(ADHOC_QUEUES.includes(q)&&cls==='adhoc');
    const useAd=ADHOC_QUEUES.includes(q)&&(cls==='adhoc'||(cls==='combined'&&!CRON_QUEUES.includes(q)));
    for(let h=0;h<24;h++){let v=0;
      if(useAd){if(selDay<0){for(let d=0;d<7;d++)v+=ADHOC.filter(s=>s.queue===q).reduce((a,s)=>a+adhocCell(s,d,h)[agg],0);}else v=ADHOC.filter(s=>s.queue===q).reduce((a,s)=>a+adhocCell(s,selDay,h)[agg],0);}
      else if(CRON_QUEUES.includes(q)&&showCron()){if(selDay<0){for(let d=0;d<7;d++)v+=cronVal(g[q][d][h]);}else v=cronVal(g[q][selDay][h]);}
      data[q][h]={v,useAd};max=Math.max(max,v);}}
  for(const q of qs){const isAd=ADHOC_QUEUES.includes(q);
    const l=document.createElement('div');l.className='ql';l.innerHTML=`<span class="dot" style="background:${QUEUE_COLOR[q]}"></span>${q}<span class="clstag">${isAd?'ad-hoc':'cron'}</span>`;labels.appendChild(l);
    const row=document.createElement('div');row.className='row';
    for(let h=0;h<24;h++){const cell=data[q][h];const el=document.createElement('div');el.className='hm-cell';
      if(cell.v>0.05){const n=logScale?Math.log(1+cell.v)/Math.log(1+(max||1)):cell.v/(max||1);const hex=rampHex(1+n*4);el.style.background=hex;el.textContent=cell.v>=10?Math.round(cell.v):cell.v.toFixed(cell.v<1?1:0);const ink=inkFor(hex);el.style.color=ink.color;el.style.textShadow=ink.shadow;
        el.addEventListener('mousemove',e=>showTip(`<div class="t">${q} · ${selDay<0?'week':DAYS[selDay]} ${String(h).padStart(2,'0')}:00</div><div class="j">${cell.useAd?`on-demand (${agg})`:(metric==='load'?'wk-min':'cron fires')}: ${cell.v.toFixed(1)}</div>`,e));el.addEventListener('mouseleave',hideTip);
      }else el.style.background=rampHex(0);
      row.appendChild(el);}
    body.appendChild(row);}
}

// ---- PER-QUEUE small multiples ----
function renderMulti(){
  const g=cronGrid();const grid=document.getElementById('multiGrid');grid.innerHTML='';
  const list=[...(showCron()?cronQueues().map(q=>({q,ad:false})):[]),...(showAdhoc()?adhocQueues().map(q=>({q,ad:true})):[])];
  for(const {q,ad} of list){
    let max=0;const val=(d,h)=>ad?ADHOC.filter(s=>s.queue===q).reduce((a,s)=>a+adhocCell(s,d,h)[agg],0):cronVal(g[q][d][h]);
    for(let d=0;d<7;d++)for(let h=0;h<24;h++)max=Math.max(max,val(d,h));
    const card=document.createElement('div');card.className='mini';
    card.innerHTML=`<h3><span class="dot" style="background:${QUEUE_COLOR[q]}"></span>${q}<span class="clstag">${ad?'ad-hoc':'cron'}</span><span style="margin-left:auto;font-weight:400;color:var(--text-muted);font-size:11px">max ${max.toFixed(ad?1:0)}</span></h3>`;
    const gw=document.createElement('div');gw.className='gridwrap';const sp=document.createElement('div');const hours=document.createElement('div');hours.className='hours';
    for(let h=0;h<24;h++){const s=document.createElement('div');s.textContent=h%6===0?String(h).padStart(2,'0'):'';hours.appendChild(s);}
    const labels=document.createElement('div');labels.className='rowlabels';labels.style.gridTemplateRows='repeat(7,1fr)';
    const body=document.createElement('div');body.className='body';body.style.gridTemplateRows='repeat(7,1fr)';
    for(const d of rowOrder()){const l=document.createElement('div');l.textContent=DAYS[d][0];labels.appendChild(l);const row=document.createElement('div');row.className='row';
      for(let h=0;h<24;h++){const v=val(d,h);const el=document.createElement('div');el.className='hm-cell';if(v>0.05){const n=logScale?Math.log(1+v)/Math.log(1+(max||1)):v/(max||1);el.style.background=rampHex(1+n*4);el.addEventListener('mousemove',e=>showTip(`<div class="t">${q} · ${DAYS[d]} ${String(h).padStart(2,'0')}:00</div><div class="j">${ad?`on-demand (${agg})`:'cron'}: ${v.toFixed(1)}</div>`,e));el.addEventListener('mouseleave',hideTip);}else el.style.background=rampHex(0);row.appendChild(el);}
      body.appendChild(row);}
    gw.appendChild(sp);gw.appendChild(hours);gw.appendChild(labels);gw.appendChild(body);card.appendChild(gw);grid.appendChild(card);
  }
}

// ---- CALENDAR ----
function renderCal(){
  const g=cronGrid();fillHours(document.getElementById('calHours'));
  const labels=document.getElementById('calRowLabels'),body=document.getElementById('calBody');labels.innerHTML='';body.innerHTML='';
  const histColor=showCron()&&source==='historical'&&colorBy==='fail';
  let max=0;for(let d=0;d<7;d++)for(let h=0;h<24;h++){const v=showCron()?cronCellAll(g,d,h).val:adhocVal(d,h);max=Math.max(max,v);}
  for(const d of rowOrder()){
    const l=document.createElement('div');l.textContent=DAYS[d];l.style.textAlign='right';l.style.paddingRight='6px';labels.appendChild(l);
    const row=document.createElement('div');row.className='row';
    for(let h=0;h<24;h++){const cc=cronCellAll(g,d,h);const v=showCron()?cc.val:adhocVal(d,h);const el=document.createElement('div');el.className='hm-cell';
      if(v>0.05){let hex;if(histColor&&cc.fires>0)hex=failHex(Math.round(cc.fails/cc.fires*100));else{const n=logScale?Math.log(1+v)/Math.log(1+(max||1)):v/(max||1);hex=rampHex(1+n*4);}
        el.style.background=hex;el.textContent=v>=10?Math.round(v):(v<1?v.toFixed(1):Math.round(v));const ink=inkFor(hex);el.style.color=ink.color;el.style.textShadow=ink.shadow;
        el.addEventListener('mousemove',e=>showTip(plTip(d,h,adhocVal(d,h),cc),e));el.addEventListener('mouseleave',hideTip);
      }else el.style.background=rampHex(0);
      row.appendChild(el);}
    body.appendChild(row);}
}

// ---- CONCURRENCY ----
function adhocConcDay(d){const pm=new Array(MIN_PER_DAY).fill(0);for(let h=0;h<24;h++){let c=0;for(const s of ADHOC){if(!selectedQueues.has(s.queue))continue;c+=adhocCell(s,d,h)[agg]*Math.max(1,s.dur)/60;}for(let m=h*60;m<h*60+60;m++)pm[m]=c;}return pm;}
function cronConcDay(d){const pm=new Array(MIN_PER_DAY).fill(0);for(const j of cronJobs()){for(const t of jobFires(j)){if(Math.floor(t/MIN_PER_DAY)!==d)continue;const s=t%MIN_PER_DAY,e=Math.min(MIN_PER_DAY,s+Math.max(1,j.dur));for(let m=s;m<e;m++)pm[m]++;}}return pm;}
function renderConc(){
  let wd=0,wp=-1;for(let d=0;d<7;d++){const a=showAdhoc()?adhocConcDay(d):new Array(MIN_PER_DAY).fill(0);const c=showCron()?cronConcDay(d):new Array(MIN_PER_DAY).fill(0);let p=0;for(let m=0;m<MIN_PER_DAY;m++)p=Math.max(p,a[m]+c[m]);if(p>wp){wp=p;wd=d;}}
  const a=showAdhoc()?adhocConcDay(wd):new Array(MIN_PER_DAY).fill(0);const c=showCron()?cronConcDay(wd):new Array(MIN_PER_DAY).fill(0);
  let peak=0,peakAt=0,ap=0;for(let m=0;m<MIN_PER_DAY;m++){const t=a[m]+c[m];if(t>peak){peak=t;peakAt=m;}ap=Math.max(ap,a[m]);}
  document.getElementById('ccPeak').textContent=peak.toFixed(1);document.getElementById('ccCap').textContent=cap;document.getElementById('ccAdhoc').textContent=ap.toFixed(1);
  document.getElementById('ccPeakAt').textContent=`${DAYS[wd]} ${String(Math.floor(peakAt/60)).padStart(2,'0')}:${String(peakAt%60).padStart(2,'0')}`;
  const W=1120,H=300,padL=38,padT=12,padB=26,padR=8,plotW=W-padL-padR,plotH=H-padT-padB,maxY=Math.max(peak,cap)*1.15;
  const x=m=>padL+(m/MIN_PER_DAY)*plotW,y=v=>padT+plotH-(v/maxY)*plotH;const B=5;let bars='';
  for(let m=0;m<MIN_PER_DAY;m+=B){let av=0,cv=0;for(let k=m;k<m+B;k++){av=Math.max(av,a[k]);cv=Math.max(cv,c[k]);}const tot=av+cv;if(tot<=0.01)continue;const bx=x(m),bw=Math.max(1.5,(plotW/MIN_PER_DAY)*B-0.4);
    if(av>0)bars+=`<rect x="${bx.toFixed(1)}" y="${y(av).toFixed(1)}" width="${bw.toFixed(1)}" height="${(padT+plotH-y(av)).toFixed(1)}" fill="var(--text-muted)" opacity="0.45"></rect>`;
    if(cv>0)bars+=`<rect x="${bx.toFixed(1)}" y="${y(av+cv).toFixed(1)}" width="${bw.toFixed(1)}" height="${(y(av)-y(av+cv)).toFixed(1)}" fill="var(--accent)" opacity="0.9"></rect>`;
    if(tot>cap)bars+=`<rect x="${(bx-0.5).toFixed(1)}" y="${(y(tot)-2).toFixed(1)}" width="${(bw+1).toFixed(1)}" height="2" fill="var(--danger)"></rect>`;}
  const capY=y(cap);let grid='';for(let h=0;h<=24;h+=3){const gx=x(h*60);grid+=`<line x1="${gx}" y1="${padT}" x2="${gx}" y2="${padT+plotH}" stroke="var(--grid-line)"></line><text x="${gx}" y="${H-8}" font-size="10" text-anchor="middle">${String(h).padStart(2,'0')}:00</text>`;}
  let yt='';for(let i=0;i<=4;i++){const vv=(maxY/4*i),gy=y(vv);yt+=`<text x="${padL-6}" y="${gy+3}" font-size="10" text-anchor="end">${vv.toFixed(0)}</text><line x1="${padL}" y1="${gy}" x2="${W-padR}" y2="${gy}" stroke="var(--grid-line)" stroke-width="0.6"></line>`;}
  document.getElementById('ccChart').innerHTML=`<svg viewBox="0 0 ${W} ${H}" width="100%" preserveAspectRatio="xMidYMid meet">${yt}${grid}${bars}<line x1="${padL}" y1="${capY}" x2="${W-padR}" y2="${capY}" stroke="var(--accent)" stroke-width="1.5" stroke-dasharray="5 4"></line><text x="${W-padR}" y="${capY-5}" font-size="10" text-anchor="end" style="fill:var(--accent)">capacity ${cap}</text></svg>`;
  document.getElementById('ccLegend').innerHTML=`<span class="key"><span class="swatch" style="background:var(--text-muted);opacity:.45"></span> ad-hoc baseline (${agg})</span><span class="key"><span class="swatch" style="background:var(--accent)"></span> cron</span><span class="key"><span class="swatch" style="background:var(--accent);height:2px"></span> capacity</span><span class="key" style="color:var(--text-muted)">worst day: <b style="color:var(--text)">${DAYS[wd]}</b></span>`;
}

// ---- RECOMMENDATIONS ----
function peakConc(starts,durs){const ev=[];for(let i=0;i<starts.length;i++){ev.push([starts[i],1]);ev.push([starts[i]+Math.max(1,durs[i]),-1]);}ev.sort((a,b)=>a[0]-b[0]||a[1]-b[1]);let cur=0,mx=0;for(const e of ev){cur+=e[1];if(cur>mx)mx=cur;}return mx;}
function buildRecs(){
  const map={};
  for(let d=0;d<7;d++)for(const q of cronQueues()){
    const ivs=[];for(const j of cronJobs()){if(j.queue!==q)continue;for(const t of jobFires(j)){if(Math.floor(t/MIN_PER_DAY)!==d)continue;const s=t%MIN_PER_DAY;ivs.push({s,e:Math.min(MIN_PER_DAY,s+Math.max(1,j.dur)),dur:Math.max(1,j.dur),id:j.id});}}
    if(ivs.length<3)continue;const mc=new Array(MIN_PER_DAY).fill(0);for(const iv of ivs)for(let m=iv.s;m<iv.e;m++)mc[m]++;
    let peak=0,pm=0;for(let m=0;m<MIN_PER_DAY;m++)if(mc[m]>peak){peak=mc[m];pm=m;}if(peak<3)continue;
    const contrib=ivs.filter(iv=>iv.s<=pm&&pm<iv.e);if(contrib.length<3)continue;
    const after=peakConc(contrib.map((_,i)=>Math.round(i*60/contrib.length)),contrib.map(c=>c.dur));
    const key=`${q}|${pm}|${contrib.map(c=>c.id).sort().join(',')}`;
    (map[key]=map[key]||{queue:q,pm,contrib,peak,after,days:new Set()}).days.add(d);
  }
  let recs=Object.values(map).filter(r=>r.after<r.peak);
  recs.forEach(r=>{r.sev=r.peak>cap?'high':'med';r.adhocAt=adhocVal(r.days.values().next().value,Math.floor(r.pm/60));});
  recs.sort((a,b)=>(b.peak-b.after)-(a.peak-a.after));
  return recs;
}
function bestWindow(){const g=cronGrid();let best=null,bv=Infinity;for(let d=0;d<7;d++)for(let h=0;h<24;h++){const v=adhocVal(d,h)+cronCellAll(g,d,h).load*0.3;if(v<bv){bv=v;best={d,h};}}return best;}
function renderRec(){
  const list=document.getElementById('recList');
  if(!showCron()){document.getElementById('recBadge').style.display='none';list.innerHTML=`<div class="j" style="color:var(--text-muted)">Recommendations apply to cron jobs. Enable Cron or Combined.</div>`;return;}
  const recs=buildRecs();document.getElementById('recBadge').textContent=recs.length;document.getElementById('recBadge').style.display=recs.length?'inline-block':'none';
  if(!recs.length){list.innerHTML=`<div class="j" style="color:var(--text-muted)">No cron overlap clusters in the current selection. 🎉</div>`;return;}
  let maxA=0;for(let d=0;d<7;d++)for(let h=0;h<24;h++)maxA=Math.max(maxA,adhocVal(d,h));const best=bestWindow();
  list.innerHTML=recs.map(r=>{const time=`${String(Math.floor(r.pm/60)).padStart(2,'0')}:${String(r.pm%60).padStart(2,'0')}`;const hi=r.adhocAt>maxA*0.4;
    return `<div class="rec ${r.sev==='high'?'high':''}"><div class="rh"><span class="sev ${r.sev}">${r.sev}</span><span class="when"><span style="display:inline-block;width:9px;height:9px;border-radius:50%;background:${QUEUE_COLOR[r.queue]};margin-right:6px"></span>${r.queue} · ${time}</span></div>
      <div class="j" style="color:var(--text)"><b>${r.peak} cron jobs</b> run together; staggering cuts the peak to <span class="delta">~${r.after}</span>.</div>
      ${hi?`<div class="adhoc-note">⚠ Slot also has high on-demand load (~${r.adhocAt.toFixed(1)}/h). Consider ${DAYS[best.d]} ${String(best.h).padStart(2,'0')}:00 — lowest combined load.</div>`:''}
      <div class="acts">${r.contrib.slice(0,4).map(c=>`<button class="btn btn-sm">${c.id}</button>`).join('')}<button class="btn btn-sm btn-accent">Auto-stagger</button></div></div>`;}).join('');
}

// ---- Insights ----
function renderInsights(){
  const best=bestWindow();document.getElementById('insBest').innerHTML=`${DAYS[best.d]} ${String(best.h).padStart(2,'0')}:00 <small>lowest load</small>`;
  let ap=0,at=null;for(let d=0;d<7;d++)for(let h=0;h<24;h++){const v=adhocVal(d,h);if(v>ap){ap=v;at={d,h};}}
  document.getElementById('insAdhoc').innerHTML=at?`${ap.toFixed(1)}/h <small>${DAYS[at.d]} ${String(at.h).padStart(2,'0')}:00</small>`:'—';
  let peak=0,pd=0,pa=0;for(let d=0;d<7;d++){const a=showAdhoc()?adhocConcDay(d):new Array(MIN_PER_DAY).fill(0);const c=showCron()?cronConcDay(d):new Array(MIN_PER_DAY).fill(0);for(let m=0;m<MIN_PER_DAY;m++){const t=a[m]+c[m];if(t>peak){peak=t;pd=d;pa=m;}}}
  document.getElementById('insPeak').innerHTML=`${peak.toFixed(1)} <small>${DAYS[pd]} ${String(Math.floor(pa/60)).padStart(2,'0')}:00 · cap ${cap}</small>`;
  document.getElementById('insPeakCard').classList.toggle('alert',peak>cap);
  const recs=showCron()?buildRecs():[];document.getElementById('insRec').innerHTML=`${recs.length} <small>${recs.length?'click to view':'all clear'}</small>`;
  document.getElementById('insRecCard').classList.toggle('alert',recs.some(r=>r.sev==='high'));
}

// ---- Table ----
function renderTable(){
  const tb=document.getElementById('jobTable');let rows='';
  if(showCron())for(const j of CRON){if(!selectedQueues.has(j.queue)||(hideSub&&j.sub))continue;const load=Math.round(jobFires(j).length*(metric==='load'?j.dur:1)/7);
    const valCell=source==='historical'?`<td style="color:${j.fail>=0.2?'var(--danger)':j.fail>=0.08?'var(--warn)':'var(--ok)'}">${Math.round(j.fail*100)}%</td>`:`<td>${load}</td>`;
    rows+=`<tr><td>${j.id}${j.sub?'<span class="pill">*/n</span>':''}</td><td><code>${j.cron}</code></td><td><span class="q" style="background:${QUEUE_COLOR[j.queue]}">${j.queue}</span></td><td><span class="clstag">cron</span></td><td>${fmtTz(j.tz)}</td><td>${j.dur}m</td>${valCell}</tr>`;}
  if(showAdhoc())for(const s of ADHOC){if(!selectedQueues.has(s.queue))continue;let pd=0;for(let d=0;d<7;d++)for(let h=0;h<24;h++)pd+=adhocCell(s,d,h)[agg];pd=Math.round(pd/7);
    rows+=`<tr><td>${s.id}</td><td><span style="color:var(--text-muted)">on-demand</span></td><td><span class="q" style="background:${QUEUE_COLOR[s.queue]}">${s.queue}</span></td><td><span class="clstag">ad-hoc</span></td><td>—</td><td>${s.dur}m</td><td>~${pd} <span style="color:var(--text-muted)">(${agg})</span></td></tr>`;}
  tb.innerHTML=rows;
  document.getElementById('colMetric').textContent=source==='historical'?'Failure / load':(metric==='load'?'Wk-min/day':'Fires/day');
}
function fmtTz(off){const s=off<0?'-':'+';const a=Math.abs(off);return `${s}${String(Math.floor(a/60)).padStart(2,'0')}:${String(a%60).padStart(2,'0')}`;}

// ---- CSV ----
function exportCSV(){
  const g=cronGrid();let csv='day,'+Array.from({length:24},(_,h)=>String(h).padStart(2,'0')).join(',')+'\n';
  for(const d of rowOrder()){const r=[DAYS[d]];for(let h=0;h<24;h++){const v=showCron()?cronCellAll(g,d,h).val:0;const a=showAdhoc()?adhocVal(d,h):0;r.push((v+a).toFixed(1));}csv+=r.join(',')+'\n';}
  const blob=new Blob([csv],{type:'text/csv'});const link=document.createElement('a');link.href=URL.createObjectURL(blob);link.download=`heatmap-v4-${cls}-${metric}.csv`;link.click();URL.revokeObjectURL(link.href);
}

// ---- chrome ----
function buildChips(){const c=document.getElementById('queueChips');c.innerHTML='';
  for(const q of ALL_QUEUES){const isAd=ADHOC_QUEUES.includes(q),cr=CRON_QUEUES.includes(q);const el=document.createElement('span');el.className='chip'+(selectedQueues.has(q)?'':' off');el.innerHTML=`<span class="dot" style="background:${QUEUE_COLOR[q]}"></span>${q}<span class="cls">${cr&&isAd?'both':isAd?'ad-hoc':'cron'}</span>`;
    el.addEventListener('click',()=>{if(selectedQueues.has(q))selectedQueues.delete(q);else selectedQueues.add(q);if(!selectedQueues.size)selectedQueues.add(q);buildChips();renderAll();});c.appendChild(el);}}
function syncChrome(){
  const demand=showAdhoc();
  document.getElementById('sourceSeg').style.display=showCron()?'inline-flex':'none';
  document.getElementById('aggWrap').style.display=demand?'inline-flex':'none';
  document.getElementById('lookWrap').style.display=demand?'inline-flex':'none';
  document.getElementById('retentionNote').style.display=demand?'block':'none';
  document.getElementById('dayWrap').style.display=view==='qh'?'inline-flex':'none';
  document.getElementById('colorWrap').style.display=(view==='cal'&&showCron()&&source==='historical')?'inline-flex':'none';
}
function renderAll(){
  renderInsights();
  if(view==='planner')renderPlanner();else if(view==='punch')renderPunch();else if(view==='qh')renderQH();else if(view==='multi')renderMulti();else if(view==='cal')renderCal();else if(view==='conc')renderConc();else renderRec();
  renderTable();syncChrome();
}
document.getElementById('classSeg').addEventListener('click',e=>{const b=e.target.closest('button');if(!b)return;cls=b.dataset.class;[...document.querySelectorAll('#classSeg button')].forEach(x=>x.classList.toggle('active',x===b));renderAll();});
document.getElementById('sourceSeg').addEventListener('click',e=>{const b=e.target.closest('button');if(!b)return;source=b.dataset.source;[...document.querySelectorAll('#sourceSeg button')].forEach(x=>x.classList.toggle('active',x===b));renderAll();});
document.getElementById('viewSeg').addEventListener('click',e=>{const b=e.target.closest('button');if(!b)return;view=b.dataset.view;[...document.querySelectorAll('#viewSeg button')].forEach(x=>x.classList.toggle('active',x===b));document.querySelectorAll('.view').forEach(v=>v.classList.remove('active'));document.getElementById('view-'+view).classList.add('active');renderAll();});
document.getElementById('windowSel').addEventListener('change',e=>{windowMode=e.target.value;renderAll();});
document.getElementById('tzSel').addEventListener('change',e=>{viewTz=+e.target.value;renderAll();});
document.getElementById('metricSel').addEventListener('change',e=>{metric=e.target.value;renderAll();});
document.getElementById('hideSub').addEventListener('change',e=>{hideSub=e.target.checked;renderAll();});
document.getElementById('logScale').addEventListener('change',e=>{logScale=e.target.checked;renderAll();});
document.getElementById('aggSel').addEventListener('change',e=>{agg=e.target.value;renderAll();});
document.getElementById('lookSel').addEventListener('change',e=>{lookback=e.target.value;renderAll();});
document.getElementById('daySel').addEventListener('change',e=>{selDay=e.target.value==='-1'?-1:DAYS.indexOf(e.target.value);renderAll();});
document.getElementById('colorSel').addEventListener('change',e=>{colorBy=e.target.value;renderAll();});
document.getElementById('capInput').addEventListener('input',e=>{cap=Math.max(1,parseInt(e.target.value)||12);renderAll();});
document.getElementById('qAll').addEventListener('click',()=>{selectedQueues=new Set(ALL_QUEUES);buildChips();renderAll();});
document.getElementById('themeBtn').addEventListener('click',()=>{const h=document.documentElement;h.dataset.theme=h.dataset.theme==='dark'?'light':'dark';renderAll();});
document.getElementById('exportBtn').addEventListener('click',exportCSV);
document.getElementById('insRecCard').addEventListener('click',()=>document.querySelector('#viewSeg button[data-view="rec"]').click());
document.getElementById('insPeakCard').addEventListener('click',()=>document.querySelector('#viewSeg button[data-view="conc"]').click());
window.addEventListener('resize',()=>{if(view==='conc')renderConc();});
try{const tz=Intl.DateTimeFormat().resolvedOptions().timeZone;document.getElementById('tzline').textContent=`Plan cron around real on-demand load · demo "now" = Thu 14:00 · ${tz}`;}catch{}
buildChips();renderAll();
