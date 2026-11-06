#!/usr/bin/env python3
"""Local camera-intent API for Cloudflare email-protected Unity desktop sessions.

The server binds only to 127.0.0.1. Cloudflare's --allowed-mail must be active.
No authentication tokens are accepted or stored here; no arbitrary shell commands.
"""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import json
import math
import os
import time
import socket
import base64

ROOT = Path(__file__).resolve().parents[2]
STATE = ROOT / "ci-live-control"
STATE.mkdir(parents=True, exist_ok=True)

HTML = """<!doctype html><html lang="en"><meta name="viewport"
content="width=device-width, initial-scale=1"><title>Aaris Unity Live</title>
<style>body{background:#0a1b26;color:#edf9ff;font-family:system-ui;margin:12px}
button,input{font-size:16px;padding:9px;margin:3px;max-width:115px}
button{background:#0e8573;color:white;border:0;border-radius:6px}
label{display:inline-block}iframe{width:100%;height:68vh;border:1px solid #477;
border-radius:8px}small{color:#b7d4dd}#result{white-space:pre-wrap}</style>
<h2>Aaris Unity live camera</h2><small>Real editor if licensed; temporary GitHub runner session.
Camera coordinates in Unity metres. Access limited to approved email via Cloudflare.</small>
<div><label>X <input id="x" type="number" value="-420"></label>
<label>Y <input id="y" type="number" value="22"></label>
<label>Z <input id="z" type="number" value="-210"></label>
<label>Yaw <input id="yaw" type="number" value="0"></label>
<label>Pitch <input id="pitch" type="number" value="20"></label>
<label>Distance <input id="distance" type="number" value="22"></label>
<button onclick="go()">Move + photograph</button></div>
<section style="border:1px solid #456;padding:12px;border-radius:8px;margin:12px 0">
<strong>Unity Personal activation</strong>
<p id="licstate">Checking authorized license state...</p>
<a id="authorize" href="#" target="_blank" rel="noopener noreferrer"
style="display:none;color:#6fead7;font-size:18px">Sign in to Unity securely</a>
<p><small>Only Unity's official sign-in URL is shown, inside the email-PIN protected Cloudflare session.
Never post this link in GitHub public issues or logs.</small></p>
<p id="licexport">Checking for a reusable Unity ULF...</p>
<a id="exportulf" href="/api/license/ulf" download="Unity_lic.ulf"
style="display:none;color:#6fead7">Download Unity license once for GitHub Secret</a>
</section>
<div style="margin:8px 0"><button onclick="licenseStatus()">Retry license status</button>
<a href="/vnc/vnc.html?autoconnect=true&resize=remote&path=/vnc/websockify"
target="_blank" rel="noopener" style="color:#6fead7">Open full-screen noVNC</a></div>
<p id="connection">Testing VNC/WebSocket...</p>
<p id="result">Opening session...</p>
<iframe src="/vnc/vnc.html?autoconnect=true&resize=remote&path=/vnc/websockify"></iframe>
<section style="border:1px solid #456;padding:12px;border-radius:8px;margin:12px 0">
<strong>AI World Agent / 3D outline</strong>
<p><small>Issue #77 controls edits. Live page shows the last real Unity Editor result; unlicensed sessions have no 3D snapshot.</small></p>
<a href="/api/agent/report" target="_blank" style="color:#6fead7">AI-readable scene report (JSON)</a> |
<a href="/api/agent/photo" target="_blank" style="color:#6fead7">Latest AI object photo (PNG)</a>
<div><label>Orbit <input id="orbit" type="range" min="0" max="360" value="30" oninput="drawCloud()"></label>
<button onclick="loadCloud()">Draw mesh exterior dots</button></div>
<canvas id="cloud" width="850" height="420" style="width:100%;max-width:850px;background:#142636;border-radius:8px"></canvas>
<p id="cloudstate">No 3D sample loaded.</p>
</section>
<p><a style="color:#6fead7" href="/api/photo" target="_blank">Latest genuine Unity PNG</a></p>
<script>
async function go(){let data={};for(let k of ['x','y','z','yaw','pitch','distance'])
data[k]=Number(document.getElementById(k).value);
try{let r=await fetch('/api/camera',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json'},
body:JSON.stringify(data)});
if(!r.headers.get('content-type')?.includes('application/json'))
throw Error('Cloudflare authentication expired (HTTP '+r.status+'). Reload and enter your email PIN.');
let answer=await r.json();document.getElementById('result').textContent=JSON.stringify(answer);
}catch(e){document.getElementById('result').textContent=String(e);}}
async function status(){try{let r=await fetch('/api/status',{credentials:'same-origin',cache:'no-store'});
if(!r.headers.get('content-type')?.includes('application/json'))return;
document.getElementById('result').textContent=JSON.stringify(await r.json());}catch(e){}}
async function licenseStatus(){let message;try{
let r=await fetch('/api/license',{credentials:'same-origin',cache:'no-store'});
if(r.status===401||r.status===403)throw Error('Cloudflare email PIN expired. Reload this page and sign in again.');
if(!r.headers.get('content-type')?.includes('application/json'))
throw Error('License endpoint returned HTTP '+r.status+' (not JSON); check your PIN session.');
let d=await r.json();message=(d.state||'unknown')+(d.detail?' — '+d.detail:'');
const reuse=d.reuse_state||'not_yet_available';
document.getElementById('licexport').textContent='Reusable .ulf: '+reuse;
document.getElementById('exportulf').style.display=
(['download_ready','unity_license_saved_in_github_secret',
'github_secret_write_failed_manual_download_available'].includes(reuse))?'inline':'none';
let a=document.getElementById('authorize');
if(d.auth_url){a.href=d.auth_url;a.style.display='inline';}
else {a.style.display='none';a.removeAttribute('href');}
}catch(e){message='License status temporarily unavailable: '+e.message;}
document.getElementById('licstate').textContent=message;
}
async function checkConnection(){
try{
let r=await fetch('/api/health',{credentials:'same-origin',cache:'no-store'});
if(!r.ok)throw Error('HTTP '+r.status);
let h=await r.json();
document.getElementById('connection').textContent=
'VNC backend: '+(h.vnc_backend?'connected':'offline')+
' | WebSocket: '+(h.vnc_proxy?'working':'offline')+
' | Unity container: '+h.unity_container;
}catch(e){document.getElementById('connection').textContent='Health check unavailable: '+e.message;}
}
setInterval(checkConnection,8000);checkConnection();

let cloudDots=[],cloudCenter=[0,0,0];
async function loadCloud(){
 try{
  const r=await fetch('/api/agent/report',{credentials:'same-origin',cache:'no-store'});
  if(!r.ok)throw Error('No licensed Unity Agent result: HTTP '+r.status);
  const data=await r.json();
  if(data.state!=='ok')throw Error(data.note||'Unity agent not ready');
  cloudDots=(data.outer_surface_points_m||[]).map(p=>[p.x,p.y,p.z]);
  cloudCenter=data.selected?[data.selected.bounds_center_m.x,
   data.selected.bounds_center_m.y,data.selected.bounds_center_m.z]:[0,0,0];
  document.getElementById('cloudstate').textContent=
   cloudDots.length+' external mesh dots in Unity metres. '+(data.note||'');
  drawCloud();
 }catch(e){document.getElementById('cloudstate').textContent=String(e);}
}
function drawCloud(){
 const canvas=document.getElementById('cloud'), ctx=canvas.getContext('2d');
 ctx.clearRect(0,0,canvas.width,canvas.height);
 ctx.fillStyle='#bed8e2';ctx.font='17px system-ui';
 ctx.fillText('AI 3D exterior blueprint / mesh vertices',16,26);
 if(!cloudDots.length)return;
 let a=Number(document.getElementById('orbit').value)*Math.PI/180;
 const cos=Math.cos(a),sin=Math.sin(a),tilt=.35;
 const points=cloudDots.map(p=>{
  const x=p[0]-cloudCenter[0],y=p[1]-cloudCenter[1],z=p[2]-cloudCenter[2];
  const rx=cos*x-sin*z,rz=sin*x+cos*z;
  return [rx, .94*y-.34*rz, rz];
 });
 const maxR=Math.max(1,...points.map(p=>Math.hypot(p[0],p[1])));
 const zoom=Math.min(canvas.width*.43,canvas.height*.42)/maxR;
 ctx.strokeStyle='#3e6c7b';
 ctx.beginPath();ctx.moveTo(0,210);ctx.lineTo(850,210);
 ctx.moveTo(425,30);ctx.lineTo(425,420);ctx.stroke();
 points.sort((a,b)=>a[2]-b[2]);
 for(const p of points){const x=425+p[0]*zoom,y=210-p[1]*zoom;
  ctx.beginPath();ctx.arc(x,y,2.6,0,Math.PI*2);
  ctx.fillStyle='#60e0cc';ctx.fill();
 }
}

setInterval(status,7000);status();
setInterval(licenseStatus,3000);licenseStatus();
</script></html>"""

def validate(data):
    allowed = {"x": (-1000, 1000), "y": (-500, 4000),
               "z": (-1000, 1000), "yaw": (-360, 360),
               "pitch": (-85, 85), "distance": (2, 1000)}
    if not isinstance(data, dict) or any(k not in allowed for k in data):
        raise ValueError("Unknown camera field")
    clean = {}
    for key, (lo, hi) in allowed.items():
        val = data.get(key)
        if isinstance(val, bool) or not isinstance(val, (int, float)) or not math.isfinite(val) or not lo <= val <= hi:
            raise ValueError("Invalid camera " + key)
        clean[key] = float(val)
    clean["nonce"] = int(time.time_ns() % 9000000000000000)
    return clean

def host_service_health():
    # Lightweight, read-only checks; do not expose private OAuth URLs.
    out = {"cloudflare_origin": "reachable", "vnc_backend": False,
           "vnc_proxy": False, "unity_container": "unknown"}
    try:
        with socket.create_connection(("127.0.0.1", 5900), timeout=0.8) as sock:
            sock.settimeout(0.8)
            out["vnc_backend"] = sock.recv(12).startswith(b"RFB ")
    except (OSError, TimeoutError):
        pass
    try:
        with socket.create_connection(("127.0.0.1", 8080), timeout=1) as sock:
            key = base64.b64encode(os.urandom(16)).decode("ascii")
            handshake = (
                "GET /vnc/websockify HTTP/1.1\\r\\n"
                "Host: localhost\\r\\n"
                "Upgrade: websocket\\r\\nConnection: Upgrade\\r\\n"
                "Sec-WebSocket-Version: 13\\r\\n"
                f"Sec-WebSocket-Key: {key}\\r\\n\\r\\n"
            ).replace("\\r", "\r").replace("\\n", "\n")
            sock.settimeout(1)
            sock.sendall(handshake.encode("ascii"))
            out["vnc_proxy"] = b" 101 " in sock.recv(512)
    except (OSError, TimeoutError):
        pass
    try:
        from subprocess import run, DEVNULL
        proc = run(["docker", "inspect", "--format", "{{.State.Running}}",
                    "aaris-unity-live"], stdout=-1, stderr=DEVNULL,
                   text=True, timeout=2, check=False)
        out["unity_container"] = "running" if proc.returncode == 0 and proc.stdout.strip() == "true" else "not_running"
    except (OSError, TimeoutError):
        out["unity_container"] = "unknown"
    return out

class Handler(BaseHTTPRequestHandler):
    def send(self, code, body, mimetype="application/json"):
        if isinstance(body, (dict, list)):
            body = json.dumps(body).encode()
        elif isinstance(body, str):
            body = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", mimetype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy", "default-src 'self' 'unsafe-inline' data: blob:; connect-src 'self' wss:; frame-src 'self'")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = self.path.split("?")[0]
        if path == "/":
            return self.send(200, HTML, "text/html; charset=utf-8")
        if path == "/api/health":
            return self.send(200, host_service_health())
        if path == "/api/license":
            status = STATE / "license-status.json"
            try:
                info = json.loads(status.read_text()) if status.exists() else {"state": "awaiting_bootstrap"}
                export_status = STATE / "license-export-status.json"
                if export_status.exists():
                    try:
                        info["reuse_state"] = json.loads(export_status.read_text()).get("state", "unknown")
                    except (OSError, json.JSONDecodeError):
                        info["reuse_state"] = "unknown"
                return self.send(200, {key: info[key] for key in ("state", "detail", "auth_url", "reuse_state") if key in info})
            except (OSError, json.JSONDecodeError):
                return self.send(503, {"state": "license_status_unavailable"})
        if path == "/api/license/ulf":
            # This route is ONLY reachable through Cloudflare's email-PIN gate.
            # No ULF is ever sent to a public Issue, GitHub Release or cache.
            ulf = STATE / "Unity_lic.ulf"
            if not ulf.is_file():
                return self.send(404, {"error": "No reusable Unity license file created on this runner"})
            try:
                from license_reuse import check_license
                data = check_license(ulf.read_bytes())
            except (OSError, ValueError):
                return self.send(404, {"error": "Unity license is not ready"})
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Disposition", 'attachment; filename="Unity_lic.ulf"')
            self.send_header("Cache-Control", "no-store, private")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        if path == "/api/status":
            status = STATE / "status.json"
            data = json.loads(status.read_text()) if status.exists() else {"state": "Waiting for Unity Editor camera bridge"}
            return self.send(200, data)
        if path == "/api/agent/report":
            target = STATE / "agent-report.json"
            if not target.is_file():
                return self.send(404, {"error": "Real Unity AI scene report not ready"})
            try:
                if target.stat().st_size > 250000:
                    raise ValueError("Agent result too large")
                report = json.loads(target.read_text())
                if report.get("source") != "licensed_unity_editor_scene":
                    raise ValueError("Not a genuine Unity Editor report")
                return self.send(200, report)
            except (OSError, ValueError):
                return self.send(503, {"error": "Unity agent report invalid"})
        if path == "/api/agent/photo":
            target = STATE / "agent-latest.png"
            if not target.is_file():
                return self.send(404, {"error": "Real Unity object photo not ready"})
            data = target.read_bytes()
            if len(data) < 5000 or not data.startswith(bytes.fromhex("89504e470d0a1a0a")):
                return self.send(503, {"error": "Unity object PNG invalid"})
            return self.send(200, data, "image/png")
        if path == "/api/photo":
            photo = STATE / "latest.png"
            if not photo.exists():
                return self.send(404, {"error": "Unity has not captured a real image yet"})
            return self.send(200, photo.read_bytes(), "image/png")
        return self.send(404, {"error": "Unknown endpoint"})

    def do_POST(self):
        if self.path != "/api/camera":
            return self.send(404, {"error": "Unknown endpoint"})
        if self.headers.get("Content-Type","").split(";")[0] != "application/json":
            return self.send(415, {"error": "Only JSON camera intents accepted"})
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > 2048:
                raise ValueError("Camera payload too large/empty")
            request = validate(json.loads(self.rfile.read(length)))
            tmp = STATE / "camera.tmp"
            tmp.write_text(json.dumps(request))
            os.replace(tmp, STATE / "camera.json")
            return self.send(202, {"queued": True, "nonce": request["nonce"]})
        except (ValueError, KeyError, TypeError, json.JSONDecodeError) as exc:
            return self.send(400, {"error": str(exc)})

if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 8765), Handler).serve_forever()
