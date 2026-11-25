#!/usr/bin/env python3
"""Source-level invariants for the lightweight native foliage upgrade.

This check is not a substitute for Unity compilation or screenshot comparison.
Run: python3 Tools/Validation/test_mobile_foliage_illusion_contract.py
"""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FOLIAGE = ROOT / "Assets/TerrainDemoScene_URP/ShaderGraphs"
TREE = ROOT / "Assets/TerrainDemoScene_URP/Prefabs/Trees"
INTERACT_GUID = "2c102aa2b5584f61bc013ccbc09d85c7"


def check_graph():
    source = (FOLIAGE / "TerrainGrass.shadergraph").read_text()
    blocks = [json.loads(chunk) for chunk in re.split(r"\n\s*\n(?=\{)", source)]
    nodes = {b["m_ObjectId"]: b for b in blocks if "m_ObjectId" in b}
    root = blocks[0]
    interaction = [
        b for b in blocks
        if b.get("m_Type") == "UnityEditor.ShaderGraph.CustomFunctionNode"
        and b.get("m_FunctionName") == "AarisGrassInteraction"
    ]
    assert len(interaction) == 1
    assert interaction[0]["m_FunctionSource"] == INTERACT_GUID
    node_id = interaction[0]["m_ObjectId"]
    assert node_id in {p["m_Id"] for p in root["m_Nodes"]}
    edges = root["m_Edges"]
    assert all(e["m_InputSlot"]["m_Node"]["m_Id"] in nodes and
               e["m_OutputSlot"]["m_Node"]["m_Id"] in nodes for e in edges)
    assert any(e["m_InputSlot"]["m_Node"]["m_Id"] == node_id for e in edges)
    assert any(e["m_OutputSlot"]["m_Node"]["m_Id"] == node_id for e in edges)
    meta = (FOLIAGE / "CustomFunctions/AarisGrassInteraction.hlsl.meta").read_text()
    assert "guid: " + INTERACT_GUID in meta

    ground = next(b for b in blocks if b.get("m_FunctionName") == "AarisDetailGround")
    assert ground["m_FunctionSource"] == "42c69bce689ca1845a44cd416f3614ef"
    inputs = {e["m_InputSlot"]["m_SlotId"]: nodes[e["m_OutputSlot"]["m_Node"]["m_Id"]]["m_Name"]
              for e in edges if e["m_InputSlot"]["m_Node"]["m_Id"] == ground["m_ObjectId"]}
    assert inputs == {0: "Sample Texture 2D", 1: "TerrainColorMatch (Custom Interpolator)",
                      2: "BiasedFade (Custom Interpolator)", 4: "Saturate"}
    outputs = {nodes[e["m_InputSlot"]["m_Node"]["m_Id"]]["m_Name"]
               for e in edges if e["m_OutputSlot"]["m_Node"]["m_Id"] == ground["m_ObjectId"]}
    assert outputs == {"SurfaceDescription.BaseColor", "Multiply"}


def check_interaction():
    shader = (FOLIAGE / "CustomFunctions/AarisGrassInteraction.hlsl").read_text()
    motor = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleSession.cs").read_text()
    assert "void AarisGrassInteraction_float" in shader
    assert "float4 _AarisGrassInteractor;" in shader
    assert "TransformWorldToObjectDir" in shader
    assert "saturate(BasePositionOS.y)" in shader
    assert "falloff * falloff * height * height * 0.45" in shader
    assert "saturate((1.8 - heightDelta) * 1.25)" in shader
    assert "(1.0 - d2 / r2) * vertical" in shader
    assert "rsqrt(d2 + 0.0144)" in shader
    assert not any(term in shader for term in ("RWTexture", "ComputeBuffer", "Raycast", "Collider"))
    assert "Shader.SetGlobalVector(GrassInteractorId, next)" in motor
    assert "footMotor.transform.position" in motor
    assert "car.transform.position" in motor
    assert "Shader.SetGlobalVector(GrassInteractorId, Vector4.zero)" in motor


def check_mobile_transmission():
    shader = (FOLIAGE / "CustomFunctions/PseudoSubsurface.hlsl").read_text()
    assert "void PseudoSubsurface_half" in shader
    assert "GetMainLight()" in shader
    assert "skyFill" in shader and "rim" in shader
    assert "acos(" not in shader and "pow(" not in shader
    assert "TransformWorldToShadowCoord" not in shader
    assert "void AarisDetailGround_half" in shader
    assert "saturate(1.0h - 2.0h * Height01)" in shader
    assert "lerp(rootedColor, TerrainColor, DistanceFade)" in shader
    assert "contact * contact * (3.0h - 2.0h * contact)" in shader
    assert "unity_SHAr.w, unity_SHAg.w, unity_SHAb.w" in shader
    assert "ambient * skyFill * coolFill" in shader


def check_ground_detail_mips():
    """Keep imported alpha mip coverage at the authored terrain-detail cutoff."""
    textures = TREE.parent / "Details/Textures"
    cutoffs = {
        "Bush_Dry": "0.5",
        "Fern": "0.75",
        "Grass_A": "0.415",
        "Grass_C": "0.437",
        "Heathe": "0.777",
        "Shrub": "0.5",
    }
    for name, cutoff in cutoffs.items():
        metadata = (textures / f"{name}_BaseColor.tif.meta").read_text()
        assert "    enableMipMap: 1\n" in metadata, name
        assert "    mipMapsPreserveCoverage: 1\n" in metadata, name
        assert f"    alphaTestReferenceValue: {cutoff}\n" in metadata, name


def check_speedtree_materials():
    leaves = [
        "Pines/Pine_A/Materials/Pine_A.mat",
        "Pines/Pine_B/Materials/Pine_B.mat",
        "Pines/Pine_C/Materials/Pine_C.mat",
        "Pines/Pine_D/Materials/Pine_D.mat",
        "Conifer/Materials/Conifer_Branches.mat",
        "Cypress/Materials/Cypress_Forest_Desktop.mat",
    ]
    bark = [
        "Pines/Pine_A/Materials/Pine_A_Bark.mat",
        "Pines/Pine_B/Materials/Pine_B_Bark.mat",
        "Pines/Pine_C/Materials/Pine_C_Bark.mat",
        "Pines/Pine_D/Materials/Pine_D_Bark.mat",
    ]
    for name in leaves + bark:
        s = (TREE / name).read_text()
        ao = re.search(r"    - _AO_Remap: \{r: ([0-9.]+), g: ([0-9.]+),", s)
        assert ao, name
        lo, hi = map(float, ao.groups())
        assert 0.0 <= lo <= hi <= 1.0, name
        assert "- EFFECT_BUMP" in s, name
        assert "m_EnableInstancingVariants: 1" in s, name
        assert "d353c971ad804ad385b5e3a933e53721" in s, name
    for name in leaves:
        s = (TREE / name).read_text()
        assert "- EFFECT_SUBSURFACE" in s, name



def check_tree_graph_illusions():
    """Validate root contact and stable instance tint without disturbing emission."""
    for file_name, func_name, expected_outputs in (
        ("SpeedTreeURP_Bark.shadergraph", "AarisBarkRootIllusion",
         {"SurfaceDescription.BaseColor", "SurfaceDescription.Occlusion"}),
        ("SpeedTreeURP_Branch.shadergraph", "AarisLeafIllusion",
         {"SurfaceDescription.BaseColor"}),
    ):
        source = (FOLIAGE / file_name).read_text().strip()
        blocks = [json.loads(chunk) for chunk in re.split(r"\n\s*\n(?=\{)", source)]
        nodes = {b["m_ObjectId"]: b for b in blocks if "m_ObjectId" in b}
        assert len(nodes) == len(blocks)
        root = blocks[0]
        matches = [b for b in blocks if b.get("m_FunctionName") == func_name]
        assert len(matches) == 1
        fn = matches[0]
        assert fn["m_Precision"] == 1  # World coordinates must stay float.
        body = fn["m_FunctionBody"]
        assert not any(x in body for x in ("pow(", "acos(", "GetMainLight(", "SAMPLE_TEXTURE", "floor("))
        if func_name == "AarisLeafIllusion":
            assert "PivotWS.xz" in body and "skyTint" in body
        else:
            assert "WorldPos.y - PivotWS.y" in body and "OriginalAO" in body
        used_inputs = set()
        outputs = set()
        for e in root["m_Edges"]:
            src, dst = e["m_OutputSlot"], e["m_InputSlot"]
            for slot in (src, dst):
                n = nodes[slot["m_Node"]["m_Id"]]
                assert any(nodes[s["m_Id"]]["m_Id"] == slot["m_SlotId"] for s in n["m_Slots"])
            key = (dst["m_Node"]["m_Id"], dst["m_SlotId"])
            assert key not in used_inputs
            used_inputs.add(key)
            if src["m_Node"]["m_Id"] == fn["m_ObjectId"]:
                outputs.add(nodes[dst["m_Node"]["m_Id"]]["m_Name"])
        assert outputs == expected_outputs
        # These additions cannot replace existing leaf transmission/emission.
        assert all(nodes[e["m_OutputSlot"]["m_Node"]["m_Id"]].get("m_FunctionName") != func_name
                   for e in root["m_Edges"]
                   if nodes[e["m_InputSlot"]["m_Node"]["m_Id"]].get("m_Name") == "SurfaceDescription.Emission")


def check_custom_speedtree_shader():
    shader = (FOLIAGE / "AarisSpeedTree8Mobile.shader").read_text()
    source = (FOLIAGE / "CustomFunctions/AarisSpeedTree8MobilePasses.hlsl").read_text()
    meta = (FOLIAGE / "AarisSpeedTree8Mobile.shader.meta").read_text()
    assert "guid: d353c971ad804ad385b5e3a933e53721" in meta
    assert 'Shader "Aaris/Nature/SpeedTree8Mobile"' in shader
    assert '#pragma multi_compile_instancing' in shader
    # Leaf and bark data share a varying that must be written with the same
    # keyword state in the vertex shader as in the fragment shader.
    for keyword in ("EFFECT_HUE_VARIATION", "EFFECT_SUBSURFACE"):
        assert shader.count("#pragma shader_feature_local " + keyword) == 2
        assert "#pragma shader_feature_local_fragment " + keyword not in shader
    assert '#include "CustomFunctions/AarisSpeedTree8MobilePasses.hlsl"' in shader
    assert "UNITY_SETUP_INSTANCE_ID" in source
    assert "float3 treePos" in source and "hueVariationAmount" in source
    assert "half rootContact" in source and "occlusion *= 1.0h - 0.20h * rootContact" in source
    assert "half3 skyTint" in source and "half transmit" in source
    assert "Light mainLight = GetMainLight(inputData, shadowMask, aoFactor);" in source
    assert "light.shadowAttenuation" in source
    assert "AarisLeafSun(inputData, mainLight, surfaceData.occlusion)" in source
    assert "AarisLeafSky(inputData, aoFactor.indirectAmbientOcclusion)" in source
    assert "MainLightRealtimeShadow(" not in source
    assert "SampleNormal" in source and "normalTs.xy *= 1.15h" in source
    # The blue varying must be shared by non-subsurface bark root AO and
    # subsurface leaf variation, never requiring an extra TEXCOORD.
    assert "output.color.b = contact * contact * (3.0h - 2.0h * contact)" in source
    assert "half3(0.84h, 0.80h, 0.73h)" in source
    assert "half3 warmSunTint" in source
    assert "rim *= rim;" in source
    assert "saturate(occlusion)" in source
    assert "normalTs.xy *= 1.15h" in source
    assert "leafDetail * 0.085h" in source
    assert "output.color.b = (half)frac(leafSeed * (leafSeed + 17.17))" in source
    # SpeedTree leaf anchor is XYZ = (uv1.z, uv1.w, uv2.w), not uv1.zw as XZ.
    # Matrix transform keeps the variation fixed to each leaf when trees rotate.
    assert "float3 leafAnchorOS = float3(input.texcoord1.z, input.texcoord1.w, input.texcoord2.w)" in source
    assert "TransformObjectToWorld(leafAnchorOS).xz" in source
    assert "float2 leafAnchorXZ = input.texcoord1.zw +" not in source
    assert "half leafDetail = input.interpolated.color.b - 0.5h" in source
    assert source.count("#if defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)") >= 2
    assert not any(term in source for term in ("Raycast(", "Physics.", "ComputeBuffer", "MaterialPropertyBlock"))


def check_hue_variation_finite():
    """Dark/black alpha-cutout texels must not introduce NaNs in leaf PBR."""
    from math import isfinite

    shader = (FOLIAGE / "CustomFunctions/AarisSpeedTree8MobilePasses.hlsl").read_text()
    assert "maxBase /= max(newMaxBase, 0.0001h);" in shader
    assert "maxBase /= newMaxBase;" not in shader
    assert "half newMaxBase = max(shiftedColor.r" in shader
    # Exercise representative pitch-black, nearly black and lit leaf colors.
    hue_rgb = (0.45, 0.55, 0.34)
    for base in ((0.0, 0.0, 0.0), (1e-6, 2e-6, 1e-6),
                 (0.02, 0.05, 0.01), (0.25, 0.43, 0.13)):
        for blend in (0.0, 0.15, 0.50, 1.0):
            shifted = [a * (1 - blend) + b * blend for a, b in zip(base, hue_rgb)]
            max_base = max(base)
            max_shifted = max(shifted)
            correction = 0.5 + 0.5 * max_base / max(max_shifted, 0.0001)
            final = [min(1.0, c * correction) for c in shifted]
            assert all(isfinite(c) and c >= 0.0 for c in final), (base, blend)
            if blend == 0.0 and max_base > 0.0001:
                assert all(abs(a - b) < 1e-7 for a, b in zip(base, final))


def check_mobile_leaf_wind():
    shader = (FOLIAGE / "AarisSpeedTree8Mobile.shader").read_text()
    source = (FOLIAGE / "CustomFunctions/AarisSpeedTree8MobilePasses.hlsl").read_text()
    grass = (FOLIAGE / "CustomFunctions/AarisGrassPhase.hlsl").read_text()
    # The same deformation must run in visible, shadow, selection and depth passes.
    assert shader.count('#include "CustomFunctions/AarisSpeedTree8MobilePasses.hlsl"') == 6
    assert source.count("InitializeData(input, unity_LODFade.x);") == 3
    assert shader.count("#pragma multi_compile_instancing") == 6
    assert "float timeSeconds = _Time.y;" in source
    assert "float fieldPhase = AarisLandscapePhase(treePos.xz)" in source
    assert "AarisLandscapeGust(fieldPhase, timeSeconds)" in source
    # The far, fully static 3D path must leave before wind normalization.
    assert source.index("distanceSq >= endSwayDistanceSq") < source.index("float3 rotatedWindVector")
    assert "AarisLandscapeGust(AarisLandscapePhase(PivotWS.xz), TimeSeconds" in grass
    start = source.index("void AarisFastLeafWind(")
    wind = source[start:source.index("\n}\n", start)]
    assert wind.index("distanceSq >= endFlutterDistanceSq") < wind.index("AarisSmoothWindWave")
    assert "float3 windOS" in wind and "dot(anchorOS," in wind
    assert "timeSeconds * 0.72" in wind and "fieldPhase * 1.618" in wind
    assert "+ 0.16 * gust" in wind
    assert "_ST_WindLeaf1Ripple.x" not in wind and "_ST_WindLeaf2Ripple.x" not in wind
    assert "cross(rotation, positionOS - anchorOS)" in wind
    assert "cross(rotation, input.normal)" in wind and "cross(rotation, input.tangent.xyz)" in wind
    start = source.index("float3 AarisCoherentTreeSway(")
    sway = source[start:source.index("\n}\n", start)]
    assert "inverseHeight <= 0.00001" in sway
    assert "heightWeight *= heightWeight" in sway
    assert "0.009" in sway and "0.025" in sway
    assert "AarisCoherentTreeSway(windyPosition, rotatedWindVector, gust, swayFade)" in source
    assert "AarisFastLeafWind(input, windyPosition, anchor, rotatedWindVector, leafTwo," in source
    for body in (wind, sway, grass):
        assert not any(operation in body for operation in
                       ("SAMPLE_TEXTURE", "tex2D", "sin(", "cos(", "pow(", "Raycast("))
    # The more expensive native options remain available for other authored
    # assets, but every actual 3D forest material uses only Fast/Fastest.
    all_materials = sorted(TREE.rglob("*.mat"))
    assert len(all_materials) == 28
    for material in all_materials:
        text = material.read_text()
        keywords = text.split("m_ValidKeywords:", 1)[1].split("m_InvalidKeywords:", 1)[0]
        if material.stem.endswith("Billboard"):
            assert "99134b1f0c27d54469a840832a28fadf" in text
            assert "_WINDQUALITY_NONE" in keywords
        else:
            assert "d353c971ad804ad385b5e3a933e53721" in text
            assert not re.search(r"_WINDQUALITY_(BETTER|BEST|PALM|NONE)\b", keywords)
            assert re.search(r"(?m)^    - _WindQuality: [12]$", text)
            assert "m_EnableInstancingVariants: 1" in text


def check_continuous_wind_math():
    """Numerical source-model checks; not a measured GPU frametime test."""
    from math import floor, isfinite, sqrt
    from struct import pack, unpack

    def clamp01(x):
        return max(0.0, min(1.0, x))

    def smooth(x):
        return x * x * (3.0 - 2.0 * x)

    def wave(phase):
        return smooth(1.0 - abs(2.0 * (phase - floor(phase)) - 1.0))

    def field(x, z, t):
        return wave(x * 0.011 + z * 0.035 - t * 0.18)

    def fade(distance, full, end):
        return smooth(clamp01((end * end - distance * distance) /
                              (end * end - full * full)))

    # No velocity cusp at the wrap or top of the cheap triangular wave.
    for p in (0.0, 0.5, 1.0, -1.0):
        assert abs(wave(p - 1e-5) - wave(p)) < 1e-8
        assert abs(wave(p + 1e-5) - wave(p)) < 1e-8
    # Spatial propagation is shared between tree and grass materials.
    assert abs(field(42, -130, 10) -
               field(42, -130 + 0.18 / 0.035, 11)) < 1e-9
    # Identical leaf anchors on two separate painted instances should
    # retain distinct ripple phases instead of swaying as synchronized clones.
    assert abs(wave(.173 + (25 * .011 + 30 * .035) * 1.618) -
               wave(.173 + (31 * .011 + 32 * .035) * 1.618)) > 0.05
    for full, end in ((35, 75), (60, 85), (85, 150)):
        samples = [fade(x / 10.0, full, end) for x in range(0, 1800)]
        assert all(a >= b for a, b in zip(samples, samples[1:]))
        assert fade(full, full, end) == 1.0 and fade(end, full, end) == 0.0
        assert fade(end - 0.001, full, end) < 1e-7

    def fp32(x):
        return unpack("f", pack("f", x))[0]

    # 30/45/60/90fps, near both island ends, and after a long session.
    # Float32 clock/phase must continue advancing; no half-time quantization.
    for fps in (30, 45, 60, 90):
        for elapsed in (0.0, 1200.0, 7200.0):
            for x, z in ((-1000.0, -1000.0), (1000.0, 1000.0), (0.0, 0.0)):
                ang = []
                for i in range(fps * 3):
                    t = fp32(elapsed + i / fps)
                    gust = 0.40 + 0.60 * field(x, z, t)
                    flutter = 2.0 * wave(fp32(t * 0.72 + 0.173 + (x * .011 + z * .035) * 1.618 + 0.16 * gust)) - 1.0
                    ang.append(0.18 * gust * (0.23 + 0.77 * flutter))
                assert all(isfinite(a) and abs(a) <= 0.18 for a in ang)
                assert len(set(ang)) > fps
                # Analytic bound: |wave'| <= 3 cycles^-1, including the
                # slow gust envelope. A 30fps frame cannot flip the leaf.
                assert max(abs(a - b) for a, b in zip(ang, ang[1:])) < 1.0 / fps + 0.0005

    # Compare the old fixed clock with the gust-warped clock across phases.
    # This is angular displacement per frame, not GPU performance evidence.
    for fps in (30, 45, 60, 90):
        old_peak = new_peak = 0.0
        for phase in (0.0, 0.173, 0.33, 0.71):
            for spatial in (-46.0, 0.0, 46.0):
                for elapsed in (0.0, 1200.0, 7200.0):
                    old, new = [], []
                    for i in range(fps * 10):
                        t = fp32(elapsed + i / fps)
                        gust = 0.40 + 0.60 * wave(spatial - t * 0.18)
                        for rate, warp, samples in ((0.95, 0.0, old), (0.72, 0.16, new)):
                            flutter = 2.0 * wave(fp32(t * rate + phase + spatial * 1.618 + warp * gust)) - 1.0
                            samples.append(0.18 * gust * (0.23 + 0.77 * flutter))
                    assert len(set(new)) > fps
                    old_peak = max(old_peak, max(abs(a - b) for a, b in zip(old, old[1:])))
                    new_peak = max(new_peak, max(abs(a - b) for a, b in zip(new, new[1:])))
        assert new_peak < 0.85 * old_peak, (fps, old_peak, new_peak)

    def sway(y, inverse_height, adherence, amplitude, gust, f):
        if inverse_height <= 0.00001 or f <= 0.0:
            return 0.0
        weight = clamp01((y * inverse_height - 0.10) * 1.111111) ** 2
        rest = max(-0.009, min(0.009, adherence))
        travel = min(abs(amplitude) * inverse_height, 0.025)
        return (rest + travel * (0.20 + 0.80 * gust)) * weight * f / inverse_height

    for h in (2.0, 8.0, 20.0, 35.0):
        for gust in (0.0, 0.5, 1.0):
            assert sway(0, 1 / h, .05, 8, gust, 1) == 0.0
            assert sway(h * 0.1, 1 / h, .05, 8, gust, 1) < 1e-25
            assert abs(sway(h, 1 / h, .05, 8, gust, 1)) <= .034001 * h
            assert sway(h, 1 / h, .05, 8, gust, 0) == 0.0
    assert sway(1, 0, .05, 8, 1, 1) == 0.0

    # Ground response is finite and direction-continuous through the player
    # center, and fades to zero at both the horizontal and vertical bounds.
    def interaction(x, y, height):
        d2 = x * x
        r2 = 1.25 * 1.25
        if d2 >= r2 or abs(y) >= 1.8:
            return 0.0
        vertical = clamp01((1.8 - abs(y)) * 1.25)
        bend = ((1.0 - d2 / r2) * vertical) ** 2 * clamp01(height) ** 2 * .45
        return bend * x / sqrt(d2 + .0144)

    assert interaction(0, 0, 1) == 0
    assert interaction(.3, 0, 0) == 0
    assert abs(interaction(1.24999, 0, 1)) < 1e-8
    assert abs(interaction(.3, 1.79999, 1)) < 1e-8
    assert abs(interaction(.00001, 0, 1) - interaction(-.00001, 0, 1)) < .0001


if __name__ == "__main__":
    check_graph()
    check_interaction()
    check_mobile_transmission()
    check_ground_detail_mips()
    check_speedtree_materials()
    check_tree_graph_illusions()
    check_custom_speedtree_shader()
    check_hue_variation_finite()
    check_mobile_leaf_wind()
    check_continuous_wind_math()
    print("AARIS_MOBILE_FOLIAGE_CONTRACT_PASS")
    print("10 foliage source/math groups checked")
