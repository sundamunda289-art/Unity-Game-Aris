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
        private static void TakePhoto(Vector3 point, Quaternion rotation, string path)
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

    // AI hands & eyes: one bounded, explicit command envelope. Only the local,
    // owner-validated GitHub issue relay may write ai-command.json.
    // Scene edits are saved locally. Publishing them requires a review PR.
    [InitializeOnLoad]
    public static class AarisWorldAgentBridge
    {
        [Serializable] private sealed class Intent
        {
            public string op, target, part, scope;
            public float[] value;
            public float radius;
            public long nonce;
        }
        [Serializable] private sealed class Report
        {
            public string state, op, target, kind, detail, scope, asset, updatedUtc;
            public long nonce;
            public bool saved;
            public Vector3 center, size, position, rotation, scale;
            public int nearbyCount, pointCount, partCount;
            public string[] nearby, parts;
            public Vector3[] points;
        }
        private static readonly string Control = Path.Combine(Directory.GetCurrentDirectory(), "ci-live-control");
        private static double nextPoll;
        private static readonly Vector3[] Directions = BuildDirections();

        static AarisWorldAgentBridge() { EditorApplication.update += Poll; }

        private static Vector3[] BuildDirections()
        {
            var list = new System.Collections.Generic.List<Vector3>();
            for (int i = 0; i < 256; ++i)
            {
                float y = 1f - 2f * (i + 0.5f) / 256f;
                float a = i * 2.39996323f;
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                list.Add(new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r));
            }
            return list.ToArray();
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 0.4;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying) return;
            string filename = Path.Combine(Control, "ai-command.json");
            if (!File.Exists(filename)) return;
            Intent request = null;
            var result = new Report { state = "error", updatedUtc = DateTime.UtcNow.ToString("O") };
            try
            {
                request = JsonUtility.FromJson<Intent>(File.ReadAllText(filename));
                File.Delete(filename);
                if (request == null || request.nonce <= 0)
                    throw new ArgumentException("Missing command nonce");
                result.nonce = request.nonce;
                result.op = request.op;
                result.target = request.target;
                Run(request, result);
                result.state = "ok";
            }
            catch (Exception e)
            {
                result.detail = e.GetType().Name + ": " + e.Message;
                Debug.LogError("[Aaris AI bridge] " + e);
            }
            finally
            {
                Directory.CreateDirectory(Control);
                string temp = Path.Combine(Control, "ai-result.tmp");
                File.WriteAllText(temp, JsonUtility.ToJson(result, true));
                string final = Path.Combine(Control, "ai-result.json");
                if (File.Exists(final)) File.Delete(final);
                File.Move(temp, final);
            }
        }

        private static void CheckValues(Intent intent, bool scale)
        {
            if (intent.value == null || intent.value.Length != 3)
                throw new ArgumentException("value must contain exactly 3 numbers");
            foreach (float v in intent.value)
                if (float.IsNaN(v) || float.IsInfinity(v) ||
                    (scale ? (v < 0.25f || v > 3f) : Mathf.Abs(v) > 100f))
                    throw new ArgumentException("Unsafe value: move <=100m per axis, scale factor 0.25..3");
        }
        private static Vector3 Vector(Intent i) => new Vector3(i.value[0], i.value[1], i.value[2]);

        private static UnityEngine.SceneManagement.Scene EnsureScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.IsValid() || scene.path != AarisExplorerBuild.ScenePath)
                scene = EditorSceneManager.OpenScene(AarisExplorerBuild.ScenePath, OpenSceneMode.Single);
            return scene;
        }
        private static string Hierarchy(Transform t)
        {
            string result = t.name;
            while (t.parent != null) { t = t.parent; result = t.name + "/" + result; }
            return result;
        }
        private static Transform ResolveScene(string selector)
        {
            if (String.IsNullOrWhiteSpace(selector) || selector.Length > 250 ||
                selector.Contains("..") || selector.Contains("\\"))
                throw new ArgumentException("Use an exact scene hierarchy path or unique object name");
            string name = selector.StartsWith("scene:", StringComparison.Ordinal)
                ? selector.Substring(6) : selector;
            Transform found = null;
            foreach (var root in EnsureScene().GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (Hierarchy(t) == name || t.name == name)
                    {
                        if (found != null && found != t)
                            throw new InvalidOperationException("Ambiguous name; use scene:Root/Child/Name");
                        found = t;
                    }
            if (found == null) throw new InvalidOperationException("Object not found: " + name);
            return found;
        }

        private static Terrain[] Tiles()
        {
            return UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None)
                .OrderBy(t => t.transform.position.x).ThenBy(t => t.transform.position.z).ToArray();
        }

        private static void Tree(string selector, out Terrain terrain, out int index,
                                 out TreeInstance tree, out GameObject prototype)
        {
            string[] p = selector.Split(':');
            if (p.Length != 3 || p[0] != "tree" ||
                !int.TryParse(p[1], out int tileIndex) || !int.TryParse(p[2], out index))
                throw new ArgumentException("Use tree:<tile-index>:<instance-index>");
            var tiles = Tiles();
            if (tileIndex < 0 || tileIndex >= tiles.Length)
                throw new ArgumentOutOfRangeException("tile-index");
            terrain = tiles[tileIndex];
            var instances = terrain.terrainData.treeInstances;
            if (index < 0 || index >= instances.Length)
                throw new ArgumentOutOfRangeException("instance-index");
            tree = instances[index];
            var prototypes = terrain.terrainData.treePrototypes;
            if (tree.prototypeIndex < 0 || tree.prototypeIndex >= prototypes.Length)
                throw new InvalidOperationException("Tree prototype missing");
            prototype = prototypes[tree.prototypeIndex].prefab;
        }
        private static Vector3 TreePosition(Terrain terrain, TreeInstance tree)
        {
            return terrain.transform.position +
                Vector3.Scale(tree.position, terrain.terrainData.size);
        }
        private static Bounds ObjectBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            bool has = false;
            var box = new Bounds(root.transform.position, Vector3.zero);
            foreach (var renderer in renderers)
            {
                if (renderer is ParticleSystemRenderer) continue;
                if (!has) { box = renderer.bounds; has = true; }
                else box.Encapsulate(renderer.bounds);
            }
            if (!has) box = new Bounds(root.transform.position, Vector3.one * 0.1f);
            return box;
        }
        private static GameObject PreviewTree(Terrain terrain, TreeInstance tree, GameObject prototype)
        {
            if (prototype == null) throw new InvalidOperationException("Tree prefab is missing");
            GameObject go = UnityEngine.Object.Instantiate(prototype);
            go.name = "__AarisAgentTreePreview";
            go.hideFlags = HideFlags.HideAndDontSave;
            go.transform.position = TreePosition(terrain, tree);
            go.transform.rotation = Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0);
            Vector3 original = go.transform.localScale;
            go.transform.localScale = new Vector3(original.x * tree.widthScale,
                original.y * tree.heightScale, original.z * tree.widthScale);
            return go;
        }
        private static void FillBounds(Report report, GameObject go)
        {
            var bounds = ObjectBounds(go);
            report.center = bounds.center;
            report.size = bounds.size;
            report.position = go.transform.position;
            report.rotation = go.transform.eulerAngles;
            report.scale = go.transform.localScale;
        }
        private static Vector3[] OuterPoints(GameObject root, Bounds fallback)
        {
            var max = new float[Directions.Length];
            var best = new Vector3[Directions.Length];
            for (int i = 0; i < max.Length; ++i) max[i] = float.NegativeInfinity;
            int read = 0;
            foreach (var meshFilter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = meshFilter.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                var vertices = mesh.vertices;
                int stride = Mathf.Max(1, vertices.Length / 12000);
                for (int j = 0; j < vertices.Length; j += stride)
                {
                    var point = meshFilter.transform.TransformPoint(vertices[j]);
                    for (int i = 0; i < max.Length; ++i)
                    {
                        float score = Vector3.Dot(Directions[i], point - fallback.center);
                        if (score > max[i]) { max[i] = score; best[i] = point; }
                    }
                    ++read;
                }
            }
            if (read == 0)
            {
                Vector3 min = fallback.min, maxPt = fallback.max;
                return new[] {
                    new Vector3(min.x,min.y,min.z), new Vector3(min.x,min.y,maxPt.z),
                    new Vector3(min.x,maxPt.y,min.z), new Vector3(min.x,maxPt.y,maxPt.z),
                    new Vector3(maxPt.x,min.y,min.z), new Vector3(maxPt.x,min.y,maxPt.z),
                    new Vector3(maxPt.x,maxPt.y,min.z), new Vector3(maxPt.x,maxPt.y,maxPt.z)
                };
            }
            var output = new System.Collections.Generic.List<Vector3>();
            foreach (var point in best)
                if (!output.Any(existing => (existing - point).sqrMagnitude < 0.0001f))
                    output.Add(point);
            return output.ToArray();
        }
        private static void Photo(Bounds bounds)
        {
            // Framing uses the measured world bounds, not a guessed camera coordinate.
            float distance = Mathf.Max(8f, bounds.size.magnitude * 1.9f);
            Vector3 point = bounds.center + new Vector3(distance * 0.65f,
                Mathf.Max(3f, distance * 0.28f), -distance);
            Quaternion rotation = Quaternion.LookRotation(bounds.center - point, Vector3.up);
            var view = SceneView.lastActiveSceneView;
            if (view == null) view = EditorWindow.GetWindow<SceneView>("Scene");
            view.in2DMode = false;
            view.orthographic = false;
            view.LookAtDirect(bounds.center, rotation, distance);
            view.Repaint();
            var go = new GameObject("__AarisAgentPhotoCamera");
            go.hideFlags = HideFlags.HideAndDontSave;
            var camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 5000f;
            camera.transform.SetPositionAndRotation(point, rotation);
            var rt = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32);
            var old = RenderTexture.active;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                tex.Apply(false);
                // Reject uniform black, grey and blank frames before publishing.
                Color32 baseColor = tex.GetPixel(16, 16);
                int different = 0;
                for (int y = 40; y < 720; y += 85)
                    for (int x = 40; x < 1280; x += 95)
                    {
                        var c = tex.GetPixel(x, y);
                        if (Mathf.Abs(c.r - baseColor.r / 255f) +
                            Mathf.Abs(c.g - baseColor.g / 255f) +
                            Mathf.Abs(c.b - baseColor.b / 255f) > 0.12f) different++;
                    }
                if (different < 5) throw new InvalidOperationException("Flat/blank Unity render refused");
                var png = tex.EncodeToPNG();
                if (png.Length < 5000) throw new InvalidOperationException("Empty Unity render");
                File.WriteAllBytes(Path.Combine(Control, "ai-photo.png"), png);
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = old;
                UnityEngine.Object.DestroyImmediate(tex);
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
        private static string[] ChildParts(GameObject root)
        {
            // Exact relative paths are directly usable by part_scale. A
            // top-level scene target and a preview tree prefab behave alike.
            var output = new System.Collections.Generic.List<string>();
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child == root.transform) continue;
                string path = child.name;
                Transform cursor = child.parent;
                while (cursor != null && cursor != root.transform)
                {
                    path = cursor.name + "/" + path;
                    cursor = cursor.parent;
                }
                Renderer renderer = child.GetComponent<Renderer>();
                Vector3 bounds = renderer == null ? Vector3.zero : renderer.bounds.size;
                Vector3 local = child.localScale;
                output.Add(path +
                    " | localScale=" + local.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    "," + local.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    "," + local.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    " | rendererSizeM=" + bounds.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    "," + bounds.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    "," + bounds.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                if (output.Count == 80) break;
            }
            return output.ToArray();
        }

        private static void Nearby(Intent command, Report report)
        {
            if (command.value == null || command.value.Length != 3 ||
                command.value.Any(v => float.IsNaN(v) || float.IsInfinity(v)) ||
                Mathf.Abs(command.value[0]) > 1000 || Mathf.Abs(command.value[2]) > 1000 ||
                command.value[1] < -500 || command.value[1] > 4000)
                throw new ArgumentException("Nearby center is outside the 2km world");
            if (float.IsNaN(command.radius) || command.radius < 1 || command.radius > 200)
                throw new ArgumentException("nearby radius must be 1..200 metres");
            Vector3 center = Vector(command);
            var entries = new System.Collections.Generic.List<System.Tuple<float, string>>();
            foreach (var root in EnsureScene().GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    float d = Vector3.Distance(center, t.position);
                    if (d <= command.radius)
                        entries.Add(System.Tuple.Create(d, "scene:" + Hierarchy(t)));
                }
            var tiles = Tiles();
            for (int i = 0; i < tiles.Length; i++)
            {
                var terrain = tiles[i];
                var size = terrain.terrainData.size;
                var pos = terrain.transform.position;
                var instances = terrain.terrainData.treeInstances;
                for (int j = 0; j < instances.Length; j++)
                {
                    float dx = pos.x + instances[j].position.x * size.x - center.x;
                    float dz = pos.z + instances[j].position.z * size.z - center.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d <= command.radius)
                        entries.Add(System.Tuple.Create(d, "tree:" + i + ":" + j));
                }
            }
            entries.Sort((a,b) => a.Item1.CompareTo(b.Item1));
            report.nearbyCount = entries.Count;
            report.nearby = entries.Take(30).Select(v => v.Item2 +
                " (" + v.Item1.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "m)").ToArray();
            report.center = center;
            report.detail = "Nearest 30 scene objects/terrain trees; total matching count included";
            report.kind = "neighborhood";
        }

        private static void Run(Intent command, Report report)
        {
            EnsureScene();
            if (command.op == "nearby") { Nearby(command, report); return; }
            if (String.IsNullOrEmpty(command.target))
                throw new ArgumentException("target is required");
            bool nativeTree = command.target.StartsWith("tree:", StringComparison.Ordinal);
            if (nativeTree)
            {
                Tree(command.target, out Terrain terrain, out int index,
                    out TreeInstance instance, out GameObject prototype);
                report.kind = "terrain-tree";
                GameObject preview = PreviewTree(terrain, instance, prototype);
                try
                {
                    FillBounds(report, preview);
                    report.position = TreePosition(terrain, instance);
                    report.scale = new Vector3(instance.widthScale, instance.heightScale, instance.widthScale);
                    var bounds = ObjectBounds(preview);
                    if (command.op == "move")
                    {
                        CheckValues(command, false);
                        var delta = Vector(command);
                        var td = terrain.terrainData;
                        var next = instance.position + new Vector3(delta.x / td.size.x,
                            0, delta.z / td.size.z);
                        if (next.x < 0 || next.x > 1 || next.z < 0 || next.z > 1)
                            throw new ArgumentException("Tree move leaves its terrain tile");
                        // Re-ground native tree; treeInstance.position is terrain-normalized.
                        next.y = td.GetInterpolatedHeight(next.x, next.z) / td.size.y;
                        instance.position = next;
                        var trees = td.treeInstances;
                        trees[index] = instance;
                        td.SetTreeInstances(trees, true);
                        terrain.Flush();
                        EditorUtility.SetDirty(td);
                        AssetDatabase.SaveAssets();
                        report.asset = AssetDatabase.GetAssetPath(td);
                        report.position = TreePosition(terrain, instance);
                        report.saved = true;
                    }
                    else if (command.op == "scale")
                    {
                        CheckValues(command, true);
                        var factor = Vector(command);
                        if (Mathf.Abs(factor.x - factor.z) > 0.0001f)
                            throw new ArgumentException("Terrain trees require equal X/Z width factors");
                        instance.widthScale = Mathf.Clamp(instance.widthScale * factor.x, 0.1f, 5f);
                        instance.heightScale = Mathf.Clamp(instance.heightScale * factor.y, 0.1f, 5f);
                        var trees = terrain.terrainData.treeInstances;
                        trees[index] = instance;
                        terrain.terrainData.SetTreeInstances(trees, false);
                        terrain.Flush();
                        EditorUtility.SetDirty(terrain.terrainData);
                        AssetDatabase.SaveAssets();
                        report.asset = AssetDatabase.GetAssetPath(terrain.terrainData);
                        report.scale = new Vector3(instance.widthScale, instance.heightScale, instance.widthScale);
                        report.saved = true;
                    }
                    else if (command.op == "part_scale")
                    {
                        // A Terrain tree is a native instance, not a GameObject.
                        // Prefab-part changes necessarily affect ALL instances
                        // sharing that prototype. Require explicit scope.
                        if (command.scope != "prototype")
                            throw new ArgumentException("Native tree part_scale requires scope=prototype; changes all matching trees");
                        CheckValues(command, true);
                        if (String.IsNullOrWhiteSpace(command.part) || command.part.Length > 160 ||
                            command.part.StartsWith("/", StringComparison.Ordinal) || command.part.Contains(".."))
                            throw new ArgumentException("Use an exact relative prefab child hierarchy");
                        string prefabPath = AssetDatabase.GetAssetPath(prototype);
                        if (String.IsNullOrEmpty(prefabPath) ||
                            !prefabPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                            !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Prototype is not an editable .prefab; model import assets cannot be changed here");
                        GameObject prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                        try
                        {
                            var part = prefabRoot.transform.Find(command.part);
                            if (part == null) throw new InvalidOperationException("Tree prefab part not found");
                            Vector3 nextScale = Vector3.Scale(part.localScale, Vector(command));
                            if (nextScale.x <= 0 || nextScale.y <= 0 || nextScale.z <= 0 ||
                                nextScale.magnitude > 10000)
                                throw new ArgumentException("Unsafe prefab part size");
                            part.localScale = nextScale;
                            if (PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath) == null)
                                throw new IOException("Cannot save native tree prototype");
                        }
                        finally { PrefabUtility.UnloadPrefabContents(prefabRoot); }
                        AssetDatabase.SaveAssets();
                        report.scope = "shared-prototype";
                        report.asset = prefabPath;
                        report.saved = true;
                        report.detail = "Entire shared tree prototype edited; every terrain tree using this prefab is affected. Re-render and check LOD/collisions.";
                    }
                    else if (command.op == "parts")
                    {
                        report.partCount = Mathf.Max(0, preview.GetComponentsInChildren<Transform>(true).Length - 1);
                        report.parts = ChildParts(preview);
                        report.detail = "Relative prefab child paths. Editing a native tree prototype part requires scope=prototype and affects all matching instances.";
                    }
                    else if (command.op == "outline")
                    {
                        report.points = OuterPoints(preview, bounds);
                        report.pointCount = report.points.Length;
                        report.detail = "World-space exterior support points sampled from readable prefab meshes; bounds fallback if meshes unreadable";
                    }
                    else if (command.op == "photo") Photo(bounds);
                    else if (command.op == "inspect") report.detail = "Native tree instance; scale X/Z=widthScale, Y=heightScale";
                    else throw new ArgumentException("Allowed tree ops: inspect, parts, outline, photo, move, scale, part_scale (prototype scope)");
                    if (report.saved)
                    {
                        // Return AFTER-edit dimensions, not stale pre-edit bounds.
                        GameObject changed = PreviewTree(terrain, instance, prototype);
                        try
                        {
                            FillBounds(report, changed);
                            report.position = TreePosition(terrain, instance);
                            report.scale = new Vector3(instance.widthScale, instance.heightScale,
                                instance.widthScale);
                        }
                        finally { UnityEngine.Object.DestroyImmediate(changed); }
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(preview); }
                return;
            }
            Transform target = ResolveScene(command.target);
            report.kind = "scene-object";
            report.target = "scene:" + Hierarchy(target);
            FillBounds(report, target.gameObject);
            var box = ObjectBounds(target.gameObject);
            report.asset = AarisExplorerBuild.ScenePath;
            if (command.op == "inspect")
                report.detail = "Bounds cover all child renderers; position/rotation/scale are transform values";
            else if (command.op == "parts")
            {
                report.partCount = Mathf.Max(0, target.GetComponentsInChildren<Transform>(true).Length - 1);
                report.parts = ChildParts(target.gameObject);
                report.detail = "Exact relative child transform paths and approximate renderer dimensions for part_scale.";
            }
            else if (command.op == "outline")
            {
                report.points = OuterPoints(target.gameObject, box);
                report.pointCount = report.points.Length;
                report.detail = "World-space surface support points; fallback bounds corners if mesh readback is disabled";
            }
            else if (command.op == "photo") Photo(box);
            else if (command.op == "move" || command.op == "scale" || command.op == "part_scale")
            {
                CheckValues(command, command.op != "move");
                Transform edited = target;
                if (command.op == "part_scale")
                {
                    if (String.IsNullOrWhiteSpace(command.part) || command.part.Length > 160 ||
                        command.part.Contains(".."))
                        throw new ArgumentException("part must be an exact relative transform path");
                    edited = target.Find(command.part);
                    if (edited == null) throw new InvalidOperationException("Child part not found");
                }
                Undo.RecordObject(edited, "Aaris AI " + command.op);
                if (command.op == "move")
                {
                    Vector3 newPosition = target.position + Vector(command);
                    if (Mathf.Abs(newPosition.x) > 1000 || Mathf.Abs(newPosition.z) > 1000 ||
                        newPosition.y < -500 || newPosition.y > 4000)
                        throw new ArgumentException("Moved object would leave world coordinates");
                    edited.position = newPosition;
                }
                else
                {
                    edited.localScale = Vector3.Scale(edited.localScale, Vector(command));
                    if (edited.localScale.x <= 0 || edited.localScale.y <= 0 || edited.localScale.z <= 0 ||
                        edited.localScale.magnitude > 10000)
                        throw new ArgumentException("Resulting scale is unsafe");
                }
                EditorUtility.SetDirty(edited);
                EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
                if (!EditorSceneManager.SaveOpenScenes())
                    throw new IOException("Unity could not save changed scene");
                AssetDatabase.SaveAssets();
                FillBounds(report, target.gameObject);
                report.saved = true;
                report.detail = "Locally saved in scene; use AI SAVE to create a Git LFS-backed review PR";
            }
            else throw new ArgumentException("Allowed ops: nearby, inspect, parts, outline, photo, move, scale, part_scale");
        }
    }

}
