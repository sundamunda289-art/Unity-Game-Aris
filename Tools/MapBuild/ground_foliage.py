"""Bake collider-free undergrowth into the native Terrain detail layers."""

import argparse
import hashlib
import json
import re
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import UnityPy

from airy_house_geometry import HOUSES, build
from airy_house_scatter import local_limits, rotate
from export_index import export


ROOT = Path(__file__).resolve().parents[2]
TERRAIN = ROOT / "Assets/TerrainDemoScene_URP/Terrain/Data"
VARIANTS = ("Bush_A", "Bush_B", "Bush_Twig")
# Measured LOD0 Mesh.bounds height. Unity's DetailPrototype sizes are multipliers.
MESH_HEIGHTS = (0.8757076, 0.8263164, 1.0120425)
WIDTH_RANGE = ((1.42, 1.85), (1.25, 1.62), (1.16, 1.48))
HOUSE_MARGIN = 2.25
ROAD_MARGIN = 3.0
MIN_SPACING = 2.65


def read_json(path):
    return json.loads(path.read_text())


def prefab_guids():
    return {
        re.search(r"(?m)^guid: ([a-f0-9]{32})$", path.read_text())[1]: path.name[:-12]
        for path in (ROOT / "Assets").rglob("*.prefab.meta")
    }


def prototype_names(terrain_object, prefab_by_guid):
    refs = {}
    for index, entry in enumerate(terrain_object.assets_file.externals, 1):
        raw = entry.guid.hex()
        guid = "".join(raw[i + 1] + raw[i] for i in range(0, 32, 2))
        refs[index] = prefab_by_guid.get(guid)
    return refs


def house_rectangles(audit):
    limits = {key: local_limits(build(house)) for key, house in HOUSES.items()}
    assert len(audit["houses"]) == 128
    for house in audit["houses"]:
        lo, hi = limits[house["prefab"]]
        position = np.array(house["position"])[[0, 2]]
        scale = np.array(house["scale"])[[0, 2]]
        yield position, house["yaw"], lo * scale - HOUSE_MARGIN, hi * scale + HOUSE_MARGIN


def protected_cells(resolution, spacing, offset, roads, houses):
    mask = np.zeros((resolution, resolution), dtype=bool)
    world = (np.arange(resolution) + .5) * spacing

    def grid(low, high):
        indices = np.floor((np.array([low, high]) - offset) / spacing).astype(int)
        start = np.clip(indices[0], 0, resolution)
        stop = np.clip(indices[1] + 1, 0, resolution)
        x0, z0 = start
        x1, z1 = stop
        window = mask[z0:z1, x0:x1]
        return window, world[None, x0:x1] + offset[0], world[z0:z1, None] + offset[1]

    for road in roads:
        radius = .5 * road["width"] + ROAD_MARGIN
        points = np.array(road["points"])[:, (0, 2)]
        for a, b in zip(points, points[1:]):
            delta = b - a
            length_sq = delta @ delta
            if length_sq <= 1e-12:
                continue
            window, x, z = grid(np.minimum(a, b) - radius, np.maximum(a, b) + radius)
            dx, dz = x - a[0], z - a[1]
            t = np.clip((dx * delta[0] + dz * delta[1]) / length_sq, 0, 1)
            window |= (dx - t * delta[0]) ** 2 + (dz - t * delta[1]) ** 2 <= radius ** 2

    for position, yaw, lo, hi in houses:
        corners = np.array(((lo[0], lo[1]), (lo[0], hi[1]), (hi[0], lo[1]), (hi[0], hi[1])))
        box = np.array(rotate(corners[:, 0], corners[:, 1], yaw)).T + position
        window, x, z = grid(box.min(axis=0), box.max(axis=0))
        x, z = rotate(x - position[0], z - position[1], -yaw)
        window |= (x >= lo[0]) & (x <= hi[0]) & (z >= lo[1]) & (z <= hi[1])
    return mask


def land_mask(heightmap, resolution, sea_level):
    raw = np.asarray(heightmap["m_Heights"], dtype=np.float32).reshape(2049, 2049)
    heights = raw * (800. / 32766.) - 120.
    centers = np.rint((np.arange(resolution) + .5) * 2048 / resolution).astype(int)
    x, z = centers[None, :], centers[:, None]
    sample = heights[z, x]
    dx = heights[z, np.minimum(x + 2, 2048)] - heights[z, np.maximum(x - 2, 0)]
    dz = heights[np.minimum(z + 2, 2048), x] - heights[np.maximum(z - 2, 0), x]
    return (sample > sea_level + 1.35) & (dx * dx + dz * dz < 1.22 ** 2)


def foliage_layers(data, names):
    database = data["m_DetailDatabase"]
    bush_slots = [i for i, name in enumerate(names) if name and (name.startswith("Bush") or name == "Shrub")]
    selected = [names.index(name) for name in VARIANTS]
    assert set(selected) <= set(bush_slots), names
    step = database["m_PatchSamples"]
    patches_per_side = database["m_PatchCount"]
    size = patches_per_side * step
    old = np.zeros((len(VARIANTS), size, size), dtype=np.uint16)
    for index, patch in enumerate(database["m_Patches"]):
        x, z = (index % patches_per_side) * step, (index // patches_per_side) * step
        for layer_index, layer in enumerate(patch["layerIndices"]):
            if layer not in bush_slots:
                continue
            name = names[layer]
            variant = 2 if "Dry" in name or name == "Bush_Twig" else 0 if name == "Bush_A" else 1
            density = patch["numberOfObjects"][layer_index * step * step:(layer_index + 1) * step * step]
            old[variant, z:z + step, x:x + step] += np.asarray(density).reshape(step, step).astype(np.uint16)
    return old, bush_slots, selected


def scatter(old, allowed, spacing, seed):
    rng = np.random.default_rng(seed)
    result = np.zeros(old.shape, dtype=np.uint8)
    blocked = ~allowed.copy()
    size = allowed.shape[0]
    distance = int(np.ceil(MIN_SPACING / spacing))
    axis = np.arange(-distance, distance + 1) * spacing
    reservation = axis[:, None] ** 2 + axis[None, :] ** 2 < MIN_SPACING ** 2

    def place(x, z, variant):
        if not (0 <= x < size and 0 <= z < size) or blocked[z, x]:
            return False
        result[variant, z, x] = 1
        x0, x1 = max(0, x - distance), min(size, x + distance + 1)
        z0, z1 = max(0, z - distance), min(size, z + distance + 1)
        rx, rz = x0 - x + distance, z0 - z + distance
        blocked[z0:z1, x0:x1] |= reservation[rz:rz + z1 - z0, rx:rx + x1 - x0]
        return True

    # Start with the authored patch shapes. Variants rarely overlap, even in
    # the dense old brush, and the spacing applies across prototype layers.
    z, x = np.where(allowed & old.any(axis=0))
    for i in rng.permutation(len(x)):
        if rng.random() < .73:
            weights = np.cumsum(old[:, z[i], x[i]])
            variant = int(np.searchsorted(weights, rng.integers(weights[-1]), side="right"))
            place(x[i], z[i], variant)

    # Fill gaps between authored clusters in suitable open ground. Jittered
    # cluster centres and uneven offsets keep the brush away from a grid look.
    cell = round(86 / spacing)
    radius = round(9 / spacing)
    placed_clusters = 0
    for top in range(0, size, cell):
        for left in range(0, size, cell):
            if rng.random() > .71:
                continue
            for attempt in range(4):
                cx = rng.integers(left, min(size, left + cell))
                cz = rng.integers(top, min(size, top + cell))
                if not allowed[cz, cx] or result[:, max(0, cz - 12):cz + 13, max(0, cx - 12):cx + 13].any():
                    continue
                count = 0
                variant = rng.choice(len(VARIANTS), p=(.47, .34, .19))
                for _ in range(rng.integers(5, 11)):
                    dx, dz = rng.integers(-radius, radius + 1, 2)
                    if dx * dx + dz * dz <= radius * radius:
                        count += place(cx + dx, cz + dz, variant)
                placed_clusters += count >= 2
                if count:
                    break
    return result, placed_clusters


def write_layers(database, result, bush_slots, selected):
    patch_size = database["m_PatchSamples"]
    patch_area = patch_size ** 2
    axis_count = database["m_PatchCount"]
    for index, patch in enumerate(database["m_Patches"]):
        x, z = (index % axis_count) * patch_size, (index // axis_count) * patch_size
        kept = {
            layer: patch["numberOfObjects"][i * patch_area:(i + 1) * patch_area]
            for i, layer in enumerate(patch["layerIndices"]) if layer not in bush_slots
        }
        for variant, slot in enumerate(selected):
            cells = result[variant, z:z + patch_size, x:x + patch_size]
            if cells.any():
                kept[slot] = cells.ravel().tolist()
        patch["layerIndices"] = sorted(kept)
        patch["numberOfObjects"] = [density for layer in patch["layerIndices"] for density in kept[layer]]

    for variant, slot in enumerate(selected):
        prototype = database["m_DetailPrototypes"][slot]
        height = MESH_HEIGHTS[variant]
        low, high = WIDTH_RANGE[variant]
        prototype.update(minHeight=1.04 / height, maxHeight=1.30 / height,
                         minWidth=low, maxWidth=high, useInstancing=1)


def decorative_shrubs(database, refs, protected, spacing):
    painted = []
    for tree in database["m_TreeInstances"]:
        proto = database["m_TreePrototypes"][tree["index"]]["prefab"]
        if refs.get(proto["m_FileID"]) != "Red_Bush":
            painted.append(tree)
            continue
        p = tree["position"]
        x, z = np.clip(np.floor(np.array((p["x"], p["z"])) * 1000 / spacing).astype(int),
                       0, protected.shape[0] - 1)
        if not protected[z, x]:
            tree.update(heightScale=.56, widthScale=.42)
            painted.append(tree)
    database["m_TreeInstances"] = painted


def baked_map(source):
    layout = read_json(ROOT / "Docs/MAP_LAYOUT.json")
    houses = list(house_rectangles(read_json(ROOT / "Docs/AIRY_HOUSE_AUDIT.json")))
    prefabs = prefab_guids()
    files = {}
    selected_slots = {}
    native = SimpleNamespace(data={}, env={})
    for seed, file in enumerate(sorted(TERRAIN.glob("Terrain_?_?_*.asset")), start=73591):
        tile = re.search(r"Terrain_(\d_\d)_", file.name)[1]
        tx, tz = map(int, tile.split("_"))
        offset = np.array(((tx - 2) * 1000, (tz - 2) * 1000))
        env = UnityPy.load(str(source / file.name))
        obj = next(o for o in env.objects if o.type.name == "TerrainData")
        data = obj.read_typetree()
        db = data["m_DetailDatabase"]
        refs = prototype_names(obj, prefabs)
        names = [refs.get(p["prototype"]["m_FileID"]) for p in db["m_DetailPrototypes"]]
        old, bush_slots, selected = foliage_layers(data, names)
        selected_slots[tile] = selected
        size = old.shape[1]
        spacing = 1000 / size
        protected = protected_cells(size, spacing, offset, layout["roads"], houses)
        allowed = ~protected & land_mask(data["m_Heightmap"], size, layout["sea_level_m"])
        result, clusters = scatter(old, allowed, spacing, seed)
        assert not result[:, protected].any()
        write_layers(db, result, bush_slots, selected)
        decorative_shrubs(db, refs, protected, spacing)
        obj.save_typetree(data)
        payload = env.file.save()
        file.write_bytes(payload)
        files[str(file.relative_to(ROOT))] = {"sha256": hashlib.sha256(payload).hexdigest(), "size": len(payload)}
        native.data[tile] = data
        native.env[tile] = (env, obj)
        print(f"{tile}: {old.sum()} → {result.sum()} bush density units; {clusters} new clusters; {result.sum(axis=(1, 2)).tolist()}")

    export(native, ROOT / "Docs", ROOT / "Tools/MapBuild")
    for report in (ROOT / "Docs/MAP_BUILD_REPORT.json", ROOT / "Docs/NATIVE_TERRAIN_VERIFICATION.json"):
        data = read_json(report)
        data["terrain_files"] = files
        if "tiles" in data:
            for tile in data["tiles"]:
                key = tile["tile"]
                tile["indexed_instance_count"] = len(native.data[key]["m_DetailDatabase"]["m_TreeInstances"])
                tile.pop("terrain_layers_and_detail_prototypes_preserved", None)
                tile["terrain_layers_preserved"] = True
                tile["other_detail_prototypes_preserved"] = True
                tile["recalibrated_bush_prototype_slots"] = selected_slots[key]
        report.write_text(json.dumps(data, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True,
                        help="Directory with the four original, hydrated pre-bake TerrainData assets")
    source = parser.parse_args().source.resolve()
    if source == TERRAIN.resolve():
        parser.error("Keep the unchanged terrain inputs in a separate directory")
    for target in TERRAIN.glob("Terrain_?_?_*.asset"):
        original = source / target.name
        if not original.is_file() or original.stat().st_size < 4096:
            parser.error(f"Original native terrain is missing: {original}")
    baked_map(source)
