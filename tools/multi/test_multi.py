"""Tests for tools/multi on synthetic data (no UO client data), for CI. Exit 0/1.

    python tools/multi/test_multi.py

Checks the multi record codecs, piece signatures and roof sides, the
generator against a tiny hand-made catalogue (walls closed, the door a gap
with a hidden marker and a sidecar door, the gable roof's courses and
fill), the validator catching a gap in a wall and a sealed room, a two-storey
L with a stair (steps on stacked blocks, a hole above, walk stops up it), a
fenced yard (a real gate, the validator catching a gap), and a small scene
(a wall and a tower cut into multis, stair stops added to its tour, the same
bytes twice).
"""
from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))

import fort  # noqa: E402
import generate  # noqa: E402
import validate  # noqa: E402
from mine import roof_side, signature  # noqa: E402
from multifile import Component, decode_mul, decode_uop, encode_mul, encode_uop  # noqa: E402

FAILS: list[str] = []


def check(ok: bool, what: str) -> None:
    print(f"{'ok  ' if ok else 'FAIL'} {what}")
    if not ok:
        FAILS.append(what)


SIGS = ["EW", "NS", "ES", "SW", "NE", "NW", "E", "W", "N", "S", "-", "NES", "NSW", "ESW", "NEW"]


def fake_catalogue(folder: Path) -> None:
    """Every wall piece id encodes its signature: 0x100 + index, window 0x200 + index, and so on."""
    fam = {
        "plaster": {
            "wall": {"19": {s: [f"{0x100 + i:#06x}"] for i, s in enumerate(SIGS)},
                     "3": {"EW": ["0x0301"], "NS": ["0x0302"], "E": ["0x0301"], "W": ["0x0301"]}},
            "window": {"19": {"EW": ["0x0201"], "NS": ["0x0202"]}},
            "door": {"any": ["0x06a5"]},
        },
        "stone": {
            "wall": {"5": {s: [f"{0x400 + i:#06x}"] for i, s in enumerate(SIGS)}},
            "stair": {"N/E": ["0x0501"], "N/EW": ["0x0502"], "N/W": ["0x0503"],
                      "S/-": ["0x0511"], "E/-": ["0x0512"], "W/-": ["0x0513"]},
            "floor": {"NESW": ["0x0611"]},
        },
        "wooden": {"floor": {"NESW": ["0x0601", "0x0602"]}},
        "tile": {"roof": {"N": ["0x0701"], "S": ["0x0702"], "W": ["0x0703"], "E": ["0x0704"],
                          "ridge_x": ["0x0705"], "ridge_y": ["0x0706"]}},
    }
    (folder / "families.json").write_text(json.dumps(fam), encoding="utf-8")
    # the block a stair stands on: 10 high, a bridge, used in the same originals as the steps
    pieces = {"0x0750": {"height": 10, "role": "floor", "flags": ["surface", "bridge"], "in": [1]}}
    pieces.update({f"{i:#06x}": {"height": 5, "role": "stair", "in": [1]} for i in (0x501, 0x502, 0x503, 0x511, 0x512, 0x513)})
    (folder / "pieces.json").write_text(json.dumps(pieces), encoding="utf-8")


def cottage(**over) -> dict:
    d = {"format": 1, "name": "t", "size": [4, 4],
         "materials": {"wall": "plaster", "foundation": "stone", "floor": "wooden", "steps": "stone", "roof": "tile"},
         "storeys": [{"openings": [{"kind": "door", "side": "S", "offset": 2},
                                   {"kind": "window", "side": "W", "offset": 2}]}],
         "roof": {"style": "gable", "ridge": "y"}}
    d.update(over)
    return d


def main() -> int:
    # codecs
    comps = [Component(0x203, -3, -3, 7), Component(0x6A5, 0, 3, 7, False), Component(1, 0, 0, 0, False)]
    check(decode_uop(encode_uop(0x3F00, comps)) == comps, "UOP multi record round-trips, hidden kept")
    check(decode_mul(encode_mul(comps), 16) == comps, "MUL multi record (16 bytes) round-trips")
    try:
        encode_uop(1, [Component(1, 0, 0, 0)] * 5000)
        check(False, "a multi beyond the shard's 64 KB entry is refused")
    except ValueError:
        check(True, "a multi beyond the shard's 64 KB entry is refused")

    # signatures and roof sides, as the miner reads them
    ring = {(x, y, 0) for x in range(3) for y in (0, 2)} | {(x, y, 0) for y in range(3) for x in (0, 2)}
    check(signature(ring, 0, 0, 0) == "ES" and signature(ring, 1, 0, 0) == "EW" and signature(ring, 2, 2, 0) == "NW",
          "wall signatures: NW corner ES, a run EW, SE corner NW")
    roof = {(0, y): {0} for y in range(3)} | {(1, y): {3} for y in range(3)} | {(2, y): {0} for y in range(3)}
    check(roof_side(roof, 0, 1, 0) == "W" and roof_side(roof, 2, 1, 0) == "E" and roof_side(roof, 1, 1, 3) == "ridge_y",
          "roof sides: west slope W, east slope E, the top ridge_y")

    with tempfile.TemporaryDirectory(prefix="multi-test-") as tmp:
        tmp = Path(tmp)
        fake_catalogue(tmp)
        cat = generate.Catalogue(tmp)
        comps, side = generate.build(cottage(), cat)
        at = {(c.x + 2, c.y + 2, c.z): c for c in comps if c.item not in (0x601, 0x602)}   # walls over the floor
        check(at[(0, 0, 7)].item == 0x100 + SIGS.index("ES"), "the NW corner takes the ES piece")
        check(at[(4, 4, 7)].item == 0x100 + SIGS.index("NW"), "the SE corner takes the NW piece")
        check(at[(0, 2, 7)].item == 0x202, "the west window is the NS window piece")
        door = at.get((2, 4, 7))
        check(door is not None and door.item == 0x6A5 and not door.visible, "the door cell holds only a hidden door marker")
        check(at[(1, 4, 7)].item == 0x100 + SIGS.index("EW") and at[(3, 4, 7)].item == 0x100 + SIGS.index("EW"),
              "the wall runs on beside the door (EW pieces, as the originals), no end pieces")
        check(side["doors"] == [{"x": 0, "y": 2, "z": 7, "storey": 0, "facing": "WestCW", "type": "DarkWoodDoor"}],
              "the sidecar door: centre-relative, WestCW in a wall along x")
        check(sum(1 for c in comps if c.z == 0 and c.item >= 0x400 and c.item < 0x500) == 16,
              "the foundation is a closed 16-piece ring at z 0")
        steps = sorted((c.x + 2, c.y + 2, c.item) for c in comps if c.z == 2)
        check(steps == [(1, 5, 0x501), (2, 5, 0x502), (3, 5, 0x503)], "three steps outside the door: ends and middle")
        roofs = sorted({c.z for c in comps if 0x701 <= c.item <= 0x706})
        check(roofs == [27, 30, 33], f"gable courses 3 z apart from the wall top (got {roofs})")
        check(all(c.x + 2 == 3 for c in comps if c.item == 0x706), "a W=4 house ridges on x = 3 (x 1..5)")
        fill = sorted((c.x + 2, c.y + 2, c.z) for c in comps if c.item == 0x301)
        check(fill == [(2, 0, 27), (2, 4, 27), (3, 0, 27), (3, 0, 30), (3, 4, 27), (3, 4, 30), (4, 0, 27), (4, 4, 27)],
              f"gable fill on both ends, the hidden north one too (got {fill})")
        check(validate.validate(comps, side, None) == [], "the cottage validates")

        try:
            generate.build(cottage(size=[5, 4]), cat)
            check(False, "an even gable span is refused")
        except generate.DescriptionError:
            check(True, "an even gable span is refused")
        try:
            generate.build(cottage(storeys=[{"openings": [{"kind": "door", "at": [2, 2]}]}]), cat)
            check(False, "an opening off the walls is refused")
        except generate.DescriptionError:
            check(True, "an opening off the walls is refused")

        # the validator: a gap in a wall, a sealed room, no door
        gap = json.loads(json.dumps(side))
        gap["local"]["storeys"][0]["walls"] = [w for w in gap["local"]["storeys"][0]["walls"] if w != [4, 2]]
        check(any("not closed" in p for p in validate.validate(comps, gap, None)), "a gap in a wall is caught")
        sealed = cottage(storeys=[{"openings": [{"kind": "door", "side": "S", "offset": 2}],
                                   "partitions": [{"x": 2, "from": 0, "to": 4}]}])
        comps2, side2 = generate.build(sealed, cat)
        check(any("unreachable" in p for p in validate.validate(comps2, side2, None)),
              "a room behind a partition with no door is caught")
        nodoor = cottage(storeys=[{"openings": []}])
        comps3, side3 = generate.build(nodoor, cat)
        check(any(p == "no door" for p in validate.validate(comps3, side3, None)), "a multi with no door is caught")

        # a two-storey L with a stair
        two = cottage(name="l", size=None, rects=[[0, 0, 6, 4], [0, 4, 4, 8]], roof={"style": "flat", "material": "stone"},
                      storeys=[{"openings": [{"kind": "door", "side": "S", "offset": 2}],
                                "stairs": [{"at": [1, 1], "rise": "S", "material": "stone"}]},
                               {"openings": []}])
        del two["size"]
        comps4, side4 = generate.build(two, cat)
        cx, cy = side4["centre"]
        steps4 = sorted((c.x + cx, c.y + cy, c.z) for c in comps4 if c.item == 0x511)
        blocks = [c for c in comps4 if c.item == 0x750]
        check(steps4 == [(1, 1 + i, 7 + 5 * i) for i in range(4)], f"stair steps rise 5 z a cell (got {steps4})")
        check(len(blocks) == 0 + 1 + 2 + 3 + 4, f"each step stands on its blocks, the landing on four (got {len(blocks)})")
        up = side4["local"]["storeys"][1]
        hole = {(1, y) for y in range(1, 6)}
        check(not hole & set(map(tuple, up["floor"])), "the floor above is open over the flight and landing")
        names = [st["name"] for st in side4["stops"]]
        check("stair1_foot" in names and any(st["z"] == 27 for st in side4["stops"]),
              f"the walk climbs the stair to z 27 (stops {names})")
        check(validate.validate(comps4, side4, None) == [], "the two-storey L validates")
        # the same with a rail round the stairwell: the sides and the foot end, not the arrival
        railed = json.loads(json.dumps(two))
        railed["storeys"][0]["stairs"][0]["rail"] = "stone"
        comps4r, side4r = generate.build(railed, cat)
        rails = {(c.x + cx, c.y + cy) for c in comps4r if c.z == 27 and 0x400 <= c.item < 0x500}
        want = {(2, y) for y in range(1, 5)}   # x 0 and y 0 are the outer walls; (2, 5) is beside the arrival
        check(rails == want, f"the stairwell rail runs beside the flight, the arrival end open (got {sorted(rails)})")
        check(not rails & {(0, 6), (1, 6), (2, 6)}, "no rail beside the arrival")
        check(validate.validate(comps4r, side4r, None) == [], "the railed L validates")

        # a fenced yard
        yd = cottage(yard={"box": [-2, -1, 6, 8], "fence": "stone", "height": 5, "gate": {"side": "S", "offset": 4},
                           "gate_type": "IronGate", "path": "stone"})
        comps5, side5 = generate.build(yd, cat)
        gates = [d for d in side5["doors"] if d["type"] == "IronGate"]
        check(len(gates) == 1, "the yard has a real gate")
        check(validate.validate(comps5, side5, None) == [], "the fenced yard validates")
        broken = json.loads(json.dumps(side5))
        broken["local"]["yard"]["fence"] = [f for f in broken["local"]["yard"]["fence"] if f != [-2, 3] and f != (-2, 3)]
        check(any("fence is not closed" in p for p in validate.validate(comps5, broken, None)),
              "a gap in the fence is caught")

        again_c, _ = generate.build(cottage(), cat)
        check([(c.item, c.x, c.y, c.z) for c in again_c] == [(c.item, c.x, c.y, c.z) for c in comps],
              "a house builds the same after other builds on the same catalogue")

        # a small scene: a wall into a tower, a stair up the wall, a tour that climbs
        scene = {"format": 1, "kind": "scene", "name": "t",
                 "materials": {"wall": "stone", "walk": "stone", "floor": "stone", "stairs": "stone"},
                 "elements": [
                     {"type": "wall", "part": "wall", "path": [[0, 0], [20, 0]], "thickness": 3, "top": 20,
                      "floor": "stone", "parapet": "none"},
                     {"type": "tower", "part": "tower", "disc": [24, 0, 4], "levels": [0, 20], "top": 40,
                      "floor": "stone", "parapet": False},
                     {"type": "stair", "part": "wall", "at": [4, 3], "rise": "E", "z": 0, "to": 20}],
                 "tour": [{"name": "foot", "at": [2, 4], "z": 0}, {"name": "walk", "at": [10, 0], "z": 20}]}
        sc = fort.build_scene(scene, cat)
        again = fort.build_scene(json.loads(json.dumps(scene)), cat)
        def overlap(a, b):
            return not (a[2] < b[0] or b[2] < a[0] or a[3] < b[1] or b[3] < a[1])
        boxes = [p["bounds"] for p in sc["parts"]]
        check(not any(overlap(a, b) for i, a in enumerate(boxes) for b in boxes[i + 1:]),
              f"no two multis of a scene overlap (the shard would lose one's tiles; got {boxes})")
        check({"wall", "tower"} <= {h for p in sc["parts"] for h in p["holds"]}, "the multis say which elements they hold")
        check([[(c.item, c.x, c.y, c.z) for c in p["comps"]] for p in sc["parts"]] ==
              [[(c.item, c.x, c.y, c.z) for c in p["comps"]] for p in again["parts"]], "a scene builds to the same bytes")
        tour = [t["name"] for t in sc["tour"]]
        check(tour == ["foot", "walk_stair0_from", "walk_stair0_to", "walk"], f"the tour climbs by the stair (got {tour})")
        check(sc["tour"][0].get("on_land") and not sc["tour"][-1].get("on_land"),
              "a stop on bare land at the ground is marked on_land (the proof reads the land's z), one on the wall not")
        climb = sc["tour"][1:3]
        check(climb[0]["z"] == 0 and climb[1]["z"] == 20 and (climb[0]["x"], climb[0]["y"]) == (3, 3),
              f"the climb starts at the stair's foot and ends on its landing (got {climb})")

        swapped = json.loads(json.dumps(scene))
        swapped["elements"] = [swapped["elements"][1], swapped["elements"][0], swapped["elements"][2]]
        ss = fort.build_scene(swapped, cat)
        same = lambda sc_: sorted((c.item, c.x + p["centre"][0], c.y + p["centre"][1], c.z) for p in sc_["parts"] for c in p["comps"])
        check(same(ss) == same(sc), "a tower listed before the wall still opens where its walkway meets it (the same pieces)")
        into = json.loads(json.dumps(scene))
        into["elements"].append({"type": "tower", "part": "t2", "disc": [8, 5, 4], "levels": [0, 20], "top": 40,
                                 "floor": "stone", "parapet": False})
        check(bool(fort.build_scene(into, cat)["problems"]), "a stair run into a tower is reported")
        check(not sc["problems"], f"a clear stair is not (got {sc['problems']})")

        # a floor of mixed materials: the first on about half the cells, the same bytes every build
        mixed = {"format": 1, "kind": "scene", "name": "m", "materials": {"wall": "stone"},
                 "elements": [{"type": "platform", "part": "yard", "z": 0, "face": False,
                               "floor": ["stone", "wooden"], "shapes": [{"box": [0, 0, 9, 9]}]}],
                 "tour": [{"name": "a", "at": [1, 1], "z": 0}, {"name": "b", "at": [8, 8], "z": 0}]}
        m1, m2 = fort.build_scene(mixed, cat), fort.build_scene(json.loads(json.dumps(mixed)), cat)
        tiles = [c.item for p in m1["parts"] for c in p["comps"] if c.z == 0]
        stone = sum(1 for i in tiles if i == 0x611)
        check(0.35 < stone / len(tiles) < 0.65 and {0x601, 0x602} & set(tiles),
              f"a mixed floor lays both materials, the first on about half ({stone} of {len(tiles)})")
        grid = {(c.x + p["centre"][0], c.y + p["centre"][1]): c.item == 0x611 for p in m1["parts"] for c in p["comps"]}
        alt = sum(grid[(x, y)] != grid[(x + 1, y)] for x in range(9) for y in range(10))
        check(alt < 70, f"a mixed floor is scattered, not a checkerboard ({alt} of 90 neighbours differ)")
        check([(c.item, c.x, c.y) for p in m1["parts"] for c in p["comps"]] ==
              [(c.item, c.x, c.y) for p in m2["parts"] for c in p["comps"]], "a mixed floor builds the same bytes")

        # props: kept on top of what they stand on, which keeps its floor; an unknown piece reported
        withp = json.loads(json.dumps(mixed))
        withp["elements"].append({"type": "props", "part": "yard", "items": [
            {"item": "0x0750", "at": [3, 3], "z": 0}, {"item": "0x7fff", "at": [4, 4], "z": 0}]})
        mp = fort.build_scene(withp, cat)
        at33 = sorted(c.item for p in mp["parts"] for c in p["comps"] if (c.x + p["centre"][0], c.y + p["centre"][1]) == (3, 3))
        check(0x0750 in at33 and len(at33) == 2, f"a prop stands on the floor, which stays (got {[hex(i) for i in at33]})")
        check(any("0x7fff" in q for q in mp["problems"]), "an unknown prop is reported")

        # the offline walk: a terrace at z 20 on walls, reached by a stair; a parapet cuts it
        import walkcheck
        from multifile import Component as C
        kinds = {"0x0001": {"flags": ["impassable"], "height": 20}, "0x0002": {"flags": ["surface"], "height": 0},
                 "0x0003": {"flags": ["surface", "bridge"], "height": 10}}
        comps = [C(1, x, y, 0) for x in range(4, 10) for y in range(0, 3)]
        comps += [C(2, x, y, 20) for x in range(4, 10) for y in range(0, 3)]
        comps += [C(3, x, 1, 5 * (x - 1)) for x in (1, 2, 3)]          # steps standing at 5, 10, 15
        terrace = [{"centre": [0, 0], "comps": comps}]
        legs = [{"name": "foot", "x": -1, "y": 1, "z": 0}, {"name": "top", "x": 8, "y": 1, "z": 20}]
        check(walkcheck.check_tour(terrace, legs, kinds) == [], "the walk climbs the stair onto the terrace")
        walled = [{"centre": [0, 0], "comps": comps + [C(1, 6, y, 20) for y in range(-1, 4)]}]
        check(len(walkcheck.check_tour(walled, legs, kinds)) == 1, "a wall across the terrace is caught")
        # two floors at z 20 over walls, touching only at a corner: UO does not step round it
        corner = [C(1, x, y, 0) for x in range(0, 6) for y in range(0, 6)]
        corner += [C(2, x, y, 20) for x, y in [(0, 0), (1, 0), (0, 1), (1, 1), (2, 2), (3, 2), (2, 3), (3, 3)]]
        corner += [C(1, x, y, 20) for x, y in [(2, 1), (1, 2), (2, 0), (0, 2)]]
        cleg = [{"name": "a", "x": 0, "y": 0, "z": 20}, {"name": "b", "x": 3, "y": 3, "z": 20}]
        check(len(walkcheck.check_tour([{"centre": [0, 0], "comps": corner}], cleg, kinds)) == 1,
              "floors that touch only at a corner do not join")
        # a long wall on the land with its only way round forty cells off: a detour, reported
        long_wall = [{"centre": [0, 0], "comps": [C(1, 0, y, 0) for y in range(-40, 41)]}]
        dleg = [{"name": "west", "x": -1, "y": 0, "z": 0}, {"name": "east", "x": 1, "y": 0, "z": 0}]
        got = walkcheck.check_tour(long_wall, dleg, kinds)
        check(len(got) == 1 and "detour" in got[0], f"a walk forty times its distance is reported as a detour (got {got})")
        near = [{"name": "west", "x": -1, "y": 36, "z": 0}, {"name": "east", "x": 1, "y": 36, "z": 0}]
        check(walkcheck.check_tour(long_wall, near, kinds) == [], "a short way round the wall's end is not")

        # a long wall is cut so the shard sends each piece before anyone stands on its far end
        long = {"format": 1, "kind": "scene", "name": "l", "materials": scene["materials"],
                "elements": [{"type": "wall", "part": "wall", "path": [[0, 0], [60, 0]], "thickness": 3, "top": 20,
                              "floor": "stone", "parapet": "none"}], "tour": []}
        sl = fort.build_scene(long, cat)
        reach = max(max(abs(c.x), abs(c.y)) for p in sl["parts"] for c in p["comps"])
        check(len(sl["parts"]) > 1 and reach <= fort.REACH,
              f"every part reaches no further than {fort.REACH} from its centre (got {len(sl['parts'])} parts, {reach})")

        # a causeway with buttresses, reached by a stair that pauses on a landing
        way = {"format": 1, "kind": "scene", "name": "w",
               "materials": {"wall": "stone", "walk": "stone", "floor": "stone", "stairs": "stone"},
               "elements": [
                   {"type": "causeway", "part": "way", "path": [[0, 0], [0, 12]], "width": 3, "z": 20,
                    "floor": "stone", "rail": "stone", "buttress": 4},
                   {"type": "stair", "part": "way", "at": [-1, 22], "rise": "N", "z": 0, "to": 20,
                    "width": 3, "landings": [10]}],
               "tour": [{"name": "foot", "at": [0, 24], "z": 0}, {"name": "top", "at": [0, 6], "z": 20}]}
        sw = fort.build_scene(way, cat)
        xy = {(c.x + sw["parts"][0]["centre"][0], c.y + sw["parts"][0]["centre"][1]) for c in sw["parts"][0]["comps"]}
        check((-3, 4) in xy and (3, 4) in xy, "a buttress stands out from each side of the causeway")
        tour = [(t["name"], t["z"]) for t in sw["tour"]]
        check([z for _, z in tour] == [0, 0, 10, 10, 20, 20],
              f"the climb pauses on the landing (got {tour})")

    print(f"test_multi: {'OK' if not FAILS else 'FAILED'} ({len(FAILS)} failing)")
    return 0 if not FAILS else 1


if __name__ == "__main__":
    raise SystemExit(main())
