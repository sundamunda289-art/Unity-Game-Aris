#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Native edit-mode lifecycle test for a recycled passive rival hierarchy.
    /// Uses Unity objects and the real production retirement code, not an FPS model.
    /// </summary>
    public static class AarisRivalRigPoolQA
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private static void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            Debug.Log("AARIS_RIG_POOL_PASS " + message);
        }

        private static FieldInfo Field(Type type, string name, BindingFlags flags)
        {
            FieldInfo field = type.GetField(name, flags);
            if (field == null) throw new InvalidOperationException("Missing " + name);
            return field;
        }

        [MenuItem("Aaris/CI/Verify bounded rival Humanoid rig reuse")]
        public static void Run()
        {
            var directorType = typeof(AarisOfflineBRDirector);
            var rivalType = directorType.Assembly.GetType("Aaris.MapReview.AarisOfflineRival", true);
            var active = Field(directorType, "activeDirector", Static);
            var lastRetire = Field(rivalType, "lastRigRetireFrame", Static);
            object oldActive = active.GetValue(null);
            object oldRetire = lastRetire.GetValue(null);
            GameObject fixture = null;
            bool pass = false;
            try
            {
                fixture = new GameObject("Temp 100-rival rig-pool native QA");
                var director = fixture.AddComponent<AarisOfflineBRDirector>();
                director.enabled = false;
                var from = new GameObject("Rival before retirement");
                from.transform.SetParent(fixture.transform);
                var bot = from.AddComponent(rivalType);
                Field(rivalType, "owner", Instance).SetValue(bot, director);
                var rig = new GameObject("Passive SkinnedMeshRenderer test rig");
                rig.transform.SetParent(from.transform, false);
                Vector3 authoredLocal = new Vector3(.06f, .075f, -.03f);
                Vector3 authoredScale = new Vector3(.87f, .87f, .87f);
                rig.transform.localPosition = authoredLocal;
                rig.transform.localScale = authoredScale;
                var skin = rig.AddComponent<SkinnedMeshRenderer>();
                var animator = rig.AddComponent<Animator>();
                animator.enabled = false;
                skin.forceRenderingOff = true;
                rig.SetActive(false);
                Field(rivalType, "visualRoot", Instance).SetValue(bot, rig);
                Field(rivalType, "visualBuilt", Instance).SetValue(bot, true);
                Field(rivalType, "detailedRenderers", Instance).SetValue(bot,
                    new Renderer[] { skin });
                Field(rivalType, "authoredRendererForcedOff", Instance).SetValue(bot,
                    new bool[] { false });
                Field(rivalType, "detailedAnimator", Instance).SetValue(bot, animator);
                Field(rivalType, "authoredAnimatorEnabled", Instance).SetValue(bot, true);
                Field(rivalType, "rigFarSince", Instance).SetValue(bot, 0f);
                lastRetire.SetValue(null, -1);

                var retire = rivalType.GetMethod("RetireHiddenRig", Instance);
                if (retire == null) throw new InvalidOperationException("Retire method missing");
                retire.Invoke(bot, new object[] { 10f, 650f * 650f });
                Check(Field(rivalType, "visualRoot", Instance).GetValue(bot) == null,
                    "retired bot releases the detailed visual reference");

                int memoryMB = SystemInfo.systemMemorySize;
                if (memoryMB >= 3072)
                {
                    Check(!rig.activeInHierarchy &&
                        rig.transform.parent == director.transform,
                        "inactive skinned rig is retained without render or native physics");
                    Check(!skin.forceRenderingOff && animator.enabled,
                        "authored renderer and Animator flags restored before pooling");
                    var newcomer = new GameObject("Rival after return");
                    newcomer.transform.SetParent(fixture.transform);
                    var take = directorType.GetMethod("TakeRetiredRivalRig", Instance);
                    var recycle = directorType.GetMethod("RecycleRetiredRivalRig", Instance);
                    Check(take != null && recycle != null,
                        "production rig-pool methods are accessible by reflection");
                    GameObject returned = (GameObject)take.Invoke(director,
                        new object[] { newcomer.transform });
                    Check(ReferenceEquals(returned, rig) && returned.activeInHierarchy,
                        "same native hierarchy returned without Instantiate");
                    Check(returned.transform.parent == newcomer.transform &&
                        returned.transform.localPosition == authoredLocal &&
                        returned.transform.localScale == authoredScale,
                        "authored fitted local placement and scale preserved");
                    Check((bool)recycle.Invoke(director, new object[] { returned }),
                        "reentry rig can be safely released again");
                    var clear = directorType.GetMethod("ClearRetiredRivalRigPool", Instance);
                    if (clear == null) throw new InvalidOperationException("Clear method missing");
                    clear.Invoke(director, null);
                    Check(rig == null &&
                        (GameObject)take.Invoke(director, new object[] { newcomer.transform }) == null,
                        "low memory destroys idle rig and prevents reuse");
                }
                else
                {
                    Check(rig != null && rig.transform.parent == from.transform,
                        "2GB memory budget refuses idle hierarchy retention");
                }
                pass = true;
            }
            catch (Exception ex)
            {
                Debug.LogError("AARIS_RIG_POOL_FAIL " + ex);
            }
            finally
            {
                if (fixture != null) Object.DestroyImmediate(fixture);
                active.SetValue(null, oldActive);
                lastRetire.SetValue(null, oldRetire);
            }
            string output = Path.Combine(Path.GetTempPath(), "aaris-rig-pool-native-qa.txt");
            File.WriteAllText(output, "RESULT=" + (pass ? "PASS" : "FAIL"));
            Debug.Log("AARIS_RIG_POOL_RESULT=" + (pass ? "PASS" : "FAIL"));
            if (Application.isBatchMode) EditorApplication.Exit(pass ? 0 : 1);
        }
    }
}
#endif
