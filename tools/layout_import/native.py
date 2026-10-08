"""Semantic grids into GUO's canonical native component and sidecar contract.

Source walls occupy cells. Target directional sprites are picked by neighbour
signature through the existing mined Catalogue; floor/blocker ownership remains
on the source grid. No second native encoder or ID allocator is introduced.
"""
from __future__ import annotations

import collections
import copy
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "multi"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import generate as G
import fort
import render
import validate
import walkcheck
import pieces
from multifile import Component
from guo.uoread import Art, TileData
from decorate import rooms as R
from decorate import db as decor_db
from decorate import decorate as D
from decorate import classify
from layout_import.cdda import digest
from layout_import.semantic import ResolutionError


DEFAULT_THEME = {"format": 1, "name": "uo-classic", "version": 1,
    "cell_scale": 1, "wall_conversion": "source-wall-cells; native-neighbour-signatures",
    "floor_z": 7, "storey_height": 20, "wall_height": 20,
    "stair_adaptation": "carve-internal-flight",
    "materials": {"wall": "stone", "floor": "wooden", "roof": "slate", "foundation": "stone", "stairs": "stone"},
    "furnishing_roles": {"bed": "bed", "bath": "bath", "oven": "oven", "forge": "forge",
          "bookcase": "bookcase", "counter": "counter", "display": "display", "chair": "chair", "table": "table",
          "chest": "chest", "container": "container", "plant": "plant", "light": "light", "farm": "farm",
          "tools": "tools", "misc": "art"}}


def groups(level):
    by_role = collections.defaultdict(set)
    cells = {(c["x"], c["y"]): c for c in level["cells"]}
    for p, cell in cells.items():
        if cell["furnishing_role"]:
            by_role[(cell["furnishing_role"], cell["furniture"])].add(p)
    out = []
    for (role, source_id), positions in sorted(by_role.items()):
        for comp in R.components(positions):
            out.append({"role": role, "source_id": source_id, "cells": sorted(comp)})
    return out


def furniture_library(path, data):
    art = Art(data)
    con = decor_db.open_ro(path)
    try:
        library = D.Library.from_db(con, has_art=lambda item: bool(art.get_static(item)))
        # Complete functional subgroups from learned composite groups: a basin
        # next to a chest is still a reusable basin. All same-role pieces stay
        # together, and the existing multipart completeness filter runs again.
        isolated = []
        raw_templates = [D.Template(row[0], json.loads(row[1]), row[2], row[3],
                          frozenset((p[1], p[2]) for p in json.loads(row[1])), row[4], bool(row[5]),
                          json.loads(row[6]), row[7], json.loads(row[8])) for row in con.execute(
                          "SELECT id,items,w,h,against,on_wall,kinds,uses,room_types FROM template")]
        for template in raw_templates:
            by_role = collections.defaultdict(list)
            for item, dx, dy, dz in template.items:
                if art.get_static(item) and -2 <= dz <= 30:
                    by_role[classify.kind_of(library.names.get(item, ""))].append([item, dx, dy, dz])
            for role, pieces in by_role.items():
                x0, y0 = min(p[1] for p in pieces), min(p[2] for p in pieces)
                items = [[p[0], p[1] - x0, p[2] - y0, p[3]] for p in pieces]
                isolated.append(D.Template(template.id, items, max(p[1] for p in items) + 1,
                    max(p[2] for p in items) + 1, frozenset((p[1], p[2]) for p in items),
                    template.against, template.on_wall, {role: len(items)}, template.uses, template.types))
        library.templates += D.complete(isolated, library.names)
        # These UO furnishing names denote independent one-cell pieces, even
        # when a miner found a row of several adjacent copies. Beds and complex
        # cooking assemblies deliberately stay complete multipart templates.
        single_names = {"bookcase", "armoire", "wooden chest", "metal chest", "chest of drawers",
                        "shelves", "wooden shelf", "barrel", "crate", "small crate", "wash basin", "water tub"}
        added = set()
        backing = collections.defaultdict(set)
        for row in con.execute("SELECT DISTINCT item, against FROM furnishing WHERE z_above=0 AND place!='on-wall'"):
            backing[row[0]].add(row[1])
        for tp in raw_templates:
            for item, dx, dy, dz in tp.items:
                name = library.names.get(item, "").lower()
                if name in single_names and dz == 0 and art.get_static(item):
                    for against in sorted(backing.get(item, {tp.against})):
                        if (item, against) in added:
                            continue
                        added.add((item, against))
                        library.templates.append(D.Template(-item, [[item, 0, 0, 0]], 1, 1, frozenset({(0, 0)}),
                            against if name in {"bookcase", "armoire", "shelves", "wooden shelf"} else "",
                            False, {classify.kind_of(name): 1}, tp.uses, tp.types))
        distinct = {}
        for tp in library.templates:
            key = (tuple(tuple(p) for p in tp.items),tp.against,tp.on_wall,tuple(sorted(tp.kinds)))
            if key not in distinct or (tp.uses,-tp.id) > (distinct[key].uses,-distinct[key].id):
                distinct[key] = tp
        library.templates = list(distinct.values())
        return library
    finally:
        con.close()


def furnish(level, storey, lib, theme, reserved):
    """Map every source group to a complete native mined template near its source anchor.

    Relocations are recorded. Door/stair clearance and all remaining room-floor
    connectivity are checked after every placement, preserving access to every
    functional group. A missing mapping blocks the build rather than disappearing.
    """
    walls = set(map(tuple, storey["walls"]))
    floor = set(map(tuple, storey["floor"])) - walls
    door_cells = set(map(tuple, storey["doors"]))
    clear = set(reserved) | door_cells
    for x, y in door_cells:
        clear |= {(x + dx, y + dy) for _, dx, dy in R.DIRS}
    owners, room_cells = {}, {}
    source_cells = {(c["x"],c["y"]):c for c in level["cells"]}
    for room in level["rooms"]:
        room_cells[room["id"]] = set(map(tuple, room["cells"])) & floor
        owners.update({p: room["id"] for p in room_cells[room["id"]]})
    occupied, items, mappings, diagnostics = set(), [], [], []
    source_groups = groups(level)
    # Structural-function anchors first, then the smaller decor groups.
    source_groups.sort(key=lambda g: (g["role"] not in ("bed", "bath", "oven", "forge"), -len(g["cells"]), g["cells"]))
    for group in source_groups:
        src = set(group["cells"])
        home = collections.Counter(owners[p] for p in src if p in owners)
        if not home and any(source_cells[p]["indoors"] for p in src) and owners:
            nearest = min(owners,key=lambda p:(min(abs(p[0]-q[0])+abs(p[1]-q[1]) for q in src),p))
            home[owners[nearest]] = 1
        if not home:
            # Exterior furnishings are preserved as explicit groups but assembled
            # by the district/world adapter rather than hidden in an indoor multi.
            mappings.append({**group, "status": "exterior-pending-world"})
            continue
        rid = home.most_common(1)[0][0]
        allowed = room_cells[rid]
        # Fully furnished small closets are reached from their doorway. The
        # source already offers no standing cell among its shelves; demanding
        # an aisle there would erase the actual storage function.
        alcove = len(allowed) <= 4 and group["role"] in {"display", "bookcase", "chest", "container"} and not allowed & set(reserved)
        group_clear = (set(reserved) | door_cells) if alcove else clear
        target_role = theme["furnishing_roles"].get(group["role"])
        templates = [t for t in lib.templates if target_role in t.kinds and not t.on_wall
                     and set(t.kinds) <= {target_role, "misc"}]
        sx, sy = min(x for x, y in src), min(y for x, y in src)
        # The original source back-wall relation controls target facings.
        source_back = set("".join(R.wall_sides(p, walls) for p in src))
        candidates = []
        for tp in templates:
            for x, y in sorted(allowed):
                positions = {(x + dx, y + dy) for dx, dy in tp.cells}
                if not positions <= allowed or positions & (occupied | group_clear):
                    continue
                if tp.against and not any(set(tp.against) <= set(R.wall_sides(p, walls)) for p in positions):
                    continue
                free = allowed - occupied - positions
                if free and len(R.reachable(min(free), free)) != len(free):
                    continue
                # A source functional group needs at least one approachable edge.
                edges = {(px + dx, py + dy) for px, py in positions for _, dx, dy in R.DIRS} - positions
                interaction = edges & ((floor - walls - occupied - positions) if alcove else free)
                if not interaction:
                    continue
                distance = abs(x - sx) + abs(y - sy)
                score = (distance + (0 if not tp.against or set(tp.against) <= source_back else 4),
                         abs(len(positions) - len(src)), -tp.uses, tp.id, x, y)
                candidates.append((score, tp, x, y, positions))
        if not candidates:
            diagnostics.append(f"level {level['z']} {group['source_id']} at {group['cells']}: no complete {target_role} template fits with facing and clearance")
            mappings.append({**group, "status": "blocked", "target_role": target_role})
            continue
        _, tp, x, y, positions = min(candidates, key=lambda v: v[0])
        occupied |= positions
        for item, dx, dy, dz in tp.items:
            items.append(Component(item, x + dx, y + dy, storey["z"] + dz))
        mappings.append({**group, "status": "mapped", "target_role": target_role, "template": tp.id,
                         "target_items": [[item,x+dx,y+dy,storey["z"]+dz] for item,dx,dy,dz in tp.items],
                         "target_cells": [list(p) for p in sorted(positions)], "against": tp.against,
                         "relocation": [x - sx, y - sy], "room": rid,
                         "source_back": "".join(sorted(source_back)),
                         "access_mode": "doorway-storage-alcove" if alcove else "room-aisle",
                         "facing_adapted": bool(tp.against and not set(tp.against) <= source_back)})
    return items, mappings, diagnostics


def reserve_stairs(levels, theme, built, cat):
    reserved = collections.defaultdict(set)
    adaptations, flights = [], []
    by_z = {level["z"]: level for level in levels}
    for level in levels:
        upper = by_z.get(level["z"] + 1)
        for source in [c for c in level["cells"] if c["role"] == "stair-up"]:
            if upper is None:
                raise ResolutionError(f"level {level['z']} stair-up has no supplied upper level")
            lower_inside = {tuple(p) for room in level["rooms"] for p in room["cells"]}
            upper_inside = {tuple(p) for room in upper["rooms"] for p in room["cells"]}
            upper_inside |= {(c["x"],c["y"]) for c in upper["cells"] if c.get("stairwell")}
            source_at = (source["x"], source["y"])
            upper_connections = {(c["x"], c["y"]) for c in upper["cells"] if c["role"] == "stair-down" or "GOES_DOWN" in c["flags"]}
            if not upper_connections:
                raise ResolutionError(f"level {level['z']} source stairs have no upper landing connector")
            proposals = []
            def circulation_ok(source_level, interior, line, cuts=set()):
                doors = {(c["x"],c["y"]) for c in source_level["cells"] if c["role"] == "door"}
                original = interior | doors | cuts
                before = len(R.components(original))
                after = original - set(line[:5]) - reserved[source_level["z"]]
                return len(R.components(after)) <= before
            for at in sorted(lower_inside):
                for direction, (dx, dy) in G.SIDE_STEP.items():
                    line = [(at[0] + i * dx, at[1] + i * dy) for i in range(6)]
                    foot = (at[0] - dx, at[1] - dy)
                    if not set(line) <= lower_inside & upper_inside or foot not in lower_inside:
                        continue
                    if set(line) & (reserved[level["z"]] | reserved[upper["z"]]):
                        continue
                    if not circulation_ok(level,lower_inside,line) or not circulation_ok(upper,upper_inside,line):
                        continue
                    if not R.shortest_path(source_at, foot, lower_inside):
                        continue
                    if not any(R.shortest_path(line[-1], p, upper_inside) for p in upper_connections):
                        continue
                    distance = abs(at[0] - source_at[0]) + abs(at[1] - source_at[1])
                    if distance <= 8:
                        proposals.append((distance, at, direction, line, foot))
            if not proposals:
                if theme.get("stair_adaptation") != "carve-internal-flight":
                    raise ResolutionError(f"source stair at {source_at} level {level['z']}: no real 6-cell UO flight/landing fits; explicit footprint adaptation required")
                def partitions(source_level, interior):
                    out = set()
                    for cell in source_level["cells"]:
                        x,y = cell["x"],cell["y"]
                        if cell["role"] == "wall" and (((x-1,y) in interior and (x+1,y) in interior) or
                                                      ((x,y-1) in interior and (x,y+1) in interior)):
                            out.add((x,y))
                    return out
                low_cuts, up_cuts = partitions(level,lower_inside), partitions(upper,upper_inside)
                convertible = (lower_inside|low_cuts) & (upper_inside|up_cuts)
                alternatives = []
                for at in sorted(convertible):
                    for direction,(dx,dy) in G.SIDE_STEP.items():
                        line = [(at[0]+i*dx,at[1]+i*dy) for i in range(6)]
                        foot = (at[0]-dx,at[1]-dy)
                        removed = set(line)&(low_cuts|up_cuts)
                        distance = abs(at[0]-source_at[0])+abs(at[1]-source_at[1])
                        if not set(line) <= convertible or foot not in lower_inside or line[-1] not in upper_inside:
                            continue
                        if distance>10 or len(removed)>3 or set(line)&(reserved[level["z"]]|reserved[upper["z"]]):
                            continue
                        if not circulation_ok(level,lower_inside,line,low_cuts) or not circulation_ok(upper,upper_inside,line,up_cuts):
                            continue
                        if not R.shortest_path(source_at,foot,lower_inside|low_cuts):
                            continue
                        if not any(R.shortest_path(line[-1],p,upper_inside|up_cuts) for p in upper_connections):
                            continue
                        alternatives.append(((len(removed),distance,at,direction),at,direction,line,foot))
                if not alternatives:
                    raise ResolutionError(f"source stair at {source_at} level {level['z']}: no bounded internal-flight adaptation fits")
                _,at,direction,line,foot = min(alternatives)
                changed = []
                for source_level in (level,upper):
                    for cell in source_level["cells"]:
                        if (cell["x"],cell["y"]) in line and cell["role"] == "wall":
                            changed.append({"source":cell["lineage"],"from":"wall","to":"stair-clearance-floor"})
                            cell["role"],cell["walkable"],cell["indoors"] = "floor",True,True
                    from layout_import.semantic import infer_rooms
                    infer_rooms(source_level)
                adaptations.append({"kind":"internal-stair-clearance", "cells":changed,
                                    "reason":"instant source transition requires a real six-cell UO flight"})
                proposals.append((0,at,direction,line,foot))
                lower_inside = {tuple(p) for room in level["rooms"] for p in room["cells"]}
                upper_inside = {tuple(p) for room in upper["rooms"] for p in room["cells"]}
            _, at, direction, line, foot = min(proposals)
            arrival_cell=next(c for c in upper["cells"] if (c["x"],c["y"])==line[-1])
            if arrival_cell.get("stairwell") and arrival_cell["role"]=="void":
                arrival_cell.update(role="floor",walkable=True)
                adaptations.append({"kind":"source-stairwell-landing-fill","source":arrival_cell["lineage"],
                                    "target":list(line[-1]),"level":upper["z"]})
            floor = lower_inside
            z = theme["floor_z"] + level["z"] * theme["storey_height"]
            holes, arrivals = G.staircase(built, cat, {"at": at, "rise": direction}, theme["materials"]["stairs"], z, floor, set())
            reserved[level["z"]].update(holes | {foot})
            reserved[upper["z"]].update(holes | arrivals)
            flight = {"foot": list(foot), "z": z, "cells": [list(p) for p in sorted(holes)],
                      "arrive": list(line[-1]), "from_level": level["z"], "to_level": upper["z"],
                      "rise": theme["storey_height"]}
            flights.append(flight)
            adaptations.append({"kind": "instant-stair-to-walkable-flight", "source": source["lineage"],
                                "target": flight, "furniture_relocated": True})
    return reserved, flights, adaptations


def compile_layout(layout, cat, lib, theme=None, as_scene=False):
    theme = copy.deepcopy(theme or DEFAULT_THEME)
    if theme["cell_scale"] != 1 or theme["storey_height"] != 20:
        raise ResolutionError("this adapter currently supports scale 1 and 20-z storeys only")
    cat.fresh()
    built = G.Built()
    all_levels = sorted(copy.deepcopy(layout["levels"]), key=lambda level: level["z"])
    living = [level for level in all_levels if level.get("rooms")]
    if not living:
        raise ResolutionError("no enclosed functional rooms in this layout")
    if living[0]["z"] < 0 and not theme.get("excavate_basements"):
        raise ResolutionError("basements require a terrain excavation/world contract; native multi cannot replace land")
    if not any(level["z"] == 0 for level in living):
        raise ResolutionError("building has no ground-level rooms")
    reserved, flights, adaptations = reserve_stairs(living, theme, built, cat)
    for level in living:
        holes = {tuple(p) for f in flights if f["to_level"] == level["z"] for p in f["cells"]}
        lower_flights = {tuple(p) for f in flights if f["from_level"] == level["z"] for p in f["cells"]}
        for cell in level["cells"]:
            if (cell["x"],cell["y"]) in holes:
                cell["role"],cell["walkable"] = "void",False
            elif (cell["x"],cell["y"]) in lower_flights:
                cell["role"],cell["walkable"] = "stair-flight",False
        from layout_import.semantic import infer_rooms
        infer_rooms(level)
    roofs = {level["z"]: {(c["x"], c["y"]) for c in level["cells"] if c["role"] in ("roof", "roof-edge")} for level in all_levels}
    walls_by_z, floor_by_z, stores = {}, {}, []
    mappings, problems, lineage, stops = [], [], [], []
    floor_ids = cat.floor(theme["materials"]["floor"])
    roof_ids = cat.floor(theme["materials"]["roof"])
    for n, level in enumerate(living):
        z = theme["floor_z"] + level["z"] * theme["storey_height"]
        cells = {(c["x"], c["y"]): c for c in level["cells"]}
        inside = {tuple(p) for room in level["rooms"] for p in room["cells"]}
        # Only fabric that actually bounds the functional interior belongs to this building.
        adjacent = {(x + dx, y + dy) for x, y in inside for _, dx, dy in R.DIRS}
        fabric = {p for p, c in cells.items() if c["role"] in ("wall", "window", "door")}
        # Include corners and continuous connected shell/partition runs.
        shell = set()
        # One shell cell, including diagonal corner ownership, bounds rooms.
        # Solid earth outside a basement is world terrain, not hundreds of
        # connected wall/floor components extending to the OMT boundary.
        stair_fabric={tuple(p) for f in flights if level["z"] in (f["from_level"],f["to_level"])
                      for p in f["cells"]+[f["foot"],f["arrive"]]}
        near = {(x+dx,y+dy) for x,y in inside | stair_fabric for dx in (-1,0,1) for dy in (-1,0,1)}
        shell = fabric & near
        doors = {p for p in shell if cells[p]["role"] == "door"}
        windows = {p for p in shell if cells[p]["role"] == "window"}
        solid = shell - doors
        rails = set()
        if level["z"] > 0:
            for x,y in inside:
                for _,dx,dy in R.DIRS:
                    p = (x+dx,y+dy)
                    if p in cells and cells[p]["role"] == "void" and p not in reserved[level["z"]]:
                        rails.add(p)
            if rails:
                adaptations.append({"kind":"upper-open-edge-railing","level":level["z"],
                                    "cells":[list(p) for p in sorted(rails)],"material":"wooden fence"})
                shell |= rails
                solid |= rails
        holes = {tuple(p) for f in flights if f["to_level"] == level["z"] for p in f["cells"]}
        floors = (inside | shell) - holes
        arrivals = [f["arrive"] for f in flights if f["to_level"] == level["z"]]
        floor_by_z[level["z"]] = floors
        walls_by_z[level["z"]] = solid
        for x, y in sorted(solid):
            built.add(cat.wall("wooden fence" if (x,y) in rails else theme["materials"]["wall"],
                              6 if (x,y) in rails else theme["wall_height"], G.signature(shell, x, y), (x, y) in windows), x, y, z)
        for x, y in sorted(floors):
            built.add(G.scatter(floor_ids, x, y), x, y, z)
            lineage.append({"target": [x, y, z], "source": cells[(x, y)]["lineage"], "kind": "floor"})
        for x, y in sorted(doors):
            along_x = (x - 1, y) in shell or (x + 1, y) in shell
            facing = "WestCW" if along_x else "SouthCW"
            built.add(cat.door(theme["materials"]["wall"]), x, y, z, False)
            built.doors.append({"x": x, "y": y, "z": z, "storey": n, "facing": facing, "type": "DarkWoodDoor"})
            if cells[(x, y)]["access"] == "locked":
                adaptations.append({"kind": "source-access-state", "source": cells[(x, y)]["lineage"],
                                    "source_state": "locked", "target_state": "unlocked pilot door"})
        storey = {"z": z, "walls": [list(p) for p in sorted(solid)], "doors": [list(p) for p in sorted(doors)],
                  "windows": [list(p) for p in sorted(windows)], "floor": [list(p) for p in sorted(floors)],
                  "open": [], "arrivals": arrivals}
        stores.append(storey)
        additions, mapped, diagnostics = furnish(level, storey, lib, theme, reserved[level["z"]])
        built.comps.extend(additions)
        mappings.extend({"level": level["z"], **m} for m in mapped)
        problems.extend(diagnostics)
        for room in level["rooms"]:
            free = set(map(tuple, room["cells"])) - reserved[level["z"]]
            taken = {tuple(p) for m in mapped if m["status"] == "mapped" for p in m["target_cells"]}
            free -= taken
            if free:
                target = min(free, key=lambda p: (abs(p[0] - (room["bounds"][0] + room["bounds"][2]) / 2)
                                              + abs(p[1] - (room["bounds"][1] + room["bounds"][3]) / 2), p))
                stops.append({"name": f"level{level['z']}_{room['id'].split(':')[-1]}_{room['use']}", "x": target[0], "y": target[1], "z": z})
    # Complete roof masks are supplied by the engine or source terrain roof links.
    roof_cells = {}
    for level in living:
        top_z = level["z"] + 1
        upper_living = next((l for l in living if l["z"] == top_z), None)
        if upper_living:
            continue
        roof = roofs.get(top_z, set()).copy()
        upper_source = next((l for l in all_levels if l["z"] == top_z), None)
        if upper_source:
            # Roof utilities (chimneys/vents/skylights) occupy roof cells too.
            # Keep a native supporting roof tile beneath their themed assembly.
            roof |= {(c["x"], c["y"]) for c in upper_source["cells"] if c["role"] == "obstacle"}
        if not roof:
            problems.append(f"level {level['z']}: no supplied source roof mask")
            continue
        railing_cells = {tuple(p) for a in adaptations if a["kind"] == "upper-open-edge-railing" and a["level"] == level["z"] for p in a["cells"]}
        extra_roof = railing_cells - roof
        if extra_roof:
            adaptations.append({"kind":"roof-over-railing-extension","level":top_z,"cells":[list(p) for p in sorted(extra_roof)]})
            roof |= extra_roof
        for x, y in sorted(roof):
            z = theme["floor_z"] + top_z * theme["storey_height"]
            built.add(G.scatter(roof_ids, x, y), x, y, z)
            roof_cells[(x, y)] = z
    if not roof_cells:
        problems.append("no complete native roof")
    ground_index = next(n for n, level in enumerate(living) if level["z"] == 0)
    ground = stores[ground_index]
    gfloor = set(map(tuple, ground["floor"]))
    walls = set(map(tuple, ground["walls"]))
    doors = set(map(tuple, ground["doors"]))
    entries = []
    for x, y in sorted(doors):
        for side, (dx, dy) in G.SIDE_STEP.items():
            if (x + dx, y + dy) not in gfloor and (x - dx, y - dy) in gfloor - walls:
                entries.append((x, y, side))
                row = {(x + dx + i, y + dy) for i in (-1, 0, 1)} if side in "NS" else {(x + dx, y + dy + i) for i in (-1, 0, 1)}
                for sx, sy in sorted(row):
                    built.add(cat.step(theme["materials"]["stairs"], G.TOWARD[side], G.signature(row, sx, sy)), sx, sy, theme["floor_z"] - 5)
    ground_holes = {tuple(p) for f in flights if 0 in (f["to_level"],f["from_level"]) for p in f["cells"]}
    foundation_edge = G.edge(gfloor | ground_holes)
    for x, y in sorted(foundation_edge):
        for dz, item in cat.course(theme["materials"]["foundation"], 5, G.signature(foundation_edge, x, y)):
            built.add(item, x, y, theme["floor_z"] - 7 + dz)
    if not entries:
        problems.append("no exterior entrance connected to a source room")
    else:
        x, y, side = entries[0]
        dx, dy = G.SIDE_STEP[side]
        stops = [{"name": "outside", "x": x + dx * 2, "y": y + dy * 2, "z": 0},
                 {"name": "entrance_step", "x": x + dx, "y": y + dy, "z": 2},
                 {"name": "doorway", "x": x, "y": y, "z": theme["floor_z"]}] + stops
    stops = fort.walk_tour(stops, [{"foot": f["foot"], "arrive": f["arrive"], "z_from": f["z"],
                                 "z_to": f["z"] + f["rise"]} for f in flights])
    cx, cy = layout["width"] // 2, layout["height"] // 2
    built.add(G.CENTRE_MARKER, cx, cy, 0, False)
    comps = [Component(c.item, c.x - cx, c.y - cy, c.z, c.visible) for c in built.comps]
    stairs = [{**f, "to": next(i for i, l in enumerate(living) if l["z"] == f["to_level"])} for f in flights]
    side = {"format": 1, "name": layout["name"], "size": [layout["width"], layout["height"]], "centre": [cx, cy],
            "storeys": [st["z"] for st in stores], "roof_z": min(roof_cells.values()) if roof_cells else None,
            "doors": [{**d, "x": d["x"] - cx, "y": d["y"] - cy} for d in built.doors],
            "components": len(comps), "notes": ["Imported semantic source grid; see layout-build.json for source/target adaptations."],
            "stops": [{**s, "x": s["x"] - cx, "y": s["y"] - cy} for s in stops],
            "local": {"storeys": stores, "stairs": stairs, "yard": None}}
    if ground_index == 0 and not as_scene:
        problems.extend(validate.validate(comps, side, None))
    else:
        # Basement entry comes from above. Reuse the canonical storey, stair,
        # roof and part checks with explicit incoming endpoints on both levels.
        reached = []
        for n, st in enumerate(stores):
            incoming = set(map(tuple, st["arrivals"]))
            incoming |= {tuple(f["foot"]) for f in flights if f["from_level"] == living[n]["z"]}
            buf = []
            problems.extend(validate.check_storey(st, 0, None if n == ground_index else incoming, buf))
            reached.append(buf[0] if buf else set())
        problems.extend(validate.check_stairs(side, reached))
        problems.extend(validate.check_roof(comps, side))
        # Give check_doors a ground-first view so below-ground doors cannot
        # acquire an imaginary exterior standing cell.
        door_view = copy.deepcopy(side)
        door_view["local"]["storeys"] = [stores[ground_index]] + stores[:ground_index] + stores[ground_index+1:]
        problems.extend(validate.check_doors(door_view))
        for _,part in fort.cut(layout["name"],comps) if as_scene else [(layout["name"],comps)]:
            problems.extend(validate.check_parts(part, None))
    side["valid"], side["problems"] = not problems, problems
    record = {"format": 1, "name": layout["name"], "layout_hash": layout["layout_hash"], "theme": theme,
              "status": "geometry-valid" if not problems else "blocked", "problems": problems,
              "furnishing_mappings": mappings, "adaptations": adaptations, "lineage": lineage,
              "source_extras": [{"level": l["z"], "records": l.get("source_extras", [])} for l in all_levels],
              "world_requirements": {"excavation": [{"cells": [list(p) for p in sorted(
                                      set(map(tuple,st["floor"])) | {tuple(p) for f in flights
                                      if f["z"]==st["z"] for p in f["cells"]} |
                                      {tuple(f["foot"]) for f in flights if f["z"]==st["z"]})], "land_z": st["z"]-7}
                                     for st in stores if st["z"] < theme["floor_z"]]},
              "proof_status": "not-run", "coordinate_contract": "target cell = source cell; z = floor_z + source_level * storey_height"}
    return comps, side, record


def save_native_scene(comps, side, record, out, data):
    """Split large buildings on disjoint cell bounds before native encoding."""
    out.mkdir(parents=True,exist_ok=True)
    (out/'parts').mkdir(exist_ok=True)
    parts=[]
    problems=list(side['problems'])
    for name,group in fort.cut(record['name'],comps):
        xs,ys=[c.x for c in group],[c.y for c in group]
        bounds=[min(xs),min(ys),max(xs),max(ys)]
        cx,cy=(bounds[0]+bounds[2])//2,(bounds[1]+bounds[3])//2
        local=[Component(c.item,c.x-cx,c.y-cy,c.z,c.visible) for c in group]
        problems.extend(validate.check_parts(local,data))
        doors=[{**d,'x':d['x']-cx,'y':d['y']-cy} for d in side['doors']
               if bounds[0]<=d['x']<=bounds[2] and bounds[1]<=d['y']<=bounds[3]]
        parts.append({'name':name,'centre':[cx,cy],'bounds':bounds,'components':len(local),'doors':doors,'comps':local})
        (out/'parts'/f'{name}.json').write_text(json.dumps([c.as_list() for c in local]),encoding='utf-8')
    from layout_import.routing import short_tour,tile_info
    ground=0
    if record['world_requirements']['excavation']:
        ground={}
        for excavation in record['world_requirements']['excavation']:
            for x,y in excavation['cells']:
                p=(x-side['centre'][0],y-side['centre'][1])
                ground[p]=min(ground.get(p,0),excavation['land_z'])
    tour,issues=short_tour(parts,side['stops'],data,ground,max_steps=6)
    issues+=walkcheck.check_tour(parts,tour,tile_info(comps,data),ground)
    problems.extend(issues)
    scene={'format':1,'kind':'scene','name':record['name'],
        'bounds':[min(c.x for c in comps),min(c.y for c in comps),max(c.x for c in comps),max(c.y for c in comps)],
        'parts':[{k:v for k,v in p.items() if k!='comps'} for p in parts],'tour':tour,
        'valid':not problems,'problems':problems,'notes':['Disjoint native parts; source geometry retained across seams.']}
    record.update(status='native-valid' if not problems else 'blocked',problems=problems,
        native_scene_parts=len(parts),offline_movement={'passed':not issues,'problems':issues})
    (out/'scene.json').write_text(json.dumps(scene,indent=1),encoding='utf-8')
    (out/'layout-build.json').write_text(json.dumps(record,indent=1),encoding='utf-8')
    render.render(comps,data,out/'preview.png')
    if side['roof_z'] is not None:
        render.render(comps,data,out/'preview_noroof.png',max_z=side['roof_z']-1)
    return not problems


def save_native(comps, side, record, out, data):
    out.mkdir(parents=True, exist_ok=True)
    problems = side["problems"] + validate.check_parts(comps, data)
    # Use actual target tiledata flags and heights, including furnishings that
    # are absent from the mined structural catalogue.
    td = TileData(data)
    info = {f"{c.item:#06x}": {"flags":pieces.flag_names(td.static(c.item)["flags"]),
                              "height":td.static(c.item)["height"]} for c in comps if td.static(c.item)}
    ground = 0
    if record["world_requirements"]["excavation"]:
        cx,cy = side["centre"]
        ground = {}
        for excavation in record["world_requirements"]["excavation"]:
            for x,y in excavation["cells"]:
                ground[(x-cx,y-cy)] = min(ground.get((x-cx,y-cy),0),excavation["land_z"])
    from layout_import.routing import short_tour
    side["stops"], route_problems = short_tour([{"centre":[0,0],"comps":comps}],side["stops"],data,ground,max_steps=6)
    problems.extend(route_problems)
    movement = walkcheck.check_tour([{"centre":[0,0],"comps":comps}],side["stops"],info,ground)
    problems.extend(movement)
    record["offline_movement"] = {"passed":not movement,"problems":movement,"tiledata":"actual target flags/heights"}
    side["valid"], side["problems"] = not problems, problems
    record["problems"] = problems
    record["status"] = "native-valid" if not problems else "blocked"
    (out / "components.json").write_text(json.dumps([c.as_list() for c in comps]), encoding="utf-8")
    (out / "multi.json").write_text(json.dumps(side, indent=1), encoding="utf-8")
    (out / "layout-build.json").write_text(json.dumps(record, indent=1), encoding="utf-8")
    render.render(comps, data, out / "preview.png")
    if side["roof_z"] is not None:
        render.render(comps, data, out / "preview_noroof.png", max_z=side["roof_z"] - 1)
    for n, z in enumerate(side["storeys"]):
        render.plan(comps, data, z, z + 1).save(out / f"plan_{n}.png")
    return not problems
