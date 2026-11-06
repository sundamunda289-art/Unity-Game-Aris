#!/usr/bin/env python3
"""Strict, owner-only issue command contract for the live Unity AI workbench.

The GitHub issue relay owns authorization. This module ONLY validates intent.
There is no shell, dynamic code, file path, or URL command capability.
"""
import json
import math
import re

OPS = {
    "scan": {"x", "z", "radius"},
    "inspect": {"target"},
    "blueprint": {"target"},
    "photo": {"target"},
    "move": {"target", "x", "y", "z"},
    "resize": {"target", "width", "height", "depth"},
    "tree": {"target", "index", "width", "height"},
}
TARGET = re.compile(r"^[A-Za-z0-9_ .:/()-]{1,120}$")

def parse_ai(body):
    if not isinstance(body, str) or not body.startswith("AI ") or len(body) > 1500:
        raise ValueError("Expected AI followed by a small JSON object")
    def no_duplicates(pairs):
        result = {}
        for k, v in pairs:
            if k in result:
                raise ValueError("Repeated JSON property")
            result[k] = v
        return result
    try:
        obj = json.loads(body[3:], object_pairs_hook=no_duplicates,
                         parse_constant=lambda _: (_ for _ in ()).throw(ValueError("Non-finite number")))
    except (json.JSONDecodeError, TypeError) as exc:
        raise ValueError("Malformed AI JSON") from exc
    if not isinstance(obj, dict) or obj.get("op") not in OPS:
        raise ValueError("Unknown AI operation")
    op = obj["op"]
    allowed = OPS[op] | {"op"}
    if set(obj) != allowed:
        raise ValueError("Missing or unknown AI fields for " + op)
    if "target" in obj:
        t = obj["target"]
        if not isinstance(t, str) or not TARGET.fullmatch(t) or ".." in t or t.strip() != t:
            raise ValueError("Invalid target. Use an exact scene object name or hierarchy path")
    bounds = {"x": (-1000, 1000), "y": (-100, 100), "z": (-1000, 1000),
              "radius": (1, 400), "width": (0.05, 200),
              "height": (0.05, 200), "depth": (0.05, 200)}
    for key in allowed - {"op", "target", "index"}:
        v = obj[key]
        lo, hi = bounds[key]
        if type(v) not in (int, float) or not math.isfinite(v) or not lo <= v <= hi:
            raise ValueError("Invalid bounded numeric " + key)
    if op == "move":
        for key in ("x", "y", "z"):
            if abs(obj[key]) > 100:
                raise ValueError("Move is a relative nudge, max 100m per axis")
    if op == "tree":
        if type(obj["index"]) is not int or not 0 <= obj["index"] <= 1000000:
            raise ValueError("Invalid native tree index")
        if obj["width"] > 10 or obj["height"] > 10:
            raise ValueError("Native tree scale must be <= 10")
    return obj

def selftest():
    good = [
        'AI {"op":"scan","x":0,"z":0,"radius":60}',
        'AI {"op":"inspect","target":"Village/House_2"}',
        'AI {"op":"move","target":"Tree_12","x":-2,"y":0,"z":3}',
        'AI {"op":"resize","target":"House/Wall","width":4,"height":3,"depth":0.4}',
        'AI {"op":"tree","target":"Terrain_0","index":4,"width":1.3,"height":0.9}',
        'AI {"op":"blueprint","target":"PineTree"}',
        'AI {"op":"photo","target":"VillageHouse"}'
    ]
    for item in good:
        assert parse_ai(item)["op"] in OPS
    unsafe = [
        'AI {"op":"shell","command":"rm -rf /"}',
        'AI {"op":"move","target":"x","x":999,"y":0,"z":0}',
        'AI {"op":"inspect","target":"../../etc/passwd"}',
        'AI {"op":"scan","x":NaN,"z":0,"radius":1}',
        'AI {"op":"photo","target":"x","shell":"oops"}',
        'AI {"op":"inspect","target":"a","target":"b"}',
        'AI {"op":"tree","target":"x","index":-1,"width":1,"height":1}',
        'AI {"op":"resize","target":"x","width":0,"height":1,"depth":1}'
    ]
    for item in unsafe:
        try:
            parse_ai(item)
        except ValueError:
            pass
        else:
            raise AssertionError("Unsafe intent accepted: " + item)
    print("AI workbench intents: strict parser PASS")

if __name__ == "__main__":
    selftest()
