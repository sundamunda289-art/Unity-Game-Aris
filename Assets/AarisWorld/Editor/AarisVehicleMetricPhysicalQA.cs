using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Real native WheelCollider + visual-shell metric smoke test for all
    /// eight car types. Does not simulate sustained Android gameplay.
    /// </summary>
    public static class AarisVehicleMetricPhysicalQA
    {
        private static Bounds Visible(GameObject obj)
        {
            bool found = false;
            var bounds = new Bounds();
            foreach (var r in obj.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = r.GetComponent<MeshFilter>();
                if (!r.enabled || r.forceRenderingOff ||
                    !r.gameObject.activeInHierarchy ||
                    mesh == null || mesh.sharedMesh == null ||
                    mesh.sharedMesh.vertexCount == 0) continue;
                if (!found) { bounds = r.bounds; found = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (!found) throw new InvalidOperationException("Invisible car " + obj.name);
            return bounds;
        }

        [MenuItem("Aaris/CI/Verify all fitted car shells and WheelColliders")]
        public static void Verify()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string[] names = {"Sedan","SUV","Sports","GT","Hatchback","Van","Utility","Buggy"};
            string[] paths = {
                "AarisVehicles/Authored/CivilianSedan",
                "AarisVehicles/Authored/1990s-suv",
                "AarisVehicles/Authored/1990s-sports-car",
                "AarisVehicles/PremiumRoadGT/AarisRoadGT",
                "AarisVehicles/Authored/1990s-hatchback",
                "AarisVehicles/Authored/1980s-van",
                "AarisVehicles/Authored/1990s-pickup-truck",
                "AarisVehicles/Authored/AarisBuggy"
            };
            string[] wheelPaths = {
                "AarisVehicles/Models/wheel-default",
                "AarisVehicles/Models/wheel-default",
                "AarisVehicles/Models/wheel-racing",
                "AarisVehicles/Models/wheel-racing",
                "AarisVehicles/Models/wheel-racing",
                "AarisVehicles/Models/wheel-truck",
                "AarisVehicles/Models/wheel-truck",
                "AarisVehicles/Models/wheel-truck"
            };
            Material wheelMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                for (int i = 0; i < names.Length; i++)
                {
                    var source = Resources.Load<GameObject>(paths[i]);
                    if (source == null) throw new InvalidOperationException("Missing "+paths[i]);
                    var root = new GameObject("Metric fitted "+names[i]);
                    try
                    {
                        var visual = UnityEngine.Object.Instantiate(source, root.transform, false);
                        var bounds = Visible(visual);
                        var fix = AarisVehicleVisualCalibration.CorrectSourceAxes(bounds.size);
                        visual.transform.localRotation = fix * visual.transform.localRotation;
                        bounds = Visible(visual);
                        if (bounds.size.x > bounds.size.z * 1.1f)
                            visual.transform.localRotation =
                                Quaternion.Euler(0,90,0)*visual.transform.localRotation;
                        bounds = Visible(visual);
                        visual.transform.localScale = Vector3.Scale(
                            visual.transform.localScale,
                            AarisVehicleVisualCalibration.FittedScale(names[i], bounds.size));
                        bounds = Visible(visual);
                        visual.transform.localPosition +=
                            new Vector3(0f,.48f,0f)-bounds.center;
                        var motor = root.AddComponent<AarisVehicleMotor>();
                        motor.Configure(names[i], 24f, 1250f);
                        motor.FitShellAndWheelbase(
                            new Bounds(new Vector3(0f,.48f,0f),bounds.size));
                        bool embedded = i>0 && i<7;
                        bool attached = embedded
                            ? motor.AttachEmbeddedWheelModels(visual.transform)
                            : motor.AttachWheelModels(
                                Resources.Load<GameObject>(wheelPaths[i]),wheelMat);
                        if (!attached)
                            throw new InvalidOperationException("Wheel visual attachment failed "+names[i]);
                        motor.ConfigureBodyDynamics(visual.transform);
                        var shell = root.GetComponent<BoxCollider>();
                        var wheels = root.GetComponentsInChildren<WheelCollider>();
                        if (shell == null || wheels.Length != 4 ||
                            shell.size.z < bounds.size.z * .90f)
                            throw new InvalidOperationException("Short shell or lost wheels "+names[i]);
                        float frontZ = (wheels[0].transform.localPosition.z +
                                        wheels[1].transform.localPosition.z)*.5f;
                        float rearZ = (wheels[2].transform.localPosition.z +
                                       wheels[3].transform.localPosition.z)*.5f;
                        if (frontZ <= rearZ ||
                            wheels[0].transform.localPosition.x >= 0 ||
                            wheels[1].transform.localPosition.x <= 0 ||
                            wheels[2].transform.localPosition.x >= 0 ||
                            wheels[3].transform.localPosition.x <= 0)
                            throw new InvalidOperationException("Axles reversed "+names[i]);
                        foreach (var wheel in wheels)
                            if (wheel.radius < .27f || wheel.radius > .46f)
                                throw new InvalidOperationException("Invalid tyre radius "+names[i]);
                        Debug.Log("AARIS_METRIC_PHYSICS "+names[i]+
                            " shellZ="+shell.size.z.ToString("F3")+
                            " visualZ="+bounds.size.z.ToString("F3")+
                            " wheelbase="+(frontZ-rearZ).ToString("F3")+
                            " embedded="+embedded);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(root); }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(wheelMat); }
            Debug.Log("AARIS_ALL_VEHICLE_METRIC_PHYSICS_PASS count=8");
        }
    }
}
