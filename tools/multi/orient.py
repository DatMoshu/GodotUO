"""Rotate and mirror a multi, remapping the pieces whose art faces a direction.

Positions. A wall piece does not fill its cell: an EW piece draws the south edge of its cell (the line
y + 1, from x to x + 1), an NS piece the east edge (the line x + 1), a post stands on a vertex. So a
turned or mirrored building cannot move cells alone: a wall that drew a cell's east edge draws, once
mirrored, a west edge, which is the east edge of the next cell. The transform therefore works on edges
and vertices of the grid, and puts each piece in the cell whose edge it lands on. A corner piece that
draws both faces (the front corner the originals use) becomes its two straight runs.

Ids. The remap table is built from a mined catalogue (the families a piece belongs to, by neighbour
signature, roof side, stair ascent), from a style set (its roles), and from tiledata names ("rug east").
Door ids follow ModernUO's door layout: 16 ids from a base, closed/open pairs for 8 facings.

  build_table(...)            -> table (JSON: guo.multi.orient/1)
  table_from_styles(styles)   -> table
  transform(comps, op, table) -> comps; op is rot90 (clockwise seen from above), rot180, rot270,
                                 mirror_x (east becomes west) or mirror_y (north becomes south)
"""
from __future__ import annotations

import re
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from generate import faces  # noqa: E402
from multifile import Component  # noqa: E402

SCHEMA = "guo.multi.orient/1"
OPS = ("rot90", "rot180", "rot270", "mirror_x", "mirror_y")

# a letter's image under each base transform (x east, y south; rot90 turns clockwise seen from above)
LETTER = {
    "rot90": {"N": "E", "E": "S", "S": "W", "W": "N"},
    "mirror_x": {"N": "N", "E": "W", "S": "S", "W": "E"},
    "mirror_y": {"N": "S", "E": "E", "S": "N", "W": "W"},
}
# ModernUO DoorFacing order: WestCW EastCCW WestCCW EastCW (a wall along x), SouthCW NorthCCW SouthCCW NorthCW
DOOR_PERM = {
    "rot90": {0: 7, 1: 6, 2: 5, 3: 4, 4: 0, 5: 1, 6: 2, 7: 3},
    "mirror_x": {0: 1, 1: 0, 2: 3, 3: 2, 4: 6, 5: 7, 6: 4, 7: 5},
    "mirror_y": {0: 2, 1: 3, 2: 0, 3: 1, 4: 5, 5: 4, 6: 7, 7: 6},
}
SIDE_WORDS = {"north": "N", "east": "E", "south": "S", "west": "W"}
WORD_OF = {v: k for k, v in SIDE_WORDS.items()}


def base_ops(op: str) -> list[str]:
    return {"rot90": ["rot90"], "rot180": ["rot90", "rot90"], "rot270": ["rot90"] * 3,
            "mirror_x": ["mirror_x"], "mirror_y": ["mirror_y"]}[op]


def map_letters(s: str, base: str) -> str:
    """A side key ('N', 'NE', 'ridge_x', 'N/EW') under a base transform."""
    if s in ("ridge_x", "ridge_y"):
        return {"ridge_x": "ridge_y", "ridge_y": "ridge_x"}[s] if base == "rot90" else s
    return "".join(LETTER[base].get(ch, ch) for ch in s)


def sorted_sig(s: str) -> str:
    return "".join(ch for ch in "NESW" if ch in s) or "-"


def map_key(key: str, base: str) -> str:
    """A catalogue key: a roof side, a signature, or a stair 'ascent/signature'."""
    if "/" in key:
        a, s = key.split("/", 1)
        return f"{sorted_sig(map_letters(a, base)) if a != '-' else '-'}/" \
               f"{sorted_sig(map_letters(s, base)) if s != '-' else '-'}"
    if key in ("-", "cap", "NSEW", "NS", "EW") or key.startswith("ridge"):
        if key in ("NS", "EW") and base == "rot90":
            return {"NS": "EW", "EW": "NS"}[key]
        return map_letters(key, base) if key.startswith("ridge") else key
    return sorted_sig(map_letters(key, base)) if re.fullmatch(r"[NESW]+", key) else key


def side_lookup(d: dict, key: str, base: str):
    """The entry of a roof or stair kit for the side `key` seen after `base`. A hip corner is written
    'SE' in a style and 'ES' in a mined catalogue: both spellings are tried."""
    k = map_key(key, base)
    if k in d:
        return d[k]
    if re.fullmatch(r"[NESW]{2}", k):
        return d.get(k[::-1])
    return None


def hexs(i: int) -> str:
    return f"{i:#06x}"


def door_base(i: int, bases: list[int]) -> int | None:
    for b in bases:
        if b <= i < b + 16:
            return b
    return None


def build_table(families: dict, pieces: dict | None = None, names: dict[int, str] | None = None,
                door_bases: list[int] | None = None) -> dict:
    """The remap table from a mined catalogue (families.json, pieces.json) and optionally tiledata names."""
    pieces = pieces or {}
    inn = lambda i: set(pieces.get(i, {}).get("in", []))
    wall: dict = {}
    maps: dict = {"rot90": {}, "mirror_x": {}, "mirror_y": {}}
    bases = list(door_bases or [])
    for mat, fam in families.items():
        for role in ("wall", "window", "post"):
            for h, sigs in fam.get(role, {}).items():
                def best(sigkey, like):
                    c = sigs.get(sigkey) or []
                    return max(c, key=lambda i: len(inn(i) & like)) if c else None
                # the commonest signature of each id
                seen: dict = {}
                for sig, ids in sigs.items():
                    for i in ids:
                        seen.setdefault(i, []).append(sig)
                for i, sg in seen.items():
                    counts = pieces.get(i, {}).get("signature") or {}
                    sig = max(counts, key=counts.get) if counts else sg[0]
                    ew, ns = faces(sig if sig != "-" else "")
                    axis = "EWNS" if ew and ns else "EW" if ew else "NS" if ns else "post"
                    like = inn(i)
                    wall[i] = {"axis": axis, "ew": i if axis == "EW" else best("EW", like) or i,
                               "ns": i if axis == "NS" else best("NS", like) or i}
        for role in ("roof", "stair"):
            for key, ids in fam.get(role, {}).items():
                for base in maps:
                    tgt = map_key(key, base)
                    cands = fam[role].get(tgt)
                    if not cands:
                        continue
                    for i in ids:
                        maps[base].setdefault(i, max(cands, key=lambda c: len(inn(c) & inn(i))))
        for d in fam.get("door", {}).get("any", []):
            b = int(d, 16) - ((int(d, 16) - 5) % 16)
            if b not in bases and 0x600 <= b < 0x900:
                bases.append(b)
    table = {"format": SCHEMA, "wall": wall, "map": maps, "door_bases": sorted(bases)}
    if names:
        add_name_maps(table, names)
    return table


def add_name_maps(table: dict, names: dict[int, str]) -> None:
    """Pieces with a side word in their tiledata name ('rug east') mapped to the piece named for the
    turned side, where exactly one tile has that name."""
    by_name: dict = {}
    for i, n in names.items():
        by_name.setdefault(n.strip().lower(), []).append(i)
    for i, n in names.items():
        words = n.strip().lower().split()
        if not any(w in SIDE_WORDS for w in words):
            continue
        for base in LETTER:
            new = [WORD_OF[LETTER[base][SIDE_WORDS[w]]] if w in SIDE_WORDS else w for w in words]
            hit = by_name.get(" ".join(new))
            if hit and len(hit) == 1 and hit[0] != i:
                table["map"][base].setdefault(hexs(i), hexs(hit[0]))


def put_variants(into: dict, src, tgt) -> None:
    """Map each id of a piece entry to the same-numbered variant of the turned piece."""
    from styles import ids_of
    a, b = ids_of(src), ids_of(tgt)
    for k, i in enumerate(a):
        if not b:
            continue
        t = b[k % len(b)]
        # an id that is one piece's slope and another's fixed art (a hip cap drawn with the N slope's art)
        # keeps the slope's turn: that way a full turn and a double mirror still return it
        if t == i and hexs(i) in into and into[hexs(i)] != hexs(i):
            continue
        into[hexs(i)] = hexs(t)


def table_from_styles(styles: dict) -> dict:
    """The remap table of a style set: every straight, corner, post and window by role, roof and stair
    kits by side, doors by their listed ids."""
    from styles import ids_of
    wall: dict = {}
    maps: dict = {"rot90": {}, "mirror_x": {}, "mirror_y": {}}
    bases: list = []
    win_ns: dict = {}
    lossy: dict = {}
    for st in styles.values():
        runs = []
        for rs in ("walls", "foundation", "parapet", "trim", "gable_fill"):
            s = st.get(rs)
            ews, nss = ids_of((s or {}).get("EW")), ids_of((s or {}).get("NS"))
            if s and ews and nss:
                runs.append((s, ews, nss))
        # posts and corners first: a post that is also a straight (the parapet's) stays the straight
        for s, ews, nss in runs:
            for i in ids_of(s.get("post")):
                wall[hexs(i)] = {"axis": "post", "ew": hexs(ews[0]), "ns": hexs(nss[0])}
            for corner, v in (s.get("corners") or {}).items():
                for i in ids_of(v):
                    axis = {"SE": "EWNS", "NE": "EW", "SW": "NS", "NW": "post"}[corner]
                    wall[hexs(i)] = {"axis": axis, "ew": hexs(ews[0]), "ns": hexs(nss[0])}
        for s, ews, nss in runs:
            ew, ns = ews[0], nss[0]
            # a piece with its own art on one axis (the NE and SW corners, the junctions) turns into its
            # partner of the other axis, so a turn and back returns it; with no partner it becomes the
            # plain straight. A mirror keeps the axis and so the piece.
            corners, junc = s.get("corners") or {}, s.get("junctions") or {}
            for pair, with_ in ((("NE", "SW"), corners), (("SW", "NE"), corners), (("EW", "NS"), junc), (("NS", "EW"), junc)):
                mine, theirs = ids_of(with_.get(pair[0])), ids_of(with_.get(pair[1]))
                on_ew = pair[0] in ("NE", "EW")
                for k, i in enumerate(mine):
                    other = theirs[k % len(theirs)] if theirs else (ns if on_ew else ew)
                    wall[hexs(i)] = ({"axis": "EW", "ew": hexs(i), "ns": hexs(other)} if on_ew
                                     else {"axis": "NS", "ew": hexs(other), "ns": hexs(i)})
            paired = len(ews) == len(nss)
            for k, i in enumerate(ews):
                wall[hexs(i)] = {"axis": "EW", "ew": hexs(i), "ns": hexs(nss[k] if paired else ns)}
            for k, i in enumerate(nss):
                wall[hexs(i)] = {"axis": "NS", "ew": hexs(ews[k] if paired else ew), "ns": hexs(i)}
        for w in st.get("windows") or []:
            e, n = ids_of(w.get("EW")), ids_of(w.get("NS"))
            if e and n:
                wall[hexs(e[0])] = {"axis": "EW", "ew": hexs(e[0]), "ns": hexs(n[0])}
                if n[0] not in win_ns:                  # sets sharing one NS art: it turns back into the first set
                    win_ns[n[0]] = e[0]
                    wall[hexs(n[0])] = {"axis": "NS", "ew": hexs(e[0]), "ns": hexs(n[0])}
                elif win_ns[n[0]] != e[0]:
                    lossy[hexs(e[0])] = hexs(win_ns[n[0]])
        r = st.get("roof") or {}
        for kit in ("gable", "hip"):
            k = r.get(kit) or {}
            for base in maps:
                for side, v in k.items():
                    tgt = side_lookup(k, side, base)
                    put_variants(maps[base], v, tgt)
        sn = (st.get("stairs") or {}).get("straight") or {}
        for base in maps:
            for asc, v in sn.items():
                put_variants(maps[base], v, sn.get(map_letters(asc, base)))
        for d in (st.get("doors") or {}).values():
            for facing in ("EW", "NS"):
                c = ids_of((d.get(facing) or {}).get("closed"))
                if c:
                    b = c[0] - (0 if facing == "EW" else 8)
                    if b not in bases:
                        bases.append(b)
    table = {"format": SCHEMA, "wall": wall, "map": maps, "door_bases": sorted(bases)}
    if lossy:
        table["lossy"] = lossy              # id -> the id it comes back as (window sets that share one NS art)
    return table


# --- the transform -----------------------------------------------------------------------------

def vertex(base: str, u: int, v: int) -> tuple[int, int]:
    if base == "rot90":
        return -v, u
    if base == "mirror_x":
        return -u, v
    return u, -v


def step(base: str, c: Component, table: dict | None) -> list[Component]:
    key = hexs(c.item)
    x, y = c.x, c.y
    wall = (table or {}).get("wall", {}).get(key)
    bases = (table or {}).get("door_bases", [])
    db = door_base(c.item, bases)
    out: list[Component] = []

    def seg_ew(item):
        p, q = vertex(base, x, y + 1), vertex(base, x + 1, y + 1)
        if p[1] == q[1]:
            return Component(item, min(p[0], q[0]), p[1] - 1, c.z, c.visible), "EW"
        return Component(item, p[0] - 1, min(p[1], q[1]), c.z, c.visible), "NS"

    def seg_ns(item):
        p, q = vertex(base, x + 1, y), vertex(base, x + 1, y + 1)
        if p[0] == q[0]:
            return Component(item, p[0] - 1, min(p[1], q[1]), c.z, c.visible), "NS"
        return Component(item, min(p[0], q[0]), p[1] - 1, c.z, c.visible), "EW"
    if db is not None:
        k = (c.item - db) // 2
        parity = (c.item - db) % 2
        axis = "EW" if k < 4 else "NS"
        new_k = DOOR_PERM[base][k]
        item = db + 2 * new_k + parity
        comp, _ = (seg_ew if axis == "EW" else seg_ns)(item)
        return [comp]
    if wall:
        axis = wall["axis"]
        if axis in ("EW", "NS"):
            comp, after = (seg_ew if axis == "EW" else seg_ns)(c.item)
            return [Component(int(wall["ew" if after == "EW" else "ns"], 16), comp.x, comp.y, comp.z, comp.visible)]
        if axis == "EWNS":
            for fn in (seg_ew, seg_ns):
                comp, after = fn(int(wall["ew" if fn is seg_ew else "ns"], 16))
                comp = Component(int(wall["ew" if after == "EW" else "ns"], 16), comp.x, comp.y, comp.z, comp.visible)
                out.append(comp)
            return out
        # a post: on the vertex (x + 1, y + 1)
        v = vertex(base, x + 1, y + 1)
        return [Component(c.item, v[0] - 1, v[1] - 1, c.z, c.visible)]
    # a cell piece (floor, roof, stair, furniture): its square
    p, q = vertex(base, x, y), vertex(base, x + 1, y + 1)
    item = c.item
    tgt = (table or {}).get("map", {}).get(base, {}).get(key)
    if tgt:
        item = int(tgt, 16)
    return [Component(item, min(p[0], q[0]), min(p[1], q[1]), c.z, c.visible)]


def corner_joins(table: dict | None) -> dict:
    """(plain EW id, plain NS id) -> the corner piece that draws both faces from one cell."""
    joins: dict = {}
    for key, w in sorted(((table or {}).get("wall") or {}).items()):
        if w["axis"] == "EWNS":
            joins.setdefault((int(w["ew"], 16), int(w["ns"], 16)), int(key, 16))
    return joins


def split_step(base: str, comps: list[Component], table: dict | None) -> list[Component]:
    """One quarter turn or mirror of every piece. A corner that draws two faces becomes its two straights;
    one that lands on a straight already there is that straight (a shared corner is not drawn twice)."""
    wall = (table or {}).get("wall", {})
    out: list[Component] = []
    for c in comps:
        if wall.get(hexs(c.item), {}).get("axis") == "EWNS":
            continue
        out += step(base, c, table)
    have = Counter((c.item, c.x, c.y, c.z, c.visible) for c in out)
    for c in comps:
        if wall.get(hexs(c.item), {}).get("axis") == "EWNS":
            for p in step(base, c, table):
                k = (p.item, p.x, p.y, p.z, p.visible)
                if have[k]:
                    continue
                have[k] += 1
                out.append(p)
    return out


def recombine(comps: list[Component], joins: dict, table: dict | None = None) -> list[Component]:
    """An EW and an NS straight of one run set that stand in one cell are the front corner piece. This is
    the inverse of the split, so four quarter turns (or two mirrors) return every corner it began with."""
    if not joins:
        return comps
    wall = (table or {}).get("wall", {})
    bases = (table or {}).get("door_bases", [])
    degree: Counter = Counter()                       # wall ends on each (vertex, z)
    for c in comps:
        axis = wall.get(hexs(c.item), {}).get("axis")
        db = door_base(c.item, bases)
        if db is not None:
            axis = "EW" if (c.item - db) // 2 < 4 else "NS"
        if axis in ("EW", "EWNS"):
            degree[(c.x, c.y + 1, c.z)] += 1
            degree[(c.x + 1, c.y + 1, c.z)] += 1
        if axis in ("NS", "EWNS"):
            degree[(c.x + 1, c.y, c.z)] += 1
            degree[(c.x + 1, c.y + 1, c.z)] += 1
    by_cell: dict = {}
    for k, c in enumerate(comps):
        by_cell.setdefault((c.x, c.y, c.z, c.visible), []).append(k)
    dead: set = set()
    made: dict = {}
    ews = {e for e, _ in joins}
    nss = {n for _, n in joins}
    for cell, idx in by_cell.items():
        # the corner of a building has exactly the two ends at its vertex; a cell crossed by more walls
        # (a T or a cross) really holds two straights
        if len(idx) < 2 or degree[(cell[0] + 1, cell[1] + 1, cell[2])] != 2:
            continue
        for a in idx:
            if a in dead or comps[a].item not in ews:
                continue
            for b in idx:
                if b in dead or comps[b].item not in nss or (comps[a].item, comps[b].item) not in joins:
                    continue
                dead |= {a, b}
                made[a] = Component(joins[(comps[a].item, comps[b].item)], cell[0], cell[1], cell[2], cell[3])
                break
    return [made.get(k, c) for k, c in enumerate(comps) if k not in dead or k in made]


def extent(comps: list[Component], table: dict | None = None) -> tuple[int, int, int, int]:
    """(min u, max u, min v, max v) of what the pieces draw, on the grid's vertices: a wall is its edge, a
    post its vertex, anything else its cell. A turn or mirror maps this box onto the turned box exactly."""
    wall = (table or {}).get("wall", {})
    bases = (table or {}).get("door_bases", [])
    us: list = []
    vs: list = []
    for c in comps:
        axis = wall.get(hexs(c.item), {}).get("axis")
        db = door_base(c.item, bases)
        if db is not None:
            axis = "EW" if (c.item - db) // 2 < 4 else "NS"
        if axis == "EW":
            us += [c.x, c.x + 1]
            vs += [c.y + 1]
        elif axis == "NS":
            us += [c.x + 1]
            vs += [c.y, c.y + 1]
        elif axis == "post":
            us += [c.x + 1]
            vs += [c.y + 1]
        else:
            us += [c.x, c.x + 1]
            vs += [c.y, c.y + 1]
    return min(us), max(us), min(vs), max(vs)


def transform(comps: list[Component], op: str, table: dict | None = None, recentre: bool = False,
              keep_centre: bool = False) -> list[Component]:
    """`recentre` puts the result's centre cell at (0, 0). `keep_centre` turns about the centre of what the
    pieces draw (a selection that turns about its own middle), so an even-sized selection does not creep
    a cell. A box whose sides are odd by even cannot turn about its middle on the grid: it keeps its top
    corner instead, which also makes four turns return it."""
    if op not in OPS:
        raise ValueError(f"op '{op}': {', '.join(OPS)}")
    cur = list(comps)
    joins = corner_joins(table)
    for base in base_ops(op):
        cur = recombine(split_step(base, cur, table), joins, table)
    out = cur
    if keep_centre and out and comps:
        a, b = extent(comps, table), extent(out, table)
        if sum(a) % 2 == 0:                         # the centre of the box is on the grid after the turn: keep it
            dx, dy = (a[0] + a[1] - b[0] - b[1]) // 2, (a[2] + a[3] - b[2] - b[3]) // 2
        else:                                       # half a cell off whatever is chosen: keep the top corner, as the
            dx, dy = a[0] - b[0], a[2] - b[2]       # editor always did, so that four turns still return the selection
        out = [Component(c.item, c.x + dx, c.y + dy, c.z, c.visible) for c in out]
    if recentre and out:
        cx = (min(c.x for c in out) + max(c.x for c in out)) // 2
        cy = (min(c.y for c in out) + max(c.y for c in out)) // 2
        out = [Component(c.item, c.x - cx, c.y - cy, c.z, c.visible) for c in out]
    return out
