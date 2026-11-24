#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    // Native math verification against the production private C# methods.
    // Combat and visibility are unaffected; tests include the 38m gun,
    // 2.25m melee, facing, and height-independent stance boundaries.
    public static class AarisRivalPostMoveRangeQA
    {
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;

        private static void Check(bool result, string label)
        {
            if (!result) throw new InvalidOperationException(label);
            Debug.Log("AARIS_POST_MOVE_PASS " + label);
        }

        [MenuItem("Aaris/CI/Verify post-movement bot shot range")]
        public static void Run()
        {
            bool pass = false;
            try
            {
                Type rival = typeof(AarisOfflineBRDirector).Assembly.GetType(
                    "Aaris.MapReview.AarisOfflineRival", true);
                MethodInfo attack = rival.GetMethod("IsPostMoveShotInRangeAndFacing", Flags);
                MethodInfo distance = rival.GetMethod("HorizontalShotDistanceSquared", Flags);
                Check(attack != null && distance != null, "production methods found");
                Vector3 zero = Vector3.zero, right = Vector3.right, left = Vector3.left;
                Vector3 targetInside = new Vector3(37.8f, 0f, 0f);
                Vector3 targetOutside = new Vector3(38.4f, 0f, 0f);
                Func<Vector3, Vector3, Vector3, float, bool> fire = (from, facing, to, reach) =>
                    (bool)attack.Invoke(null, new object[] { from, facing, to, reach });
                Check(fire(zero, right, targetInside, 38f),
                    "gun within 38 metres accepted");
                Check(!fire(-right * .7f, right, targetInside, 38f),
                    "retreat beyond 38 metres cannot shoot");
                Check(!fire(zero, right, targetOutside, 38f),
                    "old position outside range cannot shoot");
                Check(fire(right * .8f, right, targetOutside, 38f),
                    "new position inside range can shoot");
                Check(!fire(-right * .4f, right, right * 2.1f, 2.25f),
                    "retreat beyond melee reach cannot punch");
                Check(fire(right * .4f, right, right * 2.5f, 2.25f),
                    "move into melee reach can punch");
                Check(!fire(zero, left, right * 4f, 38f),
                    "wrong facing cannot fire");
                Check(fire(zero, right, right * 4f + Vector3.up * 6f, 38f),
                    "horizontal range remains stance-height independent");
                float sq = (float)distance.Invoke(null, new object[] {
                    right * .8f, targetOutside });
                Check(Mathf.Abs(sq - (37.6f * 37.6f)) < .01f,
                    "virtual reach recomputes after movement");
                pass = true;
            }
            catch (Exception e)
            {
                Debug.LogError("AARIS_POST_MOVE_FAIL " + e);
            }
            Debug.Log("AARIS_POST_MOVE_RESULT=" + (pass ? "PASS" : "FAIL"));
            if (Application.isBatchMode) EditorApplication.Exit(pass ? 0 : 1);
        }
    }
}
#endif
