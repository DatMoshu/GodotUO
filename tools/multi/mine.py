"""Data-mine the client's multis and the buildings in the map statics into a catalogue.

Everything the generators know about how UO builds a building comes from here:
piece families by material, role and facing; storey heights; stair runs; door,
sign and roof practice; and which furnishings go together. The catalogue is
derived from client data, so it lives in build/ and is never committed.

Outputs (in the catalogue folder):
  multis.json      one record per multi: size, storeys, stairs, doors, roof, materials
  families.json    per material: role -> facing -> item ids by use, with heights
  pieces.json      per item id: name, flags, role, material, facing, uses, z above floor
  stairs.json      every stair run: item ids, rise per step, length, storeys joined
  buildings.json   clusters of wall statics on a facet, with their furnishing
  furnishing.json  what furnishing stands where (against a wall, on a surface) and with what
  summary.json     the counts that describe the whole set
"""
from __future__ import annotations

import collections
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from guo.uomap import STATIC_SIZE, open_facet  # noqa: E402
from guo.uoread import TileData  # noqa: E402
from multifile import Component, Multis, bounds  # noqa: E402
from pieces import STRUCTURAL, flag_names, majority, material, role  # noqa: E402

BOAT_WORDS = ("ship", "deck", "sail", "mast", "tiller", "hatch", "plank")
FOLIAGE = 0x20000
DUNGEON_X = 5120                     # felucca/trammel: x beyond this is dungeons and the lost lands


class Pieces:
    """Accumulates per-item statistics over every building seen."""

    def __init__(self, td: TileData):
        self.td = td
        self.tiles: dict[int, dict] = {}
        self.uses = collections.Counter()
        self.static_uses = collections.Counter()
        C = lambda: collections.defaultdict(collections.Counter)  # noqa: E731
        self.context = C()        # item -> wall material of the building level it stands in
        self.sig = C()            # item -> neighbour signature (N/E/S/W walls at its z)
        self.roof_side = C()      # item -> which slope of a roof it is
        self.step = C()           # item -> ascent direction + signature, for stair pieces
        self.above_floor = C()
        self.in_multis: dict[int, set] = collections.defaultdict(set)

    def tile(self, item: int) -> dict:
        t = self.tiles.get(item)
        if t is None:
            t = self.td.static(item) or {"flags": 0, "height": 0, "name": f"item {item:#x}"}
            self.tiles[item] = t
        return t

    def material_of(self, item: int) -> str:
        """Walls, windows, posts and doors take the wall material of where they are used
        (tile names are unreliable: some plaster corners are named after other things)."""
        t = self.tile(item)
        if role(t) in ("wall", "window", "post", "door") and self.context[item]:
            return majority(self.context[item])
        return material(t)

    def to_json(self) -> dict:
        out = {}
        for item in sorted(set(self.uses) | set(self.static_uses)):
            t = self.tile(item)
            out[f"{item:#06x}"] = {
                "name": t["name"], "flags": flag_names(t["flags"]), "height": t["height"],
                "role": role(t), "material": self.material_of(item),
                "signature": dict(self.sig[item].most_common(4)),
                "roof_side": dict(self.roof_side[item].most_common(3)),
                "step": dict(self.step[item].most_common(3)),
                "uses_multi": self.uses[item], "uses_statics": self.static_uses[item],
                "multis": len(self.in_multis[item]),
                "in": sorted(t for t in self.in_multis[item] if isinstance(t, int))[:256],
                "z_above_floor": dict(self.above_floor[item].most_common(6)),
            }
        return out


DIRS = (("N", 0, -1), ("E", 1, 0), ("S", 0, 1), ("W", -1, 0))
OPPOSITE = {"N": "S", "S": "N", "E": "W", "W": "E"}


def signature(cells, x: int, y: int, z: int) -> str:
    """Which of N/E/S/W hold a wall at the same z: '' (post) .. 'NESW' (cross)."""
    return "".join(d for d, dx, dy in DIRS if (x + dx, y + dy, z) in cells)


def roof_side(roof: dict, x: int, y: int, z: int) -> str:
    """The slope a roof tile belongs to: W, E, N, S (the side it faces), a corner
    such as NW, ridge_x / ridge_y along the top, or cap."""
    up = [d for d, dx, dy in DIRS if any(0 < zz - z <= 6 for zz in roof.get((x + dx, y + dy), ()))]
    if up:
        return "".join(sorted({OPPOSITE[d] for d in up}, key="NSEW".index))
    flat = {d for d, dx, dy in DIRS if z in roof.get((x + dx, y + dy), ())}
    if flat & {"N", "S"} and not flat & {"E", "W"}:
        return "ridge_y"
    if flat & {"E", "W"} and not flat & {"N", "S"}:
        return "ridge_x"
    return "cap"


def storeys_of(floor_zs: collections.Counter) -> list[int]:
    if not floor_zs:
        return []
    top = max(floor_zs.values())
    return sorted(z for z, n in floor_zs.items() if n >= max(3, top * 0.15))


def floor_below(storeys: list[int], z: int) -> int | None:
    below = [s for s in storeys if s <= z]
    return max(below) if below else None


def stair_runs(stairs: list[tuple[int, int, int, int]], storeys: list[int]) -> list[dict]:
    """Groups stair tiles (item, x, y, z) into runs of 4-connected tiles."""
    at = collections.defaultdict(list)
    for s in stairs:
        at[(s[1], s[2])].append(s)
    seen, runs = set(), []
    for key in at:
        if key in seen:
            continue
        stack, cells = [key], []
        seen.add(key)
        while stack:
            cx, cy = stack.pop()
            cells += at[(cx, cy)]
            for n in ((cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1)):
                if n in at and n not in seen:
                    seen.add(n)
                    stack.append(n)
        cells.sort(key=lambda s: s[3])
        lo, hi = cells[0], cells[-1]
        zs = sorted({c[3] for c in cells})
        rises = collections.Counter(b - a for a, b in zip(zs, zs[1:]))
        runs.append({
            "items": sorted({f"{c[0]:#06x}" for c in cells}),
            "tiles": len(cells), "z_from": lo[3], "z_to": hi[3],
            "rise": majority(rises) or 0,
            "direction": [(hi[1] > lo[1]) - (hi[1] < lo[1]), (hi[2] > lo[2]) - (hi[2] < lo[2])],
            "from_storey": floor_below(storeys, lo[3]), "to_storey": floor_below(storeys, hi[3] + 5),
            "cells": [[c[1], c[2], c[3], f"{c[0]:#06x}"] for c in cells],
        })
    return runs


def analyse(comps: list[Component], pieces: Pieces, tag) -> dict:
    """One building's structure, as a list of components relative to any origin."""
    roles = collections.defaultdict(list)
    for c in comps:
        t = pieces.tile(c.item)
        r = role(t, c.visible)
        roles[r].append(c)
        if r == "marker":
            roles["marker:" + role(t)].append(c)
    floor_zs = collections.Counter(c.z for c in roles["floor"])
    storeys = storeys_of(floor_zs)
    walls = roles["wall"] + roles["window"] + roles["post"]
    wall_cells = {(c.x, c.y, c.z) for c in walls}
    overall = majority(collections.Counter(material(pieces.tile(c.item)) for c in roles["wall"]))
    level_mat = {}
    for z in {c.z for c in walls}:
        level_mat[z] = majority(collections.Counter(material(pieces.tile(c.item)) for c in roles["wall"] if c.z == z)) or overall
    roof_at = collections.defaultdict(set)
    for c in roles["roof"]:
        roof_at[(c.x, c.y)].add(c.z)
    surface_at = collections.defaultdict(set)
    for c in roles["floor"] + roles["stair"]:
        surface_at[(c.x, c.y)].add(c.z + pieces.tile(c.item)["height"])
    stair_cells = {(s.x, s.y, s.z) for s in roles["stair"]}
    floor_cells = {(f.x, f.y, f.z) for f in roles["floor"]}
    for c in comps:
        pieces.in_multis[c.item].add(tag)
        t = pieces.tile(c.item)
        r = role(t, c.visible)
        if r in ("wall", "window", "post"):
            pieces.sig[c.item][signature(wall_cells, c.x, c.y, c.z)] += 1
        elif r == "floor":
            pieces.sig[c.item][signature(floor_cells, c.x, c.y, c.z)] += 1
        if role(t) in ("wall", "window", "post", "door"):
            m = level_mat.get(c.z) or overall
            if m:
                pieces.context[c.item][m] += 1
        if r == "roof":
            pieces.roof_side[c.item][roof_side(roof_at, c.x, c.y, c.z)] += 1
        if r == "stair":
            top = c.z + t["height"]
            up = [d for d, dx, dy in DIRS if (c.x + dx, c.y + dy, c.z) not in stair_cells
                  and any(top <= zz <= c.z + 12 for zz in surface_at.get((c.x + dx, c.y + dy), ()))]
            pieces.step[c.item][("".join(up) or "-") + "/" + (signature(stair_cells, c.x, c.y, c.z) or "-")] += 1
        f = floor_below(storeys, c.z)
        if f is not None and r not in ("floor",):
            pieces.above_floor[c.item][c.z - f] += 1
    wall_h = collections.Counter()
    for s in storeys:
        for c in walls:
            if c.z == s:
                wall_h[pieces.tile(c.item)["height"]] += 1
    roofs = roles["roof"]
    roof = None
    if roofs:
        rz = sorted({c.z for c in roofs})
        steps = collections.Counter(b - a for a, b in zip(rz, rz[1:]))
        roof = {"z_from": rz[0], "z_to": rz[-1], "step": majority(steps), "tiles": len(roofs),
                "materials": dict(collections.Counter(material(pieces.tile(c.item)) for c in roofs).most_common(3)),
                "above_top_storey": rz[0] - storeys[-1] if storeys else None}
    stairs = [(c.item, c.x, c.y, c.z) for c in roles["stair"]]
    doors = [[c.x, c.y, c.z, f"{c.item:#06x}", c.visible] for c in roles["door"] + roles["marker:door"]]
    mats = collections.Counter(material(pieces.tile(c.item)) for c in walls)
    return {
        "components": len(comps),
        "roles": {r: len(v) for r, v in sorted(roles.items())},
        "storeys": storeys,
        "storey_steps": [b - a for a, b in zip(storeys, storeys[1:])],
        "wall_height": dict(wall_h.most_common(3)),
        "wall_materials": dict(mats.most_common(4)),
        "floor_materials": dict(collections.Counter(material(pieces.tile(c.item)) for c in roles["floor"]).most_common(3)),
        "roof": roof,
        "stairs": stair_runs(stairs, storeys),
        "doors": doors,
        "markers": [[c.x, c.y, c.z, f"{c.item:#06x}", pieces.tile(c.item)["name"]] for c in roles["marker"]],
    }


def kind_of(comps: list[Component], a: dict, pieces: Pieces) -> str:
    names = collections.Counter(pieces.tile(c.item)["name"].lower().split(" ")[0] for c in comps)
    if sum(names[w] for w in BOAT_WORDS) >= len(comps) * 0.4:
        return "boat"
    if len(comps) <= 4:
        return "marker"
    walls = a["roles"].get("wall", 0) + a["roles"].get("window", 0)
    if walls == 0:
        return "open"                   # camps, foundations, plots
    n = len(a["storeys"])
    if a["components"] > 1500 or n >= 4:
        return "castle-or-keep"
    if n >= 3 or a["components"] > 700:
        return "large-house"
    if n == 2:
        return "two-storey"
    return "one-storey"


def mine_multis(data_dir: Path, pieces: Pieces) -> list[dict]:
    m = Multis(data_dir)
    out = []
    for i in m.ids():
        comps = m.get(i) or []
        if not comps:
            continue
        for c in comps:
            pieces.uses[c.item] += 1
        a = analyse(comps, pieces, i)
        x0, y0, x1, y1 = bounds(comps)
        out.append({"id": f"{i:#06x}", "kind": kind_of(comps, a, pieces),
                    "bounds": [x0, y0, x1, y1], "size": [x1 - x0 + 1, y1 - y0 + 1], **a})
    return out


def families(pieces: Pieces) -> dict:
    """What a generator picks pieces from, most used first:
      material -> "wall"|"window"|"post" -> height -> signature -> [ids]
      material -> "roof" -> side -> [ids]
      material -> "stair" -> "ascent/signature" -> [ids]
      material -> "floor" -> signature -> [ids]   (border pieces differ from the middle)
      material -> "door" -> "any" -> [ids]
    """
    fam: dict = {}

    def put(mat, keys, item):
        d = fam.setdefault(mat, {})
        for k in keys[:-1]:
            d = d.setdefault(str(k), {})
        d.setdefault(str(keys[-1]), []).append(f"{item:#06x}")

    ranked = sorted(set(pieces.uses) | set(pieces.static_uses),
                    key=lambda i: -(pieces.uses[i] * 4 + pieces.static_uses[i]))
    for item in ranked:
        t = pieces.tile(item)
        r = role(t)
        if r not in STRUCTURAL:
            continue
        mat = pieces.material_of(item)
        if r in ("wall", "window", "post"):
            for sig, _n in pieces.sig[item].most_common(3):
                put(mat, (r, t["height"], sig or "-"), item)
        elif r == "roof":
            for side, _n in pieces.roof_side[item].most_common(2):
                put(mat, ("roof", side), item)
        elif r == "stair":
            for key, _n in pieces.step[item].most_common(2):
                put(mat, ("stair", key), item)
        elif r == "floor":
            for sig, _n in pieces.sig[item].most_common(3):
                put(mat, ("floor", sig or "-"), item)
        else:
            put(mat, (r, "any"), item)
    return fam


# --- the map statics ------------------------------------------------------------------------

def scan_statics(data_dir: Path, facet: int, pieces: Pieces, x_limit: int = DUNGEON_X):
    """Every static on a facet west of x_limit: (item, x, y, z)."""
    out = []
    with open_facet(data_dir, facet) as f:
        for bx in range(min(f.width_blocks, x_limit >> 3)):
            for by in range(f.height_blocks):
                off, length = f.statics_span(f.number(bx, by))
                if not length:
                    continue
                for sid, x, y, z, _hue in struct.iter_unpack("<HBBbH", f.sta[off:off + length - length % STATIC_SIZE]):
                    if sid in (0, 0xFFFF):
                        continue
                    out.append((sid, (bx << 3) + x, (by << 3) + y, z))
                    pieces.static_uses[sid] += 1
    return out


def buildings(statics, pieces: Pieces, min_walls: int = 12, max_span: int = 64) -> tuple[list[dict], dict]:
    """Clusters 8-connected wall statics into buildings, then reads what stands inside each."""
    walls = collections.defaultdict(list)
    by_cell = collections.defaultdict(list)
    for s in statics:
        by_cell[(s[1], s[2])].append(s)
        t = pieces.tile(s[0])
        if role(t) in ("wall", "window", "post", "door") and not t["flags"] & FOLIAGE:
            walls[(s[1], s[2])].append(s)
    seen, out = set(), []
    furn_wall = collections.Counter()
    furn_on = collections.Counter()
    furn_total = collections.Counter()
    together = collections.defaultdict(collections.Counter)
    for key in walls:
        if key in seen:
            continue
        stack, cells = [key], []
        seen.add(key)
        while stack:
            c = stack.pop()
            cells.append(c)
            cx, cy = c
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    n = (cx + dx, cy + dy)
                    if n in walls and n not in seen:
                        seen.add(n)
                        stack.append(n)
        n_walls = sum(len(walls[c]) for c in cells)
        xs, ys = [c[0] for c in cells], [c[1] for c in cells]
        x0, y0, x1, y1 = min(xs), min(ys), max(xs), max(ys)
        if n_walls < min_walls or x1 - x0 >= max_span or y1 - y0 >= max_span or x1 == x0 or y1 == y0:
            continue
        inside = [s for x in range(x0, x1 + 1) for y in range(y0, y1 + 1) for s in by_cell.get((x, y), ())]
        comps = [Component(s[0], s[1] - x0, s[2] - y0, s[3]) for s in inside]
        a = analyse(comps, pieces, f"s{x0},{y0}")
        wall_cells = {(s[1], s[2]) for c in cells for s in walls[c]}
        surfaces = {(s[1], s[2], s[3] + pieces.tile(s[0])["height"]) for s in inside
                    if pieces.tile(s[0])["flags"] & 0x200 and role(pieces.tile(s[0])) == "deco"}
        deco = []
        for s in inside:
            t = pieces.tile(s[0])
            if role(t) != "deco" or t["flags"] & FOLIAGE:
                continue
            deco.append(s[0])
            furn_total[s[0]] += 1
            if any((s[1] + dx, s[2] + dy) in wall_cells for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
                furn_wall[s[0]] += 1
            if (s[1], s[2], s[3]) in surfaces:
                furn_on[s[0]] += 1
        kinds = set(deco)
        for i in kinds:
            for j in kinds:
                if i != j:
                    together[i][j] += 1
        out.append({"origin": [x0, y0], "size": [x1 - x0 + 1, y1 - y0 + 1], "walls": n_walls,
                    "furnishing": dict(collections.Counter(f"{i:#06x}" for i in deco).most_common(24)),
                    **{k: v for k, v in a.items() if k != "markers"}})
    top = [i for i, _ in furn_total.most_common(400)]
    furnishing = {}
    for i in top:
        t = pieces.tile(i)
        furnishing[f"{i:#06x}"] = {
            "name": t["name"], "placed": furn_total[i],
            "against_wall": round(furn_wall[i] / furn_total[i], 2),
            "on_surface": round(furn_on[i] / furn_total[i], 2),
            "with": {f"{j:#06x}": n for j, n in together[i].most_common(8)},
        }
    return out, furnishing


def mine(data_dir: Path, out: Path, facet: int = 0, statics: bool = True) -> dict:
    out.mkdir(parents=True, exist_ok=True)
    td = TileData(data_dir)
    pieces = Pieces(td)
    multis = mine_multis(data_dir, pieces)
    summary = {"multis": len(multis), "kinds": dict(collections.Counter(m["kind"] for m in multis).most_common())}
    blds, furn = [], {}
    if statics:
        st = scan_statics(data_dir, facet, pieces)
        blds, furn = buildings(st, pieces)
        summary.update({"facet": facet, "statics": len(st), "buildings": len(blds)})
    steps = collections.Counter(s for m in multis + blds for s in m["storey_steps"])
    rises = collections.Counter(r["rise"] for m in multis + blds for r in m["stairs"])
    first = collections.Counter(m["storeys"][0] for m in multis if m["storeys"] and m["kind"] != "boat")
    summary.update({"storey_steps": dict(steps.most_common(8)), "stair_rise": dict(rises.most_common(6)),
                    "ground_floor_z": dict(first.most_common(6))})
    runs = [dict(r, source=m.get("id") or m.get("origin")) for m in multis + blds for r in m["stairs"]]
    files = {"multis.json": multis, "families.json": families(pieces), "pieces.json": pieces.to_json(),
             "stairs.json": runs, "buildings.json": blds, "furnishing.json": furn, "summary.json": summary}
    for name, data in files.items():
        (out / name).write_text(json.dumps(data, indent=1), encoding="utf-8")
    return summary
