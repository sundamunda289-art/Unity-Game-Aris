using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview
{
    // A single shared visual-only contact batch. The original gameplay
    // colliders, grounding and shadows remain authoritative.
    internal sealed class AarisSubjectContacts : MonoBehaviour
    {
        private struct Entry
        {
            internal Transform root, visual;
            internal CharacterController capsule;
            internal Vector2 size;
            internal float range, clearance, anchorOffset, nextProbe;
            internal Vector3 sample, normal;
            internal bool valid, candidate;
        }
        private const int Capacity=96;
        private static AarisSubjectContacts instance;
        private readonly Entry[] entries=new Entry[Capacity];
        private readonly Matrix4x4[] matrices=new Matrix4x4[Capacity];
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
            float range, CharacterController capsule=null, Transform visual=null, float anchorOffset=0f)
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
                root=root,visual=visual,capsule=capsule,size=size,range=range,clearance=clearance,anchorOffset=anchorOffset
            };
        }

        private void Awake()
        {
            Material template=Resources.Load<Material>("AarisRivalContactMaterial");
            if (template==null || template.shader==null || !template.shader.isSupported) return;
            paint=new Material(template) { name="Aaris shared subject contact",enableInstancing=true,
                hideFlags=HideFlags.HideAndDontSave };
            paint.SetFloat("_Opacity",.28f);
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
            Vector3 eye=cameraCache.transform.position;
            GeometryUtility.CalculateFrustumPlanes(cameraCache,frustum);
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
                e.candidate=true;
            }
            // Fair oldest-deadline selection: at most TWO native ground probes
            // per frame, at most four Hz per subject. No allocating queries.
            for (int budget=0;budget<2;budget++)
            {
                int selected=-1; float oldest=float.PositiveInfinity;
                for (int i=0;i<Capacity;i++)
                    if (entries[i].candidate && entries[i].nextProbe<=now &&
                        entries[i].nextProbe<oldest)
                    {selected=i;oldest=entries[i].nextProbe;}
                if (selected<0) break;
                entries[selected].nextProbe=now+.25f;
                Probe(ref entries[selected]);LastProbeCount++;
            }
            for (int i=0;i<Capacity;i++)
            {
                ref Entry e=ref entries[i];
                if (!e.candidate || !e.valid) continue;
                Vector3 p=Anchor(ref e), n=e.normal, s=e.sample;
                float dx=p.x-s.x,dz=p.z-s.z;
                if (dx*dx+dz*dz>.65f*.65f) {e.valid=false;continue;}
                float y=s.y-(n.x*dx+n.z*dz)/n.y;
                if (Mathf.Abs(p.y-y)>e.clearance) {e.valid=false;continue;}
                float fade=Mathf.Clamp01((e.range-(eye-p).magnitude)/(e.range*.3f));
                Vector3 forward=Vector3.ProjectOnPlane(e.root.forward,n);
                if (forward.sqrMagnitude<.01f) continue;
                float length=e.capsule!=null && e.capsule.height<.85f ? 1.5f : e.size.y;
                matrices[LastDrawCount++]=Matrix4x4.TRS(new Vector3(p.x,y+.018f,p.z),
                    Quaternion.LookRotation(forward,n),new Vector3(e.size.x*fade,1f,length*fade));
            }
            if (LastDrawCount>0)
                Graphics.DrawMeshInstanced(quad,0,paint,matrices,LastDrawCount,null,
                    ShadowCastingMode.Off,false,0,cameraCache,LightProbeUsage.Off);
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
