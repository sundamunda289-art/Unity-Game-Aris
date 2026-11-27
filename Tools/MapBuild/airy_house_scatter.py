"""Place low-rise houses on the real 0.488 m terrain grid without editing it."""
import json
from math import cos, hypot, radians, sin
from pathlib import Path

import numpy as np

from airy_house_geometry import HOUSES, build


def rotate(x, z, yaw):
    angle = radians(yaw)
    return x*cos(angle) + z*sin(angle), z*cos(angle) - x*sin(angle)


def local_limits(geometry):
    points = np.array(geometry.collider.arrays()[0])[:,(0,2)]
    route = np.array(geometry.route)[:,(0,2)]
    limits = np.vstack((points, route))
    return limits.min(axis=0) - .75, limits.max(axis=0) + .75


class Terrain:
    def __init__(self, root):
        import UnityPy

        self.tiles = {}
        lookup = root / "Assets/TerrainDemoScene_URP/Terrain/Data"
        for x in (1, 2):
            for z in (1, 2):
                path, = lookup.glob(f"Terrain_{x}_{z}_*.asset")
                if path.stat().st_size < 4096:
                    raise ValueError(f"Hydrate native TerrainData before placing houses: {path}")
                data, = [item for item in UnityPy.load(str(path)).objects if item.type.name == "TerrainData"]
                heightmap = data.parse_as_dict()["m_Heightmap"]
                heights = np.asarray(heightmap["m_Heights"], dtype=np.float32).reshape(2049,2049)
                self.tiles[x,z] = heights*(800./32766.) - 120.

    def heights(self, x, z):
        x, z = np.broadcast_arrays(x, z)
        result = np.full(x.shape, -120., dtype=float)
        for (i,j), tile in self.tiles.items():
            ox, oz = (i-2)*1000, (j-2)*1000
            mask = (x >= ox) & (x <= ox+1000) & (z >= oz) & (z <= oz+1000)
            ix, iz = (x[mask]-ox)*2.048, (z[mask]-oz)*2.048
            cx = np.minimum(ix.astype(int),2047)
            cz = np.minimum(iz.astype(int),2047)
            fx, fz = ix-cx, iz-cz
            result[mask] = ((tile[cz,cx]*(1-fx)+tile[cz,cx+1]*fx)*(1-fz) +
                            (tile[cz+1,cx]*(1-fx)+tile[cz+1,cx+1]*fx)*fz)
        return result


class Placement:
    def __init__(self, root, terrain, layout, reserved, towers):
        self.height = terrain.heights
        self.reserved = list(reserved)
        self.towers = np.asarray(towers)
        self.road_a, self.road_b, width = [], [], []
        for road in layout["roads"]:
            points = [(x,z) for x, _, z in road["points"]]
            for a,b in zip(points, points[1:]):
                self.road_a.append(a)
                self.road_b.append(b)
                width.append(float(road.get("width_m", road.get("width", 8))) * .5 + 3)
        self.road_a, self.road_b, self.road_width = map(np.asarray, (self.road_a,self.road_b,width))
        self.road_delta = self.road_b - self.road_a
        self.road_sq = np.maximum(np.sum(self.road_delta**2,axis=1),1e-8)
        self.districts = np.array([(d["x"],d["z"],d["width"]*.5,d["depth"]*.5) for d in layout["districts"]])
        native = [json.loads(line) for path in sorted((root/"Docs/TerrainInstances").glob("*.jsonl"))
                  for line in path.read_text().splitlines() if line]
        self.vegetation = np.array([(p["position_m"][0],p["position_m"][2],
                                     p["width_scale"] * (3.5 if p["category"]=="rock" else 2.8) + .55,
                                     p["category"]=="tree") for p in native])
        self.geometry = {name: build(house) for name,house in HOUSES.items()}
        self.limits = {name: local_limits(g) for name,g in self.geometry.items()}
        self.pads = {}
        self.approaches = {}
        for name, (lo,hi) in self.limits.items():
            x,z = np.meshgrid(np.linspace(lo[0],hi[0],int((hi[0]-lo[0])/.46)+2),
                              np.linspace(lo[1],hi[1],int((hi[1]-lo[1])/.46)+2))
            self.pads[name] = x.reshape(-1), z.reshape(-1)
            first = self.geometry[name].route[0]
            self.approaches[name] = np.array((first[0], first[2]))

    def road_distance(self, x, z):
        query = np.array((x,z))-self.road_a
        t = np.clip(np.sum(query*self.road_delta,axis=1)/self.road_sq,0,1)
        return np.linalg.norm(query-self.road_delta*t[:,None],axis=1)-self.road_width

    def validate(self, name, x, z, yaw):
        lo,hi = self.limits[name]
        dx,dz = rotate(np.array((lo[0],hi[0],hi[0],lo[0])),np.array((lo[1],lo[1],hi[1],hi[1])),yaw)
        xmin,xmax,zmin,zmax = x+dx.min(),x+dx.max(),z+dz.min(),z+dz.max()
        if not (-955 < xmin < xmax < 955 and -955 < zmin < zmax < 955):
            return None
        for a,b,c,d in self.reserved:
            if xmin-1.4 < c and xmax+1.4 > a and zmin-1.4 < d and zmax+1.4 > b:
                return None
        radius = max(np.hypot(dx,dz))
        if self.road_distance(x,z).min() < radius:
            return None
        offsets = self.vegetation[:,:2] - (x,z)
        near = np.sum(offsets**2, axis=1) < (radius+self.vegetation[:,2])**2
        if np.any(near):
            lx,lz = rotate(*offsets[near].T,-yaw)
            verge = self.vegetation[near,2]
            vx = np.maximum(lo[0]-lx,np.maximum(lx-hi[0],0))
            vz = np.maximum(lo[1]-lz,np.maximum(lz-hi[1],0))
            if np.any(np.hypot(vx,vz)<verge):
                return None
        dx,dz = rotate(*self.pads[name],yaw)
        heights = self.height(x+dx,z+dz)
        floor = float(heights.max()) + .015
        if heights.min() < -36.8 or np.ptp(heights) > .42:
            return None
        # The actual stair start and the two approach edges stay below the
        # capsule step height. This rules out a flat pad with a ditch at its lip.
        ax,az = self.approaches[name]
        dx,dz = rotate(np.array((ax,ax-.6,ax+.6,ax)),np.array((az,az-.65,az-.65,az-1.18)),yaw)
        if float(np.abs(floor-self.height(x+dx,z+dz)).max()) > .265:
            return None
        return round(floor,4), (xmin,zmin,xmax,zmax), round(float(np.ptp(heights)),4)

    def add(self, name, preset, category, district, x, z, yaw, result):
        floor, bounds, relief = result
        self.reserved.append(bounds)
        return {"name":name,"prefab":preset,"category":category,"district":district,
                "position":[round(float(x),4),floor,round(float(z),4)],"yaw":float(yaw),
                "scale":[1,1,1],"footprint_height_delta_m":relief}

    def expansion(self, original):
        presets = {"cottage":"CourtyardHouse","bungalow":"RidgeHouse","detached":"TerraceHouse"}
        out = []
        offsets = [(x,z) for x in range(-40,41,5) for z in range(-40,41,5) if hypot(x,z)<=40]
        offsets.sort(key=lambda p:(hypot(*p),p))
        for site in original:
            if site["prefabKey"] == "watchtower":
                continue
            preset = presets[site["prefabKey"]]
            for dx,dz in offsets:
                x,z = site["x"]+dx,site["z"]+dz
                yaw = site["yaw"]
                result = self.validate(preset,x,z,yaw)
                if result is not None:
                    out.append(self.add(site["name"],preset,"previous-expansion",site["district"],x,z,yaw,result))
                    break
            else:
                raise ValueError(f"No native foundation found for previous site {site['name']}")
        return out

    def scatter(self, layout):
        rng = np.random.default_rng(680041)
        coords = rng.uniform(-911,911,size=(72000,2)).round(2)
        x,z = coords.T
        elevation = self.height(x,z)
        hill_relief = np.ptp(np.stack([self.height(x+dx,z+dz) for dx,dz in
                                      ((-32,0),(32,0),(0,-32),(0,32),(0,0))]),axis=0)
        distances = np.linalg.norm(coords[:,None,:]-self.towers[None,:,:],axis=2)
        nearest_tower = distances.argmin(axis=1)
        tower_distance = distances.min(axis=1)
        outside_pads = np.ones(len(x),dtype=bool)
        for dx,dz,hw,hd in self.districts:
            outside_pads &= (abs(x-dx) > hw+23) | (abs(z-dz) > hd+23)

        categories = (
            ("Tower", (tower_distance >= 28) & (tower_distance <= 118),
             ("RidgeHouse","CoastalHouse","BrokenPavilion")),
            ("Hill", outside_pads & (hill_relief >= 3.25) & (elevation > -27) & (tower_distance >= 65),
             ("CourtyardHouse","RidgeHouse","TerraceHouse")),
            ("Forest", outside_pads & (tower_distance >= 70),
             ("RidgeHouse","BrokenPavilion","LongBarracks","TerraceHouse")),
        )
        planned = []
        for kind, mask, presets in categories:
            accepted = 0
            tower_counts = {}
            for i in np.flatnonzero(mask & (elevation > -36.4)):
                x,z = coords[i]
                if any(hypot(x-s["position"][0],z-s["position"][2])<94 for s in planned):
                    continue
                tower = int(nearest_tower[i])
                if kind == "Tower" and tower_counts.get(tower,0)>=2:
                    continue
                if kind == "Forest":
                    nearby = np.sum((self.vegetation[:,:2]-(x,z))**2,axis=1) < 56**2
                    if np.count_nonzero(self.vegetation[nearby,3]) < 6:
                        continue
                distance = float(self.road_distance(x,z).min())
                if not 12 <= distance <= (245 if kind != "Forest" else 185):
                    continue
                preset = presets[accepted % len(presets)]
                orientations = (45,90,135,180,225,270,315)
                yaw = orientations[accepted % 7]
                result = self.validate(preset,x,z,yaw)
                if result is None and kind != "Tower":
                    for yaw in orientations:
                        result = self.validate(preset,x,z,yaw)
                        if result is not None:
                            break
                if result is None:
                    continue
                district = min(layout["districts"],key=lambda d:hypot(x-d["x"],z-d["z"]))["name"]
                accepted += 1
                name = f"AiryScatter_{kind}_{accepted:02d}_{district}"
                planned.append(self.add(name,preset,kind.lower(),district,x,z,yaw,result))
                tower_counts[tower] = tower_counts.get(tower,0)+1
                if accepted==7:
                    break
            if accepted != 7:
                raise ValueError(f"Only {accepted} valid {kind} houses; expected 7")
        return planned
