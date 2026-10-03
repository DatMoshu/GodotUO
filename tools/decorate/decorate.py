"""Furnish a built multi from what the decor database learned.

Input: a built multi's sidecar (build/multi/built/<name>/multi.json): its
`local.storeys` (walls, doors, windows, floor, open, arrivals per storey) and
`local.stairs`, all in description coordinates. Output: generate.py's `decor`
list, `[{"item", "at": [x, y], "z", "storey"}]`, with z above that storey's floor.

Per storey: rooms are segmented as the miner segments UO's buildings. Each room
gets a type, drawn from the learned room types (weighted by how often that type
appears on that storey, how well the room's area fits it, and what the house
already has), then learned furniture groups (templates) are placed whole, as UO
placed them: against the same wall sides (so directional art faces into the
room), stacked items and all. A placement is refused when it covers a cell kept
clear (in front of a door, at a stair's foot, a landing, an arrival, before a
window), touches another group, or leaves any free cell of the room unreachable
from its doors and stairs.

Deterministic: the only randomness is random.Random seeded from (seed, name).
"""
from __future__ import annotations

import collections
import json
import math
import random
import sys
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import classify  # noqa: E402
import rooms as R  # noqa: E402

# Room types a house is furnished as, and the kinds that make each one (placed first).
HOUSE_TYPES = ("bedroom", "library", "parlour", "dining-hall", "bakery-kitchen", "kitchen", "storage", "tailor",
               "carpenter", "smithy", "shop", "tavern-hall", "alchemist")
ANCHORS = {"bedroom": {"bed"}, "library": {"bookcase"}, "smithy": {"forge", "anvil"}, "bakery-kitchen": {"oven"},
           "tailor": {"loom"}, "shop": {"counter", "display"}, "dining-hall": {"table"},
           "tavern-hall": {"table"}, "kitchen": {"cookware", "fireplace"}, "storage": {"container", "chest"},
           "carpenter": {"woodwork"}, "alchemist": {"alchemy"}, "parlour": {"chair", "table", "fireplace"}}
# Kinds that never go into a house by default (gore, rugs that need a whole floor, garden flora).
NEVER = {"remains", "rug", "flora"}
# Small things that suit any room: on walls and in corners.
GENERIC = {"light", "plant", "art", "chest", "container"}
# What else belongs in a room of each type, beyond the kinds that classify it.
EXTRA_KINDS = {"bedroom": {"table", "chair", "book", "cloth", "bath", "drink"},
               "library": {"chair", "map", "light"}, "parlour": {"music", "book", "drink"},
               "dining-hall": {"drink", "food", "cookware", "light"}, "kitchen": {"table", "chair", "drink", "container"},
               "bakery-kitchen": {"table", "counter", "container"}, "storage": {"farm", "food", "tools"},
               "tailor": {"table", "chair", "display"}, "carpenter": {"table", "container"},
               "smithy": {"container", "table"}, "shop": {"table", "chair", "book"},
               "tavern-hall": {"cookware", "counter", "light"}, "alchemist": {"table", "book", "display"}}
DOMESTIC = {"bedroom", "library", "parlour", "dining-hall", "kitchen", "bakery-kitchen", "storage"}
GENERIC_SHARE = 0.3             # of a type's template weight, at most, for generic pieces from other rooms
MAX_TEMPLATE = 6            # cells across, either way
# Kinds that stand with their back to a wall: never placed free in a room.
WALL_KINDS = {"bed", "bookcase", "chest", "counter", "loom", "oven", "forge", "fireplace", "display"}
# Kinds that hang on a wall cell; anything else found on one (a pot plant) was standing in a gap.
HUNG_KINDS = {"art", "light", "weapons", "map", "display"}


@dataclass
class Template:
    id: int
    items: list                 # [[item, dx, dy, z_above], ...]
    w: int
    h: int
    cells: frozenset
    against: str
    on_wall: bool
    kinds: dict
    uses: int
    types: dict


@dataclass
class Library:
    templates: list[Template]
    room_types: dict            # type -> {rooms, ground, upper, mean_area, density}
    names: dict = field(default_factory=dict)

    @classmethod
    def from_db(cls, con, has_art=None, facings: dict | None = None) -> "Library":
        """`facings`: {item: set of wall sides its art's back may stand or hang against}, for art
        that only faces some ways (load_facings). A template whose wall side one of its items
        cannot take is dropped, so no piece is ever placed facing a wall."""
        tps = []
        for r in con.execute("SELECT id, items, w, h, against, on_wall, kinds, uses, room_types FROM template "
                             "ORDER BY id"):
            items = json.loads(r[1])
            kinds = json.loads(r[6])
            if set(kinds) & NEVER or set(kinds) <= {"misc"}:
                continue
            if r[2] > MAX_TEMPLATE or r[3] > MAX_TEMPLATE:
                continue
            if any(not -2 <= it[3] <= 30 for it in items):
                continue
            if has_art and not all(has_art(it[0]) for it in items):
                continue
            if r[5] and not (set(kinds) <= HUNG_KINDS or min(it[3] for it in items) >= 5):
                continue
            if not r[5] and not r[4] and set(kinds) & WALL_KINDS:
                continue
            if facings and r[4] and not all(it[0] not in facings or facings[it[0]] is None
                                            or facings[it[0]] & set(r[4]) for it in items):
                continue
            tps.append(Template(r[0], items, r[2], r[3], frozenset((it[1], it[2]) for it in items), r[4],
                                bool(r[5]), kinds, r[7], json.loads(r[8])))
        names = {r[0]: r[1] for r in con.execute("SELECT item, name FROM item")}
        tps = complete(tps, names)
        types = {}
        for r in con.execute("SELECT type, rooms, ground, upper, mean_area, density FROM room_type"):
            types[r[0]] = {"rooms": r[1], "ground": r[2], "upper": r[3], "mean_area": r[4], "density": r[5]}
        return cls(tps, types, names)

    def relevant(self, rtype: str) -> set[str]:
        rule = next((w for name, w, _m in classify.ROOM_RULES if name == rtype), {})
        return set(rule) | GENERIC | EXTRA_KINDS.get(rtype, set())

    def for_type(self, rtype: str) -> list[tuple[Template, float]]:
        """Templates for a room type: those seen in rooms of that type whose kinds belong in
        it, then generic pieces (lights, plants, art, chests) seen anywhere, capped to a share."""
        ok = self.relevant(rtype)
        typed, generic = [], []
        for t in self.templates:
            kinds = set(t.kinds) - {"misc"}
            if not kinds <= ok:
                continue
            n = t.types.get(rtype, 0)
            if n:
                typed.append((t, float(n)))
            elif kinds <= GENERIC and sum(t.types.values()) >= 2:
                generic.append((t, float(sum(t.types.values()))))
        tsum, gsum = sum(w for _, w in typed), sum(w for _, w in generic)
        if gsum and tsum:
            k = min(1.0, GENERIC_SHARE * tsum / gsum)
            generic = [(t, w * k) for t, w in generic]
        return typed + generic


def _partnered(tp: Template, names: dict) -> dict[int, bool]:
    """Per piece of a template: does a piece of the same name stand beside it (the other
    half of a bed, the next section of a table)?"""
    at = {}
    for it in tp.items:
        at.setdefault((it[1], it[2]), []).append(it[0])
    out = {}
    for item, dx, dy, _z in tp.items:
        nm = names.get(item, "")
        out[item] = out.get(item, False) or any(
            names.get(o, "") == nm for _d, ex, ey in R.DIRS for o in at.get((dx + ex, dy + ey), ()))
    return out


def complete(tps: list[Template], names: dict) -> list[Template]:
    """Drop templates holding part of a multi-tile piece without the rest. An item is a
    part when, over every use, it mostly stands beside a piece of its own name."""
    seen, paired = collections.Counter(), collections.Counter()
    for tp in tps:
        for item, ok in _partnered(tp, names).items():
            seen[item] += tp.uses
            paired[item] += tp.uses * ok
    part = {i for i in seen if paired[i] >= 0.6 * seen[i]}
    return [tp for tp in tps if all(ok or item not in part for item, ok in _partnered(tp, names).items())]


def weighted(rng: random.Random, pairs: list):
    total = sum(w for _, w in pairs)
    if total <= 0:
        return None
    r = rng.random() * total
    for v, w in pairs:
        r -= w
        if r <= 0:
            return v
    return pairs[-1][0]


def pick_type(rng: random.Random, lib: Library, area: int, upper: bool, used: collections.Counter,
              allowed=HOUSE_TYPES) -> str:
    pairs = []
    for t in allowed:
        st = lib.room_types.get(t)
        if not st or not lib.for_type(t):
            continue
        prior = (st["upper"] + 0.3 * st["ground"]) if upper else (st["ground"] + 0.1 * st["upper"])
        fit = math.exp(-(math.log(max(area, 1) / max(st["mean_area"], 1)) ** 2) / (2 * 0.9 ** 2))
        repeat = 0.5 if t == "bedroom" and used[t] < 2 else 0.15
        w = math.sqrt(max(prior, 0.5)) * fit * (repeat ** used[t])
        if t not in DOMESTIC:
            w *= 0.3                     # a house is a home first
        if upper and t in ("shop", "smithy", "tavern-hall", "bakery-kitchen", "carpenter"):
            w *= 0.1                     # work rooms stay on the ground floor
        if not upper and t == "bedroom" and area > 40:
            w *= 0.5
        pairs.append((t, w))
    return weighted(rng, pairs) or "parlour"


@dataclass
class RoomPlan:
    storey: int
    z: int
    room: R.Room
    portals: list                    # cells inside the room that must stay reachable (door fronts, stair feet)
    clear: set                       # cells nothing may stand on
    type: str = ""
    placed: list = field(default_factory=list)       # (template, x, y)
    occupied: set = field(default_factory=set)
    wall_used: set = field(default_factory=set)


def plan_storey(n: int, st: dict, stairs: list) -> tuple[list[RoomPlan], set]:
    """The rooms of one storey and what in each must stay clear."""
    walls = set(map(tuple, st["walls"]))
    doors = set(map(tuple, st["doors"]))
    windows = set(map(tuple, st["windows"]))
    floor = set(map(tuple, st["floor"]))
    outside = set(map(tuple, st.get("open", [])))
    stair_cells, feet = set(), set()
    for s in stairs:
        if s["z"] == st["z"]:
            stair_cells |= set(map(tuple, s["cells"]))
            feet.add(tuple(s["foot"]))
    arrivals = set(map(tuple, st.get("arrivals", [])))
    rooms = R.segment(floor - outside, walls, doors=doors, windows=windows, blocked=stair_cells | outside,
                      min_cells=3)
    plans = []
    for r in rooms:
        portals, clear = [], set()
        for (c, d) in r.doors:
            dx, dy = R.STEP[d]
            front = (c[0] - dx, c[1] - dy)
            portals.append(front)
            # the door's swing and the first step in: a 3 x 2 block inside the door
            px, py = (1, 0) if d in "NS" else (0, 1)
            for k in (-1, 0, 1):
                for depth in (1, 2):
                    clear.add((c[0] - dx * depth + px * k, c[1] - dy * depth + py * k))
        for (c, d) in r.windows:
            dx, dy = R.STEP[d]
            clear.add((c[0] - dx, c[1] - dy))
        for f in feet | arrivals:
            if f in r.cells:
                portals.append(f)
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    clear.add((f[0] + dx, f[1] + dy))
        for c in stair_cells:
            # a stair's sides stay free, so the flight reads and nothing stands on a landing's edge
            for _d, dx, dy in R.DIRS:
                clear.add((c[0] + dx, c[1] + dy))
        clear &= r.cells
        plans.append(RoomPlan(n, st["z"], r, sorted(set(portals) & r.cells), clear))
    return plans, walls


def fits(tp: Template, x: int, y: int, rp: RoomPlan, walls: set) -> bool:
    cells = {(x + dx, y + dy) for dx, dy in tp.cells}
    room = rp.room.cells
    if not cells <= room or cells & rp.clear or cells & rp.occupied:
        return False
    for (cx, cy) in cells:                         # keep groups apart, as the originals' are
        for _d, dx, dy in R.DIRS:
            n = (cx + dx, cy + dy)
            if n in rp.occupied and n not in cells:
                return False
    xs0, ys0 = x, y
    xs1, ys1 = x + tp.w - 1, y + tp.h - 1
    for d, dx, dy in R.DIRS:
        edge = [c for c in cells if (d == "N" and c[1] == ys0) or (d == "S" and c[1] == ys1)
                or (d == "W" and c[0] == xs0) or (d == "E" and c[0] == xs1)]
        touching = sum((c[0] + dx, c[1] + dy) in walls for c in edge) * 2 >= len(edge)
        if (d in tp.against) != touching:
            return False
    return True


def keeps_paths(rp: RoomPlan, extra: set) -> bool:
    """Every free cell of the room is still reachable from the first portal, and every
    portal from every other."""
    free = rp.room.cells - rp.occupied - extra
    if not rp.portals:
        return True
    start = rp.portals[0]
    seen = R.reachable(start, free)
    return len(seen) == len(free) and all(p in seen for p in rp.portals)


def wall_spots(tp: Template, rp: RoomPlan, walls: set, windows: set, doors: set) -> list:
    """Wall cells a wall-hung template can go on: a plain wall on the room's `against` side."""
    d = tp.against[:1]
    if d not in R.STEP:
        return []
    dx, dy = R.STEP[d]
    out = []
    for (x, y) in sorted(rp.room.cells):
        w = (x + dx, y + dy)
        if w in walls and w not in windows and w not in doors and w not in rp.wall_used:
            out.append(w)
    return out


def furnish_room(rng: random.Random, lib: Library, rp: RoomPlan, walls: set, windows: set, doors: set,
                 density_scale: float = 1.0) -> None:
    cands = lib.for_type(rp.type)
    if not cands:
        return
    area = len(rp.room.cells)
    st = lib.room_types.get(rp.type, {})
    target = max(2, round(area * min(0.45, max(0.12, st.get("density", 0.2)) * density_scale)))
    anchors = ANCHORS.get(rp.type, set())
    anchor_cands = [(t, w) for t, w in cands if set(t.kinds) & anchors and not t.on_wall]
    floor_cands = [(t, w) for t, w in cands if not t.on_wall]
    wall_cands = [(t, w) for t, w in cands if t.on_wall]
    covered = 0
    wanted_anchors = 1 + (area >= 36)
    tries = 0
    max_groups = max(2, area // 6)
    while tries < 80 and covered < target and len(rp.placed) < max_groups:
        tries += 1
        if wanted_anchors > 0 and anchor_cands:
            pool = anchor_cands
        elif wall_cands and rng.random() < 0.2:
            pool = wall_cands
        else:
            pool = floor_cands
        tp = weighted(rng, pool)
        if tp is None:
            break
        if tp.on_wall:
            spots = wall_spots(tp, rp, walls, windows, doors)
            if spots:
                w = rng.choice(spots)
                rp.wall_used.add(w)
                rp.placed.append((tp, w[0], w[1]))
            continue
        x0, y0, x1, y1 = rp.room.bbox
        spots = [(x, y) for y in range(y0, y1 - tp.h + 2) for x in range(x0, x1 - tp.w + 2)
                 if fits(tp, x, y, rp, walls)]
        rng.shuffle(spots)
        for (x, y) in spots[:24]:
            cells = {(x + dx, y + dy) for dx, dy in tp.cells}
            if keeps_paths(rp, cells):
                rp.occupied |= cells
                rp.placed.append((tp, x, y))
                covered += len(cells)
                if set(tp.kinds) & anchors:
                    wanted_anchors -= 1
                break
        else:
            if pool is anchor_cands:
                wanted_anchors -= 0.34            # give up on anchors after a few misses


def decorate(side: dict, lib: Library, seed: int = 1, allowed=HOUSE_TYPES, density: float = 1.0,
             types: dict | None = None) -> tuple[list[dict], list[str]]:
    """The decor list for a built multi's sidecar, and a report (one line per room).
    `types` forces room types: {"<storey>:<x>,<y>": type} for the room holding that cell."""
    local = side["local"]
    rng = random.Random(f"{seed}:{side['name']}")
    used = collections.Counter()
    decor, report = [], []
    all_plans = []
    for n, st in enumerate(local["storeys"]):
        plans, walls = plan_storey(n, st, local.get("stairs", []))
        all_plans.append((n, st, plans, walls))
    # the biggest rooms choose first, so the defining rooms of a house get the room they need
    order = sorted(((n, i) for n, _st, plans, _w in all_plans for i in range(len(plans))),
                   key=lambda ni: (-len(all_plans[ni[0]][2][ni[1]].room.cells), ni))
    for n, i in order:
        rp = all_plans[n][2][i]
        forced = None
        for key, t in (types or {}).items():
            sn, xy = key.split(":")
            if int(sn) == n and tuple(int(v) for v in xy.split(",")) in rp.room.cells:
                forced = t
        rp.type = forced or pick_type(rng, lib, len(rp.room.cells), n > 0, used, allowed)
        used[rp.type] += 1
    for n, st, plans, walls in all_plans:
        windows = set(map(tuple, st["windows"]))
        doors = set(map(tuple, st["doors"]))
        for rp in plans:
            furnish_room(rng, lib, rp, walls, windows, doors, density)
            tried = {rp.type}
            while ANCHORS.get(rp.type) and not any(set(tp.kinds) & ANCHORS[rp.type] for tp, _x, _y in rp.placed)                     and len(tried) < 4:
                # its defining piece did not fit: try the room as something else
                used[rp.type] -= 1
                rp.type = pick_type(rng, lib, len(rp.room.cells), n > 0, used,
                                    [t for t in allowed if t not in tried])
                tried.add(rp.type)
                used[rp.type] += 1
                rp.placed, rp.occupied, rp.wall_used = [], set(), set()
                furnish_room(rng, lib, rp, walls, windows, doors, density)
            items = 0
            for tp, x, y in rp.placed:
                for item, dx, dy, z in tp.items:
                    decor.append({"item": f"{item:#06x}", "at": [x + dx, y + dy], "z": z, "storey": n})
                    items += 1
            x0, y0, x1, y1 = rp.room.bbox
            names = collections.Counter(lib.names.get(it[0], "?") for tp, _x, _y in rp.placed for it in tp.items)
            report.append(f"storey {n} room {x0},{y0}-{x1},{y1} ({len(rp.room.cells)} cells, {len(rp.room.doors)} doors,"
                          f" {len(rp.portals)} portals): {rp.type}, {len(rp.placed)} groups, {items} items: "
                          + ", ".join(f"{k} x{v}" for k, v in names.most_common(8)))
    return decor, report


def load_facings(path) -> dict:
    """A facings file: {"items": {"0x0a2c": ["N"], "0x0b34": null, ...}}, the wall sides each
    item's art may have its back to (null: any, the art is symmetric). Items not listed are free."""
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    return {int(k, 16): (set(v) if v else None) for k, v in raw.get("items", raw).items() if not k.startswith("_")}


def wrong_facing(side: dict, decor: list[dict], facings: dict) -> list[str]:
    """Pieces whose art faces a wall: a standing piece beside walls on none of its back sides,
    or a hung one on a wall its back cannot go against."""
    out = []
    for n, st in enumerate(side["local"]["storeys"]):
        walls = set(map(tuple, st["walls"]))
        floor = set(map(tuple, st["floor"])) - walls
        for d in decor:
            if d.get("storey") != n:
                continue
            backs = facings.get(int(d["item"], 16))
            if not backs:
                continue
            x, y = d["at"]
            if (x, y) in walls:            # hung: its back is the wall, the room on the other side
                sides = {R.OPPOSITE[k] for k, dx, dy in R.DIRS if (x + dx, y + dy) in floor}
            else:
                sides = {k for k, dx, dy in R.DIRS if (x + dx, y + dy) in walls}
            if sides and not sides & backs:
                out.append(f"storey {n}: {d['item']} at {[x, y]} faces a wall (its back goes {''.join(sorted(backs))},"
                           f" the wall is {''.join(sorted(sides))})")
    return out


def check(side: dict, decor: list[dict], facings: dict | None = None) -> list[str]:
    """What a decor list must never do: stand on a door, a stair, a landing, a window
    or an arrival, or cut a storey's doors and stairs apart; with `facings`, face a wall.
    [] when it is clean."""
    local = side["local"]
    problems = []
    for n, st in enumerate(local["storeys"]):
        mine = {tuple(d["at"]) for d in decor if d.get("storey") == n}
        walls = set(map(tuple, st["walls"]))
        doors = set(map(tuple, st["doors"]))
        windows = set(map(tuple, st["windows"]))
        stairs = set()
        for s in local.get("stairs", []):
            if s["z"] == st["z"]:
                stairs |= set(map(tuple, s["cells"])) | {tuple(s["foot"])}
        arrivals = set(map(tuple, st.get("arrivals", [])))
        for bad, what in ((doors, "a door"), (stairs, "a stair"), (arrivals, "an arrival")):
            for c in sorted(mine & bad):
                problems.append(f"storey {n}: an item stands on {what} at {list(c)}")
        on_windows = {tuple(d["at"]) for d in decor if d.get("storey") == n} & windows
        for c in sorted(on_windows):
            problems.append(f"storey {n}: an item hangs on a window at {list(c)}")
        floor = set(map(tuple, st["floor"])) | doors
        free = floor - walls - stairs - (mine - walls)
        free |= doors
        targets = [c for c in sorted(doors | arrivals) if c in free]
        for s in local.get("stairs", []):
            if s["z"] == st["z"] and tuple(s["foot"]) in floor:
                targets.append(tuple(s["foot"]))
                free.add(tuple(s["foot"]))
        if targets:
            seen = R.reachable(targets[0], free)
            for t in targets[1:]:
                if t not in seen:
                    problems.append(f"storey {n}: {list(t)} is cut off from {list(targets[0])}")
    return problems + (wrong_facing(side, decor, facings) if facings else [])


def plan_image(side: dict, decor: list[dict], n: int, png: Path, cell: int = 14) -> None:
    """Top-down plan of one storey: walls, windows, doors, stairs, arrivals, and the
    cells the decor stands on (green) or hangs on (light green)."""
    from PIL import Image, ImageDraw
    st = side["local"]["storeys"][n]
    layers = [("floor", (110, 80, 50)), ("open", (70, 90, 60)), ("walls", (170, 170, 170)),
              ("windows", (90, 160, 230)), ("doors", (230, 160, 40)), ("arrivals", (255, 80, 255))]
    cells = {}
    for key, col in layers:
        for c in st.get(key, []):
            cells[tuple(c)] = col
    for s in side["local"].get("stairs", []):
        if s["z"] == st["z"]:
            for c in s["cells"]:
                cells[tuple(c)] = (200, 60, 200)
            cells[tuple(s["foot"])] = (255, 140, 255)
    for d in decor:
        if d.get("storey") == n:
            c = tuple(d["at"])
            cells[c] = (150, 230, 150) if cells.get(c) == (170, 170, 170) else (40, 170, 70)
    if not cells:
        return
    xs, ys = [c[0] for c in cells], [c[1] for c in cells]
    x0, y0 = min(xs), min(ys)
    img = Image.new("RGB", ((max(xs) - x0 + 1) * cell, (max(ys) - y0 + 1) * cell), (20, 20, 20))
    d = ImageDraw.Draw(img)
    for (x, y), col in cells.items():
        px, py = (x - x0) * cell, (y - y0) * cell
        d.rectangle([px, py, px + cell - 2, py + cell - 2], fill=col)
    png.parent.mkdir(parents=True, exist_ok=True)
    img.save(png)
