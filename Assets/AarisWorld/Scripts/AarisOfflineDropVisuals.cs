using System;
using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Original, fully local single-player aircraft/canopy geometry. No URLs, CDN, Asset Store
    /// requests or runtime downloads. Optional local Resources models may replace these.
    /// </summary>
    internal static class AarisOfflineDropVisuals
    {
        private static Material hull, dark, wingPanels, engineDark, safetyAccent,
            canopyBlue, canopyWhite, canopyOrange, rope;
        private static Mesh cachedPropellerMesh;

        private static Material Paint(ref Material cached, Color color)
        {
            if (cached != null) return cached;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Hidden/InternalErrorShader");
            if (shader == null)
                throw new InvalidOperationException("AARIS_OFFLINE_URP_SHADER_UNAVAILABLE");
            cached = new Material(shader);
            cached.name = "AarisDrop_Original_" + ColorUtility.ToHtmlStringRGB(color);
            if (cached.HasProperty("_BaseColor")) cached.SetColor("_BaseColor", color);
            else cached.color = color;
            cached.enableInstancing = true;
            return cached;
        }

        // PBR material values are cached once, not allocated or updated per frame.
        // Distinguish painted metal, rubber-dark engine parts and glazed cockpit
        // without adding texture samples, normal maps or extra draw calls.
        private static void SetAircraftFinish(Material material, float metallic, float smoothness)
        {
            if (material == null) return;
            if (material.HasProperty("_Metallic"))
                material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", smoothness);
        }

        // Resolve once per aircraft spawn. Never mutate the shared saved URP
        // materials; old/mobile imports without these assets keep the original
        // lightweight cached fallback and its independent finish settings.
        private static Material SavedAircraftFinish(string materialName,
            ref Material fallback, Color fallbackColor, float metallic, float smoothness)
        {
            Material saved = Resources.Load<Material>("AarisDrop/Materials/" + materialName);
            if (saved != null && saved.shader != null &&
                saved.shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
                return saved;
            Material material = Paint(ref fallback, fallbackColor);
            SetAircraftFinish(material, metallic, smoothness);
            return material;
        }

        private static GameObject Part(Transform parent, string name, PrimitiveType primitive,
            Vector3 position, Quaternion rotation, Vector3 scale, Material material)
        {
            GameObject obj = GameObject.CreatePrimitive(primitive);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            obj.transform.localRotation = rotation;
            obj.transform.localScale = scale;
            Collider c = obj.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.Destroy(c);
            obj.GetComponent<Renderer>().sharedMaterial = material;
            return obj;
        }

        private static void RigidLine(Transform parent, string name, Vector3 a, Vector3 b,
            float diameter, Material material)
        {
            Vector3 offset = b - a;
            if (offset.sqrMagnitude < 0.0001f) return;
            Part(parent, name, PrimitiveType.Cylinder,
                (a + b) * 0.5f, Quaternion.FromToRotation(Vector3.up, offset),
                new Vector3(diameter, offset.magnitude * 0.5f, diameter), material);
        }

        // One lightweight three-blade rotor mesh is shared by all four propellers.
        // Unlike 8 separate cube primitives it requires no generated colliders
        // or per-blade GameObjects during a mobile match.
        private static Mesh BuildPropellerMesh()
        {
            if (cachedPropellerMesh != null) return cachedPropellerMesh;
            var vertices = new List<Vector3>(36);
            var triangles = new List<int>(72);
            for (int blade = 0; blade < 3; blade++)
            {
                float theta = blade * (Mathf.PI * 2f / 3f);
                Vector2 direction = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));
                Vector2 side = new Vector2(-direction.y, direction.x);
                float[] radii = { .24f, 1.02f, 1.66f };
                float[] widths = { .22f, .19f, .055f };
                int first = vertices.Count;
                for (int row = 0; row < radii.Length; row++)
                {
                    Vector2 middle = direction * radii[row];
                    Vector2 left = middle - side * widths[row];
                    Vector2 right = middle + side * widths[row];
                    vertices.Add(new Vector3(left.x, left.y, .027f));
                    vertices.Add(new Vector3(right.x, right.y, .027f));
                }
                for (int row = 0; row < radii.Length - 1; row++)
                {
                    int a = first + row * 2;
                    int b = a + 1;
                    int c = a + 2;
                    int d = c + 1;
                    triangles.Add(a); triangles.Add(b); triangles.Add(c);
                    triangles.Add(c); triangles.Add(b); triangles.Add(d);
                    triangles.Add(a); triangles.Add(c); triangles.Add(b);
                    triangles.Add(c); triangles.Add(d); triangles.Add(b);
                }
            }
            // The rotor cap is part of the same mesh, not another draw call.
            int cap = vertices.Count;
            vertices.Add(new Vector3(0f, 0f, .075f));
            for (int i = 0; i < 12; i++)
            {
                float angle = i * Mathf.PI / 6f;
                vertices.Add(new Vector3(Mathf.Cos(angle) * .27f,
                    Mathf.Sin(angle) * .27f, .06f));
            }
            for (int i = 0; i < 12; i++)
            {
                triangles.Add(cap);
                triangles.Add(cap + 1 + i);
                triangles.Add(cap + 1 + (i + 1) % 12);
            }
            cachedPropellerMesh = new Mesh { name = "AarisSharedTriBladeRotor" };
            cachedPropellerMesh.SetVertices(vertices);
            cachedPropellerMesh.SetTriangles(triangles, 0);
            cachedPropellerMesh.RecalculateNormals();
            cachedPropellerMesh.RecalculateBounds();
            return cachedPropellerMesh;
        }

        internal static GameObject CreatePlane(Vector3 position, Quaternion rotation,
            out Transform[] propellers)
        {
            var prefab = Resources.Load<GameObject>("AarisDrop/CargoPlane");
            GameObject root;
            if (prefab != null)
            {
                root = UnityEngine.Object.Instantiate(prefab, position, rotation);
                // Prefer the five serialized URP/Lit aircraft finishes. Unlike
                // flat procedural paints, these preserve authored PBR surface
                // properties and any future texture maps without per-frame work.
                Material hullPaint = SavedAircraftFinish("CargoPlane-HulledMetal",
                    ref hull, new Color(.31f, .36f, .35f), .38f, .32f);
                Material cockpitPaint = SavedAircraftFinish("CargoPlane-DarkCockpit",
                    ref dark, new Color(.055f, .10f, .13f), .08f, .82f);
                Material wingPaint = SavedAircraftFinish("CargoPlane-WingPanels",
                    ref wingPanels, new Color(.39f, .43f, .40f), .30f, .25f);
                Material enginePaint = SavedAircraftFinish("CargoPlane-EngineDark",
                    ref engineDark, new Color(.095f, .12f, .13f), .52f, .26f);
                Material safetyPaint = SavedAircraftFinish("CargoPlane-SafetyAccent",
                    ref safetyAccent, new Color(.60f, .60f, .49f), .18f, .27f);
                // Imported OBJ/MTL materials are not guaranteed to be URP-compatible.
                // Map each authored submesh to one cached, shared URP material.
                // Preserve wing/cockpit/intake contrast instead of flattening
                // the entire aircraft to one cartoon-like hull colour.
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var slots = renderer.sharedMaterials;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        string label = slots[i] != null ? slots[i].name : "";
                        if (label.IndexOf("Cockpit", StringComparison.OrdinalIgnoreCase) >= 0)
                            slots[i] = cockpitPaint;
                        else if (label.IndexOf("WingPanels", StringComparison.OrdinalIgnoreCase) >= 0)
                            slots[i] = wingPaint;
                        else if (label.IndexOf("EngineDark", StringComparison.OrdinalIgnoreCase) >= 0)
                            slots[i] = enginePaint;
                        else if (label.IndexOf("SafetyAccent", StringComparison.OrdinalIgnoreCase) >= 0)
                            slots[i] = safetyPaint;
                        // Some mobile OBJ import paths discard the MTL names but
                        // retain the five source submesh slots in OBJ declaration
                        // order. Preserve their contrast even in this case.
                        else if (slots.Length == 5)
                            slots[i] = i == 1 ? cockpitPaint :
                                i == 2 ? wingPaint :
                                i == 3 ? enginePaint :
                                i == 4 ? safetyPaint : hullPaint;
                        else slots[i] = hullPaint;
                    }
                    renderer.sharedMaterials = slots;
                }
            }
            else
            {
                Debug.LogError("AARIS_CARGO_PLANE_MODEL_NOT_IMPORTED: using emergency offline placeholder.");
                root = new GameObject("Aaris_OriginalCargoAircraft");
                root.transform.SetPositionAndRotation(position, rotation);
                Material shell = Paint(ref hull, new Color(0.36f, 0.44f, 0.47f));
                Material trim = Paint(ref dark, new Color(0.09f, 0.14f, 0.17f));
                SetAircraftFinish(shell, .38f, .32f);
                SetAircraftFinish(trim, .08f, .82f);
                Part(root.transform, "Fuselage", PrimitiveType.Capsule, Vector3.zero,
                    Quaternion.Euler(90f, 0f, 0f),
                    new Vector3(3.1f, 10.6f, 3.1f), shell);
                Part(root.transform, "Nose", PrimitiveType.Sphere,
                    new Vector3(0f, -0.1f, 8.7f), Quaternion.identity,
                    new Vector3(2.95f, 2.9f, 4.4f), shell);
                Part(root.transform, "Cockpit", PrimitiveType.Sphere,
                    new Vector3(0f, 1.0f, 7.1f), Quaternion.identity,
                    new Vector3(2.2f, 0.92f, 2.45f), trim);
                Part(root.transform, "MainWing", PrimitiveType.Cube,
                    new Vector3(0f, 1.35f, 1.4f), Quaternion.identity,
                    new Vector3(26f, 0.34f, 4.2f), shell);
                Part(root.transform, "TailWing", PrimitiveType.Cube,
                    new Vector3(0f, 1.1f, -8.7f), Quaternion.identity,
                    new Vector3(10f, 0.28f, 2.65f), shell);
                Part(root.transform, "TailFin", PrimitiveType.Cube,
                    new Vector3(0f, 3.3f, -8.7f), Quaternion.Euler(-10f, 0f, 0f),
                    new Vector3(0.27f, 4.6f, 2.9f), shell);
                float[] xs = { -8.7f, -4.6f, 4.6f, 8.7f };
                for (int i = 0; i < xs.Length; i++)
                {
                    Part(root.transform, "EngineNacelle" + i, PrimitiveType.Capsule,
                        new Vector3(xs[i], 0.6f, 1.7f), Quaternion.Euler(90f, 0f, 0f),
                        new Vector3(1.5f, 1.8f, 1.5f), trim);
                }
            }

            // The three-blade propeller disk sits FORWARD of each engine/wing.
            // A shared mesh and four pivots replace eight rectangular cube blades.
            float[] offsets = { -8.65f, -4.55f, 4.55f, 8.65f };
            propellers = new Transform[offsets.Length];
            Material bladeMaterial = Paint(ref dark, new Color(.055f, .08f, .085f));
            Mesh rotor = BuildPropellerMesh();
            for (int i = 0; i < offsets.Length; i++)
            {
                GameObject hub = new GameObject("PropellerPivot" + i,
                    typeof(MeshFilter), typeof(MeshRenderer));
                hub.transform.SetParent(root.transform, false);
                hub.transform.localPosition = new Vector3(offsets[i], .46f, 4.37f);
                hub.GetComponent<MeshFilter>().sharedMesh = rotor;
                hub.GetComponent<MeshRenderer>().sharedMaterial = bladeMaterial;
                propellers[i] = hub.transform;
            }
            root.name = "Aaris_CargoPlane";
            return root;
        }

        /// <summary>
        /// Build a long, tapered, arched and elliptical ram-air canopy (NOT square).
        /// Only the visual transforms bank; the player's collider stays vertical.
        /// The shape is seven curved double-sided colour panels with rope/harness rig.
        /// </summary>
        internal static GameObject CreateElongatedCanopy(Transform player)
        {
            var root = new GameObject("Aaris_ElongatedCanopy");
            root.transform.SetParent(player, false);
            const float harnessHeight = 1.55f;
            root.transform.localPosition = Vector3.up * harnessHeight;
            var rig = new GameObject("Canopy and suspension").transform;
            rig.SetParent(root.transform, false);
            rig.localPosition = Vector3.down * harnessHeight;

            var imported = Resources.Load<GameObject>("AarisDrop/ElongatedParachute");
            if (imported != null)
            {
                var model = UnityEngine.Object.Instantiate(imported, rig, false);
                model.name = "LocalCanopyArt";
                // The OBJ is centered on the wing. Lift it to the same height as the procedural rig.
                model.transform.localPosition = Vector3.up * 4.25f;
                model.transform.localScale = Vector3.one;
                Material navy = Paint(ref canopyBlue, new Color(0.05f, 0.23f, 0.39f));
                Material ivory = Paint(ref canopyWhite, new Color(0.91f, 0.91f, 0.83f));
                Material orange = Paint(ref canopyOrange, new Color(0.97f, 0.45f, 0.13f));
                foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
                {
                    var slots = renderer.sharedMaterials;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        string label = slots[i] != null ? slots[i].name : "";
                        slots[i] = label.IndexOf("Orange", StringComparison.OrdinalIgnoreCase) >= 0
                            ? orange : label.IndexOf("White", StringComparison.OrdinalIgnoreCase) >= 0
                            ? ivory : navy;
                    }
                    renderer.sharedMaterials = slots;
                }

                // An imported OBJ's pivot is not guaranteed to coincide with
                // its visible wing. Align its actual renderer bounds with the
                // known suspension rig; otherwise some Android imports put
                // the canopy underneath the player, making him appear to
                // stand ON the parachute. This is one bounds pass per drop,
                // never a per-frame mesh modification or scene change.
                var wings = model.GetComponentsInChildren<Renderer>(true);
                Bounds wingBounds = new Bounds();
                bool foundWing = false;
                foreach (var wing in wings)
                {
                    if (wing == null) continue;
                    if (!foundWing) { wingBounds = wing.bounds; foundWing = true; }
                    else wingBounds.Encapsulate(wing.bounds);
                }
                if (foundWing && wingBounds.size.sqrMagnitude > 0.01f)
                {
                    Vector3 target = rig.TransformPoint(new Vector3(0f, 4.35f, 0f));
                    model.transform.position += target - wingBounds.center;
                }
                // Keep locally generated suspension lines attached to the harness.
            }
            else
            {
                Material[] paints = {
                    Paint(ref canopyBlue, new Color(0.05f, 0.23f, 0.39f)),
                    Paint(ref canopyWhite, new Color(0.91f, 0.91f, 0.83f)),
                    Paint(ref canopyOrange, new Color(0.97f, 0.45f, 0.13f))
                };
                for (int panel = 0; panel < 7; panel++)
                {
                    var segment = new GameObject("CurvedCanopyPanel_" + panel);
                    segment.transform.SetParent(rig, false);
                    var filter = segment.AddComponent<MeshFilter>();
                    filter.sharedMesh = PanelMesh(panel, 7);
                    var renderer = segment.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = paints[panel == 3 ? 2 : panel % 2];
                }
            }

            Material cords = Paint(ref rope, new Color(0.09f, 0.12f, 0.15f));
            for (int i = 0; i < 9; i++)
            {
                float s = Mathf.Lerp(-0.91f, 0.91f, i / 8f);
                Vector3 edge = CanopyPoint(s, 0.13f);
                Vector3 harness = new Vector3(s < 0f ? -0.23f : 0.23f, 1.55f, 0.12f);
                RigidLine(rig, "LeftSuspension_" + i, edge, harness, 0.011f, cords);
                if (i % 2 == 0)
                    RigidLine(rig, "RearSuspension_" + i,
                        CanopyPoint(s, 0.88f), harness + Vector3.back * 0.2f, 0.009f, cords);
            }
            return root;
        }

        private static Vector3 CanopyPoint(float lateral, float chord)
        {
            // Taper smoothly at the wing tips, giving the long elliptical Free Fire-style outline.
            float s = Mathf.Clamp(lateral, -1f, 1f);
            float spread = Mathf.Sqrt(Mathf.Max(0.01f, 1f - s * s));
            float x = s * 4.55f;
            float y = 4.55f - 1.23f * Mathf.Pow(Mathf.Abs(s), 1.62f)
                + 0.31f * Mathf.Sin(chord * Mathf.PI);
            float z = (chord - 0.5f) * 2.65f * spread;
            return new Vector3(x, y, z);
        }

        private static Mesh PanelMesh(int panel, int count)
        {
            const int along = 6, across = 8;
            var points = new Vector3[(along + 1) * (across + 1) * 2];
            var uv = new Vector2[points.Length];
            int rowSize = (along + 1) * (across + 1);
            for (int side = 0; side < 2; side++)
            for (int i = 0; i <= along; i++)
            for (int j = 0; j <= across; j++)
            {
                float s = Mathf.Lerp(-1f, 1f, (panel + i / (float)along) / count);
                float chord = j / (float)across;
                int idx = side * rowSize + i * (across + 1) + j;
                points[idx] = CanopyPoint(s, chord) + Vector3.up * (side == 0 ? 0.012f : -0.012f);
                uv[idx] = new Vector2((s + 1f) * 0.5f, chord);
            }
            var triangles = new int[along * across * 12];
            int t = 0;
            for (int side = 0; side < 2; side++)
            for (int i = 0; i < along; i++)
            for (int j = 0; j < across; j++)
            {
                int a = side * rowSize + i * (across + 1) + j;
                int b = a + across + 1;
                int c = a + 1;
                int d = b + 1;
                if (side == 0)
                {
                    triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
                    triangles[t++] = c; triangles[t++] = b; triangles[t++] = d;
                }
                else
                {
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = c; triangles[t++] = d; triangles[t++] = b;
                }
            }
            var mesh = new Mesh { name = "AarisOriginal_ElongatedPanel_" + panel };
            mesh.vertices = points;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
