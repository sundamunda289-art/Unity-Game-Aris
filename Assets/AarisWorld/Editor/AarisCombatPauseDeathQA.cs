#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    // Isolated editor QA, never saves any authored Unity scene.
    public static class AarisCombatPauseDeathQA
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static FieldInfo Field(Type type, string name) => type.GetField(name, Private);
        static void Require(bool pass, string label)
        {
            if (!pass) throw new InvalidOperationException(label);
        }

        [MenuItem("Aaris/CI/Verify 100-bot pause and lethal hit gates")]
        public static void Run()
        {
            float previousScale = Time.timeScale;
            var root = new GameObject("Temporary bot pause / fatal QA");
            string result = "RESULT=FAIL";
            try
            {
                Require(Application.isBatchMode && !EditorApplication.isPlaying, "BATCH_ONLY");
                var player = new GameObject("Player");
                player.transform.SetParent(root.transform);
                var hp = player.AddComponent<AarisOfflineHealth>();
                Field(typeof(AarisOfflineHealth), "current").SetValue(hp, 200f);
                var matchEnd = player.AddComponent<AarisOfflineMatchEnd>();
                var directorObject = new GameObject("Director");
                directorObject.transform.SetParent(root.transform);
                var director = directorObject.AddComponent<AarisOfflineBRDirector>();
                Type directorType = typeof(AarisOfflineBRDirector);
                Field(directorType, "started").SetValue(director, true);
                Field(directorType, "playerHealth").SetValue(director, hp);
                Field(directorType, "matchEnd").SetValue(director, matchEnd);
                PropertyInfo engage = directorType.GetProperty("CanEngage", Private);
                Require(engage != null, "ENGAGE_PROPERTY_MISSING");
                Time.timeScale = 1f;
                Require((bool)engage.GetValue(director, null), "NORMAL_COMBAT_BLOCKED");
                Time.timeScale = 0f;
                Require(!(bool)engage.GetValue(director, null), "PAUSED_BOT_CAN_ATTACK");
                Time.timeScale = 1f;
                Require((bool)engage.GetValue(director, null), "RESUME_COMBAT_BLOCKED");

                var attacker = new GameObject("Attacker");
                attacker.transform.SetParent(root.transform);
                attacker.transform.position = new Vector3(7f, 0f, 0f);
                var botObject = new GameObject("Rival");
                botObject.transform.SetParent(root.transform);
                var victim = botObject.AddComponent<AarisOfflineHealth>();
                Field(typeof(AarisOfflineHealth), "current").SetValue(victim, 200f);
                Type rivalType = directorType.Assembly.GetType("Aaris.MapReview.AarisOfflineRival", true);
                var bot = botObject.AddComponent(rivalType);
                Field(rivalType, "health").SetValue(bot, victim);
                MethodInfo damaged = rivalType.GetMethod("OnDamaged", Private);
                Require(damaged != null, "DAMAGE_CALLBACK_MISSING");
                Field(rivalType, "nextThink").SetValue(bot, 30f);
                Field(rivalType, "nextHitReaction").SetValue(bot, -1f);
                Field(rivalType, "nextCoverQuery").SetValue(bot, -1f);
                victim.ApplyDamage(10f, attacker);
                damaged.Invoke(bot, new object[] { victim, 10f, attacker });
                Require((float)Field(rivalType, "nextThink").GetValue(bot) == 0f,
                    "LIVE_BOT_FAILED_TO_REACT");
                Field(rivalType, "turnOffset").SetValue(bot, 17f);
                Field(rivalType, "nextThink").SetValue(bot, 37f);
                Field(rivalType, "nextHitReaction").SetValue(bot, -1f);
                Field(rivalType, "nextCoverQuery").SetValue(bot, -1f);
                victim.ApplyDamage(190f, attacker);
                Require(victim.IsDead, "LETHAL_HP_FAILED");
                damaged.Invoke(bot, new object[] { victim, 190f, attacker });
                Require((float)Field(rivalType, "turnOffset").GetValue(bot) == 17f &&
                    (float)Field(rivalType, "nextThink").GetValue(bot) == 37f &&
                    (float)Field(rivalType, "nextCoverQuery").GetValue(bot) == -1f,
                    "FATAL_HIT_TRIGGERED_UNUSED_COVER_SEARCH");
                result = "RESULT=PASS\nPAUSE_AND_RESUME=PASS\nLIVE_REACTION=PASS\nFATAL_COVER_SKIP=PASS";
            }
            catch (Exception ex) { result += "\n" + ex; }
            finally { Time.timeScale = previousScale; UnityEngine.Object.DestroyImmediate(root); }
            string path = Path.Combine(Path.GetTempPath(), "aaris-combat-pause-death-qa");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "report.txt"), result);
            EditorApplication.Exit(result.StartsWith("RESULT=PASS") ? 0 : 1);
        }
    }
}
#endif
