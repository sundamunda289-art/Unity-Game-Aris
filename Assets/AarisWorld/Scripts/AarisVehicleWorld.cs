using System;
using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    [Serializable]
    internal sealed class AarisVehiclePoint
    {
        public string id, kind, district, origin;
        public float x, y, z, yaw;
    }

    [Serializable]
    internal sealed class AarisVehiclePointsFile
    {
        public int schema, sizeMetres, hubCount, roadsideCount;
        public AarisVehiclePoint[] candidates;
    }

    [Serializable]
    internal sealed class AarisVehicleType
    {
        public string id, modelResource, wheelResource;
        public int weight;
        public float maxSpeedMetres, massKg;
    }

    [Serializable]
    internal sealed class AarisVehicleTypesFile
    {
        public AarisVehicleType[] vehicles;
    }

    internal sealed class AarisVehicleRecord
    {
        public AarisVehiclePoint point;
        public AarisVehicleType type;
        public Vector3 position;
        public Quaternion rotation;
        public AarisVehicleMotor visible;
        public float nextProbe;
        public bool previouslySpawned;
    }

    /// <summary>
    /// Match-seeded vehicles. The pre-baked 45 garage nodes + reviewed
    /// roadside candidates are deliberately chosen across 500m sectors.
    /// Collider checks occur ONLY close to the player, while the existing
    /// AarisNearbyColliders has activated nearby buildings and bridges.
    /// </summary>
    [DefaultExecutionOrder(-70), DisallowMultipleComponent]
    public sealed class AarisVehicleWorld : MonoBehaviour
    {
        private const float LoadRadius = 42f;
        private const float UnloadRadius = 160f;
        private const float SeaLevel = -38.07552f;
        private const int TargetVehicles = 26;
        private const int MaximumNearbyVehicles = 10;
        private readonly List<AarisVehicleRecord> plans = new List<AarisVehicleRecord>(TargetVehicles);
        private readonly RaycastHit[] rayHits = new RaycastHit[24];
        private readonly Collider[] collisionHits = new Collider[40];
        private Transform player;
        private float nextPoll;
        private uint randomState;
        private AarisVehicleType[] vehicleTypes;
        private GameObject[] prefabs;
        private GameObject[] wheelPrefabs;
        private int validModels;
        private Material paletteMaterial;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (FindFirstObjectByType<AarisVehicleWorld>() != null) return;
            var motor = FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (motor == null) return;
            var obj = new GameObject("Aaris 2km Match Vehicles (streamed)");
            var world = obj.AddComponent<AarisVehicleWorld>();
            world.player = motor.transform;
            if (!world.Setup()) { Destroy(obj); return; }
            var session = motor.GetComponent<AarisVehicleSession>();
            if (session == null) session = motor.gameObject.AddComponent<AarisVehicleSession>();
            session.Configure(world, motor);
            var hudObject = new GameObject("Aaris Four Arrow Drive HUD");
            var hud = hudObject.AddComponent<AarisVehicleHUD>();
            hud.Configure(session);
        }

        private bool Setup()
        {
            TextAsset points = Resources.Load<TextAsset>("AarisVehicles/vehicle_candidates");
            TextAsset catalog = Resources.Load<TextAsset>("AarisVehicles/vehicle_catalog");
            if (points == null || catalog == null)
            {
                Debug.LogError("Aaris vehicles: authored offline candidates/catalog are missing.");
                return false;
            }
            AarisVehiclePointsFile file;
            AarisVehicleTypesFile definitions;
            try
            {
                file = JsonUtility.FromJson<AarisVehiclePointsFile>(points.text);
                definitions = JsonUtility.FromJson<AarisVehicleTypesFile>(catalog.text);
            }
            catch (Exception e)
            {
                Debug.LogError("Aaris vehicles: malformed offline data: " + e.Message);
                return false;
            }
            if (file == null || file.schema != 1 || file.sizeMetres != 2000 ||
                file.hubCount != 45 || file.candidates == null ||
                file.candidates.Length < 70 || definitions == null ||
                definitions.vehicles == null || definitions.vehicles.Length < 3)
            {
                Debug.LogError("Aaris vehicles: source data does not match the 2km authored world.");
                return false;
            }
            vehicleTypes = definitions.vehicles;
            prefabs = new GameObject[vehicleTypes.Length];
            wheelPrefabs = new GameObject[vehicleTypes.Length];
            int availableWeight = 0;
            for (int i = 0; i < vehicleTypes.Length; i++)
            {
                var item = vehicleTypes[i];
                if (item == null || item.weight <= 0 || item.maxSpeedMetres < 8f ||
                    item.maxSpeedMetres > 28f) continue;
                prefabs[i] = Resources.Load<GameObject>(item.modelResource);
                wheelPrefabs[i] = Resources.Load<GameObject>(item.wheelResource);
                if (prefabs[i] == null || wheelPrefabs[i] == null) continue;
                if (prefabs[i].GetComponentInChildren<Renderer>(true) == null ||
                    wheelPrefabs[i].GetComponentInChildren<Renderer>(true) == null)
                    continue;
                availableWeight += item.weight;
                validModels++;
            }
            if (validModels < 3 || availableWeight <= 0)
            {
                Debug.LogError("Aaris vehicles: not all required local CC0 3D vehicles imported; fail closed.");
                return false;
            }
            Texture2D palette = Resources.Load<Texture2D>("AarisVehicles/Models/colormap");
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null || palette == null)
            {
                Debug.LogError("Aaris vehicles: verified URP palette/shader missing, refusing white or pink placeholder cars.");
                return false;
            }
            paletteMaterial = new Material(shader) { name = "Aaris CC0 Kenney Car Palette" };
            paletteMaterial.SetTexture("_BaseMap", palette);
            paletteMaterial.SetColor("_BaseColor", Color.white);
            paletteMaterial.SetFloat("_Metallic", .08f);
            paletteMaterial.SetFloat("_Smoothness", .35f);

            // Stable per-match plan. A multiplayer server can later provide
            // this seed to clients; local standalone has no server authority.
            randomState = unchecked((uint)DateTime.UtcNow.Ticks) ^
                unchecked((uint)Environment.TickCount) ^ 0xB4217C35u;
            if (randomState == 0) randomState = 0x89A51C3Bu;
            var candidates = new List<AarisVehiclePoint>();
            foreach (var point in file.candidates)
            {
                if (point == null || Mathf.Abs(point.x) > 950f ||
                    Mathf.Abs(point.z) > 950f) continue;
                candidates.Add(point);
            }
            Shuffle(candidates);
            var sectors = new HashSet<int>();
            // Stage one: avoid the city-first distribution by choosing one
            // candidate in every available 500x500m sector.
            foreach (var point in candidates)
            {
                int sector = Mathf.Clamp(Mathf.FloorToInt((point.x + 1000f) / 500f), 0, 3)
                    + 4 * Mathf.Clamp(Mathf.FloorToInt((point.z + 1000f) / 500f), 0, 3);
                if (sectors.Contains(sector)) continue;
                if (AddPlan(point, 100f)) sectors.Add(sector);
            }
            // Stage two: prefer the 45 real garage spots over arbitrary
            // roadside placements while retaining map-wide coverage.
            candidates.Sort((a, b) =>
                (a.kind == "hub" ? 0 : 1).CompareTo(b.kind == "hub" ? 0 : 1));
            foreach (var point in candidates)
            {
                if (plans.Count >= TargetVehicles) break;
                AddPlan(point, 105f);
            }
            Debug.Log("AARIS_VEHICLE_MATCH planned=" + plans.Count +
                " hubs=45 authoredCandidates=" + file.candidates.Length +
                " sectorCoverage=" + sectors.Count + " (physics validated when nearby)");
            return plans.Count >= 18;
        }

        private uint Next()
        {
            randomState ^= randomState << 13;
            randomState ^= randomState >> 17;
            randomState ^= randomState << 5;
            return randomState;
        }

        private void Shuffle(List<AarisVehiclePoint> candidates)
        {
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = (int)(Next() % (uint)(i + 1));
                var t = candidates[i];
                candidates[i] = candidates[j];
                candidates[j] = t;
            }
        }

        private bool AddPlan(AarisVehiclePoint point, float minimumSeparation)
        {
            foreach (var prior in plans)
            {
                float dx = prior.point.x - point.x, dz = prior.point.z - point.z;
                if (dx * dx + dz * dz < minimumSeparation * minimumSeparation)
                    return false;
            }
            int total = 0;
            for (int i = 0; i < vehicleTypes.Length; i++)
                if (prefabs[i] != null && wheelPrefabs[i] != null)
                    total += vehicleTypes[i].weight;
            int pick = (int)(Next() % (uint)total), which = -1;
            for (int i = 0; i < vehicleTypes.Length; i++)
            {
                if (prefabs[i] == null || wheelPrefabs[i] == null) continue;
                pick -= vehicleTypes[i].weight;
                if (pick < 0) { which = i; break; }
            }
            if (which < 0) return false;
            plans.Add(new AarisVehicleRecord
            {
                point = point,
                type = vehicleTypes[which],
                position = new Vector3(point.x, point.y, point.z),
                rotation = Quaternion.Euler(0f, point.yaw, 0f)
            });
            return true;
        }

        private static Terrain TerrainAt(float x, float z)
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null) continue;
                var p = terrain.transform.position;
                var size = terrain.terrainData.size;
                if (x >= p.x && x <= p.x + size.x &&
                    z >= p.z && z <= p.z + size.z) return terrain;
            }
            return null;
        }

        private bool HasSupport(Vector3 center, float expected, out float y)
        {
            y = 0f;
            var terrain = TerrainAt(center.x, center.z);
            if (terrain == null) return false;
            Vector3 rayOrigin = new Vector3(center.x, expected + 3f, center.z);
            int hits = Physics.RaycastNonAlloc(new Ray(rayOrigin, Vector3.down),
                rayHits, 5f, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            if (hits >= rayHits.Length) return false;
            float bestError = 1.05f;
            bool found = false;
            for (int i = 0; i < hits; i++)
            {
                var h = rayHits[i];
                if (h.collider == null || h.normal.y < .9781f) continue; // <=12 deg
                if (player != null && h.collider.transform.IsChildOf(player)) continue;
                float error = Mathf.Abs(expected - h.point.y);
                if (error >= bestError || h.point.y < SeaLevel + 1.0f) continue;
                bestError = error;
                found = true;
                y = h.point.y;
            }
            return found;
        }

        private bool TryResolve(AarisVehicleRecord record, out Vector3 spot)
        {
            spot = default(Vector3);
            var p = record.point;
            // A driven vehicle can be parked miles away from its original
            // marker. Never teleport it back when its physics mesh streams in.
            float x = record.previouslySpawned ? record.position.x : p.x;
            float z = record.previouslySpawned ? record.position.z : p.z;
            var terrain = TerrainAt(x, z);
            if (terrain == null) return false;
            // Terrain.SampleHeight already returns a WORLD-SPACE Y.
            // Adding terrain.transform.position.y again breaks placement
            // on our four elevated/negative-height native Terrain tiles.
            float ground = terrain.SampleHeight(new Vector3(x, 0f, z));
            float expectedY = record.previouslySpawned ? record.position.y - .71f : p.y;
            // Ground map author stores garage Y and road Y; use sampled
            // Terrain only as a sanity check, not as a rooftop substitute.
            if (Mathf.Abs(ground - expectedY) > 2.5f) return false;
            if (!HasSupport(new Vector3(x, 0f, z), expectedY, out float y))
                return false;
            Quaternion angle = record.rotation;
            Vector3 forward = angle * Vector3.forward * 1.42f;
            Vector3 right = angle * Vector3.right * .79f;
            // The spawn stream retries candidates every few seconds. Probe all four
            // chassis corners without allocating an array on the mobile GC heap.
            for (int corner = 0; corner < 4; corner++)
            {
                float frontSign = corner < 2 ? 1f : -1f;
                float sideSign = (corner & 1) == 0 ? 1f : -1f;
                Vector3 delta = forward * frontSign + right * sideSign;
                var check = new Vector3(x + delta.x, 0f, z + delta.z);
                if (!HasSupport(check, y, out float localY) ||
                    Mathf.Abs(localY - y) > .37f) return false;
            }
            // Clearance is above the supporting ground so TerrainCollider
            // will not be counted as a vehicle-sized wall.
            Vector3 center = new Vector3(x, y + 1.15f, z);
            int count = Physics.OverlapBoxNonAlloc(center,
                new Vector3(1.14f, .64f, 2.35f), collisionHits, angle,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (count >= collisionHits.Length) return false;
            for (int i = 0; i < count; i++)
            {
                var obstacle = collisionHits[i];
                if (obstacle == null || obstacle is TerrainCollider) continue;
                if (player != null && obstacle.transform.IsChildOf(player)) return false;
                // Roads can be a valid floor, but anything occupying the
                // full chassis volume is an unsafe parking location.
                return false;
            }
            spot = new Vector3(x, y + .71f, z);
            return true;
        }

        private bool Build(AarisVehicleRecord record, Vector3 pos)
        {
            GameObject source = null;
            GameObject wheelSource = null;
            foreach (var type in vehicleTypes)
            {
                if (type != record.type) continue;
                for (int i = 0; i < vehicleTypes.Length; i++)
                    if (vehicleTypes[i] == type)
                    {
                        source = prefabs[i];
                        wheelSource = wheelPrefabs[i];
                    }
            }
            if (source == null || wheelSource == null) return false;
            var root = new GameObject("Aaris Vehicle " + record.type.id + " " + record.point.id);
            root.transform.SetParent(transform, false);
            root.transform.SetPositionAndRotation(pos, record.rotation);
            var mesh = Instantiate(source, root.transform, false);
            mesh.name = "Local CC0 Car Model";
            foreach (var collider in mesh.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            var renderers = mesh.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) { Destroy(root); return false; }
            // Kenney Car Kit uses one UV palette atlas. Share ONE URP Lit
            // material across streamed instances; never instantiate new
            // materials per car/per frame.
            foreach (var renderer in renderers)
            {
                int submeshes = Mathf.Max(1, renderer.sharedMaterials.Length);
                Material[] materials = new Material[submeshes];
                for (int i = 0; i < submeshes; i++) materials[i] = paletteMaterial;
                renderer.sharedMaterials = materials;
            }
            Bounds bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            if (bounds.size.x > bounds.size.z * 1.1f)
                mesh.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            if (bounds.size.z < .1f || bounds.size.x < .1f)
            { Destroy(root); return false; }
            float scale = Mathf.Min(1.88f / bounds.size.x, 4.28f / bounds.size.z);
            mesh.transform.localScale *= Mathf.Clamp(scale, .01f, 50f);
            bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            Vector3 targetCenter = root.transform.TransformPoint(new Vector3(0f, .48f, 0f));
            mesh.transform.position += targetCenter - bounds.center;
            var motor = root.AddComponent<AarisVehicleMotor>();
            motor.Configure(record.type.id, record.type.maxSpeedMetres, record.type.massKg);
            if (!motor.AttachWheelModels(wheelSource, paletteMaterial))
            {
                Destroy(root);
                return false;
            }
            record.visible = motor;
            if (Debug.isDebugBuild)
                Debug.Log("AARIS_CAR_PLACED " + record.point.id + " " +
                    record.type.id + " " + root.transform.position.ToString("F2"));
            return true;
        }

        private void Update()
        {
            if (player == null) return;
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + .3f;
            int active = 0, built = 0;
            foreach (var record in plans)
            {
                if (record.visible == null) continue;
                float dist2 = (record.visible.transform.position - player.position).sqrMagnitude;
                // Never despawn a driven, rolling or collided vehicle.
                if (dist2 > UnloadRadius * UnloadRadius && record.visible.CanPark)
                {
                    record.position = record.visible.transform.position;
                    record.rotation = record.visible.transform.rotation;
                    Destroy(record.visible.gameObject);
                    record.visible = null;
                }
                else active++;
            }
            foreach (var record in plans)
            {
                if (active >= MaximumNearbyVehicles || built >= 2) break;
                if (record.visible != null || Time.unscaledTime < record.nextProbe) continue;
                if ((record.position - player.position).sqrMagnitude > LoadRadius * LoadRadius)
                    continue;
                if (TryResolve(record, out Vector3 pos) && Build(record, pos))
                {
                    active++;
                    built++;
                    record.position = pos;
                    record.previouslySpawned = true;
                }
                else
                {
                    record.nextProbe = Time.unscaledTime + 3.0f;
                }
            }
        }

        private void OnDestroy()
        {
            if (paletteMaterial != null) Destroy(paletteMaterial);
        }

        public AarisVehicleMotor NearestAvailable(Vector3 origin, float maxDistance)
        {
            AarisVehicleMotor nearest = null;
            float best = maxDistance * maxDistance;
            foreach (var record in plans)
            {
                if (record.visible == null || record.visible.IsOccupied) continue;
                float d = (record.visible.transform.position - origin).sqrMagnitude;
                if (d >= best) continue;
                best = d;
                nearest = record.visible;
            }
            return nearest;
        }
    }
}
