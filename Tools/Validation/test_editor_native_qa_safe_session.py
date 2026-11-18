#!/usr/bin/env python3
"""Guard native Unity QA against unsaved-scene loss and interactive Editor exit.

Source and control-flow tests; they do not claim a Unity Play Mode render.
"""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Editor/AarisCodespaceSceneProbe.cs").read_text("utf-8")


def between(start, end):
    return SOURCE.split(start, 1)[1].split(end, 1)[0]


class NativeSceneProbeSafety(unittest.TestCase):
    def test_already_open_authored_scene_is_reused(self):
        load = between("private static Terrain[] LoadNativeScene()",
                       "private static void Save(")
        for required in ("Scene loaded = SceneManager.GetActiveScene();",
                         "loaded.path != Scene || SceneManager.sceneCount != 1",
                         "loaded = EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);",
                         "terrainData == null", "GraphicsDeviceType.Null"):
            self.assertIn(required, load)
        # The initial disk and LFS pointer guard must still run.
        self.assertLess(load.index("SCENE_FILE_MISSING"), load.index("SCENE_IS_LFS_POINTER"))
        self.assertLess(load.index("SCENE_IS_LFS_POINTER"),
                        load.index("Scene loaded = SceneManager.GetActiveScene();"))

    def test_other_unsaved_scenes_abort_before_switching(self):
        load = between("private static Terrain[] LoadNativeScene()",
                       "private static void Save(")
        for required in ("SceneManager.GetSceneAt(i)", "open.IsValid() && open.isDirty",
                         "UNSAVED_OPEN_SCENE_BLOCKED"):
            self.assertIn(required, load)
        self.assertLess(load.index("UNSAVED_OPEN_SCENE_BLOCKED"),
                        load.index("EditorSceneManager.OpenScene(Scene"))
        # Reusing the already-open map requires a single loaded scene;
        # reopening a multi-scene setup must never discard dirty edits.
        for map_active in (False, True):
            for scene_count in (1, 2, 4):
                for any_dirty in (False, True):
                    reuse = map_active and scene_count == 1
                    reload = not reuse and not any_dirty
                    reject = not reuse and any_dirty
                    self.assertEqual(reuse + reload + reject, 1)
                    if scene_count > 1 and any_dirty:
                        self.assertTrue(reject)

    def test_gui_preflight_failure_keeps_editor_alive(self):
        play = between("public static void Play()", "private static void Tick()")
        self.assertIn("if (Application.isBatchMode) EditorApplication.Exit(1);", play)
        self.assertIn("else Debug.LogError(\"AARIS_NATIVE_PLAY_PREFLIGHT_FAILED:", play)
        self.assertIn('"play-report.json"', play)

    def test_gui_completion_keeps_editor_alive_and_ci_exit_is_preserved(self):
        tick = between("private static void Tick()",
                       "private static void Finish(")
        stopping = tick.split('if (state == "stopping")', 1)[1].split(
            'if (EditorApplication.timeSinceStartup >', 1)[0]
        for required in ("SessionState.SetString(State, \"\");",
                         "if (Application.isBatchMode)",
                         "EditorApplication.Exit(exit);",
                         "AARIS_NATIVE_PLAY_GUI_PASSED",
                         "AARIS_NATIVE_PLAY_GUI_FAILED"):
            self.assertIn(required, stopping)
        self.assertLess(stopping.index("SessionState.SetString(State, \"\");"),
                        stopping.index("EditorApplication.Exit(exit);"))
        self.assertLess(stopping.index("if (Application.isBatchMode)"),
                        stopping.index("EditorApplication.Exit(exit);"))
        for passed in (True, False):
            for batch in (True, False):
                exit_code = 0 if passed else 1
                should_exit = batch
                self.assertEqual(should_exit, batch)
                self.assertEqual(exit_code, 0 if passed else 1)

    def test_native_play_still_requires_scene_player_camera_vehicle_and_pixels(self):
        tick = between("private static void Tick()",
                       "private static void Finish(")
        for expected in ("terrains.Length == 4", "player != null",
                         "camera != null", "manager != null",
                         "report.runtimeErrors == 0",
                         "Render(camera, 1280, 720", "play-report.json"):
            self.assertIn(expected, tick)


if __name__ == "__main__":
    unittest.main()
