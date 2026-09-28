"""Mine how UO's own buildings are furnished into the decor database.

Sources (both read in place, read only):
  statics   buildings on a facet: clusters of wall statics, as tools/multi/mine.py finds
            them, then every static inside each cluster's bounds
  multis    the client's multis (mostly bare shells: they add room sizes and shapes)

Per building: storeys from the floor z levels; per storey the walls, door gaps and
windows; rooms (tools/decorate/rooms.py); the furnishing on each room's cells or
hung on its walls, with where it stands; furniture groups and their layouts
(templates); room types by content (classify.py).

`analyse()` is pure over (item, x, y, z) tuples and a tile lookup, so the test can
feed it a synthetic building.
"""
from __future__ import annotations

import collections
import hashlib
import json
import statistics
import struct
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE.parent / "multi"))
sys.path.insert(0, str(HERE))

import classify  # noqa: E402
import db  # noqa: E402
import rooms as R  # noqa: E402

DUNGEON_X = 5120                      # felucca/trammel: x beyond this is dungeons and the lost lands
STRUCT_ROLES = ("wall", "window", "post")


class Tiles:
    """Tiledata lookups with the derived role, furnishing flag and kind, cached per id."""

    def __init__(self, td):
        from pieces import role
        self._role = role
        self.td = td
        self.cache: dict[int, dict] = {}

    def get(self, item: int) -> dict:
        t = self.cache.get(item)
        if t is None:
            raw = self.td.static(item) or {"flags": 0, "height": 0, "name": f"item {item:#x}"}
            t = {"name": raw["name"], "flags": raw["flags"], "height": raw["height"]}
            t["role"] = self._role(t)
            t["furn"] = t["role"] in ("deco", "floor") and classify.is_furnishing(t)
            t["kind"] = classify.kind_of(t["name"]) if t["furn"] else ""
            self.cache[item] = t
        return t


def storeys_of(zs: collections.Counter) -> list[int]:
    """Floor levels: z values (merged within 3 of a more common one) that hold a good
    share of the floor tiles."""
    if not zs:
        return []
    reps: dict[int, int] = {}
    for z, n in sorted(zs.items(), key=lambda kv: (-kv[1], kv[0])):
        near = [r for r in reps if abs(r - z) <= 3]
        if near:
            reps[near[0]] += n
        else:
            reps[z] = n
    top = max(reps.values())
    return sorted(z for z, n in reps.items() if n >= max(3, top * 0.12))


@dataclass
class Found:
    """What analyse() finds in one building."""
    storeys: list[int]
    rooms: list[dict] = field(default_factory=list)


def analyse(statics: list[tuple[int, int, int, int]], tiles, land: dict | None = None,
            min_room: int = 4) -> Found:
    """Rooms and their furnishing in one building. `statics` are (item, x, y, z);
    `land` maps (x, y) to land z, for ground floors with no floor tiles."""
    floor_z = collections.Counter()
    for item, x, y, z in statics:
        t = tiles.get(item)
        if t["role"] == "floor" and not t["furn"]:
            floor_z[z] += 1
    storeys = storeys_of(floor_z)
    use_land = False
    if not storeys and land:
        lz = collections.Counter(land.values())
        storeys = [lz.most_common(1)[0][0]]
        use_land = True
    found = Found(storeys)
    for n, s in enumerate(storeys):
        top = storeys[n + 1] if n + 1 < len(storeys) else s + 20
        walls, windows, door_items, floor, stair = set(), set(), set(), set(), set()
        furn = []
        for item, x, y, z in statics:
            t = tiles.get(item)
            r = t["role"]
            if r in STRUCT_ROLES and s - 4 <= z <= s + 6 and z + t["height"] >= s + 4:
                walls.add((x, y))
                if r == "window":
                    windows.add((x, y))
            elif r == "door" and s - 4 <= z <= s + 6:
                door_items.add((x, y))
            elif r == "floor" and not t["furn"] and abs(z - s) <= 3:
                floor.add((x, y))
            elif r == "stair" and s - 2 <= z < top - 2:
                stair.add((x, y))
            if t["furn"] and s - 2 <= z < top - 2:
                furn.append((item, x, y, z))
        if use_land:
            floor = {c for c, z in land.items() if abs(z - s) <= 3}
        door_items -= walls
        # stair blocks are 'deco' by role but stand under the steps: leave their cells out too
        doors = R.door_gaps(floor | R._gaps_between(walls) | door_items, walls) | door_items
        rooms = R.segment(floor | door_items, walls, doors=doors, windows=windows, blocked=stair, min_cells=min_room)
        if not rooms:
            continue
        found.rooms += furnish(rooms, furn, walls, tiles, n, s)
    return found


def furnish(rooms: list[R.Room], furn: list, walls: set, tiles, storey: int, s: int) -> list[dict]:
    """Assign furnishing to rooms and describe each piece's place, then its groups."""
    owner = {}
    for i, r in enumerate(rooms):
        for c in r.cells:
            owner[c] = i
    per_room: list[list[dict]] = [[] for _ in rooms]
    for item, x, y, z in furn:
        c = (x, y)
        on_wall = ""
        if c in owner:
            i = owner[c]
        elif c in walls:
            # hung on a wall: the room it faces. Prefer a room south or east of the wall (the
            # side the client's wall art is drawn for), then any neighbour.
            i = None
            for d, dx, dy in (("N", 0, 1), ("W", 1, 0), ("S", 0, -1), ("E", -1, 0)):
                n = (x + dx, y + dy)
                if n in owner:
                    i, on_wall = owner[n], d
                    break
            if i is None:
                continue
        else:
            continue
        t = tiles.get(item)
        per_room[i].append({"item": item, "cell": c, "z": z, "z_above": z - s, "t": t, "on_wall": on_wall})
    out = []
    for r, items in zip(rooms, per_room):
        out.append(describe_room(r, items, walls, storey, s))
    return out


def describe_room(r: R.Room, items: list[dict], walls: set, storey: int, s: int) -> dict:
    x0, y0, _x1, _y1 = r.bbox
    door_cells = [c for c, _ in r.doors]
    window_cells = [c for c, _ in r.windows]
    by_cell = collections.defaultdict(list)
    for it in items:
        by_cell[it["cell"]].append(it)

    def near(c, cells):
        return int(any(abs(c[0] - d[0]) <= 1 and abs(c[1] - d[1]) <= 1 for d in cells))

    furn = []
    for it in sorted(items, key=lambda i: (i["cell"], i["z"], i["item"])):
        c, t = it["cell"], it["t"]
        if it["on_wall"]:
            against, place = it["on_wall"], "on-wall"
            facing = R.OPPOSITE[against]
        else:
            against = R.wall_sides(c, walls)
            if len(against) >= 2 and set(against) & {"N", "S"} and set(against) & {"E", "W"}:
                place = "corner"
            elif against:
                place = "wall"
            elif any((c[0] + dx, c[1] + dy) in walls for dx in (-1, 0, 1) for dy in (-1, 0, 1)):
                place = "open"
            else:
                place = "centre"
            facing = classify.facing_from_wall(against)
            if t["kind"] == "chair":
                # a chair faces the table beside it
                for d, dx, dy in R.DIRS:
                    if any(o["t"]["kind"] in ("table", "counter") for o in by_cell.get((c[0] + dx, c[1] + dy), ())):
                        facing = d
                        break
        on_item = None
        for o in by_cell[c]:
            ot = o["t"]
            if o is not it and ot["flags"] & classify.SURFACE and o["z"] < it["z"] <= o["z"] + ot["height"] + 1:
                on_item = o["item"]
        furn.append({"item": it["item"], "name": t["name"], "kind": t["kind"], "flags": t["flags"],
                     "height": t["height"], "x": c[0] - x0, "y": c[1] - y0, "z_above": it["z_above"],
                     "against": against, "place": place, "near_door": near(c, door_cells),
                     "near_window": near(c, window_cells), "facing": facing, "on_item": on_item,
                     "cell": c})
    kinds = collections.Counter(f["kind"] for f in furn)
    rtype, score = classify.classify(kinds)
    groups = group_items(furn, walls, r)
    return {"storey": storey, "z": s, "bbox": r.bbox, "size": r.size, "area": len(r.cells), "fill": round(r.fill, 3),
            "shape": r.shape, "enclosed": r.enclosed, "doors": [(c[0] - x0, c[1] - y0, d) for c, d in r.doors],
            "windows": [(c[0] - x0, c[1] - y0, d) for c, d in r.windows], "open_edges": r.open_edges,
            "type": rtype, "score": score, "kinds": dict(kinds), "furn": furn, "groups": groups,
            "cells": sorted((c[0] - x0, c[1] - y0) for c in r.cells)}


def group_items(furn: list[dict], walls: set, r: R.Room) -> list[dict]:
    """Furniture groups: the floor pieces 4-connected cell to cell (with everything
    stacked on them), and each wall-hung piece on its own."""
    # rugs lie under everything: they would join a whole room into one group
    floor_cells = {f["cell"] for f in furn if f["place"] != "on-wall" and f["kind"] != "rug"}
    groups = []
    for comp in R.components(floor_cells):
        members = [i for i, f in enumerate(furn) if f["place"] != "on-wall" and f["kind"] != "rug" and f["cell"] in comp]
        gx0 = min(c[0] for c in comp)
        gy0 = min(c[1] for c in comp)
        gx1 = max(c[0] for c in comp)
        gy1 = max(c[1] for c in comp)
        against = ""
        for d, dx, dy in R.DIRS:
            edge = [c for c in comp if (d == "N" and c[1] == gy0) or (d == "S" and c[1] == gy1)
                    or (d == "W" and c[0] == gx0) or (d == "E" and c[0] == gx1)]
            if edge and sum((c[0] + dx, c[1] + dy) in walls for c in edge) * 2 >= len(edge):
                against += d
        items = sorted([furn[i]["item"], furn[i]["cell"][0] - gx0, furn[i]["cell"][1] - gy0, furn[i]["z_above"]]
                       for i in members)
        groups.append({"members": members, "at": (gx0, gy0), "items": items, "w": gx1 - gx0 + 1,
                       "h": gy1 - gy0 + 1, "cells": len(comp), "against": against, "on_wall": 0})
    for i, f in enumerate(furn):
        if f["place"] == "on-wall":
            groups.append({"members": [i], "at": f["cell"], "items": [[f["item"], 0, 0, f["z_above"]]],
                           "w": 1, "h": 1, "cells": 1, "against": f["against"], "on_wall": 1})
    return groups


def template_key(items: list, on_wall: int) -> str:
    return hashlib.sha1(json.dumps([on_wall, items]).encode()).hexdigest()[:16]


# --- sources ---------------------------------------------------------------------------------

DECORATION_FOLDERS = {0: ("Britannia", "Felucca"), 1: ("Britannia", "Trammel"), 2: ("Ilshenar",),
                      3: ("Malas",), 4: ("Tokuno",)}


def decoration_items(folder: Path | None, facet: int) -> list[tuple[int, int, int, int]]:
    """The shard's decoration for a facet (ModernUO Data/Decoration/<map>/*.cfg): what the
    server adds to the statics at start-up (doors, furniture, containers, signs), as
    (item, x, y, z). A block is a header line `Type 0xITEM [(props)]` then `x y z` lines."""
    out = []
    if not folder:
        return out
    for sub in DECORATION_FOLDERS.get(facet, ()):
        for cfg in sorted((folder / sub).glob("*.cfg")):
            item = None
            for line in cfg.read_text(encoding="utf-8", errors="replace").splitlines():
                line = line.strip()
                if not line or line.startswith("#"):
                    item = None if not line else item
                    continue
                parts = line.split()
                if not (parts[0].lstrip("-").isdigit()):
                    item = int(parts[1], 16) if len(parts) > 1 and parts[1].lower().startswith("0x") else None
                    continue
                if item is not None and len(parts) >= 3:
                    try:
                        out.append((item, int(parts[0]), int(parts[1]), int(parts[2])))
                    except ValueError:
                        pass
    return out


def facet_buildings(data_dir: Path, facet: int, tiles: Tiles, x_limit: int = DUNGEON_X, min_walls: int = 8,
                    max_span: int = 64, extra: list | None = None, log=print):
    """Yield (ref, bounds, statics, land) for each wall cluster on a facet. `extra`
    items (the shard's decoration) count as statics."""
    from guo.uomap import STATIC_SIZE, open_facet
    by_block = collections.defaultdict(list)
    for e in extra or ():
        if 0 <= e[1] < x_limit:
            by_block[(e[1] >> 3, e[2] >> 3)].append(e)
    with open_facet(data_dir, facet) as f:
        wall_cells: set = set()
        wall_ids: dict[int, bool] = {}
        bw = min(f.width_blocks, x_limit >> 3)
        for bx in range(bw):
            for by in range(f.height_blocks):
                off, length = f.statics_span(f.number(bx, by))
                if not length:
                    continue
                for sid, x, y, _z, _hue in struct.iter_unpack("<HBBbH", f.sta[off:off + length - length % STATIC_SIZE]):
                    w = wall_ids.get(sid)
                    if w is None:
                        t = tiles.get(sid)
                        w = t["role"] in STRUCT_ROLES + ("door",) and not t["flags"] & classify.FOLIAGE
                        wall_ids[sid] = w
                    if w:
                        wall_cells.add(((bx << 3) + x, (by << 3) + y))
        for es in by_block.values():
            for sid, x, y, _z in es:
                t = tiles.get(sid)
                if t["role"] in STRUCT_ROLES + ("door",) and not t["flags"] & classify.FOLIAGE:
                    wall_cells.add((x, y))
        log(f"[decorate] facet {facet}: {len(wall_cells)} wall cells, {len(extra or ())} decoration items")
        seen = set()
        for key in sorted(wall_cells):
            if key in seen:
                continue
            stack, cells = [key], []
            seen.add(key)
            while stack:
                c = stack.pop()
                cells.append(c)
                for dx in (-1, 0, 1):
                    for dy in (-1, 0, 1):
                        n = (c[0] + dx, c[1] + dy)
                        if n in wall_cells and n not in seen:
                            seen.add(n)
                            stack.append(n)
            xs, ys = [c[0] for c in cells], [c[1] for c in cells]
            x0, y0, x1, y1 = min(xs), min(ys), max(xs), max(ys)
            if len(cells) < min_walls or x1 - x0 >= max_span or y1 - y0 >= max_span or x1 - x0 < 2 or y1 - y0 < 2:
                continue
            statics, land = [], {}
            for bx in range(x0 >> 3, (x1 >> 3) + 1):
                for by in range(y0 >> 3, (y1 >> 3) + 1):
                    b = f.read(bx, by)
                    for i in range(64):
                        lx, ly = (bx << 3) + i % 8, (by << 3) + i // 8
                        if x0 <= lx <= x1 and y0 <= ly <= y1:
                            land[(lx - x0, ly - y0)] = b.land_z[i]
                    for sid, x, y, z, _h in b.statics:
                        wx, wy = (bx << 3) + x, (by << 3) + y
                        if x0 <= wx <= x1 and y0 <= wy <= y1:
                            statics.append((sid, wx - x0, wy - y0, z))
                    for sid, wx, wy, z in by_block.get((bx, by), ()):
                        if x0 <= wx <= x1 and y0 <= wy <= y1:
                            statics.append((sid, wx - x0, wy - y0, z))
            yield f"{x0},{y0}", (x0, y0, x1, y1), statics, land


def multi_buildings(data_dir: Path, log=print):
    from multifile import Multis
    m = Multis(data_dir)
    seen = set()
    for i in m.ids():
        comps = [c for c in (m.get(i) or []) if c.visible]
        if len(comps) < 12:
            continue
        key = hashlib.sha1(repr(sorted((c.item, c.x, c.y, c.z) for c in comps)).encode()).hexdigest()
        if key in seen:
            continue
        seen.add(key)
        xs, ys = [c.x for c in comps], [c.y for c in comps]
        yield f"{i:#06x}", (min(xs), min(ys), max(xs), max(ys)), [(c.item, c.x, c.y, c.z) for c in comps], None


# --- the whole run ---------------------------------------------------------------------------

def mine(data_dir: Path, out: Path, facets=(0,), multis: bool = True, decoration: Path | None = None,
         log=print) -> dict:
    from guo.uoread import TileData
    tiles = Tiles(TileData(data_dir))
    sources = []
    for fc in facets:
        extra = decoration_items(decoration, fc)
        sources.append(("statics+decoration" if extra else "statics", fc,
                        facet_buildings(data_dir, fc, tiles, extra=extra, log=log)))
    if multis:
        sources.append(("multi", None, multi_buildings(data_dir, log=log)))
    meta = {"facets": json.dumps(list(facets)), "multis": str(int(multis)),
            "decoration": str(int(decoration is not None))}
    return write(out, tiles, sources, meta, log)


def write(out: Path, tiles, sources: list, meta: dict, log=print) -> dict:
    """Analyse every building the sources yield and write the database. A source is
    (name, facet or None, iterable of (ref, bounds, statics, land))."""
    t0 = time.time()
    con = db.create(out)
    templates: dict[str, dict] = {}
    item_stats: dict[int, dict] = {}
    co_rooms = collections.Counter()
    co_adj = collections.Counter()
    type_rooms = collections.defaultdict(list)
    ids = {"b": 0, "r": 0, "f": 0, "g": 0}
    for src, fc, gen in sources:
        nb = 0
        for ref, bnds, statics, land in gen:
            found = analyse(statics, tiles, land)
            if not found.rooms:
                continue
            ids["b"] += 1
            bid = ids["b"]
            nb += 1
            nfurn = 0
            for room in found.rooms:
                ids["r"] += 1
                rid = ids["r"]
                nfurn += len(room["furn"])
                x0, y0, _x1, _y1 = room["bbox"]
                con.execute("INSERT INTO room VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                            (rid, bid, room["storey"], room["z"], x0, y0, room["size"][0], room["size"][1],
                             room["area"], room["fill"], room["shape"], int(room["enclosed"]), len(room["doors"]),
                             len(room["windows"]), room["open_edges"], room["type"], room["score"],
                             len(room["furn"]), json.dumps(room["kinds"], sort_keys=True),
                             json.dumps(room["cells"])))
                con.executemany("INSERT INTO opening VALUES (?,?,?,?,?)",
                                [(rid, "door", x, y, d) for x, y, d in room["doors"]]
                                + [(rid, "window", x, y, d) for x, y, d in room["windows"]])
                group_of = {}
                for g in room["groups"]:
                    key = template_key(g["items"], g["on_wall"])
                    tp = templates.get(key)
                    if tp is None:
                        tp = templates[key] = {"id": len(templates) + 1, "items": g["items"], "w": g["w"], "h": g["h"],
                                               "cells": g["cells"], "on_wall": g["on_wall"],
                                               "against": collections.Counter(), "types": collections.Counter(),
                                               "kinds": collections.Counter(room["furn"][m]["kind"] for m in g["members"]),
                                               "uses": 0}
                    tp["uses"] += 1
                    tp["against"][g["against"]] += 1
                    tp["types"][room["type"]] += 1
                    ids["g"] += 1
                    ax, ay = g["at"]
                    con.execute("INSERT INTO grp VALUES (?,?,?,?,?)", (ids["g"], rid, tp["id"], ax - x0, ay - y0))
                    for m in g["members"]:
                        group_of[m] = ids["g"]
                rows = []
                for k, f in enumerate(room["furn"]):
                    ids["f"] += 1
                    rows.append((ids["f"], rid, f["item"], f["name"], f["kind"], f["flags"], f["x"], f["y"],
                                 f["z_above"], f["against"], f["place"], f["near_door"], f["near_window"],
                                 f["facing"], f["on_item"], group_of.get(k)))
                    st = item_stats.setdefault(f["item"], {
                        "name": f["name"], "kind": f["kind"], "flags": f["flags"], "height": f["height"], "placed": 0,
                        "N": 0, "E": 0, "S": 0, "W": 0, "corner": 0, "centre": 0, "on_wall": 0, "stacked": 0,
                        "near_door": 0, "near_window": 0, "facing": collections.Counter(), "z": collections.Counter()})
                    st["placed"] += 1
                    for d in f["against"]:
                        st[d] += 1
                    st["corner"] += f["place"] == "corner"
                    st["centre"] += f["place"] == "centre"
                    st["on_wall"] += f["place"] == "on-wall"
                    st["stacked"] += f["on_item"] is not None
                    st["near_door"] += f["near_door"]
                    st["near_window"] += f["near_window"]
                    st["facing"][f["facing"]] += 1
                    st["z"][f["z_above"]] += 1
                con.executemany("INSERT INTO furnishing VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", rows)
                kinds = sorted({f["kind"] for f in room["furn"]})
                for a in kinds:
                    for b in kinds:
                        if a < b:
                            co_rooms[(a, b)] += 1
                cells = collections.defaultdict(set)
                for f in room["furn"]:
                    cells[f["cell"]].add(f["kind"])
                adj = set()
                for c, ks in cells.items():
                    for dx in (-1, 0, 1):
                        for dy in (-1, 0, 1):
                            if (dx or dy) and (c[0] + dx, c[1] + dy) in cells:
                                for a in ks:
                                    for b in cells[(c[0] + dx, c[1] + dy)]:
                                        if a != b:
                                            adj.add((min(a, b), max(a, b)))
                for p in adj:
                    co_adj[p] += 1
                type_rooms[room["type"]].append(room)
            x0, y0, x1, y1 = bnds
            con.execute("INSERT INTO building VALUES (?,?,?,?,?,?,?,?,?,?,?)",
                        (bid, src, fc, ref, x0, y0, x1, y1, json.dumps(found.storeys), len(found.rooms), nfurn))
        log(f"[decorate] {src}{'' if fc is None else ' facet ' + str(fc)}: {nb} buildings with rooms "
            f"({time.time() - t0:.0f}s)")
    for key, tp in templates.items():
        con.execute("INSERT INTO template VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
                    (tp["id"], key, json.dumps(tp["items"]), tp["w"], tp["h"], tp["cells"], len(tp["items"]),
                     tp["against"].most_common(1)[0][0], tp["on_wall"], json.dumps(dict(tp["kinds"]), sort_keys=True),
                     tp["uses"], json.dumps(dict(tp["types"].most_common()), sort_keys=False)))
    for item, st in sorted(item_stats.items()):
        con.execute("INSERT INTO item VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                    (item, st["name"], st["kind"], st["flags"], st["height"], st["placed"], st["N"], st["E"],
                     st["S"], st["W"], st["corner"], st["centre"], st["on_wall"], st["stacked"], st["near_door"],
                     st["near_window"], st["facing"].most_common(1)[0][0],
                     json.dumps({str(k): v for k, v in st["z"].most_common(6)})))
    for (a, b) in sorted(set(co_rooms) | set(co_adj)):
        con.execute("INSERT INTO cooccur VALUES (?,?,?,?)", (a, b, co_rooms[(a, b)], co_adj[(a, b)]))
    for t, rs in sorted(type_rooms.items()):
        areas = [r["area"] for r in rs]
        dens = [len({f["cell"] for f in r["furn"]}) / r["area"] for r in rs]
        kinds = collections.Counter(k for r in rs for k in r["kinds"])
        con.execute("INSERT INTO room_type VALUES (?,?,?,?,?,?,?,?,?)",
                    (t, len(rs), sum(r["storey"] == 0 for r in rs), sum(r["storey"] > 0 for r in rs),
                     round(sum(areas) / len(areas), 1), min(areas), max(areas), round(statistics.median(dens), 3),
                     json.dumps(dict(kinds.most_common(16)))))
    for k, v in sorted(dict(meta, seconds=str(round(time.time() - t0))).items()):
        con.execute("INSERT INTO meta VALUES (?, ?)", (k, v))
    counts = db.counts(con)
    db.finish(con, out)
    return counts
