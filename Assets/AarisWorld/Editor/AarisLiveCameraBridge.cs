using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Aaris.MapReview.Editor
{
    // One Editor-only bridge: camera, semantic inventory, measured exterior point cloud,
    // bounded transform and native Terrain tree edits. No network listeners or code execution.
    [InitializeOnLoad]
    public static class AarisLiveCameraBridge
    {
        [Serializable] private sealed class CameraIntent
        {
            public float x, y, z, yaw, pitch, distance;
            public long nonce;
        }

        [Serializable] private sealed class CameraStatus
        {
            public string state;
            public long nonce;
            public string updatedUtc;
        }

        [Serializable] private sealed class AgentIntent
        {
            public string action, target;
            public float x, y, z, radius;
            public bool apply;
            public long nonce;
        }

        [Serializable] private sealed class MapObject
        {
            public string id, name, kind, fidelity;
            public Vector3 position_m, bounds_center_m, dimensions_m;
        }

        [Serializable] private sealed class SurfaceDot
        {
            public float x, y, z;
            public SurfaceDot(Vector3 point) { x = point.x; y = point.y; z = point.z; }
        }

        [Serializable] private sealed class AgentReport
        {
            public string state, action, source, unity_version, utc, note;
            public long nonce;
            public bool applied, genuine_unity_png;
            public MapObject selected;
            public MapObject[] nearby = new MapObject[0];
            public SurfaceDot[] outer_surface_points_m = new SurfaceDot[0];
            public string[] warnings = new string[0];
            public string photo_file;
            public int native_terrain_tiles, native_terrain_tree_count;
            public Vector3 query_position_m;
            public float terrain_height_m, terrain_slope_degrees;
        }

        private sealed class Selection
        {
            public Transform transform;
            public Terrain terrain;
            public int treeIndex = -1;
            public bool IsTree { get { return terrain != null; } }
        }

        private static readonly string Folder = Path.Combine(Directory.GetCurrentDirectory(), "ci-live-control");
        private static double nextTime;

        static AarisLiveCameraBridge() { EditorApplication.update += Poll; }

        private static void EnsureScene()
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().path != AarisExplorerBuild.ScenePath)
                EditorSceneManager.OpenScene(AarisExplorerBuild.ScenePath, OpenSceneMode.Single);
            if (UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None).Length != 4)
                throw new InvalidOperationException("Expected four imported native Terrain tiles; check Git LFS.");
        }

        private static void AtomicJson(string filename, string json)
        {
            Directory.CreateDirectory(Folder);
            string dest = Path.Combine(Folder, filename);
            string temp = dest + ".write";
            File.WriteAllText(temp, json);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(temp, dest);
        }

        private static void ReportCamera(string state, long nonce)
        {
            AtomicJson("status.json", JsonUtility.ToJson(new CameraStatus {
                state = state, nonce = nonce, updatedUtc = DateTime.UtcNow.ToString("O")
            }, true));
        }

        private static void ReportAgent(AgentReport report)
        {
            report.utc = DateTime.UtcNow.ToString("O");
            report.unity_version = Application.unityVersion;
            report.source = "licensed_unity_editor_scene";
            AtomicJson("agent-report.json", JsonUtility.ToJson(report, true));
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextTime) return;
            nextTime = EditorApplication.timeSinceStartup + 0.6;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying) return;
            string camera = Path.Combine(Folder, "camera.json");
            string agent = Path.Combine(Folder, "agent-request.json");
            if (File.Exists(camera)) HandleCamera(camera);
            if (File.Exists(agent)) HandleAgent(agent);
        }

        private static void HandleCamera(string file)
        {
            CameraIntent intent = null;
            try
            {
                intent = JsonUtility.FromJson<CameraIntent>(File.ReadAllText(file));
                File.Delete(file);
                if (intent == null || !Finite(intent.x) || !Finite(intent.y) ||
                    !Finite(intent.z) || !Finite(intent.yaw) || !Finite(intent.pitch) ||
                    !Finite(intent.distance) || intent.x < -1000 || intent.x > 1000 ||
                    intent.z < -1000 || intent.z > 1000 || intent.y < -500 || intent.y > 4000 ||
                    intent.distance < 2 || intent.distance > 1000 || Mathf.Abs(intent.pitch) > 85)
                    throw new ArgumentException("Invalid camera intent.");
                EnsureScene();
                var point = new Vector3(intent.x, intent.y, intent.z);
                var rotation = Quaternion.Euler(intent.pitch, intent.yaw, 0f);
                FrameView(point, rotation, intent.distance);
                TakePhoto(point, rotation, Path.Combine(Folder, "latest.png"));
                ReportCamera("Camera moved and real Unity PNG saved", intent.nonce);
            }
            catch (Exception error)
            {
                ReportCamera("Camera error: " + error.GetType().Name + ": " + error.Message,
                    intent == null ? 0 : intent.nonce);
                Debug.LogError("[Aaris camera] " + error);
            }
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private static Terrain[] Terrains()
        {
            return UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None);
        }

        private static bool Editable(Transform tr)
        {
            if (tr == null || tr.gameObject.scene.path != AarisExplorerBuild.ScenePath) return false;
            if (tr.GetComponentInParent<Terrain>() != null ||
                tr.GetComponentInParent<AarisThirdPersonPlayer>() != null ||
                tr.GetComponentInParent<Camera>() != null ||
                tr.GetComponentInParent<Light>() != null ||
                tr.name.StartsWith("__Aaris", StringComparison.Ordinal) ||
                tr.name.StartsWith("VehicleSpawnPoint_", StringComparison.Ordinal)) return false;
            return tr.GetComponentsInChildren<Renderer>(true).Length > 0;
        }

        private static string SceneId(Transform tr)
        {
            return "scene:" + GlobalObjectId.GetGlobalObjectIdSlow(tr.gameObject).ToString();
        }

        private static string TreeId(Terrain terrain, int index)
        {
            return "tree:" + terrain.name + ":" + index;
        }

        private static Vector3 TreePosition(Terrain terrain, TreeInstance tree)
        {
            return terrain.transform.position + Vector3.Scale(tree.position, terrain.terrainData.size);
        }

        private static Bounds MeshBounds(GameObject prototype)
        {
            if (prototype == null) throw new InvalidOperationException("Tree prototype is unavailable.");
            Transform root = prototype.transform;
            Bounds b = new Bounds();
            bool found = false;
            foreach (MeshFilter filter in prototype.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                Matrix4x4 toRoot = root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                Bounds mesh = filter.sharedMesh.bounds;
                foreach (Vector3 corner in Corners(mesh))
                {
                    Vector3 vertex = toRoot.MultiplyPoint3x4(corner);
                    if (!found) { b = new Bounds(vertex, Vector3.zero); found = true; }
                    else b.Encapsulate(vertex);
                }
            }
            if (!found) throw new InvalidOperationException("Tree mesh bounds are unavailable.");
            return b;
        }

        private static Vector3[] Corners(Bounds b)
        {
            Vector3 lo = b.min, hi = b.max;
            return new[] {
                new Vector3(lo.x,lo.y,lo.z), new Vector3(lo.x,lo.y,hi.z),
                new Vector3(lo.x,hi.y,lo.z), new Vector3(lo.x,hi.y,hi.z),
                new Vector3(hi.x,lo.y,lo.z), new Vector3(hi.x,lo.y,hi.z),
                new Vector3(hi.x,hi.y,lo.z), new Vector3(hi.x,hi.y,hi.z)
            };
        }

        private static Bounds TreeBounds(Terrain terrain, TreeInstance tree)
        {
            var data = terrain.terrainData;
            var prototype = data.treePrototypes[tree.prototypeIndex];
            Bounds local = MeshBounds(prototype.prefab);
            Vector3 world = TreePosition(terrain, tree);
            Quaternion rotation = Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0);
            Bounds b = new Bounds();
            bool found = false;
            foreach (Vector3 corner in Corners(local))
            {
                Vector3 scaled = new Vector3(corner.x * tree.widthScale,
                    corner.y * tree.heightScale, corner.z * tree.widthScale);
                Vector3 p = world + rotation * scaled;
                if (!found) { b = new Bounds(p, Vector3.zero); found = true; }
                else b.Encapsulate(p);
            }
            return b;
        }

        private static Bounds SceneBounds(Transform tr)
        {
            Renderer[] renderers = tr.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException("Object has no visible mesh bounds.");
            Bounds result = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) result.Encapsulate(renderers[i].bounds);
            return result;
        }

        private static MapObject ToMapObject(Selection selection)
        {
            if (selection.IsTree)
            {
                TreeInstance tree = selection.terrain.terrainData.treeInstances[selection.treeIndex];
                Bounds b;
                string fidelity = "prefab_mesh_bounds_at_tree_instance_scale";
                try { b = TreeBounds(selection.terrain, tree); }
                catch (InvalidOperationException)
                {
                    // Keep the tree present in the inventory; zero dimensions mean UNKNOWN,
                    // not a fabricated estimate of trunk/canopy geometry.
                    b = new Bounds(TreePosition(selection.terrain, tree), Vector3.zero);
                    fidelity = "tree_prototype_geometry_unavailable_dimensions_unknown";
                }
                return new MapObject {
                    id = TreeId(selection.terrain, selection.treeIndex),
                    name = selection.terrain.name + "/tree[" + selection.treeIndex + "]",
                    kind = "native_terrain_tree",
                    fidelity = fidelity,
                    position_m = TreePosition(selection.terrain, tree),
                    bounds_center_m = b.center, dimensions_m = b.size
                };
            }
            Bounds sceneBounds = SceneBounds(selection.transform);
            return new MapObject {
                id = SceneId(selection.transform), name = selection.transform.name,
                kind = "scene_mesh_object", fidelity = "unity_renderer_world_aabb",
                position_m = selection.transform.position,
                bounds_center_m = sceneBounds.center, dimensions_m = sceneBounds.size
            };
        }

        private static Selection FindTarget(string id)
        {
            if (String.IsNullOrEmpty(id) || id.Length > 180)
                throw new ArgumentException("A target ID or exact visible object name is required.");
            if (id.StartsWith("tree:", StringComparison.Ordinal))
            {
                int last = id.LastIndexOf(':');
                if (last <= 5) throw new ArgumentException("Bad native Terrain tree ID.");
                string name = id.Substring(5, last - 5);
                int index;
                if (!int.TryParse(id.Substring(last + 1), out index))
                    throw new ArgumentException("Bad native Terrain tree index.");
                Terrain terrain = Terrains().SingleOrDefault(t => t.name == name);
                if (terrain == null || index < 0 || index >= terrain.terrainData.treeInstanceCount)
                    throw new ArgumentException("Tree ID not found; run scan again.");
                return new Selection { terrain = terrain, treeIndex = index };
            }
            if (id.StartsWith("scene:", StringComparison.Ordinal))
            {
                GlobalObjectId globalId;
                if (!GlobalObjectId.TryParse(id.Substring(6), out globalId))
                    throw new ArgumentException("Bad Unity GlobalObjectId.");
                GameObject go = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId) as GameObject;
                if (go == null || !Editable(go.transform))
                    throw new ArgumentException("Scene object ID missing or not safely editable.");
                return new Selection { transform = go.transform };
            }
            // A name lookup is human-friendly but must be unambiguous.
            // IDs from scan avoid this O(scene-objects) search entirely.
            var matches = UnityEngine.Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(Editable).Where(t => t.name == id).Take(2).ToArray();
            if (matches.Length != 1)
                throw new ArgumentException(matches.Length == 0 ? "Object not found in loaded scene." :
                    "Ambiguous object name; use the scene: ID from scan.");
            return new Selection { transform = matches[0] };
        }

        private static List<SurfaceDot> Outline(Selection selection)
        {
            var cloud = new Dictionary<int, Vector3>();
            Bounds bounds = new Bounds(ToMapObject(selection).bounds_center_m,
                                       ToMapObject(selection).dimensions_m);
            if (bounds.size == Vector3.zero) return new List<SurfaceDot>();
            Vector3 center = bounds.center;
            Action<Vector3> include = p =>
            {
                Vector3 d = p - center;
                if (d.sqrMagnitude < 0.000001f) return;
                float azimuth = Mathf.Atan2(d.z, d.x) / (Mathf.PI * 2) + 0.5f;
                float elevation = Mathf.Atan2(d.y, Mathf.Sqrt(d.x * d.x + d.z * d.z)) / Mathf.PI + 0.5f;
                int a = Mathf.Clamp(Mathf.FloorToInt(azimuth * 24), 0, 23);
                int e = Mathf.Clamp(Mathf.FloorToInt(elevation * 8), 0, 7);
                int bucket = a * 8 + e;
                Vector3 old;
                if (!cloud.TryGetValue(bucket, out old) || (p - center).sqrMagnitude > (old - center).sqrMagnitude)
                    cloud[bucket] = p;
            };

            if (selection.IsTree)
            {
                TreeInstance tree = selection.terrain.terrainData.treeInstances[selection.treeIndex];
                GameObject prefab = selection.terrain.terrainData.treePrototypes[tree.prototypeIndex].prefab;
                Vector3 position = TreePosition(selection.terrain, tree);
                Quaternion rotation = Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0);
                Matrix4x4 parent = prefab.transform.worldToLocalMatrix;
                foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null) continue;
                    Matrix4x4 local = parent * filter.transform.localToWorldMatrix;
                    IncludeMesh(filter.sharedMesh, v => {
                        Vector3 p = local.MultiplyPoint3x4(v);
                        return position + rotation * new Vector3(
                            p.x * tree.widthScale, p.y * tree.heightScale, p.z * tree.widthScale);
                    }, include);
                }
            }
            else
            {
                foreach (MeshFilter filter in selection.transform.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null)
                        IncludeMesh(filter.sharedMesh, filter.transform.TransformPoint, include);
            }
            if (cloud.Count < 8)
                foreach (Vector3 point in Corners(bounds)) include(point);
            return cloud.OrderBy(kv => kv.Key).Select(kv => new SurfaceDot(kv.Value)).ToList();
        }

        private static void IncludeMesh(Mesh mesh, Func<Vector3, Vector3> transform, Action<Vector3> include)
        {
            if (!mesh.isReadable) return;
            try
            {
                Vector3[] vertices = mesh.vertices;
                int stride = Mathf.Max(1, vertices.Length / 16000);
                for (int i = 0; i < vertices.Length; i += stride) include(transform(vertices[i]));
            }
            catch (UnityException) { /* Bounds-only fallback is explicitly described in the report. */ }
        }

        private static MapObject[] Scan(float x, float z, float radius)
        {
            // Rank cheap XZ positions first. Only measure meshes for the nearest 35:
            // avoids thousands of mesh-bounds calculations in dense native forests.
            var candidates = new List<KeyValuePair<float, Selection>>();
            foreach (Transform tr in UnityEngine.Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Editable(tr)) continue;
                if (tr.GetComponent<Renderer>() == null && tr.GetComponent<LODGroup>() == null) continue;
                float d = (new Vector2(x - tr.position.x, z - tr.position.z)).sqrMagnitude;
                if (d <= radius * radius)
                    candidates.Add(new KeyValuePair<float, Selection>(d, new Selection { transform = tr }));
            }
            foreach (Terrain terrain in Terrains())
            {
                TreeInstance[] trees = terrain.terrainData.treeInstances;
                for (int i = 0; i < trees.Length; i++)
                {
                    Vector3 p = TreePosition(terrain, trees[i]);
                    float d = (new Vector2(x - p.x, z - p.z)).sqrMagnitude;
                    if (d <= radius * radius)
                        candidates.Add(new KeyValuePair<float, Selection>(d,
                            new Selection { terrain = terrain, treeIndex = i }));
                }
            }
            var nearest = new List<MapObject>();
            foreach (var pair in candidates.OrderBy(p => p.Key).Take(35))
            {
                try { nearest.Add(ToMapObject(pair.Value)); }
                catch (InvalidOperationException) { }
            }
            return nearest.ToArray();
        }

        private static string[] ClearanceWarnings(Selection selection, Bounds proposed)
        {
            var warnings = new List<string>();
            foreach (Transform t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (selection.transform != null && (t == selection.transform ||
                    t.IsChildOf(selection.transform) || selection.transform.IsChildOf(t))) continue;
                if (t.name.StartsWith("VehicleSpawnPoint_", StringComparison.Ordinal))
                {
                    Vector3 p = t.position;
                    if (Mathf.Abs(p.x - proposed.center.x) < proposed.extents.x + 4 &&
                        Mathf.Abs(p.z - proposed.center.z) < proposed.extents.z + 6)
                        warnings.Add("Vehicle spawn corridor near " + t.name);
                }
                if (t.GetComponent<MeshRenderer>() == null) continue;
                string n = t.name.ToLowerInvariant();
                if (!(n.Contains("house") || n.Contains("building") || n.Contains("garage") ||
                    n.Contains("wall") || n.Contains("cabin"))) continue;
                if (t.GetComponent<Renderer>().bounds.Intersects(proposed))
                    warnings.Add("Intersects building surface " + t.name);
                if (warnings.Count >= 8) break;
            }
            return warnings.ToArray();
        }

        private static void HandleAgent(string file)
        {
            AgentIntent intent = null;
            var report = new AgentReport { state = "error", source = "licensed_unity_editor_scene" };
            try
            {
                intent = JsonUtility.FromJson<AgentIntent>(File.ReadAllText(file));
                File.Delete(file);
                if (intent == null) throw new ArgumentException("Empty agent request.");
                report.action = intent.action;
                report.nonce = intent.nonce;
                if (intent.nonce <= 0 || !Finite(intent.x) || !Finite(intent.y) ||
                    !Finite(intent.z) || !Finite(intent.radius))
                    throw new ArgumentException("Agent intent contains invalid numeric fields.");
                EnsureScene();
                var terrains = Terrains();
                report.native_terrain_tiles = terrains.Length;
                report.native_terrain_tree_count = terrains.Sum(t => t.terrainData.treeInstanceCount);
                if (intent.action == "health")
                {
                    report.note = "Real Editor scene imported; no image or dimensions inferred from schematic JSON.";
                }
                else if (intent.action == "scan")
                {
                    if (Mathf.Abs(intent.x) > 1000 || Mathf.Abs(intent.z) > 1000 ||
                        intent.radius < 1 || intent.radius > 150)
                        throw new ArgumentException("Scan must be inside map and radius 1..150m.");
                    report.query_position_m = new Vector3(intent.x, 0, intent.z);
                    report.nearby = Scan(intent.x, intent.z, intent.radius);
                    Terrain t = terrains.FirstOrDefault(tile => {
                        Vector3 p = tile.transform.position, s = tile.terrainData.size;
                        return intent.x >= p.x && intent.x <= p.x + s.x &&
                               intent.z >= p.z && intent.z <= p.z + s.z;
                    });
                    if (t != null)
                    {
                        report.terrain_height_m = t.SampleHeight(new Vector3(intent.x, 0, intent.z)) + t.transform.position.y;
                        Vector3 p = t.transform.position, s = t.terrainData.size;
                        report.terrain_slope_degrees = t.terrainData.GetSteepness(
                            Mathf.Clamp01((intent.x - p.x)/s.x), Mathf.Clamp01((intent.z - p.z)/s.z));
                    }
                    report.note = "35 nearest renderable scene objects/native trees at most; 3D bounds are world-axis aligned.";
                }
                else if (intent.action == "describe" || intent.action == "move" ||
                         intent.action == "scale" || intent.action == "photo")
                {
                    Selection selected = FindTarget(intent.target);
                    MapObject before = ToMapObject(selected);
                    report.selected = before;
                    if (intent.action == "describe")
                    {
                        report.outer_surface_points_m = Outline(selected).ToArray();
                        report.note = "Exterior angular-bin farthest-vertex sample, not a watertight reconstruction. "
                            + "Unreadable mesh uses bounds corners, foliage and internal leaf detail may be lost.";
                    }
                    else if (intent.action == "photo")
                    {
                        Bounds b = new Bounds(before.bounds_center_m, before.dimensions_m);
                        float extent = Mathf.Clamp(Mathf.Max(4, b.size.magnitude), 4, 180);
                        Vector3 cameraPoint = b.center + new Vector3(extent * .75f,
                            extent * .42f, -extent * 1.1f);
                        Quaternion rotation = Quaternion.LookRotation(b.center - cameraPoint, Vector3.up);
                        FrameView(cameraPoint, rotation, extent);
                        TakePhoto(cameraPoint, rotation, Path.Combine(Folder, "agent-latest.png"));
                        report.photo_file = "agent-latest.png";
                        report.genuine_unity_png = true;
                        report.note = "Real 1280x720 Unity camera RenderTexture; visual audit recommended.";
                    }
                    else
                    {
                        if (!Finite(intent.x) || !Finite(intent.y) || !Finite(intent.z))
                            throw new ArgumentException("Nonfinite transform.");
                        Bounds predicted = new Bounds(before.bounds_center_m, before.dimensions_m);
                        if (intent.action == "move")
                        {
                            if (Mathf.Abs(intent.x) > 1000 || Mathf.Abs(intent.z) > 1000 ||
                                intent.y < -500 || intent.y > 4000)
                                throw new ArgumentException("Position outside supported 2km world.");
                            predicted.center += new Vector3(intent.x, intent.y, intent.z) - before.position_m;
                        }
                        else
                        {
                            if (intent.x < .2f || intent.x > 3 || intent.y < .2f ||
                                intent.y > 3 || intent.z < .2f || intent.z > 3)
                                throw new ArgumentException("Scale factors must be 0.2..3.0.");
                            if (selected.IsTree && Mathf.Abs(intent.x - intent.z) > .001f)
                                throw new ArgumentException("Terrain tree uses one horizontal width scale (x must equal z).");
                            predicted.size = Vector3.Scale(predicted.size, new Vector3(intent.x, intent.y, intent.z));
                            if (predicted.size.x > 180 || predicted.size.y > 180 || predicted.size.z > 180)
                                throw new ArgumentException("Resulting bounds exceed 180 metres.");
                        }
                        report.warnings = ClearanceWarnings(selected, predicted);
                        report.note = intent.apply ? "Applied to native Unity assets and saved locally; Git draft checkpoint must persist it." :
                            "Dry run only: set apply=true explicitly to save the change.";
                        if (intent.apply && report.warnings.Length > 0)
                            throw new InvalidOperationException("Unsafe overlap; edit rejected: " + String.Join("; ", report.warnings));
                        if (intent.apply)
                        {
                            if (selected.IsTree)
                            {
                                Terrain terrain = selected.terrain;
                                TerrainData data = terrain.terrainData;
                                TreeInstance[] instances = data.treeInstances;
                                TreeInstance tree = instances[selected.treeIndex];
                                if (intent.action == "move")
                                {
                                    Vector3 p = new Vector3(intent.x, intent.y, intent.z) - terrain.transform.position;
                                    Vector3 normalized = new Vector3(p.x / data.size.x,
                                        p.y / data.size.y, p.z / data.size.z);
                                    if (normalized.x < 0 || normalized.x > 1 ||
                                        normalized.z < 0 || normalized.z > 1)
                                        throw new ArgumentException("Tree must stay inside its native Terrain tile.");
                                    tree.position = normalized;
                                }
                                else
                                {
                                    tree.widthScale *= intent.x;
                                    tree.heightScale *= intent.y;
                                    if (tree.widthScale < .15f || tree.widthScale > 10 ||
                                        tree.heightScale < .15f || tree.heightScale > 10)
                                        throw new ArgumentException("Native tree scale exceeds supported limits.");
                                }
                                instances[selected.treeIndex] = tree;
                                Undo.RegisterCompleteObjectUndo(data, "Aaris AI terrain tree edit");
                                data.SetTreeInstances(instances, false);
                                EditorUtility.SetDirty(data);
                                AssetDatabase.SaveAssets();
                            }
                            else
                            {
                                Transform tr = selected.transform;
                                Undo.RecordObject(tr, "Aaris AI map transform");
                                if (intent.action == "move")
                                    tr.position = new Vector3(intent.x, intent.y, intent.z);
                                else tr.localScale = Vector3.Scale(tr.localScale,
                                    new Vector3(intent.x, intent.y, intent.z));
                                if (PrefabUtility.IsPartOfPrefabInstance(tr))
                                    PrefabUtility.RecordPrefabInstancePropertyModifications(tr);
                                EditorUtility.SetDirty(tr);
                                EditorSceneManager.MarkSceneDirty(tr.gameObject.scene);
                                EditorSceneManager.SaveScene(tr.gameObject.scene);
                                AssetDatabase.SaveAssets();
                            }
                            report.selected = ToMapObject(selected);
                            report.applied = true;
                        }
                    }
                }
                else if (intent.action == "save")
                {
                    EditorSceneManager.SaveOpenScenes();
                    AssetDatabase.SaveAssets();
                    report.note = "Saved Editor scene/assets locally; checkpoint to Git branch handled by runner.";
                }
                else throw new ArgumentException("Unsupported action.");
                report.state = "ok";
            }
            catch (Exception error)
            {
                report.state = "error";
                report.note = error.GetType().Name + ": " + error.Message;
                Debug.LogError("[Aaris AI agent] " + error);
            }
            ReportAgent(report);
        }

        private static void FrameView(Vector3 point, Quaternion rotation, float distance)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null) view = EditorWindow.GetWindow<SceneView>("Scene");
            view.Show();
            view.in2DMode = false;
            view.orthographic = false;
            view.LookAtDirect(point + rotation * Vector3.forward * distance, rotation, distance);
            view.Repaint();
        }

        // Cheap 64-point pixel check catches the black/white/flat desktop bug.
        // It does NOT prove buildings, tree detail, material correctness or scene art quality.
        private static bool PlausibleRender(Texture2D texture)
        {
            float min = 1f, max = 0f;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    Color c = texture.GetPixel((x * 2 + 1) * texture.width / 16,
                                               (y * 2 + 1) * texture.height / 16);
                    float luma = .2126f * c.r + .7152f * c.g + .0722f * c.b;
                    min = Mathf.Min(min, luma);
                    max = Mathf.Max(max, luma);
                }
            return max >= .045f && min <= .965f && max - min >= .012f;
        }

        private static void TakePhoto(Vector3 point, Quaternion rotation, string path)
        {
            // Never let a previous successful snapshot impersonate a failed request.
            // Both the local browser and the public release handler check these files.
            if (File.Exists(path)) File.Delete(path);
            var go = new GameObject("__AarisLivePreviewCamera");
            go.hideFlags = HideFlags.HideAndDontSave;
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            // Camera.Render() alone is not a supported offscreen capture path in URP.
            // An explicitly configured Base camera is required for StandardRequest.
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                var urpData = go.AddComponent<UniversalAdditionalCameraData>();
                urpData.renderType = CameraRenderType.Base;
                urpData.renderPostProcessing = false;
            }
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.nearClipPlane = .1f;
            cam.farClipPlane = 5000;
            cam.transform.SetPositionAndRotation(point, rotation);
            var target = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32);
            var before = RenderTexture.active;
            var png = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = target;
                bool valid = false;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (GraphicsSettings.currentRenderPipeline != null)
                    {
                        var request = new RenderPipeline.StandardRequest { destination = target };
                        if (!RenderPipeline.SupportsRenderRequest(cam, request))
                            throw new NotSupportedException(
                                "Active URP pipeline does not support StandardRequest; no fake or empty photo will be published.");
                        RenderPipeline.SubmitRenderRequest(cam, request);
                    }
                    else
                    {
                        // Legacy built-in pipeline only.
                        cam.Render();
                    }
                    RenderTexture.active = target;
                    png.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                    png.Apply(false);
                    if (PlausibleRender(png)) { valid = true; break; }
                }
                if (!valid)
                    throw new InvalidOperationException(
                        "Unity camera rendered a black, white or nearly flat frame twice; capture rejected.");
                byte[] bytes = png.EncodeToPNG();
                if (bytes.Length < 5000)
                    throw new InvalidOperationException("Empty Unity renderer output.");
                string pending = path + ".writing";
                try
                {
                    File.WriteAllBytes(pending, bytes);
                    File.Move(pending, path);
                }
                finally
                {
                    if (File.Exists(pending)) File.Delete(pending);
                }
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = before;
                UnityEngine.Object.DestroyImmediate(png);
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
