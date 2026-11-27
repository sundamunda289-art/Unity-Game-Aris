using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview
{
    internal enum AarisHouseLootItem : byte { Empty, Weapon, Medkit, Ammo, Frag, GlooWall }

    // Both pickup streamers read the same immutable match plan. Build it once
    // after the shared loot document arrives, independently of load order.
    internal sealed class AarisHouseLoot
    {
        private static AarisHouseLoot current;
        private static int currentScene;
        internal readonly uint Seed;
        internal readonly AarisHouseLootItem[] Items;
        private readonly HashSet<string> homes = new HashSet<string>(StringComparer.Ordinal);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetMatch()
        {
            current = null;
            currentScene = 0;
        }

        internal static AarisHouseLoot ForMatch(AarisLootDocument document)
        {
            int scene = SceneManager.GetActiveScene().handle;
            if (current != null && currentScene == scene) return current;

            uint seed = unchecked((uint)DateTime.UtcNow.Ticks) ^
                unchecked((uint)Environment.TickCount) ^ 0x59371FA3u;
            current = new AarisHouseLoot(document.houseSockets, seed);
            currentScene = scene;
            return current;
        }

        internal AarisHouseLoot(AarisLootCandidate[] sockets, uint seed)
        {
            Seed = seed;
            int count = sockets == null ? 0 : sockets.Length;
            Items = new AarisHouseLootItem[count];
            var order = new int[count];
            int valid = 0;
            for (int i = 0; i < count; i++)
            {
                var socket = sockets[i];
                if (socket == null || socket.kind != "socket" ||
                    string.IsNullOrEmpty(socket.origin) ||
                    !(Mathf.Abs(socket.x) < 957f && Mathf.Abs(socket.z) < 957f &&
                      socket.y > -37.35f && socket.y < 650f)) continue;
                order[valid++] = i;
                homes.Add(socket.origin);
            }

            // Draw a fixed global number of active sockets without replacement.
            // A 3-slot house can change from zero to three stocked locations.
            uint shuffle = seed ^ 0x912F37ABu;
            int stocked = Mathf.RoundToInt(valid * 0.70f);
            for (int i = 0; i < stocked; i++)
            {
                int pick = i + (int)(AarisWeaponRandom.Next(ref shuffle) % (uint)(valid - i));
                int index = order[pick];
                order[pick] = order[i];
                uint roll = SocketSeed(sockets[index].slot);
                float weight = AarisWeaponRandom.Next01(ref roll);
                Items[index] = weight < 0.60f ? AarisHouseLootItem.Weapon :
                    weight < 0.72f ? AarisHouseLootItem.Medkit :
                    weight < 0.85f ? AarisHouseLootItem.Ammo :
                    weight < 0.93f ? AarisHouseLootItem.Frag : AarisHouseLootItem.GlooWall;
            }
        }

        internal uint SocketSeed(int slot) => Seed ^ unchecked((uint)slot * 2246822519u);

        internal bool ReplacesIndoor(AarisLootCandidate candidate) =>
            candidate != null && candidate.kind == "indoor" && homes.Contains(candidate.origin);

        // Each socket declares its actual floor elevation. A short ray cannot
        // jump to another storey, the terrain or an exterior fallback point.
        internal static bool TryResolve(AarisLootCandidate socket, out Pose pose)
        {
            pose = default;
            var expected = new Vector3(socket.x, socket.y, socket.z);
            if (!Physics.Raycast(expected + Vector3.up * .29f, Vector3.down,
                out RaycastHit hit, .44f, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore) || hit.normal.y < .96f ||
                hit.collider is TerrainCollider || hit.collider.attachedRigidbody != null)
                return false;
            Vector3 clearance = hit.point + Vector3.up * .26f;
            if (Physics.CheckCapsule(clearance, clearance + Vector3.up * .20f, .20f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;

            pose = new Pose(hit.point + Vector3.up * .018f, Quaternion.identity);
            return true;
        }
    }
}
