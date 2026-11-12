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

        private static bool Prepare(bool includePanels)
        {
            if (wall != null && roof != null && trim != null &&
                (!includePanels || (door != null && glass != null)))
                return true;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return false;
            if (wall == null)
                wall = Make(shader, "Aaris Warm Stucco", new Color(.74f, .70f, .62f), .16f);
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

        public static GameObject Create(Vector3 position,Quaternion rotation,
            float width,float depth,float height,Transform parent,Transform player,
            GameObject originalPrefab,bool includePanels = false)
        {
            if (float.IsNaN(width) || float.IsInfinity(width) ||
                float.IsNaN(depth) || float.IsInfinity(depth) ||
                float.IsNaN(height) || float.IsInfinity(height) ||
                width<8f || depth<9f || height<5f || !Prepare(includePanels)) return null;
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
            float h=height-.48f;
            Box(t,"FloorWithRealCollider",new Vector3(0f,-.075f,0f),
                new Vector3(width-.18f,.15f,depth-.18f),roof);
            Box(t,"FlatRoofWithRealCollider",new Vector3(0f,h+.24f,0f),
                new Vector3(width+.4f,.48f,depth+.4f),roof);
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
            return home;
        }
    }
}
