using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Aaris.MapReview
{
    // One shared wheelhouse mesh per body archetype. Geometry is calculated
    // in chassis space at build time and has no colliders or Update callbacks.
    internal sealed class AarisVehicleWheelhouseKit : IDisposable
    {
        private readonly Dictionary<GameObject, Mesh> layouts = new();
        private readonly Material[] materials;

        internal AarisVehicleWheelhouseKit(Shader shader)
        {
            materials = new[]
            {
                Finish(shader, "Wheel tub / textured rubber", new Color(.035f, .039f, .044f), 0f, .23f),
                Finish(shader, "Stamped steel / brake hardware", new Color(.21f, .22f, .24f), .69f, .49f)
            };
        }

        private static Material Finish(Shader shader, string label, Color tint,
            float metallic, float smoothness)
        {
            var material = new Material(shader) { name = label, enableInstancing = true };
            material.SetColor("_BaseColor", tint);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            return material;
        }

        internal void Attach(GameObject source, Transform body, WheelCollider[] wheels,
            float tyreWidth, bool needsRotor)
        {
            if (!layouts.TryGetValue(source, out Mesh mesh))
            {
                mesh = Build(wheels, Mathf.Clamp(tyreWidth, .16f, .38f), needsRotor);
                layouts.Add(source, mesh);
            }
            var detail = new GameObject("Wheelhouse liners and brake hardware");
            detail.transform.SetParent(body, false);
            detail.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = detail.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        private static Mesh Build(WheelCollider[] wheels, float tyreWidth, bool needsRotor)
        {
            var positions = new List<Vector3>(320);
            var liner = new List<int>(384);
            var hardware = new List<int>(400);
            foreach (var wheel in wheels)
            {
                if (wheel == null) continue;
                Vector3 centre = wheel.transform.localPosition + wheel.center;
                float side = Mathf.Sign(centre.x);
                float inner = centre.x - side * (tyreWidth * .65f + .055f);
                float outer = centre.x - side * (tyreWidth * .16f);
                float radius = wheel.radius + .023f;
                // Roll the arch over the upper half of the tread and close
                // its inner face behind the tyre. Keep the lip recessed for
                // steering clearance, with no extra material passes.
                const int segments = 12;
                for (int step = 0; step < segments; step++)
                {
                    float a = Mathf.Lerp(-.15f, Mathf.PI + .15f, step / (float)segments);
                    float b = Mathf.Lerp(-.15f, Mathf.PI + .15f, (step + 1f) / segments);
                    Vector3 start = centre + new Vector3(0f, Mathf.Sin(a), Mathf.Cos(a)) * radius;
                    Vector3 end = centre + new Vector3(0f, Mathf.Sin(b), Mathf.Cos(b)) * radius;
                    Vector3 innerA = new Vector3(inner, start.y, start.z);
                    Vector3 innerB = new Vector3(inner, end.y, end.z);
                    Vector3 outerA = new Vector3(outer, start.y, start.z);
                    Vector3 outerB = new Vector3(outer, end.y, end.z);
                    Panel(positions, liner, innerA, innerB, outerB, outerA, side < 0f);
                    Triangle(positions, liner,
                        new Vector3(inner, centre.y - .09f, centre.z), innerB, innerA,
                        side < 0f);
                }

                Vector3 bearing = centre - Vector3.right * side * tyreWidth * .44f;
                Vector3 crossmember = bearing - new Vector3(side * .32f, .085f, 0f);
                Arm(positions, hardware, crossmember + Vector3.forward * .21f, bearing);
                Arm(positions, hardware, crossmember - Vector3.forward * .19f, bearing);
                Vector3 face = centre - Vector3.right * side * tyreWidth * .18f;
                face.y -= wheel.suspensionDistance * .52f;
                if (needsRotor)
                    Disc(positions, hardware, face, wheel.radius * .56f, side);
                Vector3 caliper = face + new Vector3(side * .008f, .075f, -wheel.radius * .52f);
                Arm(positions, hardware, caliper - Vector3.up * .049f,
                    caliper + Vector3.up * .049f, .026f);
            }

            var mesh = new Mesh { name = "Aaris shared wheelhouse trim" };
            mesh.SetVertices(positions);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(liner, 0);
            mesh.SetTriangles(hardware, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static void Disc(List<Vector3> positions, List<int> indices, Vector3 center,
            float radius, float side)
        {
            const int segments = 18;
            for (int i = 0; i < segments; i++)
            {
                float a = i * (2f * Mathf.PI / segments);
                float b = (i + 1) * (2f * Mathf.PI / segments);
                var start = new Vector3(0f, Mathf.Sin(a), Mathf.Cos(a)) * radius;
                var end = new Vector3(0f, Mathf.Sin(b), Mathf.Cos(b)) * radius;
                Panel(positions, indices, center + start * .48f, center + start,
                    center + end, center + end * .48f, side > 0f);
            }
        }

        private static void Arm(List<Vector3> positions, List<int> indices, Vector3 a,
            Vector3 b, float halfWidth = .018f)
        {
            Vector3 direction = (b - a).normalized;
            Vector3 across = Vector3.Cross(direction, Vector3.forward).normalized * halfWidth;
            Vector3 up = Vector3.Cross(direction, across);
            Panel(positions, indices, a + across, b + across, b + up, a + up);
            Panel(positions, indices, a + up, b + up, b - across, a - across);
            Panel(positions, indices, a - across, b - across, b - up, a - up);
            Panel(positions, indices, a - up, b - up, b + across, a + across);
        }

        private static void Panel(List<Vector3> positions, List<int> indices, Vector3 a,
            Vector3 b, Vector3 c, Vector3 d, bool reverse = false)
        {
            Triangle(positions, indices, a, b, c, reverse);
            Triangle(positions, indices, a, c, d, reverse);
        }

        private static void Triangle(List<Vector3> positions, List<int> indices,
            Vector3 a, Vector3 b, Vector3 c, bool reverse)
        {
            int start = positions.Count;
            positions.Add(a);
            positions.Add(reverse ? c : b);
            positions.Add(reverse ? b : c);
            indices.Add(start);
            indices.Add(start + 1);
            indices.Add(start + 2);
        }

        public void Dispose()
        {
            foreach (var mesh in layouts.Values) Object.Destroy(mesh);
            layouts.Clear();
            foreach (var material in materials) Object.Destroy(material);
        }
    }
}
