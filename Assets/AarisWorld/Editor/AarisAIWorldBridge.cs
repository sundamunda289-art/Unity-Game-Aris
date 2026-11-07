using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview.Editor
{
    // A bounded, file-based Unity Editor tool. GitHub Issue/API callers never execute C# or shell.
    // World-space units are metres. Only an explicit edit intent saves the real scene/TerrainData.
    [InitializeOnLoad]
    public static class AarisAIWorldBridge
    {
        [Serializable] private class Intent
        {
            public string op, target, terrain;
            public long nonce;
            public float x, y, z, radius, width, height;
            public int index, samples;
        }
        [Serializable] private class Point { public float x, y, z; public Point(Vector3 p) { x=p.x; y=p.y; z=p.z; } }
        // A heartbeat is only written by successfully compiled, running Unity
        // Editor code (unlike a Linux desktop or an unlicensed Docker process).
        [Serializable] private class Heartbeat
        {
            public long unixSeconds;
            public string unityVersion, scene, graphicsDevice;
            public bool nativeSceneOpen;
        }
        [Serializable] private class Size { public Vector3 center, extents, size; }
        // Every renderer/submesh is independently measurable. Paths identify leaves,
        // trunks, walls and roof children rather than confusing their parent bounds.
        [Serializable] private class Part
        {
            public string path, rendererType;
            public Size bounds;
            public string[] materials;
            public int meshVertices;
            public bool meshReadable;
        }
        [Serializable] private class Item { public string path, prototype; public Vector3 position; public float distance; public int index; public float widthScale, heightScale; }
        [Serializable] private class Result
        {
            public bool ok;
            public string message, op, target, scene, path, photo, blueprint, treePrototype;
            public long nonce;
            public Vector3 position, rotation, localScale;
            public Size bounds;
            public string[] materials;
            public Item[] objects;
            public Part[] parts;
            public Point[] points;
            public string blueprintSampling;
            public int meshVertices, sampledVertices, boundsFallbackPoints, index, matches;
            public float treeWidthScale, treeHeightScale;
        }
        private static readonly string State = Path.Combine(Directory.GetCurrentDirectory(), "ci-live-control");
        private static double nextPoll;
        private static double nextHeartbeat;
        static AarisAIWorldBridge() { EditorApplication.update += Poll; }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 0.7;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying) return;
            PublishHeartbeat();
            string input = Path.Combine(State, "ai-command.json");
            if (!File.Exists(input)) return;
            Intent request = null;
            var reply = new Result { ok = false, message = "Unknown failure" };
            try
            {
                // Delete first to avoid accidentally applying an edit twice after domain reload.
                request = JsonUtility.FromJson<Intent>(File.ReadAllText(input));
                File.Delete(input);
                if (request == null) throw new ArgumentException("Missing JSON intent");
                reply.nonce = request.nonce;
                reply.op = request.op;
                reply.target = request.target;
                Validate(request);
                if (SceneManager.GetActiveScene().path != AarisExplorerBuild.ScenePath)
                    EditorSceneManager.OpenScene(AarisExplorerBuild.ScenePath, OpenSceneMode.Single);
                reply.scene = SceneManager.GetActiveScene().path;
                Execute(request, reply);
                reply.ok = true;
                reply.message = "Unity Editor completed " + request.op;
            }
            catch (Exception error)
            {
                reply.message = error.GetType().Name + ": " + error.Message;
                Debug.LogError("[Aaris AI world] " + error);
            }
            finally
            {
                try
                {
                    Directory.CreateDirectory(State);
                    string tmp = Path.Combine(State, "ai-result.tmp");
                    File.WriteAllText(tmp, JsonUtility.ToJson(reply, true));
                    string output = Path.Combine(State, "ai-result.json");
                    if (File.Exists(output)) File.Delete(output);
                    File.Move(tmp, output);
                }
                catch (Exception error) { Debug.LogError("[Aaris AI world status] " + error); }
            }
        }

        private static void PublishHeartbeat()
        {
            if (EditorApplication.timeSinceStartup < nextHeartbeat) return;
            nextHeartbeat = EditorApplication.timeSinceStartup + 10;
            try
            {
                Directory.CreateDirectory(State);
                var beat = new Heartbeat {
                    unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    unityVersion = Application.unityVersion,
                    scene = SceneManager.GetActiveScene().path,
                    graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                    nativeSceneOpen = SceneManager.GetActiveScene().path == AarisExplorerBuild.ScenePath
                };
                var tmp = Path.Combine(State, "editor-heartbeat.tmp");
                File.WriteAllText(tmp, JsonUtility.ToJson(beat));
                var path = Path.Combine(State, "editor-heartbeat.json");
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { Debug.LogWarning("[Aaris AI heartbeat] " + e.GetType().Name); }
        }

        private static bool Finite(float n) { return !float.IsNaN(n) && !float.IsInfinity(n); }
        private static void Validate(Intent r)
        {
            string[] valid = { "find", "nearby", "inspect", "blueprint", "photo",
                "move", "rotate", "scale", "tree_nearby", "tree_inspect", "tree_blueprint", "tree_photo", "tree_move", "tree_scale" };
            if (r.nonce <= 0 || !valid.Contains(r.op)) throw new ArgumentException("Unknown op or nonce");
            if (!Finite(r.x) || !Finite(r.y) || !Finite(r.z) ||
                !Finite(r.width) || !Finite(r.height) || !Finite(r.radius))
                throw new ArgumentException("Non-finite numbers are forbidden");
            if (Mathf.Abs(r.x) > 2000 || Mathf.Abs(r.y) > 2000 || Mathf.Abs(r.z) > 2000)
                throw new ArgumentException("Coordinate or delta outside safe world limits");
            if (r.samples < 0 || r.samples > 2048 || r.radius < 0 || r.radius > 250)
                throw new ArgumentException("Sample/radius outside allowed range");
            if ((r.target ?? "").Length > 200 || (r.terrain ?? "").Length > 200)
                throw new ArgumentException("Target too long");
            if (r.op.StartsWith("tree_") && (r.index < 0 || string.IsNullOrWhiteSpace(r.terrain)))
                throw new ArgumentException("Terrain name and non-negative tree index required");
            if (new[] { "scale", "tree_scale" }.Contains(r.op))
            {
                if (r.op == "scale" && (r.x < 0.05f || r.y < 0.05f || r.z < 0.05f ||
                                         r.x > 10 || r.y > 10 || r.z > 10))
                    throw new ArgumentException("Scale factors must be between 0.05 and 10");
                if (r.op == "tree_scale" && (r.width < 0.1f || r.height < 0.1f ||
                                               r.width > 10 || r.height > 10))
                    throw new ArgumentException("Tree width/height factors must be between 0.1 and 10");
            }
        }

        private static string PathOf(Transform t)
        {
            string name = t.name;
            for (Transform parent = t.parent; parent != null; parent = parent.parent)
                name = parent.name + "/" + name;
            return name;
        }
        private static Transform[] All()
        {
            return SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true)).ToArray();
        }
        private static Transform Target(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Missing object path/name");
            Transform[] hits = All().Where(t => (query.Contains("/") ?
                PathOf(t) == query : t.name == query)).Take(3).ToArray();
            if (hits.Length == 0) throw new ArgumentException("Object not found: " + query);
            if (hits.Length > 1) throw new ArgumentException("Ambiguous name; use full hierarchy path: " + query);
            return hits[0];
        }

        private static Bounds BoundsOf(Transform target)
        {
            bool found = false;
            Bounds b = new Bounds(target.position, Vector3.zero);
            foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(true))
            {
                if (!found) { b = renderer.bounds; found = true; }
                else b.Encapsulate(renderer.bounds);
            }
            if (!found)
                foreach (Collider collider in target.GetComponentsInChildren<Collider>(true))
                {
                    if (!found) { b = collider.bounds; found = true; }
                    else b.Encapsulate(collider.bounds);
                }
            if (!found) b = new Bounds(target.position, Vector3.one * 0.1f);
            return b;
        }

        private static void Fill(Transform t, Result reply)
        {
            reply.path = PathOf(t);
            reply.position = t.position;
            reply.rotation = t.eulerAngles;
            reply.localScale = t.localScale;
            var b = BoundsOf(t);
            reply.bounds = new Size { center = b.center, size = b.size, extents = b.extents };
            reply.materials = t.GetComponentsInChildren<Renderer>(true)
                .SelectMany(r => r.sharedMaterials).Where(m => m != null)
                .Select(m => m.name).Distinct().Take(100).ToArray();
            reply.parts = SceneParts(t);
        }

        private static Size DescribeBounds(Bounds b)
        {
            return new Size { center = b.center, size = b.size, extents = b.extents };
        }

        private static Part[] SceneParts(Transform root)
        {
            return root.GetComponentsInChildren<Renderer>(true).Take(128).Select(renderer =>
            {
                var filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh :
                    (renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : null);
                return new Part {
                    path = PathOf(renderer.transform), rendererType = renderer.GetType().Name,
                    bounds = DescribeBounds(renderer.bounds),
                    meshVertices = mesh == null ? 0 : mesh.vertexCount,
                    meshReadable = mesh != null && mesh.isReadable,
                    materials = renderer.sharedMaterials.Where(m => m != null)
                        .Select(m => m.name).Take(16).ToArray()
                };
            }).ToArray();
        }

        private static Bounds TransformBounds(Bounds local, Matrix4x4 matrix)
        {
            bool first = true;
            Bounds b = new Bounds(Vector3.zero, Vector3.zero);
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                var corner = matrix.MultiplyPoint3x4(local.center +
                    Vector3.Scale(local.extents, new Vector3(x, y, z)));
                if (first) { b = new Bounds(corner, Vector3.zero); first = false; }
                else b.Encapsulate(corner);
            }
            return b;
        }

        private static Part[] TreeParts(Transform root, Matrix4x4 worldOverride)
        {
            return root.GetComponentsInChildren<MeshFilter>(true).Take(128)
                .Where(filter => filter.sharedMesh != null).Select(filter =>
                {
                    Mesh mesh = filter.sharedMesh;
                    var renderer = filter.GetComponent<Renderer>();
                    Bounds b = TransformBounds(mesh.bounds,
                        worldOverride * filter.transform.localToWorldMatrix);
                    return new Part {
                        path = PathOf(filter.transform), rendererType = "MeshFilter",
                        bounds = DescribeBounds(b),
                        meshVertices = mesh.vertexCount, meshReadable = mesh.isReadable,
                        materials = renderer == null ? new string[0] :
                            renderer.sharedMaterials.Where(m => m != null)
                                .Select(m => m.name).Take(16).ToArray()
                    };
                }).ToArray();
        }

        private static void Execute(Intent r, Result reply)
        {
            if (r.op == "find" || r.op == "nearby")
            {
                IEnumerable<Transform> entries = All().Where(t => !t.name.StartsWith("__Aaris"));
                Vector3 anchor = new Vector3(r.x, r.y, r.z);
                if (r.op == "find")
                {
                    if (string.IsNullOrWhiteSpace(r.target)) throw new ArgumentException("Search term needed");
                    entries = entries.Where(t => t.name.IndexOf(r.target, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                else entries = entries.Where(t => Vector3.Distance(t.position, anchor) <= r.radius);
                var sorted = entries.Select(t => new Item { path = PathOf(t),
                    position = t.position, distance = Vector3.Distance(t.position, anchor) })
                    .OrderBy(t => t.distance).ToArray();
                reply.matches = sorted.Length;
                reply.objects = sorted.Take(100).ToArray();
                return;
            }

            if (r.op.StartsWith("tree_")) { ExecuteTree(r, reply); return; }
            Transform tform = Target(r.target);
            if (r.op == "move" || r.op == "rotate" || r.op == "scale")
            {
                Undo.RecordObject(tform, "Aaris AI " + r.op);
                Vector3 delta = new Vector3(r.x, r.y, r.z);
                if (r.op == "move") tform.position += delta;
                if (r.op == "rotate") tform.Rotate(delta, Space.World);
                if (r.op == "scale")
                    tform.localScale = Vector3.Scale(tform.localScale, delta);
                Vector3 p = tform.position, s = tform.localScale;
                if (Mathf.Abs(p.x) > 2000 || Mathf.Abs(p.z) > 2000 ||
                    s.x < 0.001f || s.y < 0.001f || s.z < 0.001f ||
                    s.x > 100 || s.y > 100 || s.z > 100)
                {
                    Undo.PerformUndo();
                    throw new ArgumentException("Resulting transform exceeds world/scale limits");
                }
                EditorSceneManager.MarkSceneDirty(tform.gameObject.scene);
                if (!EditorSceneManager.SaveScene(tform.gameObject.scene))
                    throw new IOException("Unity scene save failed");
            }
            Fill(tform, reply);
            if (r.op == "blueprint")
            {
                var cloud = new List<Point>();
                SampleMeshes(tform, Matrix4x4.identity, r.samples == 0 ? 512 : r.samples, cloud, reply);
                if (cloud.Count == 0)
                    throw new InvalidOperationException("No readable mesh vertices; cannot generate PLY blueprint");
                reply.points = cloud.ToArray();
                reply.sampledVertices = cloud.Count;
                reply.blueprint = "ai-blueprint.ply";
                WritePly(Path.Combine(State, reply.blueprint), cloud);
            }
            if (r.op == "photo")
            {
                reply.photo = "ai-photo.png";
                Capture(BoundsOf(tform), Path.Combine(State, reply.photo));
            }
        }

        private static void ExecuteTree(Intent r, Result reply)
        {
            var t = Target(r.terrain).GetComponent<Terrain>();
            if (t == null || t.terrainData == null) throw new ArgumentException("Named object is not a native Terrain");
            var data = t.terrainData;
            var all = data.treeInstances;
            if (r.op == "tree_nearby")
            {
                Vector3 anchor = new Vector3(r.x, r.y, r.z);
                var instances = all.Select((instance, i) =>
                {
                    Vector3 position = t.transform.position + Vector3.Scale(instance.position, data.size);
                    string prototype = instance.prototypeIndex >= 0 &&
                        instance.prototypeIndex < data.treePrototypes.Length &&
                        data.treePrototypes[instance.prototypeIndex].prefab != null
                            ? data.treePrototypes[instance.prototypeIndex].prefab.name : "MISSING";
                    return new Item {
                        path = PathOf(t.transform) + "/TreeInstance[" + i + "]",
                        index = i, position = position, prototype = prototype,
                        distance = Vector3.Distance(anchor, position),
                        widthScale = instance.widthScale, heightScale = instance.heightScale
                    };
                }).Where(item => item.distance <= r.radius).OrderBy(item => item.distance).ToArray();
                reply.matches = instances.Length;
                reply.objects = instances.Take(100).ToArray();
                return;
            }
            if (r.index >= all.Length) throw new ArgumentException("Tree index out of range: " + all.Length);
            var tree = all[r.index];
            reply.path = PathOf(t.transform) + "/TreeInstance[" + r.index + "]";
            reply.index = r.index;
            if (r.op == "tree_move" || r.op == "tree_scale")
            {
                Undo.RecordObject(data, "Aaris AI edit TerrainData tree");
                if (r.op == "tree_move")
                {
                    Vector3 n = tree.position;
                    n.x += r.x / data.size.x;
                    n.z += r.z / data.size.z;
                    if (n.x < 0 || n.x > 1 || n.z < 0 || n.z > 1)
                        throw new ArgumentException("Tree movement exceeds this TerrainData tile");
                    n.y = data.GetInterpolatedHeight(n.x, n.z) / data.size.y;
                    tree.position = n;
                }
                else
                {
                    tree.widthScale *= r.width;
                    tree.heightScale *= r.height;
                    if (tree.widthScale < 0.05f || tree.widthScale > 20 ||
                        tree.heightScale < 0.05f || tree.heightScale > 20)
                        throw new ArgumentException("Resulting tree scale outside limits");
                }
                all[r.index] = tree;
                data.SetTreeInstances(all, true);
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
            }
            reply.position = t.transform.position + Vector3.Scale(tree.position, data.size);
            reply.treeWidthScale = tree.widthScale;
            reply.treeHeightScale = tree.heightScale;
            if (tree.prototypeIndex < 0 || tree.prototypeIndex >= data.treePrototypes.Length)
                throw new InvalidOperationException("Invalid native tree prototype");
            var prefab = data.treePrototypes[tree.prototypeIndex].prefab;
            reply.treePrototype = prefab == null ? "MISSING" : prefab.name;
            if (prefab == null) throw new InvalidOperationException("Native Terrain tree prefab missing");
            var parent = Matrix4x4.TRS(reply.position,
                Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0),
                new Vector3(tree.widthScale, tree.heightScale, tree.widthScale));
            var overrideMatrix = parent * prefab.transform.worldToLocalMatrix;
            // Dimensions are accurate world-aligned mesh bounds (metres), even
            // when the imported mesh is marked non-readable and has no PLY data.
            Bounds measured = MeasureMeshBounds(prefab.transform, overrideMatrix, reply.position);
            reply.bounds = new Size {
                center = measured.center, extents = measured.extents, size = measured.size
            };
            reply.rotation = new Vector3(0, tree.rotation * Mathf.Rad2Deg, 0);
            reply.localScale = new Vector3(tree.widthScale, tree.heightScale, tree.widthScale);
            reply.materials = prefab.GetComponentsInChildren<Renderer>(true)
                .SelectMany(renderer => renderer.sharedMaterials).Where(material => material != null)
                .Select(material => material.name).Distinct().Take(100).ToArray();
            reply.parts = TreeParts(prefab.transform, overrideMatrix);
            if (r.op == "tree_photo")
            {
                reply.photo = "ai-photo.png";
                Capture(measured, Path.Combine(State, reply.photo));
            }
            if (r.op == "tree_blueprint")
            {
                var cloud = new List<Point>();
                SampleMeshes(prefab.transform, overrideMatrix,
                    r.samples == 0 ? 512 : r.samples, cloud, reply);
                if (cloud.Count == 0)
                    throw new InvalidOperationException("Tree has no readable mesh vertices for PLY");
                reply.points = cloud.ToArray();
                reply.sampledVertices = cloud.Count;
                reply.blueprint = "ai-blueprint.ply";
                WritePly(Path.Combine(State, reply.blueprint), cloud);
            }
        }

        // Outer surface AABB from all child mesh subparts; includes trunk and leaves.
        // This does not substitute for physical collision geometry or a watertight mesh.
        private static Bounds MeasureMeshBounds(Transform root, Matrix4x4 worldOverride, Vector3 origin)
        {
            bool hasMesh = false;
            Bounds combined = new Bounds(origin, Vector3.zero);
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true).Take(128))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                Bounds local = TransformBounds(mesh.bounds,
                    worldOverride * filter.transform.localToWorldMatrix);
                if (!hasMesh) { combined = local; hasMesh = true; }
                else combined.Encapsulate(local);
            }
            if (!hasMesh)
                throw new InvalidOperationException("Tree prototype lacks MeshFilter geometry");
            return combined;
        }

        private static void SampleMeshes(Transform root, Matrix4x4 worldOverride,
            int limit, List<Point> cloud, Result reply)
        {
            var meshes = root.GetComponentsInChildren<MeshFilter>(true);
            int perMesh = Mathf.Max(8, limit / Mathf.Max(1, meshes.Length));
            foreach (MeshFilter filter in meshes.Take(64))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                reply.meshVertices += mesh.vertexCount;
                try
                {
                    var vertices = mesh.vertices;
                    var matrix = worldOverride * filter.transform.localToWorldMatrix;
                    int step = Mathf.Max(1, Mathf.CeilToInt((float)vertices.Length / perMesh));
                    for (int i = 0; i < vertices.Length && cloud.Count < limit; i += step)
                        cloud.Add(new Point(matrix.MultiplyPoint3x4(vertices[i])));
                }
                catch (UnityException)
                {
                    // Imported meshes frequently have Read/Write disabled. Do not
                    // claim exact surface vertices; provide labelled coarse AABB
                    // corner samples as a useful measurable fallback instead.
                    var b = mesh.bounds;
                    var matrix = worldOverride * filter.transform.localToWorldMatrix;
                    for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        if (cloud.Count >= limit) break;
                        var local = b.center + Vector3.Scale(b.extents, new Vector3(x, y, z));
                        cloud.Add(new Point(matrix.MultiplyPoint3x4(local)));
                        reply.boundsFallbackPoints++;
                    }
                }
                if (cloud.Count >= limit) break;
            }
            reply.blueprintSampling = reply.boundsFallbackPoints > 0
                ? "mixed_mesh_vertices_and_approximate_bounds_corners"
                : "sampled_mesh_surface_vertices";
        }
        private static void WritePly(string path, List<Point> points)
        {
            Directory.CreateDirectory(State);
            using (var stream = new StreamWriter(path))
            {
                stream.WriteLine("ply\nformat ascii 1.0\ncomment Aaris world-space sample points in metres; inspect JSON blueprintSampling for bounds fallback");
                stream.WriteLine("element vertex " + points.Count);
                stream.WriteLine("property float x\nproperty float y\nproperty float z\nend_header");
                foreach (Point p in points)
                    stream.WriteLine(p.x.ToString("R", CultureInfo.InvariantCulture) + " " +
                        p.y.ToString("R", CultureInfo.InvariantCulture) + " " +
                        p.z.ToString("R", CultureInfo.InvariantCulture));
            }
        }
        private static void Capture(Bounds b, string path)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException("Real graphics device required; no -nographics");
            var go = new GameObject("__AarisAIObjectCamera");
            go.hideFlags = HideFlags.HideAndDontSave;
            var camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.fieldOfView = 60;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 6000;
            float distance = Mathf.Max(3f, b.size.magnitude * 1.6f);
            camera.transform.position = b.center + new Vector3(distance * 0.6f, distance * 0.35f, -distance);
            camera.transform.LookAt(b.center);
            RenderTexture target = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                pixels.Apply(false);
                Color32[] colors = pixels.GetPixels32();
                int lo = 255, hi = 0;
                for (int i = 0; i < colors.Length; i += 250)
                {
                    Color32 c = colors[i];
                    int luma = (c.r * 3 + c.g * 6 + c.b) / 10;
                    lo = Mathf.Min(lo, luma);
                    hi = Mathf.Max(hi, luma);
                }
                if (hi - lo < 9) throw new InvalidOperationException("Nearly uniform blank/black screenshot rejected");
                byte[] png = pixels.EncodeToPNG();
                if (png.Length < 6000) throw new InvalidOperationException("Suspiciously small screenshot");
                Directory.CreateDirectory(State);
                File.WriteAllBytes(path, png);
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(pixels);
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
