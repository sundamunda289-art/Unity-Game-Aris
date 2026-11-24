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


def check_interaction():
    shader = (FOLIAGE / "CustomFunctions/AarisGrassInteraction.hlsl").read_text()
    motor = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleSession.cs").read_text()
    assert "void AarisGrassInteraction_float" in shader
    assert "float4 _AarisGrassInteractor;" in shader
    assert "TransformWorldToObjectDir" in shader
    assert "saturate(BasePositionOS.y)" in shader
    assert "falloff * falloff * height * height * 0.45" in shader
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
    assert '#include "CustomFunctions/AarisSpeedTree8MobilePasses.hlsl"' in shader
    assert "UNITY_SETUP_INSTANCE_ID" in source
    assert "float3 treePos" in source and "hueVariationAmount" in source
    assert "half rootContact" in source and "occlusion *= 1.0h - 0.20h * rootContact" in source
    assert "half3 skyTint" in source and "half transmit" in source
    assert "Light mainLight = GetMainLight();" in source
    assert "Avoid a second shadow map" in source
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


if __name__ == "__main__":
    check_graph()
    check_interaction()
    check_mobile_transmission()
    check_speedtree_materials()
    check_tree_graph_illusions()
    check_custom_speedtree_shader()
    print("AARIS_MOBILE_FOLIAGE_CONTRACT_PASS")
