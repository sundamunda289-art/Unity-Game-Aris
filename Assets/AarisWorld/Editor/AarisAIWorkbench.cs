using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview.Editor
{
    // One bounded local IPC surface for AI eyes (scan/inspect/blueprint/photo)
    // and hands (relative move/resize/native tree scale). NO network or shell.
    // The owner-authenticated GitHub relay writes the command; Unity executes it.
    [InitializeOnLoad]
    public static class AarisAIWorkbench
    {
        [Serializable] private sealed class Intent
        {
            public string op, target;
            public long nonce;
            public float x, y, z, radius, width, height, depth;
            public int index;
        }
        [Serializable] private sealed class Item
        {
            public string name, path, kind, details;
            public Vector3 position, center, size;
        }
        [Serializable] private sealed class Answer
        {
            public bool ok, changed;
            public long nonce;
            public string op, target, utc, unityVersion, scene, error, note, photo, blueprint;
            public Item[] objects;
        }

        private static readonly string Root = Path.Combine(Directory.GetCurrentDirectory(), "ci-live-control");
        private static double nextPoll;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static AarisAIWorkbench() { EditorApplication.update += Poll; }

        [MenuItem("Aaris/AI/Process queued workbench command")]
        public static void ExecuteBatch() { ProcessCommand(); }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 0.5;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying)
                return;
            if (File.Exists(Path.Combine(Root, "ai-command.json"))) ProcessCommand();
        }

        private static void ProcessCommand()
        {
            Directory.CreateDirectory(Root);
            string file = Path.Combine(Root, "ai-command.json");
            if (!File.Exists(file)) return;
            Intent command = null;
            var output = new Answer { utc = DateTime.UtcNow.ToString("O"),
                                      unityVersion = Application.unityVersion };
            try
            {
                string raw = File.ReadAllText(file);
                File.Delete(file); // Consume once; retry only with a NEW nonce.
                if (raw.Length > 2048) throw new ArgumentException("Command too large");
                command = JsonUtility.FromJson<Intent>(raw);
                if (command == null || command.nonce <= 0) throw new ArgumentException("Missing nonce");
                output.nonce = command.nonce;
                output.op = command.op;
                output.target = command.target;
                if (!new[] { "scan", "inspect", "blueprint", "photo", "move", "resize", "tree" }
                    .Contains(command.op)) throw new ArgumentException("Unsupported operation");
                Scene scene = SceneManager.GetActiveScene();
                if (scene.path != AarisExplorerBuild.ScenePath)
                    scene = EditorSceneManager.OpenScene(AarisExplorerBuild.ScenePath, OpenSceneMode.Single);
                output.scene = scene.path;
                switch (command.op)
                {
                    case "scan":
                        RequireRange(command.x, -1000, 1000, "x");
                        RequireRange(command.z, -1000, 1000, "z");
                        RequireRange(command.radius, 1, 400, "radius");
                        output.objects = Scan(scene, command.x, command.z, command.radius).ToArray();
                        output.note = "Scene objects and native Terrain trees; positions and dimensions are in metres.";
                        break;
                    case "tree":
                        RequireRange(command.width, 0.05f, 10, "tree width scale");
                        RequireRange(command.height, 0.05f, 10, "tree height scale");
                        EditTree(command);
                        output.changed = true;
                        output.objects = new[] { FindTree(command.target, command.index) };
                        output.note = "Edited native TerrainData tree instance and saved assets. Review LFS changes before merging.";
                        break;
                    default:
                        if (String.IsNullOrWhiteSpace(command.target) || command.target.Length > 120 ||
                            command.target.Contains(".."))
                            throw new ArgumentException("Invalid target");
                        if (command.target.StartsWith("terrain-tree:", StringComparison.Ordinal))
                        {
                            string[] parts = command.target.Split(':');
                            if (parts.Length != 3 || !Int32.TryParse(parts[2], out int treeIndex))
                                throw new ArgumentException("Expected terrain-tree:TerrainName:index");
                            if (command.op == "move")
                            {
                                if (Mathf.Abs(command.y) > 0.001f)
                                    throw new ArgumentException("Terrain tree Y is derived from terrain height");
                                MoveTree(parts[1], treeIndex, command.x, command.z);
                                output.changed = true;
                            }
                            if (command.op == "resize")
                                throw new ArgumentException("Use tree op to edit native tree widthScale and heightScale");
                            var tree = FindTree(parts[1], treeIndex);
                            output.objects = new[] { tree };
                            if (command.op == "blueprint")
                            {
                                output.blueprint = "ai-blueprint-" + command.nonce + ".ply";
                                WriteTreePoints(parts[1], treeIndex, Path.Combine(Root, output.blueprint));
                            }
                            if (command.op == "photo")
                            {
                                output.photo = "ai-photo-" + command.nonce + ".png";
                                Capture(tree.center, tree.size, Path.Combine(Root, output.photo));
                            }
                        }
                        else
                        {
                            Transform transform = FindUnique(scene, command.target);
                            if (command.op == "move" || command.op == "resize")
                            {
                                EditSceneObject(transform, command);
                                output.changed = true;
                            }
                            Item item = Describe(transform);
                            output.objects = new[] { item };
                            if (command.op == "blueprint")
                            {
                                output.blueprint = "ai-blueprint-" + command.nonce + ".ply";
                                WritePoints(transform, Path.Combine(Root, output.blueprint));
                            }
                            if (command.op == "photo")
                            {
                                output.photo = "ai-photo-" + command.nonce + ".png";
                                Capture(item.center, item.size, Path.Combine(Root, output.photo));
                            }
                        }
                        output.note = output.changed
                            ? "Scene/TerrainData saved only in this runner working tree; changes require a reviewed PR to persist."
                            : "Dimensions are renderer/collider AABB, in world metres. Point cloud is sampled, not a watertight mesh.";
                        break;
                }
                output.ok = true;
            }
            catch (Exception ex)
            {
                output.error = ex.GetType().Name + ": " + ex.Message;
                Debug.LogError("[Aaris AI] " + ex);
            }
            finally
            {
                // Never include credentials, arbitrary paths or raw logs in published responses.
                File.WriteAllText(Path.Combine(Root, "ai-status.json"), JsonUtility.ToJson(output, true));
                if (output.nonce > 0)
                    File.WriteAllText(Path.Combine(Root, "ai-report-" + output.nonce + ".json"),
                        JsonUtility.ToJson(output, true));
            }
        }

        private static void RequireRange(float x, float lo, float hi, string label)
        {
            if (Single.IsNaN(x) || Single.IsInfinity(x) || x < lo || x > hi)
                throw new ArgumentException("Out-of-range " + label);
        }

        private static IEnumerable<Transform> AllTransforms(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    yield return child;
        }

        private static string Hierarchy(Transform obj)
        {
            var parts = new List<string>();
            for (Transform current = obj; current != null; current = current.parent)
                parts.Add(current.name);
            parts.Reverse();
            return String.Join("/", parts);
        }

        private static Transform FindUnique(Scene scene, string name)
        {
            var found = AllTransforms(scene).Where(t =>
                name.IndexOf('/') >= 0 ? Hierarchy(t) == name : t.name == name).Take(3).ToArray();
            if (found.Length == 0) throw new ArgumentException("Object not found: " + name);
            if (found.Length != 1) throw new ArgumentException("Name ambiguous; use exact hierarchy path: " + name);
            return found[0];
        }

        private static Bounds WorldBounds(Transform t)
        {
            Renderer[] renderers = t.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            Bounds bounds = new Bounds(t.position, Vector3.zero);
            foreach (Renderer renderer in renderers)
            {
                if (!renderer.enabled) continue;
                if (!any) { bounds = renderer.bounds; any = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!any)
                foreach (Collider collider in t.GetComponentsInChildren<Collider>(true))
                {
                    if (!collider.enabled) continue;
                    if (!any) { bounds = collider.bounds; any = true; }
                    else bounds.Encapsulate(collider.bounds);
                }
            return bounds;
        }

        private static Item Describe(Transform t)
        {
            Bounds b = WorldBounds(t);
            string lower = t.name.ToLowerInvariant();
            string kind = lower.Contains("tree") || lower.Contains("pine") || lower.Contains("cypress") ? "tree"
                : lower.Contains("wall") ? "wall" : lower.Contains("rock") ? "rock"
                : lower.Contains("house") || lower.Contains("building") ? "building" : "scene-object";
            return new Item { name = t.name, path = Hierarchy(t), kind = kind,
                position = t.position, center = b.center, size = b.size,
                details = "scale=" + t.localScale.ToString("F3") +
                          "; meshes=" + t.GetComponentsInChildren<MeshFilter>(true).Length +
                          "; renderers=" + t.GetComponentsInChildren<Renderer>(true).Length };
        }

        private static IEnumerable<Item> Scan(Scene scene, float x, float z, float radius)
        {
            Vector2 center = new Vector2(x, z);
            var objects = AllTransforms(scene)
                .Where(t => t.GetComponent<Renderer>() != null || t.GetComponent<Collider>() != null)
                .Where(t => Vector2.Distance(center, new Vector2(t.position.x, t.position.z)) <= radius)
                .OrderBy(t => (t.position.x - x) * (t.position.x - x) +
                              (t.position.z - z) * (t.position.z - z))
                .Take(24).Select(Describe).ToList();
            foreach (Terrain terrain in UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None))
            {
                TreeInstance[] trees = terrain.terrainData.treeInstances;
                Vector3 sz = terrain.terrainData.size, basePos = terrain.transform.position;
                var nearby = new SortedSet<Tuple<float, int>>();
                for (int i = 0; i < trees.Length; i++)
                {
                    float tx = basePos.x + trees[i].position.x * sz.x;
                    float tz = basePos.z + trees[i].position.z * sz.z;
                    float d = (tx - x) * (tx - x) + (tz - z) * (tz - z);
                    if (d <= radius * radius)
                    {
                        nearby.Add(Tuple.Create(d, i));
                        if (nearby.Count > 12) nearby.Remove(nearby.Max);
                    }
                }
                foreach (var candidate in nearby)
                    objects.Add(FindTree(terrain.name, candidate.Item2));
            }
            return objects.OrderBy(o => (o.position.x - x) * (o.position.x - x) +
                                       (o.position.z - z) * (o.position.z - z)).Take(30);
        }

        private static Terrain TerrainByName(string name)
        {
            var match = UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None)
                .Where(t => t.name == name).ToArray();
            if (match.Length != 1 || match[0].terrainData == null)
                throw new ArgumentException("Expected one native Terrain named: " + name);
            return match[0];
        }

        private static Item FindTree(string terrainName, int index)
        {
            Terrain t = TerrainByName(terrainName);
            var data = t.terrainData;
            TreeInstance[] instances = data.treeInstances;
            if (index < 0 || index >= instances.Length) throw new ArgumentException("Invalid tree index");
            TreeInstance a = instances[index];
            Vector3 world = t.transform.position + Vector3.Scale(a.position, data.size);
            string proto = a.prototypeIndex >= 0 && a.prototypeIndex < data.treePrototypes.Length &&
                data.treePrototypes[a.prototypeIndex].prefab != null
                ? data.treePrototypes[a.prototypeIndex].prefab.name : "unknown";
            var points = TreeSamplePoints(t, index);
            Bounds b = new Bounds(world, new Vector3(a.widthScale * 2, a.heightScale * 6, a.widthScale * 2));
            if (points.Count > 0)
            {
                b = new Bounds(points[0], Vector3.zero);
                foreach (Vector3 point in points) b.Encapsulate(point);
            }
            return new Item { name = proto + " #" + index,
                path = "terrain-tree:" + terrainName + ":" + index, kind = "native-terrain-tree",
                position = world, center = b.center, size = b.size,
                details = "prototype=" + proto + "; prototypeIndex=" + a.prototypeIndex +
                          "; widthScale=" + a.widthScale.ToString("F3", Inv) +
                          "; heightScale=" + a.heightScale.ToString("F3", Inv) +
                          "; boundsFidelity=" + (points.Count > 0 ? "sampled-prototype-mesh" : "estimated-proxy") +
                          "; widthScale/heightScale are relative multipliers" };
        }

        private static void EditTree(Intent c)
        {
            Terrain t = TerrainByName(c.target);
            TreeInstance[] trees = t.terrainData.treeInstances;
            if (c.index < 0 || c.index >= trees.Length) throw new ArgumentException("Invalid tree index");
            var inst = trees[c.index];
            inst.widthScale = c.width;
            inst.heightScale = c.height;
            trees[c.index] = inst;
            Undo.RecordObject(t.terrainData, "Aaris AI native tree scale");
            t.terrainData.SetTreeInstances(trees, true);
            EditorUtility.SetDirty(t.terrainData);
            AssetDatabase.SaveAssets();
        }

        private static void MoveTree(string terrainName, int index, float dx, float dz)
        {
            RequireRange(dx, -100, 100, "dx"); RequireRange(dz, -100, 100, "dz");
            Terrain t = TerrainByName(terrainName);
            TreeInstance[] trees = t.terrainData.treeInstances;
            if (index < 0 || index >= trees.Length) throw new ArgumentException("Invalid tree index");
            var inst = trees[index];
            Vector3 sz = t.terrainData.size;
            inst.position += new Vector3(dx / sz.x, 0, dz / sz.z);
            if (inst.position.x < 0 || inst.position.x > 1 || inst.position.z < 0 || inst.position.z > 1)
                throw new ArgumentException("Tree would leave its terrain tile");
            trees[index] = inst;
            Undo.RecordObject(t.terrainData, "Aaris AI native tree move");
            t.terrainData.SetTreeInstances(trees, true);
            EditorUtility.SetDirty(t.terrainData);
            AssetDatabase.SaveAssets();
        }

        private static void EditSceneObject(Transform transform, Intent c)
        {
            if (transform.GetComponent<Terrain>() != null || transform.GetComponent<Camera>() != null ||
                transform.name.Contains("Player") || transform.name.StartsWith("VehicleSpawnPoint_") ||
                transform.GetComponentsInChildren<Transform>(true).Length > 500)
                throw new ArgumentException("Protected gameplay/terrain object; edits refused");
            Vector3 oldPosition = transform.position, oldScale = transform.localScale;
            Undo.RegisterCompleteObjectUndo(transform, "Aaris AI " + c.op);
            Bounds before = WorldBounds(transform);
            if (c.op == "move")
            {
                RequireRange(c.x, -100, 100, "dx"); RequireRange(c.y, -100, 100, "dy");
                RequireRange(c.z, -100, 100, "dz");
                transform.position += new Vector3(c.x, c.y, c.z);
            }
            else
            {
                RequireRange(c.width, 0.05f, 200, "width");
                RequireRange(c.height, 0.05f, 200, "height");
                RequireRange(c.depth, 0.05f, 200, "depth");
                if (before.size.x < 0.001f || before.size.y < 0.001f || before.size.z < 0.001f)
                    throw new ArgumentException("Cannot resize object without three-dimensional renderer/collider bounds");
                transform.localScale = Vector3.Scale(oldScale,
                    new Vector3(c.width / before.size.x, c.height / before.size.y, c.depth / before.size.z));
            }
            try
            {
                Physics.SyncTransforms();
                Bounds after = WorldBounds(transform);
                if (after.min.x < -1000 || after.max.x > 1000 ||
                    after.min.z < -1000 || after.max.z > 1000)
                    throw new InvalidOperationException("Edited object would leave 2km world bounds");
                var all = transform.gameObject.scene.GetRootGameObjects()
                    .SelectMany(g => g.GetComponentsInChildren<Collider>(true));
                foreach (Collider other in all)
                {
                    if (!other.enabled || other.isTrigger || other is TerrainCollider ||
                        other.transform == transform || other.transform.IsChildOf(transform) ||
                        transform.IsChildOf(other.transform))
                        continue;
                    if (after.Intersects(other.bounds) && !before.Intersects(other.bounds))
                        throw new InvalidOperationException("New collision with " + Hierarchy(other.transform));
                }
                foreach (Transform marker in AllTransforms(transform.gameObject.scene))
                    if (marker.name.StartsWith("VehicleSpawnPoint_", StringComparison.Ordinal) &&
                        !marker.IsChildOf(transform) && after.Contains(marker.position) &&
                        !before.Contains(marker.position))
                        throw new InvalidOperationException("Would cover vehicle spawn " + marker.name);
            }
            catch
            {
                transform.position = oldPosition; transform.localScale = oldScale;
                Physics.SyncTransforms();
                throw;
            }
            // Persist only after all bounds/collision checks succeed.
            PrefabUtility.RecordPrefabInstancePropertyModifications(transform);
            EditorSceneManager.MarkSceneDirty(transform.gameObject.scene);
            EditorSceneManager.SaveScene(transform.gameObject.scene);
        }

        private static List<Vector3> CollectPoints(Transform root, int limit)
        {
            var points = new List<Vector3>(Math.Min(limit, 4096));
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                Vector3[] vertices = mesh.vertices;
                int step = Math.Max(1, (int)Math.Ceiling(vertices.Length / 600.0));
                for (int i = 0; i < vertices.Length && points.Count < limit; i += step)
                    points.Add(filter.transform.TransformPoint(vertices[i]));
                if (points.Count >= limit) break;
            }
            return points;
        }

        private static void WritePoints(Transform target, string path)
        {
            var points = CollectPoints(target, 4096);
            if (points.Count == 0)
            {
                Bounds b = WorldBounds(target);
                foreach (float x in new[] { b.min.x, b.max.x })
                    foreach (float y in new[] { b.min.y, b.max.y })
                        foreach (float z in new[] { b.min.z, b.max.z })
                            points.Add(new Vector3(x,y,z));
            }
            WritePly(points, path);
        }

        private static List<Vector3> TreeSamplePoints(Terrain t, int index)
        {
            var data = t.terrainData;
            TreeInstance[] instances = data.treeInstances;
            if (index < 0 || index >= instances.Length) throw new ArgumentException("Invalid tree index");
            TreeInstance inst = instances[index];
            var result = new List<Vector3>();
            if (inst.prototypeIndex < 0 || inst.prototypeIndex >= data.treePrototypes.Length)
                return result;
            var proto = data.treePrototypes[inst.prototypeIndex].prefab;
            Vector3 world = t.transform.position + Vector3.Scale(inst.position, data.size);
            if (proto == null) return result;
            var model = Matrix4x4.TRS(world,
                Quaternion.Euler(0, inst.rotation * Mathf.Rad2Deg, 0),
                new Vector3(inst.widthScale, inst.heightScale, inst.widthScale));
            foreach (MeshFilter f in proto.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null || !f.sharedMesh.isReadable) continue;
                Matrix4x4 matrix = model * proto.transform.worldToLocalMatrix * f.transform.localToWorldMatrix;
                Vector3[] vertices = f.sharedMesh.vertices;
                int step = Math.Max(1, (int)Math.Ceiling(vertices.Length / 800.0));
                for (int i=0; i<vertices.Length && result.Count<4096; i+=step)
                    result.Add(matrix.MultiplyPoint3x4(vertices[i]));
                if (result.Count >= 4096) break;
            }
            return result;
        }

        private static void WriteTreePoints(string terrainName, int index, string path)
        {
            Terrain t = TerrainByName(terrainName);
            TreeInstance inst = t.terrainData.treeInstances[index];
            var result = TreeSamplePoints(t, index);
            if (result.Count == 0)
            {
                // Explicit scale proxy; NEVER claim detailed canopy geometry without readable mesh vertices.
                Vector3 world = t.transform.position + Vector3.Scale(inst.position, t.terrainData.size);
                for (int i=0; i<24; i++)
                {
                    float angle = i * Mathf.PI * 2 / 24;
                    result.Add(world + new Vector3(Mathf.Cos(angle)*inst.widthScale, inst.heightScale*6,
                                                    Mathf.Sin(angle)*inst.widthScale));
                    result.Add(world + new Vector3(Mathf.Cos(angle)*inst.widthScale*0.25f, 0,
                                                    Mathf.Sin(angle)*inst.widthScale*0.25f));
                }
            }
            WritePly(result, path);
        }

        // Each height/azimuth cell keeps its furthest radial point: sparse outer-layer envelope.
        // This is useful for canopy/trunk silhouette comparisons, not exact manifold topology.
        private static List<Vector3> OuterShell(List<Vector3> points)
        {
            if (points.Count < 32) return points;
            Bounds b = new Bounds(points[0], Vector3.zero);
            foreach (Vector3 point in points) b.Encapsulate(point);
            const int levels = 24, angles = 32;
            var furthest = new float[levels * angles];
            var selected = new Vector3[levels * angles];
            for (int i = 0; i < furthest.Length; i++) furthest[i] = -1;
            foreach (Vector3 v in points)
            {
                int y = Mathf.Clamp(Mathf.FloorToInt((v.y - b.min.y) /
                            Mathf.Max(0.0001f, b.size.y) * levels), 0, levels - 1);
                float angle = Mathf.Atan2(v.z - b.center.z, v.x - b.center.x);
                int az = Mathf.Clamp(Mathf.FloorToInt((angle + Mathf.PI) /
                            (2 * Mathf.PI) * angles), 0, angles - 1);
                float d = (v.x - b.center.x) * (v.x - b.center.x) +
                          (v.z - b.center.z) * (v.z - b.center.z);
                int cell = y * angles + az;
                if (d > furthest[cell]) { furthest[cell] = d; selected[cell] = v; }
            }
            var shell = new List<Vector3>();
            for (int i = 0; i < selected.Length; i++)
                if (furthest[i] >= 0) shell.Add(selected[i]);
            return shell;
        }

        private static void WritePly(List<Vector3> vertices, string path)
        {
            vertices = OuterShell(vertices);
            var b = new StringBuilder("ply\nformat ascii 1.0\n");
            b.Append("comment World-space outer shell sampled by height and azimuth; not a watertight surface\n");
            b.Append("element vertex ").Append(vertices.Count).Append('\n');
            b.Append("property float x\nproperty float y\nproperty float z\nend_header\n");
            foreach (Vector3 v in vertices)
                b.Append(v.x.ToString("R", Inv)).Append(' ')
                 .Append(v.y.ToString("R", Inv)).Append(' ')
                 .Append(v.z.ToString("R", Inv)).Append('\n');
            File.WriteAllText(path, b.ToString());
        }

        private static void Capture(Vector3 center, Vector3 size, string path)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("No Unity graphics device; actual Editor rendering is required");
            var go = new GameObject("__AarisAIPhotoCamera");
            go.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false; camera.clearFlags = CameraClearFlags.Skybox;
            camera.fieldOfView = 60; camera.farClipPlane = 5500;
            float distance = Mathf.Max(5, size.magnitude * 1.65f);
            camera.transform.position = center + new Vector3(0.65f,0.3f,-1).normalized * distance;
            camera.transform.LookAt(center);
            var view = SceneView.lastActiveSceneView;
            if (view != null) { view.LookAtDirect(center, camera.transform.rotation, distance); view.Repaint(); }
            RenderTexture target = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target; camera.Render();
                RenderTexture.active = target;
                tex.ReadPixels(new Rect(0,0,1280,720),0,0); tex.Apply(false);
                byte[] png = tex.EncodeToPNG();
                if (png.Length < 6000) throw new InvalidOperationException("Blank or tiny Unity photo");
                File.WriteAllBytes(path, png);
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
