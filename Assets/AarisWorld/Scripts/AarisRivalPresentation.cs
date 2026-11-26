using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview
{
    /// <summary>
    /// One match-owned batch of soft foot contacts. No per-bot GameObject,
    /// Renderer, material clone, shadow pass or per-frame managed allocation.
    /// Native surface probes run at most twice per frame and 4Hz per visible
    /// grounded actor. Hidden/airborne/remote contacts are rejected individually.
    /// </summary>
    internal sealed class AarisRivalPresentation : IDisposable
    {
        private readonly Matrix4x4[] matrices;
        private readonly Matrix4x4[] drawMatrices;
        private readonly Vector3[] groundPoints, groundNormals, positions;
        private readonly Transform[] actors;
        private readonly float[] nextProbe, eligibleUntil, distancesSquared;
        private readonly bool[] grounded;
        private readonly RaycastHit[] groundHits = new RaycastHit[12];
        private readonly bool supported;
        private Mesh mesh;
        private Material material;
        private int probeFrame = -1, probesThisFrame;
        internal int LastDrawCount { get; private set; }
        internal int LastProbeCount => probesThisFrame;

        internal AarisRivalPresentation(int capacity)
        {
            matrices = new Matrix4x4[capacity];
            drawMatrices = new Matrix4x4[capacity];
            groundPoints = new Vector3[capacity];
            groundNormals = new Vector3[capacity];
            positions = new Vector3[capacity];
            actors = new Transform[capacity];
            distancesSquared = new float[capacity];
            nextProbe = new float[capacity];
            eligibleUntil = new float[capacity];
            grounded = new bool[capacity];
            supported = SystemInfo.supportsInstancing;
            if (!supported) return; // Existing native PBR shadows remain intact.
            // A build-included instancing-enabled template keeps this shader's
            // INSTANCING_ON variants under the project's StripUnused setting.
            // A runtime-only material cannot establish that build dependency.
            Material template = Resources.Load<Material>("AarisRivalContactMaterial");
            if (template == null || template.shader == null || !template.shader.isSupported) return;
            material = new Material(template)
            {
                name = "Aaris shared soft rival foot contact",
                enableInstancing = true,
                hideFlags = HideFlags.HideAndDontSave
            };
            mesh = new Mesh { name = "Aaris shared foot contact quad",
                hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[] { new Vector3(-.5f, 0f, -.5f),
                new Vector3(-.5f, 0f, .5f), new Vector3(.5f, 0f, .5f),
                new Vector3(.5f, 0f, -.5f) };
            mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
        }

        internal void Clear(int slot)
        {
            if (slot < 0 || slot >= eligibleUntil.Length) return;
            eligibleUntil[slot] = 0f;
            grounded[slot] = false;
            actors[slot] = null;
        }

        internal void UpdateContact(int slot, Transform actor, Vector3 position,
            bool eligible, float playerDistanceSqr, float now)
        {
            if (slot < 0 || slot >= eligibleUntil.Length) return;
            if (!supported || material == null || !eligible)
            {
                Clear(slot);
                return;
            }
            actors[slot] = actor;
            positions[slot] = position;
            distancesSquared[slot] = playerDistanceSqr;
            eligibleUntil[slot] = now + .12f;
            // Submission never spends the shared probe budget. LateUpdate
            // selects the oldest due contacts across the complete visible set,
            // so fixed bot Update order cannot starve later roster slots.
        }

        private void ProbePendingContacts(float now, int frame)
        {
            if (probeFrame != frame)
            {
                probeFrame = frame;
                probesThisFrame = 0;
            }
            while (probesThisFrame < 2)
            {
                int selected = -1;
                float oldest = float.PositiveInfinity;
                for (int i = 0; i < eligibleUntil.Length; i++)
                    if (eligibleUntil[i] > now && nextProbe[i] <= now && nextProbe[i] < oldest)
                    { selected = i; oldest = nextProbe[i]; }
                if (selected < 0) return;
                probesThisFrame++;
                nextProbe[selected] = now + .25f;
                ProbeSurface(selected);
            }
        }

        private void ProbeSurface(int slot)
        {
            grounded[slot] = false;
            Transform actor = actors[slot];
            Ray ray = new Ray(positions[slot] + Vector3.up * .5f, Vector3.down);
            int count = Physics.RaycastNonAlloc(ray, groundHits, .9f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            // A saturated unordered result cannot prove the nearest floor.
            float nearest = float.PositiveInfinity;
            if (count >= groundHits.Length) return;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = groundHits[i];
                Collider collider = hit.collider;
                if (collider == null || collider is CharacterController ||
                    (actor != null && collider.transform.IsChildOf(actor)) ||
                    hit.normal.y < .65f || hit.distance >= nearest) continue;
                nearest = hit.distance;
                groundPoints[slot] = hit.point;
                groundNormals[slot] = hit.normal;
                grounded[slot] = true;
            }
        }

        private bool PrepareContactMatrix(int slot, float now)
        {
            if (!grounded[slot]) return false;
            Vector3 position = positions[slot];
            Vector3 normal = groundNormals[slot], sample = groundPoints[slot];
            float sampleDx = position.x - sample.x, sampleDz = position.z - sample.z;
            if (sampleDx * sampleDx + sampleDz * sampleDz > 1.5f * 1.5f)
            {
                // A teleport or delayed probe cannot carry a contact patch
                // onto an untested bridge edge or another floor. Keep its old
                // due deadline so this invalidation cannot jump the fair queue.
                grounded[slot] = false;
                return false;
            }
            // Between probes follow the sampled tangent plane, retaining the
            // current x/z under the actual capsule; never slide a stationary blob.
            float groundY = sample.y - (normal.x * (position.x - sample.x) +
                normal.z * (position.z - sample.z)) / normal.y;
            if (Mathf.Abs(position.y - groundY) > .22f)
            {
                grounded[slot] = false;
                return false;
            }
            float fade = Mathf.Clamp01((65f - Mathf.Sqrt(distancesSquared[slot])) / 15f);
            matrices[slot] = Matrix4x4.TRS(new Vector3(position.x, groundY + .018f, position.z),
                Quaternion.FromToRotation(Vector3.up, normal),
                new Vector3(.86f * fade, 1f, .66f * fade));
            return true;
        }

        internal void Draw(Camera camera, float now)
        {
            LastDrawCount = 0;
            if (!supported || material == null || mesh == null || camera == null ||
                !camera.isActiveAndEnabled) return;
            ProbePendingContacts(now, Time.frameCount);
            // Graphics instancing performs group culling. Visibility/grounding
            // is checked for EACH rival before adding it to this batch.
            for (int i = 0; i < eligibleUntil.Length; i++)
                if (eligibleUntil[i] > now && PrepareContactMatrix(i, now))
                    drawMatrices[LastDrawCount++] = matrices[i];
            if (LastDrawCount == 0) return;
            Graphics.DrawMeshInstanced(mesh, 0, material, drawMatrices, LastDrawCount,
                null, ShadowCastingMode.Off, false, 0, camera, LightProbeUsage.Off);
        }

        private static void DestroyOwned(UnityEngine.Object resource)
        {
            if (resource == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying) { UnityEngine.Object.DestroyImmediate(resource); return; }
#endif
            UnityEngine.Object.Destroy(resource);
        }

        public void Dispose()
        {
            DestroyOwned(mesh);
            DestroyOwned(material);
            mesh = null;
            material = null;
            LastDrawCount = 0;
        }
    }
}
