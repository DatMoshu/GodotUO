"""Lay built districts (GUO towns from town.py, or tools/layout_import builds) on a generated map's town lots.

The map generator writes the land as a tools/world project; each district is a tools/world project
of its own plus a scene of native multis. Both replace whole 8x8 blocks, so a district moves to a
new lot by moving its blocks, and the town is the district's blocks laid over the map's. A street
then runs from each of the lot's gates (where the generator's roads end) to the district's own
streets, so the roads arrive. The result is one built folder in layout_import's district shape,
which its district-stage writes and this tool's prove walks.
"""
from __future__ import annotations

import collections
import json
import shutil
from pathlib import Path

APRON = 8            # cells of the lot left around the district: the gate streets cross it
DISTRICT = 72


def block_path(world: Path, bx: int, by: int) -> Path:
    return world / "blocks" / "0" / f"{bx}_{by}.json"


def read_block(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def write_block(path: Path, block: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(block, indent=1), encoding="utf-8")


class World:
    """A tools/world project's blocks, cell access by world x, y, kept in memory until saved."""

    def __init__(self, root: Path):
        self.root = root
        self.cache: dict[tuple[int, int], dict] = {}
        self.dirty: set[tuple[int, int]] = set()

    def block(self, bx: int, by: int) -> dict:
        key = (bx, by)
        if key not in self.cache:
            self.cache[key] = read_block(block_path(self.root, bx, by))
        return self.cache[key]

    def put(self, bx: int, by: int, block: dict) -> None:
        block = {**block, "block": [bx, by]}
        self.cache[(bx, by)] = block
        self.dirty.add((bx, by))

    def land(self, x: int, y: int) -> tuple[int, int]:
        b = self.block(x // 8, y // 8)
        tile, z = b["land"][y % 8].split()[x % 8].split(":")
        return int(tile, 16), int(z)

    def set_land(self, x: int, y: int, tile: int, z: int) -> None:
        bx, by = x // 8, y // 8
        b = self.block(bx, by)
        row = b["land"][y % 8].split()
        row[x % 8] = f"{tile:04X}:{z}"
        b["land"][y % 8] = " ".join(row)
        self.dirty.add((bx, by))

    def statics_at(self, x: int, y: int) -> list[dict]:
        return [s for s in self.block(x // 8, y // 8)["statics"] if s["x"] == x % 8 and s["y"] == y % 8]

    def clear_statics(self, x: int, y: int) -> int:
        b = self.block(x // 8, y // 8)
        before = len(b["statics"])
        b["statics"] = [s for s in b["statics"] if not (s["x"] == x % 8 and s["y"] == y % 8)]
        if len(b["statics"]) != before:
            self.dirty.add((x // 8, y // 8))
        return before - len(b["statics"])

    def save(self) -> None:
        for bx, by in sorted(self.dirty):
            write_block(block_path(self.root, bx, by), self.cache[(bx, by)])
        self.dirty.clear()


def place_district(world: World, district: Path, wx: int, wy: int) -> int:
    """Copy the district's blocks so its corner lands on world wx, wy (both on the block grid)."""
    record = json.loads((district / "district.json").read_text(encoding="utf-8"))
    ox, oy = record["origin"]
    dbx, dby = (wx - ox) // 8, (wy - oy) // 8
    n = 0
    for path in sorted((district / "world" / "blocks" / "0").glob("*.json")):
        block = read_block(path)
        bx, by = block["block"]
        world.put(bx + dbx, by + dby, block)
        n += 1
    return n


def district_obstacles(district: Path, scene: dict) -> set[tuple[int, int]]:
    """District-local cells a street may not cross: every multi component and outdoor static."""
    cells = set()
    for part in scene["parts"]:
        cx, cy = part["centre"]
        for comp in json.loads((district / "parts" / f"{part['name']}.json").read_text(encoding="utf-8")):
            cells.add((comp[1] + cx, comp[2] + cy))
    record = json.loads((district / "district.json").read_text(encoding="utf-8"))
    ox, oy = record["origin"]
    for path in (district / "world" / "blocks" / "0").glob("*.json"):
        block = read_block(path)
        bx, by = block["block"]
        for s in block["statics"]:
            cells.add((bx * 8 + s["x"] - ox, by * 8 + s["y"] - oy))
    return cells


def street(world: World, start: tuple[int, int], inward: tuple[int, int], dx0: int, dy0: int,
           road_ids: set[int], blocked: set[tuple[int, int]]) -> list[tuple[int, int]]:
    """Cells from a gate across the apron, then the shortest way through the district to one of its
    road cells, avoiding buildings and statics. World coordinates; dx0, dy0 the district corner."""
    x, y = start
    path = [(x, y)]
    for _ in range(APRON):
        x, y = x + inward[0], y + inward[1]
        path.append((x, y))
    # x, y is now the district's first cell inside its edge
    seen = {(x, y): None}
    queue = collections.deque([(x, y)])
    end = None
    while queue:
        cx, cy = queue.popleft()
        if world.land(cx, cy)[0] in road_ids and (cx, cy) != (x, y):
            end = (cx, cy)
            break
        for nx, ny in ((cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1)):
            lx, ly = nx - dx0, ny - dy0
            if not (0 <= lx < DISTRICT and 0 <= ly < DISTRICT) or (nx, ny) in seen or (lx, ly) in blocked:
                continue
            seen[(nx, ny)] = (cx, cy)
            queue.append((nx, ny))
    if end is None:
        return []
    inner = []
    cur = end
    while cur is not None:
        inner.append(cur)
        cur = seen[cur]
    return path + inner[::-1][1:]


def route(a: tuple[int, int], b: tuple[int, int], dx0: int, dy0: int, blocked: set[tuple[int, int]]):
    """Shortest 4-connected way between two world cells inside the district, round its obstacles."""
    seen = {a: None}
    queue = collections.deque([a])
    while queue:
        cur = queue.popleft()
        if cur == b:
            out = []
            while cur is not None:
                out.append(cur)
                cur = seen[cur]
            return out[::-1]
        cx, cy = cur
        for nxt in ((cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1)):
            lx, ly = nxt[0] - dx0, nxt[1] - dy0
            if nxt in seen or not (0 <= lx < DISTRICT and 0 <= ly < DISTRICT) or ((lx, ly) in blocked and nxt != b):
                continue
            seen[nxt] = cur
            queue.append(nxt)
    return None


WAYPOINT = 5         # cells between the arrival walk's stops: the client's pathfinder reach stays short


def compose(map_world: Path, pois: dict, origin: tuple[int, int], districts: list[Path], out: Path, dump=None) -> dict:
    """map_world: the map generator's world project (export --world-project, at `origin`).
    districts: built layout_import districts, one per town in pois.json id order."""
    X, Y = origin
    built = out
    if built.exists():
        raise ValueError(f"{built} exists: compose writes a fresh folder")
    world_dir = built / "world"
    shutil.copytree(map_world / "blocks", world_dir / "blocks")
    first = json.loads((districts[0] / "district.json").read_text(encoding="utf-8"))
    shutil.copyfile(districts[0] / "world" / "project.json", world_dir / "project.json")
    project = json.loads((world_dir / "project.json").read_text(encoding="utf-8"))
    project["name"] = "guo_mapgen_districts"
    (world_dir / "project.json").write_text(json.dumps(project, indent=1), encoding="utf-8")
    licences = {(d / "world" / "SOURCE-LICENSE.txt").read_text(encoding="utf-8") for d in districts
                if (d / "world" / "SOURCE-LICENSE.txt").exists()}
    if licences:
        (world_dir / "SOURCE-LICENSE.txt").write_text("\n\n".join(sorted(licences)), encoding="utf-8")
    (built / "parts").mkdir(parents=True)
    world = World(world_dir)

    towns = sorted((p for p in pois["pois"] if p["kind"] == "Town"), key=lambda p: p["id"])
    if len(towns) < len(districts):
        raise ValueError(f"{len(towns)} towns for {len(districts)} districts")
    parts, tour, jumps, placed = [], [], {}, []
    for index, (town, district) in enumerate(zip(towns, districts)):
        fx1, fy1, fx2, fy2 = town["footprint"]
        if fx2 - fx1 + 1 < DISTRICT + 2 * APRON or fy2 - fy1 + 1 < DISTRICT + 2 * APRON:
            raise ValueError(f"town {town['id']} lot {fx2 - fx1 + 1}x{fy2 - fy1 + 1} is under {DISTRICT + 2 * APRON}")
        # district corner, in map cells and in world cells; both on the 8x8 block grid
        mx, my = fx1 + APRON, fy1 + APRON
        wx, wy = X + mx, Y + my
        if wx % 8 or wy % 8:
            raise ValueError(f"town {town['id']}: district corner {wx},{wy} is off the block grid")
        record = json.loads((district / "district.json").read_text(encoding="utf-8"))
        if record["status"] != "native-valid":
            raise ValueError(f"{district.name} is not native-valid")
        scene = json.loads((district / "scene.json").read_text(encoding="utf-8"))
        blocks = place_district(world, district, wx, wy)
        tag = f"t{index + 1}"
        road_ids = set(record["land_library"]["road"])
        blocked = district_obstacles(district, scene)
        # gate streets: N, S, W, E gates step inward across the apron
        streets = []
        for (gx, gy), inward in zip(town["gates"], ((0, 1), (0, -1), (1, 0), (-1, 0))):
            cells = street(world, (X + gx, Y + gy), inward, wx, wy, road_ids, blocked)
            if not cells:
                streets.append({"gate": [gx, gy], "cells": 0, "joined": False})
                continue
            cleared = 0
            for k, (cx, cy) in enumerate(cells):
                tile = sorted(road_ids)[(cx * 7 + cy * 13) % len(road_ids)]
                world.set_land(cx, cy, tile, 0)
                cleared += world.clear_statics(cx, cy)
            streets.append({"gate": [gx, gy], "cells": len(cells), "joined": True, "statics_cleared": cleared,
                            "end": [cells[-1][0] - X, cells[-1][1] - Y], "path": [[c[0] - X, c[1] - Y] for c in cells]})
        # the scene's parts and tour, district-local to map-local (the map origin is the site)
        for part in scene["parts"]:
            name = f"{tag}_{part['name']}"
            shutil.copyfile(district / "parts" / f"{part['name']}.json", built / "parts" / f"{name}.json")
            b = part["bounds"]
            parts.append({**part, "name": name, "centre": [part["centre"][0] + mx, part["centre"][1] + my],
                          "bounds": [b[0] + mx, b[1] + my, b[2] + mx, b[3] + my]})
        # the tour arrives as a traveller does: on a generated road outside a gate (a gate the
        # map's roads reach, when the dump says which), through the gate, up its street
        entry = 0
        if dump is not None:
            for k, ((gx, gy), inward) in enumerate(zip(town["gates"], ((0, 1), (0, -1), (1, 0), (-1, 0)))):
                ox_, oy_ = gx - 3 * inward[0], gy - 3 * inward[1]
                if streets[k]["joined"] and 0 <= ox_ < dump.width and 0 <= oy_ < dump.height and dump.biome[oy_, ox_] == 18:
                    entry = k
                    break
        (gx, gy), inward = town["gates"][entry], ((0, 1), (0, -1), (1, 0), (-1, 0))[entry]
        name = f"{tag}_road_approach"
        tour.append({"name": name, "x": gx - 8 * inward[0], "y": gy - 8 * inward[1], "z": 0})
        jumps[name] = "xy"
        tour.append({"name": f"{tag}_gate", "x": gx, "y": gy, "z": 0})
        if streets[entry]["joined"]:
            # up the gate's street, then on to the district tour's first stop, a stop every few cells
            way = [(X + c[0], Y + c[1]) for c in streets[entry]["path"]]
            first_stop = scene["tour"][0]
            onward = route(way[-1], (wx + first_stop["x"], wy + first_stop["y"]), wx, wy, blocked)
            if onward:
                way += onward[1:-1]
            for k in range(WAYPOINT, len(way), WAYPOINT):
                tour.append({"name": f"{tag}_arrive{k}", "x": way[k][0] - X, "y": way[k][1] - Y, "z": 0})
        tour += [{**s, "name": f"{tag}_{s['name']}", "x": s["x"] + mx, "y": s["y"] + my} for s in scene["tour"]]
        placed.append({"town": town["id"], "district": record["name"], "source": district.name,
                       "generator": record.get("generator", "layout_import"),
                       "lot": town["footprint"], "map_corner": [mx, my], "world_corner": [wx, wy],
                       "blocks": blocks, "entry_gate": town["gates"][entry], "parts": len(scene["parts"]), "tour_stops": len(scene["tour"]),
                       "streets": streets})
    world.save()
    width, height = pois["width"], pois["height"]
    problems = [f"town {p['town']}: gate {s['gate']} has no street into the district"
                for p in placed for s in p["streets"] if not s["joined"]]
    scene = {"format": 1, "kind": "scene", "name": "guo_mapgen_districts", "bounds": [0, 0, width - 1, height - 1],
             "parts": parts, "tour": tour, "jumps": jumps,
             "notes": ["Generated map land and district roads/statics are in the companion world project."],
             "valid": True, "problems": []}
    (built / "scene.json").write_text(json.dumps(scene, indent=1), encoding="utf-8")
    record = {"format": 1, "kind": "uo-district", "name": "guo_mapgen_districts", "origin": [X, Y],
              "size": [width, height], "theme": first["theme"], "towns": placed, "parcels": [],
              "status": "native-valid", "problems": [], "street_problems": problems, "proof_status": "not-run"}
    (built / "district.json").write_text(json.dumps(record, indent=1), encoding="utf-8")
    return record
