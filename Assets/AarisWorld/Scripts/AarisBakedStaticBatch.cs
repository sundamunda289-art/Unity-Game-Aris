using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview
{
    /// <summary>
    /// Presents Editor-combined immutable geometry without changing source
    /// Renderer.enabled, scene ownership, colliders or original mesh assets.
    /// Every source belongs to one complete material/visibility cohort.
    /// No mesh data is read, allocated or combined by the rendering callback.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-92)]
    public sealed class AarisBakedStaticBatch : MonoBehaviour
    {
        [Serializable]
        private sealed class Source
        {
            public MeshRenderer renderer;
            public Matrix4x4 localToWorld;
            public Material[] materials;
            public Bounds bounds;
            public Mesh authoredMesh;
            public int layer, sortingLayer, sortingOrder;
            public uint renderingLayers;
            public ShadowCastingMode shadows;
            public bool receiveShadows, allowDynamicOcclusion;
            public LightProbeUsage lightProbes;
            public ReflectionProbeUsage reflectionProbes;
            public MotionVectorGenerationMode motionVectors;
            public Transform probeAnchor;
            [NonSerialized] public GameObject owner;
            [NonSerialized] public Transform pose;
            [NonSerialized] public MeshFilter filter;
            [NonSerialized] public Mesh runtimeMesh;
            [NonSerialized] public bool acceptedStaticBatch;
            [NonSerialized] public bool observedTransformChanged;
            [NonSerialized] public int instanceId;
            [NonSerialized] public bool originalForceOff, suppressionOwned;
        }

        [SerializeField] private Source[] sources = Array.Empty<Source>();
        [SerializeField] private MeshRenderer[] outputs = Array.Empty<MeshRenderer>();
        [SerializeField] private string fallbackScenePath = "";
        [SerializeField] private int sourceSubmeshDraws;
        [SerializeField] private Matrix4x4[] outputMatrices = Array.Empty<Matrix4x4>();
        [SerializeField] private Mesh[] outputMeshes = Array.Empty<Mesh>();
        [SerializeField] private Material[] outputMaterials = Array.Empty<Material>();
        private Transform[] outputPoses;
        private MeshFilter[] outputFilters;
        private int[] outputStateSources;
        private bool[] observedOutputTransformChanged;
        private readonly List<Material> materialScratch = new List<Material>(16);
        private bool registered, rejected, combinedVisible, pendingValidation;
        private int suppressedCount, mixedVisibilityFallbackCount;
        private string rejectionReason = "";
        private static readonly List<AarisBakedStaticBatch> live =
            new List<AarisBakedStaticBatch>(128);
        private static readonly Dictionary<int, AarisBakedStaticBatch> sourceOwners =
            new Dictionary<int, AarisBakedStaticBatch>(512);
        private const int AuditEntriesPerCamera = 8;
        private static int auditCohort, auditEntry;

        public int SourceRendererCount => sources != null ? sources.Length : 0;
        public int CombinedRendererCount => outputs != null ? outputs.Length : 0;
        public int ActiveCombinedRendererCount => combinedVisible ? CombinedRendererCount : 0;
        public int SuppressedSourceRendererCount => suppressedCount;
        public int RejectedCohortCount => rejected ? 1 : 0;
        public int MixedVisibilityFallbackCount => mixedVisibilityFallbackCount;
        public int SourceSubmeshDraws => sourceSubmeshDraws;
        public string FallbackScenePath => fallbackScenePath;
        public string RejectionReason => rejectionReason;
        public static int RegisteredCohortCount => live.Count;
        public static int TotalActiveCombinedRenderers { get; private set; }
        public static int TotalSuppressedSourceRenderers { get; private set; }
        public static int TotalRejectedCohorts { get; private set; }

        /// <summary>
        /// Authoring/fixture entry point. Outputs must contain every submesh of
        /// every source, partitioned into exact compatible material draws.
        /// Generated output renderers remain disabled in saved Edit Mode scenes.
        /// </summary>
        public void Configure(MeshRenderer[] sourceRenderers, MeshRenderer[] combinedRenderers,
            string chunkPath, int originalSubmeshDraws)
        {
            Shutdown();
            sources = new Source[sourceRenderers != null ? sourceRenderers.Length : 0];
            outputs = combinedRenderers != null ? (MeshRenderer[])combinedRenderers.Clone() :
                Array.Empty<MeshRenderer>();
            fallbackScenePath = chunkPath ?? "";
            sourceSubmeshDraws = originalSubmeshDraws;
            outputMatrices = new Matrix4x4[outputs.Length];
            outputMeshes = new Mesh[outputs.Length];
            outputMaterials = new Material[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
            {
                if (outputs[i] == null) continue;
                outputMatrices[i] = outputs[i].transform.localToWorldMatrix;
                MeshFilter filter = outputs[i].GetComponent<MeshFilter>();
                outputMeshes[i] = filter != null ? filter.sharedMesh : null;
                outputMaterials[i] = outputs[i].sharedMaterial;
            }
            for (int i = 0; i < sources.Length; i++)
            {
                MeshRenderer renderer = sourceRenderers[i];
                var source = new Source { renderer = renderer };
                sources[i] = source;
                if (renderer == null) continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                source.authoredMesh = filter != null ? filter.sharedMesh : null;
                source.localToWorld = renderer.transform.localToWorldMatrix;
                source.materials = renderer.sharedMaterials;
                source.bounds = renderer.bounds;
                source.layer = renderer.gameObject.layer;
                source.renderingLayers = renderer.renderingLayerMask;
                source.shadows = renderer.shadowCastingMode;
                source.receiveShadows = renderer.receiveShadows;
                source.allowDynamicOcclusion = renderer.allowOcclusionWhenDynamic;
                source.sortingLayer = renderer.sortingLayerID;
                source.sortingOrder = renderer.sortingOrder;
                source.lightProbes = renderer.lightProbeUsage;
                source.reflectionProbes = renderer.reflectionProbeUsage;
                source.motionVectors = renderer.motionVectorGenerationMode;
                source.probeAnchor = renderer.probeAnchor;
            }
            SetOutputs(false);
            if (Application.isPlaying && isActiveAndEnabled) Initialize();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry()
        {
            RenderPipelineManager.beginCameraRendering -= BeforeCameraRendering;
            Camera.onPreCull -= BeforeBuiltinCamera;
            AarisChunkFallbackVisibility.SilhouetteVisibilityChanged -= SourceVisibilityChanged;
            live.Clear();
            sourceOwners.Clear();
            TotalActiveCombinedRenderers = 0;
            TotalSuppressedSourceRenderers = 0;
            TotalRejectedCohorts = 0;
            auditCohort = auditEntry = 0;
        }

        private void OnEnable()
        {
            if (Application.isPlaying) Initialize();
        }

        private void Initialize()
        {
            if (registered) return;
            SetOutputs(false);
            if (sources == null || sources.Length == 0 || outputs == null || outputs.Length == 0)
            { Reject("empty-cohort"); return; }
            if (outputMatrices == null || outputMeshes == null || outputMaterials == null ||
                outputMatrices.Length != outputs.Length || outputMeshes.Length != outputs.Length ||
                outputMaterials.Length != outputs.Length)
            { Reject("missing-output-snapshots"); return; }
            outputPoses = new Transform[outputs.Length];
            outputFilters = new MeshFilter[outputs.Length];
            outputStateSources = new int[outputs.Length];
            observedOutputTransformChanged = new bool[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
                if (outputs[i] != null)
                {
                    outputPoses[i] = outputs[i].transform;
                    observedOutputTransformChanged[i] = outputPoses[i].hasChanged;
                    outputFilters[i] = outputs[i].GetComponent<MeshFilter>();
                }
            int materialDraws = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                Source source = sources[i];
                if (source == null || source.renderer == null || source.materials == null)
                { Reject("missing-source"); return; }
                source.owner = source.renderer.gameObject;
                source.pose = source.renderer.transform;
                source.observedTransformChanged = source.pose.hasChanged;
                source.filter = source.renderer.GetComponent<MeshFilter>();
                source.runtimeMesh = source.filter != null ? source.filter.sharedMesh : null;
                source.acceptedStaticBatch = source.renderer.isPartOfStaticBatch;
                source.instanceId = source.renderer.GetInstanceID();
                source.originalForceOff = source.renderer.forceRenderingOff;
                source.suppressionOwned = false;
                if (sourceOwners.ContainsKey(source.instanceId))
                { Reject("source-already-owned"); return; }
                for (int previous = 0; previous < i; previous++)
                    if (sources[previous].instanceId == source.instanceId)
                    { Reject("duplicate-source"); return; }
                materialDraws += source.materials.Length;
                if (source.materials.Length > materialScratch.Capacity)
                    materialScratch.Capacity = source.materials.Length;
            }
            if (materialDraws != sourceSubmeshDraws)
            { Reject("source-draw-coverage"); return; }
            if (!ValidateCohort()) { Reject("authored-state-mismatch"); return; }
            for (int i = 0; i < sources.Length; i++)
                sourceOwners.Add(sources[i].instanceId, this);
            if (live.Count == 0)
            {
                auditCohort = auditEntry = 0;
                RenderPipelineManager.beginCameraRendering += BeforeCameraRendering;
                Camera.onPreCull += BeforeBuiltinCamera;
                AarisChunkFallbackVisibility.SilhouetteVisibilityChanged += SourceVisibilityChanged;
            }
            live.Add(this);
            registered = true;
            pendingValidation = false;
            RefreshVisibility();
        }

        private bool ValidateCohort()
        {
            for (int i = 0; i < outputs.Length; i++)
            {
                MeshRenderer output = outputs[i];
                if (output == null || output.gameObject.scene != gameObject.scene ||
                    output.HasPropertyBlock() || output.forceRenderingOff || output.isPartOfStaticBatch) return false;
                for (int n = 0; n < sources.Length; n++)
                    if (output == sources[n].renderer) return false;
                for (int n = 0; n < i; n++) if (output == outputs[n]) return false;
                MeshFilter filter = outputFilters[i];
                if (filter == null || filter.sharedMesh == null || filter.sharedMesh != outputMeshes[i] ||
                    filter.sharedMesh.subMeshCount != 1 ||
                    !SameMatrix(outputPoses[i].localToWorldMatrix, outputMatrices[i]))
                    return false;
                output.GetSharedMaterials(materialScratch);
                if (materialScratch.Count != 1 || materialScratch[0] == null ||
                    materialScratch[0] != outputMaterials[i]) return false;
                int matchingSource = -1;
                for (int sourceIndex = 0; sourceIndex < sources.Length && matchingSource < 0; sourceIndex++)
                {
                    Source source = sources[sourceIndex];
                    if (!SameRenderState(source, output)) continue;
                    for (int material = 0; material < source.materials.Length; material++)
                        if (source.materials[material] == outputMaterials[i])
                        { matchingSource = sourceIndex; break; }
                }
                if (matchingSource < 0) return false;
                outputStateSources[i] = matchingSource;
            }
            for (int i = 0; i < sources.Length; i++)
            {
                Source source = sources[i];
                MeshRenderer renderer = source.renderer;
                if (renderer == null || renderer.gameObject.scene != gameObject.scene ||
                    renderer.GetComponentInParent<LODGroup>() != null || renderer.HasPropertyBlock() ||
                    HasAssignedLightmap(renderer.lightmapIndex) || HasAssignedLightmap(renderer.realtimeLightmapIndex) ||
                    !SameMatrix(renderer.transform.localToWorldMatrix, source.localToWorld) ||
                    !SameRenderState(source, renderer)) return false;
                MeshFilter filter = source.filter;
                if (filter == null || filter.sharedMesh == null) return false;
                AarisChunkFallbackVisibility fallback =
                    renderer.GetComponentInParent<AarisChunkFallbackVisibility>();
                if (string.IsNullOrEmpty(fallbackScenePath))
                { if (fallback != null) return false; }
                else if (fallback == null || !fallback.OwnsSectorBatchRenderer(renderer, fallbackScenePath))
                    return false;
                // Native automatic static batching substitutes a combined
                // mesh/submesh range. The authoring gate verifies original
                // asset hashes; do not mistake this native copy for a mutation.
                if (!renderer.isPartOfStaticBatch && filter.sharedMesh != source.authoredMesh)
                    return false;
                renderer.GetSharedMaterials(materialScratch);
                if (materialScratch.Count != source.materials.Length) return false;
                for (int material = 0; material < source.materials.Length; material++)
                {
                    Material expected = source.materials[material];
                    if (expected == null || materialScratch[material] != expected) return false;
                    bool covered = false;
                    for (int output = 0; output < outputs.Length; output++)
                        if (outputs[output].sharedMaterial == expected && SameRenderState(source, outputs[output]))
                        { covered = true; break; }
                    if (!covered) return false;
                }
            }
            return true;
        }

        private static bool SameRenderState(Source source, MeshRenderer renderer)
        {
            return source.layer == renderer.gameObject.layer &&
                source.renderingLayers == renderer.renderingLayerMask &&
                source.shadows == renderer.shadowCastingMode &&
                source.receiveShadows == renderer.receiveShadows &&
                source.allowDynamicOcclusion == renderer.allowOcclusionWhenDynamic &&
                source.sortingLayer == renderer.sortingLayerID && source.sortingOrder == renderer.sortingOrder &&
                source.lightProbes == renderer.lightProbeUsage &&
                source.reflectionProbes == renderer.reflectionProbeUsage &&
                source.motionVectors == renderer.motionVectorGenerationMode &&
                source.probeAnchor == renderer.probeAnchor;
        }

        private static bool SameMatrix(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(a[i]) || float.IsInfinity(a[i]) ||
                    float.IsNaN(b[i]) || float.IsInfinity(b[i]) ||
                    Mathf.Abs(a[i] - b[i]) > 0.0005f) return false;
            return true;
        }

        private static bool HasAssignedLightmap(int index) => index >= 0 && index < 65534;

        private static void SourceVisibilityChanged(Renderer renderer)
        {
            if (renderer != null && sourceOwners.TryGetValue(renderer.GetInstanceID(), out var owner))
                owner.pendingValidation = true;
        }

        private static void BeforeCameraRendering(ScriptableRenderContext context, Camera camera)
        { RefreshAll(); }

        private static void BeforeBuiltinCamera(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline == null) RefreshAll();
        }

        private static void RefreshAll()
        {
            for (int i = live.Count - 1; i >= 0; i--)
                if (live[i] != null && live[i].isActiveAndEnabled) live[i].RefreshVisibility();
            AuditSomeEntries();
        }

        // Legitimate visibility changes are handled before every camera. More
        // expensive material/state audits are spread globally across cameras;
        // these immutable sources have no movement/material controller. Never
        // clear Transform.hasChanged: other systems can own that signal.
        private void RefreshVisibility()
        {
            if (!registered || rejected) return;
            if (pendingValidation)
            {
                pendingValidation = false;
                if (!ValidateCohort()) { Reject("changed-source-state"); return; }
            }
            int visible = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                Source source = sources[i];
                if (source.renderer == null || source.owner == null || source.pose == null || source.filter == null ||
                    source.owner.scene != gameObject.scene)
                { Reject("destroyed-source"); return; }
                bool changed = source.pose.hasChanged;
                if (changed && !source.observedTransformChanged &&
                    !SameMatrix(source.pose.localToWorldMatrix, source.localToWorld))
                { Reject("moved-source"); return; }
                source.observedTransformChanged = changed;
                bool expectedForceOff = source.suppressionOwned || source.originalForceOff;
                if (source.renderer.forceRenderingOff != expectedForceOff)
                { Reject("external-force-off-owner"); return; }
                if (source.renderer.enabled && source.owner.activeInHierarchy && !source.originalForceOff)
                    visible++;
            }
            for (int i = 0; i < outputs.Length; i++)
            {
                if (outputs[i] == null || outputPoses[i] == null || outputFilters[i] == null ||
                    !outputs[i].gameObject.activeInHierarchy || outputs[i].gameObject.scene != gameObject.scene)
                { Reject("missing-output"); return; }
                if (outputs[i].forceRenderingOff || outputs[i].enabled != combinedVisible)
                { Reject("mutated-output-state"); return; }
                bool changed = outputPoses[i].hasChanged;
                if (changed && !observedOutputTransformChanged[i] &&
                    !SameMatrix(outputPoses[i].localToWorldMatrix, outputMatrices[i]))
                { Reject("moved-output"); return; }
                observedOutputTransformChanged[i] = changed;
            }
            bool shouldCombine = visible == sources.Length;
            if (visible != 0 && !shouldCombine && combinedVisible) mixedVisibilityFallbackCount++;
            ApplyPresentation(shouldCombine);
        }

        private static void AuditSomeEntries()
        {
            if (live.Count == 0) return;
            if (auditCohort >= live.Count) { auditCohort = 0; auditEntry = 0; }
            int startCohort = auditCohort, startEntry = auditEntry, skipped = 0;
            int remaining = AuditEntriesPerCamera;
            while (remaining > 0 && live.Count > 0)
            {
                AarisBakedStaticBatch cohort = live[auditCohort];
                if (cohort == null || !cohort.registered || cohort.rejected || !cohort.isActiveAndEnabled)
                {
                    auditEntry = 0; auditCohort = (auditCohort + 1) % live.Count;
                    if (++skipped >= live.Count) return;
                    continue;
                }
                skipped = 0;
                int count = cohort.sources.Length + cohort.outputs.Length;
                if (auditEntry < cohort.sources.Length) cohort.AuditSource(cohort.sources[auditEntry]);
                else cohort.AuditOutput(auditEntry - cohort.sources.Length);
                remaining--; auditEntry++;
                if (auditEntry >= count) { auditEntry = 0; auditCohort = (auditCohort + 1) % live.Count; }
                if (auditCohort == startCohort && auditEntry == startEntry) return;
            }
        }

        private void AuditSource(Source source)
        {
            if (source.renderer == null || source.pose == null || source.filter == null ||
                !SameMatrix(source.pose.localToWorldMatrix, source.localToWorld) ||
                !SameRenderState(source, source.renderer) || source.renderer.HasPropertyBlock() ||
                HasAssignedLightmap(source.renderer.lightmapIndex) || HasAssignedLightmap(source.renderer.realtimeLightmapIndex))
            { Reject("mutated-source-state"); return; }
            Mesh currentMesh = source.filter.sharedMesh;
            if (currentMesh != source.runtimeMesh)
            {
                // Some scene-load paths complete automatic static batching
                // after OnEnable. Accept that one native mesh substitution.
                if (currentMesh == null || source.acceptedStaticBatch || !source.renderer.isPartOfStaticBatch)
                { Reject("mutated-source-mesh"); return; }
                source.runtimeMesh = currentMesh; source.acceptedStaticBatch = true;
            }
            source.renderer.GetSharedMaterials(materialScratch);
            if (materialScratch.Count != source.materials.Length)
            { Reject("mutated-source-materials"); return; }
            for (int material = 0; material < source.materials.Length; material++)
                if (materialScratch[material] != source.materials[material])
                { Reject("mutated-source-materials"); return; }
        }

        private void AuditOutput(int index)
        {
            MeshRenderer output = outputs[index];
            if (output == null || outputFilters[index] == null || outputPoses[index] == null ||
                outputFilters[index].sharedMesh != outputMeshes[index] || output.sharedMaterial != outputMaterials[index] ||
                output.HasPropertyBlock() || !SameRenderState(sources[outputStateSources[index]], output) ||
                HasAssignedLightmap(output.lightmapIndex) || HasAssignedLightmap(output.realtimeLightmapIndex) ||
                !SameMatrix(outputPoses[index].localToWorldMatrix, outputMatrices[index]))
                Reject("mutated-output-state");
        }

        private void ApplyPresentation(bool showCombined)
        {
            if (showCombined == combinedVisible) return;
            if (showCombined)
            {
                int suppressed = 0;
                for (int i = 0; i < sources.Length; i++)
                {
                    Source source = sources[i];
                    if (!source.originalForceOff)
                    {
                        source.renderer.forceRenderingOff = true;
                        source.suppressionOwned = true;
                        suppressed++;
                    }
                }
                SetSuppressedCount(suppressed);
                SetOutputs(true);
                combinedVisible = true;
                TotalActiveCombinedRenderers += outputs.Length;
            }
            else
            {
                // Disable the replacement first so teardown or a mixed
                // visibility set cannot leave a duplicate draw for a frame.
                SetOutputs(false);
                if (combinedVisible) TotalActiveCombinedRenderers -= outputs.Length;
                combinedVisible = false;
                RestoreSources();
            }
        }

        private void SetOutputs(bool visible)
        {
            if (outputs == null) return;
            for (int i = 0; i < outputs.Length; i++)
                if (outputs[i] != null && outputs[i].enabled != visible)
                    outputs[i].enabled = visible;
        }

        private void SetSuppressedCount(int count)
        {
            TotalSuppressedSourceRenderers += count - suppressedCount;
            suppressedCount = count;
        }

        private void RestoreSources()
        {
            if (sources != null)
                for (int i = 0; i < sources.Length; i++)
                {
                    Source source = sources[i];
                    if (source == null || !source.suppressionOwned) continue;
                    if (source.renderer != null) source.renderer.forceRenderingOff = source.originalForceOff;
                    source.suppressionOwned = false;
                }
            SetSuppressedCount(0);
        }

        private void Reject(string reason)
        {
            SetOutputs(false);
            if (combinedVisible) TotalActiveCombinedRenderers -= outputs.Length;
            combinedVisible = false;
            RestoreSources();
            rejectionReason = reason;
            if (!rejected) { rejected = true; TotalRejectedCohorts++; }
        }

        private void Shutdown()
        {
            SetOutputs(false);
            if (combinedVisible) TotalActiveCombinedRenderers -= outputs.Length;
            combinedVisible = false;
            RestoreSources();
            if (registered)
            {
                for (int i = 0; i < sources.Length; i++)
                    if (sourceOwners.TryGetValue(sources[i].instanceId, out var owner) && owner == this)
                        sourceOwners.Remove(sources[i].instanceId);
                int index = live.IndexOf(this);
                if (index >= 0)
                {
                    if (index < auditCohort) auditCohort--;
                    else if (index == auditCohort) auditEntry = 0;
                    live.RemoveAt(index);
                    if (auditCohort >= live.Count) { auditCohort = 0; auditEntry = 0; }
                }
                registered = false;
                if (live.Count == 0)
                {
                    RenderPipelineManager.beginCameraRendering -= BeforeCameraRendering;
                    Camera.onPreCull -= BeforeBuiltinCamera;
                    AarisChunkFallbackVisibility.SilhouetteVisibilityChanged -= SourceVisibilityChanged;
                }
            }
            if (rejected) { rejected = false; TotalRejectedCohorts--; }
            rejectionReason = "";
            pendingValidation = false;
        }

        private void OnDisable() => Shutdown();
        private void OnDestroy() => Shutdown();
    }
}
