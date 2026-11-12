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
    "plane_engine_loop": 1.8, "jump_whoosh": .40,
    "wind_rush_loop": 1.8, "canopy_glide_loop": 1.9, "parachute_open": .74,
    "parachute_land": .46, "step_dirt": .17,
    "step_road": .16, "step_water": .24,
    "jump_start": .24, "land_thud": .34,
    "smg_shot": .22, "ar_shot": .31,
    "battle_rifle_shot": .34, "sniper_shot": .50,
    "shotgun_shot": .40, "bolt_reload": .27,
    "shell_drop": .17, "bullet_impact": .21,
    "punch_hit": .18, "grenade_pin": .17,
    "grenade_throw": .28, "grenade_bounce": .20,
    "grenade_boom": 1.05, "car_start": .85,
    "car_engine_loop": 1.6, "tire_skid": .70,
    "car_crash": .73, "gloo_deploy": .42,
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
            # Propeller harmonics above the bass band survive small speakers.
            motor = sin(42)*.26 + sin(84)*.21 + sin(168)*.17 + sin(252)*.08
            prop = sin(126)*.14 + sin(210)*.09 + low*.17 + high*.025
            v = motor * (.88 + .09*sin(3.1)) + prop * (.79 + .16*sin(5.7))
        elif name == "car_engine_loop":
            v = (sin(48)*.38 + sin(96)*.24 + sin(144)*.10 + low*.10) * (.9+.08*sin(5))
        elif name == "wind_rush_loop":
            v = (low*.55 + slow*.32 + high*.065) * (.78+.14*sin(1.3)+.045*sin(4.4))
        elif name == "canopy_glide_loop":
            flutter = (.55 + .23*sin(6.8) + .14*sin(10.7)) ** 2
            v = (low*.52 + high*.21) * flutter + slow*.20
        elif name == "car_start":
            v = (sin(44)*.34 + sin(88)*.18 + low*.18) * min(1,t*5) * min(1,(duration-t)*5)
        elif name.startswith("step_"):
            weight = .72 if name == "step_water" else .90 if name == "step_road" else .65
            v = math.exp(-t*23) * (
                high*(.46 if name == "step_road" else .20) + low*weight + sin(105)*.38)
            if name == "step_water":
                v += .07*low*math.exp(-t*7)
        elif name == "jump_start":
            push = min(1, t*210) * math.exp(-t*23)
            cloth = max(0, math.sin(math.pi*u)) * math.exp(-t*8)
            v = (sin(145)*.43 + sin(290)*.17 + low*.32 + high*.15) * push
            v += (low*.27 + high*.20) * cloth
        elif name in ("land_thud", "parachute_land"):
            aerial = name == "parachute_land"
            contact = min(1, t*260) * math.exp(-t*(13 if aerial else 17))
            v = (sin(95)*.42 + sin(190)*.22 + low*.36 + high*.19) * contact
            tail = max(0, t-(.09 if aerial else .045))
            v += (low*.25 + high*(.28 if aerial else .17)) * min(1, tail*48) * math.exp(-tail*13)
        elif name == "punch_hit":
            v = math.exp(-t * 21) * (sin(155)*.58 + low*.33 + high*.10)
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
        elif name == "gloo_deploy":
            strike = (sin(165) * .48 + high * .38) * math.exp(-t * 19)
            ice = (sin(1130 + 580 * u) * .18 + high * .23) * math.exp(-t * 9)
            rise = low * .28 * math.sin(math.pi * u) * (1 - u)
            v = strike + ice + rise
        elif name == "jump_whoosh":
            gate = max(0, math.sin(math.pi*u))
            gear = min(1, t*260) * math.exp(-t*38)
            v = (high*.24 + low*.58) * gate + (sin(410)*.26 + high*.18) * gear
        elif name == "parachute_open":
            pull = max(0, t-.055)
            snap = min(1, pull*270) * math.exp(-pull*22)
            fill = max(0, math.sin(math.pi*u)) * math.exp(-u*1.15)
            strap = max(0, t-.19)
            settle = min(1, strap*130) * math.exp(-strap*19)
            v = (sin(175)*.25 + sin(350)*.19 + high*.43) * snap
            v += (low*.54 + high*.26) * fill + (low*.15 + sin(690)*.12) * settle
        elif name in ("grenade_throw", "tire_skid"):
            gate = max(0, math.sin(math.pi*min(1,u)))
            if name == "tire_skid":
                v = (high*.40 + sin(1350+400*u)*.28) * gate
            else:
                v = (high*.20 + low*.48) * gate
        result.append(math.tanh(v*1.9) * .8)
    if name.endswith("_loop"):
        fade = min(round(RATE * .035), count // 3)
        # Overlap the tail with the head, then remove the overlapping tail.
        # The wrap now joins adjacent samples instead of jumping back twice.
        for i in range(fade):
            alpha = i / (fade - 1)
            result[i] = result[count-fade+i]*(1-alpha) + result[i]*alpha
        del result[-fade:]
    elif name in ("jump_start", "land_thud", "jump_whoosh", "parachute_open", "parachute_land"):
        fade = min(round(RATE * .012), count // 3)
        for i in range(fade):
            result[-1-i] *= i / (fade - 1)
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
