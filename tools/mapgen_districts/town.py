"""GUO's own towns for generated maps: streets, lots and houses from tools/multi's house generator.

A town is a 72x72 district in the shape compose.py lays on a map's town lot (the shape
tools/layout_import writes), so the two kinds of district are interchangeable there. Nothing here
reads another game's layouts: the streets and lots are drawn by rule, every house is kit.house
from the committed styles (tools/multi/styles), and the land tiles are MapGen's own tables. No
client data is needed to plan or build a town; the offline walk check uses it when it is there.

    plan(preset, seed, name)   the town as data (guo.mapgen.town/1): streets, plaza, lots, and each
                               lot's house parameters and placement, fitted and checked
    build(plan, out, cfg)      the district folder: district.json, world/, parts/, scene.json

The same preset, seed and committed styles give the same plan and the same files (project.json's
`created` aside). data_formats section 26 is the contract.
"""
from __future__ import annotations

import collections
import json
import random
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE.parent / "multi"))

import fort  # noqa: E402
import generate  # noqa: E402
import gen_cli  # noqa: E402
import kit  # noqa: E402
import validate  # noqa: E402
from multifile import Component  # noqa: E402

SCHEMA = "guo.mapgen.town/1"
CATALOGUE = HERE / "towns.json"
SIZE = 72                       # compose.DISTRICT
STREET = 3                      # street width in cells
MAIN = 34                       # the main streets' first row and column
PLAZA = (29, 29, 41, 41)        # the paved square round the crossing, inclusive
HALVES = ((0, MAIN - 1), (MAIN + STREET, SIZE - 1))
REACH = fort.REACH
WAYPOINT = 6                    # cells between outdoor tour stops

# MapGen's own land tables (tools/mapgen/MapGen/presets/tile-tables.default.json, RoadPaint.cs)
LAND = {"grass": [0x03, 0x04, 0x05, 0x06], "road": [0x3E9, 0x3EA, 0x3EB, 0x3EC],
        "dirt": [0x71, 0x72, 0x73, 0x74]}
THEME = {"format": 1, "name": "guo-town", "version": 1, "floor_z": 7, "storey_height": 20, "wall_height": 19,
         "materials": "tools/multi styles, per house"}


class TownError(ValueError):
    pass


def load_presets(path: Path = CATALOGUE) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    if data.get("schema") != "guo.mapgen.towns/1":
        raise TownError(f"{path.name}: schema is not guo.mapgen.towns/1")
    return data["presets"]


def pick(rng: random.Random, weights: dict):
    keys = sorted(weights)
    return rng.choices(keys, [weights[k] for k in keys])[0]


def rect_cells(r) -> set[tuple[int, int]]:
    x0, y0, x1, y1 = r
    return {(x, y) for x in range(x0, x1 + 1) for y in range(y0, y1 + 1)}


def layout(rng: random.Random, preset: dict) -> tuple[list[dict], list[tuple[int, int, int, int]]]:
    """Streets and bands. Every house faces south onto a street or the plaza: a band is the rows
    above one, and the main cross, the bottom street and the side streets the seed keeps make them."""
    streets = [{"name": "main_ns", "rect": [MAIN, 0, MAIN + STREET - 1, SIZE - 1]},
               {"name": "main_ew", "rect": [0, MAIN, SIZE - 1, MAIN + STREET - 1]},
               {"name": "south", "rect": [0, SIZE - STREET, SIZE - 1, SIZE - 1]}]
    north = rng.random() < preset.get("side_streets", 0.5)
    south = rng.random() < preset.get("side_streets", 0.5)
    bands = []
    if north:
        s = rng.choice((14, 15, 16))
        streets.append({"name": "side_north", "rect": [0, s, SIZE - 1, s + STREET - 1]})
        bands += [(0, s - 1), (s + STREET, MAIN - 1)]
    else:
        bands.append((0, MAIN - 1))
    if south:
        t = rng.choice((50, 51, 52))
        streets.append({"name": "side_south", "rect": [0, t, SIZE - 1, t + STREET - 1]})
        bands += [(MAIN + STREET, t - 1), (t + STREET, SIZE - STREET - 1)]
    else:
        bands.append((MAIN + STREET, SIZE - STREET - 1))
    return streets, bands


def house_params(rng: random.Random, preset: dict, lot_w: int, seed: int) -> dict:
    storeys = int(pick(rng, preset["storeys"]))
    w0, w1 = preset["house_width"]
    d0, d1 = preset["house_depth"]
    width = rng.randint(w0, min(w1, max(w0, lot_w - 2)))
    p = {"style": pick(rng, preset["styles"]), "seed": seed, "shape": pick(rng, preset["shapes"]),
         "width": width, "depth": rng.randint(d0, d1), "storeys": storeys, "roof": pick(rng, preset["roofs"]),
         "rooms": rng.randint(1, 3) + (storeys > 1), "porch": True, "yard": rng.random() < preset.get("yard", 0),
         "balcony": storeys > 1 and rng.random() < preset.get("balcony", 0)}
    if p["shape"] != "rect" and width < 10:
        p["shape"] = "rect"
    return p


def make_house(ctx: gen_cli.Context, p: dict):
    """kit.house's expansion, keeping the generator's side record (stairs for the tour)."""
    cat = ctx.catalogue()
    desc, notes = kit.house_description(cat, p)
    comps, side = generate.build(desc, cat)
    problems = validate.validate(comps, side, None)
    return comps, side, problems


def shrink(p: dict, k: int) -> dict:
    """The k-th fallback for a house that does not fit or does not validate: smaller, then plainer."""
    q = dict(p)
    q["width"] = max(7, p["width"] - k)
    q["depth"] = max(6, p["depth"] - (k + 1) // 2)
    if k >= 2:
        q["yard"] = False
    if k >= 3:
        q["shape"] = "rect"
    if k >= 4:
        q["storeys"], q["balcony"] = 1, False
    return q


def fit(comps, side, lot: list[int], taken: set) -> tuple[int, int] | None:
    """The centre that stands the house in its lot with its front on the lot's south edge; None if
    it spills out of the lot, reaches too far for one multi, or its arrival stop is not free."""
    x0, y0, x1, y1 = lot
    xs, ys = [c.x for c in comps], [c.y for c in comps]
    if max(map(abs, xs + ys)) > REACH:
        return None
    cx = (x0 + x1) // 2 - (min(xs) + max(xs)) // 2
    cy = y1 - max(ys)
    if min(xs) + cx < x0 or max(xs) + cx > x1 or min(ys) + cy < y0:
        return None
    first = side["stops"][0] if side["stops"] else None
    if first is None:
        return None
    fx, fy = first["x"] + cx, first["y"] + cy
    if not (0 <= fx < SIZE and 0 <= fy < SIZE) or (fx, fy) in taken:
        return None
    return cx, cy


def plan(preset_name: str, seed: int, name: str | None = None, presets: dict | None = None,
         ctx: gen_cli.Context | None = None) -> dict:
    presets = presets or load_presets()
    if preset_name not in presets:
        raise TownError(f"no town preset '{preset_name}' (have {', '.join(sorted(presets))})")
    preset = presets[preset_name]
    ctx = ctx or gen_cli.Context(user=False)
    rng = random.Random(f"guo-town:{preset_name}:{seed}")
    streets, bands = layout(rng, preset)
    paved = set().union(*(rect_cells(s["rect"]) for s in streets)) | rect_cells(PLAZA)
    lots, taken, notes = [], set(), []
    for by0, by1 in bands:
        for hx0, hx1 in HALVES:
            x = hx0
            lw0, lw1 = preset["lot_width"]
            while x + lw0 - 1 <= hx1:
                w = rng.randint(lw0, lw1)
                if x + w - 1 > hx1 or hx1 - (x + w - 1) < lw0:
                    w = hx1 - x + 1          # the last lot of the run takes what is left
                lx0, lx1 = x, x + w - 1
                x = lx1 + 2                  # a one-cell alley between lots
                # the plaza bites the corners of the bands beside the crossing: a lot there gives up
                # the plaza's columns while it stays wide enough, else the plaza's rows
                ly0, ly1 = by0, by1
                if rect_cells([lx0, ly0, lx1, ly1]) & paved:
                    narrow = preset["house_width"][0] + 2
                    if lx1 >= PLAZA[0] > lx0 and PLAZA[0] - lx0 >= narrow:
                        lx1 = PLAZA[0] - 1
                    elif lx0 <= PLAZA[2] < lx1 and lx1 - PLAZA[2] >= narrow:
                        lx0 = PLAZA[2] + 1
                while ly0 <= ly1 and any((cx, ly0) in paved for cx in range(lx0, lx1 + 1)):
                    ly0 += 1
                while ly1 >= ly0 and any((cx, ly1) in paved for cx in range(lx0, lx1 + 1)):
                    ly1 -= 1
                lot = {"id": f"lot{len(lots) + 1}", "rect": [lx0, ly0, lx1, ly1]}
                lots.append(lot)
                hseed = rng.randrange(1, 1 << 30)
                if rng.random() >= preset.get("fill", 1.0) or ly1 - ly0 < 12:
                    lot["house"] = None
                    continue
                p0 = house_params(rng, preset, lx1 - lx0 + 1, hseed)
                p0["name"] = f"{name or preset_name}_{lot['id']}"
                for k in range(6):
                    p = shrink(p0, k)
                    try:
                        comps, side, problems = make_house(ctx, p)
                    except generate.DescriptionError as e:
                        notes.append(f"{lot['id']}: try {k}: {e}")
                        continue
                    if problems:
                        notes.append(f"{lot['id']}: try {k}: {problems[0]}")
                        continue
                    at = fit(comps, side, lot["rect"], taken)
                    if at is None:
                        continue
                    xs, ys = [c.x + at[0] for c in comps], [c.y + at[1] for c in comps]
                    box = rect_cells([min(xs), min(ys), max(xs), max(ys)])
                    # a front short of the street (a lot the plaza bit) gets a paved path down to it
                    fx, fy = side["stops"][0]["x"] + at[0], side["stops"][0]["y"] + at[1]
                    path = []
                    while (fx, fy) not in paved and fy < SIZE and (fx, fy) not in taken | box:
                        path.append([fx, fy])
                        fy += 1
                    if (fx, fy) not in paved:
                        continue
                    lot["house"] = {"params": p, "centre": list(at)}
                    if path:
                        lot["path"] = path
                    taken |= box
                    break
                else:
                    lot["house"] = None
                    notes.append(f"{lot['id']}: no house fits; left as a green")
    return {"schema": SCHEMA, "name": name or f"{preset_name}_{seed}", "preset": preset_name, "seed": seed,
            "size": [SIZE, SIZE], "streets": streets, "plaza": list(PLAZA), "lots": lots, "notes": notes}


# --- build -------------------------------------------------------------------------------------

def land_rows(plan_: dict) -> dict[tuple[int, int], int]:
    paved = set().union(*(rect_cells(s["rect"]) for s in plan_["streets"])) | rect_cells(plan_["plaza"])
    paved |= {tuple(c) for lot in plan_["lots"] for c in lot.get("path", [])}
    out = {}
    for y in range(SIZE):
        for x in range(SIZE):
            out[(x, y)] = generate.scatter(LAND["road"] if (x, y) in paved else LAND["grass"], x, y)
    return out


def route(a, b, blocked: set) -> list[tuple[int, int]] | None:
    """Shortest 4-connected walk between two district cells round the houses' bounds."""
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
        for nxt in ((cur[0] + 1, cur[1]), (cur[0] - 1, cur[1]), (cur[0], cur[1] + 1), (cur[0], cur[1] - 1)):
            if nxt in seen or not (0 <= nxt[0] < SIZE and 0 <= nxt[1] < SIZE) or (nxt in blocked and nxt != b):
                continue
            seen[nxt] = cur
            queue.append(nxt)
    return None


def build(plan_: dict, out: Path, cfg=None, ctx: gen_cli.Context | None = None, origin=(0, 0)) -> dict:
    """The plan's district folder at `out` (fresh). cfg (guo.load_config()) adds the install
    fingerprint to project.json and the offline walk check; without it both are skipped."""
    out = Path(out)
    if out.exists() and any(out.iterdir()):
        raise TownError(f"{out} is not empty: build writes a fresh folder")
    ctx = ctx or gen_cli.Context(user=False)
    ox, oy = origin
    if ox % 8 or oy % 8:
        raise TownError("origin must be on the 8x8 block grid")
    name = plan_["name"]
    parts, problems, tour, flights = [], [], [], []
    blocked: set = set()
    houses = []
    for lot in plan_["lots"]:
        h = lot.get("house")
        if not h:
            continue
        comps, side, probs = make_house(ctx, h["params"])
        problems += [f"{lot['id']}: {q}" for q in probs]
        cx, cy = h["centre"]
        xs, ys = [c.x + cx for c in comps], [c.y + cy for c in comps]
        bounds = [min(xs), min(ys), max(xs), max(ys)]
        x0, y0, x1, y1 = lot["rect"]
        if bounds[0] < x0 or bounds[1] < y0 or bounds[2] > x1 or bounds[3] > y1:
            problems.append(f"{lot['id']}: the house no longer fits its lot (styles changed since the plan?)")
        blocked |= rect_cells(bounds)
        part = {"name": lot["id"], "centre": [cx, cy], "bounds": bounds, "doors": side["doors"],
                "components": len(comps), "house": h["params"], "comps": comps}
        parts.append(part)
        c0 = side["centre"]
        fl = [{"foot": [s["foot"][0] - c0[0] + cx, s["foot"][1] - c0[1] + cy],
               "arrive": [s["arrive"][0] - c0[0] + cx, s["arrive"][1] - c0[1] + cy],
               "z_from": s["z"], "z_to": side["storeys"][s["to"]]} for s in side["local"]["stairs"]]
        flights += fl
        # a balcony door stands over the front door: asked for it, the pathfinder (x and y only) may
        # take the walker down to the ground floor. validate has already checked the balcony is reached
        stops = [dict(s, x=s["x"] + cx, y=s["y"] + cy) for s in side["stops"] if "balcony" not in s["name"]]
        houses.append((lot, part, stops, fl))
    # the tour: from the crossing, along the streets to each house in turn (bands north to south,
    # west to east), through it and up its stairs, and back out of its front
    here = (MAIN + 1, MAIN + 1)
    tour.append({"name": "crossing", "x": here[0], "y": here[1], "z": 0})
    for lot, part, stops, fl in houses:
        first = (stops[0]["x"], stops[0]["y"])
        way = route(here, first, blocked)
        if way is None:
            problems.append(f"{lot['id']}: no street walk reaches its front")
            continue
        for k in range(WAYPOINT, len(way) - 1, WAYPOINT):
            tour.append({"name": f"{lot['id']}_way{k}", "x": way[k][0], "y": way[k][1], "z": 0})
        inner = [dict(s, name=f"{lot['id']}_{s['name']}") for s in stops]
        inner.append(dict(stops[0], name=f"{lot['id']}_exit"))
        tour += fort.walk_tour(inner, fl)
        here = first
    # land: one world project of the district's 81 blocks
    land = land_rows(plan_)
    blocks = {}
    for by in range(SIZE // 8):
        for bx in range(SIZE // 8):
            rows = [" ".join(f"{land[(bx * 8 + x, by * 8 + y)]:04X}:0" for x in range(8)) for y in range(8)]
            blocks[(bx, by)] = {"format": 1, "facet": 0, "block": [bx + ox // 8, by + oy // 8], "land": rows,
                                "statics": []}
    if cfg is not None:
        problems += walk_check(cfg, parts, tour)
    out.mkdir(parents=True, exist_ok=True)
    (out / "parts").mkdir()
    for p in parts:
        (out / "parts" / f"{p['name']}.json").write_text(json.dumps([c.as_list() for c in p["comps"]]),
                                                        encoding="utf-8")
    bdir = out / "world" / "blocks" / "0"
    bdir.mkdir(parents=True)
    for (bx, by), blk in sorted(blocks.items()):
        (bdir / f"{blk['block'][0]}_{blk['block'][1]}.json").write_text(json.dumps(blk, indent=1) + "\n",
                                                                         encoding="utf-8")
    project = {"format": 1, "name": name, "created": "1970-01-01T00:00:00+00:00", "base": {}}
    if cfg is not None:
        from multi.storeys import install_fingerprint
        project["base"] = {"client_version": cfg.client_version, "fingerprint": install_fingerprint(cfg.client_data)}
    (out / "world" / "project.json").write_text(json.dumps(project, indent=1) + "\n", encoding="utf-8")
    (out / "plan.json").write_text(json.dumps(plan_, indent=1) + "\n", encoding="utf-8")
    scene = {"format": 1, "kind": "scene", "name": name, "bounds": [0, 0, SIZE - 1, SIZE - 1],
             "parts": [{k: v for k, v in p.items() if k not in ("comps", "house")} for p in parts], "tour": tour,
             "notes": ["GUO town: streets in the companion world project, one house multi per lot."],
             "valid": not problems, "problems": problems}
    (out / "scene.json").write_text(json.dumps(scene, indent=1) + "\n", encoding="utf-8")
    record = {"format": 1, "kind": "uo-district", "generator": "guo-town", "name": name, "origin": [ox, oy],
              "size": [SIZE, SIZE], "theme": THEME, "plan": {k: plan_[k] for k in ("schema", "preset", "seed")},
              "houses": len(parts), "land_library": {k: v for k, v in LAND.items()}, "parcels": [],
              "status": "native-valid" if not problems else "blocked", "problems": problems, "proof_status": "not-run"}
    (out / "district.json").write_text(json.dumps(record, indent=1) + "\n", encoding="utf-8")
    return record


def walk_check(cfg, parts: list[dict], tour: list[dict]) -> list[str]:
    """The offline walk (tools/multi walkcheck) when the install is there to give tile heights."""
    if not (cfg.client_data and Path(cfg.client_data).is_dir()):
        return []
    import walkcheck
    from layout_import import routing
    tiles = routing.tile_info([c for p in parts for c in p["comps"]], cfg.client_data)
    land_z = {(x, y): 0 for x in range(SIZE) for y in range(SIZE)}
    return walkcheck.check_tour(parts, tour, tiles, land_z)


def preview(cfg, built: Path, png: Path, max_z: int | None = None) -> Path | None:
    """An isometric preview of the town's houses (needs the install's art)."""
    import render
    every = []
    scene = json.loads((built / "scene.json").read_text(encoding="utf-8"))
    for p in scene["parts"]:
        cx, cy = p["centre"]
        for c in json.loads((built / "parts" / f"{p['name']}.json").read_text(encoding="utf-8")):
            if len(c) < 5:
                every.append(Component(c[0], c[1] + cx, c[2] + cy, c[3], True))
    if not every:
        return None
    render.render(every, cfg.client_data, png, max_z=max_z) if max_z is not None else render.render(every, cfg.client_data, png)
    return png
