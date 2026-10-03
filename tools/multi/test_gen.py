"""Synthetic tests for the style catalogue, the generators, rotate/mirror and the legacy formats
(no client data). Called from test_multi.main; run alone with `python tools/multi/test_gen.py`."""
from __future__ import annotations

import io
import json
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))

import formats  # noqa: E402
import gen_cli  # noqa: E402
import generate  # noqa: E402
import kit  # noqa: E402
import orient  # noqa: E402
import styles as S  # noqa: E402
import validate  # noqa: E402
from multifile import Component  # noqa: E402

FAKE = {
    "a": {
        "name": "Test A", "material": "a", "hues": [0], "z": {"floor": 7, "storey": 20, "wall": 19},
        "walls": {"height": 19, "EW": "0x0101", "NS": "0x0102", "post": "0x0103", "corners": {"SE": "0x0104", "NE": "0x0105", "SW": "0x0106"}},
        "windows": [{"EW": "0x0111", "NS": "0x0112"}, {"EW": "0x0113", "NS": "0x0114"}],
        "doors": {"wood": {"EW": {"closed": "0x0201", "open": "0x0202"}, "NS": {"closed": "0x0209", "open": "0x020a"}}},
        "floors": [{"id": "0x0301", "weight": 3}, {"id": "0x0302", "weight": 1}],
        "foundation": {"height": 5, "EW": "0x0121", "NS": "0x0122", "post": "0x0123"},
        "parapet": {"height": 3, "EW": "0x0131", "NS": "0x0132", "post": "0x0133"},
        "gable_fill": {"height": 3, "EW": "0x0141", "NS": "0x0142"},
        "roof": {"gable": {"N": "0x0401", "S": "0x0402", "E": "0x0403", "W": "0x0404", "ridge_x": "0x0405", "ridge_y": "0x0406"},
                 "hip": {"NW": "0x0411", "NE": "0x0412", "SW": "0x0413", "SE": "0x0414", "cap": "0x0415"}, "flat": "0x0301"},
        "stairs": {"straight": {"N": "0x0501", "E": "0x0502", "S": "0x0503", "W": "0x0504"}, "block": "0x0510", "ladder": "0x0520"},
    },
}


def fake_cat() -> S.StyleCatalogue:
    return S.StyleCatalogue(json.loads(json.dumps(FAKE)))


def run(check) -> None:
    # --- styles
    cat = fake_cat()
    print("     style notes:", S.check_style("a", FAKE["a"]))
    check(S.check_style("a", FAKE["a"]) == [f"stairs.turned: none (a turn uses the straight flights and a landing)"],
          "a complete style has only the optional turned-stair note")
    check(any("windows" in n for n in S.check_style("x", {"walls": {"EW": "0x1", "NS": "0x2"}})), "a style missing roles is noted")
    check(S.load_file(HERE / "styles" / "default.json") and
          all(not S.check_style(k, v) or "roof" not in " ".join(S.check_style(k, v)) for k, v in S.load_file(HERE / "styles" / "default.json").items()),
          "the committed default styles load and have roofs")
    check(int(cat.wall("a", 19, "NW")) == 0x104 and int(cat.wall("a", 19, "SW")) == 0x105 and int(cat.wall("a", 19, "NE")) == 0x106, "corner pieces by position: SE 0x104, NE 0x105, SW 0x106")
    check(int(cat.wall("a", 19, "ES")) == 0x103 and int(cat.wall("a", 19, "EW")) == 0x101 and int(cat.wall("a", 19, "NS")) == 0x102,
          "post at the back corner, EW and NS straights")
    check(int(cat.wall("a", 19, "NW")) == 0x104 and int(cat.wall("a", 19, "SW")) == 0x106 or int(cat.wall("a", 19, "SW")) == 0x105,
          "front and side corner pieces by neighbours")
    check(cat.door("a", "NS", "wood") == 0x209 and cat.door_pair("a", "EW", "wood") == (0x201, 0x202), "doors by facing, with the open id")
    ids = cat.floor("a")
    check(len(ids) == 2 and ids.weights == [3, 1], "floors carry weights")
    picks = [generate.scatter(ids, x, y) for x in range(20) for y in range(20)]
    check(0.6 < picks.count(0x301) / len(picks) < 0.9, "weighted floors scatter by weight")

    # --- autowall
    p = {"style": "a", "path": [[0, 0], [9, 0], [9, 6]], "window_every": 3, "door": {"index": "middle"}}
    r = kit.autowall(cat, p)
    items = {(c[1], c[2]): c[0] for c in r["components"]}
    check(items[(0, 0)] == 0x101 and items[(9, 0)] == 0x105, "autowall: a run end takes the straight, the NE corner piece at the bend")
    check(len(r["windows"]) >= 3 and all(items[tuple(w)] in (0x111, 0x112) for w in r["windows"]), "autowall: windows every N cells")
    check(len(r["doors"]) == 1 and r["doors"][0]["closed"] == 0x201 and r["doors"][0]["open"] == 0x202, "autowall: a door with open and closed ids")
    d = r["doors"][0]
    check((d["x"], d["y"]) not in {tuple(w) for w in r["windows"]} and not any(abs(w[0] - d["x"]) + abs(w[1] - d["y"]) == 1 for w in r["windows"]),
          "autowall: no window beside the door")
    check(json.dumps(kit.autowall(cat, p)["components"]) == json.dumps(r["components"]), "autowall: deterministic")
    r2 = kit.autowall(cat, {"style": "a", "path": [[0, 0], [8, 0], [8, 8], [0, 8]], "closed": True, "storeys": 2, "door": False})
    check({c[3] for c in r2["components"]} == {7, 27} and not r2["doors"], "autowall: a closed ring of two storeys")
    check(kit.expand_path([[0, 0], [3, 2]])[0][-1] == (3, 2), "a diagonal path becomes a 4-connected run")

    # --- roof
    for kind in ("gable", "hip", "flat"):
        rr = kit.roof(cat, {"style": "a", "kind": kind, "box": [0, 0, 8, 6], "z": 27, "parapet": kind == "flat"})
        zs = sorted({c[3] for c in rr["components"]})
        check(bool(rr["components"]) and zs[0] == 27, f"roof {kind}: built from z 27")
        if kind != "flat":
            check(all((b - a) % 3 == 0 for a, b in zip(zs, zs[1:])), "roof: courses 3 z apart")
    g = kit.roof(cat, {"style": "a", "kind": "gable", "box": [0, 0, 8, 6], "z": 27, "ridge": "y"})
    check(sorted({c[3] for c in g["components"]}) == [27, 30, 33, 36, 39], "gable: z in steps of 3 to the ridge")
    check(any(c[0] == 0x141 for c in g["components"]) or any(c[0] == 0x142 for c in g["components"]), "gable: gable fill")
    odd = kit.roof(cat, {"style": "a", "kind": "gable", "box": [0, 0, 7, 6], "z": 27, "ridge": "y"})
    check(bool(odd["notes"]), "gable: an odd box is widened, with a note")
    h = kit.roof(cat, {"style": "a", "kind": "hip", "box": [0, 0, 8, 6], "z": 27})
    check(any(c[0] == 0x411 for c in h["components"]) and any(c[0] == 0x405 for c in h["components"]), "hip: corner pieces and a ridge")

    # --- stairs
    s = kit.stairs(cat, {"style": "a", "at": [2, 2], "rise": "S", "z": 7, "open": [1, 1, 6, 9], "above": {"box": [1, 1, 6, 9]}})
    check(sorted(c[3] for c in s["components"] if c[0] == 0x503) == [7, 12, 17, 22], "stairs: four steps, 5 z each")
    check(len(s["holes"]) == 5 and s["arrive"] == [[2, 7]], "stairs: a hole of 5 cells, arrival beyond the landing")
    up = {(c[1], c[2]) for c in s["components"] if c[3] == 27}
    check(not up & {tuple(c) for c in s["holes"]} and (3, 3) in up, "stairs: the floor above is cut over the flight")
    t = kit.stairs(cat, {"style": "a", "at": [2, 2], "rise": "S", "kind": "turned", "z": 7})
    check(len(t["holes"]) == 6 and t["arrive"][0][0] == 6, "stairs: a turned flight arrives to the side")
    check(sorted({c[3] for c in t["components"] if c[0] in (0x501, 0x502, 0x503, 0x504)}) == [7, 12, 17, 22], "turned stairs rise 5 z a step")
    lad = kit.stairs(cat, {"style": "a", "at": [2, 2], "rise": "S", "kind": "ladder"})
    check(lad["components"][0][0] == 0x520 and len(lad["holes"]) == 1, "stairs: a ladder is one piece and a one-cell hole")
    try:
        kit.stairs(cat, {"style": "a", "at": [2, 2], "rise": "S", "open": [1, 1, 3, 3]})
        check(False, "stairs: a flight off the open floor is refused")
    except generate.DescriptionError:
        check(True, "stairs: a flight off the open floor is refused")

    # --- house
    for shape in kit.SHAPES:
        for roof in ("gable", "hip", "flat"):
            for storeys in (1, 2):
                res = kit.house(fake_cat(), {"style": "a", "seed": 4, "shape": shape, "width": 14, "depth": 12, "storeys": storeys,
                                            "roof": roof, "rooms": 4})
                if res["problems"]:
                    check(False, f"house {shape}/{roof}/{storeys}: {res['problems'][:2]}")
                    break
        else:
            check(True, f"house {shape}: validates with gable, hip and flat roofs, 1 and 2 storeys")
    params = {"style": "a", "seed": 11, "shape": "T", "width": 16, "depth": 14, "storeys": 2, "rooms": 5, "balcony": True}
    a1, a2 = kit.house(fake_cat(), params), kit.house(fake_cat(), params)
    check(a1["components"] == a2["components"], "house: the same seed gives the same bytes")
    others = [kit.house(fake_cat(), dict(params, seed=s))["components"] for s in range(12, 18)]
    check(len({json.dumps(o) for o in others}) > 1, "house: reroll by seed changes the house")
    t0 = time.perf_counter()
    for sd in range(20):
        kit.house(fake_cat(), {"style": "a", "seed": sd, "shape": "L", "width": 14, "depth": 12, "storeys": 2})
    per = (time.perf_counter() - t0) * 1000 / 20
    check(per < 200, f"house: a typical two-storey L generates in {per:.0f} ms (limit 200)")
    print(f"     timing: house generation {per:.1f} ms mean over 20 seeds")
    big = kit.house(fake_cat(), {"style": "a", "seed": 2, "shape": "U", "width": 24, "depth": 20, "storeys": 3, "rooms": 8, "yard": True})
    check(big["ms"] < 200 and not big["problems"], f"house: a 24 x 20 three-storey U with a yard in {big['ms']:.0f} ms, valid {big['problems'][:2]}")
    check(bool(big["doors"]) and any(d["storey"] is None for d in big["doors"]) , "house: a gate and doors in the sidecar")
    rooms = kit.house(fake_cat(), {"style": "a", "seed": 3, "width": 16, "depth": 12, "rooms": 5})
    check(len(rooms["description"]["storeys"][0].get("partitions", [])) >= 3, "house: the room split makes partitions")

    # --- validator extensions
    comps, side = generate.build(rooms["description"], fake_cat())
    roofless = [c for c in comps if c.z < side["roof_z"]]
    check(any("open to the sky" in q for q in validate.validate(roofless, side, None)), "validate: a hole in the roof is caught")
    two = kit.house(fake_cat(), {"style": "a", "seed": 6, "width": 14, "depth": 12, "storeys": 2})
    c2, s2 = generate.build(two["description"], fake_cat())
    bad = json.loads(json.dumps(s2))
    bad["local"]["stairs"][0]["arrive"] = [0, 0]
    check(any("arrives" in q for q in validate.validate(c2, bad, None)), "validate: a stair arriving off the floor is caught")
    bad = json.loads(json.dumps(s2))
    bad["local"]["storeys"][1]["floor"] = sorted(set(map(tuple, bad["local"]["storeys"][1]["floor"])) | set(map(tuple, bad["local"]["stairs"][0]["cells"])))
    check(any("not cut open" in q for q in validate.validate(c2, bad, None)), "validate: an uncut floor above a stair is caught")
    bad = json.loads(json.dumps(s2))
    d0 = bad["local"]["storeys"][0]["doors"][0]
    bad["local"]["storeys"][0]["walls"] = sorted(set(map(tuple, bad["local"]["storeys"][0]["walls"])) | {(d0[0], d0[1] + 1), (d0[0], d0[1] - 1), (d0[0] + 1, d0[1]), (d0[0] - 1, d0[1])})
    check(any("nowhere to stand" in q for q in validate.validate(c2, bad, None)), "validate: a door walled in on both sides is caught")

    # --- rotate and mirror
    table = orient.table_from_styles(FAKE)
    cot = gen_cli.Context(None, [], user=False)
    cot._styles = json.loads(json.dumps(FAKE))
    house_c = [Component(*c[:4], len(c) < 5) for c in kit.house(fake_cat(), {"style": "a", "seed": 9, "width": 12, "depth": 10, "storeys": 2})["components"]]
    for op in ("rot90", "rot180", "rot270", "mirror_x", "mirror_y"):
        out = orient.transform(house_c, op, table)
        check(len(out) >= len(house_c) * 0.9 and all(isinstance(c.item, int) for c in out), f"{op}: components come out")
    norm = lambda cs: sorted((c.item, c.x, c.y, c.z) for c in cs)
    walls_only = [c for c in house_c if c.item in (0x101, 0x102) and c.z == 7]
    wx = orient.transform(orient.transform(walls_only, "mirror_x", table), "mirror_x", table)
    check(norm(wx) == norm(walls_only), "mirror_x twice returns the walls to their cells")
    wy = orient.transform(orient.transform(walls_only, "mirror_y", table), "mirror_y", table)
    check(norm(wy) == norm(walls_only), "mirror_y twice returns the walls to their cells")
    rr = orient.transform(orient.transform(orient.transform(orient.transform(walls_only, "rot90", table), "rot90", table), "rot90", table), "rot90", table)
    check(norm(rr) == norm(walls_only), "four quarter turns return the walls to their cells")
    ew = [Component(0x101, 3, 0, 7)]
    check([(c.item, c.x, c.y) for c in orient.transform(ew, "rot90", table)] == [(0x102, -2, 3)], "an EW wall turns into an NS wall one clockwise quarter on")
    roofs = [Component(0x401, 0, 0, 27), Component(0x403, 1, 0, 27)]
    check([c.item for c in orient.transform(roofs, "mirror_x", table)] == [0x401, 0x404] , "a mirror swaps the E and W roof slopes")
    check([c.item for c in orient.transform([Component(0x401, 0, 0, 27)], "rot90", table)] == [0x403], "a quarter turn takes the N slope to E")
    st = [Component(0x503, 0, 0, 7)]
    check(orient.transform(st, "rot90", table)[0].item == 0x504, "a quarter turn takes the S stair to W")
    door = [Component(0x201, 2, 0, 7, False)]
    t90 = orient.transform(door, "rot90", table)[0]
    check(t90.item == 0x20f and not t90.visible, "a door turns to the other wall axis (WestCW to NorthCW), hidden stays hidden")
    check(orient.transform(orient.transform(door, "rot90", table), "rot270", table)[0].item == 0x201, "a door survives a turn and back")
    fam_table = orient.build_table({"m": {"wall": {"20": {"EW": ["0x0010"], "NS": ["0x0011"], "NW": ["0x0012"], "ES": ["0x0013"]}},
                                          "roof": {"N": ["0x0020"], "E": ["0x0021"], "S": ["0x0022"], "W": ["0x0023"]}}}, {},
                                   {0x30: "rug east", 0x31: "rug south", 0x32: "rug west", 0x33: "rug north"})
    check(fam_table["wall"]["0x0012"]["axis"] == "EWNS" and fam_table["wall"]["0x0013"]["axis"] == "post", "table from a catalogue: axes by signature")
    check(fam_table["map"]["rot90"]["0x0020"] == "0x0021" and fam_table["map"]["mirror_y"]["0x0020"] == "0x0022", "table: roof sides turn and mirror")
    check(fam_table["map"]["rot90"]["0x0030"] == "0x0031" and fam_table["map"]["mirror_x"]["0x0030"] == "0x0032", "table: tiledata names ('rug east') turn")
    vcorner = orient.transform([Component(0x12, 0, 0, 7)], "mirror_x", fam_table)
    check(sorted(c.item for c in vcorner) == [0x10, 0x11], "a corner drawing both faces splits into its straights when mirrored")

    # --- formats
    tiles = [formats.Tile(0x203, -3, -3, 7, True, 0x44), formats.Tile(0x6A5, 0, 3, 7, False, 0), formats.Tile(0x1, 5, -2, -1, True, 0)]
    for fmt in formats.FORMATS:
        data = formats.write(fmt, tiles)
        back = formats.read(fmt, data)
        hue_ok = fmt in ("wsc", "centred", "uoab")
        same = [(t.item, t.x, t.y, t.z, t.visible if fmt not in ("wsc", "uox3", "uoab") else True, t.hue if hue_ok else 0) for t in tiles]
        got = [(t.item, t.x, t.y, t.z, t.visible, t.hue) for t in back]
        check(got == same, f"format {fmt}: round-trips" + (" (hue kept)" if hue_ok else ""))
        check(formats.detect("x" + {"txt": ".txt", "uoa": ".uoa", "wsc": ".wsc", "uox3": ".dfn"}.get(fmt, ".bin"), data[:512]) in
              (fmt, "txt") or fmt in ("csv-punt", "csv-swerv", "centred", "uoab"), f"format {fmt}: detected")
    two_designs = formats.write_uoab([formats.Design("a", "b", "c", tiles), formats.Design("d", "e", "f", tiles[:1])])
    ds = formats.read_uoab(two_designs)
    check(len(ds) == 2 and ds[0].name == "a" and ds[1].category == "e", "uoab version 2 holds several designs")
    check(formats.read_txt("203 1 2 3\n0x6a5 0 0 0 0\n")[0].item == 203 and not formats.read_txt("203 1 2 3\n0x6a5 0 0 0 0\n")[1].visible,
          "txt reads decimal ids and flags")
    try:
        formats.read_uoa("6 version\n1 template id\n-1 item version\n3 num components\n1 0 0 0 1\n")
        check(False, "uoa: a wrong component count is refused")
    except formats.FormatError:
        check(True, "uoa: a wrong component count is refused")
    check(formats.detect("a.csv", b"TileID,OffsetX,OffsetY,OffsetZ,Flag,Cliloc\n") == "csv-swerv", "csv: the SwervUO header is told from Punt's")
    rt = gen_cli.run_op(cot, "import", {"data": formats.write("uoa", tiles).decode(), "name": "x.uoa"})
    check(rt["format"] == "uoa" and len(rt["components"]) == 3, "import through the JSON op")
    ex = gen_cli.run_op(cot, "export", {"components": rt["components"], "format": "wsc"})
    check("WORLDITEM" in ex["data"], "export through the JSON op")

    # --- JSON ops and serve
    out = io.StringIO()
    reqs = [{"op": "house", "style": "a", "seed": 1, "id": 1}, {"op": "rotate", "components": [[0x101, 1, 0, 7]], "turns": 1},
            {"op": "styles"}, {"op": "nonsense"}, {"op": "house", "style": "missing"}]
    gen_cli.serve(cot, io.StringIO("\n".join(json.dumps(r) for r in reqs) + "\n"), out)
    ans = [json.loads(ln) for ln in out.getvalue().splitlines()]
    check(len(ans) == 5 and "components" in ans[0] and ans[0]["id"] == 1 and "error" in ans[3] and "error" in ans[4], "serve: one answer per request, errors as data")
    check(ans[1]["components"][0][0] == 0x102, "serve: rotate through the op")


def main() -> int:
    fails = []

    def check(ok, what):
        print(f"{'ok  ' if ok else 'FAIL'} {what}")
        if not ok:
            fails.append(what)
    run(check)
    print(f"test_gen: {'OK' if not fails else 'FAILED'} ({len(fails)} failing)")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
