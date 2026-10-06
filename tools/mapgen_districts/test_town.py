"""town.py without client data: plans are deterministic, every house stands in its lot facing a
paved cell, the built folder has the district shape compose.py lays on a lot, and a GUO town goes
through compose like any district. Run: python tools/mapgen_districts/test_town.py"""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import compose  # noqa: E402
import town  # noqa: E402
from test_compose import GRASS, write_world  # noqa: E402

CTX = town.gen_cli.Context(user=False)


class TownTest(unittest.TestCase):
    def test_presets_load(self):
        presets = town.load_presets()
        self.assertGreaterEqual(len(presets), 4)
        for name, p in presets.items():
            for key in ("styles", "shapes", "storeys", "roofs", "lot_width", "house_width", "house_depth"):
                self.assertIn(key, p, name)
            self.assertTrue(set(p["styles"]) <= set(CTX.styles), f"{name}: unknown style")

    def test_plan_is_deterministic(self):
        a = town.plan("market_town", 7, ctx=CTX)
        b = town.plan("market_town", 7, ctx=CTX)
        c = town.plan("market_town", 8, ctx=CTX)
        self.assertEqual(json.dumps(a, sort_keys=True), json.dumps(b, sort_keys=True))
        self.assertNotEqual(json.dumps(a, sort_keys=True), json.dumps(c, sort_keys=True))
        self.assertEqual(a["schema"], town.SCHEMA)

    def test_houses_fit_and_face_the_street(self):
        for preset in sorted(town.load_presets()):
            for seed in (1, 2, 3):
                p = town.plan(preset, seed, ctx=CTX)
                houses = [lot for lot in p["lots"] if lot["house"]]
                self.assertGreaterEqual(len(houses), 4, f"{preset} {seed}: {len(houses)} houses")
                paved = set().union(*(town.rect_cells(s["rect"]) for s in p["streets"])) | town.rect_cells(p["plaza"])
                paved |= {tuple(c) for lot in p["lots"] for c in lot.get("path", [])}
                boxes = []
                for lot in houses:
                    comps, side, problems = town.make_house(CTX, lot["house"]["params"])
                    self.assertEqual(problems, [], f"{preset} {seed} {lot['id']}")
                    cx, cy = lot["house"]["centre"]
                    xs, ys = [c.x + cx for c in comps], [c.y + cy for c in comps]
                    x0, y0, x1, y1 = lot["rect"]
                    self.assertTrue(x0 <= min(xs) and max(xs) <= x1 and y0 <= min(ys) and max(ys) <= y1, lot["id"])
                    front = side["stops"][0]
                    self.assertTrue((front["x"] + cx, front["y"] + cy) in paved, f"{preset} {seed} {lot['id']} front")
                    box = town.rect_cells([min(xs), min(ys), max(xs), max(ys)])
                    self.assertFalse(any(box & b for b in boxes), f"{lot['id']} overlaps")
                    self.assertFalse(box & paved, f"{lot['id']} stands on a street or path")
                    boxes.append(box)

    def test_build_and_compose(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            p = town.plan("hamlet", 3, "t", ctx=CTX)
            record = town.build(p, tmp / "town", ctx=CTX)
            self.assertEqual(record["status"], "native-valid", record["problems"])
            self.assertEqual(record["generator"], "guo-town")
            blocks = list((tmp / "town" / "world" / "blocks" / "0").glob("*.json"))
            self.assertEqual(len(blocks), 81)
            scene = json.loads((tmp / "town" / "scene.json").read_text(encoding="utf-8"))
            self.assertEqual(len(scene["parts"]), record["houses"])
            names = [t["name"] for t in scene["tour"]]
            self.assertEqual(names[0], "crossing")
            for part in scene["parts"]:
                self.assertIn(f"{part['name']}_entrance", names)
                self.assertIn(f"{part['name']}_exit", names)
                self.assertTrue((tmp / "town" / "parts" / f"{part['name']}.json").exists())
            # the same plan builds the same files (project.json carries no time)
            town.build(p, tmp / "again", ctx=CTX)
            for f in sorted((tmp / "town").rglob("*.json")):
                self.assertEqual(f.read_bytes(), (tmp / "again" / f.relative_to(tmp / "town")).read_bytes(), f.name)
            with self.assertRaises(town.TownError):
                town.build(p, tmp / "town", ctx=CTX)
            # on a lot of a generated map: every gate's street joins the town's own
            origin, size = (800, 400), 96
            write_world(tmp / "map", origin[0] // 8, origin[1] // 8, size // 8, size // 8, lambda x, y: GRASS)
            gates = [[43, 0], [43, 87], [0, 43], [87, 43]]
            pois = {"width": size, "height": size, "pois": [{"id": 1, "kind": "Town", "footprint": [0, 0, 87, 87], "gates": gates}]}
            placed = compose.compose(tmp / "map", pois, origin, [tmp / "town"], tmp / "built")
            self.assertEqual(placed["street_problems"], [])
            self.assertEqual(placed["towns"][0]["generator"], "guo-town")
            self.assertFalse((tmp / "built" / "world" / "SOURCE-LICENSE.txt").exists())

    def test_balcony_is_reached(self):
        # kit.house opens a door onto the porch's balcony from the storey above the front door
        for style in sorted(CTX.styles):
            p = {"style": style, "seed": 3, "shape": "rect", "width": 12, "depth": 9, "storeys": 2,
                 "roof": "gable", "rooms": 2, "porch": True, "yard": False, "balcony": True}
            comps, side, problems = town.make_house(CTX, p)
            self.assertEqual(problems, [], style)
            self.assertIn("storey1_balcony", [s["name"] for s in side["stops"]], style)

    def test_unknown_preset(self):
        with self.assertRaises(town.TownError):
            town.plan("no_such_town", 1, ctx=CTX)


if __name__ == "__main__":
    unittest.main()
