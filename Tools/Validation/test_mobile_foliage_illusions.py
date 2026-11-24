"""Static ShaderGraph source test, not native Unity compiler proof."""
import json
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
S = ROOT / "Assets/TerrainDemoScene_URP/ShaderGraphs"
def graph(name):
    src = (S / name).read_text()
    dec = json.JSONDecoder()
    idx = 0
    objects = []
    while idx < len(src):
        while idx < len(src) and src[idx].isspace():
            idx += 1
        if idx == len(src):
            break
        obj, idx = dec.raw_decode(src, idx)
        objects.append(obj)
    nodes = {x["m_ObjectId"]: x for x in objects if "m_ObjectId" in x}
    assert len(nodes) == len([x for x in objects if "m_ObjectId" in x])
    doc = objects[0]
    for e in doc["m_Edges"]:
        for side in ("m_OutputSlot", "m_InputSlot"):
            ep = e[side]
            node = nodes[ep["m_Node"]["m_Id"]]
            assert any(nodes[slot["m_Id"]]["m_Id"] == ep["m_SlotId"] for slot in node["m_Slots"])
    return doc, nodes
def function(name, fname, expected):
    d,nodes=graph(name)
    funcs=[x for x in nodes.values() if x.get("m_FunctionName")==fname]
    assert len(funcs)==1
    fn=funcs[0]
    inputs=[e for e in d["m_Edges"] if e["m_InputSlot"]["m_Node"]["m_Id"]==fn["m_ObjectId"]]
    outputs=[e for e in d["m_Edges"] if e["m_OutputSlot"]["m_Node"]["m_Id"]==fn["m_ObjectId"]]
    assert len(inputs)>=expected and outputs
    return fn
leaf=function("SpeedTreeURP_Branch.shadergraph","AarisLeafIllusion",5)
bark=function("SpeedTreeURP_Bark.shadergraph","AarisBarkRootIllusion",4)
grass=function("TerrainGrass.shadergraph","AarisGrassInteract",3)
assert leaf["m_SourceType"] == 1 and bark["m_SourceType"] == 1
assert grass["m_SourceType"] == 0
assert grass["m_FunctionSource"] == "9a1dd51c027549e9ac99361329e3f402"
meta=(S/"CustomFunctions/AarisGrassInteraction.hlsl.meta").read_text()
assert "guid: " + grass["m_FunctionSource"] in meta
hlsl=(S/"CustomFunctions/AarisGrassInteraction.hlsl").read_text()
assert "float4 _AarisGrassInteract;" in hlsl and "TransformWorldToObjectDir" in hlsl
pseudo=(S/"CustomFunctions/PseudoSubsurface.hlsl").read_text()
assert "acos(" not in pseudo.split("void PseudoSubsurface_half")[1] and "pow(" not in pseudo.split("void PseudoSubsurface_half")[1]
player=(ROOT/"Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs").read_text()
assert 'Shader.PropertyToID("_AarisGrassInteract")' in player
assert 'Shader.SetGlobalVector(GrassInteractShaderId' in player
vehicle=(ROOT/"Assets/AarisWorld/Scripts/AarisVehicleSession.cs").read_text()
assert 'Shader.PropertyToID("_AarisGrassInteract")' in vehicle
assert 'new Vector4(pos.x, pos.y, pos.z, 2.25f)' in vehicle
assert 'Shader.SetGlobalVector(GrassInteractShaderId, Vector4.zero)' in vehicle
print("AARIS_FOLIAGE_STATIC_SHADERGRAPH_PASS")
