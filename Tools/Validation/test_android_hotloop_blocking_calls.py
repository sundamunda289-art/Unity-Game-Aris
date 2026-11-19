#!/usr/bin/env python3
"""Adversarial tests for direct Unity hot-callback blocking-call guard."""
import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "Tools/Validation/check_android_hotloop_blocking_calls.py"
spec = importlib.util.spec_from_file_location("hotloop_guard", SOURCE)
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


def problems(code):
    return guard.check_source(code, "sample.cs")[0]


class HotCallbackGuardTests(unittest.TestCase):
    def test_all_existing_gameplay_scripts_pass(self):
        files = sorted((ROOT / "Assets/AarisWorld/Scripts").glob("*.cs"))
        callbacks = 0
        self.assertGreater(len(files), 25)
        for file in files:
            errors, count = guard.check_source(file.read_text(encoding="utf-8"), file.name)
            self.assertEqual(errors, [], file.name)
            callbacks += count
        self.assertGreaterEqual(callbacks, 30)

    def test_flags_sync_resources_load_and_loadall(self):
        code = """class A { void Update() {
            var x = Resources.Load<GameObject>("x");
            var y = Resources.LoadAll<AudioClip>("x");
        }}"""
        detected = problems(code)
        self.assertEqual(len(detected), 2)
        self.assertTrue(all("sync Resources.Load/LoadAll" in x for x in detected))

    def test_allows_async_load_and_startup_sync_fallback(self):
        code = """class A {
           void Start() { var x = Resources.Load<GameObject>("x"); }
           void Update() { var q = Resources.LoadAsync<GameObject>("x"); }
           void LateUpdate() { if (ready) Yield(); }
        }"""
        self.assertEqual(problems(code), [])

    def test_flags_forced_gc_blocking_wait_and_disk_io(self):
        code = """class A {
         void FixedUpdate() { GC.Collect(); Task.WaitAll(tasks); }
         void LateUpdate() { File.ReadAllBytes("x"); Read().GetAwaiter().GetResult(); }
        }"""
        found = problems(code)
        self.assertEqual(len(found), 4)
        self.assertTrue(any("forced full GC" in s for s in found))
        self.assertEqual(sum("blocking wait" in s for s in found), 2)
        self.assertTrue(any("synchronous disk I/O" in s for s in found))

    def test_flags_global_scene_search_but_not_cached_lookup(self):
        code = """class A {
         void Update() { var x = GameObject.Find("player"); }
         void LateUpdate() { var y = FindFirstObjectByType<A>(); }
         void Awake() { var x = FindFirstObjectByType<A>(); }
        }"""
        found = problems(code)
        self.assertEqual(len(found), 2)
        self.assertTrue(all("scene-wide object lookup" in s for s in found))

    def test_strings_and_comments_are_not_executable_calls(self):
        code = r'''class A {
          void Update() {
            // Resources.Load<GameObject>("x"); GC.Collect();
            /* File.ReadAllBytes("secret"); */
            string x = "Physics { GC.Collect(); } Resources.LoadAll<X>(q)";
            string y = @"FindFirstObjectByType<X>() "" Resource.Load(z)";
            char c = '}';
            Resources.LoadAsync<GameObject>("safe");
          }
        }'''
        self.assertEqual(problems(code), [])

    def test_nested_braces_and_expression_body(self):
        code = """class A {
         void Update() { if (go) { for (int i=0; i<2; i++) { GC.Collect(); } } }
         void LateUpdate() => Thread.Sleep(20);
        }"""
        found = problems(code)
        self.assertEqual(len(found), 2)
        self.assertIn("sample.cs:2", found[0])
        self.assertIn("sample.cs:3", found[1])

    def test_no_false_positive_on_helper_name_or_similar_symbol(self):
        code = """class A {
          void Update() {
             Resources.LoadAsync<GameObject>("asset");
             ResourcePool.Load<GameObject>("already cached");
             GameObject.FindWithTag("safe") ;
          }
          void Start() { Resources.LoadAll<GameObject>("old fallback"); }
        }"""
        self.assertEqual(problems(code), [])

    def test_reports_unclosed_callback_instead_of_ignoring(self):
        with self.assertRaises(ValueError):
            problems("class A { void Update() { GC.Collect(); ")

    def test_source_line_numbers_preserved_by_comments(self):
        code = 'class A {\n// X\n/* commented\nstuff */\nvoid FixedUpdate() {\n  GC.Collect();\n}\n}'
        self.assertIn("sample.cs:6", problems(code)[0])


if __name__ == "__main__":
    unittest.main(verbosity=2)
