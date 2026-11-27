"""Original low-rise combat house meshes and their walkable collision shells."""
from dataclasses import dataclass
from math import ceil, hypot, sqrt

import numpy as np


MATERIALS = ("wall", "stone", "wood", "roof")


@dataclass(frozen=True)
class House:
    name: str
    width: float
    depth: float
    storeys: int
    stairs: str
    tint: str


HOUSES = {
    house.name: house for house in (
        House("RidgeHouse", 9.4, 8.8, 1, "straight", "linen"),
        House("CourtyardHouse", 10.6, 8.6, 1, "wrap", "ochre"),
        House("TerraceHouse", 11.4, 10.8, 2, "interior", "sage"),
        House("LongBarracks", 15.2, 9.6, 1, "straight", "sage"),
        House("BrokenPavilion", 9.6, 8.4, 1, "straight", "ochre"),
        House("CoastalHouse", 10.4, 9.2, 1, "straight", "linen"),
    )
}
STOREY = 2.85
WALL = 0.24
STAIR = 1.95


class Mesh:
    def __init__(self):
        self.faces = []

    def face(self, points, normal, material):
        a, b, c = (np.array(p) for p in points[:3])
        cross = np.cross(b - a, c - a)
        length = sqrt(float(cross @ cross))
        if length < 1e-6:
            return
        if float(cross @ normal) < 0:
            points = tuple(reversed(points))
            cross = -cross
        normal = cross / length
        axis = int(np.argmax(np.abs(normal)))
        u, v = ((2, 1), (0, 2), (0, 1))[axis]
        self.faces.append((tuple(points), normal, u, v, material))

    def prism(self, x0, x1, z0, z1, corners_y, thickness, mat):
        top = tuple((x, y, z) for (x, z), y in zip(
            ((x0, z0), (x1, z0), (x1, z1), (x0, z1)), corners_y))
        bottom = tuple((x, y - thickness, z) for x, y, z in top)
        self.face(top, (0, 1, 0), mat)
        self.face(bottom, (0, -1, 0), mat)
        for i, normal in enumerate(((0, 0, -1), (1, 0, 0), (0, 0, 1), (-1, 0, 0))):
            j = (i + 1) % 4
            self.face((top[i], top[j], bottom[j], bottom[i]), normal, mat)

    def box(self, x0, x1, y0, y1, z0, z1, mat):
        if min(x1-x0, y1-y0, z1-z0) > 0.002:
            self.prism(x0, x1, z0, z1, (y1,) * 4, y1-y0, mat)

    def arrays(self):
        positions, normals, uvs, indices, groups, materials = [], [], [], [], [], []
        for key in MATERIALS:
            first = len(indices)
            for points, normal, u, v, material in self.faces:
                if material != key:
                    continue
                base = len(positions)
                positions.extend(points)
                normals.extend([normal] * len(points))
                uvs.extend((p[u] * 0.44, p[v] * 0.44) for p in points)
                for i in range(1, len(points) - 1):
                    indices.extend((base, base+i, base+i+1))
            if len(indices) != first:
                materials.append(key)
                groups.append((first, len(indices)-first))
        return positions, normals, uvs, indices, groups, materials


class Geometry:
    def __init__(self, house):
        self.house = house
        self.lods = [Mesh(), Mesh(), Mesh()]
        self.collider = Mesh()
        self.route = []
        self.sockets = []

    def box(self, x0, x1, y0, y1, z0, z1, material, *, collide=True, lod=2):
        for mesh in self.lods[:lod + 1]:
            mesh.box(x0, x1, y0, y1, z0, z1, material)
        if collide:
            self.collider.box(x0, x1, y0, y1, z0, z1, "stone")

    def slab(self, x0, x1, z0, z1, y, mat, thickness=0.22, hole=None):
        if hole is None:
            self.box(x0, x1, y-thickness, y, z0, z1, mat)
            return
        a, b, c, d = hole
        for left, right, back, front in ((x0, a, z0, z1), (b, x1, z0, z1),
                                         (a, b, z0, c), (a, b, d, z1)):
            self.box(left, right, y-thickness, y, back, front, mat)

    def wall(self, axis, side, a, b, floor, height, openings):
        def section(u0, u1, y0, y1, depth=WALL, mat="wall", **kwargs):
            if axis == "z":
                self.box(u0, u1, y0, y1, side-depth*.5, side+depth*.5, mat, **kwargs)
            else:
                self.box(side-depth*.5, side+depth*.5, y0, y1, u0, u1, mat, **kwargs)

        offset = a
        for centre, width, base, top in sorted(openings):
            left, right = centre-width*.5, centre+width*.5
            if not offset <= left < right <= b or not base < top <= height:
                raise ValueError(f"Invalid wall opening in {self.house.name}")
            section(offset, left, floor, floor+height)
            section(left, right, floor, floor+base)
            section(left, right, floor+top, floor+height)
            section(left-.095, left, floor+base, floor+top, WALL+.06, "wood", collide=False, lod=1)
            section(right, right+.095, floor+base, floor+top, WALL+.06, "wood", collide=False, lod=1)
            section(left-.13, right+.13, floor+top, floor+top+.095, WALL+.08, "stone", collide=False, lod=1)
            if base > 0:
                section(left-.17, right+.17, floor+base-.085, floor+base,
                        WALL+.19, "stone", collide=False, lod=1)
            offset = right
        section(offset, b, floor, floor+height)

    def stairs(self, start, end):
        x, y, z = start
        a, b, c = end
        along_x = abs(a-x) > abs(c-z)
        length = hypot(a-x, c-z)
        if length == 0 or b <= y or abs(a-x)*abs(c-z) > 1e-6:
            raise ValueError("Stair flight must be aligned and ascending")
        h = STAIR*.5
        if along_x:
            x0, x1, z0, z1 = min(x,a), max(x,a), z-h, z+h
            low, high = (y, b) if a > x else (b, y)
            ramp = (low, high, high, low)
        else:
            x0, x1, z0, z1 = x-h, x+h, min(z,c), max(z,c)
            low, high = (y, b) if c > z else (b, y)
            ramp = (low, low, high, high)
        self.collider.prism(x0, x1, z0, z1, ramp, .28, "stone")
        self.lods[1].prism(x0, x1, z0, z1, ramp, .28, "stone")
        self.lods[2].prism(x0, x1, z0, z1, ramp, .28, "stone")
        steps = ceil((b-y)/.18)
        for i in range(steps):
            f0, f1 = i/steps, (i+1)/steps
            xx0, xx1 = sorted((x+(a-x)*f0, x+(a-x)*f1))
            zz0, zz1 = sorted((z+(c-z)*f0, z+(c-z)*f1))
            tread_top = y+(b-y)*f1
            if along_x:
                zz0, zz1 = z0, z1
            else:
                xx0, xx1 = x0, x1
            self.box(xx0, xx1, tread_top-.29, tread_top, zz0, zz1, "stone",
                     collide=False, lod=0)
        # Low continuous stone stringers keep the walking lane and outside
        # cover legible. They don't add a second walkable step surface.
        for sign in (-1, 1):
            if along_x:
                r0 = z+sign*(h+.065)-.065
                for mesh in self.lods[:2]:
                    mesh.prism(x0, x1, r0, r0+.13, tuple(v+.36 for v in ramp), .55, "wood")
            else:
                r0 = x+sign*(h+.065)-.065
                for mesh in self.lods[:2]:
                    mesh.prism(r0, r0+.13, z0, z1, tuple(v+.36 for v in ramp), .55, "wood")


def parapet(g, x, z, y, opening=None, min_x=None):
    x0 = -x if min_x is None else min_x
    lines = (("z", -z, x0, x), ("z", z, x0, x), ("x", x0, -z, z), ("x", x, -z, z))
    for axis, side, start, end in lines:
        # Leave interruptions in cover for counterplay and sightlines.
        gap = (end-start)*.42 + start
        segments = [(start, gap-.52), (gap+.52, end)]
        if opening is not None and opening[:2] == (axis, side):
            low, high = opening[2:]
            segments = [(a,min(b,low)) for a,b in segments] + [(max(a,high),b) for a,b in segments]
        for a, b in segments:
            if b <= a:
                continue
            if axis == "x":
                g.box(side-.11, side+.11, y, y+.82, a, b, "wall")
                g.box(side-.16, side+.16, y+.82, y+.9, a, b, "stone", collide=False, lod=1)
            else:
                g.box(a, b, y, y+.82, side-.11, side+.11, "wall")
                g.box(a, b, y+.82, y+.9, side-.16, side+.16, "stone", collide=False, lod=1)


def build(house):
    g = Geometry(house)
    x, z = house.width*.5, house.depth*.5
    y = STOREY*house.storeys
    ruin = house.name == "BrokenPavilion"
    barracks = house.name == "LongBarracks"
    g.slab(-x-.12, x+.12, -z-.12, z+.12, 0, "stone", .47)
    g.box(-x-.22, x+.22, -.38, -.20, -z-.22, z+.22, "roof", collide=False, lod=1)

    if ruin:
        # The west half is an open courtyard with progressively collapsed walls.
        for a,b,height in ((-x,-x+.8,1.38),(-x+.8,-x+2,.91),(-x+2,-x+3.1,.53)):
            g.box(a,b,0,height,z-.36,z,"wall")
            g.box(a,b,height-.08,height,z-.41,z+.05,"stone",collide=False,lod=1)
        g.wall("x", x, -z, z, 0, STOREY, [(0,1.9,.91,2.21)])
        g.wall("z", z, .32, x, 0, STOREY, [(2.35,1.85,0,2.28)])
        g.box(.44,.72,0,STOREY,-z-.12,-z+.14,"stone")
        g.box(x-.28,x,0,STOREY,-z-.12,-z+.14,"stone")
    else:
        for floor_number in range(house.storeys):
            floor = floor_number*STOREY
            upper = floor_number > 0
            entry_x = 3.10 if house.stairs == "interior" else -x*.32
            other_x = -x*.42 if house.stairs == "interior" else x*.33
            door = (entry_x, 2.10, 0, 2.31) if not upper else (entry_x,1.82,.92,2.22)
            exit_opening = (other_x,2.10,0,2.31)
            if upper:
                exit_opening = (other_x,2.25,0,2.30)
            g.wall("z", -z, -x, x, floor, STOREY,
                   [door, (-entry_x, 1.95, .94, 2.19)])
            g.wall("z", z, -x, x, floor, STOREY,
                   [exit_opening, (-other_x, 1.90, .94, 2.19)])
            for side in (-x, x):
                g.wall("x", side, -z, z, floor, STOREY,
                       [(-z*.49, 1.83,.94,2.16), (z*.49, 1.83,.94,2.16)])
                # Narrow corner masonry protects exposed plaster and breaks up the silhouette.
                for corner in (-z, z):
                    g.box(side-.16, side+.16, floor, floor+STOREY-.07,
                          corner-.16, corner+.16, "stone", collide=False, lod=1)
            if not upper:
                g.slab(entry_x-1.35,entry_x+1.35,-z-.78,-z+.12,0,"stone",.20)
                g.slab(other_x-1.32,other_x+1.32,z-.12,z+.68,0,"stone",.20)
                g.box(entry_x-1.48,entry_x+1.48,2.38,2.53,-z-1.03,-z+.08,"roof",collide=False,lod=1)
                for sign in (-1,1):
                    xx = entry_x+sign*1.36
                    g.box(xx-.07,xx+.07,0,2.4,-z-.91,-z-.77,"wood",collide=False,lod=0)
            else:
                g.slab(other_x-1.61,other_x+1.61,z-.12,z+1.25,floor,"stone")
                g.box(other_x-1.61,other_x+1.61,floor,floor+.88,z+1.12,z+1.27,"wall")
                for sign in (-1,1):
                    xx = other_x+sign*1.55
                    g.box(xx-.07,xx+.07,floor,floor+.88,z,z+1.25,"wood")

    deck_x = .28 if ruin else -x-.17
    roof_x, roof_z = x+.17, z+.17
    if house.stairs == "interior":
        a, b = 3.10, -3.10
        front, back = -3.62, 2.34
        g.slab(-x,x,-z,z,STOREY,"stone",hole=(a-1.07,a+1.07,front-.24,back))
        g.slab(-x-.17,roof_x,-roof_z,roof_z,y,"roof",hole=(b-1.07,b+1.07,front,back+.24))
        g.stairs((a,0,front),(a,STOREY,back))
        g.stairs((b,STOREY,back),(b,y,front))
        parapet(g,roof_x,roof_z,y)
        g.route = [(a,0,-z-.55),(a,0,front),(a,STOREY,back),(a,STOREY,back+1.05),
                   (b,STOREY,back+1.05),(b,STOREY,back),(b,y,front),(b,y,front-.78)]
        g.sockets = [(0,0,-2.6),(3.1,0,3.5),(-.8,0,3.25),
                     (0,STOREY,1.25),(-2.9,STOREY,-3.75),(1.35,y,1.8)]
    elif house.stairs == "wrap":
        east = x+1.21
        north = roof_z+1.02
        mid = y*.50
        west_end = -x*.34
        g.slab(deck_x,roof_x,-roof_z,roof_z,y,"roof")
        g.slab(east-1.04,east+1.04,-3.33,-2.35,0,"stone",.34)
        g.stairs((east,0,-2.35),(east,mid,z+.08))
        g.slab(east-1.06,east+1.06,z+.08,north+1.02,mid,"stone")
        g.stairs((east,mid,north),(west_end,y,north))
        g.slab(west_end-1.16,west_end+.02,roof_z,north+1.02,y,"stone")
        parapet(g,roof_x,roof_z,y,("z",roof_z,west_end-1.18,west_end+.08))
        g.route = [(east,0,-2.9),(east,0,-2.35),(east,mid,z+.08),(east,mid,north),
                   (west_end,y,north),(west_end-.56,y,north),(west_end-.56,y,roof_z-.86)]
        g.sockets = [(-x*.55,0,.95),(x*.52,0,-.85),(x*.54,y,-.92),(0,y,z*.48)]
    else:
        east = roof_x+1.00
        front, back = -z+.54, z-.92
        g.slab(deck_x,roof_x,-roof_z,roof_z,y,"roof")
        g.slab(east-1.04,east+1.04,front-.98,front,0,"stone",.34)
        g.stairs((east,0,front),(east,y,back))
        g.slab(roof_x,east+1.02,back,roof_z,y,"stone")
        parapet(g,roof_x,roof_z,y,("x",roof_x,back-.05,roof_z+.1),min_x=deck_x)
        g.route = [(east,0,front-.51),(east,0,front),(east,y,back),
                   (east,y,back+.56),(roof_x-.88,y,back+.56)]
        g.sockets = ([(1.8,0,-1.2), (-2.7,0,1.6), (2.25,y,-.56)] if ruin else
                     [(-x*.50,0,.83),(x*.52,0,-.63),(x*.31,y,-1.06)])
        if barracks:
            g.box(-.15,.15,0,1.16,-.65,1.58,"wall")
            g.box(-.22,.22,1.16,1.26,-.71,1.63,"wood",collide=False,lod=1)
            g.sockets.extend([(x*.49,0,z*.49),(-x*.55,y,z*.3)])
        elif house.name == "CoastalHouse":
            g.sockets.append((-x*.49,y,z*.44))

    # Projecting coping shades the walls with the existing main-light shadows.
    g.box(deck_x-.03,roof_x+.03,y-.23,y-.12,-roof_z-.05,-roof_z+.03,"wood",collide=False,lod=1)
    g.box(deck_x-.03,roof_x+.03,y-.23,y-.12,roof_z-.03,roof_z+.05,"wood",collide=False,lod=1)
    for k in range(3 if not ruin else 1):
        xx = (deck_x+roof_x)*.50 + (k-1)*.33
        g.box(xx-.12,xx+.12,y,y+.19,-roof_z+.54,-roof_z+.78,"stone",collide=False,lod=0)
    return g
