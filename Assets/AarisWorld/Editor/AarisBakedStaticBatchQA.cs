#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>Real Play Mode/Camera.Render handoff checks; no project writes.</summary>
    [InitializeOnLoad]
    public static class AarisBakedStaticBatchQA
    {
        private const string Session = "AarisBakedStaticBatchQA";
        private const string Output = "/tmp/aaris-baked-static-batch-qa";
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly List<string> results = new List<string>(64);
        private static int checks, failures;
        private static Camera camera;
        private static RenderTexture target;
        private static AsyncOperation pendingUnload;
        private static Fixture unloadFixture;

        static AarisBakedStaticBatchQA()
        {
            EditorApplication.update += Tick;
            Application.logMessageReceived += RuntimeLog;
        }

        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("BATCH_ONLY");
            foreach (string argument in Environment.GetCommandLineArgs())
                if (argument.Equals("-quit", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("PLAY_QA_REQUIRES_NO_QUIT_FLAG");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("EDITOR_BUSY");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("UNSAVED_SCENE");
            Directory.CreateDirectory(Output);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Session + ".Active", true);
            SessionState.SetBool(Session + ".Executed", false);
            SessionState.SetBool(Session + ".Finishing", false);
            SessionState.SetInt(Session + ".Errors", 0);
            SessionState.SetInt(Session + ".Exit", 1);
            SessionState.SetString(Session + ".FirstError", "");
            SessionState.SetFloat(Session + ".Deadline", (float)EditorApplication.timeSinceStartup + 180f);
            File.WriteAllText(Path.Combine(Output, "result.txt"), "STARTING_PLAYMODE\n");
            EditorApplication.isPlaying = true;
        }

        private static void RuntimeLog(string condition, string stack, LogType type)
        {
            if (!SessionState.GetBool(Session + ".Active", false) ||
                (type != LogType.Error && type != LogType.Assert && type != LogType.Exception)) return;
            int errors = SessionState.GetInt(Session + ".Errors", 0);
            SessionState.SetInt(Session + ".Errors", errors + 1);
            if (errors == 0) SessionState.SetString(Session + ".FirstError", condition + "\n" + stack);
        }

        private static void Tick()
        {
            if (!SessionState.GetBool(Session + ".Active", false)) return;
            if (SessionState.GetBool(Session + ".Finishing", false))
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    if (EditorApplication.timeSinceStartup < SessionState.GetFloat(Session + ".Deadline", 0) + 30f)
                        return;
                    SessionState.SetInt(Session + ".Exit", 1);
                    File.AppendAllText(Path.Combine(Output, "result.txt"), "FAIL PLAYMODE_EXIT_TIMEOUT\n");
                }
                int errors = SessionState.GetInt(Session + ".Errors", 0);
                int exit = errors == 0 ? SessionState.GetInt(Session + ".Exit", 1) : 1;
                File.AppendAllText(Path.Combine(Output, "result.txt"), "PLAYMODE_RUNTIME_ERRORS=" + errors +
                    (errors == 0 ? "" : "\nFIRST_RUNTIME_ERROR=" + SessionState.GetString(Session + ".FirstError", "")) +
                    "\nRESULT=" + (exit == 0 ? "PASS" : "FAIL") + "\n");
                SessionState.SetBool(Session + ".Active", false);
                EditorApplication.Exit(exit);
                return;
            }
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Session + ".Deadline", 0))
            {
                File.AppendAllText(Path.Combine(Output, "result.txt"), "FAIL PLAYMODE_TIMEOUT\n");
                SessionState.SetInt(Session + ".Exit", 1);
                SessionState.SetBool(Session + ".Finishing", true);
                EditorApplication.isPlaying = false;
                return;
            }
            if (!Application.isPlaying || !EditorApplication.isPlaying || Time.frameCount < 2) return;
            if (SessionState.GetBool(Session + ".Executed", false))
            {
                if (pendingUnload != null && pendingUnload.isDone)
                {
                    pendingUnload = null;
                    Check("actual_scene_unload_releases_sources_outputs_and_registry", unloadFixture.Root == null &&
                        AarisBakedStaticBatch.RegisteredCohortCount == 0 &&
                        AarisBakedStaticBatch.TotalActiveCombinedRenderers == 0 &&
                        AarisBakedStaticBatch.TotalSuppressedSourceRenderers == 0);
                    unloadFixture.Dispose(); unloadFixture = null;
                    Finish();
                }
                return;
            }
            SessionState.SetBool(Session + ".Executed", true);
            Execute();
        }

        private static void Execute()
        {
            results.Clear(); checks = failures = 0;
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    throw new InvalidOperationException("REAL_GRAPHICS_CONTEXT_REQUIRED");
                camera = new GameObject("CohortQACamera").AddComponent<Camera>();
                camera.enabled = false;
                camera.transform.position = new Vector3(0, 2, -10);
                camera.transform.LookAt(Vector3.zero);
                camera.orthographic = true; camera.orthographicSize = 5;
                camera.nearClipPlane = .1f; camera.farClipPlane = 30;
                camera.clearFlags = CameraClearFlags.SolidColor;
                target = new RenderTexture(32, 32, 24, RenderTextureFormat.ARGB32);
                target.Create(); camera.targetTexture = target;
                Cases();
                AllocationCheck();
                Check("all_fixture_cohort_owners_released", AarisBakedStaticBatch.RegisteredCohortCount == 0 &&
                    AarisBakedStaticBatch.TotalActiveCombinedRenderers == 0 &&
                    AarisBakedStaticBatch.TotalSuppressedSourceRenderers == 0 &&
                    AarisBakedStaticBatch.TotalRejectedCohorts == 0);
                Scene unloadScene = SceneManager.CreateScene("CohortUnloadQA");
                unloadFixture = new Fixture();
                SceneManager.MoveGameObjectToScene(unloadFixture.Root, unloadScene);
                unloadFixture.Activate(); Render();
                Check("scene_owned_cohort_admitted_before_unload", unloadFixture.Output.enabled);
                pendingUnload = SceneManager.UnloadSceneAsync(unloadScene);
                if (pendingUnload == null) throw new InvalidOperationException("UNLOAD_DID_NOT_START");
                return;
            }
            catch (Exception error) { failures++; results.Add("FAIL HARNESS_EXCEPTION " + error); }
            Finish();
        }

        private static void Finish()
        {
            if (unloadFixture != null) { unloadFixture.Dispose(); unloadFixture = null; }
            if (camera != null) { camera.targetTexture = null; Object.DestroyImmediate(camera.gameObject); }
            if (target != null) { target.Release(); Object.DestroyImmediate(target); }
            results.Insert(0, "UNITY=" + Application.unityVersion + " MODE=PLAY CHECKS=" + checks + " FAILURES=" + failures);
            results.Add("SCOPE=actual Camera.Render callbacks, atomic static presentation handoff, source flags/colliders, native static-batch compatibility and fail-open lifecycle; not whole-game GC or Android performance");
            File.WriteAllLines(Path.Combine(Output, "result.txt"), results);
            SessionState.SetInt(Session + ".Exit", failures == 0 ? 0 : 1);
            SessionState.SetBool(Session + ".Finishing", true);
            EditorApplication.isPlaying = false;
        }

        private static void Cases()
        {
            using (var f = new Fixture())
            {
                f.Activate(); Render();
                Check("whole_cohort_admitted", f.Batch.RejectedCohortCount == 0 && f.Output.enabled &&
                    f.First.forceRenderingOff && f.Second.forceRenderingOff && f.First.enabled && f.Second.enabled);
                Check("physics_owners_untouched", f.First.GetComponent<BoxCollider>().enabled &&
                    f.Second.GetComponent<BoxCollider>().enabled);
                f.First.enabled = false; Render();
                Check("mixed_visibility_restores_remaining_original", !f.Output.enabled &&
                    !f.First.enabled && f.Second.enabled && !f.First.forceRenderingOff && !f.Second.forceRenderingOff);
                f.Second.enabled = false; Render();
                Check("hidden_sources_keep_combined_output_hidden", !f.Output.enabled && !f.First.enabled && !f.Second.enabled);
                f.First.enabled = f.Second.enabled = true; Render();
                Check("whole_cohort_readmits_without_mesh_work", f.Output.enabled && f.First.forceRenderingOff && f.Second.forceRenderingOff);
                f.Batch.enabled = false;
                Check("disable_releases_owned_suppression_synchronously", !f.Output.enabled &&
                    !f.First.forceRenderingOff && !f.Second.forceRenderingOff && f.First.enabled && f.Second.enabled);
                f.Batch.enabled = true; Render();
                Check("reenable_revalidates_and_readmits", f.Output.enabled && f.Batch.RejectedCohortCount == 0);
                f.First.gameObject.SetActive(false); Render();
                Check("inactive_source_hierarchy_never_resurrects_in_batch", !f.Output.enabled &&
                    !f.First.gameObject.activeInHierarchy && !f.Second.forceRenderingOff);
            }
            using (var f = new Fixture())
            {
                f.First.forceRenderingOff = true; f.Activate(); Render();
                Check("preexisting_force_off_is_preserved", f.First.forceRenderingOff && !f.Second.forceRenderingOff && !f.Output.enabled);
                f.Batch.enabled = false;
                Check("release_does_not_clear_preexisting_force_off", f.First.forceRenderingOff && !f.Second.forceRenderingOff);
            }
            using (var f = new Fixture())
            {
                f.SourceMesh.UploadMeshData(true); f.OutputMesh.UploadMeshData(true);
                f.Activate(); Render();
                Check("unreadable_assets_need_no_runtime_mesh_readback", !f.SourceMesh.isReadable &&
                    !f.OutputMesh.isReadable && f.Output.enabled && f.Batch.RejectedCohortCount == 0);
            }
            using (var f = new Fixture())
            {
                f.First.gameObject.isStatic = true; f.Second.gameObject.isStatic = true;
                StaticBatchingUtility.Combine(new[] { f.First.gameObject, f.Second.gameObject }, f.Root);
                f.TrackNativeMeshes(); f.Activate(); Render();
                Check("native_static_batch_fixture_is_real", f.First.isPartOfStaticBatch && f.Second.isPartOfStaticBatch);
                Check("native_combined_mesh_substitution_is_accepted", f.Output.enabled && f.Batch.RejectedCohortCount == 0);
            }
            Mutation("source_pose_mutation_fails_open", f => f.First.transform.position += Vector3.right);
            Mutation("output_pose_mutation_fails_open", f => f.Output.transform.position += Vector3.right);
            Mutation("source_mesh_mutation_fails_open", f => f.First.GetComponent<MeshFilter>().sharedMesh = f.OutputMesh);
            Mutation("output_mesh_mutation_fails_open", f => f.Output.GetComponent<MeshFilter>().sharedMesh = f.SourceMesh);
            Mutation("source_material_mutation_fails_open", f => f.First.sharedMaterial = f.NewMaterial());
            Mutation("output_material_mutation_fails_open", f => f.Output.sharedMaterial = f.NewMaterial());
            Mutation("output_force_off_fails_open", f => f.Output.forceRenderingOff = true);
            Mutation("external_source_force_owner_fails_open", f => f.First.forceRenderingOff = false);
            Mutation("output_shadow_state_mutation_fails_open", f => f.Output.shadowCastingMode = ShadowCastingMode.Off);
            Mutation("source_property_block_fails_open", f =>
            { var block = new MaterialPropertyBlock(); block.SetFloat("_Fixture", 1); f.First.SetPropertyBlock(block); });
            using (var f = new Fixture())
            {
                f.Activate(); Object.DestroyImmediate(f.First.gameObject); Render();
                Check("destroyed_source_restores_survivor", f.Batch.RejectedCohortCount == 1 && !f.Output.enabled && !f.Second.forceRenderingOff);
            }
            using (var f = new Fixture())
            {
                f.Batch.Configure(new[] { f.First, f.First }, new[] { f.Output }, "", 2);
                f.Activate(); Render();
                Check("duplicate_source_ownership_rejected", f.Batch.RejectedCohortCount == 1 && !f.Output.enabled && !f.First.forceRenderingOff);
            }
            using (var f = new Fixture())
            {
                f.Second.sharedMaterial = f.NewMaterial(); f.Configure(); f.Activate(); Render();
                Check("missing_material_coverage_rejected", f.Batch.RejectedCohortCount == 1 && !f.Output.enabled && !f.First.forceRenderingOff);
            }
            using (var f = new Fixture())
            {
                LODGroup group = f.First.gameObject.AddComponent<LODGroup>();
                group.SetLODs(new[] { new LOD(.01f, new Renderer[] { f.First }) });
                f.Activate(); Render();
                Check("LOD_membership_is_not_silently_combined", f.Batch.RejectedCohortCount == 1 && !f.Output.enabled);
            }
            using (var f = new Fixture())
            {
                string path = "Assets/AarisWorld/Scenes/Chunks/CohortQA_Detail.unity";
                var a = Fallback(f.First, path); var b = Fallback(f.Second, path);
                f.Configure(path); f.Activate(); Render();
                Check("exact_fallback_cohort_admitted", f.Output.enabled && f.Batch.RejectedCohortCount == 0);
                Show(a, false); Show(b, false); Render();
                Check("fallback_notification_hides_cohort_before_render", !f.Output.enabled && !f.First.enabled && !f.Second.enabled);
                Show(a, true); Show(b, true); Render();
                Check("fallback_notification_restores_cohort_before_render", f.Output.enabled && f.First.forceRenderingOff && f.Second.forceRenderingOff);
            }
            using (var f = new Fixture())
            {
                Fallback(f.First, "Assets/First.unity"); Fallback(f.Second, "Assets/Second.unity");
                f.Configure("Assets/First.unity"); f.Activate(); Render();
                Check("different_chunk_ownership_is_rejected", f.Batch.RejectedCohortCount == 1 && !f.Output.enabled);
            }
            using (var f = new Fixture())
            {
                f.Activate(); Render();
                var secondHost = new GameObject("DuplicateOwner"); secondHost.transform.SetParent(f.Root.transform, false);
                secondHost.SetActive(false);
                secondHost.AddComponent<MeshFilter>().sharedMesh = f.OutputMesh;
                MeshRenderer duplicateOutput = secondHost.AddComponent<MeshRenderer>();
                duplicateOutput.sharedMaterial = f.First.sharedMaterial;
                var duplicate = secondHost.AddComponent<AarisBakedStaticBatch>();
                duplicate.Configure(new[] { f.First, f.Second }, new[] { duplicateOutput }, "", 2);
                secondHost.SetActive(true); Render();
                Check("duplicate_cohort_does_not_steal_source_suppression", duplicate.RejectedCohortCount == 1 &&
                    !duplicateOutput.enabled && f.Output.enabled && f.First.forceRenderingOff && f.Second.forceRenderingOff);
            }
            var many = new List<Fixture>();
            try
            {
                for (int i = 0; i < 10; i++) { var f = new Fixture(); many.Add(f); f.Activate(); }
                Fixture last = many[many.Count - 1];
                last.Second.sharedMaterial = last.NewMaterial();
                int renders = 0;
                while (renders++ < 5 && last.Batch.RejectedCohortCount == 0) Render();
                Check("global_amortized_audit_reaches_late_cohort_within_bound", last.Batch.RejectedCohortCount == 1 &&
                    !last.Output.enabled && !last.First.forceRenderingOff && !last.Second.forceRenderingOff);
                results.Add("AMORTIZED_AUDIT_FIXTURE cohorts=10 entries=30 renders=" + (renders - 1) + " budget=8_per_camera");
            }
            finally { for (int i = many.Count - 1; i >= 0; i--) many[i].Dispose(); }
        }

        private static void AllocationCheck()
        {
            MethodInfo allocation = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static);
            if (allocation == null) { results.Add("SKIP callback_allocation_counter_unavailable"); return; }
            var allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), allocation);
            var callback = (Action)Delegate.CreateDelegate(typeof(Action), typeof(AarisBakedStaticBatch).GetMethod(
                "RefreshAll", BindingFlags.NonPublic | BindingFlags.Static));
            using (var fixture = new Fixture())
            {
                fixture.Activate(); Render();
                for (int i = 0; i < 64; i++) callback();
                long before = allocated();
                for (int i = 0; i < 256; i++) callback();
                long bytes = allocated() - before;
                Check("warmed_callback_fixture_has_zero_managed_allocation", bytes == 0);
                results.Add("CALLBACK_FIXTURE_CALLS=256 ALLOCATED_BYTES=" + bytes +
                    " SCOPE=cached_production_callback_with_three_audited_entries_per_call_not_Camera.Render_or_whole_game");
            }
        }

        private static void Mutation(string name, Action<Fixture> mutate)
        {
            using (var f = new Fixture())
            {
                f.Activate(); Render(); mutate(f); Render();
                Check(name, f.Batch.RejectedCohortCount == 1 && !f.Output.enabled &&
                    !f.First.forceRenderingOff && !f.Second.forceRenderingOff && f.First.enabled && f.Second.enabled);
            }
        }

        private static AarisChunkFallbackVisibility Fallback(MeshRenderer renderer, string path)
        {
            AarisChunkFallbackVisibility fallback = renderer.gameObject.AddComponent<AarisChunkFallbackVisibility>();
            fallback.enabled = false;
            var serialized = new SerializedObject(fallback);
            serialized.FindProperty("chunkScenePath").stringValue = path;
            SerializedProperty members = serialized.FindProperty("silhouetteRenderers");
            members.arraySize = 1; members.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            fallback.enabled = true;
            return fallback;
        }

        private static void Show(AarisChunkFallbackVisibility fallback, bool visible)
        { typeof(AarisChunkFallbackVisibility).GetMethod("ShowSilhouette", Members).Invoke(fallback, new object[] { visible }); }
        private static void Render() => camera.Render();
        private static void Check(string name, bool pass)
        { checks++; if (!pass) failures++; results.Add((pass ? "PASS " : "FAIL ") + name); }

        private sealed class Fixture : IDisposable
        {
            internal readonly GameObject Root, Host;
            internal readonly MeshRenderer First, Second, Output;
            internal readonly Mesh SourceMesh, OutputMesh;
            internal readonly AarisBakedStaticBatch Batch;
            private readonly List<Object> owned = new List<Object>();
            private readonly Shader shader;

            internal Fixture()
            {
                shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) throw new InvalidOperationException("URP_UNLIT_SHADER_MISSING");
                Root = new GameObject("StaticCohortFixture");
                SourceMesh = new Mesh { name = "QA tetrahedron" };
                SourceMesh.vertices = new[] { new Vector3(-.5f, 0, -.5f), new Vector3(.5f, 0, -.5f),
                    new Vector3(0, 0, .5f), new Vector3(0, 1, 0) };
                SourceMesh.triangles = new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3 };
                SourceMesh.RecalculateNormals(); SourceMesh.RecalculateBounds(); owned.Add(SourceMesh);
                Material material = NewMaterial();
                First = Source("first", new Vector3(-1, 0, 0), material);
                Second = Source("second", new Vector3(1, 0, 0), material);
                Host = new GameObject("CombinedHost"); Host.transform.SetParent(Root.transform, false); Host.SetActive(false);
                OutputMesh = new Mesh { name = "QA combined" };
                OutputMesh.CombineMeshes(new[] { new CombineInstance { mesh = SourceMesh, transform = First.transform.localToWorldMatrix },
                    new CombineInstance { mesh = SourceMesh, transform = Second.transform.localToWorldMatrix } }, true, true, false);
                owned.Add(OutputMesh);
                var output = new GameObject("output"); output.transform.SetParent(Host.transform, false);
                output.AddComponent<MeshFilter>().sharedMesh = OutputMesh;
                Output = output.AddComponent<MeshRenderer>(); Output.sharedMaterial = material;
                Output.enabled = false;
                Batch = Host.AddComponent<AarisBakedStaticBatch>(); Configure();
            }
            private MeshRenderer Source(string name, Vector3 position, Material material)
            {
                var item = new GameObject(name); item.transform.SetParent(Root.transform, false); item.transform.localPosition = position;
                item.AddComponent<MeshFilter>().sharedMesh = SourceMesh;
                MeshRenderer renderer = item.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                item.AddComponent<BoxCollider>();
                return renderer;
            }
            internal Material NewMaterial()
            { var material = new Material(shader); owned.Add(material); return material; }
            internal void Configure(string path = "")
            { Batch.Configure(new[] { First, Second }, new[] { Output }, path, 2); }
            internal void Activate() => Host.SetActive(true);
            internal void TrackNativeMeshes()
            {
                foreach (MeshRenderer renderer in new[] { First, Second })
                {
                    Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                    if (mesh != null && !owned.Contains(mesh)) owned.Add(mesh);
                }
            }
            public void Dispose()
            {
                if (Root != null) Object.DestroyImmediate(Root);
                for (int i = owned.Count - 1; i >= 0; i--) if (owned[i] != null) Object.DestroyImmediate(owned[i]);
            }
        }
    }
}
#endif
