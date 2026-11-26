using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>Real PhysX/CharacterController fixtures; no alternate motor.</summary>
    public static class AarisPlayerStanceQA
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private static int assertions;
        private static long allocatedBytes;
        private delegate void Advance(float dt);
        private delegate bool JumpStand();

        [Serializable] private sealed class Report
        {
            public string status;
            public int assertions, frameRates = 4, slopeCases = 9;
            public long steadyStanceManagedBytes;
        }

        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Batch mode required");
            assertions = 0;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                foreach (int fps in new[] { 10, 30, 60, 120 }) CheckTransitions(fps);
                CheckMovingCeiling();
                CheckSlopes();
                Write("passed");
                Debug.Log($"AARIS_PLAYER_STANCE_QA_PASS assertions={assertions} steadyBytes={allocatedBytes}");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Write("failed");
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static void Write(string status)
        {
            string dir = Environment.GetEnvironmentVariable("AARIS_GAMEPLAY_QA_DIR") ??
                "/tmp/aaris-gameplay-qa";
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "player-stance.json"), JsonUtility.ToJson(
                new Report { status = status, assertions = assertions,
                    steadyStanceManagedBytes = allocatedBytes }, true));
        }

        private sealed class Fixture : IDisposable
        {
            internal GameObject floor, actor;
            internal CharacterController capsule;
            internal AarisBattleActions actions;
            internal Advance advance;
            internal JumpStand jumpStand;
            private TerrainData terrainData;

            internal Fixture(float pitch = 0f, int supportKind = 0)
            {
                if (supportKind == 2)
                {
                    terrainData = new TerrainData { heightmapResolution = 33,
                        size = new Vector3(12f, 6f, 12f) };
                    var heights = new float[33, 33];
                    for (int z = 0; z < 33; z++)
                    for (int x = 0; x < 33; x++)
                        heights[z, x] = 0.5f - (z / 32f - 0.5f) * 2f *
                            Mathf.Tan(pitch * Mathf.Deg2Rad);
                    terrainData.SetHeights(0, 0, heights);
                    floor = Terrain.CreateTerrainGameObject(terrainData);
                    floor.transform.position = new Vector3(-6f, -3f, -6f);
                }
                else
                {
                    floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    floor.transform.rotation = Quaternion.Euler(pitch, 0f, 0f);
                    floor.transform.localScale = new Vector3(12f, 1f, 12f);
                    floor.transform.position = -floor.transform.up * 0.5f;
                    if (supportKind == 1)
                    {
                        Mesh shape = floor.GetComponent<MeshFilter>().sharedMesh;
                        Object.DestroyImmediate(floor.GetComponent<BoxCollider>());
                        floor.AddComponent<MeshCollider>().sharedMesh = shape;
                    }
                }
                floor.name = "QA real support";
                actor = new GameObject("QA stance actor");
                actor.transform.position = Vector3.up * 0.07f;
                capsule = actor.AddComponent<CharacterController>();
                capsule.height = 1.8f;
                capsule.radius = 0.28f;
                capsule.center = Vector3.up * 0.9f;
                capsule.skinWidth = 0.035f;
                capsule.stepOffset = 0.3f;
                capsule.slopeLimit = 46f;
                actions = actor.AddComponent<AarisBattleActions>();
                typeof(AarisBattleActions).GetMethod("Awake", Private).Invoke(actions, null);
                advance = (Advance)Delegate.CreateDelegate(typeof(Advance), actions,
                    typeof(AarisBattleActions).GetMethod("AdvanceStanceTransition", Private));
                jumpStand = (JumpStand)Delegate.CreateDelegate(typeof(JumpStand), actions,
                    typeof(AarisBattleActions).GetMethod("TryStandForJump", Private));
                Ground();
            }

            internal void Ground()
            {
                Physics.SyncTransforms();
                capsule.Move(Vector3.down * 0.12f);
            }

            internal void Settle(int fps = 60)
            {
                for (int i = 0; i < fps; i++) { advance(1f / fps); Ground(); }
                Require(!actions.IsStanceTransitioning, "Transition did not settle");
            }

            public void Dispose()
            {
                Object.DestroyImmediate(actor);
                Object.DestroyImmediate(floor);
                if (terrainData != null) Object.DestroyImmediate(terrainData);
            }
        }

        private static void CheckTransitions(int fps)
        {
            using (var f = new Fixture())
            {
                Require(!f.actions.TrySetStance((AarisBattleActions.PlayerStance)99), "Invalid stance accepted");
                foreach (var target in new[] { AarisBattleActions.PlayerStance.Crouching,
                    AarisBattleActions.PlayerStance.Prone, AarisBattleActions.PlayerStance.Crouching,
                    AarisBattleActions.PlayerStance.Standing })
                {
                    float previous = f.capsule.height;
                    Require(f.actions.TrySetStance(target), "Requested stance rejected on open floor");
                    Require(Mathf.Abs(f.capsule.height - previous) < 0.00001f,
                        "Stance request teleported capsule height");
                    for (int i = 0; i < fps; i++)
                    {
                        Vector3 position = f.actor.transform.position;
                        float floor = f.capsule.center.y - f.capsule.height * 0.5f;
                        f.advance(1f / fps);
                        Require(Mathf.Abs(f.capsule.center.y - f.capsule.height * 0.5f - floor) < 0.00001f,
                            "Resize moved capsule foot");
                        Require(f.actor.transform.position == position, "Resize moved motor transform");
                        Require(f.actions.CameraEyeHeight <= f.actions.MaximumCameraEyeHeight + 0.00001f,
                            "Camera eye exceeds physical headroom");
                        Require(f.capsule.height >= f.capsule.radius * 2f, "Capsule below diameter");
                        f.Ground();
                    }
                    Require(!f.actions.IsStanceTransitioning && f.actions.MovementStance == target,
                        "Settled movement stance disagrees with capsule");
                }
                Require(f.actions.TrySetStance(AarisBattleActions.PlayerStance.Prone), "Reverse setup");
                f.advance(0.10f);
                f.Ground();
                float interrupted = f.capsule.height;
                Require(f.actions.TryStand(), "Mid-transition reversal rejected");
                Require(Mathf.Abs(f.capsule.height - interrupted) < 0.00001f,
                    "Reversal snapped collider");
                Require(f.actions.MovementStance == AarisBattleActions.PlayerStance.Prone,
                    "Mid-rise capsule acquired standing sprint gait");
                f.Settle(fps);
                Require(f.actions.TrySetStance(AarisBattleActions.PlayerStance.Crouching), "Jump setup");
                f.Settle(fps);
                Require(f.jumpStand() && !f.actions.IsStanceTransitioning &&
                    Mathf.Abs(f.capsule.height - 1.8f) < 0.0001f, "Validated jump stand is incomplete");
                f.Ground();
                Require(Mathf.Abs(f.actions.WeaponRecoilMultiplier - 1f) < 0.0001f, "Standing recoil changed");
                f.actions.TrySetStance(AarisBattleActions.PlayerStance.Crouching);
                f.Settle(fps);
                float crouchRecoil = f.actions.WeaponRecoilMultiplier;
                Require(crouchRecoil < 0.85f && crouchRecoil > 0.80f, "Crouch stability missing");
                f.actions.TrySetStance(AarisBattleActions.PlayerStance.Prone);
                f.Settle(fps);
                Require(f.actions.WeaponRecoilMultiplier < crouchRecoil &&
                    f.actions.WeaponSpreadMultiplier < 0.60f, "Prone stability missing");
                f.capsule.enabled = false;
                Require(f.actions.WeaponRecoilMultiplier == 1f &&
                    !f.actions.TryStand(), "Disabled motor/capsule grants stance benefit");
                f.capsule.enabled = true;
                f.Ground();
                Vector3 groundedPosition = f.actor.transform.position;
                f.capsule.enabled = false;
                f.actor.transform.position = groundedPosition + Vector3.up * 3f;
                f.capsule.enabled = true;
                Physics.SyncTransforms();
                f.capsule.Move(Vector3.down * 0.01f);
                Require(!f.capsule.isGrounded, "Airborne fixture unexpectedly has real contact");
                Require(f.actions.WeaponRecoilMultiplier == 1f &&
                    f.actions.WeaponSpreadMultiplier == 1f,
                    "A falling low capsule retains grounded weapon stability");
                Require(f.jumpStand(), "Clear airborne get-up rejected");
                f.capsule.Move(Vector3.down * 0.01f);
                Require(!f.actions.TrySetStance(AarisBattleActions.PlayerStance.Prone),
                    "Airborne standing capsule can enter prone");
                Require(f.actions.TrySetStance(AarisBattleActions.PlayerStance.Crouching),
                    "Authored airborne crouch permission changed");
                f.advance(0.15f);
                f.capsule.Move(Vector3.down * 0.01f);
                Require(!f.actions.TrySetStance(AarisBattleActions.PlayerStance.Prone),
                    "Airborne crouched capsule can enter prone");
                Require(f.actions.WeaponRecoilMultiplier == 1f &&
                    f.actions.WeaponSpreadMultiplier == 1f, "Air crouch grants braced accuracy");
                f.capsule.enabled = false;
                f.actor.transform.position = groundedPosition;
                f.capsule.enabled = true;
                f.Ground();
                f.actions.TrySetStance(AarisBattleActions.PlayerStance.Prone);
                f.Settle(fps);
                for (int i = 0; i < 20; i++) f.advance(1f / fps);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) f.advance(1f / fps);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                allocatedBytes += bytes;
                Require(bytes == 0, "Steady stance tick allocated managed memory");
            }
        }

        private static void CheckMovingCeiling()
        {
            using (var f = new Fixture())
            {
                f.actions.TrySetStance(AarisBattleActions.PlayerStance.Crouching);
                f.Settle();
                GameObject roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
                try
                {
                    roof.transform.localScale = new Vector3(3f, 0.12f, 3f);
                    roof.transform.position = new Vector3(0f, 1.42f, 0f);
                    Physics.SyncTransforms();
                    Require(!f.actions.TryStand(), "Existing ceiling permits standing");
                    roof.transform.position = new Vector3(0f,
                        f.actor.transform.position.y + 1.8f + 0.04f + 0.06f, 0f);
                    Physics.SyncTransforms();
                    Require(f.actions.TryStand(), "Valid tight headroom rejected at request");
                    f.Settle();
                    Require(f.actions.Stance == AarisBattleActions.PlayerStance.Standing &&
                        Mathf.Abs(f.capsule.height - 1.8f) < 0.0001f,
                        "Progressive check invented a ceiling above its proposed capsule");
                    f.actions.TrySetStance(AarisBattleActions.PlayerStance.Crouching);
                    f.Settle();
                    roof.transform.position += Vector3.up * 3f;
                    Physics.SyncTransforms();
                    Require(f.actions.TryStand(), "Open headroom cannot stand");
                    f.advance(0.06f);
                    float before = f.capsule.height;
                    // Arrives after acceptance, clear of the current capsule.
                    roof.transform.position = new Vector3(0f,
                        f.actor.transform.position.y + before + 0.075f, 0f);
                    Physics.SyncTransforms();
                    f.advance(0.10f);
                    Require(f.capsule.height <= before + 0.0001f, "New ceiling penetrated during rise");
                    Require(f.actions.Stance == AarisBattleActions.PlayerStance.Crouching &&
                        f.actions.MovementStance == AarisBattleActions.PlayerStance.Crouching,
                        "Blocked rise left a standing target or gait");
                    f.Settle();
                    Require(Mathf.Abs(f.capsule.height - 1.14f) < 0.0001f, "Blocked rise cannot recover");
                    roof.transform.position += Vector3.up * 3f;
                    Physics.SyncTransforms();
                    Require(f.actions.TryStand(), "Recovered stance cannot be requested again");
                    f.Settle();
                }
                finally { Object.DestroyImmediate(roof); }
            }
        }

        private static void CheckSlopes()
        {
            foreach (int supportKind in new[] { 0, 1, 2 })
            foreach (float slope in new[] { -24f, 0f, 24f })
            {
                using (var f = new Fixture(slope, supportKind))
                {
                    Require(f.capsule.isGrounded, "Slope fixture has no actual controller contact");
                    Require(f.actions.TrySetStance(AarisBattleActions.PlayerStance.Prone),
                        "Walkable slope prevents prone entry");
                    f.Settle();
                    Vector3 normal = (Vector3)typeof(AarisBattleActions).GetProperty("ProneGroundNormal",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.actions);
                    Require(Vector3.Angle(normal, Quaternion.Euler(slope, 0f, 0f) * Vector3.up) < 0.15f,
                        "Prone support normal disagrees with actual mesh");
                    Require(f.actions.CanAdvanceProne(f.actor.transform.position + Vector3.forward * 0.04f,
                        Vector3.forward), "Crawling rejected on the same clear support plane");
                    Require(Vector3.Angle(f.actor.transform.up, Vector3.up) < 0.001f,
                        "Slope alignment tilted the physical player");
                    GameObject obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    try
                    {
                        Vector3 head = f.actor.transform.position + Vector3.up * 0.31f +
                            Vector3.ProjectOnPlane(Vector3.forward, normal).normalized * 0.55f;
                        obstacle.transform.position = head;
                        obstacle.transform.localScale = new Vector3(0.18f, 0.8f, 0.18f);
                        Physics.SyncTransforms();
                        Require(!f.actions.CanFaceProne(Vector3.forward), "Slope footprint crossed head wall");
                        obstacle.transform.position = head + Vector3.up * 0.19f;
                        obstacle.transform.localScale = new Vector3(0.5f, 0.08f, 0.5f);
                        Physics.SyncTransforms();
                        Require(!f.actions.CanFaceProne(Vector3.forward), "Slope footprint crossed overhead slab");
                    }
                    finally { Object.DestroyImmediate(obstacle); }
                }
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
    }
}
