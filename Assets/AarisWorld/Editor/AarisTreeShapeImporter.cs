#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Aaris.WorldAutomation
{
    // Import-time authoring only: original SpeedTree source, texture UVs and wind weights survive.
    // Height is changed by SpeedTreeImporter's scaleFactor in the existing source .meta.
    // The two radial factors compensate that uniform reduction and taper the thicker base
    // into the crown. Bark, branches and foliage receive the same continuous deformation.
    public sealed class AarisTreeShapeImporter : AssetPostprocessor
    {
        public override uint GetVersion() { return 3; }

        private void OnPostprocessSpeedTree(GameObject root)
        {
            if (!assetPath.StartsWith("Assets/TerrainDemoScene_URP/Prefabs/Trees/", StringComparison.Ordinal)) return;
            string[] fields = (assetImporter.userData ?? "").Split('|');
            // v1/v2 source profiles remain valid. Import v3 reshapes only the
            // mid-trunk taper, retaining reviewed base/crown widths and height.
            if (fields.Length == 0 ||
                (fields[0] != "aaris-tree-shape-v1" &&
                 fields[0] != "aaris-tree-shape-v2")) return;
            if (fields.Length != 3 ||
                !float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float baseScale) ||
                !float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float crownScale) ||
                !Finite(baseScale) || !Finite(crownScale) || baseScale < 1 || baseScale > 4 ||
                crownScale < 1 || crownScale > baseScale)
                throw new InvalidOperationException("Invalid inspected Aaris tree-shape import profile: " + assetPath);

            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            var meshFrames = new Dictionary<Mesh, Matrix4x4>();
            var billboards = new HashSet<Mesh>();
            float height = 0;
            foreach (MeshFilter filter in filters)
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                if (!mesh.isReadable) throw new InvalidOperationException("SpeedTree import geometry is unreadable: " + assetPath);
                Matrix4x4 frame = root.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                if (meshFrames.TryGetValue(mesh, out Matrix4x4 previous))
                {
                    if (previous != frame) throw new InvalidOperationException("Shared tree mesh has inconsistent import transforms");
                    continue;
                }
                meshFrames.Add(mesh, frame);
                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                if (mesh.name.IndexOf("Billboard", StringComparison.OrdinalIgnoreCase) >= 0) billboards.Add(mesh);
                if (renderer != null)
                    foreach (Material material in renderer.sharedMaterials)
                        if (material != null && (material.IsKeywordEnabled("EFFECT_BILLBOARD") ||
                            material.name.IndexOf("Billboard", StringComparison.OrdinalIgnoreCase) >= 0)) billboards.Add(mesh);
                foreach (Vector3 vertex in mesh.vertices) height = Mathf.Max(height, frame.MultiplyPoint3x4(vertex).y);
            }
            if (!Finite(height) || height < .1f) throw new InvalidOperationException("Invalid source tree height: " + assetPath);
            foreach (var item in meshFrames) Deform(item.Key, item.Value, height, baseScale, crownScale, billboards.Contains(item.Key));
            foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true)) group.RecalculateBounds();
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        // Deterministic, species-aware mid-trunk fullness. A monotonically
        // increasing taper coordinate stays within [0,1], so neither the reviewed
        // root envelope nor the crown envelope grows. No added vertices, random
        // instance widths, new UVs or runtime per-frame mesh processing.
        // Use exactly the same profile for positions, SpeedTree morph/wind anchors,
        // normals and tangents; their derivatives must agree to avoid LOD pops.
        private static void RadialProfile(float y, float height, float lower, float upper,
            out float radial, out float derivative)
        {
            float u = Mathf.Clamp01(y / height);
            float smooth = u * u * (3f - 2f * u);
            float contrast = Mathf.Clamp01(lower / upper - 1f);
            float bias = Mathf.Lerp(0.35f, 0.60f, contrast);
            float shaped = smooth - bias * smooth * (1f - smooth);
            radial = Mathf.Lerp(lower, upper, shaped);
            derivative = (u > 0f && u < 1f)
                ? (upper - lower) * (1f - bias + 2f * bias * smooth) *
                  6f * u * (1f - u) / height
                : 0f;
        }

        private static Vector3 WarpPoint(Vector3 point, Matrix4x4 toRoot, Matrix4x4 toMesh, float height, float lower, float upper)
        {
            Vector3 p = toRoot.MultiplyPoint3x4(point);
            RadialProfile(p.y, height, lower, upper, out float radial, out _);
            return toMesh.MultiplyPoint3x4(new Vector3(p.x * radial, p.y, p.z * radial));
        }

        private static void Deform(Mesh mesh, Matrix4x4 toRoot, float height, float lower, float upper, bool billboard)
        {
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            if (normals.Length != vertices.Length || tangents.Length != vertices.Length)
                throw new InvalidOperationException("Tree normals/tangents must be present; refusing to discard authored attributes");
            Matrix4x4 toMesh = toRoot.inverse;
            Matrix4x4 normalToRoot = toMesh.transpose, normalToMesh = toRoot.transpose;
            var uv1 = new List<Vector4>(); var uv2 = new List<Vector4>(); var uv3 = new List<Vector4>();
            if (!billboard)
            {
                mesh.GetUVs(1, uv1); mesh.GetUVs(2, uv2); mesh.GetUVs(3, uv3);
                if (uv1.Count != vertices.Length || uv2.Count != vertices.Length || uv3.Count != vertices.Length)
                    throw new InvalidOperationException("Expected SpeedTree 8 anchor/morph vertex channels");
            }
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = toRoot.MultiplyPoint3x4(vertices[i]);
                if (!billboard)
                {
                    // SpeedTree 8 stores the next-LOD position in UV2.xyz and the
                    // leaf-facing/wind anchor in (UV1.z, UV1.w, UV2.w). Deforming
                    // POSITION alone would snap LODs and detach animated leaves.
                    // Contract: Unity Graphics ShaderGraphLibrary/Nature/SpeedTreeCommon.hlsl
                    // and SpeedTree8Wind.hlsl. UV0.xy and packed wind weights stay intact.
                    Vector4 a = uv1[i], m = uv2[i];
                    Vector3 morph = WarpPoint(new Vector3(m.x, m.y, m.z), toRoot, toMesh, height, lower, upper);
                    int geometryType = (int)(uv3[i].w + .25f);
                    if (geometryType > 3) geometryType -= 2;
                    if (geometryType > 1)
                    {
                        Vector3 anchor = WarpPoint(new Vector3(a.z, a.w, m.w), toRoot, toMesh, height, lower, upper);
                        a.z = anchor.x; a.w = anchor.y; m.w = anchor.z;
                    }
                    m.x = morph.x; m.y = morph.y; m.z = morph.z;
                    uv1[i] = a; uv2[i] = m;
                }
                RadialProfile(p.y, height, lower, upper, out float radial, out float derivative);
                Vector3 n = normalToRoot.MultiplyVector(normals[i]);
                Vector3 warpedNormal = new Vector3(n.x / radial,
                    n.y - derivative * (p.x * n.x + p.z * n.z) / radial, n.z / radial);
                Vector3 tangent = toRoot.MultiplyVector(new Vector3(tangents[i].x, tangents[i].y, tangents[i].z));
                Vector3 warpedTangent = new Vector3(radial * tangent.x + p.x * derivative * tangent.y,
                    tangent.y, radial * tangent.z + p.z * derivative * tangent.y);
                Vector3 finalNormal = normalToMesh.MultiplyVector(warpedNormal).normalized;
                Vector3 finalTangent = toMesh.MultiplyVector(warpedTangent);
                finalTangent = (finalTangent - finalNormal * Vector3.Dot(finalTangent, finalNormal)).normalized;
                vertices[i] = toMesh.MultiplyPoint3x4(new Vector3(p.x * radial, p.y, p.z * radial));
                normals[i] = finalNormal;
                tangents[i] = new Vector4(finalTangent.x, finalTangent.y, finalTangent.z, tangents[i].w);
                if (!Finite(vertices[i].x) || !Finite(vertices[i].y) || !Finite(vertices[i].z) ||
                    finalNormal.sqrMagnitude < .9f || finalTangent.sqrMagnitude < .9f)
                    throw new InvalidOperationException("Invalid deformed tree vertex/frame");
            }
            mesh.vertices = vertices; mesh.normals = normals; mesh.tangents = tangents;
            if (!billboard) { mesh.SetUVs(1, uv1); mesh.SetUVs(2, uv2); }
            mesh.RecalculateBounds();
        }
    }
}
#endif
