// Browser UI is deliberately read-only. The bookmark key is URL fragment only.
export function renderWatchPage(nonce) {
return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="referrer" content="no-referrer"><title>Aaris Private Live Monitor</title>
<style>
:root{color-scheme:dark;--bg:#0a1020;--panel:#142036;--line:#30405d;--fg:#eff5ff;--sub:#a5b9d4;--green:#48daa6}
*{box-sizing:border-box}body{margin:0;background:radial-gradient(circle at 15% 0,#213451,transparent 60%),var(--bg);color:var(--fg);font:14px system-ui,sans-serif}
main{width:min(1050px,100%);margin:auto;padding:15px 12px 40px}header{display:flex;justify-content:space-between;align-items:center;margin-bottom:15px;gap:10px}
h1{font-size:21px;margin:0}small,.sub{color:var(--sub);font-size:12px}.panel{background:var(--panel);border:1px solid var(--line);border-radius:15px;overflow:hidden;margin-bottom:13px}
.bar{display:flex;gap:8px;align-items:center;flex-wrap:wrap;padding:11px;border-bottom:1px solid var(--line)}.grow{flex:1}
select,input,button{color:var(--fg);background:#1e314f;border:1px solid #425773;padding:8px 10px;border-radius:8px;font:inherit}
button{cursor:pointer}button:disabled{opacity:.6}button.primary{background:#176251;border-color:#287d69;font-weight:bold}
#machine{flex:1 1 200px;min-width:170px;max-width:410px}#screenBox{background:#020407;min-height:220px;display:flex;justify-content:center;align-items:center;position:relative}
#screen{display:block;max-width:100%;max-height:70vh;object-fit:contain}
#noFrame{color:var(--sub);padding:35px;text-align:center}
#chip{position:absolute;left:10px;top:10px;background:#071424ec;border:1px solid #485a70;padding:5px 9px;border-radius:7px;font-size:11px;max-width:94%;overflow-wrap:anywhere}
#meta{display:flex;justify-content:space-between;gap:10px;flex-wrap:wrap;padding:10px 12px;color:var(--sub);font-size:12px}
#logs{height:285px;overflow:auto;padding:10px 13px;font:12px/1.55 ui-monospace,SFMono-Regular,Consolas,monospace}
.job{padding:8px 0;border-bottom:1px solid #273652}.command{color:#83b8ff;white-space:pre-wrap;overflow-wrap:anywhere}.result{color:#c5e7d0;white-space:pre-wrap;overflow-wrap:anywhere}.detail{color:var(--sub);font-size:11px}
#locked{max-width:480px;margin:10vh auto;padding:25px}#locked input{width:100%;margin:14px 0}.signal{display:inline-block;width:9px;height:9px;border-radius:50%;background:#ed9e6b;margin-right:7px}.signal.online{background:var(--green)}
footer{color:var(--sub);font-size:11px;line-height:1.6} [hidden]{display:none!important}
@media(max-width:600px){main{padding:10px 8px 30px}h1{font-size:17px}.bar{padding:8px;gap:6px}#logs{height:230px}}
</style></head><body><main>
<header><div><h1>Aaris <span style="color:#48daa6">Live Monitor</span></h1><small>Private Codespace desktop + terminal</small></div><span class="sub"><span id="signal" class="signal"></span><span id="connection">Checking…</span></span></header>
<section id="locked" class="panel" hidden><h2>🔐 Unlock your private monitor</h2><p class="sub">Use your saved bookmark, or enter the private key. The key is not sent in the URL query string.</p>
<input id="access" type="password" autocomplete="off" placeholder="Private 64-character key" maxlength="64"><button class="primary" id="unlock">Unlock</button><p id="lockMessage" class="sub"></p></section>
<div id="viewer" hidden>
<section class="panel">
<div class="bar"><select id="machine" aria-label="Codespace"><option value="">Auto: newest connected Codespace</option></select>
<select id="quality" aria-label="Screenshot quality"><option value="fast">Fast preview</option><option value="full">Full quality</option></select>
<select id="interval" aria-label="Refresh interval"><option value="10000">10 sec (recommended)</option><option value="5000">5 sec</option><option value="3000">3 sec</option></select>
<span class="grow"></span><button id="pause">Pause</button><button id="snap" class="primary">📸 Snap now</button></div>
<div id="screenBox"><div id="noFrame">Waiting for the next screenshot…</div><img id="screen" alt="Latest Codespace desktop" hidden><span id="chip">No live frame yet</span></div>
<div id="meta"><span id="size">Waiting for frame</span><span id="when">No capture yet</span></div></section>
<section class="panel"><div class="bar"><strong>Latest terminal commands</strong><span class="grow"></span><span id="count" class="sub">0 jobs</span></div><div id="logs"><span class="sub">Waiting for terminal activity…</span></div></section>
<footer>View-only. Captures are requested only while this page is visible and not paused; no public remote shell. The screen is on-demand, not a continuous WebRTC video. Never share your private bookmark.</footer>
</div></main>
<script nonce="__NONCE__">
(function(){
"use strict";
const el=id=>document.getElementById(id);
let key=new URLSearchParams(location.hash.substring(1)).get("key")||"";
let machine="", frameId="", frameMachine="", oldUrl="", busy=false,paused=false,last=0;
function valid(s){return /^[0-9a-f]{64}$/.test(s);}
function lock(message){el("locked").hidden=false;el("viewer").hidden=true;el("connection").textContent="Locked";if(message)el("lockMessage").textContent=message;}
function unlock(){el("locked").hidden=true;el("viewer").hidden=false;}
async function req(path,options){
 const opts=Object.assign({cache:"no-store",credentials:"omit"},options||{});
 opts.headers=Object.assign({"Authorization":"Bearer "+key},opts.headers||{});
 const res=await fetch(path,opts);
 if(res.status===401){lock("Invalid or missing private key.");throw Error("unauthorized");}
 if(!res.ok)throw Error("HTTP "+res.status);
 return res;
}
function renderJobs(jobs){
 const host=el("logs");host.replaceChildren();el("count").textContent=jobs.length+" jobs";
 if(!jobs.length){host.textContent="No recent commands on this machine.";return;}
 for(const j of jobs){
 const row=document.createElement("div");row.className="job";
 const cmd=document.createElement("div");cmd.className="command";cmd.textContent="$ "+(j.command||"command");
 const meta=document.createElement("div");meta.className="detail";meta.textContent=(j.status||"unknown")+(j.exit_code!=null?" • exit "+j.exit_code:"")+(j.created_at?" • "+new Date(j.created_at*1000).toLocaleTimeString():"");
 row.append(cmd,meta);
 if(j.output){const t=document.createElement("div");t.className="result";t.textContent=j.output;row.append(t);}
 host.append(row);
 }
}
function renderMachines(items){
 const sel=el("machine");
 const desired=[""].concat(items.map(x=>x.name));
 if(Array.from(sel.options).map(x=>x.value).join("|")!==desired.join("|")){
 sel.replaceChildren();const opt=document.createElement("option");opt.value="";opt.textContent="Auto: newest connected Codespace";sel.append(opt);
 for(const m of items){const o=document.createElement("option");o.value=m.name;o.textContent=(m.live?"🟢 ":"○ ")+m.name;sel.append(o);}
 }
 sel.value=desired.includes(machine)?machine:"";
 if(!desired.includes(machine))machine="";
}
async function showFrame(m,id){
 if(!m||!id||(frameId===id&&frameMachine===m))return;
 const u="/watch/frame?machine="+encodeURIComponent(m)+"&id="+encodeURIComponent(id);
 const response=await req(u);const blob=await response.blob();
 if(blob.type!=="image/jpeg"||blob.size<100||blob.size>145000)throw Error("bad frame");
 const link=URL.createObjectURL(blob),previous=oldUrl;
 el("screen").src=link;el("screen").hidden=false;el("noFrame").hidden=true;
 oldUrl=link;frameId=id;frameMachine=m;
 if(previous)URL.revokeObjectURL(previous);
}
async function poll(force){
 if(document.hidden||paused||busy||!valid(key))return;
 const rate=Number(el("interval").value)||10000;
 if(!force && Date.now()-last<rate-200)return;
 busy=true;last=Date.now();
 try{
 const args=new URLSearchParams({capture:"1",quality:el("quality").value});
 if(machine)args.set("machine",machine);
 const r=await req("/watch/api?"+args.toString());
 const d=await r.json();
 renderMachines(d.machines||[]);
 const online=!!d.live;el("signal").classList.toggle("online",online);
 el("connection").textContent=online?"Connected":"Offline";
 el("chip").textContent=d.machine?(online?"● LIVE: ":"○ OFFLINE: ")+d.machine:"No connected Codespace";
 el("size").textContent=d.frame?(d.frame.width+"×"+d.frame.height+" • "+d.frame.mode):"No screenshot yet";
 el("when").textContent=d.frame?.updated_at?("Captured "+new Date(d.frame.updated_at*1000).toLocaleTimeString()):"Waiting for first capture";
 renderJobs(d.jobs||[]);
 if(d.frame?.id&&d.machine)await showFrame(d.machine,d.frame.id);
 }catch(e){if(e.message!=="unauthorized")el("connection").textContent="Network error";}
 finally{busy=false;}
}
async function snap(){
 if(document.hidden||!valid(key))return;
 el("snap").disabled=true;
 try{await req("/watch/capture",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({machine:machine||null,quality:el("quality").value})});
 last=0;setTimeout(()=>poll(true),1200);
 }catch(e){el("connection").textContent="Capture unavailable";}
 finally{el("snap").disabled=false;}
}
el("unlock").addEventListener("click",()=>{
 const x=el("access").value.trim().toLowerCase();
 if(!valid(x)){el("lockMessage").textContent="Enter your private 64-character key.";return;}
 key=x;location.hash="key="+key;el("access").value="";unlock();poll(true);
});
el("snap").addEventListener("click",snap);
el("pause").addEventListener("click",()=>{paused=!paused;el("pause").textContent=paused?"Resume":"Pause";if(!paused){last=0;poll(true);}});
el("quality").addEventListener("change",()=>{last=0;poll(true)});
el("interval").addEventListener("change",()=>{last=0;poll(true)});
el("machine").addEventListener("change",()=>{machine=el("machine").value;frameId="";frameMachine="";el("screen").hidden=true;el("noFrame").hidden=false;last=0;poll(true);});
document.addEventListener("visibilitychange",()=>{if(!document.hidden){last=0;poll(true)}});
setInterval(()=>poll(false),1000);
if(valid(key)){unlock();poll(true);}else lock("");
})();
</script></body></html>`.replace("__NONCE__",nonce);
}
