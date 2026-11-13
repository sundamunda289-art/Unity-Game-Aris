# Aaris 2×2km local weapon + loot architecture

## Installed binary weapon assets

Five CC0 Pichuliru East pack rigged FBXs are committed locally in
`Assets/Resources/AarisWeapons/Models/`; no live download URL is used
by the game. Download hashes and rights are documented in
`Docs/WEAPON_ASSETS_LICENSE_AND_SOURCE.md`. Models are **game-ready
flat-shaded, not yet photorealistic**; URP appearance/hand grip requires
a genuine Unity Editor visual check.

## Data-driven magazine, recoil and scope rules

These Aaris numbers are balancing defaults, NOT values copied from
Free Fire/PUBG. Change `Assets/Resources/AarisWeapons/weapon_catalog.json`
rather than creating a new combat script for each gun.

| Aaris class | RPM | Mode | Magazine | Damage | ADS FOV |
| --- | ---: | --- | ---: | ---: | ---: |
| Compact SMG | 780 | Automatic | 36 | 20 | 47° |
| Tactical AR | 660 | Automatic | 30 | 33 | 40° |
| Battle Rifle | 520 | Automatic | 25 | 42 | 38° |
| Bolt Sniper | 48 | Single-press | 5 | 112 | 14° |
| Pump Shotgun | 85 | Single-press; 8 pellets | 6 | 11 per pellet | 48° |

**Inventory rule (updated): exactly ONE gun carried at a time.** With
empty hands the Fire button punches. Initial automatic pickup is allowed
for an unarmed player near a validated authored ground weapon. The player
must press **PICK** while already armed to replace their current gun;
the former gun appears at the collected weapon's proven floor position,
with original magazine, reserve ammo, active reload timer and firing state
still attached. No hidden second slot/backpack gun is created.

The player may press **DROP** at any time. This first validates a reachable
flat ground spot (not underwater, not in a wall or off a cliff), reserves
the dropped pickup, then removes the held gun; on failure the gun remains
safely equipped. After intentional DROP, automatic re-equipping stays
disabled until the player explicitly presses PICK again. Each dropped
item persists as a match record when distant meshes are streamed out;
revisiting the area reconstructs its model without rerolling ammo.

The five movement/combat buttons remain unchanged. Compact utility
controls now use `ADS`, `PICK`, `DROP` and `RELOAD`; PICK is shown only
near reachable loot while the other three require a held gun. The
buttons share the existing EventSystem and cannot also trigger camera
look. Desktop review: E=pick, G=drop, R=reload, right-mouse=ADS.
The player's third-person motor remains sole camera/FOV and collision owner.

## Three-tier match loot

1. **Buildings:** From the actual 59 authored map buildings, the offline
   baker chooses 156 potential room-floor coordinates from likely
   residential structures. Each match samples candidates with a 72%
   probability, but spawns only where **both physical floor and roof
   are verified**, clearance above the floor is available, and a player
   can approach the item. Unusable solid meshes are skipped rather
   than pretending they are accessible rooms.
2. **Native trees:** Four Terrain tiles currently report about 7,508
   native TreeInstances. At most 12 per tile are sampled at boot,
   with at most 72 attempts per tile and >=32m spacing between
   candidates. Actual item locations are offset 2–9m from sampled
   trunks; floor/overlap and terrain/water checks happen near player.
   Other trees receive no gun.
3. **Open world:** 1346 spatially distributed, jittered 50m grid
   candidates outside the authored building AABBs. Each match samples
   at 32%, all across the 2km map. Ground, water and wall clearance
   remain mandatory; no guaranteed gun in every cell.

Match loot uses a local xorshift seed and cached candidate list, so
returning to an area does not reroll its drops. A collected authored
pickup remains consumed. Player-dropped guns are separate, persistent
negative-ID match records. Rendered pickup objects stream within **42m**
of the player, with **40 active maximum**, **6 newly created maximum
per refresh**, and disappear visually past 69m (but retain their
per-match state). A brief pickup grace period prevents instantly
recollecting a dropped gun. Surrounding native world colliders are already
enabled out to 55m by the existing `AarisNearbyColliders` system.

**Placement QA detail:** Baked building X/Z positions are only candidates.
At runtime, `TryResolve` chooses a floor hit near the authored elevation
from a bounded downward raycast. This prevents a low house roof from
stealing the floor ray. Indoor candidates also require a real overhead
collider, slope clearance and unblocked floor. Open-ground candidates
require the TerrainCollider. Rejected probes back off for 3 seconds.
Development builds log each `AARIS_LOOT_PLACED`, `AARIS_LOOT_DROPPED`
and `AARIS_LOOT_COLLECTED` event with map coordinates. Actual placements
still require Unity Play-mode verification.

**No full 2km physics scan, no per-frame tree-instance copies, no terrain
or building edits, no fake gun cubes, no network asset dependency.**

## Quality and trust boundaries

- These systems are for local gameplay; online multiplayer needs a
  server-side authoritative loot seed, pickup ownership arbitration,
  hit validation, ammunition and shot reconciliation.
- Current raycast hit-scan is an abstract game mechanic, not a full
  ballistic/penetration/ricochet model.
- Accessible interiors and tree loot need true **Unity Play mode**
  checks against the actual TerrainCollider/MeshCollider geometry.
- There is no claim of actual 60 FPS or working reload/humanoid visual
  attachment before Play-mode/Android testing.
- Only source+binary integrity is verified in license-free GitHub Actions.
- The intended future Editor check is
  `Aaris.MapReview.Editor.AarisWeaponImportValidation.Run` under a
  legitimately licensed Unity Editor 6000.3.26f1.

## Rebuild/provenance verification

```sh
python Tools/Weapons/bake_loot.py
python Tools/Validation/check_weapon_loot.py
python Tools/Validation/check_single_weapon_transactions.py
```

The 1502 baked indoor/grid candidates are reproducible from the
existing `Docs/MAP_LAYOUT.json`; the optional bounded tree sampling
runs at the start of the match, not at build time.

Original five-gun feature merged as PR #94; the one-gun pickup/drop upgrade
is reviewed in PR #95. Neither modifies the native scene, Terrain, houses
or original Git LFS map assets.

**50m outdoor grid note (2026-11-13):** The denser authored locations expand
candidate coverage but do not increase the 40-weapon live mesh limit or
per-frame spawn/probe budgets. A planned candidate is not a confirmed spawn:
Unity still requires a clear and reachable physical surface. Android FPS and
in-Editor real pickup/vehicle checks are required before declaring this fixed.
