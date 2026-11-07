#!/usr/bin/env python3
"""Strict, license-independent AI request validator for owner-only GitHub Issue commands.

MAP {"op":"find","target":"Tree"}
MAP {"op":"inspect","target":"Root/Tree"}
MAP {"op":"blueprint","target":"Root/Tree","samples":512}
MAP {"op":"photo","target":"Root/Tree"}
MAP {"op":"move","target":"Root/Tree","x":2,"y":0,"z":-3}
MAP {"op":"scale","target":"Root/House/Wall","x":1.2,"y":1,"z":1}
MAP {"op":"tree_scale","terrain":"Terrain","index":12,"width":1.2,"height":1}
"""
import json
import math
import re

OPS = {
    "find": ("target",), "nearby": ("x", "y", "z", "radius"),
    "inspect": ("target",), "blueprint": ("target",), "photo": ("target",),
    "move": ("target", "x", "y", "z"),
    "rotate": ("target", "x", "y", "z"),
    "scale": ("target", "x", "y", "z"),
    "tree_nearby": ("terrain", "x", "y", "z", "radius"),
    "tree_inspect": ("terrain", "index"),
    "tree_blueprint": ("terrain", "index"),
    "tree_photo": ("terrain", "index"),
    "tree_move": ("terrain", "index", "x", "z"),
    "tree_scale": ("terrain", "index", "width", "height"),
}
OPTIONAL = {"blueprint": {"samples"}, "tree_blueprint": {"samples"}}
EDIT_OPS = {"move", "rotate", "scale", "tree_move", "tree_scale"}

def validate(data):
    if not isinstance(data, dict) or type(data.get("op")) is not str:
        raise ValueError("Expected JSON object and an op")
    op = data["op"]
    if op not in OPS:
        raise ValueError("Unsupported operation")
    required = set(OPS[op])
    actual = set(data) - {"op"}
    if not required <= actual or not actual <= required | OPTIONAL.get(op, set()):
        raise ValueError("Missing or unexpected fields")
    out = {"op": op}
    for key in actual:
        value = data[key]
        if key in ("target", "terrain"):
            if (not isinstance(value, str) or not 1 <= len(value) <= 200
                    or ".." in value or value.startswith("/")
                    or any(ord(c) < 32 or ord(c) == 127 for c in value)):
                raise ValueError("Unsafe object name/path")
        elif key in ("index", "samples"):
            if type(value) is not int or not 0 <= value <= (8192 if key == "samples" else 1000000):
                raise ValueError("Invalid index/samples")
        else:
            if type(value) not in (int, float) or not math.isfinite(value):
                raise ValueError("Coordinates/factors must be finite numbers")
            low, high = (-2000, 2000)
            if key == "radius": low, high = 0.1, 250
            if key in ("width", "height"): low, high = 0.1, 10
            if op == "scale": low, high = 0.05, 10
            if op in ("move", "tree_move"): low, high = -100, 100
            if op == "rotate": low, high = -360, 360
            if not low <= value <= high:
                raise ValueError("Out-of-range map value: " + key)
        out[key] = value
    return out

def parse(body):
    if not isinstance(body, str) or not body.startswith("MAP ") or len(body) > 1504:
        raise ValueError("Use MAP followed by a small JSON object")
    return validate(json.loads(body[4:], parse_constant=lambda _: (_ for _ in ()).throw(ValueError("Non-finite JSON"))))

def selftest():
    a=parse('MAP {"op":"move","target":"House/Wall","x":2,"y":0,"z":-3}')
    assert a["x"] == 2
    assert validate({"op":"tree_scale","terrain":"Terrain","index":42,"width":1.2,"height":0.9})
    assert validate({"op":"blueprint","target":"Tree","samples":8192})
    assert validate({"op":"tree_photo","terrain":"Terrain","index":10})
    assert validate({"op":"tree_nearby","terrain":"Terrain","x":-420,"y":20,"z":-210,"radius":40})
    bad=[
        'MAP {"op":"move","target":"Tree","x":200,"y":0,"z":0}',
        'MAP {"op":"move","target":"Tree","x":true,"y":0,"z":0}',
        'MAP {"op":"move","target":"../Tree","x":1,"y":0,"z":0}',
        'MAP {"op":"find","target":"Tree","shell":"ls"}',
        'MAP {"op":"photo","target":"Tree","nonce":1}',
        'MAP {"op":"scale","target":"Tree","x":0,"y":1,"z":1}',
        'MAP {"op":"nearby","x":NaN,"y":0,"z":0,"radius":50}',
        'MAP {"op":"tree_inspect","terrain":"Terrain","index":-1}',
        'MAP {"op":"blueprint","target":"Tree","samples":8193}',
    ]
    for raw in bad:
        try: parse(raw)
        except ValueError: pass
        else: raise AssertionError("Unsafe MAP request accepted: " + raw)
    print("Aaris AI command validation: PASS")

if __name__=="__main__":
    selftest()
