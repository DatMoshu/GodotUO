"""compose.py on a synthetic map and district: blocks move to the lot, gate streets join, the
scene's parts and tour shift to map cells. Run: python tools/mapgen_districts/test_compose.py"""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import compose  # noqa: E402

GRASS, ROAD = 0x0003, 0x03E9


def write_world(root: Path, bx0: int, by0: int, nbx: int, nby: int, land_of, statics_of=lambda x, y: []):
    for by in range(by0, by0 + nby):
        for bx in range(bx0, bx0 + nbx):
            land = [" ".join(f"{land_of(bx * 8 + c, by * 8 + r):04X}:0" for c in range(8)) for r in range(8)]
            statics = [s for r in range(8) for c in range(8) for s in statics_of(bx * 8 + c, by * 8 + r)]
            compose.write_block(compose.block_path(root, bx, by),
                                {"format": 1, "facet": 0, "block": [bx, by], "land": land, "statics": statics})


class ComposeTest(unittest.TestCase):
    def test_district_on_a_lot(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            origin = (800, 400)                      # world corner of the generated map
            size = 96                                # one 88 lot fits
            write_world(tmp / "map", origin[0] // 8, origin[1] // 8, size // 8, size // 8, lambda x, y: GRASS)
            # a 72x72 district at world 6144,2304: a road cross through its middle, one building
            # part in the north-west quarter, a static tree on the cross's west arm
            d = tmp / "district"
            dox, doy = 6144, 2304

            def dland(x, y):
                lx, ly = x - dox, y - doy
                return ROAD if lx in (35, 36) or ly in (35, 36) else GRASS

            def dstatics(x, y):
                lx, ly = x - dox, y - doy
                return [{"id": "0x0CCA", "x": x % 8, "y": y % 8, "z": 0, "hue": "0x0000"}] if (lx, ly) == (10, 35) else []
            write_world(d / "world", dox // 8, doy // 8, 9, 9, dland, dstatics)
            (d / "world" / "project.json").write_text(json.dumps({"format": 1, "name": "x"}), encoding="utf-8")
            (d / "parts").mkdir()
            (d / "parts" / "house.json").write_text(json.dumps([[0x0001, dx, dy, 0] for dx in range(-3, 4) for dy in range(-3, 4)]), encoding="utf-8")
            (d / "scene.json").write_text(json.dumps({"parts": [{"name": "house", "centre": [12, 12], "bounds": [0, 0, 23, 23], "doors": []}],
                                                      "tour": [{"name": "public_street_start", "x": 36, "y": 36, "z": 0}]}), encoding="utf-8")
            (d / "district.json").write_text(json.dumps({"name": "x", "origin": [dox, doy], "status": "native-valid",
                                                         "theme": {"floor_z": 7}, "land_library": {"road": [ROAD]}}), encoding="utf-8")
            lot = [0, 0, 87, 87]
            gates = [[43, 0], [43, 87], [0, 43], [87, 43]]
            pois = {"width": size, "height": size, "pois": [{"id": 1, "kind": "Town", "footprint": lot, "gates": gates}]}
            record = compose.compose(tmp / "map", pois, origin, [d], tmp / "built")

            town = record["towns"][0]
            self.assertEqual(town["world_corner"], [origin[0] + 8, origin[1] + 8])
            self.assertTrue(all(s["joined"] for s in town["streets"]), town["streets"])
            self.assertEqual(record["street_problems"], [])
            world = compose.World(tmp / "built" / "world")
            # the cross moved with the district
            self.assertEqual(world.land(origin[0] + 8 + 35, origin[1] + 8 + 3)[0], ROAD)
            # every gate cell, and the apron in front of the west gate, is road now
            for gx, gy in gates:
                self.assertEqual(world.land(origin[0] + gx, origin[1] + gy)[0], ROAD)
            self.assertEqual(world.land(origin[0] + 4, origin[1] + 43)[0], ROAD)
            # the west street had to go round the tree: the tree stays
            self.assertTrue(world.statics_at(origin[0] + 8 + 10, origin[1] + 8 + 35))
            scene = json.loads((tmp / "built" / "scene.json").read_text(encoding="utf-8"))
            self.assertEqual(scene["parts"][0]["name"], "t1_house")
            self.assertEqual(scene["parts"][0]["centre"], [20, 20])
            names = [t["name"] for t in scene["tour"]]
            self.assertEqual(names[:2], ["t1_road_approach", "t1_gate"])
            self.assertIn("t1_public_street_start", names)
            self.assertEqual(scene["jumps"], {"t1_road_approach": "xy"})
            self.assertTrue((tmp / "built" / "parts" / "t1_house.json").exists())
            # a second compose into the same folder is refused
            with self.assertRaises(ValueError):
                compose.compose(tmp / "map", pois, origin, [d], tmp / "built")

    def test_lot_too_small(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            (tmp / "map" / "blocks").mkdir(parents=True)
            d = tmp / "d"
            (d / "world").mkdir(parents=True)
            (d / "world" / "project.json").write_text("{}", encoding="utf-8")
            (d / "district.json").write_text(json.dumps({"theme": {}}), encoding="utf-8")
            pois = {"width": 96, "height": 96, "pois": [{"id": 1, "kind": "Town", "footprint": [0, 0, 79, 79], "gates": []}]}
            with self.assertRaises(ValueError):
                compose.compose(tmp / "map", pois, (800, 400), [d], tmp / "built")


if __name__ == "__main__":
    unittest.main()
