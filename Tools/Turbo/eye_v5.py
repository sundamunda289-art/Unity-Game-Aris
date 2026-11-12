#!/usr/bin/env python3
"""AARIS AI Eyes & Hands V5: one bounded local scenario -> evidence bundle.

Runs ONLY inside the authenticated Codespace terminal. No HTTP server, arbitrary
shell, new Unity Editor, code edits, or fabricated frames. A game UI action goes
through Unity's named EventSystem bridge; OS actions are explicit fallbacks.
"""
from __future__ import annotations

import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import time
import uuid
import zipfile

import gui_v4
import unity_v4

ROOT = Path("/tmp/aaris-v5-evidence")
MAX_STEPS = 12
STEP_NAMES = {"state", "ui_tap", "ui_hold", "action", "camera", "os_click", "os_hold",
              "os_drag", "os_key", "wait", "mark"}
ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,55}$")


def validate(script, width, height):
    if not isinstance(script, list) or len(script) > MAX_STEPS:
        raise ValueError("scenario must be a bounded JSON array")
    result, delay = [], 0.0
    for item in script:
        if not isinstance(item, dict) or item.get("op") not in STEP_NAMES:
            raise ValueError("unsupported or malformed scenario step")
        op = item["op"]
        allowed = {"op"}
        if op in ("ui_tap", "ui_hold"):
            allowed.add("target")
            if item.get("target") not in unity_v4.UI_TARGETS:
                raise ValueError("invalid named UI target")
            if op == "ui_hold":
                allowed.add("seconds")
        elif op == "action":
            allowed.add("target")
            if item.get("target") not in unity_v4.ACTIONS:
                raise ValueError("invalid game action")
            if item["target"] in ("Fire", "Jump"):
                allowed.add("seconds")
        elif op == "camera":
            allowed.update(("x","y","z","yaw","pitch"))
            unity_v4.validate({"id": "check", "op": "camera",
                               **{k: item.get(k) for k in ("x","y","z","yaw","pitch")}})
        elif op == "os_click":
            allowed.update(("x", "y"))
            gui_v4.verify_sequence([{"op": "click", "x": item.get("x"), "y": item.get("y")}],width,height)
        elif op == "os_hold":
            allowed.update(("x", "y", "seconds"))
            gui_v4.verify_sequence([{"op":"mouse_hold","x":item.get("x"),
                                    "y":item.get("y"),"seconds":item.get("seconds"),
                                    "button":item.get("button",1)}],width,height)
            if "button" in item:
                allowed.add("button")
        elif op == "os_drag":
            allowed.update(("x", "y", "to_x", "to_y", "seconds"))
            gui_v4.verify_sequence([{"op": "move", "x": item.get("x"), "y": item.get("y")},
                                    {"op": "move_smooth", "x": item.get("to_x"), "y": item.get("to_y"),
                                     "seconds": item.get("seconds")}],width,height)
        elif op == "os_key":
            allowed.add("key")
            gui_v4.verify_sequence([{"op":"key_tap","key":item.get("key")}],width,height)
        elif op == "wait":
            allowed.add("seconds")
        elif op == "mark":
            allowed.add("label")
            if not isinstance(item.get("label"),str) or not ID.fullmatch(item["label"]):
                raise ValueError("invalid marker")
        if op in ("ui_hold","wait") or (op == "action" and item["target"] in ("Fire","Jump")):
            seconds=item.get("seconds")
            if type(seconds) not in (int,float) or not 0 <= seconds <= (3 if op!="wait" else 2.5):
                raise ValueError("invalid scenario duration")
            if op!="wait" and seconds < .05:
                raise ValueError("hold needs at least 0.05 seconds")
            delay += seconds
        if op in ("os_drag","os_hold"):
            delay+=item["seconds"]
        if set(item)!=allowed:
            raise ValueError("unexpected scenario arguments")
        result.append(dict(item))
    if delay > 30:
        raise ValueError("scenario duration budget exceeded")
    return result


def unity_action(step, timeout):
    op=step["op"]
    mapped={"state":"status","ui_tap":"tap_ui","ui_hold":"hold_ui"}.get(op,op)
    intent={"id":"v5_"+uuid.uuid4().hex[:24],"op":mapped}
    for key in ("target","seconds","x","y","z","yaw","pitch"):
        if key in step: intent[key]=step[key]
    before=time.monotonic()
    result=unity_v4.send(intent,unity_v4.FOLDER,timeout)
    return {"result":result.get("result"),"detail":result.get("detail",""),
            "playing":result.get("playing"),"scene":result.get("scene"),
            "frame":result.get("frame"),"playerPosition":result.get("playerPosition"),
            "stance":result.get("stance"),"weapon":result.get("weapon"),
            "magazine":result.get("magazine"),"fireHeld":result.get("fireHeld"),
            "aiming":result.get("aiming"),"consoleErrors":result.get("consoleErrors"),
            "ms":round((time.monotonic()-before)*1000,2)}


def os_action(step):
    op=step["op"]
    if op=="os_click":
        gui_v4.execute_steps([{"op":"click","x":step["x"],"y":step["y"]}],[],time.monotonic())
    elif op=="os_hold":
        gui_v4.execute_steps([{"op":"mouse_hold","x":step["x"],"y":step["y"],
                              "seconds":step["seconds"],"button":step.get("button",1)}],
                             [],time.monotonic())
    elif op=="os_key":
        gui_v4.execute_steps([{"op":"key_tap","key":step["key"]}],[],time.monotonic())
    elif op=="os_drag":
        gui_v4.invoke("xdotool","mousemove","--sync",str(step["x"]),str(step["y"]))
        gui_v4.invoke("xdotool","mousedown","1")
        try:
            gui_v4.smoothly_move(step["to_x"],step["to_y"],step["seconds"])
        finally:
            gui_v4.invoke("xdotool","mouseup","1")


def safe_unity_state():
    path=unity_v4.FOLDER/"ai_v4_result.json"
    try:
        data=json.loads(path.read_text())
        fields=("id","scene","playing","paused","frame","playerPosition","weapon",
                "stance","magazine","reserve","fireHeld","consoleErrors")
        return {"age_sec":round(time.time()-path.stat().st_mtime,1),
                **{k:data.get(k) for k in fields}}
    except (OSError, ValueError, TypeError):
        return {"unavailable":True}


def editor_health():
    import glob
    proc_ids=[]
    for proc in glob.glob("/proc/[0-9]*/cmdline"):
        try:
            data=Path(proc).read_bytes().replace(b"\x00",b" ")
            if b"/Editor/Unity" in data and b"-projectPath" in data:
                proc_ids.append(int(proc.split("/")[2]))
        except (OSError, ValueError):pass
    log=Path("/tmp/aaris-codespaces/Editor.log")
    return {"unity_processes":proc_ids[:8],
            "editor_log_age_sec":round(time.time()-log.stat().st_mtime,1) if log.exists() else None,
            "unity_result":safe_unity_state()}


def evidence(run_id, script, fps, seconds, timeout, inline_preview):
    if not ID.fullmatch(run_id) or not 1<=fps<=30 or not 1<=seconds<=30 or not 2<=timeout<=60:
        raise ValueError("invalid run limits")
    width,height=gui_v4.screen_size()
    script=validate(script,width,height)
    ROOT.mkdir(mode=0o700,parents=True,exist_ok=True)
    folder=ROOT/run_id
    folder.mkdir(mode=0o700,exist_ok=False)
    recorder=gui_v4.Recorder(folder,fps,max_frames=900)
    events=[]
    t0=time.monotonic()
    with open(gui_v4.LOCK_PATH,"a+b") as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        recorder.start()
        try:
            for index,step in enumerate(script):
                stamp=time.monotonic()-t0
                event={"step":index,"op":step["op"],"at_sec":round(stamp,3)}
                if step["op"] in ("state","ui_tap","ui_hold","action","camera"):
                    try:
                        event["telemetry"]=unity_action(step,timeout)
                        if event["telemetry"]["result"]!="ok":
                            event["error"]="UnityRejectedCommand"
                    except (RuntimeError,TimeoutError,ValueError,OSError) as ex:
                        event["error"]=type(ex).__name__+": "+str(ex)[:180]
                elif step["op"] in ("os_click","os_drag","os_key","os_hold"):
                    try:
                        os_action(step)
                    except (OSError,subprocess.SubprocessError,RuntimeError) as ex:
                        event["error"]=type(ex).__name__+": "+str(ex)[:180]
                elif step["op"]=="wait":time.sleep(step["seconds"])
                else:event["label"]=step["label"]
                file=folder/f"event_{index:02d}.jpg"
                try:
                    gui_v4.make_capture(file)
                    event["image"]=file.name
                except (OSError,subprocess.SubprocessError,RuntimeError) as ex:
                    event["capture_error"]=type(ex).__name__
                events.append(event)
                # Never continue a test sequence after an unverified state.
                if event.get("error"): break
        finally:
            remaining=seconds-(time.monotonic()-t0)
            # On cancellation or failed input, release immediately instead of
            # waiting for the full originally requested recording window.
            if remaining>0 and not any(e.get("error") for e in events):
                time.sleep(remaining)
            recorder.finish()
            fcntl.flock(lock,fcntl.LOCK_UN)
    sheet=gui_v4.compose_sheet(folder,recorder.frames)
    duration=round((time.monotonic()-t0)*1000,2)
    actual=recorder.frames
    span=actual[-1]["at_ms"]-actual[0]["at_ms"] if len(actual)>1 else 0
    manifest={"schema":"aaris-v5-vision-1","run_id":run_id,
              "source":"actual X11 screenshots, not synthetic Unity captures",
              "desktop":[width,height],"requested_fps":fps,"frames":actual,
              "actual_frames":len(actual),"frame_span_ms":round(span,2),
              "observed_capture_fps":round((len(actual)-1)*1000/span,2) if span>0 else 0,
              "events":events,"capture_errors":recorder.errors,"duration_ms":duration,
              "health":editor_health(),
              "result":"failed" if any(e.get("error") for e in events) or not actual else "captured"}
    (folder/"manifest.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2))
    with zipfile.ZipFile(folder/"replay.zip","w",zipfile.ZIP_DEFLATED,compresslevel=3) as z:
        z.write(folder/"manifest.json","manifest.json")
        if sheet:z.write(sheet,"contact_sheet.jpg")
        for f in folder.glob("frame_*.jpg"):z.write(f,"frames/"+f.name)
        for f in folder.glob("event_*.jpg"):z.write(f,"events/"+f.name)
    summary={k:v for k,v in manifest.items() if k not in ("frames","events")}
    summary.update({"bundle":str(folder/"replay.zip"),"sheet":str(sheet) if sheet else None,
                    "events":events,
                    "sha256":hashlib.sha256((folder/"replay.zip").read_bytes()).hexdigest()})
    print(json.dumps(summary,ensure_ascii=False),flush=True)
    if inline_preview and sheet and sheet.stat().st_size<=200000:
        import base64
        print("AARIS_V5_SHEET_BASE64="+base64.b64encode(sheet.read_bytes()).decode(),flush=True)
        print("AARIS_V5_SHEET_COMPLETE",flush=True)
    return 0 if manifest["result"]=="captured" else 2


def selftest():
    examples=[[{"op":"state"},{"op":"mark","label":"after"}],
              [{"op":"ui_tap","target":"HUD Crouch"},{"op":"state"}],
              [{"op":"ui_hold","target":"HUD Fire","seconds":0.6}],
              [{"op":"os_drag","x":10,"y":20,"to_x":100,"to_y":110,"seconds":.2}],
              [{"op":"os_hold","x":100,"y":200,"seconds":3}],
              [{"op":"os_hold","x":1279,"y":719,"seconds":12,"button":3}]]
    for s in examples:assert validate(s,1280,720)==s
    for bad in [[{"op":"shell","command":"rm -rf ."}],
                [{"op":"ui_tap","target":"NotButton"}],
                [{"op":"os_click","x":9999,"y":5}],
                [{"op":"wait","seconds":60}],
                [{"op":"state","extra":True}],
                [{"op":"os_hold","x":20,"y":30,"seconds":50}],
                [{"op":"os_hold","x":20,"y":30,"seconds":1,"button":8}]]:
        try:validate(bad,1280,720)
        except ValueError:continue
        raise AssertionError("unsafe scenario accepted")
    print("AARIS_V5_SELFTEST_PASS")


def main():
    p=argparse.ArgumentParser(description="AARIS V5 private local eye/hand test bundle")
    p.add_argument("--selftest",action="store_true")
    p.add_argument("--id",default="")
    p.add_argument("--script",default="[]",help="bounded JSON array; empty is a visual-only inspection")
    p.add_argument("--hold",nargs=3,type=float,metavar=("X","Y","SECONDS"),
                   help="one-step mouse long press at any screen X,Y for SECONDS")
    p.add_argument("--button",type=int,default=1,choices=(1,2,3),
                   help="mouse button to hold, default 1 (left)")
    p.add_argument("--fps",type=int,default=12)
    p.add_argument("--seconds",type=float,default=3.0)
    p.add_argument("--unity-timeout",type=float,default=15.0)
    p.add_argument("--inline-preview",action="store_true")
    args=p.parse_args()
    if args.selftest:
        selftest();return 0
    run_id=args.id or "v5_"+uuid.uuid4().hex[:14]
    if args.hold is not None:
        if args.script != "[]":
            p.error("use --hold or --script, not both")
        x,y,duration=args.hold
        if not x.is_integer() or not y.is_integer():
            p.error("X and Y must be whole-screen pixels")
        script=[{"op":"os_hold","x":int(x),"y":int(y),
                 "seconds":duration,"button":args.button}]
        seconds=max(args.seconds,duration+0.5)
    else:
        script=json.loads(args.script)
        seconds=args.seconds
    return evidence(run_id,script,args.fps,seconds,args.unity_timeout,args.inline_preview)


if __name__=="__main__":
    # Ensure SIGTERM still executes the mouse/key release finally blocks.
    def _cancel(signum, frame):
        raise RuntimeError("cancelled; release held inputs")
    signal.signal(signal.SIGTERM, _cancel)
    try:sys.exit(main())
    except (OSError,ValueError,RuntimeError,subprocess.SubprocessError,ImportError) as exc:
        print("AARIS_V5_ERROR:"+type(exc).__name__+":"+str(exc)[:180],file=sys.stderr)
        sys.exit(2)
