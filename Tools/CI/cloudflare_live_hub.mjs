// Aaris Unity: free Cloudflare Worker read-only camera hub.
// No KV/R2, user tokens, premium services or executable commands.
const REPO="sundamunda289-art/Unity-Game-Aris";
const ISSUE=77;
const ISSUE_URL="https://github.com/"+REPO+"/issues/"+ISSUE;
const RUN_URL="https://github.com/"+REPO+"/actions/workflows/aaris-live-unity.yml";
const PHOTO_RE=/https:\/\/github\.com\/sundamunda289-art\/Unity-Game-Aris\/releases\/download\/live-photos\/live-camera-\d+\.png/;
const AI_REPORT_RE=/https:\/\/github\.com\/sundamunda289-art\/Unity-Game-Aris\/releases\/download\/live-photos\/ai-report-\d+\.json/;
const AI_BLUEPRINT_RE=/https:\/\/github\.com\/sundamunda289-art\/Unity-Game-Aris\/releases\/download\/live-photos\/ai-blueprint-\d+\.ply/;
const AI_PHOTO_RE=/https:\/\/github\.com\/sundamunda289-art\/Unity-Game-Aris\/releases\/download\/live-photos\/ai-photo-\d+\.png/;
const TUNNEL_RE=/^AARIS_LIVE_DESKTOP run_id=(\d+) session_minutes=(\d+) url=(https:\/\/[a-z0-9-]+\.trycloudflare\.com)$/;
function json(data,status=200){return new Response(JSON.stringify(data),{status,headers:{"Content-Type":"application/json","Cache-Control":"public, max-age=15","X-Content-Type-Options":"nosniff"}})}
async function snapshots(){
  const since=new Date(Date.now()-8*3600000).toISOString();
  const uri="https://api.github.com/repos/"+REPO+"/issues/"+ISSUE+"/comments?per_page=100&since="+encodeURIComponent(since);
  const res=await fetch(uri,{headers:{Accept:"application/vnd.github+json","User-Agent":"aaris-unity-free-hub"},cf:{cacheEverything:true,cacheTtl:45}});
  if(!res.ok)throw Error("GitHub API unavailable");
  let comments=await res.json(),lastPhoto=null,active=null,latestCameraStatus='No camera result yet',latestLicenseStatus='Unknown',latestAiStatus='No AI workbench result yet',lastAiReport=null,lastAiBlueprint=null;
  for(const item of comments){
    const body=String(item.body||"").trim();
    const tunnel=body.match(TUNNEL_RE);
    if(tunnel&&item.user?.login==="github-actions[bot]"){
      const mins=Math.min(300,Number(tunnel[2])),at=Date.parse(item.created_at);
      if(Number.isFinite(at)&&mins>0&&Date.now()<at+(mins+2)*60000)
        active={url:tunnel[3],expires_at:new Date(at+mins*60000).toISOString()};
    }
    if(body.startsWith("Camera request #")&&item.user?.login==="github-actions[bot]"){
      if(body.includes("timed out waiting for a licensed Unity Editor"))latestCameraStatus="Camera received; Unity Editor needs a valid license.";
      else if(body.includes("real Unity 1280"))latestCameraStatus="Camera rendered and saved an authentic Unity PNG.";
      else latestCameraStatus=body.slice(0,170);
    }
    if(/^AARIS_LICENSE_STATUS [a-z_]+$/.test(body)&&item.user?.login==="github-actions[bot]"){
      latestLicenseStatus=body.slice("AARIS_LICENSE_STATUS ".length);
    }
    if(body.startsWith("AI request #")&&item.user?.login==="github-actions[bot]"){
      latestAiStatus=body.slice(0,180);
      const report=body.match(AI_REPORT_RE),blueprint=body.match(AI_BLUEPRINT_RE),aiPhoto=body.match(AI_PHOTO_RE);
      if(report)lastAiReport=report[0];
      if(blueprint)lastAiBlueprint=blueprint[0];
      if(aiPhoto)lastPhoto=aiPhoto[0];
    }
    const photo=body.match(PHOTO_RE);
    if(photo&&item.user?.login==="github-actions[bot]")lastPhoto=photo[0];
  }
  return {live_url:active?.url||null,expires_at:active?.expires_at||null,
    latest_photo:lastPhoto,latest_camera_status:latestCameraStatus,latest_license_status:latestLicenseStatus,latest_ai_status:latestAiStatus,latest_ai_report:lastAiReport,latest_ai_blueprint:lastAiBlueprint,issue_url:ISSUE_URL,workflow_url:RUN_URL,
    authorization:"email PIN required for live desktop",
    note:"Only actual licensed Unity PNGs count as 3D captures. A Cloudflare page is not Unity itself."};
}
const PAGE=[
'<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">',
'<title>Aaris Unity Live Hub</title><style>body{font:16px system-ui;background:#0c1f29;color:#effaff;padding:20px;max-width:700px}a{color:#8aead7}',
'.box{padding:18px;border:1px solid #466575;border-radius:12px;margin:20px 0}p{line-height:1.55}</style>',
'<h2>Aaris Unity Live Hub</h2><p>Cloudflare Workers Free. Camera control uses authorized GitHub Issue comments; the remote desktop uses a protected Cloudflare Tunnel.</p>',
'<div class="box"><b>Desktop</b><p id="desktop">Checking active session...</p></div>',
'<div class="box"><b>Latest real Unity screenshot</b><p id="photo">Checking for rendered PNG...</p></div>',
'<div class="box"><b>AI workbench geometry and dimensions</b><p id="ai">Waiting for first AI scan...</p></div>',
'<div class="box"><b>Camera/License Status</b><p id="camera">Checking...</p></div>',
'<p><a href="https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77">Camera command issue</a> &middot; ',
'<a href="https://github.com/sundamunda289-art/Unity-Game-Aris/actions/workflows/aaris-live-unity.yml">Start a 15/45/90/180/300 min session</a></p>',
'<p>ChatGPT can inspect uploaded screenshots through GitHub, but cannot watch the live VNC stream continuously.</p>',
'<script>async function refresh(){try{const r=await fetch("/status");const v=await r.json();',
'for(const k of ["desktop","photo","camera","ai"]){document.getElementById(k).replaceChildren()}',
'const d=document.getElementById("desktop"),p=document.getElementById("photo");document.getElementById("camera").textContent="License: "+(v.latest_license_status||"Unknown")+" | Camera: "+(v.latest_camera_status||"No update");',
'if(v.live_url){let a=document.createElement("a");a.href=v.live_url;a.textContent="Open live desktop (PIN required)";d.appendChild(a)}',
'else d.textContent="No active desktop session";',
'if(v.latest_photo){let a=document.createElement("a");a.href=v.latest_photo;a.textContent="Open latest Unity PNG";p.appendChild(a)}',
'else p.textContent="No verified Unity photo yet";',
'const ai=document.getElementById("ai");ai.textContent=v.latest_ai_status||"No report";',
'for(const [url,label] of [[v.latest_ai_report,"JSON dimensions/report"],[v.latest_ai_blueprint,"3D point cloud PLY"]])if(url){ai.appendChild(document.createElement("br"));let a=document.createElement("a");a.href=url;a.textContent=label;ai.appendChild(a)}',
'}catch(e){document.getElementById("desktop").textContent="GitHub lookup unavailable"}}refresh();setInterval(refresh,30000);</script></html>'
].join("");
export default{async fetch(req){
  const path=new URL(req.url).pathname;
  if(req.method!=="GET"&&req.method!=="HEAD")return new Response("Read only",{status:405});
  if(path==="/"||path==="/index.html")return new Response(req.method==="HEAD"?null:PAGE,
    {headers:{"Content-Type":"text/html; charset=utf-8","X-Content-Type-Options":"nosniff",
      "Content-Security-Policy":"default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'"}});
  if(path==="/status"||path==="/photo"){
    try{let v=await snapshots();if(path==="/status")return json(v);
      if(!v.latest_photo)return json({error:"No licensed Unity PNG available"},404);
      return Response.redirect(v.latest_photo,302);
    }catch(e){return json({error:"GitHub status temporarily unavailable"},503)}
  }
  return new Response("Not found",{status:404});
}};
