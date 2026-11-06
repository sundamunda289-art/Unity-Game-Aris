using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    // Editor-only remote scene navigation. Reads a validated local JSON command.
    // No arbitrary script execution, no asset/scene writes, no network listeners.
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
        private static readonly string Folder = Path.Combine(Directory.GetCurrentDirectory(), "ci-live-control");
        private static double nextTime;
        static AarisLiveCameraBridge()
        {
            EditorApplication.update += Poll;
        }
        private static void Report(string state, long nonce)
        {
            Directory.CreateDirectory(Folder);
            var report = new CameraStatus { state = state, nonce = nonce,
                updatedUtc = DateTime.UtcNow.ToString("O") };
            File.WriteAllText(Path.Combine(Folder, "status.json"),
                JsonUtility.ToJson(report, true));
        }
        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextTime) return;
            nextTime = EditorApplication.timeSinceStartup + 0.6;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying) return;
            string file = Path.Combine(Folder, "camera.json");
            if (!File.Exists(file)) return;
            CameraIntent intent = null;
            try
            {
                intent = JsonUtility.FromJson<CameraIntent>(File.ReadAllText(file));
                File.Delete(file);
                if (intent == null || float.IsNaN(intent.x) || float.IsNaN(intent.y) ||
                    float.IsNaN(intent.z) || intent.x < -1000 || intent.x > 1000 ||
                    intent.z < -1000 || intent.z > 1000 ||
                    intent.y < -500 || intent.y > 4000 ||
                    intent.distance < 2 || intent.distance > 1000 ||
                    intent.pitch < -85 || intent.pitch > 85)
                    throw new ArgumentException("Invalid camera coordinates");
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (scene.path != AarisExplorerBuild.ScenePath)
                    EditorSceneManager.OpenScene(AarisExplorerBuild.ScenePath, OpenSceneMode.Single);

                SceneView view = SceneView.lastActiveSceneView;
                if (view == null) view = EditorWindow.GetWindow<SceneView>("Scene");
                view.Show();
                Quaternion rotation = Quaternion.Euler(intent.pitch, intent.yaw, 0f);
                Vector3 cameraPoint = new Vector3(intent.x, intent.y, intent.z);
                Vector3 lookPoint = cameraPoint + rotation * Vector3.forward * intent.distance;
                view.in2DMode = false;
                view.orthographic = false;
                view.LookAtDirect(lookPoint, rotation, intent.distance);
                view.Repaint();
                TakePhoto(cameraPoint, rotation, Path.Combine(Folder, "latest.png"));
                Report("Camera moved and real Unity PNG saved", intent.nonce);
            }
            catch (Exception error)
            {
                Report("Camera error: " + error.GetType().Name + ": " + error.Message,
                    intent == null ? 0 : intent.nonce);
                Debug.LogError("[Aaris live bridge] " + error);
            }
        }
        internal static void TakePhoto(Vector3 point, Quaternion rotation, string path)
        {
            var go = new GameObject("__AarisLivePreviewCamera");
            go.hideFlags = HideFlags.HideAndDontSave;
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 5000f;
            cam.transform.SetPositionAndRotation(point, rotation);
            var target = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32);
            var before = RenderTexture.active;
            var png = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = target;
                cam.Render();
                RenderTexture.active = target;
                png.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                png.Apply(false);
                // Flat or black frames are not reliable evidence of an imported scene.
                Color32[] pixels = png.GetPixels32();
                int low = 255, high = 0;
                for (int py = 48; py < 720; py += 96)
                    for (int px = 64; px < 1280; px += 128)
                    {
                        Color32 c = pixels[py * 1280 + px];
                        int v = (c.r + c.g + c.b) / 3;
                        low = Math.Min(low, v);
                        high = Math.Max(high, v);
                    }
                if (high - low < 8)
                    throw new InvalidOperationException("Flat/black camera output; renderer or scene not ready");
                var bytes = png.EncodeToPNG();
                if (bytes.Length < 5000) throw new InvalidOperationException("Empty renderer output");
                File.WriteAllBytes(path, bytes);
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


namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// AI hands and eyes: constrained JSON commands, no interpreter, reflection,
    /// network listener, arbitrary file path or Unity menu execution.
    /// C# runs on the Editor main thread; Python performs strict ingress validation.
    /// Read-only results are JSON with world-space metres and sampled mesh dots.
    /// </summary>
    [InitializeOnLoad]
    public static class AarisAiSceneBridge
    {
        [Serializable] private sealed class Intent
        {
            public string action, id, query, axis, part, hex;
            public bool shared;
            public float x, y, z, radius, dx, dy, dz, sx, sy, sz, value, yaw, pitch, distance;
            public int limit;
            public long nonce;
        }

        [Serializable] private sealed class Item
        {
            public string id, name, kind, color, sourceAsset;
            public string[] parts;
            public Vector3 position, dimensions;
            public Vector3[] dots;
        }

        [Serializable] private sealed class Answer
        {
            public long nonce;
            public string action, state, message, updatedUtc, modifiedAsset, photo;
            public int matched;
            public Item[] items;
        }

        private static readonly string Folder = Path.Combine(Directory.GetCurrentDirectory(), "ci-live-control");
        private static double nextPoll;
        private const int MaxDots = 400;
        private static readonly System.StringComparison IgnoreCase = System.StringComparison.OrdinalIgnoreCase;

        static AarisAiSceneBridge() { EditorApplication.update += Poll; }

        private static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
        private static void Check(bool condition, string explanation)
        {
            if (!condition) throw new ArgumentException(explanation);
        }

        private static void Validate(Intent q)
        {
            Check(q != null && q.nonce > 0, "Invalid AI request or nonce");
            Check(new[] {"inspect", "outline", "move", "scale", "resize", "capture", "tint_shared", "part_scale_shared"}
                .Contains(q.action), "Action is not allowlisted");
            Check(q.id == null || q.id.Length <= 180, "ID too long");
            Check(q.query == null || q.query.Length <= 80, "Query too long");
            Check(q.part == null || q.part.Length <= 80, "Part too long");
            foreach (float v in new[] { q.x, q.y, q.z, q.dx, q.dy, q.dz, q.sx,
                         q.sy, q.sz, q.radius, q.value, q.yaw, q.pitch, q.distance })
                Check(Finite(v), "Non-finite number");
            Check(Mathf.Abs(q.x) <= 1000 && Mathf.Abs(q.z) <= 1000 && q.y >= -500 && q.y <= 4000,
                "World coordinates outside map");
            Check(q.radius >= 0 && q.radius <= 250 && q.limit >= 0 && q.limit <= 30,
                "Invalid search bounds");
            Check(Mathf.Abs(q.dx) <= 50 && Mathf.Abs(q.dy) <= 50 && Mathf.Abs(q.dz) <= 50,
                "Move exceeds 50 metres");
            Check(q.sx >= 0 && q.sx <= 3 && q.sy >= 0 && q.sy <= 3 && q.sz >= 0 && q.sz <= 3,
                "Invalid relative scale factor");
            Check(q.value >= 0 && q.value <= 100, "Dimension out of bounds");
            Check(q.yaw >= -360 && q.yaw <= 360 && q.pitch >= -85 && q.pitch <= 85,
                "Camera angle out of bounds");
            Check(q.distance >= 0 && q.distance <= 500, "Camera distance out of bounds");
            if (q.action != "inspect" && q.action != "capture")
                Check(!string.IsNullOrEmpty(q.id), "This operation needs an exact object ID");
            if (q.action == "tint_shared")
                Check(q.shared && !string.IsNullOrEmpty(q.part) && !string.IsNullOrEmpty(q.hex),
                    "Shared material edit requires explicit opt-in, part and colour");
            if (q.action == "part_scale_shared")
                Check(q.shared && !string.IsNullOrEmpty(q.part) && q.sx >= .2f && q.sy >= .2f && q.sz >= .2f,
                    "Shared prefab part scaling requires explicit opt-in, named part and safe factors");
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 0.5;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying) return;
            string file = Path.Combine(Folder, "ai-command.json");
            if (!File.Exists(file)) return;
            Intent q = null;
            Answer answer = new Answer { state = "error", items = new Item[0],
                updatedUtc = DateTime.UtcNow.ToString("O") };
            try
            {
                string raw = File.ReadAllText(file);
                File.Delete(file);
                q = JsonUtility.FromJson<Intent>(raw);
                if (q != null) { answer.nonce = q.nonce; answer.action = q.action; }
                Validate(q);
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (scene.path != AarisExplorerBuild.ScenePath)
                    EditorSceneManager.OpenScene(AarisExplorerBuild.ScenePath, OpenSceneMode.Single);
                Perform(q, answer);
                answer.state = "ok";
            }
            catch (Exception error)
            {
                answer.message = error.GetType().Name + ": " + error.Message;
                Debug.LogError("[Aaris AI bridge] " + error);
            }
            try
            {
                Directory.CreateDirectory(Folder);
                string temp = Path.Combine(Folder, "ai-result.tmp");
                File.WriteAllText(temp, JsonUtility.ToJson(answer, true));
                if (File.Exists(Path.Combine(Folder, "ai-result.json")))
                    File.Delete(Path.Combine(Folder, "ai-result.json"));
                File.Move(temp, Path.Combine(Folder, "ai-result.json"));
            }
            catch (IOException error) { Debug.LogError("[Aaris AI bridge] Result write failed: " + error); }
        }

        private static Terrain[] Terrains()
        {
            var terrains = UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None);
            Array.Sort(terrains, (a, b) => {
                int n = a.transform.position.x.CompareTo(b.transform.position.x);
                return n != 0 ? n : a.transform.position.z.CompareTo(b.transform.position.z);
            });
            return terrains;
        }

        private static Vector3 TreePosition(Terrain terrain, TreeInstance tree)
        {
            Vector3 s = terrain.terrainData.size;
            return terrain.transform.position + Vector3.Scale(tree.position, s);
        }

        private static Bounds SceneBounds(Transform t)
        {
            Renderer[] all = t.GetComponentsInChildren<Renderer>(true);
            Bounds b = new Bounds(t.position, Vector3.zero);
            bool found = false;
            foreach (Renderer r in all)
            {
                if (!found) { b = r.bounds; found = true; }
                else b.Encapsulate(r.bounds);
            }
            return found ? b : new Bounds(t.position, Vector3.zero);
        }

        // A terrain tree does not exist as a scene GameObject. Compute its size
        // from its native TreePrototype's rendered bounds and per-instance scale.
        private static Bounds TreeBounds(Terrain terrain, TreeInstance tree)
        {
            Vector3 p = TreePosition(terrain, tree);
            var prototypes = terrain.terrainData.treePrototypes;
            if (tree.prototypeIndex < 0 || tree.prototypeIndex >= prototypes.Length)
                return new Bounds(p, Vector3.zero);
            GameObject prefab = prototypes[tree.prototypeIndex].prefab;
            if (prefab == null) return new Bounds(p, Vector3.zero);
            Bounds local = new Bounds(Vector3.zero, Vector3.zero);
            bool found = false;
            foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Bounds m = mf.sharedMesh.bounds;
                Matrix4x4 localMatrix = prefab.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                foreach (Vector3 corner in Corners(m))
                {
                    Vector3 v = localMatrix.MultiplyPoint3x4(corner);
                    if (!found) { local = new Bounds(v, Vector3.zero); found = true; }
                    else local.Encapsulate(v);
                }
            }
            if (!found)
            {
                foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    if (!found) { local = renderer.bounds; found = true; }
                    else local.Encapsulate(renderer.bounds);
                }
            }
            if (!found) return new Bounds(p, Vector3.zero);
            Quaternion rotate = Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0);
            Vector3 scale = new Vector3(tree.widthScale, tree.heightScale, tree.widthScale);
            Bounds world = new Bounds(p, Vector3.zero);
            foreach (Vector3 corner in Corners(local))
                world.Encapsulate(p + rotate * Vector3.Scale(corner, scale));
            return world;
        }

        private static Vector3[] Corners(Bounds b)
        {
            Vector3 a = b.min, z = b.max;
            return new[] {
                new Vector3(a.x,a.y,a.z),new Vector3(a.x,a.y,z.z),
                new Vector3(a.x,z.y,a.z),new Vector3(a.x,z.y,z.z),
                new Vector3(z.x,a.y,a.z),new Vector3(z.x,a.y,z.z),
                new Vector3(z.x,z.y,a.z),new Vector3(z.x,z.y,z.z)
            };
        }

        private static string SceneId(Transform t)
        {
            return GlobalObjectId.GetGlobalObjectIdSlow(t.gameObject).ToString();
        }

        private static string[] PartNames(GameObject root)
        {
            return root.GetComponentsInChildren<Transform>(true)
                .Where(t => t != root.transform)
                .Select(t => t.gameObject.name).Distinct()
                .Take(80).ToArray();
        }

        private static GameObject TreePrefab(Terrain terrain, int treeIndex)
        {
            TreeInstance[] instances = terrain.terrainData.treeInstances;
            int proto = instances[treeIndex].prototypeIndex;
            TreePrototype[] prototypes = terrain.terrainData.treePrototypes;
            Check(proto >= 0 && proto < prototypes.Length, "Native tree prototype unavailable");
            GameObject prefab = prototypes[proto].prefab;
            Check(prefab != null, "Native tree has no prefab");
            return prefab;
        }

        private static Item SceneItem(Transform t, bool dots)
        {
            Bounds b = SceneBounds(t);
            string color = "";
            var renderer = t.GetComponentInChildren<Renderer>(true);
            if (renderer != null && renderer.sharedMaterial != null)
            {
                Material mat = renderer.sharedMaterial;
                if (mat.HasProperty("_BaseColor"))
                    color = "#" + ColorUtility.ToHtmlStringRGBA(mat.GetColor("_BaseColor"));
                else if (mat.HasProperty("_Color"))
                    color = "#" + ColorUtility.ToHtmlStringRGBA(mat.color);
            }
            return new Item { id = SceneId(t), name = t.name,
                kind = "scene-object", color = color, position = t.position,
                dimensions = b.size, sourceAsset = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,
                parts = PartNames(t.gameObject), dots = dots ? SceneDots(t, b) : new Vector3[0] };
        }

        private static Item TreeItem(Terrain terrain, int tile, int index, bool dots)
        {
            var inst = terrain.terrainData.treeInstances[index];
            Bounds bounds = TreeBounds(terrain, inst);
            var proto = terrain.terrainData.treePrototypes[inst.prototypeIndex];
            GameObject prefab = proto.prefab;
            string asset = AssetDatabase.GetAssetPath(terrain.terrainData);
            return new Item { id = "terrain:" + tile + ":" + index,
                name = prefab == null ? "Tree" : prefab.name,
                kind = "native-terrain-tree", position = TreePosition(terrain, inst),
                dimensions = bounds.size, color = "#" + ColorUtility.ToHtmlStringRGBA(inst.color),
                sourceAsset = asset, parts = prefab == null ? new string[0] : PartNames(prefab),
                dots = dots ? TreeDots(terrain, inst, bounds) : new Vector3[0] };
        }

        private static Vector3[] SceneDots(Transform root, Bounds b)
        {
            var points = new System.Collections.Generic.List<Vector3>(Corners(b));
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Vector3[] vertices;
                try { vertices = mf.sharedMesh.vertices; }
                catch (UnityException) { continue; } // Non-readable imported mesh.
                int space = Math.Max(1, vertices.Length / 50);
                for (int i = 0; i < vertices.Length && points.Count < MaxDots; i += space)
                    points.Add(mf.transform.TransformPoint(vertices[i]));
                if (points.Count >= MaxDots) break;
            }
            return points.ToArray();
        }

        private static Vector3[] TreeDots(Terrain terrain, TreeInstance tree, Bounds b)
        {
            var points = new System.Collections.Generic.List<Vector3>(Corners(b));
            var prototypes = terrain.terrainData.treePrototypes;
            if (tree.prototypeIndex >= prototypes.Length) return points.ToArray();
            GameObject root = prototypes[tree.prototypeIndex].prefab;
            if (root == null) return points.ToArray();
            Vector3 pos = TreePosition(terrain, tree);
            Quaternion yaw = Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0);
            Vector3 scale = new Vector3(tree.widthScale, tree.heightScale, tree.widthScale);
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Vector3[] vertices;
                try { vertices = mf.sharedMesh.vertices; }
                catch (UnityException) { continue; }
                Matrix4x4 localMatrix = root.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                int space = Math.Max(1, vertices.Length / 70);
                for (int i = 0; i < vertices.Length && points.Count < MaxDots; i += space)
                    points.Add(pos + yaw * Vector3.Scale(localMatrix.MultiplyPoint3x4(vertices[i]), scale));
                if (points.Count >= MaxDots) break;
            }
            return points.ToArray();
        }

        private static bool Select(string id, out Transform obj, out Terrain terrain, out int treeIndex, out int tileIndex)
        {
            obj = null; terrain = null; treeIndex = -1; tileIndex = -1;
            if (id != null && id.StartsWith("terrain:", StringComparison.Ordinal))
            {
                string[] pieces = id.Split(':');
                int i, j;
                var tiles = Terrains();
                if (pieces.Length != 3 || !int.TryParse(pieces[1], out i) ||
                    !int.TryParse(pieces[2], out j) || i < 0 || i >= tiles.Length ||
                    j < 0 || j >= tiles[i].terrainData.treeInstanceCount) return false;
                terrain = tiles[i]; treeIndex = j; tileIndex = i; return true;
            }
            foreach (Transform t in UnityEngine.Object.FindObjectsByType<Transform>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.gameObject.scene.path != AarisExplorerBuild.ScenePath) continue;
                if (SceneId(t) != id) continue;
                obj = t;
                return true;
            }
            return false;
        }

        private sealed class Candidate
        {
            public Transform obj;
            public Terrain terrain;
            public int tile, index;
            public float distanceSq;
        }

        private static void Inspect(Intent q, Answer answer)
        {
            // Rank by cheap native positions. Inspect only the closest meshes.
            var candidates = new System.Collections.Generic.List<Candidate>();
            int max = q.limit == 0 ? 10 : q.limit;
            bool named = !string.IsNullOrEmpty(q.query);
            float maxSquare = q.radius * q.radius;
            foreach (Transform t in UnityEngine.Object.FindObjectsByType<Transform>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.gameObject.scene.path != AarisExplorerBuild.ScenePath) continue;
                if (named && t.name.IndexOf(q.query, IgnoreCase) < 0) continue;
                float dx = t.position.x - q.x, dz = t.position.z - q.z;
                float distance = dx * dx + dz * dz;
                if (distance > maxSquare) continue;
                if (!named && t.GetComponent<Renderer>() == null) continue;
                candidates.Add(new Candidate { obj = t, distanceSq = distance });
            }
            Terrain[] tiles = Terrains();
            for (int tile = 0; tile < tiles.Length; tile++)
            {
                Terrain terrain = tiles[tile];
                TreeInstance[] trees = terrain.terrainData.treeInstances;
                TreePrototype[] prototypes = terrain.terrainData.treePrototypes;
                Vector3 position = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                for (int i = 0; i < trees.Length; i++)
                {
                    TreeInstance tree = trees[i];
                    float dx = position.x + tree.position.x * size.x - q.x;
                    float dz = position.z + tree.position.z * size.z - q.z;
                    float distance = dx * dx + dz * dz;
                    if (distance > maxSquare) continue;
                    if (named)
                    {
                        if (tree.prototypeIndex < 0 || tree.prototypeIndex >= prototypes.Length) continue;
                        GameObject prefab = prototypes[tree.prototypeIndex].prefab;
                        string treeName = prefab == null ? "Tree" : prefab.name;
                        if (treeName.IndexOf(q.query, IgnoreCase) < 0) continue;
                    }
                    candidates.Add(new Candidate {
                        terrain = terrain, tile = tile, index = i, distanceSq = distance
                    });
                }
            }
            candidates.Sort((a,b) => a.distanceSq.CompareTo(b.distanceSq));
            answer.matched = candidates.Count;
            answer.items = candidates.Take(max).Select(c => c.obj != null
                ? SceneItem(c.obj,false) : TreeItem(c.terrain,c.tile,c.index,false)).ToArray();
            answer.message = "Nearest items in world-space metres. Exact IDs enable bounded edits.";
        }

        private static void Perform(Intent q, Answer answer)
        {
            if (q.action == "inspect") { Inspect(q, answer); return; }
            Transform obj; Terrain terrain; int treeIndex, tileIndex;
            bool hasTarget = Select(q.id, out obj, out terrain, out treeIndex, out tileIndex);
            if (q.action != "capture")
                Check(hasTarget, "Object ID not found; inspect again after reload");
            Bounds bounds = hasTarget
                ? (obj != null ? SceneBounds(obj) : TreeBounds(terrain, terrain.terrainData.treeInstances[treeIndex]))
                : new Bounds(new Vector3(q.x,q.y,q.z), new Vector3(2,2,2));
            if (q.action == "outline")
            {
                answer.items = new[] {obj != null ? SceneItem(obj,true) :
                    TreeItem(terrain,tileIndex,treeIndex,true)};
                answer.matched = 1;
                answer.message = "3D sampled mesh vertices plus 8 bounds corners. Not a photogrammetry scan.";
                return;
            }
            if (q.action == "capture")
            {
                Vector3 point = hasTarget
                    ? bounds.center + new Vector3(0, Mathf.Max(2,bounds.size.y*.35f),
                      -Mathf.Max(6, bounds.size.magnitude*1.5f))
                    : new Vector3(q.x,q.y,q.z);
                Quaternion rotation = hasTarget
                    ? Quaternion.LookRotation(bounds.center-point, Vector3.up)
                    : Quaternion.Euler(q.pitch,q.yaw,0);
                float distance = hasTarget ? Vector3.Distance(point,bounds.center) :
                    Mathf.Max(2,q.distance);
                SceneView view = SceneView.lastActiveSceneView;
                if (view == null) view = EditorWindow.GetWindow<SceneView>("Scene");
                view.Show();
                view.in2DMode = false; view.orthographic = false;
                view.LookAtDirect(hasTarget ? bounds.center :
                    point + rotation * Vector3.forward * distance, rotation, distance);
                view.Repaint();
                Directory.CreateDirectory(Folder);
                string filename = Path.Combine(Folder,"latest.png");
                AarisLiveCameraBridge.TakePhoto(point,rotation,filename);
                Check(new FileInfo(filename).Length > 5000, "Unity photo is empty");
                answer.photo = "latest.png";
                answer.items = hasTarget ? new[] {obj != null ? SceneItem(obj,false) :
                    TreeItem(terrain,tileIndex,treeIndex,false)} : new Item[0];
                answer.message = "Authentic Unity camera render saved to latest.png";
                return;
            }

            if (q.action == "part_scale_shared")
            {
                Check(terrain != null, "Shared part scaling currently requires a native tree ID");
                GameObject prototype = TreePrefab(terrain, treeIndex);
                string prefabPath = AssetDatabase.GetAssetPath(prototype);
                Check(prefabPath.StartsWith("Assets/", StringComparison.Ordinal) &&
                    prefabPath.EndsWith(".prefab", IgnoreCase),
                    "Tree part cannot be edited: prototype is not a standalone writable .prefab");
                GameObject editing = null;
                try
                {
                    editing = PrefabUtility.LoadPrefabContents(prefabPath);
                    Transform[] matches = editing.GetComponentsInChildren<Transform>(true)
                        .Where(t => t != editing.transform &&
                            string.Equals(t.name,q.part,IgnoreCase)).ToArray();
                    Check(matches.Length == 1,
                        "Part name must match exactly ONE prefab child; inspect parts and retry");
                    Transform target = matches[0];
                    Vector3 changed = Vector3.Scale(target.localScale,new Vector3(q.sx,q.sy,q.sz));
                    Check(Mathf.Abs(changed.x) >= .01f && Mathf.Abs(changed.y) >= .01f &&
                          Mathf.Abs(changed.z) >= .01f && Mathf.Abs(changed.x) <= 100 &&
                          Mathf.Abs(changed.y) <= 100 && Mathf.Abs(changed.z) <= 100,
                          "Shared part scale out of bounds");
                    target.localScale = changed;
                    Check(PrefabUtility.SaveAsPrefabAsset(editing,prefabPath) != null,
                        "Could not save updated shared tree prefab");
                }
                finally
                {
                    if (editing != null) PrefabUtility.UnloadPrefabContents(editing);
                }
                answer.modifiedAsset = prefabPath;
                answer.items = new[] {TreeItem(terrain,tileIndex,treeIndex,false)};
                answer.matched = 1;
                answer.message = "Saved shared tree prefab part scale. ALL trees of this prototype are affected.";
                return;
            }

            if (q.action == "tint_shared")
            {
                GameObject root = obj != null ? obj.gameObject : TreePrefab(terrain, treeIndex);
                Color tint;
                Check(ColorUtility.TryParseHtmlString(q.hex, out tint), "Invalid HTML colour");
                Renderer selected = null;
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                    if (renderer.name.IndexOf(q.part, IgnoreCase) >= 0)
                    { selected = renderer; break; }
                Check(selected != null, "Named mesh part was not found");
                Material mat = selected.sharedMaterial;
                Check(mat != null, "The selected part has no material");
                string path = AssetDatabase.GetAssetPath(mat);
                Check(path.StartsWith("Assets/", StringComparison.Ordinal) &&
                      path.EndsWith(".mat", IgnoreCase),
                      "Only saved standalone project .mat files are editable");
                string prop = mat.HasProperty("_BaseColor") ? "_BaseColor" :
                    mat.HasProperty("_Color") ? "_Color" : "";
                Check(prop != "", "Material has no supported colour property");
                Undo.RecordObject(mat,"AI shared material colour");
                mat.SetColor(prop,tint);
                EditorUtility.SetDirty(mat);
                AssetDatabase.SaveAssets();
                answer.modifiedAsset = path;
                answer.message = "Shared material changed: affects ALL users of this material";
                answer.items = new[] { obj != null ? SceneItem(obj,false) :
                    TreeItem(terrain,tileIndex,treeIndex,false) };
                answer.matched = 1;
                return;
            }

            if (obj != null)
            {
                Check(!EditorUtility.IsPersistent(obj.gameObject), "Prefab asset cannot be edited in scene mode");
                Undo.RecordObject(obj,"AI bounded scene edit");
                if (q.action == "move")
                {
                    Vector3 np = obj.position + new Vector3(q.dx,q.dy,q.dz);
                    Check(Mathf.Abs(np.x) <= 1000 && Mathf.Abs(np.z) <= 1000 &&
                          np.y >= -500 && np.y <= 4000,"Moved object outside world");
                    obj.position = np;
                }
                else if (q.action == "scale")
                {
                    Check(q.sx >= .2f && q.sy >= .2f && q.sz >= .2f, "Scale factors below 0.2");
                    Vector3 np = Vector3.Scale(obj.localScale,new Vector3(q.sx,q.sy,q.sz));
                    Check(np.x > .01f && np.y > .01f && np.z > .01f &&
                          np.x < 100 && np.y < 100 && np.z < 100,"Unsafe object scale");
                    obj.localScale = np;
                }
                else if (q.action == "resize")
                {
                    Check(q.value >= .05f, "Desired dimension too small");
                    Bounds old = SceneBounds(obj);
                    Vector3 oldSize = old.size;
                    Vector3 local = obj.localScale;
                    if (q.axis == "x") { Check(oldSize.x > .001f,"No X mesh dimension"); local.x *= q.value/oldSize.x; }
                    else if (q.axis == "y") { Check(oldSize.y > .001f,"No Y mesh dimension"); local.y *= q.value/oldSize.y; }
                    else if (q.axis == "z") { Check(oldSize.z > .001f,"No Z mesh dimension"); local.z *= q.value/oldSize.z; }
                    else throw new ArgumentException("axis must be x, y or z");
                    Check(local.x >= .01f && local.y >= .01f && local.z >= .01f &&
                          local.x <= 100 && local.y <= 100 && local.z <= 100, "Unsafe target dimension");
                    obj.localScale = local;
                }
                EditorSceneManager.MarkSceneDirty(obj.gameObject.scene);
                EditorSceneManager.SaveOpenScenes();
                answer.modifiedAsset = AarisExplorerBuild.ScenePath;
                answer.items = new[] {SceneItem(obj,false)};
                answer.message = "Saved native scene edit. Git commit requires SAVE command.";
            }
            else
            {
                TreeInstance[] instances = terrain.terrainData.treeInstances;
                TreeInstance tree = instances[treeIndex];
                Undo.RegisterCompleteObjectUndo(terrain.terrainData,"AI native tree edit");
                if (q.action == "move")
                {
                    Check(Mathf.Abs(q.dy) < .001f, "Native terrain tree Y snaps to terrain; dy must be zero");
                    Vector3 size = terrain.terrainData.size;
                    Vector3 p = tree.position + new Vector3(q.dx / size.x,0,q.dz / size.z);
                    Check(p.x >= 0 && p.x <= 1 && p.z >= 0 && p.z <= 1,
                          "Tree would leave terrain tile; use smaller move");
                    tree.position = p;
                }
                else if (q.action == "scale")
                {
                    Check(q.sx >= .2f && q.sy >= .2f && Mathf.Abs(q.sx - q.sz) < .001f,
                        "Native tree width uses equal X/Z scale factor (0.2 to 3)");
                    tree.widthScale *= q.sx;
                    tree.heightScale *= q.sy;
                    Check(tree.widthScale >= .1f && tree.widthScale <= 10 &&
                          tree.heightScale >= .1f && tree.heightScale <= 10,
                          "Unsafe tree instance scale");
                }
                else if (q.action == "resize")
                {
                    Check(q.value >= .1f, "Tree target size too small");
                    Bounds old = TreeBounds(terrain, tree);
                    if (q.axis == "y")
                    {
                        Check(old.size.y > .001f, "Tree height unknown");
                        tree.heightScale *= q.value/old.size.y;
                    }
                    else if (q.axis == "x" || q.axis == "z")
                    {
                        float width = q.axis == "x" ? old.size.x : old.size.z;
                        Check(width > .001f, "Tree canopy width unknown");
                        tree.widthScale *= q.value/width;
                    }
                    else throw new ArgumentException("Tree axis must be x, y or z");
                    Check(tree.widthScale >= .1f && tree.widthScale <= 10 &&
                          tree.heightScale >= .1f && tree.heightScale <= 10,
                          "Unsafe tree dimension");
                }
                instances[treeIndex] = tree;
                terrain.terrainData.SetTreeInstances(instances,true);
                EditorUtility.SetDirty(terrain.terrainData);
                AssetDatabase.SaveAssets();
                answer.modifiedAsset = AssetDatabase.GetAssetPath(terrain.terrainData);
                answer.items = new[] {TreeItem(terrain,tileIndex,treeIndex,false)};
                answer.message = "Saved native TerrainData tree edit. Git commit requires SAVE command.";
            }
            answer.matched = 1;
        }
    }
}
