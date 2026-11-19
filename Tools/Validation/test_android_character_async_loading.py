#!/usr/bin/env python3
"""Regression checks for Android-only full-fidelity asynchronous rig loading."""
from pathlib import Path
import unittest
S=(Path(__file__).resolve().parents[2]/'Assets/AarisWorld/Scripts/AarisCharacterVisual.cs').read_text()
class AsyncCharacterBootTests(unittest.TestCase):
    def test_android_uses_sequential_async_loading(self):
        body=S.split('private IEnumerator LoadCharacterAsynchronously()',1)[1].split('#endif',1)[0]
        for required in ('Resources.LoadAsync<GameObject>(candidate)', 'yield return request;', 'if (!isActiveAndEnabled) yield break;', 'request.asset as GameObject', 'yield break;'):
            self.assertIn(required,body)
        for name in ('PrimaryModel','PreviousPrimaryModel','LegacyPrimaryModel','FallbackModel'):
            self.assertIn(name,body)
        self.assertEqual(body.count('Resources.LoadAsync<GameObject>'),1)
    def test_editor_preview_and_full_quality_remain(self):
        self.assertIn('TryInstantiateCharacter(PrimaryModel)',S)
        self.assertIn('FinishVisualSetup();',S)
        self.assertIn('TryApplySkin();',S)
        self.assertIn('if (TryPrepareAnimatedBody()) return true;',S)
        self.assertIn('Resources.LoadAll<Avatar>(resourcePath)',S)
    def test_android_sync_prefab_load_is_compile_gated_out(self):
        body=S.split('private bool TryInstantiateCharacter(',1)[1].split('visual = Instantiate(',1)[0]
        self.assertIn('#if !UNITY_ANDROID || UNITY_EDITOR',body)
        self.assertIn('Resources.Load<GameObject>(resourcePath)',body)
if __name__=='__main__':
    unittest.main()
