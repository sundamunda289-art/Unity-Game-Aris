using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Reproduce the exact GT width/rolling-plane fit with real imported
    /// URP prefab and native WheelColliders. Does not simulate road driving.
    /// </summary>
    public static class AarisRoadGtScaleQA
    {
        private static bool Visible(GameObject car, out Bounds combined)
        {
            combined = new Bounds();
            bool found = false;
            foreach (var renderer in car.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled || renderer.forceRenderingOff ||
                    !renderer.gameObject.activeInHierarchy) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null ||
                    filter.sharedMesh.vertexCount == 0) continue;
                if (!found) { combined = renderer.bounds; found = true; }
                else combined.Encapsulate(renderer.bounds);
            }
            return found;
        }

        [MenuItem("Aaris/CI/Verify balanced Premium GT wheel and body scale")]
        public static void Verify()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var prefab = Resources.Load<GameObject>(
                "AarisVehicles/PremiumRoadGT/AarisRoadGT");
            var json = Resources.Load<TextAsset>(
                "AarisVehicles/vehicle_catalog");
            if (prefab == null || json == null)
                throw new InvalidOperationException("Premium GT assets unavailable");
            var catalog = JsonUtility.FromJson<AarisVehicleStudioCatalog>(json.text);
            AarisVehicleStudioRecord gt = Array.Find(catalog.vehicles,
                item => item != null && item.id == "GT");
            if (gt == null) throw new InvalidOperationException("GT catalog absent");
            var root = new GameObject("Temporary GT geometry and real physics QA");
            try
            {
                var model = UnityEngine.Object.Instantiate(prefab, root.transform, false);
                if (!Visible(root,
                    out Bounds before))
                    throw new InvalidOperationException("GT import has no geometry");
                float width = Mathf.Clamp(gt.referenceWidthMetres, 1.65f, 2.15f);
                float length = Mathf.Clamp(gt.referenceLengthMetres, 3.35f, 5.50f);
                float lateral = Mathf.Clamp(width / before.size.x, .01f, 50f);
                float rolling = Mathf.Clamp(length / before.size.z, .01f, 50f);
                model.transform.localScale = Vector3.Scale(model.transform.localScale,
                    new Vector3(lateral, rolling, rolling));
                if (!Visible(root,
                    out Bounds measured))
                    throw new InvalidOperationException("GT scaled mesh missing");
                if (Mathf.Abs(measured.size.x - width) > .03f ||
                    Mathf.Abs(measured.size.z - length) > .03f ||
                    measured.size.y < 1.06f || measured.size.y > 1.36f)
                    throw new InvalidOperationException("GT visual metric mismatch " +
                        measured.size.ToString("F3"));
                model.transform.localPosition += new Vector3(0,.48f,0)-measured.center;
                if (!Visible(root,
                    out Bounds chassisBox))
                    throw new InvalidOperationException("GT recentered mesh missing");
                var motor = root.AddComponent<AarisVehicleMotor>();
                motor.Configure("GT", gt.maxSpeedMetres, gt.massKg);
                motor.ConfigureHandling(gt.drivetrain, gt.tyreGripScale,
                    gt.suspensionScale, gt.steeringScale, gt.suspensionTravelScale);
                motor.FitShellAndWheelbase(chassisBox);
                if (!motor.AttachEmbeddedWheelModels(model.transform))
                    throw new InvalidOperationException("GT four wheel attachment failed");
                motor.ConfigureBodyDynamics(model.transform);
                var wheels = root.GetComponentsInChildren<WheelCollider>();
                if (wheels.Length != 4 ||
                    root.GetComponent<BoxCollider>() == null ||
                    root.GetComponent<Rigidbody>() == null)
                    throw new InvalidOperationException("GT chassis physics invalid");
                foreach (var wheel in wheels)
                {
                    if (wheel.radius < .29f || wheel.radius > .43f)
                        throw new InvalidOperationException("GT wheel radius out of range " +
                            wheel.radius);
                }
                Debug.Log("AARIS_ROAD_GT_METRIC_PASS width=" +
                    measured.size.x.ToString("F3") + " height=" +
                    measured.size.y.ToString("F3") + " length=" +
                    measured.size.z.ToString("F3") +
                    " four-wheel-physics=PASS");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
