"""Tests for tools/multi on synthetic data (no UO client data), for CI. Exit 0/1.

    python tools/multi/test_multi.py

Checks the multi record codecs, piece signatures and roof sides, the
generator against a tiny hand-made catalogue (walls closed, the door a gap
with a hidden marker and a sidecar door, the gable roof's courses and
fill), and the validator catching a gap in a wall and a sealed room.
"""
from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))

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
            "stair": {"N/E": ["0x0501"], "N/EW": ["0x0502"], "N/W": ["0x0503"]},
        },
        "wooden": {"floor": {"NESW": ["0x0601", "0x0602"]}},
        "tile": {"roof": {"N": ["0x0701"], "S": ["0x0702"], "W": ["0x0703"], "E": ["0x0704"],
                          "ridge_x": ["0x0705"], "ridge_y": ["0x0706"]}},
    }
    (folder / "families.json").write_text(json.dumps(fam), encoding="utf-8")
    (folder / "pieces.json").write_text("{}", encoding="utf-8")


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
        check(at[(1, 4, 7)].item == 0x100 + SIGS.index("W") and at[(3, 4, 7)].item == 0x100 + SIGS.index("E"),
              "the wall ends beside the door are end pieces (W, E)")
        check(side["doors"] == [{"x": 0, "y": 2, "z": 7, "storey": 0, "facing": "WestCW", "type": "DarkWoodHouseDoor"}],
              "the sidecar door: centre-relative, WestCW in a wall along x")
        check(sum(1 for c in comps if c.z == 0 and c.item >= 0x400 and c.item < 0x500) == 16,
              "the foundation is a closed 16-piece ring at z 0")
        steps = sorted((c.x + 2, c.y + 2, c.item) for c in comps if c.z == 2)
        check(steps == [(1, 5, 0x501), (2, 5, 0x502), (3, 5, 0x503)], "three steps outside the door: ends and middle")
        roofs = sorted({c.z for c in comps if 0x701 <= c.item <= 0x706})
        check(roofs == [27, 30, 33], f"gable courses 3 z apart from the wall top (got {roofs})")
        check(all(c.x + 2 == 3 for c in comps if c.item == 0x706), "a W=4 house ridges on x = 3 (x 1..5)")
        fill = sorted((c.x + 2, c.y + 2, c.z) for c in comps if c.item == 0x301)
        check(fill == [(2, 4, 27), (3, 4, 27), (3, 4, 30), (4, 4, 27)], f"gable fill on the south end (got {fill})")
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

    print(f"test_multi: {'OK' if not FAILS else 'FAILED'} ({len(FAILS)} failing)")
    return 0 if not FAILS else 1


if __name__ == "__main__":
    raise SystemExit(main())
