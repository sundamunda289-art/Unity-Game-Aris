#!/usr/bin/env python3
"""Regression for an Android-only coroutine accidentally compiled in Unity Editor.

The Unity Editor C# compile must not reference a method hidden behind
#if UNITY_ANDROID && !UNITY_EDITOR. This source check supplements (not replaces)
real Unity compilation and Android release verification.
"""
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs").read_text(encoding="utf-8")

ANDROID_PLAYER_GUARD = "UNITY_ANDROID && !UNITY_EDITOR"


def active_preprocessor_conditions(marker):
    """Return conditional nesting where an exact C# source marker appears."""
    conditions = []
    occurrences = []
    for raw in SOURCE.splitlines():
        line = raw.strip()
        if line.startswith("#if "):
            conditions.append(line[4:].strip())
        elif line.startswith("#elif "):
            if not conditions:
                raise AssertionError("unexpected #elif")
            conditions[-1] = line[6:].strip()
        elif line == "#else":
            if not conditions:
                raise AssertionError("unexpected #else")
            conditions[-1] = "else(" + conditions[-1] + ")"
        elif line == "#endif":
            if not conditions:
                raise AssertionError("unexpected #endif")
            conditions.pop()
        elif marker in raw:
            occurrences.append(tuple(conditions))
    if conditions:
        raise AssertionError("unclosed preprocessor conditional")
    return occurrences


class AndroidOnlyCoroutineEditorCompileGuard(unittest.TestCase):
    def assert_android_only(self, marker):
        scopes = active_preprocessor_conditions(marker)
        self.assertEqual(len(scopes), 1, "marker must appear exactly once: " + marker)
        self.assertIn(ANDROID_PLAYER_GUARD, scopes[0],
                      "unguarded Android-only reference in Editor compile: " + marker)

    def test_async_document_loader_declaration_is_android_only(self):
        self.assert_android_only("internal static IEnumerator LoadForAndroidAsync()")

    def test_coroutine_declaration_is_android_only(self):
        # This exact missing guard caused a real Unity 6000.3.26f1 CS0117
        # in a divergent Codespace branch; canonical main is already correct.
        self.assert_android_only("private IEnumerator InitializeAndroid()")

    def test_android_only_call_sites_cannot_enter_editor_compile(self):
        self.assert_android_only("world.StartCoroutine(world.InitializeAndroid());")
        self.assert_android_only("yield return AarisLootDocument.LoadForAndroidAsync();")
        self.assert_android_only("yield return AarisWeaponCatalog.LoadForMatchAsync();")


if __name__ == "__main__":
    unittest.main()
