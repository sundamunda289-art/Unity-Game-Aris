"""Static movement-gym isolation checks (not a Unity runtime test)."""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]


class LocomotionGymTests(unittest.TestCase):
    def read(self, path):
        return (ROOT / path).read_text(encoding="utf-8")

    def test_real_player_and_single_scene(self):
        source = self.read("Assets/AarisWorld/Editor/AarisLocomotionGymBuilder.cs")
        self.assertIn('AarisPlayer.prefab', source)
        self.assertIn('AarisThirdPersonPlayer', source)
        self.assertIn('AarisBattleActions', source)
        self.assertIn('AarisLocomotionGymMarker', source)
        self.assertIn('scenes = new[] { ScenePath }', source)
        self.assertIn('com.aaris.locomotiongym', source)
        self.assertIn('BuildTarget.Android', source)
        self.assertIn('AndroidArchitecture.ARMv7', source)
        self.assertIn('AndroidArchitecture.ARM64', source)
        self.assertIn('AARIS_GYM_ARM64', source)
        self.assertIn('ScriptingImplementation.IL2CPP', source)
        self.assertIn('ScriptingImplementation.Mono2x', source)

    def test_stance_uses_production_controller(self):
        source = self.read("Assets/AarisWorld/Scripts/AarisLocomotionGymMarker.cs")
        for op in ('ToggleCrouch()', 'ToggleProne()', 'TryStand()'):
            self.assertIn(op, source)
        motor = self.read("Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs")
        self.assertIn('if (isLocomotionGym)', motor)
        self.assertIn('if (Physics.Raycast(position + Vector3.up * 0.45f,', motor)
        self.assertLess(motor.index('if (isLocomotionGym)\n            {\n                AarisCharacterVisual'),
                        motor.index('AarisBattleHUD.EnsureForPlayer(this);'))

    def test_map_preprocessor_is_narrowly_bypassed(self):
        builder = self.read("Assets/AarisWorld/Editor/AarisLocomotionGymBuilder.cs")
        self.assertIn('enabled.Length == 1 && enabled[0].enabled', builder)
        self.assertIn('Path.GetFileName(outputPath)', builder)
        explorer = self.read("Assets/AarisWorld/Editor/AarisExplorerBuild.cs")
        self.assertIn('AarisLocomotionGymBuilder.IsIsolatedGymBuild', explorer)
        self.assertIn('Configure();', explorer)

    def test_no_other_systems_boot_in_gym(self):
        excluded = (
            "AarisBattleHUD.cs", "AarisWeaponHUD.cs",
            "AarisOfflineDrop.cs", "AarisOfflineBRDirector.cs",
            "AarisRoadsideBikeScenery.cs", "AarisOfflineTacticalHUD.cs",
        )
        for script in excluded:
            data = self.read("Assets/AarisWorld/Scripts/" + script)
            self.assertIn('GetComponent<AarisLocomotionGymMarker>()', data, script)
        motor = self.read("Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs")
        self.assertIn("!isLocomotionGym && GetComponent<AarisAudioDirector>()", motor)

    def test_filtered_staging_and_no_full_map(self):
        shell = self.read("Tools/CI/build_locomotion_gym.sh")
        self.assertIn('Assets/Resources/AarisCharacters', shell)
        self.assertIn('Assets/Resources/AarisMocap', shell)
        self.assertIn('STAGE=', shell)
        self.assertIn('flock -n 9', shell)
        self.assertIn('AarisLocomotionGymBuilder.BuildGymAndroid', shell)
        self.assertNotIn('cp -a "$ROOT/Assets/TerrainDemoScene_URP"', shell)
        self.assertIn('case "${f##*/}" in', shell)


if __name__ == "__main__":
    unittest.main()
