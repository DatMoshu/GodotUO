"""Compose resolved engine parcels into a native UO world project and multi scene.

Land/statics use the existing world project writer; buildings use the existing
multi scene writer. The road geometry comes from resolved terrain metadata.
"""
from __future__ import annotations

import collections
import json
from pathlib import Path

from layout_import import catalogue, native, semantic
from layout_import.bake import Bake
from layout_import.cdda import digest
from guo.uomap import Block, open_facet
from guo.uoread import Art, TileData
from multi import storeys


def coherent_ids(values, limit=4):
    """Use one contiguous native material run, avoiding biome/edge mixtures."""
    runs=[]
    for item in sorted(set(values)):
        if runs and item==runs[-1][-1]+1:
            runs[-1].append(item)
        else:
            runs.append([item])
    if not runs:
        return []
    chosen=next((r for r in runs if len(r)>=2),runs[0])
    return chosen[:limit]


def land_library(data):
    td, art = TileData(data), Art(data)
    result = collections.defaultdict(list)
    for item in range(0x4000):
        info = td.land(item)
        if not info or not art.get_land(item):
            continue
        name, flags = info["name"].lower(), info["flags"]
        if flags & 0x80 and "water" in name:
            result["water"].append(item)
        elif not flags & (0x40 | 0x80):
            if name == "grass":
                result["grass"].append(item)
            if "cobblestone" in name:
                result["road"].append(item)
            if name == "dirt":
                result["dirt"].append(item)
    for role in ("grass", "road", "dirt", "water"):
        if not result[role]:
            raise semantic.ResolutionError(f"no native land art for {role}")
    return {name:coherent_ids(values) for name,values in result.items()}


def exterior_land(cell):
    text = cell["terrain_name"].lower()
    if cell["role"] == "water":
        return "water"
    if "ROAD" in cell["flags"] or any(word in text for word in ("pavement", "asphalt", "sidewalk", "concrete", "cement")):
        return "road"
    if any(word in text for word in ("dirt", "soil", "sand", "gravel", "grave")):
        return "dirt"
    return "grass"


def prop_library(data):
    td, art = TileData(data), Art(data)
    out = collections.defaultdict(list)
    for item in range(td.static_count):
        info = td.static(item)
        name = info["name"].lower()
        if name in {"gravestone", "headstone", "round bush", "juniper bush", "shrubs", "flowers", "rock", "boulder", "cart", "wagon"} and art.get_static(item):
            out["bush" if name in {"round bush", "juniper bush"} else name].append(item)
    return dict(out)


def outdoor(level, excluded, cat, lib, props, theme):
    """Translate each exterior source group; do not silently erase unmapped groups."""
    comps, mappings, problems, occupied, doors = [], [], [], set(), []
    cells = {(c["x"], c["y"]): c for c in level["cells"]}
    wall_cells = {p for p, c in cells.items() if p not in excluded and c["role"] in ("wall", "window")}
    door_cells = {p for p, c in cells.items() if p not in excluded and c["role"] == "door"}
    for x, y in sorted(wall_cells):
        fence = "fence" in cells[(x,y)]["terrain_name"].lower()
        comps.append(native.Component(cat.wall("wooden fence" if fence else theme["materials"]["wall"],
                      6 if fence else 20, native.G.signature(wall_cells | door_cells, x, y)), x, y, 0))
    for x,y in sorted(door_cells):
        along_x = (x-1,y) in wall_cells|door_cells or (x+1,y) in wall_cells|door_cells
        doors.append({"x":x,"y":y,"z":0,"facing":"WestCW" if along_x else "SouthCW",
                      "type":"LightWoodGate" if "gate" in cells[(x,y)]["terrain_name"].lower() else "DarkWoodDoor"})
    for p, c in sorted(cells.items()):
        if p in excluded or c["role"] != "obstacle":
            continue
        name = c["terrain_name"].lower()
        role = "bush" if any(w in name for w in ("tree", "shrub", "bush", "vegetation")) else "rock"
        ids = props.get(role, [])
        if not ids:
            problems.append(f"outdoor obstacle {c['terrain']} has no native {role}")
            continue
        comps.append(native.Component(native.G.scatter(ids, *p), *p, 0))
        occupied.add(p)
        mappings.append({"source": c["lineage"], "kind": "outdoor-obstacle", "target": role,
                         "adaptation": "native vegetation/rock silhouette"})
    for group in native.groups(level):
        source = set(group["cells"])
        if source <= excluded:
            continue
        if source & excluded:
            problems.append(f"exterior furnishing group crosses building ownership: {group['source_id']}")
            continue
        sx, sy = min(source)
        name = cells[(sx, sy)]["furniture_name"].lower()
        target_role = theme["furnishing_roles"].get(group["role"])
        grave_ids = props.get("gravestone", []) or props.get("headstone", [])
        if any(w in name for w in ("grave", "headstone", "cross")) and grave_ids:
            positions = source
            additions = [native.Component(native.G.scatter(grave_ids, x, y), x, y, 0) for x, y in sorted(positions)]
            template = "native-gravestone"
        else:
            templates = [t for t in lib.templates if target_role in t.kinds and not t.on_wall and not t.against
                         and set(t.kinds) <= {target_role, "misc"}]
            candidates = []
            for tp in templates:
                for x in range(max(0, sx - 3), min(max(p[0] for p in cells), sx + 3) + 1):
                    for y in range(max(0, sy - 3), min(max(p[1] for p in cells), sy + 3) + 1):
                        positions = {(x + dx, y + dy) for dx, dy in tp.cells}
                        if not positions <= cells.keys() or positions & (excluded | occupied | wall_cells):
                            continue
                        if any(not cells[p]["walkable"] for p in positions):
                            continue
                        candidates.append(((abs(x-sx)+abs(y-sy), abs(len(positions)-len(source)), -tp.uses, tp.id, x, y), tp, x, y, positions))
            if not candidates:
                problems.append(f"outdoor furnishing {group['source_id']}: no complete native {target_role} group fits")
                mappings.append({**group, "status": "blocked"})
                continue
            _, tp, x, y, positions = min(candidates, key=lambda v: v[0])
            additions = [native.Component(item, x+dx, y+dy, dz) for item, dx, dy, dz in tp.items]
            template = tp.id
        comps.extend(additions)
        occupied |= positions
        mappings.append({**group, "status": "mapped", "target_role": target_role, "template": template,
                         "target_cells": [list(p) for p in sorted(positions)]})
    return comps, mappings, problems, doors


def build(cfg, con, profile, bake_root, bounds, name, origin, out, cat_path, decor_path, theme=None):
    bake, definitions = Bake(bake_root), semantic.SourceDefinitions(con, profile)
    theme = theme or native.DEFAULT_THEME
    cat, lib = native.G.Catalogue(cat_path), native.furniture_library(decor_path, cfg.client_data)
    lands, props = land_library(cfg.client_data), prop_library(cfg.client_data)
    x0, y0, x1, y1 = bounds
    ox, oy = origin
    width, height = (x1-x0+1)*24, (y1-y0+1)*24
    if ox % 8 or oy % 8:
        raise semantic.ResolutionError("district origin must align to an 8x8 world block")
    blocks, parts, records, every, problems, tour = {}, [], [], [], [], []
    road_cells, parcel_roads, entrances, source_walk = set(), {}, [], set()
    with open_facet(cfg.client_data, 0) as facet:
        if not (0 <= ox and 0 <= oy and ox+width <= facet.width_blocks*8 and oy+height <= facet.height_blocks*8):
            raise semantic.ResolutionError("district bounds exceed facet 0")
        for by in range(oy//8, (oy+height)//8):
            for bx in range(ox//8, (ox+width)//8):
                blocks[(bx, by)] = {"land": Block(), "statics": []}
    for ay in range(y0, y1+1):
        for ax in range(x0, x1+1):
            if (ax, ay) not in bake.omts:
                raise semantic.ResolutionError(f"missing district parcel {(ax, ay)}")
            raw = bake.omts[(ax, ay)][0]
            levels = sorted(l["z"] for l in raw["layers"])
            layout = bake.layout(definitions, f"{name}_{ax}_{ay}", [ax, ay, ax, ay], levels)
            instance = catalogue.save_layout(con, profile, layout)
            dx, dy = (ax-x0)*24, (ay-y0)*24
            ground = next(l for l in layout["levels"] if l["z"] == 0)
            excluded = set()
            record = {"omt": [ax, ay], "overmap_terrain": raw.get("oter"), "instance": instance,
                      "levels": levels, "source_extras": [{"z": l["z"], "records": l["source_extras"]} for l in layout["levels"]]}
            if any(l["rooms"] for l in layout["levels"]):
                try:
                    comps, side, native_record = native.compile_layout(layout, cat, lib, theme)
                    problems += [f"{ax},{ay}: {p}" for p in side["problems"]]
                    problems += native.validate.check_parts(comps, cfg.client_data)
                    centre = [dx+12, dy+12]
                    part_name = f"parcel_{ax}_{ay}"
                    parts.append({"name": part_name, "centre": centre, "bounds": [dx,dy,dx+23,dy+23],
                                  "doors": side["doors"], "components": len(comps), "comps": comps})
                    every += [native.Component(c.item,c.x+centre[0],c.y+centre[1],c.z,c.visible) for c in comps]
                    for st in side["local"]["storeys"]:
                        if st["z"] == theme["floor_z"]:
                            excluded |= set(map(tuple, st["floor"])) | set(map(tuple, st["walls"])) | set(map(tuple, st["doors"]))
                    excluded |= {(c["x"],c["y"]) for c in ground["cells"] if c["indoors"]}
                    excluded |= {tuple(p) for m in native_record["furnishing_mappings"] if m.get("level")==0 and m["status"]=="mapped" for p in m.get("source_cells",[])}
                    record["native"] = native_record
                    record["part"] = part_name
                    # A tour per parcel is retained separately; the district tour
                    # walks streets between each building's outside/entrance/room stops.
                    record["tour"] = [{**s,"x":s["x"]+centre[0],"y":s["y"]+centre[1]} for s in side["stops"]]
                    record["flights"] = [{"foot":[f["foot"][0]+dx,f["foot"][1]+dy],
                        "arrive":[f["arrive"][0]+dx,f["arrive"][1]+dy],"z_from":f["z"],"z_to":f["z"]+f["rise"]}
                        for f in side["local"]["stairs"]]
                    if record["tour"]:
                        entrances.append({"part":part_name,"at":[record["tour"][0]["x"],record["tour"][0]["y"]]})
                except semantic.ResolutionError as exc:
                    problems.append(f"{ax},{ay}: {exc}")
                    record["native"] = {"status":"blocked", "problems":[str(exc)]}
            outdoor_comps, mappings, issues, outdoor_doors = outdoor(ground, excluded, cat, lib, props, theme)
            problems += [f"{ax},{ay}: {p}" for p in issues]
            record["outdoor_mappings"] = mappings
            parcel_roads[(ax,ay)] = {(c["x"],c["y"]) for c in ground["cells"] if c["role"] == "exterior" and exterior_land(c)=="road"}
            road_cells |= {(dx+x,dy+y) for x,y in parcel_roads[(ax,ay)]}
            source_walk |= {(dx+c["x"],dy+c["y"]) for c in ground["cells"] if c["walkable"] and not c["source_furniture_blocks"]}
            if outdoor_doors:
                owned = next((p for p in parts if p["name"] == record.get("part")), None)
                if owned is None:
                    owned = {"name":f"parcel_{ax}_{ay}","centre":[dx+12,dy+12],"bounds":[dx,dy,dx+23,dy+23],
                             "doors":[],"components":0,"comps":[]}
                    parts.append(owned)
                for door in outdoor_doors:
                    owned["doors"].append({**door,"x":door["x"]-12,"y":door["y"]-12})
                    owned["comps"].append(native.Component(native.G.CENTRE_MARKER,door["x"]-12,door["y"]-12,0,False))
                owned["components"] = len(owned["comps"])
            for c in ground["cells"]:
                wx, wy = ox+dx+c["x"], oy+dy+c["y"]
                blk = blocks[(wx//8,wy//8)]["land"]
                i = (wy%8)*8+wx%8
                blk.land_id[i] = native.G.scatter(lands[exterior_land(c)],wx,wy)
                blk.land_z[i] = 0
                for excavation in record.get("native",{}).get("world_requirements",{}).get("excavation",[]):
                    if [c["x"],c["y"]] in excavation["cells"]:
                        blk.land_z[i] = min(blk.land_z[i],excavation["land_z"])
            for c in outdoor_comps:
                wx, wy = ox+dx+c.x, oy+dy+c.y
                blocks[(wx//8,wy//8)]["statics"].append((c.item,wx%8,wy%8,c.z,0))
                every.append(native.Component(c.item,dx+c.x,dy+c.y,c.z,c.visible))
            records.append(record)
    from layout_import import routing
    street_report = routing.streets(road_cells,parcel_roads,bounds,entrances,source_walk,theme.get("require_public_network",True))
    problems.extend(street_report["problems"])
    if road_cells:
        rx,ry = min(road_cells,key=lambda p:(abs(p[0]-width/2)+abs(p[1]-height/2),p))
        tour.append({"name":"public_street_start","x":rx,"y":ry,"z":0})
    for record in records:
        if record.get("tour"):
            first = record["tour"][0]
            building_tour = [{**s,"name":f"{record['part']}_{s['name']}"} for s in record["tour"]]
            building_tour.append({**first,"name":f"{record['part']}_exit"})
            tour.extend(native.fort.walk_tour(building_tour,record.get("flights",[])))
    outdoor_parts = [{"centre":[0,0],"comps":[native.Component(sid,bx*8-ox+sx,by*8-oy+sy,z)
                    for (bx,by),v in blocks.items() for sid,sx,sy,z,hue in v["statics"]]}]
    land_z = {(bx*8-ox+i%8,by*8-oy+i//8):v["land"].land_z[i] for (bx,by),v in blocks.items() for i in range(64)}
    tour,route_problems = routing.short_tour(parts+outdoor_parts,tour,cfg.client_data,land_z,max_steps=6)
    route_problems += native.walkcheck.check_tour(parts+outdoor_parts,tour,
                                                routing.tile_info([c for p in parts+outdoor_parts for c in p["comps"]],cfg.client_data),land_z)
    problems.extend(route_problems)
    out.mkdir(parents=True, exist_ok=True)
    (out/"parts").mkdir(exist_ok=True)
    for p in parts:
        (out/"parts"/f"{p['name']}.json").write_text(json.dumps([c.as_list() for c in p["comps"]]),encoding="utf-8")
    converter=digest({p.name:p.read_text(encoding="utf-8") for p in
        [Path(__file__),Path(native.__file__),Path(semantic.__file__),Path(routing.__file__)]})
    for record in records:
        if record.get("part") and record.get("native"):
            result=record["native"]
            result["status"]="native-valid" if not problems else "blocked"
            result["district_validation"]={"passed":not problems,"problems":problems}
            part_file=out/"parts"/f"{record['part']}.json"
            result["native_artifact_hash"]=digest(json.loads(part_file.read_text(encoding="utf-8")))
            record["build_id"]=catalogue.save_build(con,record["instance"],theme,converter,result)
            catalogue.save_validation(con,record["build_id"],"district-offline",result["district_validation"])
            catalogue.save_artifacts(con,record["build_id"],"native-components",[part_file])
    # World projects are private derived outputs, including source attribution.
    storeys.write_project({"facet":0,"blocks":blocks},out/"world",name,cfg)
    (out/"world"/"SOURCE-LICENSE.txt").write_text(definitions.license,encoding="utf-8")
    side = {"format":1,"kind":"scene","name":name,"bounds":[0,0,width-1,height-1],
            "parts":[{k:v for k,v in p.items() if k!="comps"} for p in parts],"tour":tour,
            "notes":["Roads and outdoor statics are in the companion world project."],"valid":not problems,"problems":problems}
    (out/"scene.json").write_text(json.dumps(side,indent=1),encoding="utf-8")
    result = {"format":1,"kind":"uo-district","name":name,"origin":[ox,oy],"size":[width,height],
              "bounds":list(bounds),"theme":theme,"parcels":records,"land_library":lands,
              "street_validation":street_report,"route_validation":{"passed":not route_problems,"problems":route_problems},
              "status":"native-valid" if not problems else "blocked","problems":problems,
              "source_snapshot":definitions.snapshot,"bake_hash":bake.manifest_hash,"proof_status":"not-run"}
    (out/"district.json").write_text(json.dumps(result,indent=1),encoding="utf-8")
    native.render.render(every,cfg.client_data,out/"preview.png")
    native.render.render(every,cfg.client_data,out/"preview_noroof.png",max_z=26)
    return result
