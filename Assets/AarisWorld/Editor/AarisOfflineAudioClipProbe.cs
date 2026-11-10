using System;
using UnityEditor;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Native Unity import gate for the 42 on-device offline SFX clips.
    /// Run from an isolated review worktree; never modify source or scene.
    /// This verifies actual imported AudioClips, not headphone playback.
    /// </summary>
    public static class AarisOfflineAudioClipProbe
    {
        private static readonly string[] Synthetic = {
            "plane_engine_loop",
            "jump_whoosh",
            "wind_rush_loop",
            "parachute_open",
            "parachute_land",
            "step_dirt",
            "step_road",
            "step_water",
            "jump_start",
            "land_thud",
            "smg_shot",
            "ar_shot",
            "battle_rifle_shot",
            "sniper_shot",
            "shotgun_shot",
            "bolt_reload",
            "shell_drop",
            "bullet_impact",
            "punch_hit",
            "grenade_pin",
            "grenade_throw",
            "grenade_bounce",
            "grenade_boom",
            "car_start",
            "car_engine_loop",
            "tire_skid",
            "car_crash",
        };
        private static readonly string[] RecordedCC0 = {
            "footstep_concrete_000",
            "footstep_concrete_001",
            "footstep_concrete_002",
            "footstep_concrete_003",
            "footstep_concrete_004",
            "footstep_grass_000",
            "footstep_grass_001",
            "footstep_grass_002",
            "footstep_grass_003",
            "footstep_grass_004",
            "impactMetal_light_000",
            "impactPunch_medium_000",
            "impactPunch_medium_001",
            "impactPunch_medium_002",
            "impactSoft_heavy_000",
        };

        public static void Verify()
        {
            try
            {
                int count = 0;
                foreach (string name in Synthetic)
                {
                    Check("Assets/Resources/SFX/" + name + ".wav");
                    count++;
                }
                foreach (string name in RecordedCC0)
                {
                    Check("Assets/Resources/SFX/CC0/" + name + ".ogg");
                    count++;
                }
                if (count != 42)
                    throw new InvalidOperationException("INCORRECT_SOUND_COUNT");
                Debug.Log("AARIS_AUDIO_CLIPS_IMPORT_PASS=42");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("AARIS_AUDIO_NATIVE_IMPORT_GATE_FAILED:" +
                    (e is InvalidOperationException ? "VALIDATION" : "UNITY_IMPORT"));
                EditorApplication.Exit(1);
            }
        }

        private static void Check(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (importer == null || clip == null ||
                clip.samples < 256 || clip.channels < 1 || clip.channels > 2 ||
                clip.frequency < 8000 || clip.frequency > 96000 ||
                clip.length <= 0.005f || clip.length > 10f)
                throw new InvalidOperationException("INVALID_CLIP");
        }
    }
}
