using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Aaris.MapReview.Editor
{
    // Shared authored-in-code loot meshes. No primitive colliders are ever
    // created; the existing medical collection/streaming authority stays intact.
    public static class AarisSupplyMeshBaker
    {
        private static Mesh medical, helmet, crate;
        private sealed class Builder
        {
            private readonly List<Vector3> vertices = new List<Vector3>(384);
            private readonly List<int>[] indices;
            internal Builder(int slots)
            {
                indices = new List<int>[slots];
                for (int i = 0; i < slots; i++) indices[i] = new List<int>(512);
            }
            private void Tri(Vector3 a, Vector3 b, Vector3 c, int slot)
            {
                int n = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(c);
                indices[slot].Add(n); indices[slot].Add(n+1); indices[slot].Add(n+2);
            }
            private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int slot)
            {
                int n = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
                indices[slot].Add(n); indices[slot].Add(n+1); indices[slot].Add(n+2);
                indices[slot].Add(n); indices[slot].Add(n+2); indices[slot].Add(n+3);
            }
            internal void Box(Vector3 center, Vector3 size, float bevel, int slot)
            {
                Vector3 h = size * .5f;
                float b = Mathf.Min(bevel, Mathf.Min(h.x, Mathf.Min(h.y,h.z)) * .8f);
                Vector3[][] rings = new Vector3[4][];
                for (int r = 0; r < 4; r++)
                {
                    float inset = r == 0 || r == 3 ? b : 0f;
                    float x = h.x-inset, z = h.z-inset;
                    float y = r == 0 ? -h.y : r == 1 ? -h.y+b : r == 2 ? h.y-b : h.y;
                    float c = Mathf.Min(b, Mathf.Min(x,z)*.5f);
                    rings[r] = new[] {
                        center+new Vector3(-x+c,y,-z), center+new Vector3(x-c,y,-z),
                        center+new Vector3(x,y,-z+c), center+new Vector3(x,y,z-c),
                        center+new Vector3(x-c,y,z), center+new Vector3(-x+c,y,z),
                        center+new Vector3(-x,y,z-c), center+new Vector3(-x,y,-z+c)
                    };
                }
                for (int i = 0; i < 8; i++)
                {
                    int j = (i+1)%8;
                    for (int r = 0; r < 3; r++)
                        Quad(rings[r][i],rings[r+1][i],rings[r+1][j],rings[r][j],slot);
                    Tri(center+Vector3.down*h.y,rings[0][i],rings[0][j],slot);
                    Tri(center+Vector3.up*h.y,rings[3][j],rings[3][i],slot);
                }
            }
            internal void HelmetRing(float lower, float upper, float lx, float lz,
                float ux, float uz, int slot)
            {
                const int steps = 24;
                int start = vertices.Count;
                for (int i = 0; i < steps; i++)
                {
                    float angle = i * Mathf.PI * 2f / steps;
                    float x = Mathf.Cos(angle), z = Mathf.Sin(angle);
                    vertices.Add(new Vector3(x*lx,lower,z*lz));
                    vertices.Add(new Vector3(x*ux,upper,z*uz));
                }
                for (int i = 0; i < steps; i++)
                {
                    int a=start+i*2, b=start+((i+1)%steps)*2;
                    indices[slot].Add(a);indices[slot].Add(a+1);indices[slot].Add(b+1);
                    indices[slot].Add(a);indices[slot].Add(b+1);indices[slot].Add(b);
                }
            }
            internal void HelmetDome()
            {
                const int steps=24;
                float[] y={.105f,.155f,.238f,.312f,.365f,.393f,.398f};
                float[] rx={.285f,.29f,.275f,.231f,.155f,.076f,.001f};
                float[] rz={.26f,.27f,.256f,.217f,.143f,.067f,.001f};
                int start=vertices.Count;
                for(int r=0;r<y.Length;r++)
                for(int i=0;i<steps;i++)
                {
                    float a=i*Mathf.PI*2f/steps;
                    vertices.Add(new Vector3(Mathf.Cos(a)*rx[r],y[r],Mathf.Sin(a)*rz[r]));
                }
                for(int r=0;r<y.Length-1;r++)
                for(int i=0;i<steps;i++)
                {
                    int a=start+r*steps+i,b=start+r*steps+(i+1)%steps;
                    indices[0].Add(a);indices[0].Add(a+steps);indices[0].Add(b+steps);
                    indices[0].Add(a);indices[0].Add(b+steps);indices[0].Add(b);
                }
            }
            internal Mesh Finish(string name, float scale = 1f, float baseHeight = 0f)
            {
                if (scale != 1f || baseHeight != 0f)
                {
                    for (int i = 0; i < vertices.Count; i++)
                        vertices[i] = (vertices[i] - Vector3.up * baseHeight) * scale;
                }
                var mesh = new Mesh { name=name };
                mesh.SetVertices(vertices);
                mesh.subMeshCount=indices.Length;
                for (int i=0;i<indices.Length;i++) mesh.SetTriangles(indices[i],i);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        private static Mesh Medical
        {
            get
            {
                if (medical != null) return medical;
                var b = new Builder(3);
                b.Box(new Vector3(0f, .090f, 0f), new Vector3(.518f, .158f, .380f), .023f, 2);
                b.Box(new Vector3(0f, .203f, 0f), new Vector3(.534f, .066f, .394f), .017f, 0);
                b.Box(new Vector3(0f, .170f, 0f), new Vector3(.511f, .022f, .374f), .003f, 2);
                for (int side = -1; side <= 1; side += 2)
                {
                    b.Box(new Vector3(side * .214f, .022f, 0f),
                        new Vector3(.069f, .040f, .393f), .010f, 2);
                    b.Box(new Vector3(side * .153f, .163f, -.196f),
                        new Vector3(.069f, .083f, .031f), .008f, 0);
                    b.Box(new Vector3(side * .143f, .172f, .195f),
                        new Vector3(.081f, .048f, .028f), .008f, 2);
                    b.Box(new Vector3(side * .086f, .112f, -.203f),
                        new Vector3(.037f, .036f, .056f), .008f, 2);
                }
                b.Box(new Vector3(0f, .112f, -.232f),
                    new Vector3(.200f, .035f, .030f), .008f, 2);
                // Three flush painted faces form a broad medical marking.
                b.Box(new Vector3(0f, .237f, 0f), new Vector3(.084f, .004f, .263f), .001f, 1);
                b.Box(new Vector3(-.092f, .237f, 0f), new Vector3(.100f, .004f, .084f), .001f, 1);
                b.Box(new Vector3(.092f, .237f, 0f), new Vector3(.100f, .004f, .084f), .001f, 1);
                medical=b.Finish("Aaris shared beveled medical case");
                return medical;
            }
        }
        private static Mesh Crate
        {
            get
            {
                if (crate != null) return crate;
                var b = new Builder(2);
                b.Box(new Vector3(0, .21f, 0), new Vector3(.68f, .40f, .48f), .026f, 0);
                b.Box(new Vector3(0, .443f, 0), new Vector3(.71f, .073f, .50f), .018f, 0);
                for (int side = -1; side <= 1; side += 2)
                {
                    float x = side * .218f;
                    b.Box(new Vector3(x, .477f, 0), new Vector3(.053f, .025f, .49f), .009f, 1);
                    b.Box(new Vector3(x, .383f, -.244f), new Vector3(.049f, .114f, .027f), .007f, 1);
                    b.Box(new Vector3(x, .333f, -.250f), new Vector3(.064f, .025f, .027f), .005f, 0);
                    b.Box(new Vector3(0, .154f, side * .236f), new Vector3(.49f, .032f, .025f), .008f, 0);
                    b.Box(new Vector3(0, .102f, side * .236f), new Vector3(.52f, .024f, .020f), .006f, 0);
                    b.Box(new Vector3(side * .332f, .234f, 0f), new Vector3(.030f, .19f, .30f), .012f, 0);
                }
                b.Box(new Vector3(0, .276f, -.249f), new Vector3(.17f, .032f, .027f), .008f, 1);
                crate = b.Finish("Aaris shared ammunition crate");
                return crate;
            }
        }

        private static Mesh Helmet
        {
            get
            {
                if (helmet != null) return helmet;
                var b = new Builder(2);
                b.HelmetDome();
                b.HelmetRing(.077f,.105f,.265f,.247f,.292f,.272f,1);
                b.Box(new Vector3(0,.115f,-.245f),new Vector3(.39f,.070f,.112f),.018f,0);
                // Forehead mounting plate and paired accessory rails share the trim submesh.
                b.Box(new Vector3(0,.208f,-.265f),new Vector3(.112f,.062f,.028f),.009f,1);
                b.Box(new Vector3(0,.218f,-.281f),new Vector3(.060f,.028f,.012f),.004f,0);
                for (int side = -1; side <= 1; side += 2)
                {
                    b.Box(new Vector3(side*.275f,.165f,0),new Vector3(.043f,.064f,.208f),.011f,1);
                    for (int lug = -1; lug <= 1; lug += 2)
                        b.Box(new Vector3(side * .290f, .179f, lug * .044f),
                            new Vector3(.022f, .014f, .023f), .004f, 0);
                }
                helmet=b.Finish("Aaris shared tactical helmet", .84f, .077f);
                return helmet;
            }
        }
        [MenuItem("Aaris/Art/Bake shared ground supply meshes")]
        public static void Bake()
        {
            WriteMesh(Medical, "AarisLootMedical");
            WriteMesh(Helmet, "AarisLootHelmet");
            WriteMesh(Crate, "AarisLootAmmoCrate");
            AssetDatabase.SaveAssets();
            medical = helmet = crate = null;
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        private static void WriteMesh(Mesh generated, string resourceName)
        {
            string path = "Assets/Resources/" + resourceName + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
                AssetDatabase.CreateAsset(generated, path);
            else
            {
                EditorUtility.CopySerialized(generated, existing);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(generated);
            }
        }
    }
}
