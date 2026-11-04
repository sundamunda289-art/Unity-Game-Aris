// Explicit authoring helper. Kept OUTSIDE Assets; the launcher injects it only
// into disposable projects. No import/build/startup callbacks are registered.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.WorldAutomation
{
    public static class TreeCoreBake
    {
        [Serializable] public sealed class Source
        {
            public string source, prefab, output, tag;
            public Binding[] bindings;
        }
        [Serializable] public sealed class Binding
        {
            // Strings preserve Unity's 64-bit local identifiers in JSON clients.
            public string sourceId, bakedId, bakedGuid, fingerprint;
            public int vertices, submeshes;
        }
        [Serializable] public sealed class Request
        {
            public string mode, unityVersion;
            public Source[] sources;
        }
        [Serializable] public sealed class Response
        {
            public string status, mode, unityVersion;
            public Source[] sources;
        }

        public static void Run()
        {
            string input = Environment.GetEnvironmentVariable("AARIS_TREE_BAKE_REQUEST");
            string output = Environment.GetEnvironmentVariable("AARIS_TREE_BAKE_RESPONSE");
            var request = JsonUtility.FromJson<Request>(File.ReadAllText(input));
            Require(Application.unityVersion == request.unityVersion, "Matching project Unity Editor required");
            Require(request.sources != null && request.sources.Length == 6, "Exactly six inspected tree sources required");
            Require(request.mode == "bake" || request.mode == "verify", "Explicit bake/verify mode required");
            if (request.mode == "bake")
                Require(TypeCache.GetTypesDerivedFrom<AssetPostprocessor>().Any(t => t.FullName ==
                    "Aaris.WorldAutomation.AarisTreeShapeImporter"), "Corrected importer must be present for the explicit bake");
            if (request.mode == "verify")
                Require(!TypeCache.GetTypesDerivedFrom<AssetPostprocessor>().Any(t => t.FullName ==
                    "Aaris.WorldAutomation.AarisTreeShapeImporter"), "Automatic deformation must be absent during fresh verification");
            foreach (Source source in request.sources)
            {
                if (request.mode == "bake") Bake(source);
                else Verify(source);
            }
            File.WriteAllText(output, JsonUtility.ToJson(new Response {
                status = "ok", mode = request.mode, unityVersion = Application.unityVersion, sources = request.sources
            }, true));
        }

        static void Bake(Source source)
        {
            Require(AssetImporter.GetAtPath(source.source).userData == source.tag,
                "Reviewed source shape profile required: " + source.source);
            AssetDatabase.ImportAsset(source.source, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var meshes = SourceMeshes(source.source);
            Require(source.bindings.Length == 4 && meshes.Count == 4, "Four SpeedTree mesh LODs required");
            string outputPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), source.output);
            Require(!File.Exists(outputPath) && !File.Exists(outputPath + ".meta"), "Never overwrite an existing core mesh asset");
            string directory = Path.GetDirectoryName(source.output).Replace('\\', '/');
            Require(AssetDatabase.IsValidFolder(directory), "Existing output directory required");
            bool first = true;
            foreach (Binding binding in source.bindings)
            {
                Require(meshes.TryGetValue(binding.sourceId, out Mesh original), "Missing source mesh " + binding.sourceId);
                binding.fingerprint = Fingerprint(original);
                binding.vertices = original.vertexCount; binding.submeshes = original.subMeshCount;
                // Native clone preserves every packed SpeedTree vertex stream, not
                // just positions/UV0. Never recalculate normals, tangents or bounds.
                Mesh clone = UnityEngine.Object.Instantiate(original);
                clone.name = original.name; clone.hideFlags = HideFlags.None;
                if (!original.isReadable && clone.isReadable) clone.UploadMeshData(true);
                Require(Fingerprint(clone) == binding.fingerprint, "Native clone differs from corrected import");
                if (first) AssetDatabase.CreateAsset(clone, source.output);
                else AssetDatabase.AddObjectToAsset(clone, source.output);
                first = false;
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(source.output, ImportAssetOptions.ForceSynchronousImport);
            var saved = AssetDatabase.LoadAllAssetsAtPath(source.output).OfType<Mesh>().ToArray();
            Require(saved.Length == 4, "Four saved native meshes required");
            var consumed = new HashSet<Mesh>();
            foreach (Binding binding in source.bindings)
            {
                Mesh mesh = saved.Single(m => m.name == meshes[binding.sourceId].name && !consumed.Contains(m));
                consumed.Add(mesh);
                Require(Fingerprint(mesh) == binding.fingerprint, "Saved geometry/attributes changed");
                Require(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long id), "Saved mesh identity missing");
                binding.bakedGuid = guid; binding.bakedId = id.ToString(CultureInfo.InvariantCulture);
            }
        }

        static void Verify(Source source)
        {
            Require(string.IsNullOrEmpty(AssetImporter.GetAtPath(source.source).userData), "Retired shape tag still active");
            var saved = AssetDatabase.LoadAllAssetsAtPath(source.output).OfType<Mesh>().ToArray();
            Require(saved.Length == 4, "Saved native LOD mesh count changed");
            foreach (Binding binding in source.bindings)
            {
                Mesh mesh = saved.Single(m => Identity(m) == binding.bakedId);
                Require(AssetDatabase.AssetPathToGUID(source.output) == binding.bakedGuid &&
                    Fingerprint(mesh) == binding.fingerprint && mesh.vertexCount == binding.vertices &&
                    mesh.subMeshCount == binding.submeshes, "Cold import changed baked geometry or identity");
            }
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(source.prefab);
            Require(prefab != null, "Saved tree prefab missing");
            var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
            Require(filters.Length == 4, "Tree prefab must retain four mesh LODs");
            var used = new HashSet<string>();
            foreach (MeshFilter filter in filters)
            {
                Require(filter.sharedMesh != null && AssetDatabase.GetAssetPath(filter.sharedMesh) == source.output,
                    "Prefab still depends on an imported source mesh");
                Require(used.Add(Identity(filter.sharedMesh)), "Repeated/missing prefab LOD mesh binding");
                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                Require(renderer != null && renderer.sharedMaterials.Length == filter.sharedMesh.subMeshCount &&
                    renderer.sharedMaterials.All(m => m != null && m.shader != null), "Missing tree material/shader binding");
                Tree tree = filter.GetComponent<Tree>();
                Require(tree != null, "SpeedTree wind component missing");
                var wind = new SerializedObject(tree).FindProperty("m_SpeedTreeWindAsset");
                Require(wind != null && wind.objectReferenceValue != null &&
                    AssetDatabase.GetAssetPath(wind.objectReferenceValue) == source.source, "SpeedTree wind asset missing");
            }
            Require(used.SetEquals(source.bindings.Select(b => b.bakedId)), "Saved LOD identities differ");
            LODGroup[] groups = prefab.GetComponentsInChildren<LODGroup>(true);
            Require(groups.Length == 1 && groups[0].GetLODs().Length == 4, "Tree LODGroup changed");
            Require(groups[0].GetLODs().All(l => l.renderers.Length > 0 && l.renderers.All(r => r != null)), "Missing LOD renderer");
            // Reimporting the source without the custom postprocessor must leave
            // every persisted mesh unchanged. The proprietary source remains a
            // standard Unity wind/material dependency, never a geometry generator.
            AssetDatabase.ImportAsset(source.source, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var plain = SourceMeshes(source.source);
            foreach (Binding binding in source.bindings)
            {
                Require(Fingerprint(plain[binding.sourceId]) != binding.fingerprint,
                    "Correction was not captured separately from the plain source import");
                Require(Fingerprint(saved.Single(m => Identity(m) == binding.bakedId)) == binding.fingerprint,
                    "Source reimport mutated a persisted mesh");
            }
        }

        static Dictionary<string, Mesh> SourceMeshes(string path)
        {
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToDictionary(Identity);
        }
        static string Identity(Mesh mesh)
        {
            Require(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long id), "Mesh identity missing");
            return id.ToString(CultureInfo.InvariantCulture);
        }
        static string Fingerprint(Mesh mesh)
        {
            Require(mesh.vertexCount > 0 && mesh.subMeshCount > 0 && mesh.blendShapeCount == 0 && mesh.bindposes.Length == 0,
                "Unsupported or empty SpeedTree mesh");
            Require(mesh.HasVertexAttribute(VertexAttribute.Normal) && mesh.HasVertexAttribute(VertexAttribute.Tangent), "Missing vertex frames");
            using (var data = MeshUtility.AcquireReadOnlyMeshData(mesh))
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes))
            {
                var d = data[0];
                // Validate all geometry/frame values before trusting raw-byte equality.
                using (var positions = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp))
                using (var normals = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp))
                using (var tangents = new NativeArray<Vector4>(mesh.vertexCount, Allocator.Temp))
                {
                    d.GetVertices(positions); d.GetNormals(normals); d.GetTangents(tangents);
                    for (int i = 0; i < positions.Length; ++i)
                    {
                        Check(positions[i]); Check(normals[i]);
                        var t = tangents[i]; Check(new Vector3(t.x, t.y, t.z));
                        Require(normals[i].sqrMagnitude > .9f && new Vector3(t.x, t.y, t.z).sqrMagnitude > .9f &&
                            !float.IsNaN(t.w) && !float.IsInfinity(t.w), "Invalid vertex frame");
                    }
                }
                writer.Write(mesh.vertexCount); writer.Write(mesh.isReadable); writer.Write((int)mesh.indexFormat);
                var attributes = mesh.GetVertexAttributes(); writer.Write(attributes.Length);
                foreach (var attribute in attributes)
                {
                    writer.Write((int)attribute.attribute); writer.Write((int)attribute.format);
                    writer.Write(attribute.dimension); writer.Write(attribute.stream);
                }
                writer.Write(mesh.vertexBufferCount);
                for (int stream = 0; stream < mesh.vertexBufferCount; ++stream)
                {
                    var buffer = d.GetVertexData<byte>(stream); writer.Write(buffer.Length);
                    for (int i = 0; i < buffer.Length; ++i) writer.Write(buffer[i]);
                }
                var indices = d.GetIndexData<byte>(); writer.Write(indices.Length);
                for (int i = 0; i < indices.Length; ++i) writer.Write(indices[i]);
                writer.Write(mesh.subMeshCount);
                for (int i = 0; i < mesh.subMeshCount; ++i)
                {
                    var sub = d.GetSubMesh(i); writer.Write(sub.indexStart); writer.Write(sub.indexCount);
                    writer.Write(sub.baseVertex); writer.Write((int)sub.topology);
                    writer.Write(sub.firstVertex); writer.Write(sub.vertexCount); Write(writer, sub.bounds);
                }
                Write(writer, mesh.bounds); writer.Flush();
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        static void Write(BinaryWriter writer, Bounds bounds)
        {
            Check(bounds.center); Check(bounds.extents);
            writer.Write(bounds.center.x); writer.Write(bounds.center.y); writer.Write(bounds.center.z);
            writer.Write(bounds.extents.x); writer.Write(bounds.extents.y); writer.Write(bounds.extents.z);
        }
        static void Check(Vector3 v)
        {
            Require(!float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
                !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z), "Non-finite mesh data");
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
