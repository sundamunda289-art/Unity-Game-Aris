#!/usr/bin/env python3
"""Deterministic ORIGINAL short mobile game audio; never download third-party sounds."""
from __future__ import annotations
import hashlib
import math
import random
import struct
import sys
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEST = ROOT / "Assets/Resources/SFX"
RATE = 22050
SOUNDS = {
    "plane_engine_loop": 1.6, "jump_whoosh": .36,
    "wind_rush_loop": 1.6, "parachute_open": .65,
    "parachute_land": .30, "step_dirt": .17,
    "step_road": .16, "step_water": .24,
    "jump_start": .20, "land_thud": .27,
    "smg_shot": .22, "ar_shot": .31,
    "battle_rifle_shot": .34, "sniper_shot": .50,
    "shotgun_shot": .40, "bolt_reload": .27,
    "shell_drop": .17, "bullet_impact": .21,
    "punch_hit": .18, "grenade_pin": .17,
    "grenade_throw": .28, "grenade_bounce": .20,
    "grenade_boom": 1.05, "car_start": .85,
    "car_engine_loop": 1.6, "tire_skid": .70,
    "car_crash": .73,
}


def guid(name: str) -> str:
    return hashlib.md5(("AARIS-ORIGINAL-AUDIO-2026/" + name).encode()).hexdigest()


def generated(name: str, duration: float) -> bytes:
    count = round(RATE * duration)
    randomizer = random.Random(int(hashlib.sha256(name.encode()).hexdigest()[:16], 16))
    low = slow = 0.0
    result = []
    for i in range(count):
        t = i / RATE
        u = t / duration
        noise = randomizer.uniform(-1, 1)
        low = low * .85 + noise * .15
        slow = slow * .997 + noise * .003
        high = noise - low
        sin = lambda hz: math.sin(2 * math.pi * hz * t)
        v = 0.0
        if name == "plane_engine_loop":
            v = (sin(35)*.38 + sin(70)*.22 + sin(105)*.10 + low*.14) * (.9+.08*sin(3))
        elif name == "car_engine_loop":
            v = (sin(48)*.38 + sin(96)*.24 + sin(144)*.10 + low*.10) * (.9+.08*sin(5))
        elif name == "wind_rush_loop":
            v = (low*.53 + slow*.50 + high*.04) * (.75+.18*sin(1))
        elif name == "car_start":
            v = (sin(44)*.34 + sin(88)*.18 + low*.18) * min(1,t*5) * min(1,(duration-t)*5)
        elif name.startswith("step_"):
            weight = .72 if name == "step_water" else .90 if name == "step_road" else .65
            v = math.exp(-t*23) * (
                high*(.46 if name == "step_road" else .20) + low*weight + sin(105)*.38)
            if name == "step_water":
                v += .07*low*math.exp(-t*7)
        elif name in ("jump_start", "parachute_land", "land_thud", "punch_hit"):
            v = math.exp(-t * (17 if name == "land_thud" else 21)) * (
                sin(155 if name == "punch_hit" else 85)*.58 + low*.33 + high*.10)
        elif name in (
            "smg_shot", "ar_shot", "battle_rifle_shot", "sniper_shot",
            "shotgun_shot", "grenade_boom", "car_crash", "bullet_impact"):
            boom, crash, impact = (name == "grenade_boom",
                                  name == "car_crash", name == "bullet_impact")
            decay = 6.5 if boom else 10 if crash else 24 if impact else 10 if name == "sniper_shot" else 16
            bass = 52 if boom else 77 if crash else 130 if impact else 63 if name == "sniper_shot" else 78 if name == "shotgun_shot" else 95
            v = (high*.62 + low*.20 + sin(bass)*.45) * math.exp(-t*decay)
            v += (low*.13 + sin(bass*.6)*.07) * math.exp(-t * (4 if boom else 8))
        elif name in ("grenade_pin", "grenade_bounce", "bolt_reload", "shell_drop"):
            freq = {"grenade_pin": 1650, "bolt_reload": 870,
                    "shell_drop": 1370, "grenade_bounce": 1010}[name]
            v = (sin(freq)*.45 + sin(freq*1.36)*.20 + high*.20) * math.exp(-t*22)
        elif name in ("jump_whoosh", "parachute_open", "grenade_throw", "tire_skid"):
            gate = max(0, math.sin(math.pi*min(1,u)))
            if name == "tire_skid":
                v = (high*.40 + sin(1350+400*u)*.28) * gate
            else:
                v = (high*.20 + low*.48) * gate
        result.append(math.tanh(v*1.9) * .8)
    if name.endswith("_loop"):
        fade = min(round(RATE * .035), count // 3)
        for i in range(fade):
            alpha = (i+1)/fade
            k = count-fade+i
            result[k] = result[k]*(1-alpha) + result[i]*alpha
    peak = max(map(abs, result), default=1)
    gain = .89 / peak if peak else 0
    audio = bytearray()
    for x in result:
        audio.extend(struct.pack("<h", round(max(-1., min(1., x*gain))*32767)))
    import io
    output = io.BytesIO()
    with wave.open(output, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(audio)
    return output.getvalue()


def meta(name: str, looping: bool) -> bytes:
    text = f"""fileFormatVersion: 2
guid: {guid(name)}
AudioImporter:
  externalObjects: {{}}
  serializedVersion: 7
  defaultSettings:
    loadType: {1 if looping else 0}
    sampleRateSetting: 0
    sampleRateOverride: 44100
    compressionFormat: {1 if looping else 2}
    quality: 0.58
    conversionMode: 0
  platformSettingOverrides: {{}}
  forceToMono: 1
  normalize: 0
  preloadAudioData: 1
  loadInBackground: 0
  ambisonic: 0
  3D: 1
  userData: ''
  assetBundleName: ''
  assetBundleVariant: ''
"""
    return text.encode()


def safe_write(path: Path, data: bytes):
    if path.exists():
        if path.read_bytes() != data:
            raise RuntimeError(f"Refusing to overwrite existing edited asset: {path}")
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)


def main():
    if sys.argv[1:] == ["--check"]:
        for name in SOUNDS:
            path = DEST / (name + ".wav")
            with wave.open(str(path), "rb") as clip:
                assert clip.getnchannels() == 1 and clip.getframerate() == RATE
                assert clip.getsampwidth() == 2 and clip.getnframes() > 1000
            assert path.read_bytes() == generated(name, SOUNDS[name])
            assert (DEST / (name + ".wav.meta")).read_bytes() == meta(name, name.endswith("_loop"))
        print(f"AARIS_AUDIO_CHECK_PASS {len(SOUNDS)}")
        return
    assert not sys.argv[1:], "usage: generate_sfx.py [--check]"
    folder = f"""fileFormatVersion: 2
guid: {guid("SFX-folder")}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData: ''
  assetBundleName: ''
  assetBundleVariant: ''
""".encode()
    safe_write(DEST.with_suffix(".meta"), folder)
    for name, duration in SOUNDS.items():
        safe_write(DEST / (name + ".wav"), generated(name, duration))
        safe_write(DEST / (name + ".wav.meta"), meta(name, name.endswith("_loop")))
    print(f"AARIS_ORIGINAL_SFX_GENERATED {len(SOUNDS)} files")
    main_check = ["--check"]
    sys.argv[1:] = main_check
    main()


if __name__ == "__main__":
    main()
