#!/usr/bin/env python3
"""Read-only preflight for real Unity 6 world partitioning.

Never edits scenes, prefabs, materials, GUIDs or collider registrations.
This audits serialized scene object references before any additive migration.
Usage: python3 Tools/Validation/world_partition_readiness.py [--json] [--require-safe]
"""
import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass
import json
from pathlib import Path
import re
import sys

SCENE = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"
_BLOCK = re.compile(r"(?=^--- !u!\d+ &-?\d+)", re.MULTILINE)
_HEADER = re.compile(r"^--- !u!(\d+) &(-?\d+)", re.MULTILINE)
_REF = re.compile(r"fileID: (-?\d+)")
_GO = re.compile(r"^\s*m_GameObject: \{fileID: (\d+)\}", re.MULTILINE)
_FATHER = re.compile(r"^\s*m_Father: \{fileID: (-?\d+)\}", re.MULTILINE)
_NAME = re.compile(r"^\s*m_Name: (.+)$", re.MULTILINE)


@dataclass(frozen=True)
class Block:
    kind: int
    body: str


class WorldScene:
    def __init__(self, source: str):
        self.blocks = {}
        self.names = {}
        self.transform_to_go = {}
        self.go_to_transform = {}
        self.parent_transform = {}
        self.owner = {}
        self.component_types = defaultdict(set)
        for section in _BLOCK.split(source):
            match = _HEADER.search(section)
            if not match:
                continue
            kind, uid = int(match.group(1)), int(match.group(2))
            if uid in self.blocks:
                raise ValueError("Duplicate Unity fileID: " + str(uid))
            self.blocks[uid] = Block(kind, section)
            if kind == 1:
                name = _NAME.search(section)
                self.names[uid] = name.group(1) if name else "<unnamed>"
                self.owner[uid] = uid
            elif kind == 4:
                go = _GO.search(section)
                parent = _FATHER.search(section)
                if not go or not parent:
                    raise ValueError("Transform missing owner or parent: " + str(uid))
                go = int(go.group(1))
                self.transform_to_go[uid] = go
                self.go_to_transform[go] = uid
                self.parent_transform[uid] = int(parent.group(1))
                self.owner[uid] = go
            else:
                go = _GO.search(section)
                if go:
                    go = int(go.group(1))
                    self.owner[uid] = go
                    self.component_types[go].add(kind)
        missing = set(self.names) - set(self.go_to_transform)
        if missing:
            raise ValueError("Scene GameObjects without Transforms: " +
                             str(len(missing)))
        self.children = defaultdict(list)
        self.parent_go = {}
        for go, transform in self.go_to_transform.items():
            parent = self.parent_transform[transform]
            parent_go = self.transform_to_go.get(parent)
            if parent != 0 and parent_go is None:
                raise ValueError("Broken Transform parent: " + str(parent))
            self.parent_go[go] = parent_go
            if parent_go is not None:
                self.children[parent_go].append(go)

    def find_unique(self, name):
        matches = [go for go, item in self.names.items() if item == name]
        if len(matches) != 1:
            raise ValueError("Expected one named root " + name + ": " + str(len(matches)))
        return matches[0]

    def descendants(self, root):
        result = set()
        pending = [root]
        while pending:
            go = pending.pop()
            if go in result:
                raise ValueError("Cyclic Transform hierarchy")
            result.add(go)
            pending.extend(self.children[go])
        return result

    def semantic_cross_refs(self, members):
        """Return unique external object links crossing member boundaries.

        Structural hierarchy links are excluded: a migrated group would be
        reparented to its new scene root. Retain semantic inspector/LOD links.
        A non-GameObject asset GUID isn't a scene reference and is ignored.
        """
        outgoing, incoming = set(), set()
        for source, block in self.blocks.items():
            src_go = self.owner.get(source)
            # Transform hierarchy children/parents and Unity scene roots are
            # structural references, rewritten automatically when reparenting.
            # Counting them as inspector dependencies would mask the actual
            # MobileRuntime -> MeshCollider cross-scene safety blocker.
            if block.kind in (4, 1660057539):
                continue
            src_inside = src_go in members
            for line in block.body.splitlines():
                if "m_Father:" in line or "m_CorrespondingSourceObject:" in line:
                    continue
                for found in _REF.finditer(line):
                    target = int(found.group(1))
                    dst_go = self.owner.get(target)
                    if dst_go is None or dst_go == src_go:
                        continue
                    if src_inside != (dst_go in members):
                        record = (source, target)
                        (outgoing if src_inside else incoming).add(record)
        return outgoing, incoming

    def report(self):
        island = self.find_unique("Aaris_Island_2km")
        districts = self.find_unique("Districts")
        terrain = self.find_unique("Terrain_2x2")
        props = self.find_unique("BattleRoyaleProps")
        if self.parent_go[districts] != island or self.parent_go[terrain] != island:
            raise ValueError("Authored terrain/district ownership changed")
        totals = Counter(block.kind for block in self.blocks.values())
        rows = []
        for district in sorted(self.children[districts], key=lambda g: self.names[g]):
            members = self.descendants(district)
            types = Counter(kind for go in members for kind in self.component_types[go])
            outgoing, incoming = self.semantic_cross_refs(members)
            rows.append({
                "name": self.names[district],
                "objects": len(members),
                "mesh_renderers": types[23],
                "lod_groups": types[205],
                "mesh_colliders": types[64],
                "box_colliders": types[65],
                "inbound_inspector_refs": len(incoming),
                "outbound_inspector_refs": len(outgoing),
                # No automatic removal of physical cover, LOD meshes, or
                # externally referenced objects. The streaming tool must
                # create/test faithful persistent proxies first.
                "direct_move_safe": not (types[64] or types[65] or
                                         types[205] or incoming or outgoing),
            })
        other_protected = []
        for name, obj in (("Terrain_2x2", terrain),
                          ("BattleRoyaleProps", props),
                          ("FoundationsAndAprons",
                           self.find_unique("FoundationsAndAprons")),
                          ("RoadNetwork", self.find_unique("RoadNetwork"))):
            members = self.descendants(obj)
            other_protected.append({"name": name, "objects": len(members)})
        report = {
            "scene": SCENE,
            "game_objects": len(self.names),
            "terrain_objects": totals[218],
            "mesh_renderers": totals[23],
            "lod_groups": totals[205],
            "mesh_colliders": totals[64],
            "box_colliders": totals[65],
            "districts": rows,
            "persistent_groups": other_protected,
            "ready_for_district_migration": all(r["direct_move_safe"] for r in rows),
            "outcome": ("SAFE" if all(r["direct_move_safe"] for r in rows) else
                        "BLOCKED_PENDING_COLLISION_LOD_AND_REFERENCE_PROXY_VALIDATION"),
        }
        return report


def audit_scene(path: Path):
    if not path.is_file():
        raise ValueError("Missing scene: " + str(path))
    return WorldScene(path.read_text(encoding="utf-8")).report()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scene", type=Path,
                        default=Path(__file__).resolve().parents[2] / SCENE)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--require-safe", action="store_true",
                        help="Fail closed until collider, LOD and references are safe")
    args = parser.parse_args(argv)
    try:
        report = audit_scene(args.scene)
    except ValueError as exc:
        print("WORLD_PARTITION_PREFLIGHT_INVALID: " + str(exc), file=sys.stderr)
        return 2
    if args.json:
        print(json.dumps(report, indent=2, sort_keys=True))
    else:
        print("WORLD_PARTITION_PREFLIGHT " + report["scene"])
        print("GAME_OBJECTS={game_objects} TERRAIN={terrain_objects} "
              "MESH_RENDERERS={mesh_renderers} LOD_GROUPS={lod_groups} "
              "MESH_COLLIDERS={mesh_colliders} BOX_COLLIDERS={box_colliders}"
              .format(**report))
        for d in report["districts"]:
            print("DISTRICT {name}: objects={objects} lod={lod_groups} "
                  "mesh_collider={mesh_colliders} incoming={inbound_inspector_refs} "
                  "outgoing={outbound_inspector_refs} safe={direct_move_safe}"
                  .format(**d))
        print("DIRECT_DISTRICT_MOVE=" +
              ("SAFE" if report["ready_for_district_migration"] else "BLOCKED"))
    return 1 if args.require_safe and not report["ready_for_district_migration"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
