#!/usr/bin/env python3
"""Towns on a generated map: the map generator picks the lots, GUO's own towns (town.py) fill them.

    python tools/mapgen_districts/run.py build --out DIR [--towns 3] [--town-preset P ...] [--town-seed S]
        [--district D ...] [--size 1024] [--seed 1234567] [--origin 6144 2560] [--lot 88]
    python tools/mapgen_districts/run.py town --out DIR [--preset market_town] [--seed 1]
    python tools/mapgen_districts/run.py stage --out DIR
    python tools/mapgen_districts/run.py prove --out DIR [--min-free-gb 12]

build: one map generator run to find flat lots on dry ground (lot size, on the 8x8 block grid),
then the real run with those lots as fixed z0 town sites (Town Sites + Town Roads, streets off:
bare pads the generator's roads end at), its export as a world project at --origin on facet 0,
and compose.py: each district (72x72) laid on a lot with a street from every gate into its own.
The districts are GUO towns (town.py, presets in towns.json, deterministic from --town-seed) unless
--district names built tools/layout_import folders. Writes DIR/towns (the GUO towns), DIR/map (the
run), DIR/built (the district shape), DIR/radar_towns.png and one crop per town.

town: one GUO town on its own, for a look: DIR/plan.json, the district folder and preview.png.

stage: layout_import's district-stage into DIR/stage (tools/world export of the whole map plus
the districts' multis, read back).

prove: the private shard (tools/editor_shard) and a scripted client walk the tour: for each town,
a step onto a generated road outside a gate, the walk through the gate and up its street, then the
district's own tour (streets, every building, its rooms and stairs). Between towns the staff
account steps by command (recorded as a jump). DIR/proof holds report.json and walk.mp4.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))

from guo import load_config  # noqa: E402

import compose  # noqa: E402
from mapdump import MapDump, pick_lots  # noqa: E402


def mapgen(cfg, *args) -> None:
    cmd = [sys.executable, str(cfg.tools / "mapgen" / "run.py"), *map(str, args)]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode:
        print(r.stdout[-3000:], r.stderr[-3000:])
        raise SystemExit(f"[mapgen_districts] map generator failed: {' '.join(cmd[2:4])}")
    return r.stdout


def town_args(lots, lot):
    sites = ";".join(f"{x},{y},{lot},{lot},0" for x, y, *_ in lots)
    return ["--enable", "Town Sites", "--enable", "Town Roads",
            "--set", f"Town Sites.Sites={sites}", "--set", "Town Sites.TownsPerQuadrant=0",
            "--set", "Town Roads.PaintStreets=false", "--set", "Town Roads.FlattenTarget=poi"]


def cmd_build(cfg, a) -> int:
    out = a.out.resolve()
    if out.exists():
        print(f"[mapgen_districts] {out} exists: build writes a fresh folder")
        return 2
    if a.district:
        districts = [d.resolve() for d in a.district]
    else:
        districts = guo_towns(cfg, a, out / "towns")
        if districts is None:
            return 1
    probe = out / "probe"
    mapgen(cfg, "run", "--out", probe, "--size", a.size, "--seed", a.seed)
    lots = pick_lots(MapDump(probe / "map.bin"), a.lot, len(districts), a.spacing, max_relief=a.max_relief)
    print(f"[mapgen_districts] lots: {lots}")
    if len(lots) < len(districts):
        print(f"[mapgen_districts] only {len(lots)} lots for {len(districts)} districts")
        return 1
    run = out / "map"
    mapgen(cfg, "run", "--out", run, "--size", a.size, "--seed", a.seed, "--step-previews", *town_args(lots, a.lot))
    mapgen(cfg, "export", "--run", run, "--world-project", run / "world", "--origin-x", a.origin[0],
           "--origin-y", a.origin[1])
    pois = json.loads((run / "pois.json").read_text(encoding="utf-8"))
    record = compose.compose(run / "world", pois, tuple(a.origin), districts, out / "built", MapDump(run / "map.bin"))
    import preview
    images = preview.towns(cfg, out, record)
    print(json.dumps({"built": str(out / "built"), "towns": [
        {k: t[k] for k in ("town", "source", "lot", "world_corner", "entry_gate")} |
        {"streets_joined": sum(s["joined"] for s in t["streets"])} for t in record["towns"]],
        "street_problems": record["street_problems"], "images": images}, indent=1))
    return 0


def town_list(a) -> list[tuple[str, int]]:
    """(preset, seed) per town: the presets in turn, each town's seed from --town-seed and its index."""
    import town
    presets = a.town_preset or sorted(town.load_presets())
    return [(presets[i % len(presets)], int(a.town_seed) * 1000 + i + 1) for i in range(a.towns)]


def guo_towns(cfg, a, folder: Path) -> list[Path] | None:
    import town
    out = []
    for i, (preset, seed) in enumerate(town_list(a)):
        plan = town.plan(preset, seed, f"town{i + 1}_{preset}")
        record = town.build(plan, folder / f"t{i + 1}", cfg)
        print(f"[mapgen_districts] town {i + 1}: {preset} seed {seed}, {record['houses']} houses, {record['status']}")
        if record["status"] != "native-valid":
            print("\n".join(record["problems"][:20]))
            return None
        out.append(folder / f"t{i + 1}")
    return out


def cmd_town(cfg, a) -> int:
    import town
    out = a.out.resolve()
    plan = town.plan(a.preset, a.seed)
    record = town.build(plan, out, cfg)
    png = town.preview(cfg, out, out / "preview.png")
    print(json.dumps({"town": record["name"], "status": record["status"], "houses": record["houses"],
                      "problems": record["problems"], "notes": plan["notes"], "preview": str(png)}, indent=1))
    return 0 if record["status"] == "native-valid" else 1


def cmd_stage(cfg, a) -> int:
    from layout_import import deploy
    out = a.out.resolve()
    return deploy.district_stage(cfg, out / "built", out / "stage")


def cmd_prove(cfg, a) -> int:
    sys.path.insert(0, str(cfg.tools / "multi"))
    import hashlib
    import os
    import shutil
    import prove
    out = a.out.resolve()
    built, stage, proof = out / "built", out / "stage", out / "proof"
    district = json.loads((built / "district.json").read_text(encoding="utf-8"))
    scene_built = json.loads((built / "scene.json").read_text(encoding="utf-8"))
    staged = json.loads((stage / "scenes.json").read_text(encoding="utf-8"))[district["name"]]
    parts = [(district["name"] + "." + p["name"], p["id"], p["centre"][0], p["centre"][1], p.get("doors", []))
             for p in staged["parts"]]
    # the recorder stops at 15 minutes: each town walks its arrival and the first --town-stops of its tour
    stops, kept = [], {}
    for t in scene_built["tour"]:
        town = t["name"].split("_", 1)[0]
        kept[town] = kept.get(town, 0) + 1
        if not a.town_stops or kept[town] <= a.town_stops + 3:
            stops.append((t["name"], t["x"], t["y"], t["z"]))
    jumps = scene_built.get("jumps", {})
    h = hashlib.sha256()
    for path in sorted(stage.glob("*.mul")) + sorted(stage.glob("*.uop")):
        h.update(path.name.encode())
        h.update(path.read_bytes())
    shard_name = "GUO-Towns-" + h.hexdigest()[:12]
    cache_root = Path(os.environ.get("ProgramData") or Path.home()).resolve()
    cache = cache_root / shard_name
    made = not cache.exists()
    proof.mkdir(parents=True, exist_ok=True)
    try:
        rc = prove.session(cfg, stage, proof, parts, (*district["origin"], 0), stops, proof / "walk.mp4",
                           a.min_free_gb, "GUO: a generated map with " + ("GUO's own towns" if all(t.get("generator") == "guo-town" for t in district["towns"]) else "towns from imported layouts"),
                           {"towns": [{k: t[k] for k in ("town", "source", "world_corner")} for t in district["towns"]]},
                           shard_env={"GUO_BRIDGE_SHARD": shard_name},
                           profile={"use_custom_light_level": True, "light_level": 0, "light_level_type": 0},
                           jumps=jumps)
    finally:
        if made and cache.parent == cache_root and cache.name == shard_name:
            shutil.rmtree(cache, ignore_errors=True)
    return rc


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("build")
    b.add_argument("--out", type=Path, required=True)
    b.add_argument("--district", type=Path, action="append", help="built tools/layout_import districts instead of GUO towns")
    b.add_argument("--towns", type=int, default=3, help="GUO towns to generate (without --district)")
    b.add_argument("--town-preset", action="append", help="towns.json presets, used in turn (default: all)")
    b.add_argument("--town-seed", default="1")
    b.add_argument("--size", type=int, default=1024)
    b.add_argument("--seed", default="1234567")
    b.add_argument("--origin", type=int, nargs=2, default=[6144, 2560])
    b.add_argument("--lot", type=int, default=88)
    b.add_argument("--spacing", type=int, default=180)
    b.add_argument("--max-relief", type=int, default=8)
    t = sub.add_parser("town")
    t.add_argument("--out", type=Path, required=True)
    t.add_argument("--preset", default="market_town")
    t.add_argument("--seed", type=int, default=1)
    for name in ("stage", "prove"):
        s = sub.add_parser(name)
        s.add_argument("--out", type=Path, required=True)
        if name == "prove":
            s.add_argument("--min-free-gb", type=float, default=12)
            s.add_argument("--town-stops", type=int, default=55, help="district tour stops per town (0: all)")
    a = p.parse_args(argv)
    cfg = load_config()
    if a.cmd == "build" and (a.origin[0] % 8 or a.origin[1] % 8):
        p.error("--origin must be on the 8x8 block grid")
    return {"build": cmd_build, "town": cmd_town, "stage": cmd_stage, "prove": cmd_prove}[a.cmd](cfg, a)


if __name__ == "__main__":
    raise SystemExit(main())
