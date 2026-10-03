"""Synthetic tests for tools/decorate: no client data needed. Exits 0 when all pass, 1 otherwise.

    python tools/decorate/test_decorate.py
"""
from __future__ import annotations

import json
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE.parent / "multi"))
sys.path.insert(0, str(HERE))

import classify  # noqa: E402
import db  # noqa: E402
import decorate  # noqa: E402
import mine_decor  # noqa: E402
import planner  # noqa: E402
import rooms as R  # noqa: E402

WALL, IMP, SURF, WIN = classify.WALL, classify.IMPASSABLE, classify.SURFACE, classify.WINDOW
TILES = {
    0x0001: ("stone wall", WALL | IMP, 20),
    0x0002: ("window", WALL | WIN | IMP, 20),
    0x0003: ("marble", SURF, 0),
    0x0A5C: ("bed", SURF | IMP, 2),
    0x0A63: ("bed", SURF | IMP, 2),
    0x0B34: ("table", SURF | IMP, 6),
    0x0B56: ("chair", IMP, 3),
    0x09D7: ("plate", 0, 1),
    0x0FB1: ("forge", IMP, 10),
    0x0FAF: ("anvil", IMP, 6),
    0x0E43: ("wooden chest", IMP, 4),
    0x0A28: ("candle", 0, 2),
}


class FakeTileData:
    def static(self, item):
        if item not in TILES:
            return None
        n, f, h = TILES[item]
        return {"name": n, "flags": f, "height": h}


def building() -> list[tuple[int, int, int, int]]:
    """A 12 x 8 stone house: a partition at x = 6 with a door at (6, 4), a front door at (3, 8),
    a window at (9, 0). West room: a bed against the north wall and a chest. East room: a forge
    with its anvil against the east wall, a table with a chair and a plate on it."""
    out = []
    walls = {(x, y) for x in range(13) for y in range(9) if x in (0, 12) or y in (0, 8)}
    walls |= {(6, y) for y in range(9)}
    walls -= {(6, 4), (3, 8)}
    for (x, y) in sorted(walls):
        out.append((0x0002 if (x, y) == (9, 0) else 0x0001, x, y, 0))
    for x in range(1, 13):
        for y in range(1, 9):
            out.append((0x0003, x, y, 0))
    out += [(0x0A63, 3, 1, 0), (0x0A5C, 3, 2, 0), (0x0E43, 1, 7, 0),
            (0x0FB1, 11, 2, 0), (0x0FAF, 10, 2, 0),
            (0x0B34, 9, 5, 0), (0x0B56, 9, 6, 0), (0x09D7, 9, 5, 6)]
    return out


def test_classify():
    assert classify.kind_of("bed") == "bed"
    assert classify.kind_of("forge") == "forge"
    assert classify.kind_of("flowers") == "flora"
    assert classify.kind_of("skull with candle") == "remains"
    assert classify.kind_of("pen and ink") == "book"
    assert classify.classify(__import__("collections").Counter({"bed": 2, "chest": 1}))[0] == "bedroom"
    assert classify.classify(__import__("collections").Counter({"forge": 1, "anvil": 1}))[0] == "smithy"
    assert classify.classify(__import__("collections").Counter())[0] == "empty"
    assert not classify.is_furnishing({"name": "stone wall", "flags": WALL, "height": 20})
    assert not classify.is_furnishing({"name": "marble", "flags": SURF, "height": 0})
    assert classify.is_furnishing({"name": "rug", "flags": SURF, "height": 0})
    assert classify.facing_from_wall("N") == "S"


def test_segment():
    b = building()
    walls = {(x, y) for i, x, y, _z in b if i in (0x0001, 0x0002)}
    floor = {(x, y) for i, x, y, _z in b if i == 0x0003}
    doors = R.door_gaps(floor | R._gaps_between(walls), walls)
    assert {(6, 4), (3, 8)} <= doors, doors
    rooms = R.segment(floor, walls, doors=doors, windows={(9, 0)})
    assert len(rooms) == 2, [r.bbox for r in rooms]
    west, east = sorted(rooms, key=lambda r: r.bbox)
    assert west.bbox == (1, 1, 5, 7) and east.bbox == (7, 1, 11, 7), (west.bbox, east.bbox)
    assert ((6, 4), "E") in west.doors and ((3, 8), "S") in west.doors
    assert east.windows == [((9, 0), "N")]
    assert west.enclosed and east.enclosed
    assert R.wall_sides((1, 1), walls) == "NW"


def test_analyse():
    tiles = mine_decor.Tiles(FakeTileData())
    found = mine_decor.analyse(building(), tiles)
    assert found.storeys == [0], found.storeys
    types = sorted(r["type"] for r in found.rooms)
    assert types == ["bedroom", "smithy"], types
    east = next(r for r in found.rooms if r["type"] == "smithy")
    forge = next(f for f in east["furn"] if f["kind"] == "forge")
    assert forge["against"] == "E" and forge["facing"] == "W", forge
    plate = next(f for f in east["furn"] if f["item"] == 0x09D7)
    assert plate["on_item"] == 0x0B34 and plate["z_above"] == 6, plate
    chair = next(f for f in east["furn"] if f["kind"] == "chair")
    assert chair["facing"] == "N", chair                 # it faces the table north of it
    g = next(g for g in east["groups"] if any(i[0] == 0x0FB1 for i in g["items"]))
    assert {i[0] for i in g["items"]} == {0x0FB1, 0x0FAF} and g["against"] == "E", g
    west = next(r for r in found.rooms if r["type"] == "bedroom")
    bed = next(g for g in west["groups"] if any(i[0] == 0x0A63 for i in g["items"]))
    assert bed["against"] == "N" and bed["w"] == 1 and bed["h"] == 2, bed


def make_db(path: Path) -> dict:
    tiles = mine_decor.Tiles(FakeTileData())
    src = [("test", 1, [(f"b{k}", (0, 0, 12, 8), building(), None) for k in range(3)])]
    return mine_decor.write(path, tiles, src, {"test": "1"}, log=lambda *a: None)


def test_db():
    with tempfile.TemporaryDirectory() as d:
        p = Path(d) / "decor.sqlite"
        counts = make_db(p)
        assert counts["building"] == 3 and counts["room"] == 6, counts
        con = db.open_ro(p)
        uses = {r[0]: r[1] for r in con.execute("SELECT items, uses FROM template")}
        assert max(uses.values()) == 3, uses
        rt = {r[0]: r[1] for r in con.execute("SELECT type, rooms FROM room_type")}
        assert rt == {"bedroom": 3, "smithy": 3}, rt
        lib = decorate.Library.from_db(con)
        con.close()
        assert lib.for_type("smithy") and lib.for_type("bedroom")


def sidecar() -> dict:
    """A built multi's sidecar as generate.py writes it: 12 x 8, two storeys, a partition at
    x = 6 with doors at (6, 4) (both storeys), a front door at (3, 8), windows, and a stair
    at x = 10 rising north from (10, 6) to arrive at (10, 1) upstairs."""
    def storey(z, holes, arrivals, doors):
        walls = {(x, y) for x in range(13) for y in range(9) if x in (0, 12) or y in (0, 8)} | {(6, y) for y in range(9)}
        solid = walls - set(doors)
        floor = {(x, y) for x in range(1, 13) for y in range(1, 9)} - holes
        return {"z": z, "walls": sorted(map(list, solid)), "doors": sorted(map(list, doors)),
                "windows": [[9, 0], [0, 4], [12, 4]], "floor": sorted(map(list, floor)), "open": [],
                "arrivals": [list(a) for a in arrivals]}
    stair_cells = {(10, y) for y in range(2, 7)}
    return {"name": "synthetic", "storeys": [7, 27],
            "local": {"storeys": [storey(7, set(), [], [(6, 4), (3, 8)]),
                                  storey(27, stair_cells, [(10, 1)], [(6, 4)])],
                      "stairs": [{"foot": [10, 7], "z": 7, "cells": sorted(map(list, stair_cells)),
                                  "arrive": [10, 1], "to": 1}]}}


def test_decorate():
    with tempfile.TemporaryDirectory() as d:
        p = Path(d) / "decor.sqlite"
        make_db(p)
        con = db.open_ro(p)
        lib = decorate.Library.from_db(con)
        con.close()
    side = sidecar()
    allowed = ("bedroom", "smithy")
    a, report = decorate.decorate(side, lib, seed=5, allowed=allowed)
    b, _ = decorate.decorate(side, lib, seed=5, allowed=allowed)
    assert a == b, "not deterministic"
    assert a, report
    assert decorate.check(side, a) == [], decorate.check(side, a)
    for n, st in enumerate(side["local"]["storeys"]):
        plans, walls = decorate.plan_storey(n, st, side["local"]["stairs"])
        clear = set().union(*(rp.clear for rp in plans))
        cells = set().union(*(rp.room.cells for rp in plans))
        for dd in a:
            if dd["storey"] == n:
                c = tuple(dd["at"])
                assert c in cells or c in walls, (n, dd)
                assert c not in clear, (n, dd, "stands where it must stay clear")
    # a bed stands with its head to a wall on its learned side
    walls0 = set(map(tuple, side["local"]["storeys"][0]["walls"]))
    walls1 = set(map(tuple, side["local"]["storeys"][1]["walls"]))
    for dd in a:
        if dd["item"] == "0x0a63":
            x, y = dd["at"]
            assert (x, y - 1) in (walls0 if dd["storey"] == 0 else walls1), dd
    # forced types, and a different seed may differ but stays clean
    c, _ = decorate.decorate(side, lib, seed=6, allowed=allowed, types={"0:2,2": "smithy"})
    assert decorate.check(side, c) == []
    assert any(dd["item"] == "0x0fb1" and dd["storey"] == 0 and dd["at"][0] < 6 for dd in c), c


def test_check_catches():
    side = sidecar()
    bad = [{"item": "0x0b34", "at": [6, 4], "z": 0, "storey": 0},            # in a doorway
           {"item": "0x0b34", "at": [10, 7], "z": 0, "storey": 0}]           # on a stair's foot
    probs = decorate.check(side, bad)
    assert any("door" in p for p in probs) and any("stair" in p for p in probs), probs
    # a wall of furniture across the west room cuts the front door off from the partition door
    wall = [{"item": "0x0b34", "at": [x, 5], "z": 0, "storey": 0} for x in range(1, 6)]
    assert any("cut off" in p for p in decorate.check(side, wall)), decorate.check(side, wall)


def rules_plan(side: dict, lib, allowed) -> dict:
    """A plan in the model's answer format made from the rules' own placements (so it is valid)."""
    import random
    all_plans = planner.house_plans(side)
    rooms = []
    for rid, rp in planner.room_ids(all_plans).items():
        n, st, _p, walls = all_plans[rp.storey]
        rp.type = "smithy" if rp.storey == 0 and (2, 2) in rp.room.cells else "bedroom"
        decorate.furnish_room(random.Random(3), lib, rp, walls, set(map(tuple, st["windows"])),
                              set(map(tuple, st["doors"])))
        pl = []
        for tp, x, y in rp.placed:
            if tp.on_wall:                        # the model names the room cell before the wall
                d = planner.R.STEP[tp.against[0]]
                x, y = x - d[0], y - d[1]
            pl.append({"template": tp.id, "x": x, "y": y})
        rooms.append({"room": rid, "type": rp.type, "focal": pl[0]["template"] if pl else -1,
                      "placements": pl, "reason": "test"})
    return {"rooms": rooms}


def test_planner():
    with tempfile.TemporaryDirectory() as d:
        p = Path(d) / "decor.sqlite"
        make_db(p)
        con = db.open_ro(p)
        lib = decorate.Library.from_db(con)
        side = sidecar()
        allowed = ("bedroom", "smithy")
        req = planner.build_request(side, lib, con, "a smith's house", allowed)
        assert "s0r0" in req["user"] and req["pngs"] and req["pngs"][0][1:4] == b"PNG", req["user"][:200]
        assert '"examples"' in req["user"] and req["offered"]
        good = rules_plan(side, lib, allowed)
        assert all(r["placements"] for r in good["rooms"]), good

        # a valid answer: placed as planned, one call, nothing by the rules, and cached
        calls = []

        def fake(answers):
            def call(msgs, note=""):
                calls.append(msgs)
                return answers[len(calls) - 1], {"input_tokens": 10, "output_tokens": 5}
            return call
        cache = Path(d) / "plans"
        a, report, meta = planner.decorate_ai(side, lib, con, seed=2, allowed=allowed, cache_dir=cache,
                                              call=fake([good]))
        assert meta["calls"] == 1 and not meta["errors_final"] and not meta["fallback_rooms"], (meta, report)
        assert a and decorate.check(side, a) == [], decorate.check(side, a)
        assert any(dd["item"] == "0x0fb1" and dd["storey"] == 0 and dd["at"][0] < 6 for dd in a), a
        assert len(list(cache.glob("*.json"))) == 1

        # the cache answers the second time; offline never calls
        def boom(msgs, note=""):
            raise AssertionError("called the API")
        b, _r, meta2 = planner.decorate_ai(side, lib, con, seed=2, allowed=allowed, cache_dir=cache,
                                           call=boom, offline=True)
        assert meta2["cached"] and a == b

        # a broken answer goes back once with its errors; the corrected one is used
        bad = json.loads(json.dumps(good))
        room0 = bad["rooms"][0]
        room0["placements"].insert(0, {"template": room0["placements"][0]["template"], "x": -40, "y": -40})
        bad["rooms"][1]["type"] = "throne-room"
        calls.clear()
        c, _r, meta3 = planner.decorate_ai(side, lib, con, seed=3, allowed=allowed, call=fake([bad, good]))
        assert meta3["calls"] == 2 and len(meta3["errors_first"]) == 2 and not meta3["errors_final"], meta3
        errs = calls[1][-1]["content"]
        assert "outside the room" in errs and "throne-room" in errs, errs

        # an answer that stays wrong: its rooms fall back to the rules, and the house is still clean
        calls.clear()
        e, _r, meta4 = planner.decorate_ai(side, lib, con, seed=4, allowed=allowed,
                                           call=fake([{"rooms": []}, {"rooms": []}]))
        assert set(meta4["fallback_rooms"]) == set(planner.room_ids(planner.house_plans(side))), meta4
        assert e and decorate.check(side, e) == []

        # no plan at all (offline, nothing cached) is the rules' house
        f, _r, meta5 = planner.decorate_ai(side, lib, con, seed=4, allowed=allowed, offline=True)
        assert meta5["errors_final"][0].startswith("no plan") and f and decorate.check(side, f) == []

        # a forced type overrides the model's choice for that room, and the model is told
        calls.clear()
        g, _r, meta6 = planner.decorate_ai(side, lib, con, seed=5, allowed=allowed, types={"0:2,2": "bedroom"},
                                           call=fake([good, good]))
        assert meta6["calls"] == 2 and "must be a bedroom" in calls[1][-1]["content"], meta6
        assert meta6["fallback_rooms"] == ["s0r0"], meta6
        assert not any(dd["item"] == "0x0fb1" and dd["storey"] == 0 and dd["at"][0] < 6 for dd in g), g
        con.close()


def test_planner_why_not():
    side = sidecar()
    all_plans = planner.house_plans(side)
    ids = planner.room_ids(all_plans)
    rp = next(r for r in ids.values() if r.storey == 0 and (2, 2) in r.room.cells)
    walls = all_plans[0][3]
    tp = decorate.Template(1, [[0xA63, 0, 0, 0], [0xA63, 1, 0, 0]], 2, 1, frozenset({(0, 0), (1, 0)}), "N",
                           False, {"bed": 2}, 1, {})
    assert planner.why_not(tp, 2, 1, rp, walls) == ""
    assert "must stand against" in planner.why_not(tp, 2, 3, rp, walls)
    assert "outside the room" in planner.why_not(tp, 5, 0, rp, walls)


def test_facings():
    """Art that only faces one way: a bed whose back may only go north never stands against
    another wall, and the check counts a piece that faces a wall."""
    with tempfile.TemporaryDirectory() as d:
        p = Path(d) / "decor.sqlite"
        make_db(p)
        con = db.open_ro(p)
        free = decorate.Library.from_db(con)
        f = Path(d) / "facings.json"
        f.write_text(json.dumps({"items": {"0x0a63": ["W"]}}), encoding="utf-8")
        facings = decorate.load_facings(f)
        only_w = decorate.Library.from_db(con, facings=facings)
        con.close()
    beds = lambda lib: [t for t in lib.templates if any(it[0] == 0xA63 for it in t.items)]  # noqa: E731
    assert beds(free) and all("N" in t.against for t in beds(free))
    assert not beds(only_w), "a bed whose art needs a west wall was kept for a north wall"
    side = sidecar()
    a, _ = decorate.decorate(side, free, seed=5, allowed=("bedroom", "smithy"))
    assert decorate.check(side, a) == []
    wrong = decorate.check(side, a, facings)
    n_beds = sum(dd["item"] == "0x0a63" for dd in a)
    assert n_beds and len(wrong) == n_beds and "faces a wall" in wrong[0], wrong
    ok = dict(facings)
    ok[0xA63] = {"N"}
    assert decorate.check(side, a, ok) == []
    # hung on a wall: its back is that wall
    hung = [{"item": "0x0a63", "at": [3, 0], "z": 5, "storey": 0}]
    assert decorate.check(side, hung, ok) == [] and decorate.wrong_facing(side, hung, facings)


def main() -> int:
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_") and callable(v)]
    failed = 0
    for t in tests:
        try:
            t()
            print(f"PASS {t.__name__}")
        except Exception:
            failed += 1
            print(f"FAIL {t.__name__}")
            traceback.print_exc()
    print(f"{len(tests) - failed}/{len(tests)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
