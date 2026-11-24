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
        assert "99134b1f0c27d54469a840832a28fadf" in s, name
    for name in leaves:
        s = (TREE / name).read_text()
        assert "- EFFECT_SUBSURFACE" in s, name


if __name__ == "__main__":
    check_graph()
    check_interaction()
    check_mobile_transmission()
    check_speedtree_materials()
    print("AARIS_MOBILE_FOLIAGE_CONTRACT_PASS")
