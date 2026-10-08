"""Source semantics and room graphs, independent of target graphics."""
from __future__ import annotations

import collections
import copy
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from decorate import rooms as room_geometry
from layout_import.cdda import canonical, digest


class ResolutionError(ValueError):
    pass


def deep_merge(base, own):
    out = copy.deepcopy(base)
    for key, value in own.items():
        if key in ("copy-from", "extend", "delete", "relative", "proportional"):
            continue
        if isinstance(value, dict) and isinstance(out.get(key), dict):
            out[key] = deep_merge(out[key], value)
        else:
            out[key] = copy.deepcopy(value)
    for key, values in own.get("extend", {}).items():
        if not isinstance(values, list) or not isinstance(out.get(key, []), list):
            raise ResolutionError(f"unsupported extend expression: {key}")
        out[key] = list(dict.fromkeys(out.get(key, []) + values))
    for key, values in own.get("delete", {}).items():
        if not isinstance(values, list) or not isinstance(out.get(key, []), list):
            raise ResolutionError(f"unsupported delete expression: {key}")
        out[key] = [value for value in out.get(key, []) if value not in values]
    for key, value in own.get("relative", {}).items():
        if not isinstance(value, (int, float)) or not isinstance(out.get(key), (int, float)):
            raise ResolutionError(f"unsupported relative expression: {key}")
        out[key] += value
    for key, value in own.get("proportional", {}).items():
        if not isinstance(value, (int, float)) or not isinstance(out.get(key), (int, float)):
            raise ResolutionError(f"unsupported proportional expression: {key}")
        out[key] *= value
    return out


def name_text(value):
    if isinstance(value, str):
        return value
    if isinstance(value, dict):
        return value.get("str", value.get("str_sp", ""))
    return ""


class SourceDefinitions:
    """Terrain/furniture metadata under an explicit selected mod profile.

    Ambiguous same-namespace IDs fail. Mod layers with explicit copy-from-self
    apply to the prior layer; otherwise a later mod replaces the base record.
    This implements these two factories' semantic attributes, not all CDDA JSON.
    """
    def __init__(self, con, profile):
        row = con.execute("SELECT * FROM profile WHERE id=?", (profile,)).fetchone()
        if row is None or row["status"] == "blocked":
            raise ResolutionError("missing or blocked source profile")
        self.snapshot = row["snapshot_id"]
        self.order = json.loads(row["load_order_json"])
        self.index, self.cache = {}, {}
        for record in con.execute("SELECT d.* FROM definition d WHERE snapshot_id=? AND type IN ('terrain','furniture','palette','mapgen')", (self.snapshot,)):
            if record["namespace"] not in self.order:
                continue
            raw = json.loads(record["raw_json"])
            ids = raw.get("id", raw.get("abstract"))
            if isinstance(ids, str):
                ids = [ids]
            if isinstance(ids, list):
                for ident in ids:
                    self.index.setdefault((record["type"], ident), []).append((record, raw))
        self.license = con.execute("SELECT license_text FROM source_snapshot WHERE id=?", (self.snapshot,)).fetchone()[0]

    def get(self, kind, ident, stack=()):
        key = (kind, ident)
        if key in self.cache:
            return self.cache[key]
        if key in stack:
            raise ResolutionError(f"metadata inheritance cycle: {stack + (key,)}")
        candidates = self.index.get(key, [])
        if not candidates:
            raise ResolutionError(f"missing {kind} metadata: {ident}")
        by_namespace = collections.defaultdict(list)
        for row, raw in candidates:
            by_namespace[row["namespace"]].append((row, raw))
        result = None
        for namespace in self.order:
            rows = by_namespace.get(namespace, [])
            if len(rows) > 1:
                raise ResolutionError(f"ambiguous {namespace}:{kind}:{ident}; source load-order review required")
            if not rows:
                continue
            row, raw = rows[0]
            parent = raw.get("copy-from")
            if parent == ident:
                if result is None:
                    raise ResolutionError(f"copy-from self has no earlier layer: {key}")
                base = result
            elif parent:
                base = self.get(kind, parent, stack + (key,))
            else:
                base = {}
            result = deep_merge(base, raw)
            result["_definition"] = row["id"]
        self.cache[key] = result
        return result

    def terrain(self, ident):
        if ident in ("t_null", "t_open_air"):
            return {"name": ident, "move_cost": 0, "flags": ["NO_FLOOR"], "_definition": None}
        return self.get("terrain", ident)

    def furniture(self, ident):
        return {} if ident == "f_null" else self.get("furniture", ident)


FURNITURE_ROLES = (
    ("bed", ("bed", "cot", "mattress")),
    ("bath", ("toilet", "bathtub", "shower", "sink", "washbasin")),
    ("oven", ("oven", "stove", "fireplace", "fire pit")),
    ("forge", ("forge", "kiln", "welding", "anvil")),
    ("bookcase", ("bookcase", "bookshelf", "book shelf")),
    ("counter", ("counter", "workbench", "refrigerator", "freezer")),
    ("display", ("shelf", "rack", "display")),
    ("chair", ("chair", "bench", "sofa", "armchair", "stool", "seat")),
    ("table", ("table", "desk")),
    ("chest", ("dresser", "wardrobe", "locker", "cabinet", "cupboard")),
    ("container", ("crate", "box", "barrel", "trash", "bin", "basket")),
    ("plant", ("plant", "flower", "shrub", "bush")),
    ("light", ("lamp", "light", "candle", "torch")),
    ("farm", ("hay", "trough", "crop")),
    ("tools", ("machine", "tool", "drill", "computer", "console", "radio", "dryer", "dishwasher", "water heater")),
)


def furnishing_role(metadata):
    if not metadata:
        return None
    text = name_text(metadata.get("name")).lower()
    for role, words in FURNITURE_ROLES:
        if any(word in text for word in words):
            return role
    return "misc"


def cell_semantics(terrain, furniture, definitions):
    ter, furn = definitions.terrain(terrain), definitions.furniture(furniture)
    flags = set(ter.get("flags", []))
    if "NO_FLOOR" in flags or terrain in ("t_null", "t_open_air"):
        role = "void"
    elif "GOES_UP" in flags:
        role = "stair-up"
    elif "GOES_DOWN" in flags:
        role = "stair-down"
    elif flags & {"DOOR", "BARRICADABLE_DOOR"} or ("LOCKED" in flags and (
            "PICKABLE" in flags or any(word in name_text(ter.get("name")).lower() for word in ("door", "gate")))) or (
            ter.get("close") and any(word in name_text(ter.get("name")).lower() for word in ("door", "gate"))):
        role = "door"
    elif "WINDOW" in flags:
        role = "window"
    elif "WALL" in flags or (ter.get("move_cost", 2) == 0 and (
            "WALL" in ter.get("connect_groups", []) or any(word in name_text(ter.get("name")).lower() for word in ("fence", "wall")))):
        role = "wall"
    elif "DEEP_WATER" in flags or "SHALLOW_WATER" in flags:
        role = "water"
    elif "GUTTER" in ter.get("connect_groups", []) or "gutter" in name_text(ter.get("name")).lower():
        role = "roof-edge"
    elif "ROOF" in flags or "roof" in name_text(ter.get("name")).lower():
        role = "roof"
    elif ter.get("move_cost", 2) == 0:
        role = "obstacle"
    elif "INDOORS" in flags:
        role = "floor"
    else:
        role = "exterior"
    # Structural walkability deliberately ignores source closed-door state.
    walkable = role in ("floor", "exterior", "door", "stair-up", "stair-down", "roof", "roof-edge")
    return {"role": role, "indoors": "INDOORS" in flags, "walkable": walkable,
            "furnishing_role": furnishing_role(furn), "flags": sorted(flags),
            "terrain_name": name_text(ter.get("name")), "furniture_name": name_text(furn.get("name")),
            "terrain_definition": ter.get("_definition"), "furniture_definition": furn.get("_definition"),
            "access": "locked" if "LOCKED" in flags or "locked" in name_text(ter.get("name")).lower() else
                      "closed" if role == "door" and ter.get("move_cost", 2) == 0 else "open",
            "roof_terrain": ter.get("roof"), "source_furniture_blocks": furn.get("move_cost_mod", 0) < 0}


def infer_rooms(level):
    cells = {(c["x"], c["y"]): c for c in level["cells"]}
    walls = {p for p, c in cells.items() if c["role"] in ("wall", "window", "obstacle")}
    doors = {p for p, c in cells.items() if c["role"] == "door"}
    # Explicit INDOORS is authoritative; flood to the boundary detects gaps and
    # identifies covered floors that source metadata doesn't label INDOORS.
    walk = {p for p, c in cells.items() if c["walkable"]}
    boundary = {p for p in walk if any((p[0] + dx, p[1] + dy) not in cells
                 for _, dx, dy in room_geometry.DIRS)}
    exterior = set()
    for group in room_geometry.components(walk - doors):
        if group & boundary:
            exterior.update(group)
    inside = {p for p in walk - exterior - doors if cells[p]["role"] not in ("roof", "roof-edge")}
    inside |= {p for p in walk - doors if cells[p]["indoors"] and cells[p]["role"] not in ("roof", "roof-edge")}
    segmented = room_geometry.segment(inside, walls, doors, {p for p, c in cells.items() if c["role"] == "window"}, min_cells=1)
    rooms = []
    open_zones = []
    owners = {}
    for i, region in enumerate(segmented):
        if not any(cells[p]["indoors"] or cells[p]["role"] in ("floor", "stair-up", "stair-down") for p in region.cells):
            open_zones.append({"cells": [list(p) for p in sorted(region.cells)], "use": "exterior-enclosure", "label_origin": "inferred"})
            continue
        roles = collections.Counter(cells[p]["furnishing_role"] for p in region.cells if cells[p]["furnishing_role"])
        use, evidence = "unclassified", []
        for label, kinds in (("bedroom", {"bed"}), ("bathroom", {"bath"}), ("kitchen", {"oven"}),
                             ("workshop", {"forge", "tools"}), ("library", {"bookcase"}),
                             ("shop", {"display", "counter"}), ("dining", {"table", "chair"}),
                             ("storage", {"chest", "container"})):
            if set(roles) & kinds:
                use = label
                evidence = [f"{kind}:{roles[kind]}" for kind in sorted(set(roles) & kinds)]
                break
        ident = f"z{level['z']}:room{i}"
        owners.update({p: ident for p in region.cells})
        rooms.append({"id": ident, "cells": [list(p) for p in sorted(region.cells)],
                      "bounds": list(region.bbox), "area": len(region.cells), "use": use,
                      "label_origin": "inferred", "confidence": 0.75 if evidence else 0.25,
                      "evidence": evidence, "open_plan": len(roles) > 2, "furnishing_roles": dict(roles),
                      "exterior_leak": bool(region.cells & exterior)})
        authored = collections.Counter(cells[p].get("authored_room_name") for p in region.cells if cells[p].get("authored_room_name"))
        if authored:
            label=authored.most_common(1)[0][0]
            use=next(cells[p].get("authored_room_use","unclassified") for p in region.cells if cells[p].get("authored_room_name")==label)
            rooms[-1].update(use=use,label_origin="authored",confidence=1.0,evidence=[f"Authored room: {label}"],
                             source_room_names=dict(authored),open_plan=len(authored)>1)
    openings = []
    for p in sorted(doors | {p for p, c in cells.items() if c["role"] == "window"}):
        neighboring = sorted({owners[q] for _, dx, dy in room_geometry.DIRS
                              if (q := (p[0] + dx, p[1] + dy)) in owners})
        outside = any((p[0] + dx, p[1] + dy) in exterior for _, dx, dy in room_geometry.DIRS)
        openings.append({"at": list(p), "kind": cells[p]["role"], "rooms": neighboring,
                         "exterior": outside, "access": cells[p]["access"]})
    level["rooms"], level["openings"] = rooms, openings
    level["open_plan_zones"] = open_zones
    level["exterior"] = [list(p) for p in sorted(exterior)]
    return level


def rotate(layout, turns):
    """Rotate semantics and lineage together, keeping source coordinates intact."""
    out = copy.deepcopy(layout)
    for _ in range(turns % 4):
        w, h = out["width"], out["height"]
        for level in out["levels"]:
            for room in level.get("authored_rooms",[]):
                room["cells"]=[[h-1-y,x] for x,y in room["cells"]]
            for cell in level["cells"]:
                cell["x"], cell["y"] = h - 1 - cell["y"], cell["x"]
            level["cells"].sort(key=lambda c: (c["y"], c["x"]))
            infer_rooms(level)
        out["width"], out["height"] = h, w
    out.setdefault("adaptations", []).append({"kind": "rotation", "quarter_turns": turns % 4})
    out["layout_hash"] = layout_hash(out)
    return out


def layout_hash(layout):
    return digest({"format": layout["format"], "width": layout["width"], "height": layout["height"],
                   "levels": [{"z": level["z"], "cells": [[c["x"], c["y"], c["role"], c["indoors"],
                         c["furnishing_role"], c["access"]] for c in level["cells"]]} for level in layout["levels"]]})


def plans(layout, out):
    from PIL import Image, ImageDraw
    palette = {"wall": "#777777", "window": "#439bcc", "door": "#ffa646", "floor": "#c4b899",
               "exterior": "#58815d", "void": "#16191b", "roof": "#784343", "roof-edge": "#916464", "water": "#3e6fb8",
               "stair-up": "#b571d9", "stair-down": "#b571d9", "obstacle": "#434343"}
    out.mkdir(parents=True, exist_ok=True)
    for level in layout["levels"]:
        img = Image.new("RGB", (layout["width"] * 18, layout["height"] * 18 + 32), "#16191b")
        draw = ImageDraw.Draw(img)
        draw.text((8, 8), f"{layout['name']} / source level {level['z']}", fill="white")
        for c in level["cells"]:
            x, y = c["x"] * 18, c["y"] * 18 + 32
            draw.rectangle((x, y, x + 16, y + 16), fill=palette[c["role"]])
            if c["furnishing_role"]:
                draw.rectangle((x + 5, y + 5, x + 11, y + 11), fill="#dddb84")
        for i, room in enumerate(level.get("rooms", [])):
            x, y = room["cells"][len(room["cells"]) // 2]
            draw.text((x * 18, y * 18 + 32), str(i), fill="black")
        img.save(out / f"source_level_{level['z']}.png")
