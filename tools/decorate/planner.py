"""The AI room planner: an OpenAI model chooses what goes where, the decorator places and checks.

One call per house. The model gets, per storey, a labelled plan (text grid and a picture drawn
here, never game art), each room's cells, openings and neighbours, a brief of the house, and a
menu: the room types the database knows and, per type, the learned furniture groups (templates)
with their footprints, wall sides and item names, plus the typical density and the kinds found
together. One to three real UO rooms of each type go along as examples, as data.

It answers in structured JSON: a type per room, templates at cells, a focal piece and a reason.
Every placement is checked with the decorator's own rules (fit, the wall sides a template stands
against, keep-clear cells, groups kept apart, every free cell reachable). What fails goes back to
the model once with the exact errors; a room still without its defining piece is then furnished
by the rules, as before.

Answers are cached in build/decorate/plans/ by house, seed, model, prompt version and a hash of
the request, so a rebuild is repeatable and never calls the API (`offline=True` refuses to).
Each call is logged to the OpenAI usage database (OPENAI_USAGE_DB, default
~/.config/openai/usage.db, the same `runs` table the openai-api skill writes).
The key is the user's OPENAI_API_KEY; it is never printed or written anywhere.
"""
from __future__ import annotations

import base64
import collections
import hashlib
import io
import json
import os
import random
import re
import sqlite3
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import decorate as D  # noqa: E402
import rooms as R  # noqa: E402

PROMPT_VERSION = 3
MODEL = os.environ.get("GUO_DECORATE_MODEL", "gpt-5.4")      # gpt-5.4-mini ignored the anchor lists half the time
EFFORT = os.environ.get("GUO_DECORATE_EFFORT", "medium")
API = os.environ.get("OPENAI_BASE_URL", "https://api.openai.com/v1").rstrip("/")
MENU_FLOOR, MENU_WALL = 16, 6           # templates offered per room type
EXAMPLES = 1                            # mined rooms shown per room type on the menu
MAX_SPOTS = 14                          # legal anchors listed per footprint per room

SYSTEM = """You furnish the interior of a house in Ultima Online (1997), room by room, the way UO's own
towns are furnished. You only choose: a type for every room, and which furniture groups
(templates, from the menu) stand at which cells. A program places them and checks every rule.

Coordinates are house cells, x to the east, y to the south. A floor template's anchor (x, y) is its
north-west corner: it covers the cells (x + dx, y + dy) of its footprint.

Rules every placement must keep:
1. A floor template lies inside its room on free cells ('.' in the grid), never on a keep-clear
   cell ('+': in front of doors, at stairs and windows).
2. It touches the walls on exactly the sides listed as its `against` (N, E, S, W): with side N,
   the wall must run along its north edge; a template with no sides stands free, off every wall.
   This is what makes beds, bookcases and counters face into the room.
3. Groups keep one cell apart: no cell of one group beside (N, E, S or W) a cell of another.
4. Every free cell of the room stays reachable from its doors and stairs.
5. A wall-hung template (hangs: true) goes on a room cell whose wall on its `against` side is plain
   wall (not a door or window); give that room cell as its anchor.
Each room lists, per footprint (the `shape` of each template), the anchors where a template of that
shape fits the empty room. Choose anchors from those lists: they already keep rules 1, 2 and 5.
What they cannot know is the other groups you place, so keep your own groups apart (rule 3) and
leave a way through (rule 4). A shape with no list for a room does not fit in it.
Place the room type's defining piece first (a bed in a bedroom, bookcases in a library, a forge in a
smithy, an oven in a bakery), then what belongs with it, against the walls, and keep the middle
and the way between the doors open. Each room gives `aim_groups`, about what the original towns put in a room
that size; place about that many (a room of one type with a lower density, fewer), never an
empty room. Make the rooms fit the house's brief and each other (kitchen by the dining
room, bedrooms upstairs, a shop or workroom by the street door). Every room gets a type."""

PLAN_SCHEMA = {
    "type": "object", "additionalProperties": False, "required": ["rooms"],
    "properties": {"rooms": {"type": "array", "items": {
        "type": "object", "additionalProperties": False,
        "required": ["room", "type", "focal", "placements", "reason"],
        "properties": {
            "room": {"type": "string"},
            "type": {"type": "string"},
            "focal": {"type": "integer", "description": "the template id of the room's main piece, or -1"},
            "placements": {"type": "array", "items": {
                "type": "object", "additionalProperties": False, "required": ["template", "x", "y"],
                "properties": {"template": {"type": "integer"}, "x": {"type": "integer"}, "y": {"type": "integer"}}}},
            "reason": {"type": "string"}}}}},
}


# ---------------------------------------------------------------- the house as the model sees it
def house_plans(side: dict) -> list[tuple]:
    """[(storey, storey dict, [RoomPlan], walls)] with the decorator's own segmentation."""
    out = []
    for n, st in enumerate(side["local"]["storeys"]):
        plans, walls = D.plan_storey(n, st, side["local"].get("stairs", []))
        out.append((n, st, plans, walls))
    return out


def room_ids(all_plans) -> dict[str, D.RoomPlan]:
    return {f"s{n}r{i}": rp for n, _st, plans, _w in all_plans for i, rp in enumerate(plans)}


def storey_grid(side: dict, n: int, all_plans, ids: dict) -> str:
    """The storey as text: a column of y, a header of x (last digit), and one character a cell."""
    st = side["local"]["storeys"][n]
    ch = {}
    for c in st.get("floor", []):
        ch[tuple(c)] = " "
    for key, sym in (("walls", "#"), ("windows", "W"), ("doors", "D"), ("arrivals", "a")):
        for c in st.get(key, []):
            ch[tuple(c)] = sym
    for s in side["local"].get("stairs", []):
        if s["z"] == st["z"]:
            for c in s["cells"]:
                ch[tuple(c)] = "S"
    for rid, rp in ids.items():
        if rp.storey != n:
            continue
        for c in rp.room.cells:
            ch[c] = "+" if c in rp.clear else "."
    if not ch:
        return ""
    xs, ys = [c[0] for c in ch], [c[1] for c in ch]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    lines = ["     " + "".join(str(abs(x) % 10) for x in range(x0, x1 + 1)) + f"   (x {x0}..{x1})"]
    for y in range(y0, y1 + 1):
        lines.append(f"{y:>4} " + "".join(ch.get((x, y), " ") for x in range(x0, x1 + 1)))
    return "\n".join(lines)


def room_info(rid: str, rp: D.RoomPlan, ids: dict, walls: set) -> dict:
    x0, y0, x1, y1 = rp.room.bbox
    door_owner = collections.defaultdict(set)
    for oid, orp in ids.items():
        if orp.storey == rp.storey:
            for c, _d in orp.room.doors:
                door_owner[c].add(oid)
    nb = sorted({o for c, _d in rp.room.doors for o in door_owner[c] if o != rid})
    free = sorted(rp.room.cells - rp.clear)
    return {"room": rid, "storey": rp.storey, "bbox": [x0, y0, x1, y1], "area": len(rp.room.cells),
            "free_cells": len(free),
            "doors": [{"at": list(c), "side": d} for c, d in sorted(rp.room.doors)],
            "windows": [{"at": list(c), "side": d} for c, d in sorted(rp.room.windows)],
            "stairs_or_arrivals": [list(p) for p in rp.portals if p not in
                                   {(c[0] - R.STEP[d][0], c[1] - R.STEP[d][1]) for c, d in rp.room.doors}],
            "neighbours": nb}


def shape_key(tp: D.Template) -> str:
    """Templates with the same footprint and wall sides fit in the same places."""
    cells = "" if len(tp.cells) == tp.w * tp.h else "~" + ".".join(f"{x}{y}" for x, y in sorted(tp.cells))
    return f"{'wall' if tp.on_wall else 'floor'}{tp.w}x{tp.h}{'-' + tp.against if tp.against else ''}{cells}"


def spots(tp: D.Template, rp: D.RoomPlan, walls: set, windows: set, doors: set) -> list:
    """Every anchor where the template fits the room while it is empty, as the model names it."""
    if tp.on_wall:
        d = R.STEP[tp.against[0]]
        return [[w[0] - d[0], w[1] - d[1]] for w in D.wall_spots(tp, rp, walls, windows, doors)]
    x0, y0, x1, y1 = rp.room.bbox
    return [[x, y] for y in range(y0, y1 - tp.h + 2) for x in range(x0, x1 - tp.w + 2)
            if D.fits(tp, x, y, rp, walls) and D.keeps_paths(rp, {(x + dx, y + dy) for dx, dy in tp.cells})]


def thin(xs: list, k: int) -> list:
    if len(xs) <= k:
        return xs
    return [xs[round(i * (len(xs) - 1) / (k - 1))] for i in range(k)]


def aim_groups(area: int) -> int:
    """About as many groups as the rules would place at most: 30% of the room covered in groups of
    about 2 cells, and no more than one group per 6 cells."""
    return max(2, min(area // 6, round(area * 0.3 / 2)))


def template_entry(tp: D.Template, names: dict) -> dict:
    items = collections.Counter(names.get(it[0], "?") for it in tp.items)
    e = {"id": tp.id, "shape": shape_key(tp), "w": tp.w, "h": tp.h, "against": tp.against, "hangs": tp.on_wall,
         "kinds": sorted(k for k in tp.kinds if k != "misc"),
         "items": ", ".join(f"{k} x{v}" if v > 1 else k for k, v in items.most_common())}
    if len(tp.cells) < tp.w * tp.h:
        e["cells"] = sorted([list(c) for c in tp.cells])
    return e


def menu(lib: D.Library, allowed) -> tuple[dict, dict]:
    """Per room type: its stats and the ids of the templates offered; and those templates."""
    types, offered = {}, {}
    for t in allowed:
        st = lib.room_types.get(t)
        cands = lib.for_type(t)
        if not st or not cands:
            continue
        floor = sorted((c for c in cands if not c[0].on_wall), key=lambda c: (-c[1], c[0].id))[:MENU_FLOOR]
        wall = sorted((c for c in cands if c[0].on_wall), key=lambda c: (-c[1], c[0].id))[:MENU_WALL]
        anchors = D.ANCHORS.get(t, set())
        types[t] = {"mined_rooms": st["rooms"], "ground": st["ground"], "upper": st["upper"],
                    "typical_area": round(st["mean_area"]), "density": round(st["density"], 2),
                    "defining_kinds": sorted(anchors),
                    "templates": [tp.id for tp, _w in floor + wall]}
        for tp, _w in floor + wall:
            offered[tp.id] = template_entry(tp, lib.names)
    return types, offered


def examples(con, rtype: str, area: int, k: int = EXAMPLES) -> list[dict]:
    """Real UO rooms of a type (enclosed, furnished), the nearest in area, as data."""
    if con is None:
        return []
    rows = con.execute("SELECT id, w, h, area FROM room WHERE type = ? AND enclosed = 1 AND items >= 4 "
                       "ORDER BY ABS(area - ?), id LIMIT ?", (rtype, area, k)).fetchall()
    out = []
    for rid, w, h, a in rows:
        ops = con.execute("SELECT kind, x, y, side FROM opening WHERE room_id = ?", (rid,)).fetchall()
        fur = con.execute("SELECT name, x, y, against FROM furnishing WHERE room_id = ? AND z_above < 20 "
                          "ORDER BY y, x LIMIT 30", (rid,)).fetchall()
        out.append({"type": rtype, "size": [w, h], "area": a,
                    "openings": [f"{kd}@{x},{y}{s}" for kd, x, y, s in ops],
                    "items": [f"{nm}@{x},{y}" + (f"/{ag}" if ag else "") for nm, x, y, ag in fur]})
    return out


def cooccur(con, kinds: set, k: int = 24) -> list[str]:
    if con is None:
        return []
    rows = con.execute("SELECT a, b, rooms, adjacent FROM cooccur ORDER BY adjacent DESC, rooms DESC").fetchall()
    return [f"{a}+{b}: side by side in {adj}, together in {n} rooms" for a, b, n, adj in rows
            if a in kinds and b in kinds][:k]


def plan_png(side: dict, n: int, all_plans, ids: dict, cell: int = 18) -> bytes:
    """Our own schematic of a storey (no game art): room cells light, keep-clear hatched, walls
    grey, doors orange, windows blue, stairs purple, x and y numbered along the edges, room ids."""
    from PIL import Image, ImageDraw
    st = side["local"]["storeys"][n]
    col = {}
    for c in st.get("floor", []):
        col[tuple(c)] = (60, 60, 60)
    for key, rgb in (("walls", (150, 150, 150)), ("windows", (80, 150, 230)), ("doors", (235, 150, 40)),
                     ("arrivals", (255, 90, 255))):
        for c in st.get(key, []):
            col[tuple(c)] = rgb
    for s in side["local"].get("stairs", []):
        if s["z"] == st["z"]:
            for c in s["cells"]:
                col[tuple(c)] = (170, 70, 200)
    clear = set()
    for rid, rp in ids.items():
        if rp.storey == n:
            for c in rp.room.cells:
                col[c] = (225, 215, 190)
            clear |= rp.clear
    xs, ys = [c[0] for c in col], [c[1] for c in col]
    x0, y0 = min(xs), min(ys)
    m = 22
    img = Image.new("RGB", ((max(xs) - x0 + 1) * cell + m, (max(ys) - y0 + 1) * cell + m), (15, 15, 15))
    d = ImageDraw.Draw(img)
    for (x, y), rgb in col.items():
        px, py = (x - x0) * cell + m, (y - y0) * cell + m
        d.rectangle([px, py, px + cell - 2, py + cell - 2], fill=rgb)
        if (x, y) in clear:
            d.line([px, py + cell - 2, px + cell - 2, py], fill=(200, 60, 60), width=2)
    for x in range(x0, max(xs) + 1):
        d.text(((x - x0) * cell + m + 3, 4), str(abs(x) % 100), fill=(230, 230, 230))
    for y in range(y0, max(ys) + 1):
        d.text((2, (y - y0) * cell + m + 3), str(y), fill=(230, 230, 230))
    for rid, rp in ids.items():
        if rp.storey == n:
            bx0, by0, bx1, by1 = rp.room.bbox
            cx, cy = ((bx0 + bx1) / 2 - x0) * cell + m, ((by0 + by1) / 2 - y0) * cell + m
            d.text((cx, cy), rid, fill=(20, 20, 160))
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def build_request(side: dict, lib: D.Library, con=None, brief: str = "", allowed=D.HOUSE_TYPES,
                  types: dict | None = None, images: bool = True) -> dict:
    all_plans = house_plans(side)
    ids = room_ids(all_plans)
    tmenu, offered = menu(lib, allowed)
    rooms = [room_info(rid, rp, ids, all_plans[rp.storey][3]) for rid, rp in ids.items()]
    by_shape = {}
    for tid in sorted(offered):
        tp = next(t for t in lib.templates if t.id == tid)
        by_shape.setdefault(shape_key(tp), tp)
    for info, rp in zip(rooms, ids.values()):
        _n, st, _p, walls = all_plans[rp.storey]
        windows, doors = set(map(tuple, st["windows"])), set(map(tuple, st["doors"]))
        info["aim_groups"] = aim_groups(len(rp.room.cells))
        info["anchors"] = {k: thin(v, MAX_SPOTS) for k, tp in by_shape.items()
                           if (v := spots(tp, rp, walls, windows, doors))}
    forced = {}
    for key, t in (types or {}).items():
        sn, xy = key.split(":")
        cell = tuple(int(v) for v in xy.split(","))
        for rid, rp in ids.items():
            if rp.storey == int(sn) and cell in rp.room.cells:
                forced[rid] = t
    ex = {}
    for t in tmenu:
        ex[t] = examples(con, t, tmenu[t]["typical_area"])
    kinds = {k for e in offered.values() for k in e["kinds"]}
    payload = {
        "house": side["name"], "brief": brief or "an ordinary townhouse",
        "storeys": [{"storey": n, "floor_z": st["z"], "grid": storey_grid(side, n, all_plans, ids)}
                    for n, st, _p, _w in all_plans],
        "grid_legend": "# wall, W window, D door, S stair, a arrival, . free room cell, + keep-clear room cell",
        "rooms": rooms, "forced_types": forced,
        "room_types": tmenu, "templates": list(offered.values()),
        "found_together": cooccur(con, kinds),
        "examples": ex,
    }
    user = ("Furnish this house. Answer with the JSON plan only.\n\n" + json.dumps(payload, separators=(",", ":")))
    pngs = [plan_png(side, n, all_plans, ids) for n, _st, _p, _w in all_plans] if images else []
    return {"system": SYSTEM, "user": user, "pngs": pngs, "offered": sorted(offered), "forced": forced}


# ---------------------------------------------------------------- checking a plan
def why_not(tp: D.Template, x: int, y: int, rp: D.RoomPlan, walls: set) -> str:
    """'' when a floor template fits at (x, y); otherwise what is wrong, in words."""
    cells = {(x + dx, y + dy) for dx, dy in tp.cells}
    out = sorted(cells - rp.room.cells)
    if out:
        return f"cells {[list(c) for c in out[:4]]} are outside the room"
    if cells & rp.clear:
        return f"cells {[list(c) for c in sorted(cells & rp.clear)[:4]]} must stay clear"
    if cells & rp.occupied:
        return "it overlaps another group"
    for (cx, cy) in cells:
        for _d, dx, dy in R.DIRS:
            nb = (cx + dx, cy + dy)
            if nb in rp.occupied and nb not in cells:
                return f"it touches another group at {list(nb)} (keep one cell apart)"
    x1, y1 = x + tp.w - 1, y + tp.h - 1
    for d, dx, dy in R.DIRS:
        edge = [c for c in cells if (d == "N" and c[1] == y) or (d == "S" and c[1] == y1)
                or (d == "W" and c[0] == x) or (d == "E" and c[0] == x1)]
        touching = sum((c[0] + dx, c[1] + dy) in walls for c in edge) * 2 >= len(edge)
        if d in tp.against and not touching:
            return f"its {d} side must stand against a wall and does not"
        if d not in tp.against and touching:
            return f"its {d} side touches a wall but the template does not stand against {d}"
    if not D.keeps_paths(rp, cells):
        return "it cuts off part of the room from the doors and stairs"
    return ""


SNAP = 3                                # cells a refused group may move to its nearest legal anchor


def snap_to(tp: D.Template, x: int, y: int, rp: D.RoomPlan, walls: set, windows: set, doors: set):
    """The legal anchor nearest (x, y), within SNAP, given what the room already holds; or None."""
    if tp.on_wall:
        d = R.STEP[tp.against[0]]
        cands = [(w[0] - d[0], w[1] - d[1]) for w in D.wall_spots(tp, rp, walls, windows, doors)]
        ok = lambda cx, cy: True  # noqa: E731
    else:
        x0, y0, x1, y1 = rp.room.bbox
        cands = [(cx, cy) for cy in range(y0, y1 - tp.h + 2) for cx in range(x0, x1 - tp.w + 2)]
        ok = lambda cx, cy: not why_not(tp, cx, cy, rp, walls)  # noqa: E731
    near = sorted((max(abs(cx - x), abs(cy - y)), abs(cx - x) + abs(cy - y), cy, cx) for cx, cy in cands
                  if max(abs(cx - x), abs(cy - y)) <= SNAP)
    return next(((cx, cy) for _a, _b, cy, cx in near if ok(cx, cy)), None)


def apply(plan: dict, all_plans, lib: D.Library, allowed=D.HOUSE_TYPES, offered=None,
          forced: dict | None = None, snap: bool = False, moved: list | None = None) -> tuple[dict, list[str]]:
    """Place a plan's rooms. Returns ({room id: RoomPlan with .placed}, [errors]). With `snap`, a
    group refused where it was put goes to the nearest legal anchor instead (listed in `moved`)."""
    ids = room_ids(all_plans)
    by_id = {tp.id: tp for tp in lib.templates}
    errors = []
    moved = moved if moved is not None else []
    for rp in ids.values():
        rp.placed, rp.occupied, rp.wall_used, rp.type = [], set(), set(), ""
    seen = set()
    for r in plan.get("rooms", []):
        rid = r.get("room")
        rp = ids.get(rid)
        if rp is None or rid in seen:
            errors.append(f"room {rid}: no such room" if rp is None else f"room {rid}: planned twice")
            continue
        seen.add(rid)
        t = r.get("type", "")
        if t not in allowed or t not in lib.room_types:
            errors.append(f"room {rid}: type '{t}' is not on the menu")
            continue
        if forced and rid in forced and t != forced[rid]:
            errors.append(f"room {rid}: must be a {forced[rid]} (forced_types), not a {t}")
            continue
        rp.type = t
        n, st, _plans, walls = all_plans[rp.storey]
        windows, doors = set(map(tuple, st["windows"])), set(map(tuple, st["doors"]))
        for p in r.get("placements", []):
            tid, x, y = p.get("template"), p.get("x"), p.get("y")
            tp = by_id.get(tid)
            if tp is None or (offered is not None and tid not in offered):
                errors.append(f"room {rid}: template {tid} is not on the menu")
                continue
            if not isinstance(x, int) or not isinstance(y, int):
                errors.append(f"room {rid}: template {tid}: no anchor")
                continue
            d = tp.against[:1]
            if tp.on_wall:
                w = (x + R.STEP[d][0], y + R.STEP[d][1]) if d in R.STEP else None
                if (x, y) not in rp.room.cells:
                    why = "not a cell of the room"
                elif w is None or w not in walls or w in windows or w in doors:
                    why = f"no plain wall on its {d} side"
                elif w in rp.wall_used:
                    why = "that wall cell is taken"
                else:
                    why = ""
            else:
                why = why_not(tp, x, y, rp, walls)
            if why:
                to = snap_to(tp, x, y, rp, walls, windows, doors) if snap else None
                if to is None:
                    errors.append(f"room {rid}: template {tid} at ({x},{y}): {why}")
                    continue
                moved.append(f"room {rid}: template {tid} moved from ({x},{y}) to {to}: {why}")
                x, y = to
            if tp.on_wall:
                w = (x + R.STEP[d][0], y + R.STEP[d][1])
                rp.wall_used.add(w)
                rp.placed.append((tp, w[0], w[1]))
            else:
                rp.occupied |= {(x + dx, y + dy) for dx, dy in tp.cells}
                rp.placed.append((tp, x, y))
    for rid in ids:
        if rid not in seen:
            errors.append(f"room {rid}: missing from the plan")
    return ids, errors


def top_up(rng: random.Random, lib: D.Library, rp: D.RoomPlan, walls: set, windows: set, doors: set) -> int:
    """Fill a planned room short of its aim with its type's secondary pieces (never a second
    defining piece), placed by the rules. Returns how many groups were added."""
    anchors = D.ANCHORS.get(rp.type, set())
    pool = [(t, w) for t, w in lib.for_type(rp.type) if not set(t.kinds) & anchors
            and t.id not in {tp.id for tp, _x, _y in rp.placed}]
    added, tries = 0, 0
    while len(rp.placed) < aim_groups(len(rp.room.cells)) and pool and tries < 40:
        tries += 1
        tp = D.weighted(rng, pool)
        if tp.on_wall:
            ws = D.wall_spots(tp, rp, walls, windows, doors)
            if ws:
                w = rng.choice(ws)
                rp.wall_used.add(w)
                rp.placed.append((tp, w[0], w[1]))
                added += 1
            continue
        x0, y0, x1, y1 = rp.room.bbox
        free = [(x, y) for y in range(y0, y1 - tp.h + 2) for x in range(x0, x1 - tp.w + 2)
                if D.fits(tp, x, y, rp, walls) and D.keeps_paths(rp, {(x + dx, y + dy) for dx, dy in tp.cells})]
        if free:
            x, y = rng.choice(free)
            rp.occupied |= {(x + dx, y + dy) for dx, dy in tp.cells}
            rp.placed.append((tp, x, y))
            added += 1
    return added


def has_anchor(rp: D.RoomPlan) -> bool:
    anchors = D.ANCHORS.get(rp.type)
    return not anchors or any(set(tp.kinds) & anchors for tp, _x, _y in rp.placed)


# ---------------------------------------------------------------- the API
def api_key() -> str | None:
    k = None
    if sys.platform == "win32":
        try:
            import winreg
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as h:
                k = winreg.QueryValueEx(h, "OPENAI_API_KEY")[0]
        except OSError:
            k = None
    k = k or os.environ.get("OPENAI_API_KEY")
    return k.strip() if k else None


def redact(text: str) -> str:
    return re.sub(r"sk-[A-Za-z0-9_*\-]+", "sk-[redacted]", text or "")


def log_run(**row) -> None:
    """One row in the shared OpenAI usage database (the openai-api skill's `runs` table)."""
    db = Path(os.environ.get("OPENAI_USAGE_DB", Path.home() / ".config" / "openai" / "usage.db"))
    try:
        db.parent.mkdir(parents=True, exist_ok=True)
        con = sqlite3.connect(db)
        con.execute("CREATE TABLE IF NOT EXISTS runs (id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, "
                    "caller TEXT, project TEXT, tag TEXT, kind TEXT, model TEXT, size TEXT, quality TEXT, n INTEGER, "
                    "status TEXT NOT NULL, prompt_chars INTEGER, input_tokens INTEGER, input_text_tokens INTEGER, "
                    "input_image_tokens INTEGER, output_tokens INTEGER, cost_usd REAL, latency_s REAL, outputs TEXT, "
                    "error TEXT)")
        row.setdefault("ts", time.strftime("%Y-%m-%dT%H:%M:%S"))
        row.setdefault("project", "GUO")
        cols = ",".join(row)
        con.execute(f"INSERT INTO runs ({cols}) VALUES ({','.join('?' * len(row))})", list(row.values()))
        con.commit()
        con.close()
    except sqlite3.Error as e:
        print(f"[planner] usage log not written: {e}")


def openai_call(messages: list, model: str = MODEL, caller: str = "GUO3", tag: str = "decorator",
                note: str = "", schema: dict = PLAN_SCHEMA, name: str = "house_plan",
                project: str = "GUO") -> tuple[dict, dict]:
    """One Responses API call answered in `schema` (strict JSON). Returns (answer, usage)."""
    key = api_key()
    if not key:
        raise RuntimeError("no OPENAI_API_KEY for the planner")
    body = {"model": model, "input": messages,
            "text": {"format": {"type": "json_schema", "name": name, "schema": schema, "strict": True}}}
    if model.startswith(("gpt-5", "o")):
        body["reasoning"] = {"effort": EFFORT}
    req = urllib.request.Request(API + "/responses", data=json.dumps(body).encode(), method="POST",
                                 headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    t0 = time.time()
    chars = sum(len(c.get("text", "")) for m in messages for c in (m["content"] if isinstance(m["content"], list)
                                                                    else [{"text": m["content"]}]))
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            resp = json.loads(r.read())
    except urllib.error.HTTPError as e:
        try:
            msg = json.loads(e.read()).get("error", {}).get("message", "")
        except Exception:
            msg = ""
        err = redact(f"HTTP {e.code}: {msg}")
        log_run(caller=caller, tag=tag, kind="plan", model=model, status="error", prompt_chars=chars, project=project,
                latency_s=round(time.time() - t0, 1), error=err, outputs=note)
        raise RuntimeError(err) from None
    usage = resp.get("usage") or {}
    text = "".join(c.get("text", "") for o in resp.get("output", []) if o.get("type") == "message"
                   for c in o.get("content", []) if c.get("type") == "output_text")
    det = usage.get("input_tokens_details") or {}
    log_run(caller=caller, tag=tag, kind="plan", model=resp.get("model", model), status="ok", prompt_chars=chars, project=project,
            input_tokens=usage.get("input_tokens"), input_text_tokens=None,
            input_image_tokens=det.get("image_tokens"), output_tokens=usage.get("output_tokens"),
            latency_s=round(time.time() - t0, 1), outputs=note)
    return json.loads(text), usage


def messages_for(req: dict) -> list:
    content = [{"type": "input_text", "text": req["user"]}]
    for png in req["pngs"]:
        content.append({"type": "input_image", "image_url": "data:image/png;base64," + base64.b64encode(png).decode()})
    return [{"role": "system", "content": req["system"]}, {"role": "user", "content": content}]


# ---------------------------------------------------------------- the whole house
def cache_key(side: dict, seed: int, model: str, req: dict) -> str:
    h = hashlib.sha1(json.dumps([PROMPT_VERSION, model, EFFORT, seed, req["system"], req["user"]]).encode()).hexdigest()
    return f"{side['name']}_s{seed}_v{PROMPT_VERSION}_{h[:12]}"


def decorate_ai(side: dict, lib: D.Library, con=None, seed: int = 1, brief: str = "", allowed=D.HOUSE_TYPES,
                density: float = 1.0, types: dict | None = None, cache_dir: Path | None = None,
                model: str = MODEL, offline: bool = False, call=None, caller: str = "GUO3",
                images: bool = True) -> tuple[list[dict], list[str], dict]:
    """The decor list for a built multi, planned by the model and placed by the decorator.
    Returns (decor, report, meta). `call(messages) -> (plan, usage)` replaces the API (tests)."""
    req = build_request(side, lib, con, brief, allowed, types, images)
    key = cache_key(side, seed, model, req)
    cached = (cache_dir / f"{key}.json") if cache_dir else None
    meta = {"key": key, "model": model, "calls": 0, "moved": [], "topped_up": {}, "cached": False, "errors_first": [], "errors_final": [],
            "fallback_rooms": [], "usage": []}
    record = None
    if cached and cached.exists():
        record = json.loads(cached.read_text(encoding="utf-8"))
        meta["cached"] = True
    all_plans = house_plans(side)
    offered = set(req["offered"])
    plan = None
    if record is None and not offline:
        call = call or (lambda msgs, note="": openai_call(msgs, model, caller, note=note))
        msgs = messages_for(req)
        record = {"key": key, "model": model, "prompt_version": PROMPT_VERSION, "plans": [], "usage": []}
        try:
            first, usage = call(msgs, note=f"{side['name']} seed {seed}")
            meta["calls"] += 1
            record["plans"].append(first)
            record["usage"].append(usage)
            _ids, errs = apply(first, all_plans, lib, allowed, offered, req["forced"])
            if errs:
                msgs = msgs + [{"role": "assistant", "content": json.dumps(first)},
                               {"role": "user", "content": "These placements broke the rules and were refused:\n- "
                                + "\n- ".join(errs) + "\nReturn the whole plan again, corrected."}]
                second, usage = call(msgs, note=f"{side['name']} seed {seed} retry")
                meta["calls"] += 1
                record["plans"].append(second)
                record["usage"].append(usage)
        except (RuntimeError, OSError, ValueError) as e:
            record["error"] = str(e)
        if cached and record["plans"]:
            cached.parent.mkdir(parents=True, exist_ok=True)
            cached.write_text(json.dumps(record, indent=1), encoding="utf-8")
    if record and record.get("plans"):
        _ids, meta["errors_first"] = apply(record["plans"][0], all_plans, lib, allowed, offered, req["forced"])
        plan = record["plans"][-1]
        meta["usage"] = record.get("usage", [])
    ids, errs = apply(plan or {"rooms": []}, all_plans, lib, allowed, offered, req["forced"], snap=True,
                      moved=meta["moved"])
    meta["errors_final"] = errs if plan else ["no plan: " + (record or {}).get("error", "offline, nothing cached")]
    # the fallback: a room with no type, or without its defining piece, is furnished by the rules
    rng = random.Random(f"{seed}:{side['name']}:rules")
    used = collections.Counter(rp.type for rp in ids.values() if rp.type)
    forced = req["forced"]
    for rid, rp in ids.items():
        if rp.type and has_anchor(rp):
            n, st, _plans, walls = all_plans[rp.storey]
            k = top_up(rng, lib, rp, walls, set(map(tuple, st["windows"])), set(map(tuple, st["doors"])))
            if k:
                meta["topped_up"][rid] = k
            continue
        meta["fallback_rooms"].append(rid)
        n, st, _plans, walls = all_plans[rp.storey]
        windows, doors = set(map(tuple, st["windows"])), set(map(tuple, st["doors"]))
        rp.placed, rp.occupied, rp.wall_used = [], set(), set()
        rp.type = forced.get(rid) or rp.type or D.pick_type(rng, lib, len(rp.room.cells), rp.storey > 0, used, allowed)
        D.furnish_room(rng, lib, rp, walls, windows, doors, density)
    decor, report = [], []
    reasons = {r.get("room"): r.get("reason", "") for r in (plan or {}).get("rooms", [])}
    for rid, rp in ids.items():
        items = 0
        for tp, x, y in rp.placed:
            for item, dx, dy, z in tp.items:
                decor.append({"item": f"{item:#06x}", "at": [x + dx, y + dy], "z": z, "storey": rp.storey})
                items += 1
        names = collections.Counter(lib.names.get(it[0], "?") for tp, _x, _y in rp.placed for it in tp.items)
        how = "rules" if rid in meta["fallback_rooms"] else (
            f"plan + {meta['topped_up'][rid]} by the rules" if rid in meta["topped_up"] else "plan")
        report.append(f"{rid} ({len(rp.room.cells)} cells): {rp.type} by {how}, {len(rp.placed)} groups, {items} items: "
                      + ", ".join(f"{k} x{v}" for k, v in names.most_common(6))
                      + (f" -- {reasons[rid]}" if reasons.get(rid) and how == "plan" else ""))
    return decor, report, meta
