using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview
{
    /// <summary>
    /// Only used at explicitly marked expansion sites. Creates a real
    /// hollow home with wall segments around the doorway/windows.
    /// New battle-royale homes default to clear, physically open passages:
    /// no door leaf, glass renderer, pane collider or breakable-panel Update.
    /// Never attaches fake doors to original one-piece MeshColliders.
    /// </summary>
    internal static class AarisModularHouse
    {
        private static Material wall, roof, door, trim, glass;
        private const float WallThickness = .24f;
        private const float DoorWidth = 1.52f, DoorHeight = 2.34f;
        private const float WindowWidth = 1.38f, WindowHeight = 1.12f;
        private const float Sill = 1.17f;

        private static Material Make(Shader shader, string name, Color tint, float smooth,
            bool transparent = false)
        {
            var m = new Material(shader) { name = name, enableInstancing = true };
            m.SetColor("_BaseColor", tint);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", .03f);
            if (transparent)
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetFloat("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetOverrideTag("RenderType", "Transparent");
                m.renderQueue = (int)RenderQueue.Transparent;
            }
            return m;
        }

        // Reuse the existing baked limestone PBR maps already loaded by facade
        // trims. No extra runtime texture generation, material clones or shaders.
        private static void ApplySharedMasonryMaps(Material material)
        {
            Texture2D albedo = Resources.Load<Texture2D>(
                "AarisBuildings/ArchitecturalLimestone");
            if (albedo != null && material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", albedo);
                // Preserve visible grain across long straight facade walls.
                material.SetTextureScale("_BaseMap", new Vector2(2.5f, 1.5f));
            }
            Texture2D normal = Resources.Load<Texture2D>(
                "AarisBuildings/ArchitecturalLimestoneNormal");
            if (normal != null && material.HasProperty("_BumpMap"))
            {
                material.SetTexture("_BumpMap", normal);
                material.SetTextureScale("_BumpMap", new Vector2(2.5f, 1.5f));
                material.SetFloat("_BumpScale", .42f);
                material.EnableKeyword("_NORMALMAP");
            }
        }

        private static bool Prepare(GameObject originalPrefab, bool includePanels)
        {
            if (wall != null && roof != null && trim != null &&
                (!includePanels || (door != null && glass != null)))
                return true;
            // The imported settlement catalog retains direct prefab->material
            // references. Prefer that already retained URP shader so our
            // first optional house does not require an unrelated name lookup.
            // Keep the old fallback for prefabs with missing/other renderers.
            Shader shader = null;
            if (originalPrefab != null)
            {
                var source = originalPrefab.GetComponentInChildren<MeshRenderer>(true);
                if (source != null)
                    foreach (Material material in source.sharedMaterials)
                    {
                        if (material != null && material.shader != null &&
                            material.shader.name == "Universal Render Pipeline/Lit")
                        {
                            shader = material.shader;
                            break;
                        }
                    }
            }
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return false;
            if (wall == null)
            {
                wall = Make(shader, "Aaris Baked Limestone Stucco",
                    new Color(.85f, .82f, .77f), .16f);
                ApplySharedMasonryMaps(wall);
            }
            if (roof == null)
                roof = Make(shader, "Aaris Slate Roof", new Color(.28f, .32f, .36f), .23f);
            if (trim == null)
                trim = Make(shader, "Aaris Dark Frames", new Color(.16f, .21f, .23f), .29f);
            // Do not allocate a transparent material on budget Android houses.
            if (includePanels)
            {
                if (door == null)
                    door = Make(shader, "Aaris Walnut Door", new Color(.39f, .24f, .15f), .22f);
                if (glass == null)
                    glass = Make(shader, "Aaris Glass Pane", new Color(.54f, .78f, .83f, .60f), .60f, true);
            }
            return true;
        }

        // Reuse the existing local game's already imported URP textures.
        // Never download unverified third-party art at gameplay runtime.
        private static Material Match(GameObject original, string keywords,
            Material fallback)
        {
            if (original == null) return fallback;
            var renderer = original.GetComponentInChildren<MeshRenderer>(true);
            if (renderer == null) return fallback;
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null || material.shader == null ||
                    material.shader.name != "Universal Render Pipeline/Lit") continue;
                string label = material.name.ToLowerInvariant();
                foreach (var term in keywords.Split(new[] { '|' }))
                    if (label.Contains(term)) return material;
            }
            return fallback;
        }

        private static GameObject Box(Transform parent, string label, Vector3 pos,
            Vector3 size, Material material, bool collide = true)
        {
            GameObject o = GameObject.CreatePrimitive(PrimitiveType.Cube);
            o.name = label;
            o.transform.SetParent(parent, false);
            o.transform.localPosition = pos;
            o.transform.localScale = size;
            o.GetComponent<MeshRenderer>().sharedMaterial = material;
            if (!collide) o.GetComponent<BoxCollider>().enabled = false;
            return o;
        }

        // Create actual solid wall sections AROUND an opening, not a hollow
        // visual cutout painted over a solid nonconvex MeshCollider.
        private static void WallSection(Transform parent, float x0, float x1,
            float y0, float y1, float z)
        {
            if (x1 - x0 <= .02f || y1 - y0 <= .02f) return;
            Box(parent, "SolidWall", new Vector3((x0+x1)*.5f, (y0+y1)*.5f,z),
                new Vector3(x1-x0,y1-y0,WallThickness), wall);
        }

        private static void Window(Transform root, float center, float z, string id,
            bool includePanels)
        {
            // The frame stays visible, but the opening remains empty in the
            // default battle-royale layout; neither glass nor its collider exists.
            if (includePanels)
            {
                GameObject mount = new GameObject(id + "_WindowBreakable");
                mount.transform.SetParent(root, false);
                mount.transform.localPosition = new Vector3(center, 0f, z);
                GameObject pane = Box(mount.transform, "ShootableGlass",
                    new Vector3(0f,Sill+WindowHeight*.5f,0f),
                    new Vector3(WindowWidth-.12f,WindowHeight-.11f,.075f),glass);
                mount.AddComponent<AarisBreakablePanel>().Configure(
                    pane.transform,pane.GetComponent<BoxCollider>(),null,false,15f,root);
            }
            Box(root, id+"_Sill",new Vector3(center,Sill-.06f,z-.09f),
                new Vector3(WindowWidth+.13f,.11f,.22f),trim);
            Box(root, id+"_Lintel",new Vector3(center,Sill+WindowHeight+.06f,z-.09f),
                new Vector3(WindowWidth+.13f,.11f,.22f),trim);
            Box(root, id+"_LeftJamb",new Vector3(center-WindowWidth*.5f-.05f,
                Sill+WindowHeight*.5f,z-.09f),new Vector3(.10f,WindowHeight,.22f),trim);
            Box(root, id+"_RightJamb",new Vector3(center+WindowWidth*.5f+.05f,
                Sill+WindowHeight*.5f,z-.09f),new Vector3(.10f,WindowHeight,.22f),trim);
        }

        private static void Front(Transform root, float width, float depth,
            float height, Transform player, bool includePanels)
        {
            float z = -depth*.5f, last = -width*.5f;
            float winX = width*.31f;
            for(int i=0;i<3;i++)
            {
                float x = i==0?-winX:i==1?0f:winX;
                bool isDoor = i==1;
                float w = isDoor?DoorWidth:WindowWidth;
                float left = x-w*.5f, right=x+w*.5f;
                WallSection(root,last,left,0f,height,z);
                if (isDoor)
                {
                    WallSection(root,left,right,DoorHeight,height,z);
                    if (includePanels)
                    {
                        GameObject hinge = new GameObject("AutomaticBreakableDoorHinge");
                        hinge.transform.SetParent(root,false);
                        hinge.transform.localPosition = new Vector3(left+.05f,0f,z);
                        GameObject leaf = Box(hinge.transform,"RealDoorLeaf",
                            new Vector3((DoorWidth-.10f)*.5f,DoorHeight*.5f,0f),
                            new Vector3(DoorWidth-.10f,DoorHeight-.08f,.11f),door);
                        hinge.AddComponent<AarisBreakablePanel>().Configure(
                            leaf.transform,leaf.GetComponent<BoxCollider>(),player,true,85f,root);
                    }
                    Box(root,"DoorLintel",new Vector3(0f,DoorHeight+.06f,z-.1f),
                        new Vector3(DoorWidth+.25f,.12f,.21f),trim);
                    Box(root,"DoorLeftJamb",new Vector3(left-.05f,DoorHeight*.5f,z-.1f),
                        new Vector3(.10f,DoorHeight,.21f),trim);
                    Box(root,"DoorRightJamb",new Vector3(right+.05f,DoorHeight*.5f,z-.1f),
                        new Vector3(.10f,DoorHeight,.21f),trim);
                }
                else
                {
                    WallSection(root,left,right,0f,Sill,z);
                    WallSection(root,left,right,Sill+WindowHeight,height,z);
                    Window(root,x,z,i==0?"FrontLeft":"FrontRight",includePanels);
                }
                last=right;
            }
            WallSection(root,last,width*.5f,0f,height,z);
        }

        private static void Back(Transform root,float width,float depth,float height,
            bool includePanels)
        {
            float z=depth*.5f;
            float left=-WindowWidth*.5f,right=WindowWidth*.5f;
            WallSection(root,-width*.5f,left,0f,height,z);
            WallSection(root,left,right,0f,Sill,z);
            WallSection(root,left,right,Sill+WindowHeight,height,z);
            WallSection(root,right,width*.5f,0f,height,z);
            Window(root,0f,z,"Rear",includePanels);
        }

        // A pitched silhouette and eaves break the flat box look without
        // transparent surfaces, expensive shaders or extra roof colliders.
        private static void PitchedRoof(Transform root, float width,
            float depth, float wallHeight, AarisOwnedMeshCleanup owned)
        {
            float x = width * .5f + .32f, z = depth * .5f + .34f;
            float baseY = wallHeight + .11f;
            float peakY = baseY + Mathf.Clamp(width * .12f, .75f, 1.35f);
            // Duplicate vertices at each distinct roof plane. The former
            // six-vertex mesh averaged normals across the ridge/gables and
            // made a sharp pitched roof shade like rounded plastic. Keep
            // exactly the same triangles, silhouette, UV tiling and renderer.
            Vector3[] corners = {
                new Vector3(-x,baseY,-z), new Vector3(0f,peakY,-z),
                new Vector3(x,baseY,-z), new Vector3(-x,baseY,z),
                new Vector3(0f,peakY,z), new Vector3(x,baseY,z)
            };
            int[] faceCorners = {
                0,3,1,4, // left slope
                1,4,2,5, // right slope
                0,1,2,   // front gable
                3,5,4    // back gable
            };
            Vector3[] vertices = new Vector3[faceCorners.Length];
            for (int i = 0; i < faceCorners.Length; i++)
                vertices[i] = corners[faceCorners[i]];
            Mesh mesh = new Mesh { name = "Aaris_PitchedRoof" };
            owned.Track(mesh);
            mesh.vertices = vertices;
            mesh.triangles = new[] {
                0,1,2, 2,1,3, 4,5,6, 6,5,7, 8,9,10, 11,12,13
            };
            float lengthTiling = depth / 4f;
            mesh.uv = new[] {
                new Vector2(0,0), new Vector2(0,lengthTiling),
                new Vector2(.5f,1f), new Vector2(.5f,1f+lengthTiling),
                new Vector2(.5f,1f), new Vector2(.5f,1f+lengthTiling),
                new Vector2(1f,0), new Vector2(1f,lengthTiling),
                new Vector2(0,0), new Vector2(.5f,1f), new Vector2(1f,0),
                new Vector2(0,0), new Vector2(1f,0), new Vector2(.5f,1f)
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            GameObject roofObject = new GameObject("GabledPitchedRoof");
            roofObject.transform.SetParent(root, false);
            roofObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            roofObject.AddComponent<MeshRenderer>().sharedMaterial = roof;
        }

        // Unity constructs each modular home synchronously on its main thread.
        // Reuse the query buffers and material grouping for subsequent houses:
        // no mesh, renderer, collision, or authored material is cached/replaced.
        private static readonly Dictionary<Material, List<CombineInstance>> scratchGroups =
            new Dictionary<Material, List<CombineInstance>>();
        private static readonly List<List<CombineInstance>> scratchCombinePool =
            new List<List<CombineInstance>>(8);
        private static readonly List<MeshFilter> scratchFilters = new List<MeshFilter>(96);
        private static readonly List<Material> scratchMaterials = new List<Material>(4);
        private static readonly List<MeshRenderer> scratchOriginals = new List<MeshRenderer>(64);
        // Unity consumes CombineMeshes input synchronously. Keep only a bounded
        // set of exact-sized, reusable arrays for repeated house layouts.
        // Do not pad arrays: a default CombineInstance changes the result.
        private const int MaximumCachedCombineArraySizes = 24;
        private static readonly Dictionary<int, CombineInstance[]> scratchCombineArrays =
            new Dictionary<int, CombineInstance[]>();

        // The static walls, roof and trim share a handful of materials.
        // Merge by material once at spawning time, while keeping every
        // original BoxCollider and interactive door/glass renderer intact.
        // Replaces dozens of renderer submissions with a few per house.
        private static void CombineStaticSurfaces(Transform root, AarisOwnedMeshCleanup owned)
        {
            Dictionary<Material, List<CombineInstance>> groups = scratchGroups;
            List<MeshRenderer> originals = scratchOriginals;
            groups.Clear();
            originals.Clear();
            scratchFilters.Clear();
            scratchMaterials.Clear();
            int groupsUsed = 0;
            try
            {
                Matrix4x4 toRoot = root.worldToLocalMatrix;
                // Unity 6 fills the supplied list without allocating a new
                // MeshFilter[] for every modular house.
                root.GetComponentsInChildren<MeshFilter>(true, scratchFilters);
                foreach (MeshFilter filter in scratchFilters)
                {
                    MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                    Mesh source = filter.sharedMesh;
                    if (renderer == null || !renderer.enabled || source == null ||
                        !source.isReadable ||
                        filter.GetComponentInParent<AarisBreakablePanel>() != null)
                        continue;
                    // Do not allocate Material[] for each wall, frame, or roof.
                    scratchMaterials.Clear();
                    renderer.GetSharedMaterials(scratchMaterials);
                    if (scratchMaterials.Count != 1 || scratchMaterials[0] == null ||
                        source.subMeshCount != 1)
                        continue;
                    Material material = scratchMaterials[0];
                    if (!groups.TryGetValue(material, out List<CombineInstance> group))
                    {
                        if (groupsUsed < scratchCombinePool.Count)
                            group = scratchCombinePool[groupsUsed];
                        else
                        {
                            group = new List<CombineInstance>(24);
                            scratchCombinePool.Add(group);
                        }
                        groupsUsed++;
                        group.Clear();
                        groups.Add(material, group);
                    }
                    group.Add(new CombineInstance {
                        mesh = source, subMeshIndex = 0,
                        transform = toRoot * filter.transform.localToWorldMatrix
                    });
                    originals.Add(renderer);
                }
                if (groups.Count == 0) return;
                // Preserve the original 16-bit index safety check and leave
                // every original renderer enabled if a group is too large.
                foreach (var pair in groups)
                {
                    long vertices = 0;
                    foreach (CombineInstance part in pair.Value)
                        vertices += part.mesh.vertexCount;
                    if (vertices > 65534) return;
                }
                foreach (var pair in groups)
                {
                    Mesh combined = new Mesh { name = "Aaris_StaticHouse_" + pair.Key.name };
                    owned.Track(combined);
                    combined.indexFormat = IndexFormat.UInt16;
                    // Copy to an exact-sized array. Repeated house layouts
                    // reuse it instead of allocating through List.ToArray().
                    // A bounded cache prevents retaining arbitrarily many sizes.
                    int partCount = pair.Value.Count;
                    bool cached = scratchCombineArrays.TryGetValue(
                        partCount, out CombineInstance[] parts);
                    if (!cached)
                    {
                        parts = new CombineInstance[partCount];
                        if (partCount <= 256 &&
                            scratchCombineArrays.Count < MaximumCachedCombineArraySizes)
                        {
                            scratchCombineArrays.Add(partCount, parts);
                            cached = true;
                        }
                    }
                    pair.Value.CopyTo(parts);
                    try
                    {
                        combined.CombineMeshes(parts, true, true, false);
                    }
                    finally
                    {
                        // Never retain Mesh references in static managed buffers.
                        if (cached) System.Array.Clear(parts, 0, parts.Length);
                    }
                    combined.RecalculateBounds();
                    GameObject section = new GameObject("Batched_" + pair.Key.name);
                    section.transform.SetParent(root, false);
                    section.AddComponent<MeshFilter>().sharedMesh = combined;
                    section.AddComponent<MeshRenderer>().sharedMaterial = pair.Key;
                    combined.UploadMeshData(true);
                }
                foreach (MeshRenderer original in originals) original.enabled = false;
            }
            finally
            {
                // Clear references to native Unity objects after each build,
                // even when a 16-bit safety rejection or exception occurs.
                foreach (List<CombineInstance> group in groups.Values) group.Clear();
                groups.Clear();
                originals.Clear();
                scratchFilters.Clear();
                scratchMaterials.Clear();
            }
        }

        public static GameObject Create(Vector3 position,Quaternion rotation,
            float width,float depth,float height,Transform parent,Transform player,
            GameObject originalPrefab,bool includePanels = false)
        {
            if (float.IsNaN(width) || float.IsInfinity(width) ||
                float.IsNaN(depth) || float.IsInfinity(depth) ||
                float.IsNaN(height) || float.IsInfinity(height) ||
                width<8f || depth<9f || height<5f || !Prepare(originalPrefab, includePanels)) return null;
            // Material matching belongs to this house only. An imported material
            // must never become the shared fallback for later unrelated houses.
            Material defaultWall = wall, defaultRoof = roof;
            Material defaultTrim = trim, defaultDoor = door;
            try
            {
                wall = Match(originalPrefab, "sandstone|plaster|brick|stone|concrete", wall);
                roof = Match(originalPrefab, "slate|roof|tile|thatch", roof);
                if (includePanels)
                    door = Match(originalPrefab, "wood|timber|oak", door);
                trim = Match(originalPrefab, "metal|iron|steel", trim);
                GameObject home=new GameObject(includePanels
                    ? "Aaris Modular Door Window House" : "Aaris Open Combat House");
                home.transform.SetParent(parent,false);
                home.transform.SetPositionAndRotation(position,rotation);
                Transform t=home.transform;
                // Own only meshes generated for this house, never prefab/shared meshes.
                AarisOwnedMeshCleanup owned = home.AddComponent<AarisOwnedMeshCleanup>();
                float h=height-.48f;
                Box(t,"FloorWithRealCollider",new Vector3(0f,-.075f,0f),
                    new Vector3(width-.18f,.15f,depth-.18f),roof);
                GameObject roofCollision = Box(t,"FlatRoofWithRealCollider",
                    new Vector3(0f,h+.04f,0f),
                    new Vector3(width+.4f,.12f,depth+.4f),roof);
                // Retain the original safe collision plane but render a
                // more realistic pitched roof instead of a thick flat cube.
                roofCollision.GetComponent<MeshRenderer>().enabled = false;
                PitchedRoof(t,width,depth,h,owned);
                Box(t,"WestSolidWall",new Vector3(-width*.5f,h*.5f,0f),
                    new Vector3(WallThickness,h,depth),wall);
                Box(t,"EastSolidWall",new Vector3(width*.5f,h*.5f,0f),
                    new Vector3(WallThickness,h,depth),wall);
                Front(t,width,depth,h,player,includePanels);
                Back(t,width,depth,h,includePanels);
                Box(t,"EntranceStep",new Vector3(0f,-.02f,-depth*.5f-.32f),
                    new Vector3(2.35f,.12f,.64f),roof);
                Box(t,"RainAwning",new Vector3(0f,DoorHeight+.32f,-depth*.5f-.50f),
                    new Vector3(2.7f,.14f,1.13f),roof);
                Box(t,"FoundationBand",new Vector3(0f,.07f,0f),
                    new Vector3(width+.28f,.15f,depth+.28f),trim,false);
                Box(t,"FrontPorchLeftSupport",
                    new Vector3(-1.28f,DoorHeight*.5f,-depth*.5f-.51f),
                    new Vector3(.13f,DoorHeight,.13f),trim,false);
                Box(t,"FrontPorchRightSupport",
                    new Vector3(1.28f,DoorHeight*.5f,-depth*.5f-.51f),
                    new Vector3(.13f,DoorHeight,.13f),trim,false);
                CombineStaticSurfaces(t,owned);
                return home;
            }
            finally
            {
                wall = defaultWall;
                roof = defaultRoof;
                trim = defaultTrim;
                door = defaultDoor;
            }
        }
    }
}
