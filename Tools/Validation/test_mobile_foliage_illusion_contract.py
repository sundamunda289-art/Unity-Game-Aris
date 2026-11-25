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
                      2: "BiasedFade (Custom Interpolator)",
                      5: "PerInstanceRandom (Custom Interpolator)", 6: "Property"}
    outputs = {nodes[e["m_InputSlot"]["m_Node"]["m_Id"]]["m_Name"]
               for e in edges if e["m_OutputSlot"]["m_Node"]["m_Id"] == ground["m_ObjectId"]}
    assert outputs == {"SurfaceDescription.BaseColor", "Multiply", "Power"}

    def destinations(node, port):
        return [(nodes[e["m_InputSlot"]["m_Node"]["m_Id"]], e["m_InputSlot"]["m_SlotId"])
                for e in edges if e["m_OutputSlot"]["m_Node"]["m_Id"] == node["m_ObjectId"]
                and e["m_OutputSlot"]["m_SlotId"] == port]

    pack = next(b for b in blocks if b.get("m_FunctionName") == "AarisPackDetailGround")
    [(interpolator, _)] = destinations(pack, 3)
    assert interpolator["m_SerializedDescriptor"] == "VertexDescription.TerrainColorMatch#4"
    assert [(n["m_Name"], p) for n, p in destinations(ground, 4)] == [("Power", 0)]
    keep = next(b for b in blocks if b.get("m_FunctionName") == "AarisDetailKeepCanopyNormal")
    branches = destinations(keep, 3)
    assert len(branches) == 2 and all(n["m_Name"] == "Branch" and p == 0 for n, p in branches)
    assert any(n["m_Name"] == "PseudoSubsurface"
               for branch, _ in branches for n, _ in destinations(branch, 3))
    [(frame, port)] = destinations(interaction[0], 5)
    assert frame["m_FunctionName"] == "AarisDetailCanopyFrame" and port == 8


def check_interaction():
    shader = (FOLIAGE / "CustomFunctions/AarisGrassInteraction.hlsl").read_text()
    motor = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleSession.cs").read_text()
    assert "void AarisGrassInteraction_float" in shader
    assert "float4 _AarisGrassInteractor;" in shader
    assert "float inverseRadiusSq = _AarisGrassInteractor.w;" in shader
    assert "float radial = d2 * inverseRadiusSq;" in shader
    assert "TransformWorldToObjectDir" in shader
    assert "saturate(BasePositionOS.y)" in shader
    assert "falloff * falloff * vertical * 0.45" in shader
    assert "Out += offsetOS * (height * height)" in shader
    assert "BendSlopeOS = clamp(offsetOS * (2.0 * height * inverseHeight)" in shader
    assert "saturate((1.8 - heightDelta) * 1.25)" in shader
    # Shader work on unrelated floors/slopes is rejected before per-leaf
    # object-to-world transforms. The old conjunctive bound is equivalent.
    assert "UNITY_BRANCH if (heightDelta >= 1.8)" in shader
    assert "float3 pivot = float3(UNITY_MATRIX_M[0].w, UNITY_MATRIX_M[1].w, UNITY_MATRIX_M[2].w);" in shader
    assert "TransformObjectToWorld(float3(0.0, 0.0, 0.0))" not in shader
    assert shader.index("float heightDelta = abs(pivot.y") < shader.index(
        "TransformObjectToWorld(BasePositionOS).xz")
    assert "if (radial < 1.0)" in shader
    assert shader.index("Out = WindPositionOS;") < shader.index(
        "UNITY_BRANCH if (heightDelta >= 1.8)")
    for vertical_delta in (0.0, 0.8, 1.799999, 1.8, 1.800001, 4.0):
        for radial in (0.0, 0.25, 0.999999, 1.0, 1.000001, 4.0):
            old_contact = radial < 1.0 and vertical_delta < 1.8
            new_contact = vertical_delta < 1.8 and radial < 1.0
            assert old_contact == new_contact

    assert "float falloff = 1.0 - radial;" in shader
    assert "radius * radius" not in shader and "d2 / r2" not in shader
    assert "vertical = vertical * vertical * (3.0 - 2.0 * vertical)" in shader
    assert "rsqrt(d2 + 0.0144)" in shader
    assert "rcp(max(GroundHeight, 0.05))" in shader
    assert "BasePositionOS.y * inverseHeight" in shader
    assert "TransformObjectToWorld(BasePositionOS).xz" in shader
    assert "min(1.0, max(GroundHeight, 0.25))" in shader
    assert not any(term in shader for term in ("RWTexture", "ComputeBuffer", "Raycast", "Collider"))
    assert "Shader.SetGlobalVector(GrassInteractorId, next)" in motor
    assert "FootGrassInverseRadiusSq = 1f / (1.25f * 1.25f)" in motor
    assert "VehicleGrassInverseRadiusSq = 1f / (2.6f * 2.6f)" in motor
    assert "new Vector4(p.x, p.y, p.z, FootGrassInverseRadiusSq)" in motor
    assert "new Vector4(p.x, p.y, p.z, VehicleGrassInverseRadiusSq)" in motor
    assert "footMotor.transform.position" in motor
    assert "car.transform.position" in motor
    assert "Shader.SetGlobalVector(GrassInteractorId, Vector4.zero)" in motor


def check_red_bush_broadphase():
    """Stress-test the native and FBX LOD bounds; no reachable card is rejected."""
    from math import cos, sin, pi
    from random import Random
    shader = (FOLIAGE / "CustomFunctions/AarisGrassInteraction.hlsl").read_text()
    for term in ("abs(GroundHeight - 2.13) < 0.002",
                 "float3(2.30, 2.20, 2.50)",
                 "dot(abs(UNITY_MATRIX_M[0].xyz), maxAbsRedBushOS)",
                 "dot(abs(UNITY_MATRIX_M[2].xyz), maxAbsRedBushOS)",
                 "float2 outside = max(abs(delta) - spanXZ, 0.0)",
                 "dot(outside, outside) * inverseRadiusSq >= 1.0"):
        assert term in shader
    broadphase = shader[shader.index("const float3 maxAbsRedBushOS"):
                        shader.index("delta = TransformObjectToWorld(BasePositionOS).xz")]
    assert "rsqrt(" not in broadphase and "contactRadius" not in broadphase
    assert shader.index("dot(outside, outside) * inverseRadiusSq") < shader.index(
        "TransformObjectToWorld(BasePositionOS).xz")
    # Measured from Unity AssetDatabase: painted bush + standalone LOD0/1/2.
    models = [
        ((0., 1.017657, 0.), (2.20198035, 1.11452365, 1.784371)),
        ((-.0244450569, 1.017657, -.6276578), (2.20198035, 1.11452365, 1.784371)),
        ((-.0353512764, 1.01483321, -.627658367), (2.21810246, 1.1228416, 1.78437042)),
        ((-.0353508, 1.014833, -.5967015), (2.218102, 1.12284184, 1.81532729)),
    ]
    bounds = (2.30, 2.20, 2.50)
    for center, extent in models:
        assert all(abs(c) + e < bound for c, e, bound in zip(center, extent, bounds))
    rng = Random(251126)
    def dot(a, b): return sum(x*y for x, y in zip(a, b))
    for i in range(10000):
        center, extent = models[i % 4]
        # Arbitrary matrices include rotation, tilt, mirrors, nonuniform scaling.
        mx = [rng.uniform(-4, 4) for _ in range(3)]
        mz = [rng.uniform(-4, 4) for _ in range(3)]
        point = [c + rng.uniform(-e, e) for c, e in zip(center, extent)]
        x, z = dot(mx, point), dot(mz, point)
        sx = dot([abs(v) for v in mx], bounds)
        sz = dot([abs(v) for v in mz], bounds)
        assert abs(x) <= sx + 1e-6 and abs(z) <= sz + 1e-6
        radius = (1.25, 2.6, 5.)[i % 3]
        angle = rng.uniform(-pi, pi)
        dist = radius * rng.uniform(0, .9999)
        px, pz = x + cos(angle)*dist, z + sin(angle)*dist
        assert (px-x)**2 + (pz-z)**2 < radius*radius
        assert abs(px) <= sx + radius + 1e-5
        assert abs(pz) <= sz + radius + 1e-5
        # Closest-AABB distance cannot exceed distance to this reachable card.
        ox = max(abs(px) - sx, 0.0)
        oz = max(abs(pz) - sz, 0.0)
        assert (ox*ox + oz*oz) / (radius*radius) < 1.0

    # Corner-only points accepted by the old radius-expanded square can be
    # rejected by the tighter rounded envelope, still without a square root.
    tightened = 0
    for _ in range(4000):
        sx, sz = rng.uniform(.2, 6), rng.uniform(.2, 6)
        radius = rng.choice((1.25, 2.6, 5.0))
        px = rng.uniform(-sx-radius, sx+radius)
        pz = rng.uniform(-sz-radius, sz+radius)
        old_reject = abs(px) > sx + radius or abs(pz) > sz + radius
        ox, oz = max(abs(px)-sx, 0.0), max(abs(pz)-sz, 0.0)
        new_reject = (ox*ox + oz*oz) >= radius*radius
        assert not old_reject
        tightened += int(new_reject)
    assert tightened > 0


def check_mobile_transmission():
    shader = (FOLIAGE / "CustomFunctions/PseudoSubsurface.hlsl").read_text()
    assert "void PseudoSubsurface_half" in shader
    assert "GetMainLight()" in shader
    assert "GetCameraPositionWS()" in shader
    assert "distanceSq >= 8100.0" in shader
    assert shader.index("distanceSq >= 8100.0") < shader.index("GetMainLight()")
    assert "0.00016460905" in shader and "ssAmount *= illusionFade" in shader
    assert "skyFill" in shader and "rim" in shader and "hemiFill" in shader
    assert "acos(" not in shader and "pow(" not in shader
    assert "TransformWorldToShadowCoord" not in shader
    assert "void AarisDetailGround_half" in shader
    assert "saturate(1.0h - 2.0h * Height01)" in shader
    assert "lerp(rootedColor, terrainColor, fade)" in shader
    assert "Height01 = TerrainColorAndHeight.w" in shader
    assert "TransmissionColor = Out * (0.20h + 0.80h * heightMask)" in shader
    assert "0.25h * SSRadius.x" in shader
    assert "Out = FrontFace || Profile.x > 0.001h" in shader
    assert "contact * contact * (3.0h - 2.0h * contact)" in shader
    assert "unity_SHAr.w, unity_SHAg.w, unity_SHAb.w" in shader
    assert "ambient * skyFill * hemiFill" in shader
    assert "0.035h + 0.020h * up" in shader


def check_neutral_ground_chroma_fastpath():
    """Neutral grass skips shrub chroma math without changing the color model."""
    shader = (FOLIAGE / "CustomFunctions/PseudoSubsurface.hlsl").read_text()
    start = shader.index("void AarisDetailGround_half")
    end = shader.index("\n}\n", start)
    detail = shader[start:end]
    branch = "UNITY_BRANCH if (Profile.x > 0.0h)"
    assert branch in detail
    assert detail.index(branch) < detail.index("saturate(InstanceSeed)")
    assert "half3 localColor = LeafColor * Profile.w;" in detail
    assert "localColor *= 1.0h + variation * half3(0.10h, 0.07h, -0.05h);" in detail

    def saturate(value):
        return max(0.0, min(1.0, value))

    # The new branch is algebraically identical for every profile value:
    # non-positive profiles used to multiply by zero variation, positive
    # profiles execute the exact original expression.
    for profile in (-0.25, 0.0, 0.0001, 0.35, 0.92, 1.0, 1.4):
        for seed in (-0.2, 0.0, 0.25, 0.5, 0.9, 1.0, 1.2):
            old = (saturate(seed) - 0.5) * saturate(profile)
            new = ((saturate(seed) - 0.5) * saturate(profile)
                   if profile > 0.0 else 0.0)
            assert abs(old - new) < 1e-12


def check_ground_detail_mips():
    """Keep imported alpha mip coverage at the authored terrain-detail cutoff."""
    textures = TREE.parent / "Details/Textures"
    cutoffs = {
        "Bush_A": "0.482",
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
    for keyword, count in (("EFFECT_HUE_VARIATION", 2), ("EFFECT_SUBSURFACE", 3)):
        assert shader.count("#pragma shader_feature_local " + keyword) == count
        assert "#pragma shader_feature_local_fragment " + keyword not in shader
    assert '#include "CustomFunctions/AarisSpeedTree8MobilePasses.hlsl"' in shader
    assert "UNITY_SETUP_INSTANCE_ID" in source
    assert "float3 treePos" in source and "hueVariationAmount" in source
    assert "half rootContact" in source and "occlusion *= 1.0h - 0.20h * rootContact" in source
    assert "half3 hemiTint" in source and "half transmit" in source
    assert "Light mainLight = GetMainLight(inputData, shadowMask, aoFactor);" in source
    assert "light.shadowAttenuation" in source
    assert "AarisLeafSun(inputData.normalWS, inputData.viewDirectionWS, mainLight, surfaceData.occlusion)" in source
    assert "AarisLeafSky(inputData.normalWS, inputData.bakedGI, aoFactor.indirectAmbientOcclusion)" in source
    assert "MainLightRealtimeShadow(" not in source
    assert "SampleNormal" in source and source.count("normalTs.xy *= 1.20h - 0.12h * rootContact") == 2
    # The blue varying must be shared by non-subsurface bark root AO and
    # subsurface leaf variation, never requiring an extra TEXCOORD.
    assert "output.color.b = contact * contact * (3.0h - 2.0h * contact)" in source
    assert "half3(0.84h, 0.80h, 0.73h)" in source
    assert "half3 warmSunTint" in source
    assert "half back2 = back * back;" in source
    assert "half viewBack = saturate(-dot(viewDirectionWS, light.direction));" in source
    assert "half viewLobe = 0.78h + 0.22h * viewBack * viewBack;" in source
    assert "half viewRim = 1.0h - saturate(abs(dot(normalWS, viewDirectionWS)));" in source
    assert "viewRim *= viewRim;" in source
    assert "half transmit = 0.31h * back2 * viewLobe" in source
    assert "+ 0.055h * rim * (0.35h + 0.65h * back)" in source
    assert "+ 0.035h * viewRim * back * (0.65h + 0.35h * viewBack);" in source
    assert "towardSun" not in source
    assert "rim *= rim;" in source
    assert "saturate(occlusion)" in source
    assert "smoothness *= 1.0h - 0.16h * rootContact" in source
    assert "half hemiStrength = 0.17h + 0.07h * up;" in source
    assert source.count("half rootContact = saturate(input.interpolated.color.b);") == 2
    assert "leafDetail * 0.085h" in source
    assert "float macroTone = AarisForestMacroTone(treePivotXZ);" in source
    assert "leafTone * 0.70 + macroTone * 0.30" in source
    # Forest-scale breakup comes from the world-space instance pivot.
    # Near leaves add stable object-space anchor noise without matrix transforms.
    assert "float2 treePivotXZ = float2(UNITY_MATRIX_M[0].w, UNITY_MATRIX_M[2].w)" in source
    assert "float3 leafAnchorOS = float3(input.texcoord1.z, input.texcoord1.w, input.texcoord2.w)" in source
    assert "leafSeed += dot(leafAnchorOS, float3(0.173, 0.117, 0.137))" in source
    assert "TransformObjectToWorld(leafAnchorOS).xz" not in source
    assert "float2 leafAnchorXZ = input.texcoord1.zw +" not in source
    assert "half leafDetail = input.interpolated.color.b - 0.5h" in source
    assert source.count("#if defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)") >= 2
    assert not any(term in source for term in ("Raycast(", "Physics.", "ComputeBuffer", "MaterialPropertyBlock"))


def check_speedtree_view_edge_lobe_math():
    """Camera-edge translucency stays bounded by the previous 0.40 peak."""
    for i in range(-100, 101):
        ndl = i * 0.01
        back = max(0.0, min(1.0, 0.5 - 0.5 * ndl))
        back2 = back * back
        rim = (1.0 - min(1.0, abs(ndl))) ** 2
        for j in range(-100, 101, 5):
            ndv = j * 0.01
            view_rim = (1.0 - min(1.0, abs(ndv))) ** 2
            for view_back in (0.0, 0.25, 0.5, 0.75, 1.0):
                view_lobe = 0.78 + 0.22 * view_back * view_back
                transmit = (0.31 * back2 * view_lobe
                            + 0.055 * rim * (0.35 + 0.65 * back)
                            + 0.035 * view_rim * back * (0.65 + 0.35 * view_back))
                assert 0.0 <= transmit <= 0.4000001
    # At equal backlighting, a grazing card must receive more fake edge light
    # than a camera-facing card, but never exceed the old peak energy budget.
    back = 1.0
    view_back = 1.0
    frontal = 0.31 * back * view_back + 0.055 * 0.0 + 0.035 * 0.0
    grazing = 0.31 * back * view_back + 0.055 * 0.0 + 0.035 * 1.0
    assert grazing > frontal
    assert grazing <= 0.40 + 1e-9


def check_forest_macro_tone_math():
    """Broad world-space colour field stays finite, bounded and wrap-continuous."""
    from math import floor, isfinite

    canopy = (FOLIAGE / "CustomFunctions/AarisFoliageCanopy.hlsl").read_text()
    start = canopy.index("float AarisForestMacroTone(")
    body = canopy[start:canopy.index("\n}\n", start)]
    for term in ("frac(dot(pivotXZ", "0.001953125", "0.001220703",
                 "-0.001464844", "0.001708984", "a * 0.57 + b * 0.43"):
        assert term in body
    assert not any(term in body for term in
                   ("sin(", "cos(", "pow(", "SAMPLE_TEXTURE", "tex2D", "floor("))

    def tri(v):
        f = v - floor(v)
        return 1.0 - abs(f * 2.0 - 1.0)

    def tone(x, z):
        a = tri(x * .001953125 + z * .001220703)
        b = tri(x * -.001464844 + z * .001708984)
        return max(0.0, min(1.0, a * .57 + b * .43))

    samples = [tone(x, z)
               for x in range(-1400, 1401, 37)
               for z in range(-1400, 1401, 53)]
    assert all(isfinite(v) and 0.0 <= v <= 1.0 for v in samples)
    assert max(samples) - min(samples) > 0.75

    eps = 1e-5
    for k in range(-3, 4):
        assert abs(tri(k - eps) - tri(k + eps)) < 5e-5

    deltas = []
    for x, z in ((-900, -700), (-320, 410), (0, 0), (470, -610), (880, 790)):
        deltas.append(abs(tone(x, z) - tone(x + 12, z - 9)))
    assert max(deltas) < 0.08


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
    assert source.count("InitializeData(input, unity_LODFade.x);") == 2
    assert source.count("AarisInitializeLitGeometry(input);") == 2
    start = source.index("void AarisInitializeLitGeometry(")
    lit = source[start:source.index("\n}\n", start)]
    deform = lit.index("InitializeData(input, unity_LODFade.x);")
    assert "defined(_WINDQUALITY_FAST)" in lit[:deform]
    assert "AarisTreeCanopyFrame(input)" in lit[:deform]
    assert "!defined(_WINDQUALITY_FAST)" in lit[deform:]
    assert "AarisTreeCanopyFrame(input)" in lit[deform:]
    # The authored distant 3D materials serialize Fastest, which maps to the
    # shader's default/no-quality-keyword variant. That path must use the
    # cheaper hemispherical canopy frame before any ellipsoid reconstruction;
    # near Fast leaves and billboards keep their existing paths.
    frame_start = source.index("void AarisTreeCanopyFrame(")
    frame = source[frame_start:source.index("\n}\n", frame_start)]
    cheap = frame.index("Fastest is the shader's default wind variant")
    anchor = frame.index("float3 anchor")
    assert cheap < anchor
    for keyword in ("!defined(_WINDQUALITY_NONE)", "!defined(_WINDQUALITY_FAST)",
                    "!defined(_WINDQUALITY_BETTER)", "!defined(_WINDQUALITY_BEST)",
                    "!defined(_WINDQUALITY_PALM)"):
        assert keyword in frame[cheap:anchor]
    assert "lerp(input.normal, float3(0.0, 1.0, 0.0), 0.62)" in frame[cheap:anchor]
    assert "AarisCanopyTangentOS(input.tangent.xyz, input.normal)" in frame[cheap:anchor]
    assert frame.index("AarisVolumeNormalOS", anchor) > anchor
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
    assert "float twist = angle * (leafTwo ? -0.11 : 0.11);" in wind
    assert "rotation += windOS * twist;" in wind
    assert wind.count("AarisSmoothWindWave") == 1
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

    # CPU-precomputed inverse radius squared is algebraically equivalent to
    # the old per-vertex divide for both interaction radii.
    for radius in (1.25, 2.6):
        inv_r2 = 1.0 / (radius * radius)
        for step in range(1001):
            d = radius * step / 1000.0
            old_radial = (d * d) / (radius * radius)
            new_radial = (d * d) * inv_r2
            assert abs(old_radial - new_radial) < 1e-12

    # Ground response is finite and direction-continuous through the player
    # center, and fades to zero at both the horizontal and vertical bounds.
    def interaction(x, y, height):
        d2 = x * x
        r2 = 1.25 * 1.25
        if d2 >= r2 or abs(y) >= 1.8:
            return 0.0
        vertical = clamp01((1.8 - abs(y)) * 1.25)
        bend = (1.0 - d2 / r2) ** 2 * smooth(vertical) * clamp01(height) ** 2 * .45
        return bend * x / sqrt(d2 + .0144)

    assert interaction(0, 0, 1) == 0
    assert interaction(.3, 0, 0) == 0
    assert abs(interaction(1.24999, 0, 1)) < 1e-8
    assert abs(interaction(.3, 1.79999, 1)) < 1e-8
    assert abs(interaction(.00001, 0, 1) - interaction(-.00001, 0, 1)) < .0001
    # Both the full-contact shoulder and release boundary must be C1
    # when crossing heights on a slope or returning from a jump.
    eps = 1e-5
    for boundary in (-1.8, -1.0, 1.0, 1.8):
        for side in (-1, 1):
            assert abs(interaction(.3, boundary + side * eps, 1) -
                       interaction(.3, boundary, 1)) / eps < 0.0001
    for x in (.2, .6, 1.1):
        for y in (0, .8, 1.2, 1.5, 1.799):
            assert 0 <= interaction(x, y, 1) <= .45
            assert interaction(x, y, 0) == 0



def check_canopy_asset_bindings():
    """Every native bush family uses the active terrain-detail graph."""
    details = TREE.parent / "Details"
    expected = {
        "Bush.asset": .86, "Bush_Dry.asset": 1.08,
        "Bush_Twig.asset": .52, "Shrub.asset": .35,
        "Fern.asset": .38, "Heather.asset": .92, "Bush_Red.mat": 2.13,
    }
    for name, height in expected.items():
        path = details / "Materials" / name
        mat = path.read_text()
        assert not mat.startswith("version https://git-lfs.github.com/spec/v1"), (
            f"{name}: hydrate the native ground-foliage material LFS objects before this check")
        assert "guid: dfa3b96612ed4df4a97b7fd06217ba23" in mat, name
        assert "m_EnableInstancingVariants: 1" in mat, name
        assert "_AlphaClip: 1" in mat and "_Cull: 0" in mat, name
        profile = re.search(r"_AarisCanopyProfile: \{r: ([\d.]+), g: ([\d.]+), b: ([\d.]+), a: ([\d.]+)\}", mat)
        assert profile, name
        strength, center, gradient, tint = map(float, profile.groups())
        assert 0 < strength <= 1 and 0 < center < height and gradient > 0 and 0 < tint <= 1, name
        assert float(re.search(r"_GroundFalloff: ([\d.]+)", mat).group(1)) == height
        assert "m_CustomRenderQueue: 2450" in mat, name
    red = (details / "Materials/Bush_Red.mat").read_text()
    for guid in ("1933fb8bd6eed9d429dd93c5de2f061c", "90a6b9df8334d6040b2ae1928aaab244",
                 "cfc599ff14b5f5d45bcdacfde81269a4", "d2418be9721fd3e40b01f6a5f3c43325"):
        assert guid in red
    red_meta = TREE.parent / "Foliage/Red_Bush/Textures/Red_Bush_BaseColor.tif.meta"
    assert "mipMapsPreserveCoverage: 1" in red_meta.read_text()
    assert "alphaTestReferenceValue: 0.5" in red_meta.read_text()
    for name in ("Cypress_Forest_Desktop.mat", "Cypress_Forest_Desktop_Distant.mat"):
        assert "m_CustomRenderQueue: -1" in (TREE / "Cypress/Materials" / name).read_text()
    graph = (FOLIAGE / "TerrainGrass.shadergraph").read_text().strip()
    bs = [json.loads(x) for x in re.split(r"\n\s*\n(?=\{)", graph)]
    by = {o["m_ObjectId"]: o for o in bs}
    assert len(by) == len(bs)
    root = bs[0]
    profile = next(o for o in bs if o.get("m_OverrideReferenceName") == "_AarisCanopyProfile")
    assert profile["m_Value"] == {"x": 0., "y": .4, "z": 1., "w": 1.}
    assert profile["m_ObjectId"] in {o["m_Id"] for o in root["m_Properties"]}
    frame = next(o for o in bs if o.get("m_FunctionName") == "AarisDetailCanopyFrame")
    assert frame["m_FunctionSource"] == "83cdd9a72e5c4ae1bb9278de809f7261"
    assert frame["m_Precision"] == 1
    outputs = {}
    inbound = set()
    for edge in root["m_Edges"]:
        src, dst = edge["m_OutputSlot"], edge["m_InputSlot"]
        for t in (src, dst):
            node = by[t["m_Node"]["m_Id"]]
            assert any(by[r["m_Id"]]["m_Id"] == t["m_SlotId"] for r in node["m_Slots"])
        k = (dst["m_Node"]["m_Id"], dst["m_SlotId"])
        assert k not in inbound, k
        inbound.add(k)
        if src["m_Node"]["m_Id"] == frame["m_ObjectId"]:
            outputs[src["m_SlotId"]] = by[dst["m_Node"]["m_Id"]]["m_Name"]
    assert outputs == {5: "VertexDescription.Normal", 6: "VertexDescription.Tangent"}
    depth = (FOLIAGE / "AarisSpeedTree8Mobile.shader").read_text().split('Name "DepthNormals"')[1]
    assert "#pragma shader_feature_local EFFECT_SUBSURFACE" in depth
    assert "#pragma shader_feature_local EFFECT_BILLBOARD" in depth
    passes = (FOLIAGE / "CustomFunctions/AarisSpeedTree8MobilePasses.hlsl").read_text()
    assert passes.count("AarisTreeCanopyFrame(input);") == 2
    assert "float4 probeOcclusion       : TEXCOORD9;" in passes
    assert "input.interpolated.probeOcclusion," in passes
    assert "inputData.shadowMask = half4(1, 1, 1, 1);" in passes
    assert "if (geometry <= GEOM_TYPE_FROND)" in passes
    assert passes.count("AlphaDiscard(alpha, 0.3333)") == 3


def check_canopy_math():
    """Stress outward hemisphere, degenerate tangent frames and root pinning."""
    from math import sqrt, isfinite
    from random import Random
    hlsl = (FOLIAGE / "CustomFunctions/AarisFoliageCanopy.hlsl").read_text()
    for term in ("AarisVolumeNormalOS", "AarisCanopyTangentOS", "Profile.x > 0.001",
                 "2.0 * min(dot(n, crown), 0.0)", "dot(projected, projected)",
                 "MotionFade - 0.64", "fade >= 0.999"):
        assert term in hlsl
    assert not any(s in hlsl for s in ("SAMPLE_TEXTURE", "tex2D", "sin(", "cos(", "pow(", "Raycast", "ComputeBuffer"))
    def dot(a, b): return sum(x*y for x,y in zip(a,b))
    def add(a,b): return tuple(x+y for x,y in zip(a,b))
    def mul(a,b): return tuple(x*b for x in a)
    def unit(a, fallback=(0.,1.,0.)):
        d = dot(a,a)
        return mul(a,1/sqrt(d)) if d > 1e-8 else fallback
    def cross(a,b):
        return (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
    def volume(n, gradient, strength):
        n, c = unit(n), unit(gradient)
        n = add(n,mul(c,-2*min(dot(n,c),0)))
        return unit(add(mul(n,1-strength),mul(c,strength)),c)
    rng = Random(61125)
    for _ in range(12000):
        n = tuple(rng.uniform(-1,1) for _ in range(3))
        radial = tuple(rng.uniform(-2,2) for _ in range(3))
        if _ % 1000 == 0: n, radial = (0,0,0),(0,0,0)
        if _ % 251 == 0: n = mul(unit(radial),-1)
        c = unit(radial)
        out = volume(n,radial,rng.uniform(.32,.78))
        assert all(isfinite(x) for x in out) and abs(dot(out,out)-1) < 1e-6
        assert dot(out,c) >= -1e-6
        tangent = out if _ % 71 == 0 else n
        projected = add(tangent,mul(out,-dot(tangent,out)))
        fallback = unit(cross((0,1,0) if abs(out[1])<.9 else (1,0,0),out))
        tangent = unit(projected,fallback)
        assert abs(dot(tangent,out)) < 1e-6
        assert abs(dot(tangent,tangent)-1) < 1e-6
        # The contact deformation is a shear along the rest-height axis.
        # Transform tangents by its Jacobian and normals by inverse transpose.
        slope = tuple(rng.uniform(-.65, .65) for _ in range(3))
        bent_tangent = add(tangent, mul(slope, tangent[1]))
        bent_normal = unit(add(out, (0, -dot(out, slope) / (1 + slope[1]), 0)))
        assert all(isfinite(x) for x in bent_normal)
        assert abs(dot(bent_normal, bent_tangent)) < 1e-6
    # Fastest/distant SpeedTree leaves use a normalized hemispherical frame.
    # For any unit authored normal, the 0.62 upward blend cannot invert the
    # canopy and stays finite; no gradient/crown reconstruction is needed.
    for _ in range(4000):
        n = unit(tuple(rng.uniform(-1, 1) for _ in range(3)))
        fast = unit((n[0] * .38, n[1] * .38 + .62, n[2] * .38))
        assert all(isfinite(x) for x in fast)
        assert abs(dot(fast, fast) - 1) < 1e-6
        assert fast[1] > 0

    # The shared distance curve has zero slope at both ends. Far foliage
    # uses the cheap upward normal/tangent and retains its full silhouette.
    def canopy_fade(x):
        v = max(0, min(1, (x-.64)*2.7777778))
        return v*v*(3-2*v)
    curve = [canopy_fade(i/1000) for i in range(1001)]
    assert all(a <= b for a,b in zip(curve,curve[1:]))
    assert canopy_fade(.64) == 0 and abs(canopy_fade(1)-1) < 1e-8
    assert canopy_fade(.64001) < 1e-7 and 1-canopy_fade(.99999) < 1e-7
    # A root stays fixed for every authored shrub height; tiny shrubs cannot
    # move as far as the large red bush. Horizontal contact is bounded.
    for h in (.35,.52,.86,1.08,2.13):
        for y in (0,h*.2,h*.5,h):
            height = max(0,min(1,y/max(h,.05)))
            for d in (0,.1,.7,1.24999,1.25):
                f = max(0,1-d*d/(1.25*1.25))
                bend=f*f*height*height*.45*min(1,max(h,.25))
                assert 0 <= bend <= .45
                if y == 0 or d >= 1.25: assert bend == 0
    # Approximate lobe has a finite energy limit, lower than the old 0.92
    # unsaturated root glow; no new shadow/texture reads are needed.
    for radius in (0,.125,.5,1):
        for i in range(-100,101):
            ndl=i*.01
            back=max(0,min(1,(radius-ndl)/(1+radius)))
            wrapped=back*back*(3-2*back)
            rim=(1-abs(ndl))**2
            transmit=(.14+.23*radius)*wrapped+.10*rim
            assert 0 <= transmit <= .47 + 1e-9


if __name__ == "__main__":
    check_graph()
    check_interaction()
    check_red_bush_broadphase()
    check_mobile_transmission()
    check_neutral_ground_chroma_fastpath()
    check_ground_detail_mips()
    check_speedtree_materials()
    check_tree_graph_illusions()
    check_custom_speedtree_shader()
    check_speedtree_view_edge_lobe_math()
    check_forest_macro_tone_math()
    check_hue_variation_finite()
    check_mobile_leaf_wind()
    check_continuous_wind_math()
    check_canopy_asset_bindings()
    check_canopy_math()
    print("AARIS_MOBILE_FOLIAGE_CONTRACT_PASS")
    print("16 foliage source/math groups checked")
