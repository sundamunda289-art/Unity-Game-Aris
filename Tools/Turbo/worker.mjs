import { renderWatchPage } from "./watch_page.mjs";
// Aaris Turbo Broker. No public command submission endpoint.
// Only owner-authorized Cloudflare Durable Object SQL API may enqueue commands.
const REPO = "sundamunda289-art/Unity-Game-Aris";
const REPO_ID = "1443109769";
const ACTOR_ID = "353065137";
const WORKFLOW = REPO + "/.github/workflows/aaris-turbo-bootstrap.yml@refs/heads/main";
const AUDIENCE = "aaris-turbo-bootstrap-v1";
const TRANSACTION_PROTOCOL = 1;
const TX_RE = /^[a-f0-9]{32}$/;
const TOKEN_RE = /^[a-f0-9]{64}$/;
async function boundedJSON(request, maximum = 4096) {
  const reader = request.body?.getReader();
  if (!reader) throw Error("body_required");
  const parts = [];
  let size = 0;
  for (;;) {
    const {done, value} = await reader.read();
    if (done) break;
    size += value.length;
    if (size > maximum) { await reader.cancel(); throw Error("body_too_large"); }
    parts.push(value);
  }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const part of parts) { bytes.set(part, offset); offset += part.length; }
  return JSON.parse(new TextDecoder().decode(bytes));
}
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
  setting(key) {
    const rows = Array.from(this.ctx.storage.sql.exec("SELECT value FROM config WHERE key=?", key));
    return rows.length ? rows[0].value : null;
  }
  putSetting(key, value) {
    this.ctx.storage.sql.exec("INSERT INTO config(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, String(value));
  }
  transactionRecord() {
    const raw = this.setting("agent_transaction");
    return raw ? JSON.parse(raw) : null;
  }
  runningCount() {
    return Array.from(this.ctx.storage.sql.exec("SELECT COUNT(*) AS n FROM commands WHERE status='running'"))[0].n;
  }
  expiredRunningCount() {
    // A deadline is not proof that an old process stopped. Keep these jobs
    // visible and require reconciliation instead of silently declaring drain.
    return Array.from(this.ctx.storage.sql.exec("SELECT COUNT(*) AS n FROM commands WHERE status='running' AND (started_at IS NULL OR started_at + max_seconds + 90 < unixepoch())"))[0].n;
  }
  transactionStatus(record) {
    const probes=record.probe_job?Array.from(this.ctx.storage.sql.exec("SELECT status,started_at,max_seconds,expires_at FROM commands WHERE id=?",record.probe_job)):[];
    let probeState=probes.length?probes[0].status:null;
    if (probes.length && ((probeState==="queued" && probes[0].expires_at<=Math.floor(Date.now()/1000)) ||
        (probeState==="running" && probes[0].started_at+probes[0].max_seconds+90<Math.floor(Date.now()/1000)))) probeState="expired";
    return {ok:true, transaction_protocol:TRANSACTION_PROTOCOL,
      transaction_id:record.id, state:record.state, generation:record.generation,
      expires_at:record.expires_at, deadline:record.deadline,
      active_generation:Number(this.setting("agent_generation") || 0),
      running_jobs:this.runningCount(),expired_running_jobs:this.expiredRunningCount(), probe_verified:Boolean(record.probe_verified),probe_state:probeState,
      previous_active:record.previous_active,
      machine:record.machine, ready_target:record.state==="ready"?{
        machine:record.machine,transaction_id:record.id,generation:record.generation,
        expected_revision:record.expected_active_revision}:null,
      rollback_target:record.state==="rolled_back"?{transaction_id:record.id,
        machine:record.machine,generation:record.generation,previous:record.previous_active}:null};
  }
  expirePreparation() {
    const record = this.transactionRecord();
    if (record && ["prepared", "draining"].includes(record.state) && record.deadline < Math.floor(Date.now()/1000)) {
      record.state = "aborted";
      record.reason = "preparation_expired";
      this.putSetting("agent_transaction", JSON.stringify(record));
      if (this.setting("dispatch_pause") === record.id) this.putSetting("dispatch_pause", "");
    }
    return record;
  }
  async prepareGrant(data) {
    if (!TX_RE.test(data.transaction_id || "") || !TOKEN_RE.test(data.token || "") || !validMachine(data.machine) ||
        !Number.isSafeInteger(data.expected_active_revision) || data.expected_active_revision<0)
      return json({error:"invalid_transaction"},400);
    const tokenHash = await sha256(data.token);
    return this.ctx.storage.transactionSync(() => {
      const previous = this.expirePreparation();
      if (previous?.id === data.transaction_id) {
        if (!constantTimeEqual(previous.token_hash, tokenHash)) return json({error:"transaction_token_mismatch"},409);
        if (["aborted", "rolled_back"].includes(previous.state)) return json({error:"transaction_finished",state:previous.state},409);
        return json(this.transactionStatus(previous));
      }
      if (previous && !["ready", "aborted", "rolled_back"].includes(previous.state))
        return json({error:"transaction_busy"},409);
      const now = Math.floor(Date.now()/1000);
      const record = {
        id:data.transaction_id, machine:data.machine, token_hash:tokenHash,
        generation:Number(this.setting("agent_generation") || 0)+1,
        state:"prepared", expires_at:now+7*86400, deadline:now+3600,
        expected_active_revision:data.expected_active_revision,
        previous_active:data.previous_active || null,
        probe_nonce:crypto.randomUUID().replace(/-/g,""), probe_verified:false,
        previous_hash:this.setting("token_sha256"),
        previous_expiry:this.setting("token_expires"),
        previous_generation:Number(this.setting("agent_generation") || 0)
      };
      this.putSetting("agent_transaction", JSON.stringify(record));
      return json(this.transactionStatus(record));
    });
  }
  async transactionOperation(req) {
    let data;
    try { data = await boundedJSON(req); } catch (_) { return json({error:"invalid_transaction_body"},400); }
    const token = (req.headers.get("authorization") || "").replace(/^Bearer /, "");
    if (!TOKEN_RE.test(token) || !TX_RE.test(data.transaction_id || "")) return json({error:"unauthorized"},401);
    const tokenHash = await sha256(token);
    return this.ctx.storage.transactionSync(() => {
      const record = this.expirePreparation();
      if (!record || record.id !== data.transaction_id || !constantTimeEqual(record.token_hash, tokenHash))
        return json({error:"unauthorized"},401);
      const status = () => json(this.transactionStatus(record));
      const save = () => this.putSetting("agent_transaction", JSON.stringify(record));
      if (data.operation === "status") return status();
      if (record.expires_at<=Math.floor(Date.now()/1000)) return json({error:"transaction_expired"},401);
      if (data.operation === "abort") {
        if (["aborted", "rolled_back"].includes(record.state)) return status();
        if (!["prepared", "draining"].includes(record.state)) return json({error:"already_committed"},409);
        record.state="aborted"; save();
        if (this.setting("dispatch_pause")===record.id) this.putSetting("dispatch_pause", "");
        return status();
      }
      if (data.operation === "probe" && ["prepared", "draining"].includes(record.state))
        return json({...this.transactionStatus(record),nonce:record.probe_nonce});
      if (data.operation === "probe-result" && ["prepared", "draining"].includes(record.state)) {
        if (data.nonce!==record.probe_nonce || data.exit_code!==0 || data.output!=="AARIS_TRANSACTION_PROBE="+record.probe_nonce+"\n")
          return json({error:"probe_failed"},409);
        record.probe_verified=true; save(); return status();
      }
      if (data.operation === "begin-cutover" && ["prepared", "draining"].includes(record.state)) {
        if (!record.probe_verified) return json({error:"probe_required"},409);
        if (Number(this.setting("agent_generation") || 0)!==record.previous_generation ||
            this.setting("token_sha256")!==record.previous_hash) return json({error:"generation_changed"},409);
        record.state="draining"; save(); this.putSetting("dispatch_pause",record.id);
        return status();
      }
      if (data.operation === "commit") {
        if (["committed", "ready"].includes(record.state)) return status();
        if (record.state!=="draining" || !record.probe_verified || this.runningCount()!==0)
          return json({error:"drain_required",running_jobs:this.runningCount()},409);
        if (Number(this.setting("agent_generation") || 0)!==record.previous_generation ||
            this.setting("token_sha256")!==record.previous_hash) return json({error:"generation_changed"},409);
        record.state="committed";
        record.probe_job="txn_probe_"+record.id;
        record.committed_at=Math.floor(Date.now()/1000);
        this.putSetting("token_sha256",record.token_hash);
        this.putSetting("token_expires",record.expires_at);
        this.putSetting("agent_generation",record.generation);
        this.putSetting("dispatch_pause",record.id);
        const argv=["python3","-c","print('AARIS_TRANSACTION_ACTIVE="+record.probe_nonce+"')"];
        this.ctx.storage.sql.exec("INSERT OR IGNORE INTO commands(id,argv_json,cwd,max_seconds,expires_at,status) VALUES(?,?,?,?,?,'queued')",
          record.probe_job,JSON.stringify(argv),"/workspaces/Unity-Game-Aris",30,Math.floor(Date.now()/1000)+300);
        save(); return status();
      }
      if (data.operation === "retry-probe" && record.state==="committed") {
        const probeState=this.transactionStatus(record).probe_state;
        if (!["failed","expired"].includes(probeState)) return json({error:"probe_still_running"},409);
        if ((record.probe_retries || 0)>=3) return json({error:"probe_retry_limit"},409);
        this.ctx.storage.sql.exec("UPDATE commands SET status='failed',error='transaction_probe_retry',finished_at=unixepoch() WHERE id=? AND status IN ('queued','running')",record.probe_job);
        record.probe_retries=(record.probe_retries || 0)+1;
        record.probe_nonce=crypto.randomUUID().replace(/-/g,"");
        record.probe_job="txn_probe_"+record.id+"_"+record.probe_retries;
        const argv=["python3","-c","print('AARIS_TRANSACTION_ACTIVE="+record.probe_nonce+"')"];
        this.ctx.storage.sql.exec("INSERT INTO commands(id,argv_json,cwd,max_seconds,expires_at,status) VALUES(?,?,?,?,?,'queued')",
          record.probe_job,JSON.stringify(argv),"/workspaces/Unity-Game-Aris",30,Math.floor(Date.now()/1000)+300);
        save();return status();
      }
      if (data.operation === "rollback-prepare" && ["committed", "ready", "rollback_draining"].includes(record.state)) {
        record.state="rollback_draining"; save(); this.putSetting("dispatch_pause",record.id); return status();
      }
      if (data.operation === "rollback") {
        if (record.state==="rolled_back") return status();
        if (record.state!=="rollback_draining" || this.runningCount()!==0) return json({error:"rollback_drain_required"},409);
        if (Number(this.setting("agent_generation") || 0)!==record.generation || this.setting("token_sha256")!==record.token_hash)
          return json({error:"generation_changed"},409);
        if (record.previous_hash && Number(record.previous_expiry)>Math.floor(Date.now()/1000)) {
          this.putSetting("token_sha256",record.previous_hash);
          this.putSetting("token_expires",record.previous_expiry);
        } else {
          this.ctx.storage.sql.exec("DELETE FROM config WHERE key IN ('token_sha256','token_expires')");
        }
        this.putSetting("agent_generation",record.previous_generation);
        this.putSetting("dispatch_pause", "");
        if (record.probe_job) this.ctx.storage.sql.exec("UPDATE commands SET status='failed',error='transaction_rolled_back',finished_at=unixepoch() WHERE id=? AND status='queued'",record.probe_job);
        record.state="rolled_back"; save(); return status();
      }
      return json({error:"transaction_phase",state:record.state},409);
    });
  }
  async fetch(req) {
    const path = new URL(req.url).pathname;
    if (path === "/internal/prepare" && req.method === "POST") return this.prepareGrant(await boundedJSON(req));
    if (path === "/internal/consumer-state" && req.method === "GET")
      return json({generation:Number(this.setting("agent_generation") || 0),running_jobs:this.runningCount(),expired_running_jobs:this.expiredRunningCount()});
    if (path === "/internal/consumer-drain" && req.method === "POST") {
      const data=await boundedJSON(req);
      if (!TX_RE.test(data.transaction_id || "") || !Number.isSafeInteger(data.generation) ||
          !["drain","status","resume"].includes(data.operation)) return json({error:"invalid_handoff"},400);
      const generation=Number(this.setting("agent_generation") || 0);
      if (generation!==data.generation) return json({error:"previous_generation_changed"},409);
      const raw=this.setting("handoff_pause"), pause=raw?JSON.parse(raw):null;
      if (data.operation==="drain") {
        if (pause && pause.generation===generation && pause.transaction_id!==data.transaction_id)
          return json({error:"another_handoff_active"},409);
        this.putSetting("handoff_pause",JSON.stringify({transaction_id:data.transaction_id,generation}));
      } else if (data.operation==="resume" && pause?.transaction_id===data.transaction_id) {
        this.ctx.storage.sql.exec("DELETE FROM config WHERE key='handoff_pause'");
      }
      const actual=this.setting("handoff_pause"), paused=actual?JSON.parse(actual):null;
      return json({ok:true,generation,running_jobs:this.runningCount(),expired_running_jobs:this.expiredRunningCount(),paused:paused?.transaction_id===data.transaction_id});
    }
    if (path === "/internal/broker-check" && req.method === "POST") {
      const {nonce}=await boundedJSON(req);
      if (!TX_RE.test(nonce || "")) return json({error:"invalid_nonce"},400);
      return this.ctx.storage.transactionSync(() => {
        this.putSetting("broker_probe",nonce);
        const actual=this.setting("broker_probe");
        this.ctx.storage.sql.exec("DELETE FROM config WHERE key='broker_probe'");
        return json({ok:actual===nonce,nonce:actual,storage_ready:actual===nonce});
      });
    }
    if (path === "/internal/active" && req.method === "GET") {
      const raw=this.setting("active_target");
      const active=raw?JSON.parse(raw):null;
      return json({active,revision:Number(this.setting("active_revision") || active?.revision || 0)});
    }
    if (path === "/internal/active" && req.method === "POST") {
      const data=await boundedJSON(req);
      if (!validMachine(data.machine)||!TX_RE.test(data.transaction_id || "")||!Number.isSafeInteger(data.generation)||!Number.isSafeInteger(data.expected_revision))
        return json({error:"invalid_target"},400);
      const raw=this.setting("active_target"), active=raw?JSON.parse(raw):null;
      if (active?.transaction_id===data.transaction_id && active?.machine===data.machine && active?.generation===data.generation)
        return json({ok:true});
      if (Number(this.setting("active_revision") || active?.revision || 0)!==data.expected_revision) return json({error:"active_target_changed"},409);
      this.putSetting("active_target",JSON.stringify({machine:data.machine,generation:data.generation,
        transaction_id:data.transaction_id,revision:data.expected_revision+1}));
      this.putSetting("active_revision",data.expected_revision+1);
      return json({ok:true});
    }
    if (path === "/internal/restore-active" && req.method === "POST") {
      const data=await boundedJSON(req);
      const raw=this.setting("active_target"), active=raw?JSON.parse(raw):null;
      if (!active || active.transaction_id!==data.transaction_id || active.machine!==data.machine || active.generation!==data.generation)
        return json({ok:true,restored:false});
      const revision=Number(this.setting("active_revision") || active.revision || 0)+1;
      if (data.previous) this.putSetting("active_target",JSON.stringify({...data.previous,revision}));
      else this.ctx.storage.sql.exec("DELETE FROM config WHERE key='active_target'");
      this.putSetting("active_revision",revision);
      return json({ok:true,restored:true});
    }
    if (path === "/agent/transaction" && req.method === "POST") return this.transactionOperation(req);
    if (path === "/internal/grant" && req.method === "POST") {
      const data = await req.json();
      if (typeof data.token !== "string" || data.token.length !== 64) return json({error:"token"}, 400);
      const hash=await sha256(data.token);
      // Old entrypoints may bootstrap a new/expired machine, but must never revoke
      // a valid working generation. Rotation requires the transactional protocol.
      if (Number(this.setting("token_expires"))>Math.floor(Date.now()/1000))
        return json({error:"transaction_required_for_rotation"},409);
      this.ctx.storage.sql.exec("INSERT INTO config(key,value) VALUES('token_sha256',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", hash);
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
      const active=this.setting("active_target");
      return json({machines,active:active?JSON.parse(active):null});
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
    if (!TOKEN_RE.test(token)) return json({error:"unauthorized"},401);
    const presentedHash=await sha256(token);
    const hashes = Array.from(this.ctx.storage.sql.exec("SELECT value FROM config WHERE key='token_sha256'"));
    const expires = Array.from(this.ctx.storage.sql.exec("SELECT value FROM config WHERE key='token_expires'"));
    if (!hashes.length || !expires.length || Number(expires[0].value) <= Math.floor(Date.now()/1000) ||
        !constantTimeEqual(presentedHash,hashes[0].value)) return json({error:"unauthorized"},401);
    const sql = this.ctx.storage.sql;
    if (path === "/agent/control") {
      let data;
      try {data=await boundedJSON(req);} catch (_) {return json({error:"invalid_control"},400);}
      if (!constantTimeEqual(this.setting("token_sha256"),presentedHash)) return json({error:"unauthorized"},401);
      if (!TX_RE.test(data.transaction_id || "") || !["drain","status","resume"].includes(data.operation))
        return json({error:"invalid_control"},400);
      const owner="external:"+data.transaction_id;
      const pause=this.setting("dispatch_pause");
      if (data.operation==="drain") {
        if (pause && pause!==owner && pause!==data.transaction_id) return json({error:"another_drain_active"},409);
        this.putSetting("dispatch_pause",owner);
      } else if (data.operation==="resume" && pause===owner) {
        this.putSetting("dispatch_pause", "");
      }
      return json({ok:true,running_jobs:this.runningCount(),expired_running_jobs:this.expiredRunningCount(),paused:Boolean(this.setting("dispatch_pause"))});
    }
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
      const transaction=this.transactionRecord();
      return json({ok:true,ready_target:transaction?.state==="ready"?this.transactionStatus(transaction).ready_target:null});
    }
    if (path === "/agent/next") {
      const transaction=this.expirePreparation();
      const rawPause=this.setting("handoff_pause"), handoffPause=rawPause?JSON.parse(rawPause):null;
      if (handoffPause?.generation===Number(this.setting("agent_generation") || 0)) return json({job:null});
      // A committed candidate proves the real queue/child/output path before it
      // can consume any ordinary command, including on a non-selected machine.
      if (transaction?.state==="committed" && this.setting("token_sha256")===transaction.token_hash) {
        const rows=Array.from(sql.exec("UPDATE commands SET status='running', started_at=unixepoch() WHERE id=? AND status='queued' AND expires_at>unixepoch() RETURNING id,argv_json,cwd,max_seconds",transaction.probe_job));
        return json({job:rows[0]||null});
      }
      if (this.setting("dispatch_pause") || req.headers.get("x-aaris-dispatch-allowed")==="0" ||
          (req.headers.has("x-aaris-active-generation") && Number(req.headers.get("x-aaris-active-generation"))!==Number(this.setting("agent_generation") || 0))) return json({job:null});
      sql.exec("UPDATE commands SET status='failed',error='agent_lease_expired',finished_at=unixepoch() WHERE status='running' AND started_at + max_seconds + 90 < unixepoch()");
      const active = Array.from(sql.exec("SELECT COUNT(*) AS n FROM commands WHERE status='running'"));
      if (active[0].n >= 2) return json({job:null});
      const rows = Array.from(sql.exec("UPDATE commands SET status='running', started_at=unixepoch() WHERE id=(SELECT id FROM commands WHERE status='queued' AND expires_at>unixepoch() ORDER BY created_at,id LIMIT 1) RETURNING id,argv_json,cwd,max_seconds"));
      return json({job:rows[0]||null});
    }
    if (path === "/agent/chunk") {
      const d=await req.json();
      if (!constantTimeEqual(this.setting("token_sha256"),presentedHash)) return json({error:"unauthorized"},401);
      if(typeof d.id!=="string"||d.id.length>128||!Number.isSafeInteger(d.seq)||d.seq<0||
          !["stdout","stderr"].includes(d.kind)||typeof d.text!=="string"||d.text.length>8192) return json({error:"chunk"},400);
      const found=Array.from(sql.exec("SELECT 1 FROM commands WHERE id=? AND status='running'",d.id));
      if(!found.length) return json({error:"not_running"},409);
      sql.exec("INSERT OR IGNORE INTO chunks(command_id,seq,kind,text) VALUES(?,?,?,?)",d.id,d.seq,d.kind,d.text);
      return json({ok:true});
    }
    if (path === "/agent/done") {
      const d=await req.json();
      if (!constantTimeEqual(this.setting("token_sha256"),presentedHash)) return json({error:"unauthorized"},401);
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
      const transaction=this.transactionRecord();
      if (transaction?.state==="committed" && d.id===transaction.probe_job) {
        const completed=Array.from(sql.exec("SELECT status,exit_code FROM commands WHERE id=?",d.id));
        const output=Array.from(sql.exec("SELECT text FROM chunks WHERE command_id=? AND kind='stdout' ORDER BY seq LIMIT 10",d.id)).map(r=>r.text).join("");
        if (completed[0]?.status==="completed" && completed[0]?.exit_code===0 && d.exit_code===0 && output==="AARIS_TRANSACTION_ACTIVE="+transaction.probe_nonce+"\n") {
          transaction.state="ready";
          this.putSetting("agent_transaction",JSON.stringify(transaction));
          this.putSetting("dispatch_pause", "");
        }
      }
      return json({ok:true,ready_target:transaction?.state==="ready"?this.transactionStatus(transaction).ready_target:null});
    }
    return json({error:"not_found"},404);
  }
}
async function previousConsumer(env, machine, transaction, operation) {
  const previous=transaction.previous_active;
  if (!previous || previous.machine===machine) return {ok:true,running_jobs:0,expired_running_jobs:0,paused:true};
  const stub=env.MACHINES.get(env.MACHINES.idFromName("machine:"+previous.machine));
  const response=await stub.fetch("https://internal/internal/consumer-drain",{method:"POST",body:JSON.stringify({
    transaction_id:transaction.transaction_id,generation:previous.generation,operation})});
  if (!response.ok) return {ok:false,status:response.status};
  return response.json();
}
async function routeAgent(env, machine, operation, payload, credential) {
  const stub=env.MACHINES.get(env.MACHINES.idFromName("machine:"+machine));
  const registry=env.MACHINES.get(env.MACHINES.idFromName("registry"));
  const headers=new Headers({authorization:"Bearer "+credential,"content-type":"application/json"});
  if (operation==="transaction" && payload.operation==="commit") {
    const checked=await stub.fetch("https://internal/agent/transaction",{method:"POST",headers,
      body:JSON.stringify({operation:"status",transaction_id:payload.transaction_id})});
    if (!checked.ok) return checked;
    const candidate=await checked.json();
    if (candidate.state==="draining") {
      const old=await previousConsumer(env,machine,candidate,"drain");
      if (!old.ok) return json({error:"previous_consumer_reconciliation_required"},old.status || 503);
      if (!old.paused || old.running_jobs) return json({error:"previous_consumer_still_draining"},409);
    }
  }
  if (operation==="next") {
    const state=await registry.fetch("https://internal/internal/active");
    if (!state.ok) return json({error:"registry_unavailable"},503);
    const {active}=await state.json();
    headers.set("x-aaris-dispatch-allowed",!active || active.machine===machine?"1":"0");
    if (active?.machine===machine) headers.set("x-aaris-active-generation",String(active.generation));
  }
  const response=await stub.fetch("https://internal/agent/"+operation,
    {method:"POST",headers,body:JSON.stringify(payload)});
  if (!response.ok) return response;
  const result=await response.json();
  if (operation==="transaction" && result.previous_active && result.previous_active.machine!==machine) {
    if (["draining","committed"].includes(result.state)) {
      const old=await previousConsumer(env,machine,result,result.state==="draining"?"drain":"status");
      if (!old.ok) return json({error:"previous_consumer_reconciliation_required"},old.status || 503);
      if (!old.paused) return json({error:"previous_consumer_not_fenced"},409);
      result.running_jobs+=old.running_jobs;
      result.expired_running_jobs+=old.expired_running_jobs || 0;
    } else if (["aborted","rolled_back"].includes(result.state)) {
      const old=await previousConsumer(env,machine,result,"resume");
      if (!old.ok) return json({error:"previous_consumer_resume_unconfirmed"},old.status || 503);
    }
  }
  if (result.ready_target) {
    // Compare-and-swap against the registry revision captured at preparation.
    // An old machine heartbeat can never reselect itself after a newer switch.
    const promoted=await registry.fetch("https://internal/internal/active",{
      method:"POST",body:JSON.stringify(result.ready_target)});
    if (!promoted.ok && promoted.status!==409) return json({error:"registry_unavailable"},503);
  }
  if (result.rollback_target) {
    const restored=await registry.fetch("https://internal/internal/restore-active",{
      method:"POST",body:JSON.stringify(result.rollback_target)});
    if (!restored.ok) return json({error:"registry_unavailable"},503);
  }
  if (operation==="transaction") {
    const state=await registry.fetch("https://internal/internal/active");
    if (!state.ok) return json({error:"registry_unavailable"},503);
    const {active}=await state.json();
    result.selected=Boolean(active && active.machine===machine && active.transaction_id===result.transaction_id && active.generation===result.generation);
  }
  delete result.ready_target;
  delete result.rollback_target;
  delete result.previous_active;
  if (operation==="ping") {
    await registry.fetch("https://internal/internal/register",{
      method:"POST",body:JSON.stringify({name:machine,stage:"connected"})});
  }
  return json(result);
}
export default {
  async fetch(request, env) {
    const u = new URL(request.url);
    if (u.pathname==="/broker/check" && request.method==="POST") {
      const token=(request.headers.get("authorization")||"").replace(/^Bearer /,"");
      if (!TOKEN_RE.test(token) || !env.BROKER_PROBE_HASH ||
          !constantTimeEqual(await sha256(token),String(env.BROKER_PROBE_HASH))) return json({error:"unauthorized"},401);
      let payload;
      try {payload=await boundedJSON(request);} catch (_) {return json({error:"invalid_probe"},400);}
      if (!TX_RE.test(payload.nonce || "")) return json({error:"invalid_probe"},400);
      const registry=env.MACHINES.get(env.MACHINES.idFromName("registry"));
      const result=await registry.fetch("https://internal/internal/broker-check",{method:"POST",body:JSON.stringify({nonce:payload.nonce})});
      if (!result.ok) return json({error:"storage_unavailable"},503);
      return json({...await result.json(),service:"aaris-turbo",transaction_protocol:TRANSACTION_PROTOCOL,
        deployment_operation:env.AARIS_DEPLOY_OPERATION || null,source_sha:env.AARIS_SOURCE_SHA || null});
    }

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
      const {machines:rows=[],active=null}=await response.json();
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
        (active?(machines.find(m=>m.name===active.machine)||{name:active.machine,live:false,last_seen:0}):(machines.find(m=>m.live)||machines[0]));
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
      if (!operation) return json({service:"aaris-turbo",version:2,status:"ready",transaction_protocol:TRANSACTION_PROTOCOL,
        deployment_operation:env.AARIS_DEPLOY_OPERATION || null,source_sha:env.AARIS_SOURCE_SHA || null});
      const machine = request.headers.get("x-aaris-machine");
      const credential = request.headers.get("x-aaris-token");
      const encoded = request.headers.get("x-aaris-payload") || "";
      if (!["ping","next","chunk","done","transaction","control"].includes(operation) ||
          !validMachine(machine) || !/^[0-9a-f]{64}$/.test(credential || "") ||
          encoded.length > 11000 || !/^[A-Za-z0-9_-]*$/.test(encoded)) return json({error:"unauthorized"},401);
      let payload;
      try {
        payload=encoded ? JSON.parse(new TextDecoder().decode(decodeB64(encoded))) : {};
        if (!payload || typeof payload !== "object" || Array.isArray(payload)) throw Error("not_object");
      } catch (_) { return json({error:"bad_payload"},400); }
      return routeAgent(env,machine,operation,payload,credential);
    }
    if (u.pathname === "/bootstrap" && request.method==="POST") {
      try {
        const bearer = (request.headers.get("authorization")||"").replace(/^Bearer /, "");
        await verifyActionsJWT(bearer);
        const b = await boundedJSON(request);
        if (!validMachine(b.machine)) return json({error:"bad_machine"},400);
        if (b.mode==="prepare") {
          if (!TX_RE.test(b.transaction_id || "") || !TOKEN_RE.test(b.token || "")) return json({error:"invalid_transaction"},400);
          const registry=env.MACHINES.get(env.MACHINES.idFromName("registry"));
          const state=await registry.fetch("https://internal/internal/active");
          if (!state.ok) return json({error:"registry_unavailable"},503);
          const {revision,active}=await state.json();
          let previous=active;
          if (!previous) {
            const listing=await registry.fetch("https://internal/internal/watch/registry");
            if (!listing.ok) return json({error:"registry_unavailable"},503);
            const now=Math.floor(Date.now()/1000);
            const fresh=(await listing.json()).machines.filter(m=>m.stage==="connected" && now-m.last_seen<=90);
            if (fresh.length>1) return json({error:"legacy_active_machine_selection_required"},409);
            if (fresh.length===1) {
              const old=env.MACHINES.get(env.MACHINES.idFromName("machine:"+fresh[0].name));
              const oldState=await old.fetch("https://internal/internal/consumer-state");
              if (!oldState.ok) return json({error:"previous_consumer_unavailable"},503);
              previous={machine:fresh[0].name,generation:(await oldState.json()).generation,transaction_id:"legacy",revision};
            }
          }
          const stub=env.MACHINES.get(env.MACHINES.idFromName("machine:"+b.machine));
          const response=await stub.fetch("https://internal/internal/prepare",{method:"POST",body:JSON.stringify({
            machine:b.machine,transaction_id:b.transaction_id,token:b.token,expected_active_revision:revision,previous_active:previous})});
          if (!response.ok) return response;
          const grant=await response.json();
          delete grant.ready_target;
          delete grant.rollback_target;
          delete grant.previous_active;
          return json({...grant,endpoint:u.origin+"/agent/"+encodeURIComponent(b.machine)});
        }
        const token = Array.from(crypto.getRandomValues(new Uint8Array(32)), v=>v.toString(16).padStart(2,"0")).join("");
        const stub = env.MACHINES.get(env.MACHINES.idFromName("machine:"+b.machine));
        const response = await stub.fetch("https://internal/internal/grant",{method:"POST",body:JSON.stringify({token})});
        if (!response.ok) return json({error:"grant_failed"},503);
        const registry = env.MACHINES.get(env.MACHINES.idFromName("registry"));
        await registry.fetch("https://internal/internal/register",{method:"POST",body:JSON.stringify({name:b.machine,stage:"bootstrapped"})});
        return json({machine:b.machine,token,endpoint:u.origin+"/agent/"+encodeURIComponent(b.machine),expires_at:Math.floor(Date.now()/1000)+7*86400});
      } catch (_) { return json({error:"unauthorized"},401); }
    }
    const match=u.pathname.match(/^\/agent\/([A-Za-z0-9-]{3,120})\/(ping|next|chunk|done|transaction|control)$/);
    if(!match || request.method!=="POST") return json({error:"not_found"},404);
    let payload;
    try {payload=await boundedJSON(request,16384);} catch (_) {return json({error:"invalid_body"},400);}
    const credential=(request.headers.get("authorization")||"").replace(/^Bearer /,"");
    return routeAgent(env,match[1],match[2],payload,credential);
  }
};

