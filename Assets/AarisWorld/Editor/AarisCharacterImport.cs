using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Deterministic native-FBX import. It only touches the vendored character
    /// directory; terrain meshes, scene assets and existing prefabs are untouched.
    /// </summary>
    public sealed class AarisCharacterImport : AssetPostprocessor
    {
        private const string Prefix = "Assets/Resources/AarisCharacters/Primary/";
        private const string Player = Prefix + "Player.fbx";
        private const string Anim = Prefix + "Animations/";

        private static readonly string[] KayKitClips =
        {
            "walk", "run", "sprint", "crouch", "crouch_walk", "prone", "crawl", "punch"
        };

        private static readonly (string bone, string human)[] KayKitHumanoid =
        {
            ("hips", "Hips"), ("spine", "Spine"), ("chest", "Chest"), ("head", "Head"),
            ("upperarm.l", "LeftUpperArm"), ("lowerarm.l", "LeftLowerArm"), ("hand.l", "LeftHand"),
            ("upperarm.r", "RightUpperArm"), ("lowerarm.r", "RightLowerArm"), ("hand.r", "RightHand"),
            ("upperleg.l", "LeftUpperLeg"), ("lowerleg.l", "LeftLowerLeg"), ("foot.l", "LeftFoot"),
            ("upperleg.r", "RightUpperLeg"), ("lowerleg.r", "RightLowerLeg"), ("foot.r", "RightFoot"),
            ("toes.l", "LeftToes"), ("toes.r", "RightToes")
        };

        private void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Prefix, StringComparison.Ordinal) ||
                !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return;

            var importer = assetImporter as ModelImporter;
            if (importer == null) return;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.autoGenerateAvatarMappingIfUnspecified = true;
            bool isMotion = assetPath.StartsWith(Anim, StringComparison.Ordinal);
            string clipName = Path.GetFileNameWithoutExtension(assetPath);
            bool kayKit = isMotion && Array.IndexOf(KayKitClips, clipName) >= 0;
            if (kayKit)
            {
                var humanoid = importer.humanDescription;
                humanoid.human = KayKitHumanoid.Select(pair => new HumanBone
                {
                    boneName = pair.bone,
                    humanName = pair.human,
                    limit = new HumanLimit { useDefaultValues = true }
                }).ToArray();
                humanoid.skeleton = Array.Empty<SkeletonBone>();
                importer.humanDescription = humanoid;
            }
            importer.importAnimation = isMotion;
            importer.materialImportMode = isMotion
                ? ModelImporterMaterialImportMode.None
                : ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.importBlendShapes = true;
            if (isMotion)
            {
                // In-place human motions. The CharacterController, not the
                // animation track, controls x/z/y world-space displacement.
                var clips = importer.defaultClipAnimations;
                bool loop = assetPath.EndsWith("idle.fbx", StringComparison.OrdinalIgnoreCase)
                    || assetPath.EndsWith("walk.fbx", StringComparison.OrdinalIgnoreCase)
                    || assetPath.EndsWith("run.fbx", StringComparison.OrdinalIgnoreCase)
                    || assetPath.EndsWith("crouch.fbx", StringComparison.OrdinalIgnoreCase)
                    || assetPath.EndsWith("crouch_walk.fbx", StringComparison.OrdinalIgnoreCase)
                    || assetPath.EndsWith("jump.fbx", StringComparison.OrdinalIgnoreCase)
                    || (kayKit && clipName != "punch");
                foreach (var clip in clips)
                {
                    clip.loopTime = loop;
                    clip.loopPose = loop;
                    clip.lockRootRotation = true;
                    clip.lockRootHeightY = true;
                    clip.lockRootPositionXZ = true;
                    // Primary and KayKit clips must preserve authored forward
                    // orientation; averaging hips across cycles can bake a
                    // crooked root direction into in-place walk/run/jump.
                    clip.keepOriginalPositionY = true;
                    clip.keepOriginalOrientation = true;
                    if (kayKit)
                        clip.name = clipName;
                }
                if (clips.Length > 0) importer.clipAnimations = clips;
            }
            importer.addCollider = false;
            importer.isReadable = false;
            importer.optimizeMeshPolygons = true;
            importer.optimizeMeshVertices = true;
            importer.importCameras = false;
            importer.importLights = false;
            // Root motion is implemented by AarisThirdPersonPlayer's controller,
            // never a second transform-driven animation controller.
        }

        [MenuItem("Aaris/Validate Vendored Male Character")]
        public static void Validate()
        {
            string[] files =
            {
                "idle.fbx", "walk.fbx", "run.fbx", "jump.fbx",
                "jump_start.fbx", "jump_land.fbx", "crouch.fbx",
                "crouch_walk.fbx", "punch.fbx", "shoot.fbx",
                "sprint.fbx", "prone.fbx", "crawl.fbx"
            };
            if (!File.Exists(Player))
                throw new InvalidOperationException("Player.fbx is not committed locally. Run the CC0 vendor pipeline.");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Player);
            if (model == null) throw new InvalidOperationException("Unable to load Player.fbx as a Unity model.");
            var avatar = AssetDatabase.LoadAllAssetsAtPath(Player).OfType<Avatar>()
                .FirstOrDefault(x => x != null && x.isValid && x.isHuman);
            if (avatar == null)
                throw new InvalidOperationException("Player.fbx lacks valid Unity Humanoid Avatar. Fix rig mapping.");

            foreach (string name in files)
            {
                string path = Anim + name;
                if (!File.Exists(path))
                    throw new InvalidOperationException("Locally vendored animation missing: " + path);
                var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                    .Where(c => c != null && !c.legacy && c.humanMotion && c.length > 0.04f).ToArray();
                if (clips.Length == 0)
                    throw new InvalidOperationException("No valid retargetable animation clip: " + name);
            }
            if (AssetDatabase.LoadAssetAtPath<MonoScript>("Assets/AarisWorld/Scripts/AarisCharacterVisual.cs") == null)
                throw new InvalidOperationException("AarisCharacterVisual.cs is missing.");
            Debug.Log("Aaris character verified: model=1, Humanoid Avatar=valid, 13 animation FBXs. Runtime requires no external URLs.");
        }
    }
}
