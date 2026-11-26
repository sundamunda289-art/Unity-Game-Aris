using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview
{
    // A single shared visual-only contact batch. The original gameplay
    // colliders, grounding and shadows remain authoritative.
    [DefaultExecutionOrder(360)]
    internal sealed class AarisSubjectContacts : MonoBehaviour
    {
        private struct Entry
        {
            internal Transform root, visual, leftFoot, rightFoot;
            internal CharacterController capsule;
            internal AarisVehicleMotor vehicle;
            internal Vector2 size;
            internal float range, clearance, anchorOffset, nextProbe;
            internal Vector3 sample, normal;
            internal bool valid, candidate, wheelPlane;
        }
        private const int Capacity=96;
        private static AarisSubjectContacts instance;
        private readonly Entry[] entries=new Entry[Capacity];
        // One or two quads per subject, all submitted in a single batch.
        private readonly Matrix4x4[] matrices = new Matrix4x4[Capacity * 2];
        private readonly RaycastHit[] hits=new RaycastHit[16];
        private readonly Plane[] frustum=new Plane[6];
        private Camera cameraCache;
        private Material paint;
        private Mesh quad;
        private float cameraRetry;
        internal int LastProbeCount { get; private set; }
        internal int LastDrawCount { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { instance=null; }

        internal static void Register(Transform root, Vector2 size, float clearance,
            float range, CharacterController capsule = null, Transform visual = null,
            float anchorOffset = 0f, Transform leftFoot = null, Transform rightFoot = null,
            AarisVehicleMotor vehicle = null)
        {
            if (!Application.isPlaying || !SystemInfo.supportsInstancing || root==null) return;
            if (instance==null)
            {
                var host=new GameObject("Aaris shared subject contacts");
                instance=host.AddComponent<AarisSubjectContacts>();
            }
            if (instance.paint==null) return;
            int free=-1;
            for (int i=0;i<Capacity;i++)
            {
                if (instance.entries[i].root==root) { free=i; break; }
                if (free<0 && instance.entries[i].root==null) free=i;
            }
            if (free<0) return;
            instance.entries[free]=new Entry {
                root = root, visual = visual, capsule = capsule, size = size,
                range = range, clearance = clearance, anchorOffset = anchorOffset,
                leftFoot = leftFoot, rightFoot = rightFoot, vehicle = vehicle
            };
        }

        private void Awake()
        {
            Material template=Resources.Load<Material>("AarisRivalContactMaterial");
            if (template==null || template.shader==null || !template.shader.isSupported) return;
            paint=new Material(template) { name="Aaris shared subject contact",enableInstancing=true,
                hideFlags=HideFlags.HideAndDontSave };
            paint.SetFloat("_Opacity",.28f);
            // Road meshes sit up to 0.12 m above the TerrainCollider.
            paint.SetFloat("_ReceiverDepthBias", .13f);
            quad=new Mesh { name="Aaris shared subject contact quad",hideFlags=HideFlags.HideAndDontSave };
            quad.vertices=new[] {new Vector3(-.5f,0,-.5f),new Vector3(-.5f,0,.5f),
                new Vector3(.5f,0,.5f),new Vector3(.5f,0,-.5f)};
            quad.uv=new[] {Vector2.zero,Vector2.up,Vector2.one,Vector2.right};
            quad.triangles=new[] {0,1,2,0,2,3};
            quad.RecalculateBounds();
            quad.UploadMeshData(true);
        }

        private static Vector3 Anchor(ref Entry e)
        {
            Vector3 p=e.root.position;
            if (e.capsule!=null) p.y=e.capsule.bounds.min.y;
            else p.y += e.anchorOffset;
            return p;
        }

        private void Probe(ref Entry e)
        {
            e.valid=false;
            Vector3 p=Anchor(ref e);
            int count=Physics.RaycastNonAlloc(p+Vector3.up*.45f,Vector3.down,
                hits,.45f+e.clearance,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore);
            if (count>=hits.Length) return;
            float nearest=float.PositiveInfinity;
            for (int i=0;i<count;i++)
            {
                var h=hits[i];
                if (h.collider==null || h.collider is CharacterController ||
                    h.collider.attachedRigidbody!=null || h.collider.transform.IsChildOf(e.root) ||
                    h.normal.y<.72f || h.distance>=nearest) continue;
                if (Mathf.Abs(p.y-h.point.y)>e.clearance) continue;
                nearest=h.distance;e.sample=h.point;e.normal=h.normal;e.valid=true;
            }
        }

        private void LateUpdate()
        {
            LastProbeCount=LastDrawCount=0;
            if (paint==null || quad==null) return;
            float now=Time.unscaledTime;
            if (cameraCache==null || !cameraCache.isActiveAndEnabled)
            {
                if (now<cameraRetry) return;
                cameraRetry=now+.45f;
                cameraCache=Camera.main;
            }
            if (cameraCache==null || !cameraCache.isActiveAndEnabled) return;
            DrawContacts(cameraCache);
        }

        private void DrawContacts(Camera camera)
        {
            LastProbeCount = LastDrawCount = 0;
            float now = Time.unscaledTime;
            Vector3 eye = camera.transform.position;
            GeometryUtility.CalculateFrustumPlanes(camera, frustum);
            for (int i=0;i<Capacity;i++)
            {
                ref Entry e=ref entries[i];
                e.candidate=false;
                if (e.root==null || !e.root.gameObject.activeInHierarchy ||
                    (e.visual!=null && !e.visual.gameObject.activeInHierarchy) ||
                    (e.capsule!=null && (!e.capsule.enabled || !e.capsule.isGrounded)))
                {e.valid=false;continue;}
                Vector3 p=Anchor(ref e);
                if ((eye-p).sqrMagnitude>e.range*e.range ||
                    !GeometryUtility.TestPlanesAABB(frustum,new Bounds(p,
                        new Vector3(e.size.x,2f,e.size.y)))) {e.valid=false;continue;}
                e.candidate = true;
                bool useWheelPlane = e.vehicle != null && e.vehicle.IsOccupied;
                if (useWheelPlane != e.wheelPlane)
                {
                    e.wheelPlane = useWheelPlane;
                    e.valid = false;
                    e.nextProbe = 0f;
                }
                // An occupied car already samples both axles in FixedUpdate.
                // In the air, skip its shadow and leave ray slots for other subjects.
                if (e.wheelPlane)
                    e.valid = e.vehicle.TryGetWheelShadowPlane(out e.sample, out e.normal);
            }
            // Fair oldest-deadline selection: at most TWO native ground probes
            // per frame, at most four Hz per subject. No allocating queries.
            for (int budget=0;budget<2;budget++)
            {
                int selected=-1; float oldest=float.PositiveInfinity;
                for (int i=0;i<Capacity;i++)
                    if (entries[i].candidate && !entries[i].wheelPlane &&
                        entries[i].nextProbe <= now && entries[i].nextProbe < oldest)
                    {selected=i;oldest=entries[i].nextProbe;}
                if (selected<0) break;
                entries[selected].nextProbe=now+.25f;
                Probe(ref entries[selected]);LastProbeCount++;
            }
            for (int i=0;i<Capacity;i++)
            {
                ref Entry e=ref entries[i];
                if (!e.candidate || !e.valid) continue;
                Vector3 p = Anchor(ref e), n = e.normal, s = e.sample;
                float dx = p.x - s.x, dz = p.z - s.z;
                // A wheel plane can be off-centre if one side is unloaded.
                float maxOffset = e.wheelPlane ? 2.5f : .65f;
                if (dx * dx + dz * dz > maxOffset * maxOffset) { e.valid = false; continue; }
                float y = PlaneHeight(s, n, p);
                if (Mathf.Abs(p.y - y) > e.clearance) { e.valid = false; continue; }
                float fade = Mathf.Clamp01((e.range - (eye - p).magnitude) / (e.range * .3f));
                if (fade <= 0f) continue;
                Vector3 forward = Vector3.ProjectOnPlane(e.root.forward, n);
                if (forward.sqrMagnitude < .01f) continue;

                Quaternion rotation = Quaternion.LookRotation(forward, n);
                bool prone = e.capsule != null && e.capsule.height < .85f;
                if (!prone && e.leftFoot != null && e.rightFoot != null)
                {
                    PlaceFootContact(ref e, e.leftFoot.position, rotation, fade);
                    PlaceFootContact(ref e, e.rightFoot.position, rotation, fade);
                }
                else
                {
                    AddContact(new Vector3(p.x, y, p.z), n, rotation,
                        e.size.x * fade, (prone ? 1.5f : e.size.y) * fade);
                }
            }
            if (LastDrawCount == 0) return;
            var render = new RenderParams(paint)
            {
                // Layer filtering keeps the batch visible to offscreen game
                // cameras, which can skip a camera-specific instanced draw.
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                lightProbeUsage = LightProbeUsage.Off
            };
            Graphics.RenderMeshInstanced(render, quad, 0, matrices, LastDrawCount);
        }

        private static float PlaneHeight(Vector3 point, Vector3 normal, Vector3 position)
        {
            return point.y - (normal.x * (position.x - point.x) +
                normal.z * (position.z - point.z)) / normal.y;
        }

        private void PlaceFootContact(ref Entry e, Vector3 foot, Quaternion rotation, float fade)
        {
            float floor = PlaneHeight(e.sample, e.normal, foot);
            float height = foot.y - floor;
            if (height < -.10f) return;
            // Ankle height above its shared support plane shrinks the shoe
            // contact during the swing phase without another raycast.
            float scale = fade * Mathf.Clamp01((.31f - height) / .14f);
            if (scale <= .02f) return;
            AddContact(new Vector3(foot.x, floor, foot.z), e.normal, rotation,
                .30f * scale, .43f * scale);
        }

        private void AddContact(Vector3 position, Vector3 normal, Quaternion rotation,
            float width, float length)
        {
            matrices[LastDrawCount++] = Matrix4x4.TRS(position + normal * .018f,
                rotation, new Vector3(width, 1f, length));
        }

        private void OnDestroy()
        {
            if (instance==this) instance=null;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                if (quad!=null) DestroyImmediate(quad);
                if (paint!=null) DestroyImmediate(paint);
                return;
            }
#endif
            if (quad!=null) Destroy(quad);
            if (paint!=null) Destroy(paint);
        }
    }
}
