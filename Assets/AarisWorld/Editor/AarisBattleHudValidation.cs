using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Real Unity-only import smoke gate for the 2 km battle HUD upgrade.
    /// Run after Git LFS hydration, in a legitimately licensed Unity Editor:
    /// -batchmode -quit -projectPath . -executeMethod Aaris.MapReview.Editor.AarisBattleHudValidation.Run -logFile -
    /// Source/CI checks alone cannot certify that Humanoid clips render.
    /// </summary>
    public static class AarisBattleHudValidation
    {
        [MenuItem("Aaris/Validate Battle HUD And Player")]
        public static void Run()
        {
            AarisExplorerBuild.ValidateSavedScene();
            var motor = UnityEngine.Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (motor == null || motor.GetComponent<CharacterController>() == null)
                throw new InvalidOperationException("The native scene has no saved player motor/collider.");

            string root = "Assets/AarisWorld/Scripts/";
            string[] scripts =
            {
                "AarisBattleHUD.cs",
                "AarisHudButton.cs",
                "AarisHudGlyphs.cs",
                "AarisBattleActions.cs",
                "AarisEquippedGun.cs",
                "AarisThirdPersonPlayer.cs",
                "AarisCharacterVisual.cs"
            };
            foreach (string file in scripts)
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(root + file);
                if (script == null || script.GetClass() == null)
                    throw new InvalidOperationException("Unity failed to compile/import: " + root + file);
            }

            // Unity Editor import check in addition to the static Resources
            // path check: the exact shader needed by Android HUDs must compile.
            Shader hudShader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Assets/Resources/AarisHUDOverlay.shader");
            if (hudShader == null || hudShader.name != "Aaris/UI/MobileOverlay")
                throw new InvalidOperationException("Build-retained mobile UI shader failed Unity import.");
            if (!hudShader.isSupported)
                throw new InvalidOperationException("Mobile UI overlay shader is unsupported by the active graphics device.");

            string path = "Assets/Resources/AarisCharacters/Primary/Player.fbx";
            if (!File.Exists(path)) throw new InvalidOperationException("Local male FBX missing.");
            using (var stream = File.OpenRead(path))
            {
                byte[] data = new byte[Mathf.Min(64, (int)stream.Length)];
                stream.Read(data, 0, data.Length);
                if (System.Text.Encoding.ASCII.GetString(data).StartsWith("version https://git-lfs"))
                    throw new InvalidOperationException("Male FBX is an LFS pointer, not hydrated bytes.");
            }
            AarisCharacterImport.Validate();

            var capsule = motor.GetComponent<CharacterController>();
            if (Mathf.Abs(capsule.height - 1.78f) > 0.005f)
                throw new InvalidOperationException("Standing collider no longer matches the native 1.78m player.");
            Debug.Log("AARIS_BATTLE_HUD_IMPORT_PASS: native 2km scene, one player motor, " +
                      "compiled HUD/state/weapon scripts, hydrated Humanoid FBX and locomotion clips.");
            Debug.Log("RUNTIME_QA_STILL_REQUIRED: inspect touch HUD positioning, 5 gestures, actual melee pose, " +
                      "headroom stance, walk on slope and phone device FPS in Play mode.");
        }
    }
}
