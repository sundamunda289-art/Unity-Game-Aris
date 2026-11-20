using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    public static class AarisStanceTurnChecks
    {
        private delegate Vector3 ResolveContact(Vector3 goal, float speed, float turnRate,
            float weight, bool transitioning, float yaw, ref float plantedYaw,
            ref bool locked, ref Vector3 anchor);
        private delegate float ProjectSupportHeight(float sampledHeight,
            Vector3 sampleFoot, Vector3 resolvedFoot, Vector3 normal);

        // -batchmode -executeMethod Aaris.MapReview.Editor.AarisStanceTurnChecks.Run
        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Batch mode required");
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                CheckFootContact();
                CheckProjectedUprightFootHeight();
                CheckTurnBesideCover();
                Debug.Log("STANCE_TURN_CHECKS_PASS");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static void CheckFootContact()
        {
            var method = typeof(AarisCharacterVisual).GetMethod("ResolveLowStanceFootContact",
                BindingFlags.Static | BindingFlags.NonPublic);
            var resolve = (ResolveContact)Delegate.CreateDelegate(typeof(ResolveContact), method);
            foreach (int fps in new[] { 10, 30, 60, 120 })
            {
                foreach (int direction in new[] { -1, 1 })
                {
                    bool locked = false;
                    float plantedYaw = 0f;
                    Vector3 anchor = Vector3.zero;
                    float startYaw = direction > 0 ? 350f : 10f;
                    resolve(Vector3.zero, 0f, 0f, 1f, false, startYaw, ref plantedYaw,
                        ref locked, ref anchor);
                    Require(locked, "Initial contact");
                    Vector3 goal = Vector3.right * 0.08f;
                    for (int frame = 1; frame <= fps; frame++)
                    {
                        float twist = 40f * frame / fps;
                        float yaw = Mathf.Repeat(startYaw + direction * twist, 360f);
                        Vector3 solved = resolve(goal, 0f, direction * 40f, 1f, false, yaw,
                            ref plantedYaw, ref locked, ref anchor);
                        if (twist < 12f) Require(solved.x < 0.0001f, "Planted boot drift");
                        if (locked) continue;
                        Require(twist >= 31.99f && twist <= 32.01f + 40f / fps,
                            "Slow turn release angle");
                        Require(Mathf.Abs(solved.x - goal.x) < 0.0001f, "Release position");
                        break;
                    }
                    Require(!locked, "Contact remained twisted across yaw wrap");
                }
            }
        }

        private static void CheckProjectedUprightFootHeight()
        {
            var method = typeof(AarisCharacterVisual).GetMethod(
                "ProjectSupportedAnkleHeight",
                BindingFlags.Static | BindingFlags.NonPublic);
            Require(method != null, "Upright slope projection method missing");
            var project = (ProjectSupportHeight)Delegate.CreateDelegate(
                typeof(ProjectSupportHeight), method);
            Vector3 origin = new Vector3(2f, 1f, 3f);
            foreach (int fps in new[] { 10, 30, 60, 120 })
            {
                foreach (float degrees in new[] { 0f, 10f, 25f, 30f, 40f })
                {
                    Vector3 normal = Quaternion.Euler(0f, 0f, degrees) * Vector3.up;
                    foreach (float shift in new[] { -0.08f, 0f, 0.08f })
                    {
                        Vector3 target = origin + Vector3.right * shift;
                        float solved = project(1f, origin, target, normal);
                        float expected = 1f +
                            Mathf.Tan(degrees * Mathf.Deg2Rad) * shift;
                        Require(Mathf.Abs(solved - expected) < 0.0002f,
                            "Upright shoe contact wrong on slope at " + fps + " FPS");
                    }
                }
            }
            // A near-vertical normal must not divide by a near-zero Y.
            Vector3 wall = new Vector3(1f, 0.01f, 0f);
            Require(Mathf.Abs(project(1.5f, origin, origin +
                Vector3.right * 0.1f, wall) - 1.5f) < 0.0001f,
                "Upright slope projection near vertical");
        }

        private static void CheckTurnBesideCover()
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.position = Vector3.down * 0.5f;
            floor.transform.localScale = new Vector3(8f, 1f, 8f);
            var actor = new GameObject("Turn fixture");
            var capsule = actor.AddComponent<CharacterController>();
            capsule.center = Vector3.up * 0.9f;
            capsule.height = 1.8f;
            capsule.radius = 0.26f;
            var battle = actor.AddComponent<AarisBattleActions>();
            // Edit-mode components do not receive gameplay Awake automatically.
            typeof(AarisBattleActions).GetMethod("Awake",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(battle, null);
            Physics.SyncTransforms();
            capsule.Move(Vector3.down * 0.1f);
            Quaternion target = Quaternion.Euler(0f, 90f, 0f);
            Require(Quaternion.Angle(battle.LimitStanceTurn(target, 0.1f), target) < 0.01f,
                "Standing facing");
            Require(battle.TrySetStance(AarisBattleActions.PlayerStance.Crouching), "Enter crouch");
            capsule.Move(Vector3.down * 0.1f);
            Require(Mathf.Abs(battle.LimitStanceTurn(target, 0.1f).eulerAngles.y - 24f) < 0.02f,
                "Crouch rate");
            Require(battle.TrySetStance(AarisBattleActions.PlayerStance.Prone), "Enter prone");

            var peg = new GameObject("Cover edge").AddComponent<SphereCollider>();
            peg.radius = 0.003f;
            Vector3 chest = actor.transform.position + Vector3.up * 0.31f;
            foreach (int sign in new[] { -1, 1 })
            {
                foreach (int end in new[] { -1, 1 })
                {
                    peg.transform.position = chest + Quaternion.Euler(0f, sign * 7.5f, 0f)
                        * Vector3.forward * (end * 0.792f);
                    Physics.SyncTransforms();
                    var destination = Quaternion.Euler(0f, sign * 15f, 0f);
                    Require(battle.CanFaceProne(Vector3.forward) &&
                        battle.CanFaceProne(destination * Vector3.forward), "Both endpoints clear");
                    Require(!battle.CanFaceProne(Quaternion.Euler(0f, sign * 7.5f, 0f)
                        * Vector3.forward), "Blocked intermediate angle");
                    foreach (int fps in new[] { 10, 30, 60, 120 })
                    {
                        actor.transform.rotation = Quaternion.identity;
                        for (int i = 0; i < fps; i++)
                            actor.transform.rotation = battle.LimitStanceTurn(destination, 1f / fps);
                        float reached = Mathf.Abs(Mathf.DeltaAngle(0f, actor.transform.eulerAngles.y));
                        Require(reached > 0.1f && reached < 7.5f, "Turn tunneled or did not advance");
                        var away = battle.LimitStanceTurn(Quaternion.identity, 0.15f);
                        Require(Quaternion.Angle(away, Quaternion.identity) < 0.02f, "Can't turn away");
                    }
                    actor.transform.rotation = Quaternion.identity;
                }
            }
            UnityEngine.Object.DestroyImmediate(peg.gameObject);
            for (int i = 0; i < 6; i++)
                actor.transform.rotation = battle.LimitStanceTurn(target, 0.15f);
            Require(Quaternion.Angle(actor.transform.rotation, target) < 0.03f, "Clear 90-degree turn");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
