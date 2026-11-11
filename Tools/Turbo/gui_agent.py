#!/usr/bin/env python3
"""Private, bounded X11 screenshot/GUI actions for a trusted Aaris Turbo Codespace.

No web server or public image URL is opened. Frames are printed as JPEG base64
ONLY to the existing private Cloudflare Durable Object command-output channel.
Supported: look, init, click x y, double_click x y, right_click x y,
move x y, key KEYSYM, scroll NUMBER, type TEXT, paste TEXT.
Also: batch JSON with 1-4 validated pointer/key actions, one screenshot afterward.
Each action returns the next screenshot; never perpetual video streaming.
"""
from __future__ import annotations
import base64
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

SCREEN_W, SCREEN_H = 1280, 720
MAX_IMAGE_BYTES = 96_000
MAX_INPUT_CHARS = 2048
DISPLAY = os.environ.get("DISPLAY") or ":99"
if not re.fullmatch(r":[0-9]{1,3}", DISPLAY):
    DISPLAY = ":99"
SAFE_KEY = re.compile(r"[A-Za-z0-9_+:-]{1,64}\Z")
ACTIONS = {"look", "init", "click", "double_click", "right_click",
           "move", "key", "scroll", "type", "paste", "batch"}
BATCH_ACTIONS = {"click", "double_click", "right_click", "move", "scroll", "key"}
BATCH_LIMIT = 4


def exec_safe(args, *, timeout=12, input=None):
    return subprocess.run(args, check=True, input=input, timeout=timeout,
                          stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)


def screen_ready():
    if not shutil.which("xdotool"):
        return False
    try:
        result=exec_safe(["xdotool", "getdisplaygeometry"], timeout=4)
        return bool(re.fullmatch(rb"\d+\s+\d+\s*", result.stdout))
    except (subprocess.SubprocessError, OSError):
        return False


def spawn_bg(argv, label):
    if not shutil.which(argv[0]):
        raise RuntimeError("MISSING_" + label)
    home=Path.home()/".cache"/"aaris-turbo"
    home.mkdir(parents=True, exist_ok=True, mode=0o700)
    with open(home/(label+".log"), "ab") as log:
        subprocess.Popen(argv, stdin=subprocess.DEVNULL,
                         stdout=log, stderr=subprocess.STDOUT,
                         start_new_session=True, close_fds=True)


def ensure_screen():
    if screen_ready():
        return
    if not shutil.which("Xvfb"):
        raise RuntimeError("MISSING_XVFB")
    if not Path("/tmp/.X11-unix/X"+DISPLAY[1:]).exists():
        spawn_bg(["Xvfb", DISPLAY, "-screen", "0",
                  f"{SCREEN_W}x{SCREEN_H}x24", "-nolisten", "tcp"], "virtual-screen")
    for _ in range(30):
        if screen_ready():
            break
        time.sleep(.2)
    if not screen_ready():
        raise RuntimeError("VIRTUAL_SCREEN_NOT_READY")
    # Never kill an existing user's GUI desktop.
    try:
        subprocess.Popen(["fluxbox"],stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,
                         start_new_session=True,close_fds=True)
    except OSError:
        pass
    time.sleep(.35)
    if shutil.which("xterm"):
        try:
            subprocess.Popen(["xterm","-title","Aaris Cloud Computer",
                              "-geometry","110x32+60+50"],
                             stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL,start_new_session=True,close_fds=True)
        except OSError:
            pass
    time.sleep(.4)


def validate(action, args):
    if action == "batch":
        if len(args) != 1 or len(args[0]) > 1400:
            raise ValueError("BATCH_SIZE")
        try:
            steps = json.loads(args[0])
        except (TypeError, ValueError) as exc:
            raise ValueError("BATCH_JSON") from exc
        if not isinstance(steps, list) or not 1 <= len(steps) <= BATCH_LIMIT:
            raise ValueError("BATCH_COUNT")
        normalized = []
        for step in steps:
            if not isinstance(step, dict) or set(step.keys()) != {"action", "args"}:
                raise ValueError("BATCH_STEP_FORMAT")
            kind, values = step["action"], step["args"]
            if kind not in BATCH_ACTIONS or not isinstance(values, list) or any(
                isinstance(v, bool) or not isinstance(v, (str, int)) for v in values
            ):
                raise ValueError("BATCH_ACTION_NOT_ALLOWED")
            normalized.append((kind, validate(kind, [str(v) for v in values])))
        return normalized
    if action not in ACTIONS:
        raise ValueError("ACTION_NOT_ALLOWED")
    if action in {"click", "double_click", "right_click", "move"}:
        if len(args)!=2:
            raise ValueError("COORD_COUNT")
        x,y=(int(v) for v in args)
        if not(0<=x<SCREEN_W and 0<=y<SCREEN_H):
            raise ValueError("COORD_RANGE")
        return [x,y]
    if action=="scroll":
        if len(args)!=1 or not -12<=int(args[0])<=12:
            raise ValueError("SCROLL_RANGE")
        return [int(args[0])]
    if action=="key":
        if len(args)!=1 or not SAFE_KEY.fullmatch(args[0]):
            raise ValueError("INVALID_KEY")
        return args
    if action in {"type","paste"}:
        if len(args)!=1 or len(args[0])>MAX_INPUT_CHARS or "\x00" in args[0]:
            raise ValueError("TEXT_LIMIT")
        return args
    if args:
        raise ValueError("EXTRA_ARGS")
    return []


def map_coords(x,y):
    state=Path.home()/".cache"/"aaris-turbo"/"frame-info.json"
    try:
        data=json.loads(state.read_text(encoding="utf-8"))
        fw,fh=data["frame_width"],data["frame_height"]
        sw,sh=data["screen_width"],data["screen_height"]
        if not all(isinstance(v,int) and 100<=v<=8192 for v in (fw,fh,sw,sh)):
            raise ValueError("INVALID_FRAME_METADATA")
        if x >= fw or y >= fh:
            raise ValueError("COORD_OUTSIDE_FRAME")
        return min(sw-1,round(x*sw/fw)),min(sh-1,round(y*sh/fh))
    except FileNotFoundError:
        return x,y


def perform(action,args):
    if action == "batch":
        for kind, values in args:
            perform(kind, values)
        return
    if action in {"look","init"}:
        return
    if action in {"click","double_click","right_click","move"}:
        x,y=map_coords(*args)
        exec_safe(["xdotool","mousemove","--sync",str(x),str(y)])
        clicks={"click":["click","1"],"double_click":["click","--repeat","2","--delay","90","1"],
                "right_click":["click","3"]}
        if action!="move":
            exec_safe(["xdotool",*clicks[action]])
    elif action=="key":
        exec_safe(["xdotool","key","--clearmodifiers",args[0]])
    elif action=="scroll":
        direction="4" if args[0]>0 else "5"
        for _ in range(abs(args[0])):
            exec_safe(["xdotool","click",direction])
    elif action=="paste":
        exec_safe(["xclip","-selection","clipboard"],input=args[0].encode("utf-8"))
        exec_safe(["xdotool","key","--clearmodifiers","ctrl+v"])
    elif action=="type":
        exec_safe(["xdotool","type","--clearmodifiers","--delay","4","--",args[0]],timeout=30)


def capture_jpeg(*, fast=False):
    try:
        from PIL import Image, ImageDraw
    except ImportError as exc:
        raise RuntimeError("MISSING_PYTHON_PIL") from exc
    if not shutil.which("scrot"):
        raise RuntimeError("MISSING_SCROT")
    directory=Path.home()/".cache"/"aaris-turbo"
    directory.mkdir(parents=True,exist_ok=True,mode=0o700)
    fd,path=tempfile.mkstemp(prefix="frame-",suffix=".png",dir=directory)
    os.close(fd)
    try:
        exec_safe(["scrot","-z","-o",path],timeout=15)
        with Image.open(path) as source:
            original=source.convert("RGB")
        screen_width,screen_height=original.size
        pointer=None
        try:
            position=exec_safe(["xdotool","getmouselocation","--shell"],timeout=3).stdout.decode()
            mx=re.search(r"^X=(\d+)$",position,re.M)
            my=re.search(r"^Y=(\d+)$",position,re.M)
            if mx and my: pointer=(int(mx.group(1)),int(my.group(1)))
        except (OSError,subprocess.SubprocessError,ValueError):
            pass
        resolutions = ((800,450),(640,360),(512,288)) if fast else ((1280,720),(1024,576),(960,540),(800,450))
        byte_budget = 32_768 if fast else MAX_IMAGE_BYTES
        for max_width,max_height in resolutions:
            image=original.copy()
            image.thumbnail((max_width,max_height))
            width,height=image.size
            draw=ImageDraw.Draw(image)
            for x in range(100,width,100):
                draw.line(((x,0),(x,height)),fill=(80,145,170),width=1)
                draw.text((x+3,5),str(x),fill=(255,225,105))
            for y in range(100,height,100):
                draw.line(((0,y),(width,y)),fill=(80,145,170),width=1)
                draw.text((6,y+3),str(y),fill=(255,225,105))
            if pointer:
                x=round(pointer[0]*width/screen_width)
                y=round(pointer[1]*height/screen_height)
                draw.polygon([(x,y),(x,y+22),(x+6,y+15),(x+12,y+24),
                              (x+16,y+22),(x+10,y+13),(x+18,y+13)],
                             fill="red",outline="white")
            for quality in (68,55,42,32,24,18,12):
                result=io.BytesIO()
                image.save(result,format="JPEG",quality=quality,optimize=True)
                if result.tell()<=byte_budget:
                    state=directory/"frame-info.json"
                    tmp=directory/"frame-info.new"
                    tmp.write_text(json.dumps({"frame_width":width,
                             "frame_height":height,"screen_width":screen_width,
                             "screen_height":screen_height}),encoding="utf-8")
                    tmp.chmod(0o600)
                    tmp.replace(state)
                    return width,height,result.getvalue()
        raise RuntimeError("FRAME_EXCEEDS_BUDGET")
    finally:
        Path(path).unlink(missing_ok=True)


def selftest():
    assert validate("click",["120","350"]) == [120,350]
    assert validate("scroll",["-2"]) == [-2]
    assert validate("key",["ctrl+v"]) == ["ctrl+v"]
    for action,args in [("click",["9999","100"]),("key",[";rm -rf /"]),
                        ("paste",["a","b"]),("look",["extra"])]:
        try:
            validate(action,args)
        except ValueError:
            pass
        else:
            raise AssertionError((action,args))
    two = '[{"action":"move","args":[120,350]},{"action":"key","args":["Tab"]}]'
    assert validate("batch",[two]) == [("move",[120,350]),("key",["Tab"])]
    for invalid in ['[]', '[{"action":"type","args":["secret"]}]',
                    '[{"action":"click","args":[5000,4]}]',
                    '[{"action":"move","args":[1,1]}]'*5]:
        try:
            validate("batch",[invalid])
        except ValueError:
            pass
        else:
            raise AssertionError("BATCH_SHOULD_REJECT")
    assert MAX_IMAGE_BYTES<=96000
    assert validate("look", []) == []
    assert validate("click", ["799", "449"]) == [799,449]
    print("AARIS_GUI_SELFTEST_PASS")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return 0
    action=sys.argv[1] if len(sys.argv)>1 else "look"
    raw_args=sys.argv[2:]
    fast=bool(raw_args and raw_args[-1]=="--fast")
    if fast:
        raw_args=raw_args[:-1]
    args=validate(action,raw_args)
    os.environ["DISPLAY"]=DISPLAY
    ensure_screen()
    perform(action,args)
    time.sleep(.12 if action=="batch" else .22)
    width,height,data=capture_jpeg(fast=fast)
    print(f"AARIS_GUI_FRAME_SIZE={width}x{height}")
    print("AARIS_GUI_FRAME_MODE="+("fast" if fast else "full"))
    print("AARIS_GUI_FRAME_BASE64="+base64.b64encode(data).decode("ascii"))
    print("AARIS_GUI_FRAME_COMPLETE")
    return 0


if __name__=="__main__":
    try:
        sys.exit(main())
    except (OSError,ValueError,RuntimeError,subprocess.SubprocessError) as exc:
        # Avoid full stack traces and untrusted GUI content in error channels.
        print("AARIS_GUI_ERROR_CLASS="+type(exc).__name__,file=sys.stderr)
        sys.exit(3)
