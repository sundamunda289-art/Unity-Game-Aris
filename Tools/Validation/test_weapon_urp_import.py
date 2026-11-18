#!/usr/bin/env python3
"""Guard the visual-only textured gun URP fallback; no weapon mechanics changed."""
from pathlib import Path
import unittest
R=Path(__file__).resolve().parents[2]
P=R/"Assets/AarisWorld/Scripts/AarisWeaponInventory.cs"
class GunPbrImport(unittest.TestCase):
 def test_urp_conversion_preserves_authored_maps(self):
  src=P.read_text()
  method=src.split("private static Material ConvertedWeaponFinish(",1)[1].split(
   "internal static Quaternion ImportedModelRotation(",1)[0]
  for value in ('if (HasAuthoredTexture(source))',
    'Shader.Find("Universal Render Pipeline/Lit")',
    'shaderName == "Universal Render Pipeline/Lit"',
    'source.GetTextureScale(texProperty)',
    'source.GetTextureOffset(texProperty)',
    'converted.SetTexture("_BumpMap"',
    'converted.EnableKeyword("_NORMALMAP")',
    'converted.SetTexture("_MetallicGlossMap"',
    'converted.EnableKeyword("_METALLICSPECGLOSSMAP")',
    'weaponFinishCache[source] = converted;',
    'return converted;'):
   self.assertIn(value,method)
  self.assertNotIn('renderer.material',method)
  self.assertNotIn('new GameObject',method)
 def test_preserves_weapon_logic_and_single_cache_per_material(self):
  src=P.read_text()
  self.assertIn("private static readonly Dictionary<Material, Material> weaponFinishCache",src)
  for name in ("Reload()","DropCurrent()","SwapWeapons()"):
   self.assertIn(name,src)
  self.assertIn("ApplyTextureAwareWeaponFinish(",src)
 def test_invisible_weapon_meshes_cannot_pass_bounds_validation(self):
  pickup=(R/"Assets/AarisWorld/Scripts/AarisWeaponPickup.cs").read_text("utf-8")
  source=pickup.split("internal static bool TryMeasure(",1)[1].split(
   "internal static class AarisLootBeaconMaterial",1)[0]
  for required in ("!renderer.enabled", "renderer.forceRenderingOff",
                   "!renderer.gameObject.activeInHierarchy",
                   "skinned.sharedMesh.vertexCount <= 0",
                   "meshFilter.sharedMesh.vertexCount <= 0",
                   "renderer is MeshRenderer",
                   "return found && bounds.size.sqrMagnitude > 0.000001f;"):
   self.assertIn(required,source)
  for forbidden in ("renderer.bounds", "GetComponentsInChildren", "new Material("):
   self.assertNotIn(forbidden,source)
  inventory=(R/"Assets/AarisWorld/Scripts/AarisWeaponInventory.cs").read_text("utf-8")
  self.assertIn("AarisWeaponMeshBounds.TryMeasure(root.transform,",inventory)
  self.assertIn("AarisWeaponMeshBounds.TryMeasure(transform,",pickup)

 def test_unity_editor_checks_real_imported_model_scale(self):
  editor=(R/"Assets/AarisWorld/Editor/AarisWeaponImportValidation.cs").read_text("utf-8")
  self.assertIn("ValidateVisibleScale(spec, model);", editor)
  self.assertIn("renderer.transform.TransformPoint(local)", editor)
  self.assertIn("renderer.forceRenderingOff", editor)
  self.assertIn("float groundLength = longest * scale;", editor)
  self.assertIn("float heldLength = groundLength * spec.handScale;", editor)
  self.assertIn("AARIS_WEAPON_SCALE_PASS:", editor)
  self.assertIn("UnityEngine.Object.DestroyImmediate(auditRoot);", editor)
  self.assertNotIn("renderer.bounds;", editor)

if __name__=="__main__":
 unittest.main()
