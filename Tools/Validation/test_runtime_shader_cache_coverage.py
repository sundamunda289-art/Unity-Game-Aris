from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
CACHE = SCRIPTS / "AarisMobileShaderResidency.cs"
MIGRATED = (
    "AarisDeathCrateWorld.cs",
    "AarisOfflineFragGrenade.cs",
    "AarisOfflineGlooWall.cs",
    "AarisOfflineTacticalHUD.cs",
    "AarisWeaponInventory.cs",
    "AarisWeaponPickup.cs",
)


class RuntimeShaderCacheCoverageTests(unittest.TestCase):
    def test_only_cache_implementation_calls_shader_find_directly(self):
        direct = []
        for path in SCRIPTS.glob("*.cs"):
            if "Shader.Find(" in path.read_text(errors="ignore"):
                direct.append(path.name)
        self.assertEqual(direct, ["AarisMobileShaderResidency.cs"])

    def test_remaining_material_factories_use_shared_cache(self):
        for name in MIGRATED:
            code = (SCRIPTS / name).read_text(errors="ignore")
            with self.subTest(file=name):
                self.assertNotIn("Shader.Find(", code)
                self.assertIn("AarisShaderCache.Find(", code)

    def test_cache_keeps_shader_names_and_missing_shader_retry_semantics(self):
        code = CACHE.read_text(errors="ignore")
        self.assertIn("Shader resolved = Shader.Find(shaderName);", code)
        self.assertIn("if (resolved != null)", code)
        self.assertIn("shaders[shaderName] = resolved;", code)
        self.assertNotIn("shaders[shaderName] = null", code)


if __name__ == "__main__":
    unittest.main()
