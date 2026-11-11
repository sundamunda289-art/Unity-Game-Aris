import { renderWatchPage } from "./watch_page.mjs";
// Aaris Turbo Broker. No public command submission endpoint.
// Only owner-authorized Cloudflare Durable Object SQL API may enqueue commands.
const REPO = "sundamunda289-art/Unity-Game-Aris";
const REPO_ID = "1443109769";
const ACTOR_ID = "353065137";
const WORKFLOW = REPO + "/.github/workflows/aaris-turbo-bootstrap.yml@refs/heads/main";
const AUDIENCE = "aaris-turbo-bootstrap-v1";
const json = (value, status = 200) => new Response(JSON.stringify(value), {
  status, headers: {"content-type": "application/json; charset=utf-8", "cache-control": "no-store"}
});
function decodeB64(s) {
  const b = atob(s.replace(/-/g, "+").replace(/_/g, "/"));
  return Uint8Array.from(b, c => c.charCodeAt(0));
}
function parseJWTPart(s) {
  return JSON.parse(new TextDecoder().decode(decodeB64(s)));
}
async function verifyActionsJWT(token) {
  const parts = token.split(".");
  if (parts.length !== 3) throw Error("token_parts");
  const header = parseJWTPart(parts[0]), claims = parseJWTPart(parts[1]);
  const now = Math.floor(Date.now() / 1000);
  if (header.alg !== "RS256" || !header.kid ||
      claims.iss !== "https://token.actions.githubusercontent.com" ||
      claims.aud !== AUDIENCE ||
      String(claims.repository_id) !== REPO_ID ||
      String(claims.actor_id) !== ACTOR_ID ||
      claims.repository !== REPO ||
      claims.ref !== "refs/heads/main" ||
      claims.workflow_ref !== WORKFLOW ||
      !["issue_comment", "workflow_dispatch"].includes(claims.event_name) ||
      !Number.isFinite(claims.exp) || claims.exp <= now || claims.exp > now + 1800 ||
      !Number.isFinite(claims.iat) || claims.iat > now + 60) {
    throw Error("claims");
  }
  const discovery = await fetch("https://token.actions.githubusercontent.com/.well-known/openid-configuration");
  if (!discovery.ok) throw Error("issuer_discovery");
  const config = await discovery.json();
  if (!config.jwks_uri || !config.jwks_uri.startsWith("https://token.actions.githubusercontent.com/")) throw Error("issuer_jwks");
  const keyResponse = await fetch(config.jwks_uri);
  if (!keyResponse.ok) throw Error("jwks");
  const jwks = await keyResponse.json();
  const jwk = (jwks.keys || []).find(k => k.kid === header.kid && k.kty === "RSA");
  if (!jwk) throw Error("unknown_kid");
  const key = await crypto.subtle.importKey("jwk", jwk, {name: "RSASSA-PKCS1-v1_5", hash: "SHA-256"}, false, ["verify"]);
  const message = new TextEncoder().encode(parts[0] + "." + parts[1]);
  if (!await crypto.subtle.verify("RSASSA-PKCS1-v1_5", key, decodeB64(parts[2]), message)) throw Error("signature");
  return claims;
}
function validMachine(name) {
  return typeof name === "string" && /^[a-zA-Z0-9][a-zA-Z0-9-]{2,119}$/.test(name);
}
async function sha256(s) {
  const bytes = new TextEncoder().encode(s);
  return Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)),
    b => b.toString(16).padStart(2, "0")).join("");
}

function constantTimeEqual(a,b) {
  if (typeof a !== "string" || typeof b !== "string" || a.length !== b.length) return false;
  let diff=0;for(let i=0;i<a.length;i++)diff |= a.charCodeAt(i)^b.charCodeAt(i);
  return diff===0;
}
function redactViewerText(value) {
  let s=String(value||"").slice(0,1800);
  s=s.replace(/github_pat_[A-Za-z0-9_]+|ghp_[A-Za-z0-9_]+|sk-[A-Za-z0-9_-]{12,}/g,"[redacted]");
  s=s.replace(/(Bearer\s+)[A-Za-z0-9._-]+/gi,"$1[redacted]");
  s=s.replace(/((?:token|password|secret|api_key)\s*[=:]\s*)[^\s"'&]+/gi,"$1[redacted]");
  return s;
}
function viewerCommandLabel(argvJson,id) {
  if (id.startsWith("watch_")) return "GUI • watch snapshot";
  try {
    const a=JSON.parse(argvJson);
    if(!Array.isArray(a))return "command";
    if(a.some(x=>typeof x==="string" && (x.includes("gui.py")||x.includes("gui_agent.py"))))
      return "GUI • screenshot or mouse/keyboard";
    return redactViewerText(a.map(x=>String(x).slice(0,220)).join(" ")).slice(0,300);
  } catch (_) {return "command";}
}
export class TurboMachine {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    const sql = ctx.storage.sql;
    sql.exec("CREATE TABLE IF NOT EXISTS config (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
    sql.exec("CREATE TABLE IF NOT EXISTS machines (name TEXT PRIMARY KEY, last_seen INTEGER NOT NULL, stage TEXT NOT NULL)");
    sql.exec("CREATE TABLE IF NOT EXISTS commands (id TEXT PRIMARY KEY, argv_json TEXT NOT NULL, cwd TEXT NOT NULL DEFAULT '/workspaces', max_seconds INTEGER NOT NULL DEFAULT 120, expires_at INTEGER NOT NULL, status TEXT NOT NULL DEFAULT 'queued', created_at INTEGER NOT NULL DEFAULT (unixepoch()), started_at INTEGER, finished_at INTEGER, exit_code INTEGER, error TEXT)");
    sql.exec("CREATE TABLE IF NOT EXISTS chunks (command_id TEXT NOT NULL, seq INTEGER NOT NULL, kind TEXT NOT NULL, text TEXT NOT NULL, ts INTEGER NOT NULL DEFAULT (unixepoch()), PRIMARY KEY (command_id, seq))");
    sql.exec("CREATE INDEX IF NOT EXISTS chunks_command ON chunks (command_id, seq)");
    sql.exec("CREATE TABLE IF NOT EXISTS viewer_frame (id INTEGER PRIMARY KEY, command_id TEXT NOT NULL, base64 TEXT NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL, mode TEXT NOT NULL, updated_at INTEGER NOT NULL)");
    // Each polling request must read only a bounded number of indexed command rows.
    // Old watch captures can outnumber ordinary commands by thousands on the Free tier.
    sql.exec("CREATE INDEX IF NOT EXISTS commands_recent ON commands(created_at DESC,id DESC)");
    sql.exec("CREATE INDEX IF NOT EXISTS commands_status_created ON commands(status,created_at,id)");
    sql.exec("CREATE INDEX IF NOT EXISTS commands_watch_pending ON commands(status,expires_at,started_at) WHERE id GLOB 'watch_*'");
    sql.exec("CREATE INDEX IF NOT EXISTS commands_nonwatch_recent ON commands(created_at DESC,id DESC) WHERE id NOT GLOB 'watch_*'");
  }
  async fetch(req) {
    const path = new URL(req.url).pathname;
    if (path === "/internal/grant" && req.method === "POST") {
      const data = await req.json();
      if (typeof data.token !== "string" || data.token.length !== 64) return json({error:"token"}, 400);
      this.ctx.storage.sql.exec("INSERT INTO config(key,value) VALUES('token_sha256',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", await sha256(data.token));
      this.ctx.storage.sql.exec("INSERT INTO config(key,value) VALUES('last_seen',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", String(Math.floor(Date.now()/1000)));
      this.ctx.storage.sql.exec("INSERT INTO config(key,value) VALUES('token_expires',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", String(Math.floor(Date.now()/1000)+7*86400));
      return json({ok:true});
    }
    if (path === "/internal/register" && req.method === "POST") {
      const data = await req.json();
      if (!validMachine(data.name)) return json({error:"name"}, 400);
      this.ctx.storage.sql.exec("INSERT INTO machines(name,last_seen,stage) VALUES(?,unixepoch(),?) ON CONFLICT(name) DO UPDATE SET last_seen=unixepoch(),stage=excluded.stage", data.name, data.stage === "connected" ? "connected" : "bootstrapped");
      return json({ok:true});
    }

    // Internal Durable Object routes are only reached through the authenticated Worker.
    if (path === "/internal/watch/registry" && req.method === "GET") {
      const machines=Array.from(this.ctx.storage.sql.exec(
        "SELECT name,stage,last_seen FROM machines ORDER BY last_seen DESC LIMIT 30"));
      return json({machines});
    }
    if (path === "/internal/watch/state" && req.method === "GET") {
      const sql=this.ctx.storage.sql;
      const now=Math.floor(Date.now()/1000);
      const heartbeat=Array.from(sql.exec("SELECT value FROM config WHERE key='last_seen'"));
      const last=heartbeat.length?Number(heartbeat[0].value):0;
      const expires=Array.from(sql.exec("SELECT value FROM config WHERE key='token_expires'"));
      const live=last>0 && now-last<=90 && expires.length>0 && Number(expires[0].value)>now;
      const frames=Array.from(sql.exec("SELECT command_id,width,height,mode,updated_at FROM viewer_frame WHERE id=1"));
      const recent=Array.from(sql.exec(
        "SELECT id,argv_json,status,exit_code,created_at FROM commands WHERE id NOT GLOB 'watch_*' ORDER BY created_at DESC,id DESC LIMIT 20"));
      const outputs=Array.from(sql.exec(
        "SELECT c.id,ch.kind,substr(ch.text,1,900) AS content FROM chunks ch JOIN commands c ON c.id=ch.command_id WHERE c.id IN (SELECT id FROM commands WHERE id NOT GLOB 'watch_*' ORDER BY created_at DESC,id DESC LIMIT 20) AND instr(c.argv_json,'gui.py')=0 AND instr(c.argv_json,'gui_agent.py')=0 AND c.id NOT GLOB 'watch_*' AND ch.seq<8 AND length(ch.text)<1200 ORDER BY c.created_at DESC,ch.seq DESC LIMIT 65"));
      const logMap=new Map();
      for(const r of outputs.reverse()){
        const prev=logMap.get(r.id)||"";
        if(prev.length<1800)logMap.set(r.id,(prev+(r.kind==="stderr"?"[stderr] ":"")+r.content).slice(-1800));
      }
      return json({live,heartbeat_age:last?now-last:null,frame:frames.length?
        {id:frames[0].command_id,width:frames[0].width,height:frames[0].height,
          updated_at:frames[0].updated_at,mode:frames[0].mode}:null,
        jobs:recent.map(r=>({id:r.id,command:viewerCommandLabel(r.argv_json,r.id),
          status:r.status,exit_code:r.exit_code,created_at:r.created_at,
          output:redactViewerText(logMap.get(r.id)||"").slice(-1500)}))});
    }
    if (path === "/internal/watch/frame" && req.method === "GET") {
      const id=new URL(req.url).searchParams.get("id");
      const frames=Array.from(this.ctx.storage.sql.exec(
        "SELECT command_id,base64 FROM viewer_frame WHERE id=1"));
      if(!frames.length || id!==frames[0].command_id)return json({error:"not_found"},404);
      if(frames[0].base64.length>195000)return json({error:"invalid_frame"},500);
      return new Response(decodeB64(frames[0].base64),{headers:{
        "content-type":"image/jpeg","cache-control":"private, no-store",
        "x-content-type-options":"nosniff"}});
    }
    if (path === "/internal/watch/capture" && req.method === "POST") {
      const d=await req.json();
      const sql=this.ctx.storage.sql;
      const mode=d.quality==="full"?"full":"fast";
      const now=Math.floor(Date.now()/1000);
      const seen=Array.from(sql.exec("SELECT value FROM config WHERE key='last_seen'"));
      const last=seen.length?Number(seen[0].value):0;
      if (!last || now-last>90)return json({scheduled:false,reason:"offline"});
      const pending=Array.from(sql.exec(
        "SELECT id FROM commands WHERE id GLOB 'watch_*' AND ((status='queued' AND expires_at>unixepoch()) OR (status='running' AND started_at>unixepoch()-120)) LIMIT 1"));
      if(pending.length)return json({scheduled:false,reason:"already_pending",id:pending[0].id});
      const requested=Array.from(sql.exec("SELECT value FROM config WHERE key='watch_requested'"));
      const since=requested.length?now-Number(requested[0].value):999;
      if(since<3)return json({scheduled:false,reason:"cooldown"});
      const previous=Array.from(sql.exec("SELECT updated_at,mode FROM viewer_frame WHERE id=1"));
      if(!d.force && previous.length && now-previous[0].updated_at<3 &&
         previous[0].mode===mode)return json({scheduled:false,reason:"fresh_frame"});
      const lastCwd=Array.from(sql.exec(
        "SELECT cwd FROM commands WHERE id NOT GLOB 'watch_*' AND cwd LIKE '/workspaces/%' ORDER BY created_at DESC LIMIT 1"));
      const cwd=lastCwd.length?lastCwd[0].cwd:"/workspaces/Unity-Game-Aris";
      const id="watch_"+crypto.randomUUID().replace(/-/g,"");
      const shell="python3 \"$HOME/.local/share/aaris-turbo/gui.py\" look"+(mode==="fast"?" --fast":"");
      sql.exec("INSERT INTO commands(id,argv_json,cwd,max_seconds,expires_at,status) VALUES(?,?,?,?,unixepoch()+180,'queued')",
        id,JSON.stringify(["bash","-lc",shell]),cwd,90);
      sql.exec("INSERT INTO config(key,value) VALUES('watch_requested',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",String(now));
      return json({scheduled:true,id});
    }
    if (!path.startsWith("/agent/") || req.method !== "POST") return json({error:"not_found"},404);
    const token = (req.headers.get("authorization") || "").replace(/^Bearer /, "");
    const hashes = Array.from(this.ctx.storage.sql.exec("SELECT value FROM config WHERE key='token_sha256'"));
    const expires = Array.from(this.ctx.storage.sql.exec("SELECT value FROM config WHERE key='token_expires'"));
    if (!hashes.length || !expires.length || Number(expires[0].value) <= Math.floor(Date.now()/1000) ||
        token.length !== 64 || await sha256(token) !== hashes[0].value) return json({error:"unauthorized"},401);
    const sql = this.ctx.storage.sql;
    if (path === "/agent/ping") {
      const now = Math.floor(Date.now()/1000);
      sql.exec("INSERT INTO config(key,value) VALUES('last_seen',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",String(now));
      // Keep authenticated output privately for one day. Remove old image chunks,
      // completed jobs and expired abandoned queue items; never prune running jobs.
      const last = Array.from(sql.exec("SELECT value FROM config WHERE key='last_cleanup'"));
      if (!last.length || now - Number(last[0].value) >= 3600) {
        const stale = "(status IN ('completed','failed') AND COALESCE(finished_at,created_at) < unixepoch()-86400) OR (status='queued' AND expires_at < unixepoch()-86400)";
        sql.exec("DELETE FROM chunks WHERE command_id IN (SELECT id FROM commands WHERE " + stale + ")");
        sql.exec("DELETE FROM commands WHERE " + stale);
        sql.exec("DELETE FROM viewer_frame WHERE updated_at < unixepoch()-86400");
        sql.exec("INSERT INTO config(key,value) VALUES('last_cleanup',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",String(now));
      }
      return json({ok:true});
    }
    if (path === "/agent/next") {
      sql.exec("UPDATE commands SET status='failed',error='agent_lease_expired',finished_at=unixepoch() WHERE status='running' AND started_at + max_seconds + 90 < unixepoch()");
      const active = Array.from(sql.exec("SELECT COUNT(*) AS n FROM commands WHERE status='running'"));
      if (active[0].n >= 2) return json({job:null});
      const rows = Array.from(sql.exec("UPDATE commands SET status='running', started_at=unixepoch() WHERE id=(SELECT id FROM commands WHERE status='queued' AND expires_at>unixepoch() ORDER BY created_at,id LIMIT 1) RETURNING id,argv_json,cwd,max_seconds"));
      return json({job:rows[0]||null});
    }
    if (path === "/agent/chunk") {
      const d=await req.json();
      if(typeof d.id!=="string"||d.id.length>128||!Number.isSafeInteger(d.seq)||d.seq<0||
          !["stdout","stderr"].includes(d.kind)||typeof d.text!=="string"||d.text.length>8192) return json({error:"chunk"},400);
      const found=Array.from(sql.exec("SELECT 1 FROM commands WHERE id=? AND status='running'",d.id));
      if(!found.length) return json({error:"not_running"},409);
      sql.exec("INSERT OR IGNORE INTO chunks(command_id,seq,kind,text) VALUES(?,?,?,?)",d.id,d.seq,d.kind,d.text);
      return json({ok:true});
    }
    if (path === "/agent/done") {
      const d=await req.json();
      if (typeof d.id !=="string" || d.id.length>128 || !Number.isInteger(d.exit_code) || d.exit_code< -255 || d.exit_code>255) return json({error:"done"},400);
      const record=Array.from(sql.exec(
        "SELECT argv_json,status FROM commands WHERE id=?",d.id));
      if(record.length && record[0].status==="running" && d.exit_code===0 &&
          (record[0].argv_json.includes("gui.py")||record[0].argv_json.includes("gui_agent.py"))) {
        const chunks=Array.from(sql.exec(
          "SELECT text FROM chunks WHERE command_id=? AND kind='stdout' ORDER BY seq LIMIT 100",d.id));
        const payload=chunks.map(x=>x.text).join("").slice(0,175000);
        const b64=payload.match(/AARIS_GUI_FRAME_BASE64=([A-Za-z0-9+/=]+)\s+AARIS_GUI_FRAME_COMPLETE/);
        const size=payload.match(/AARIS_GUI_FRAME_SIZE=(\d{3,4})x(\d{3,4})/);
        const mode=payload.match(/AARIS_GUI_FRAME_MODE=(fast|full)/);
        if(b64 && size && b64[1].length>100 && b64[1].length<150000) {
          const width=Number(size[1]),height=Number(size[2]);
          if(width<=1920 && height<=1080 && width>=100 && height>=100){
            sql.exec("INSERT INTO viewer_frame(id,command_id,base64,width,height,mode,updated_at) VALUES(1,?,?,?,?,?,unixepoch()) ON CONFLICT(id) DO UPDATE SET command_id=excluded.command_id,base64=excluded.base64,width=excluded.width,height=excluded.height,mode=excluded.mode,updated_at=excluded.updated_at",
              d.id,b64[1],width,height,mode?.[1]||"full");
          }
        }
      }
      sql.exec("UPDATE commands SET status=?,exit_code=?,finished_at=unixepoch(),error=? WHERE id=? AND status='running'",
        d.exit_code===0?"completed":"failed",d.exit_code,
        typeof d.error==="string"?d.error.slice(0,100):"",d.id);
      return json({ok:true});
    }
    return json({error:"not_found"},404);
  }
}
export default {
  async fetch(request, env) {
    const u = new URL(request.url);

    // Permanent browser URL; fragment-held key is never in the HTTP request URL.
    if (u.pathname === "/watch" && request.method==="GET") {
      const nonce=btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(18))))
        .replace(/=/g,"").replace(/\+/g,"-").replace(/\//g,"_");
      const policy="default-src 'none'; script-src 'nonce-"+nonce+
        "'; style-src 'unsafe-inline'; img-src blob:; connect-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'";
      return new Response(renderWatchPage(nonce),{headers:{
        "content-type":"text/html; charset=utf-8",
        "content-security-policy":policy,
        "referrer-policy":"no-referrer","cache-control":"private, no-store",
        "x-content-type-options":"nosniff","x-frame-options":"DENY"}});
    }
    if (u.pathname.startsWith("/watch/")) {
      const presented=(request.headers.get("authorization")||"").match(/^Bearer ([0-9a-f]{64})$/);
      if(!env.WATCH_TOKEN_HASH || !presented ||
         !constantTimeEqual(await sha256(presented[1]),String(env.WATCH_TOKEN_HASH)))
        return json({error:"unauthorized"},401);
      const registry=env.MACHINES.get(env.MACHINES.idFromName("registry"));
      const response=await registry.fetch("https://internal/internal/watch/registry");
      if(!response.ok)return json({error:"registry_unavailable"},503);
      const {machines:rows=[]}=await response.json();
      const now=Math.floor(Date.now()/1000);
      const machines=rows.filter(m=>validMachine(m.name)).map(m=>({
        name:m.name,stage:m.stage,last_seen:m.last_seen,
        live:m.stage==="connected"&&now-Number(m.last_seen)<=90&&now>=Number(m.last_seen)-60
      }));
      const requested=u.pathname==="/watch/capture" ?
        null : u.searchParams.get("machine");
      let requestedName=requested;
      let payload={};
      if(u.pathname==="/watch/capture" && request.method==="POST"){
        if(request.headers.get("origin") && request.headers.get("origin")!==u.origin)
          return json({error:"origin"},403);
        try{payload=await request.json();}catch(_){return json({error:"json"},400);}
        if(payload && payload.machine)requestedName=payload.machine;
      }
      if(requestedName && (!validMachine(requestedName)||!machines.some(m=>m.name===requestedName)))
        return json({error:"unknown_machine"},404);
      const selected=requestedName?machines.find(m=>m.name===requestedName):
        (machines.find(m=>m.live)||machines[0]);
      if(!selected){
        return json({machine:null,live:false,heartbeat_age:null,
          machines,jobs:[],frame:null});
      }
      const stub=env.MACHINES.get(env.MACHINES.idFromName("machine:"+selected.name));
      if(u.pathname==="/watch/api" && request.method==="GET") {
        if(u.searchParams.get("capture")==="1" && selected.live) {
          const quality=u.searchParams.get("quality")==="full"?"full":"fast";
          await stub.fetch("https://internal/internal/watch/capture",{method:"POST",
            body:JSON.stringify({quality,force:false})});
        }
        const data=await stub.fetch("https://internal/internal/watch/state");
        if(!data.ok)return json({error:"machine_state"},503);
        const state=await data.json();
        return json({machine:selected.name,live:state.live&&selected.live,
          heartbeat_age:state.heartbeat_age,frame:state.frame,
          jobs:state.jobs,machines});
      }
      if(u.pathname==="/watch/frame" && request.method==="GET") {
        const id=u.searchParams.get("id");
        if(!id || !/^[A-Za-z0-9_-]{1,128}$/.test(id))return json({error:"invalid_frame"},400);
        const frame=await stub.fetch("https://internal/internal/watch/frame?id="+encodeURIComponent(id));
        const headers=new Headers(frame.headers);
        headers.set("referrer-policy","no-referrer");
        headers.set("cache-control","private, no-store");
        headers.set("x-content-type-options","nosniff");
        return new Response(frame.body,{status:frame.status,headers});
      }
      if(u.pathname==="/watch/capture" && request.method==="POST") {
        if(!selected.live)return json({scheduled:false,reason:"offline"},409);
        const result=await stub.fetch("https://internal/internal/watch/capture",{method:"POST",
          body:JSON.stringify({quality:payload?.quality==="full"?"full":"fast",force:true})});
        return new Response(result.body,{status:result.status,headers:{
          "content-type":"application/json","cache-control":"private, no-store"}});
      }
      return json({error:"not_found"},404);
    }
    if (u.pathname === "/health" && request.method==="GET") {
      const operation = request.headers.get("x-aaris-op");
      if (!operation) return json({service:"aaris-turbo",version:2,status:"ready"});
      const machine = request.headers.get("x-aaris-machine");
      const credential = request.headers.get("x-aaris-token");
      const encoded = request.headers.get("x-aaris-payload") || "";
      if (!["ping","next","chunk","done"].includes(operation) ||
          !validMachine(machine) || !/^[0-9a-f]{64}$/.test(credential || "") ||
          encoded.length > 11000 || !/^[A-Za-z0-9_-]*$/.test(encoded)) return json({error:"unauthorized"},401);
      let payload;
      try {
        payload=encoded ? JSON.parse(new TextDecoder().decode(decodeB64(encoded))) : {};
        if (!payload || typeof payload !== "object" || Array.isArray(payload)) throw Error("not_object");
      } catch (_) { return json({error:"bad_payload"},400); }
      const stub=env.MACHINES.get(env.MACHINES.idFromName("machine:"+machine));
      const response=await stub.fetch("https://internal/agent/"+operation,
        {method:"POST",headers:{"authorization":"Bearer "+credential,"content-type":"application/json"},
          body:JSON.stringify(payload)});
      if (operation === "ping" && response.ok) {
        const registry = env.MACHINES.get(env.MACHINES.idFromName("registry"));
        await registry.fetch("https://internal/internal/register",{
          method:"POST",body:JSON.stringify({name:machine,stage:"connected"})});
      }
      return response;
    }
    if (u.pathname === "/bootstrap" && request.method==="POST") {
      try {
        const bearer = (request.headers.get("authorization")||"").replace(/^Bearer /, "");
        await verifyActionsJWT(bearer);
        const b = await request.json();
        if (!validMachine(b.machine)) return json({error:"bad_machine"},400);
        const token = Array.from(crypto.getRandomValues(new Uint8Array(32)), v=>v.toString(16).padStart(2,"0")).join("");
        const stub = env.MACHINES.get(env.MACHINES.idFromName("machine:"+b.machine));
        const response = await stub.fetch("https://internal/internal/grant",{method:"POST",body:JSON.stringify({token})});
        if (!response.ok) return json({error:"grant_failed"},503);
        const registry = env.MACHINES.get(env.MACHINES.idFromName("registry"));
        await registry.fetch("https://internal/internal/register",{method:"POST",body:JSON.stringify({name:b.machine,stage:"bootstrapped"})});
        return json({machine:b.machine,token,endpoint:u.origin+"/agent/"+encodeURIComponent(b.machine),expires_at:Math.floor(Date.now()/1000)+7*86400});
      } catch (_) { return json({error:"unauthorized"},401); }
    }
    const match=u.pathname.match(/^\/agent\/([A-Za-z0-9-]{3,120})\/(ping|next|chunk|done)$/);
    if(!match || request.method!=="POST") return json({error:"not_found"},404);
    const stub=env.MACHINES.get(env.MACHINES.idFromName("machine:"+match[1]));
    const response = await stub.fetch(new Request("https://internal/agent/"+match[2],request));
    if (match[2] === "ping" && response.ok) {
      const registry = env.MACHINES.get(env.MACHINES.idFromName("registry"));
      await registry.fetch("https://internal/internal/register", {method:"POST",
        body:JSON.stringify({name:match[1],stage:"connected"})});
    }
    return response;
  }
};
