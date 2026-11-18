using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    /// <summary>Checks actual Resources meshes and metric proportions in the
    /// Unity importer. It does not claim Android Play Mode FPS.</summary>
    public static class AarisVehicleMetricFitQA
    {
        private static Bounds RealVisibleBounds(GameObject root)
        {
            Bounds box = new Bounds();
            bool found = false;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled || renderer.forceRenderingOff ||
                    !renderer.gameObject.activeInHierarchy) continue;
                MeshFilter mesh = renderer.GetComponent<MeshFilter>();
                if (mesh == null || mesh.sharedMesh == null ||
                    mesh.sharedMesh.vertexCount < 3) continue;
                if (!found) { box = renderer.bounds; found = true; }
                else box.Encapsulate(renderer.bounds);
            }
            if (!found) throw new InvalidOperationException(
                "Missing rendered car mesh: " + root.name);
            return box;
        }

        private static Transform NamedWheel(GameObject model, string token)
        {
            foreach (var tr in model.GetComponentsInChildren<Transform>(true))
                if (tr.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return tr;
            return null;
        }

        [MenuItem("Aaris/CI/Verify all vehicle class metre scale and axle positions")]
        public static void Verify()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string[] ids = { "Sedan", "SUV", "Sports", "GT",
                "Hatchback", "Van", "Utility", "Buggy" };
            string[] sources = {
                "AarisVehicles/Authored/CivilianSedan",
                "AarisVehicles/Authored/1990s-suv",
                "AarisVehicles/Authored/1990s-sports-car",
                "AarisVehicles/PremiumRoadGT/AarisRoadGT",
                "AarisVehicles/Authored/1990s-hatchback",
                "AarisVehicles/Authored/1980s-van",
                "AarisVehicles/Authored/1990s-pickup-truck",
                "AarisVehicles/Authored/AarisBuggy"
            };
            int reoriented = 0, testedWheels = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                GameObject prefab = Resources.Load<GameObject>(sources[i]);
                if (prefab == null)
                    throw new InvalidOperationException("Car resource unavailable: " + ids[i]);
                GameObject car = UnityEngine.Object.Instantiate(prefab);
                try
                {
                    Bounds source = RealVisibleBounds(car);
                    Quaternion axis = AarisVehicleVisualCalibration.CorrectSourceAxes(source.size);
                    if (axis != Quaternion.identity)
                    {
                        car.transform.rotation = axis * car.transform.rotation;
                        reoriented++;
                    }
                    Bounds fitted = RealVisibleBounds(car);
                    if (fitted.size.x > fitted.size.z * 1.1f)
                        car.transform.rotation = Quaternion.Euler(0f, 90f, 0f) *
                            car.transform.rotation;
                    fitted = RealVisibleBounds(car);
                    // Same axes, scale calculation and authored wheel names as Build().
                    Vector3 axes = AarisVehicleVisualCalibration.FittedScale(
                        ids[i], fitted.size);
                    car.transform.localScale = Vector3.Scale(car.transform.localScale, axes);
                    fitted = RealVisibleBounds(car);
                    Vector2 max = AarisVehicleVisualCalibration.FootprintMetres(ids[i]);
                    if (fitted.size.x > max.x + .02f ||
                        fitted.size.z > max.y + .02f ||
                        fitted.size.x < 1.65f || fitted.size.z < 3.25f ||
                        fitted.size.y < 1.05f || fitted.size.y > 2.10f)
                        throw new InvalidOperationException("Unreal car dimensions: " +
                            ids[i] + " " + fitted.size + " target " + max);
                    Transform fl = NamedWheel(car, "wheel_fl");
                    Transform fr = NamedWheel(car, "wheel_fr");
                    Transform rl = NamedWheel(car, "wheel_rl");
                    Transform rr = NamedWheel(car, "wheel_rr");
                    if (fl != null || fr != null || rl != null || rr != null)
                    {
                        if (fl == null || fr == null || rl == null || rr == null)
                            throw new InvalidOperationException("Partial authored wheels: " + ids[i]);
                        float front = (fl.position.z + fr.position.z) * .5f;
                        float rear = (rl.position.z + rr.position.z) * .5f;
                        float xLeft = (fl.position.x + rl.position.x) * .5f;
                        float xRight = (fr.position.x + rr.position.x) * .5f;
                        if (front - rear < 1.8f || xRight - xLeft < 1.25f ||
                            Mathf.Abs(fl.position.z - fr.position.z) > .10f ||
                            Mathf.Abs(rl.position.z - rr.position.z) > .10f ||
                            Mathf.Abs(fl.position.y - fr.position.y) > .08f)
                            throw new InvalidOperationException("Misaligned axles: " +
                                ids[i] + " left=" + xLeft + " right=" + xRight +
                                " front=" + front + " rear=" + rear);
                        testedWheels++;
                    }
                    Debug.Log("AARIS_METRIC_FIT " + ids[i] +
                        " width=" + fitted.size.x.ToString("F3") +
                        " height=" + fitted.size.y.ToString("F3") +
                        " length=" + fitted.size.z.ToString("F3") +
                        " sourceAxisFixed=" + (axis != Quaternion.identity));
                }
                finally { UnityEngine.Object.DestroyImmediate(car); }
            }
            // Some Unity model importers pre-convert source axes; others
            // leave the vendor FBX Z-up. Both are valid when final metre
            // proportions and all six independent wheel sets pass.
            if (testedWheels != 6)
                throw new InvalidOperationException("Missing imported source QA " +
                    reoriented + " " + testedWheels);
            Debug.Log("AARIS_MAP_VEHICLE_METRIC_PASS eight=8 axisFixed=" +
                reoriented + " wheelSets=" + testedWheels);
        }
    }
}
