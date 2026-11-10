#!/usr/bin/env python3
"""Import ONLY SHA-pinned Kenney CC0 original foley samples; no executable archive."""
import hashlib
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DIR = ROOT / "Assets/Resources/SFX/CC0"
SOURCE_COMMIT = "9662dc1bed3b06ae753045915f9accfac044c7ce"
SOURCE = "GeorgeQLe/assets-sfx-combat"
FILES = {
    "footstep_concrete_000.ogg": "163cc56468f1734fe694351e7ff984699afa8b86",
    "footstep_concrete_001.ogg": "4c62bac1fbdd2a18594139a34939eab2e1c82ba7",
    "footstep_concrete_002.ogg": "99aec9818d9da261ba1350a9b50144f7189a20e5",
    "footstep_concrete_003.ogg": "bfb613ab88407279aabcab0a963c7aaa755c508b",
    "footstep_concrete_004.ogg": "fe8d18d13d00a85ef7750caef662f83fa4a551e3",
    "footstep_grass_000.ogg": "5b5ec879a5ca457d18ad01a81af805409e35207f",
    "footstep_grass_001.ogg": "56c3a844ccaa5df7dd137141b3eee15ae66fee91",
    "footstep_grass_002.ogg": "aa0b4148594497b2f83e6cf89cebf0efc2c55444",
    "footstep_grass_003.ogg": "fe9e119a60d8889328d997205ff1c2478503925d",
    "footstep_grass_004.ogg": "0dcff840b0dcb07e27586672a0bcdebe0e8f0a38",
    "impactMetal_light_000.ogg": "7d49262cc9f69eb517cf644f61940ace16714f22",
    "impactPunch_medium_000.ogg": "d8e0b890ae019533ec190dd2d1decce249ea556e",
    "impactPunch_medium_001.ogg": "33080462eeb8bc99025e669abfef610a6e630815",
    "impactPunch_medium_002.ogg": "9704fd215d8122bc36dc6cead55cfa620fae276e",
    "impactSoft_heavy_000.ogg": "1c321ef698c367144d0d55b52d7a680a4b8e5958",
}


def check_blob(name, data):
    # GitHub's Git blob SHA matches Git's canonical "blob <len>\\0" + bytes.
    header = b"blob " + str(len(data)).encode("ascii") + b"\x00"
    actual = hashlib.sha1(header + data).hexdigest()
    expected = FILES[name]
    if actual != expected or len(data) > 500_000 or not data.startswith(b"OggS"):
        raise ValueError("INVALID_UPSTREAM_AUDIO_" + name)


def meta(name):
    guid = hashlib.md5(("AARIS_KENNEY_CC0_" + name).encode()).hexdigest()
    return f"""fileFormatVersion: 2
guid: {guid}
AudioImporter:
  externalObjects: {{}}
  serializedVersion: 7
  defaultSettings:
    loadType: 0
    sampleRateSetting: 0
    sampleRateOverride: 44100
    compressionFormat: 1
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
""".encode()


def safe_write(path, raw):
    if path.exists():
        if path.read_bytes() != raw:
            raise ValueError("REFUSING_TO_OVERWRITE_EDITED_FILE_" + path.name)
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(raw)


def run(check_only):
    if not check_only:
        folder = f"""fileFormatVersion: 2
guid: {hashlib.md5(b"AARIS_KENNEY_CC0_DIR").hexdigest()}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData: ''
  assetBundleName: ''
  assetBundleVariant: ''
""".encode()
        safe_write(Path(str(DIR) + ".meta"), folder)
    else:
        assert Path(str(DIR) + ".meta").is_file()
    for name in FILES:
        path = DIR / name
        if check_only:
            raw = path.read_bytes()
        elif path.exists():
            raw = path.read_bytes()
        else:
            url = (f"https://raw.githubusercontent.com/{SOURCE}/{SOURCE_COMMIT}/"
                   f"assets/kenney/impact-sounds/Audio/{name}")
            request = urllib.request.Request(url, headers={"User-Agent": "Aaris-audio-CC0-import/1"})
            with urllib.request.urlopen(request, timeout=25) as response:
                raw = response.read(500_001)
        check_blob(name, raw)
        if not check_only:
            safe_write(path, raw)
            safe_write(Path(str(path) + ".meta"), meta(name))
        elif Path(str(path) + ".meta").read_bytes() != meta(name):
            raise ValueError("META_CHANGED_" + name)
    print("AARIS_VERIFIED_KENNEY_CC0_FOLEY_" + str(len(FILES)))


if __name__ == "__main__":
    run(check_only=(sys.argv[1:] == ["--check"]))
