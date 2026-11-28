// Protocol tests use real SQLite queries and fresh object instances over the
// same database. They do not call Cloudflare or execute user jobs.
import assert from "node:assert/strict";
import {DatabaseSync} from "node:sqlite";
import worker, {TurboMachine} from "./worker.mjs";

const objects=new Map();
function object(name) {
  if (!objects.has(name)) {
    const db=new DatabaseSync(":memory:");
    const context={storage:{sql:{exec(query,...bindings){return db.prepare(query).all(...bindings);}},
      transactionSync(callback){db.exec("BEGIN IMMEDIATE");try{const value=callback();db.exec("COMMIT");return value;}catch(e){db.exec("ROLLBACK");throw e;}}}};
    objects.set(name,{db,context,instance:new TurboMachine(context,{})});
  }
  return objects.get(name);
}
const env={MACHINES:{idFromName:name=>name,get:name=>({fetch:(url,init)=>object(name).instance.fetch(new Request(url,init))})},
  AARIS_DEPLOY_OPERATION:"a".repeat(32),AARIS_SOURCE_SHA:"b".repeat(40)};
const oldToken="1".repeat(64),newToken="2".repeat(64),laterToken="3".repeat(64);
const tx="a".repeat(32),laterTx="b".repeat(32),machine="codespace-first";
const sha=async value=>Buffer.from(await crypto.subtle.digest("SHA-256",new TextEncoder().encode(value))).toString("hex");
async function request(machine,token,operation,payload={}) {
  const response=await worker.fetch(new Request("https://example.workers.dev/agent/"+machine+"/"+operation,
    {method:"POST",headers:{Authorization:"Bearer "+token,"Content-Type":"application/json"},body:JSON.stringify(payload)}),env);
  return {status:response.status,body:await response.json()};
}
async function transaction(machine,token,id,operation,extra={}) {
  return request(machine,token,"transaction",{transaction_id:id,operation,...extra});
}
async function prepare(machine,token,id) {
  const registry=object("registry").instance;
  const response=await registry.fetch(new Request("https://internal/internal/active"));
  const {active,revision}=await response.json();
  return object("machine:"+machine).instance.prepareGrant({machine,token,transaction_id:id,expected_active_revision:revision,previous_active:active});
}
async function probe(machine,token,id) {
  const challenge=await transaction(machine,token,id,"probe");
  assert.equal(challenge.status,200);
  const nonce=challenge.body.nonce;
  const result=await transaction(machine,token,id,"probe-result",{nonce,exit_code:0,output:"AARIS_TRANSACTION_PROBE="+nonce+"\n"});
  assert.equal(result.status,200);
  return nonce;
}
async function executeReservedProbe(machine,token,id,nonce) {
  const next=await request(machine,token,"next");
  assert.equal(next.status,200);
  assert.equal(next.body.job.id,"txn_probe_"+id);
  assert.equal((await request(machine,token,"chunk",{id:next.body.job.id,seq:0,kind:"stdout",text:"AARIS_TRANSACTION_ACTIVE="+nonce+"\n"})).status,200);
  assert.equal((await request(machine,token,"done",{id:next.body.job.id,exit_code:0,error:""})).status,200);
}
function enqueue(machine,id) {
  object("machine:"+machine).context.storage.sql.exec("INSERT INTO commands(id,argv_json,cwd,max_seconds,expires_at,status) VALUES(?,?,?,?,unixepoch()+600,'queued')",
    id,'["pwd"]',"/workspaces/Unity-Game-Aris",30);
}

// Broker-only nonce proof touches storage but creates no machine or command.
env.BROKER_PROBE_HASH=await sha("9".repeat(64));
env.WATCH_TOKEN_HASH=await sha("8".repeat(64));
const brokerProbe=async token=>worker.fetch(new Request("https://example.workers.dev/broker/check",{
  method:"POST",headers:{Authorization:"Bearer "+token},body:JSON.stringify({nonce:"c".repeat(32)})}),env);
assert.equal((await brokerProbe("8".repeat(64))).status,401);
let response=await brokerProbe("9".repeat(64));
assert.equal(response.status,200);
let body=await response.json();
assert.equal(body.nonce,"c".repeat(32));assert.equal(body.storage_ready,true);
assert.equal(body.transaction_protocol,1);assert.equal(body.source_sha,env.AARIS_SOURCE_SHA);
assert.equal(object("registry").instance.setting("broker_probe"),null);
assert.equal((await worker.fetch(new Request("https://example.workers.dev/watch/api",{headers:{Authorization:"Bearer "+"9".repeat(64)}}),env)).status,401);

// A valid old grant and its in-flight job survive candidate preparation.
response=await object("machine:"+machine).instance.fetch(new Request("https://internal/internal/grant",{
  method:"POST",body:JSON.stringify({token:oldToken})}));
assert.equal(response.status,200);
enqueue(machine,"old_job");
assert.equal((await request(machine,oldToken,"next")).body.job.id,"old_job");
response=await prepare(machine,newToken,tx);body=await response.json();
assert.equal(body.state,"prepared");assert.equal(body.generation,1);
assert.equal((await request(machine,oldToken,"ping")).status,200);
assert.equal((await request(machine,newToken,"next")).status,401);
assert.equal((await request(machine,newToken,"ping")).status,401);
assert.equal((await prepare(machine,laterToken,tx)).status,409);
assert.equal((await prepare(machine,newToken,tx)).status,200);
assert.equal((await prepare(machine,laterToken,laterTx)).status,409);
assert.equal((await transaction(machine,newToken,tx,"commit")).status,409);
let nonce=await probe(machine,newToken,tx);
assert.equal((await transaction(machine,newToken,tx,"begin-cutover")).body.running_jobs,1);
assert.equal((await request(machine,oldToken,"next")).body.job,null);
assert.equal((await transaction(machine,newToken,tx,"commit")).status,409);
assert.equal((await request(machine,oldToken,"done",{id:"old_job",exit_code:0})).status,200);

// Object restart does not lose the prepared transaction or its old grant.
const first=object("machine:"+machine);
first.instance=new TurboMachine(first.context,{});
assert.equal((await transaction(machine,newToken,tx,"status")).body.state,"draining");
assert.equal((await request(machine,oldToken,"ping")).status,200);
enqueue(machine,"ordinary_job");
assert.equal((await transaction(machine,newToken,tx,"commit")).body.state,"committed");
assert.equal((await transaction(machine,newToken,tx,"commit")).body.generation,1);
assert.equal((await request(machine,oldToken,"next")).status,401);
assert.equal((await request(machine,newToken,"ping")).status,200);
await executeReservedProbe(machine,newToken,tx,nonce);
let status=await transaction(machine,newToken,tx,"status");
assert.equal(status.body.state,"ready");assert.equal(status.body.selected,true);
assert.equal((await request(machine,newToken,"next")).body.job.id,"ordinary_job");
await request(machine,newToken,"done",{id:"ordinary_job",exit_code:0});

// Candidate abort after drain unpauses the active grant without rotating it.
assert.equal((await prepare(machine,laterToken,laterTx)).status,200);
await probe(machine,laterToken,laterTx);
await transaction(machine,laterToken,laterTx,"begin-cutover");
assert.equal((await transaction(machine,laterToken,laterTx,"abort")).body.state,"aborted");
assert.equal((await request(machine,newToken,"ping")).status,200);
assert.equal((await request(machine,laterToken,"next")).status,401);
assert.equal(object("machine:"+machine).instance.setting("dispatch_pause"),"");

// Another machine becomes selected only after its real nonce queue succeeds.
const second="codespace-second",secondToken="4".repeat(64),secondTx="d".repeat(32);
enqueue(machine,"cross_machine_running");
assert.equal((await request(machine,newToken,"next")).body.job.id,"cross_machine_running");
assert.equal((await prepare(second,secondToken,secondTx)).status,200);
nonce=await probe(second,secondToken,secondTx);
assert.equal((await transaction(second,secondToken,secondTx,"begin-cutover")).body.running_jobs,1);
assert.equal((await transaction(second,secondToken,secondTx,"commit")).status,409);
object("machine:"+machine).context.storage.sql.exec("UPDATE commands SET started_at=unixepoch()-1000 WHERE id='cross_machine_running'");
assert.equal((await transaction(second,secondToken,secondTx,"status")).body.expired_running_jobs,1);
assert.equal(object("machine:"+machine).context.storage.sql.exec("SELECT status FROM commands WHERE id='cross_machine_running'")[0].status,"running");
object("machine:"+machine).context.storage.sql.exec("UPDATE commands SET started_at=unixepoch() WHERE id='cross_machine_running'");
enqueue(machine,"retained_old_queue");
// A request that read the registry before handoff must still hit the old DO's
// generation-bound pause. It cannot take another ordinary command.
const stale=await object("machine:"+machine).instance.fetch(new Request("https://internal/agent/next",{
  method:"POST",headers:{Authorization:"Bearer "+newToken,"x-aaris-dispatch-allowed":"1","x-aaris-active-generation":"1"},body:"{}"}));
assert.equal((await stale.json()).job,null);
assert.equal((await request(machine,newToken,"done",{id:"cross_machine_running",exit_code:0})).status,200);
await transaction(second,secondToken,secondTx,"commit");
await request(second,secondToken,"ping");
await executeReservedProbe(second,secondToken,secondTx,nonce);
assert.equal((await transaction(second,secondToken,secondTx,"status")).body.selected,true);
assert.equal((await request(machine,newToken,"next")).body.job,null);
await request(machine,newToken,"ping");
assert.equal((await transaction(second,secondToken,secondTx,"status")).body.selected,true);

// Rollback restores the prior registry target without consuming old queued work.
await transaction(second,secondToken,secondTx,"rollback-prepare");
assert.equal((await transaction(second,secondToken,secondTx,"rollback")).body.state,"rolled_back");
assert.equal((await request(second,secondToken,"next")).status,401);
assert.equal((await request(machine,newToken,"next")).body.job.id,"retained_old_queue");
await request(machine,newToken,"done",{id:"retained_old_queue",exit_code:0});

// Legacy bootstrap must not revoke a still-valid modern/legacy active token.
assert.equal((await object("machine:"+machine).instance.fetch(new Request("https://internal/internal/grant",{
  method:"POST",body:JSON.stringify({token:laterToken})}))).status,409);
console.log("AARIS_TRANSACTION_PROTOCOL_PASS: pending isolation, idempotency, drain, nonce queue, abort, restart, target pin, rollback, broker-only proof");
for (const value of objects.values()) value.db.close();
