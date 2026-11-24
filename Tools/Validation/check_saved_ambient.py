#!/usr/bin/env python3
"""Guard the native saved daylight fix without replacing LFS TerrainData."""
import re
from pathlib import Path

root=Path(__file__).resolve().parents[2]
scene_path=root/"Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"
scene=scene_path.read_text()
generator=(root/"Tools/MapBuild/world_review.py").read_text()
assert scene.startswith("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:"), "native Unity scene not present"
assert not scene.startswith("version https://git-lfs.github.com/spec/v1"), "LFS scene pointer"
match=re.search(r"(?m)^--- !u!104 &\d+\nRenderSettings:\n(.*?)(?=^--- !u!|\Z)",scene,re.S|re.M)
assert match, "native RenderSettings missing"
block=match.group(1)
assert "m_Fog: 0\n" in block, "Daylight map fog must remain disabled"
assert "m_AmbientMode: 3\n" in block, "Flat ambient not saved"
assert "m_AmbientIntensity: 1.2\n" in block, "ambient intensity mismatch"
# YAML serializes 0.70 and 1.0 as 0.7 and 1 respectively; check numbers,
# not their exact textual decimal presentation.
ambient = re.search(r"(?m)^\s*m_AmbientSkyColor:\s*\{r: ([0-9.]+), g: ([0-9.]+), b: ([0-9.]+), a: ([0-9.]+)\}\s*$", block)
assert ambient, "Native ambient sky color absent"
assert all(abs(float(value) - target) < 0.0001 for value, target
           in zip(ambient.groups(), (0.69, 0.70, 0.65, 1.0))), "Native ambient sky color mismatch"
assert "m_Sun: {fileID: 10010}" in block, "sun link unexpectedly changed"
assert "m_SkyboxMaterial: {fileID: 2100000, guid: 26f55ddef4057d341bb944e97b32d31c, type: 2}" in block
assert "d.update(m_Fog=0,m_AmbientMode=3,m_AmbientIntensity=1.2,m_AmbientSkyColor=color(.69,.70,.65)," in generator
assert 'm_Shadows' in generator, "shadow authoring unexpectedly removed"
print("AARIS_NATIVE_SAVED_AMBIENT_CONTRACT_PASS")
