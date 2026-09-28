"""Authoring multis: mine the client's buildings, describe, build, validate, write, place, prove.

    python tools/multi/run.py mine    [--out DIR] [--facet 0] [--no-statics]
    python tools/multi/run.py sheets  [--catalogue DIR]
    python tools/multi/run.py build   DESC.json [--out DIR]     generate, validate, preview
    python tools/multi/run.py write   NAME [--stage DIR]       a built multi into the staged set
    python tools/multi/run.py prove   NAME [--stage DIR] [--clip OUT.mp4] [--at X Y [Z]] [--visit X Y Z ...]
    python tools/multi/run.py show    ID [--data DIR] [--png FILE]
    python tools/multi/run.py storeys DESC.json --project DIR   raise storeys on map buildings, as a world project
    python tools/multi/run.py scene-build SCENE.json [--cut Z]  a scene: every element, cut into parts
    python tools/multi/run.py scene-write NAME [--stage DIR]    each part as a multi, and the scene
    python tools/multi/run.py scene-prove NAME [--stage DIR] [--clip OUT.mp4] [--at X Y [Z]]

The catalogue (default build/multi/catalogue) is derived from client data and
never committed. Nothing here writes into the UO install.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from guo import load_config  # noqa: E402


def cmd_mine(cfg, a) -> int:
    import mine
    out = a.out or cfg.build / "multi" / "catalogue"
    t = time.time()
    summary = mine.mine(cfg.client_data, out, facet=a.facet, statics=not a.no_statics)
    print(json.dumps(summary, indent=1))
    print(f"[multi] catalogue: {out} ({time.time() - t:.0f}s)")
    return 0


def cmd_sheets(cfg, a) -> int:
    """Contact sheets: one per multi kind (distinct designs only), one per wall material."""
    import render
    from multifile import Component, Multis
    cat = a.catalogue or cfg.build / "multi" / "catalogue"
    multis = json.loads((cat / "multis.json").read_text(encoding="utf-8"))
    fams = json.loads((cat / "families.json").read_text(encoding="utf-8"))
    src = Multis(cfg.client_data)
    out = cat / "sheets"
    by_kind: dict[str, list] = {}
    seen = set()
    for m in multis:
        key = (m["kind"], m["components"], tuple(m["size"]))
        if key in seen or m["kind"] == "marker":
            continue
        seen.add(key)
        by_kind.setdefault(m["kind"], []).append(m)
    for kind, ms in by_kind.items():
        imgs = [(f"{m['id']} {m['size'][0]}x{m['size'][1]} s{len(m['storeys'])}",
                 render.render(src.get(int(m["id"], 16)), cfg.client_data)) for m in ms[:48]]
        render.sheet(imgs, out / f"multis_{kind}.png")
        print(f"[multi] {kind}: {len(ms)} distinct, sheet of {len(imgs)}")
    top = sorted(fams, key=lambda k: -sum(len(v) for r in fams[k].values() for v in r.values()))[:24]
    for mat in top:
        imgs = []
        for r, faces in fams[mat].items():
            for face, ids in faces.items():
                for i in ids[:6]:
                    imgs.append((f"{r}/{face} {i}", render.render([Component(int(i, 16), 0, 0, 0)], cfg.client_data)))
        render.sheet(imgs[:60], out / f"family_{mat.replace(' ', '_')}.png", cols=10, tile=110)
    print(f"[multi] sheets: {out}")
    return 0


def cmd_build(cfg, a) -> int:
    import generate
    import render
    import validate
    desc = json.loads(a.desc.read_text(encoding="utf-8"))
    out = a.out or cfg.build / "multi" / "built" / desc["name"]
    cat = generate.Catalogue(a.catalogue or cfg.build / "multi" / "catalogue")
    try:
        comps, side = generate.build(desc, cat)
    except generate.DescriptionError as e:
        print(f"[multi] {a.desc.name}: {e}")
        return 1
    problems = validate.validate(comps, side, cfg.client_data)
    side["valid"] = not problems
    side["problems"] = problems
    out.mkdir(parents=True, exist_ok=True)
    (out / "components.json").write_text(json.dumps([c.as_list() for c in comps]), encoding="utf-8")
    (out / "multi.json").write_text(json.dumps(side, indent=1), encoding="utf-8")
    render.render(comps, cfg.client_data, out / "preview.png")
    for n, z in enumerate(side["storeys"]):
        render.plan(comps, cfg.client_data, z - 7 if n == 0 else z, z + 1).save(out / f"plan_{n}.png")
    if side.get("roof_z") is not None:
        render.render(comps, cfg.client_data, out / "preview_noroof.png", max_z=side["roof_z"] - 1)
    print(f"[multi] {desc['name']}: {len(comps)} components, {len(side['doors'])} door(s), storeys {side['storeys']}")
    for p in problems:
        print(f"[multi]   PROBLEM {p}")
    print(f"[multi] {'valid' if not problems else 'NOT valid'}: {out}")
    return 0 if not problems else 1


def cmd_write(cfg, a) -> int:
    """Writes a built (and valid) multi into the staged set, in the multi pack's range (ADR-0022)."""
    from multifile import Component
    built = a.built or cfg.build / "multi" / "built" / a.name
    side = json.loads((built / "multi.json").read_text(encoding="utf-8"))
    if not side.get("valid"):
        print(f"[multi] {a.name} did not validate; build it again and fix: {side.get('problems')}")
        return 1
    comps = [Component(c[0], c[1], c[2], c[3], len(c) < 5) for c in
             json.loads((built / "components.json").read_text(encoding="utf-8"))]
    mid, ok = write_multi(cfg, a.stage, a.ranges, a.name, comps, side)
    if mid is not None and ok:
        side["multi_id"] = mid
        (built / "multi.json").write_text(json.dumps(side, indent=1), encoding="utf-8")
    return 0 if ok else 1


def write_multi(cfg, stage_dir, ranges, name: str, comps, side: dict) -> tuple[int | None, bool]:
    """One multi into the stage under `name` (its id taken from the multi pack), with its
    multis.json entry. The same name with the same components only refreshes the entry."""
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "uodata_write"))
    import uodata as U
    from guo.uorecord import AssetRecord
    from multifile import Multis, encode_uop
    stage = U.Stage(stage_dir or cfg.build / "uodata" / "multi", cfg.client_data)
    reg = U.Registry(stage, U.load_policy(ranges))
    if "multi" not in reg.data["packs"]:
        reg.reserve("multi", "multi", U.free_multis(stage), 256)
    used = reg.data["packs"]["multi"].get("used", {}).get("multi", {})
    placed = stage.root / "multis.json"
    index = json.loads(placed.read_text(encoding="utf-8")) if placed.exists() else {}
    if name in used:
        mid = used[name]
        if Multis(stage.root).get(mid) != comps:
            print(f"[multi] {name} is already multi {mid:#06x} in this stage with other components (the writer "
                  f"only adds; build it under a new name, or start a new stage)")
            return mid, False
        # The same components: only the sidecar (doors, say) is refreshed.
        index[name] = placed_entry(mid, side, comps)
        placed.write_text(json.dumps(index, indent=1), encoding="utf-8")
        print(f"[multi] {name} is multi {mid:#06x} already, components equal; its sidecar is refreshed")
        return mid, True
    mid = reg.take("multi", "multi", name)
    rec = AssetRecord("multi", mid, encode_uop(mid, comps))
    for line in U.write_records(stage, [rec]):
        print(f"[multi] wrote {line} into {stage.root}")
    same = U.read_back_equal(stage, rec)
    back = Multis(stage.root).get(mid) if (stage.root / "MultiCollection.uop").exists() else None
    same_comps = back == comps
    changed = stage.check_install_unchanged()
    index[name] = placed_entry(mid, side, comps)
    placed.write_text(json.dumps(index, indent=1), encoding="utf-8")
    print(f"[multi] {name} = multi {mid:#06x}: read back {'equal' if same else 'DIFFERENT'}, "
          f"components {'equal' if same_comps else 'DIFFERENT'} ({len(comps)}), install unchanged: {not changed}")
    return mid, same and same_comps and not changed


def placed_entry(mid: int, side: dict, comps) -> dict:
    """The stage's multis.json entry: what placing and proving a multi needs."""
    xs, ys = [c.x for c in comps], [c.y for c in comps]
    return {"id": mid, "doors": side.get("doors", []), "size": side.get("size"), "storeys": side.get("storeys", []),
            "bounds": [min(xs), min(ys), max(xs), max(ys)], "stops": side.get("stops", [])}


def cmd_prove(cfg, a) -> int:
    import prove
    stage = (a.stage or cfg.build / "uodata" / "multi").resolve()
    out = (a.out or cfg.build / "multi_proof" / f"{a.name}-{time.strftime('%Y%m%d-%H%M%S')}").resolve()
    out.mkdir(parents=True, exist_ok=True)
    visits = [tuple(int(v) for v in s.split(",")) for s in a.visit or []]
    return prove.prove(cfg, a.name, stage, out, a.clip, at=a.at, visits=visits, min_free_gb=a.min_free_gb,
                       caption=a.caption or "")


def cmd_scene_build(cfg, a) -> int:
    """A scene (fort.py): every element on one grid, cut into parts; each part checked and saved."""
    import fort
    import generate
    import render
    import validate
    import walkcheck
    from multifile import Component
    desc = json.loads(a.desc.read_text(encoding="utf-8"))
    out = a.out or cfg.build / "multi" / "scenes" / desc["name"]
    cat = generate.Catalogue(a.catalogue or cfg.build / "multi" / "catalogue")
    try:
        sc = fort.build_scene(desc, cat)
    except generate.DescriptionError as e:
        print(f"[multi] {a.desc.name}: {e}")
        return 1
    (out / "parts").mkdir(parents=True, exist_ok=True)
    for old in (out / "parts").glob("*.json"):
        old.unlink()
    problems, every = list(sc.get("problems", [])), []
    # an offline walk of the tour: where a proof would stop, before it spends a shard run
    problems += walkcheck.check_tour(sc["parts"], sc["tour"], cat.pieces, desc.get("ground", 0))
    for p in sc["parts"]:
        problems += [f"{p['name']}: {q}" for q in validate.check_parts(p["comps"], cfg.client_data)]
        (out / "parts" / f"{p['name']}.json").write_text(json.dumps([c.as_list() for c in p["comps"]]),
                                                         encoding="utf-8")
        cx, cy = p["centre"]
        every += [Component(c.item, c.x + cx, c.y + cy, c.z, c.visible) for c in p["comps"] if c.visible]
        print(f"[multi]   part {p['name']}: {len(p['comps'])} components, centre {p['centre']}, "
              f"{len(p.get('doors', []))} door(s)")
    xs, ys = [c.x for c in every], [c.y for c in every]
    side = {"format": 1, "kind": "scene", "name": sc["name"], "bounds": [min(xs), min(ys), max(xs), max(ys)],
            "parts": [{k: v for k, v in p.items() if k != "comps"} | {"components": len(p["comps"])}
                      for p in sc["parts"]],
            "tour": sc["tour"], "notes": sc["notes"], "valid": not problems, "problems": problems}
    (out / "scene.json").write_text(json.dumps(side, indent=1), encoding="utf-8")
    render.render(every, cfg.client_data, out / "preview.png")
    if a.cut is not None:
        render.render(every, cfg.client_data, out / f"preview_below_{a.cut}.png", max_z=a.cut)
    for q in problems:
        print(f"[multi]   PROBLEM {q}")
    print(f"[multi] scene {sc['name']}: {len(sc['parts'])} parts, {len(every)} shown components, "
          f"{'valid' if not problems else 'NOT valid'}: {out}")
    return 0 if not problems else 1


def cmd_scene_write(cfg, a) -> int:
    """Every part of a built scene into the stage as "<scene>.<part>", and the scene into scenes.json."""
    from multifile import Component
    built = a.built or cfg.build / "multi" / "scenes" / a.name
    side = json.loads((built / "scene.json").read_text(encoding="utf-8"))
    if not side.get("valid"):
        print(f"[multi] scene {a.name} did not validate: {side.get('problems')}")
        return 1
    stage_dir = a.stage or cfg.build / "uodata" / "multi"
    entry = {"bounds": side["bounds"], "tour": side["tour"], "parts": []}
    ok_all = True
    for p in side["parts"]:
        comps = [Component(c[0], c[1], c[2], c[3], len(c) < 5) for c in
                 json.loads((built / "parts" / f"{p['name']}.json").read_text(encoding="utf-8"))]
        b = p["bounds"]
        mid, ok = write_multi(cfg, stage_dir, a.ranges, f"{a.name}.{p['name']}", comps,
                              {"doors": p.get("doors", []), "size": [b[2] - b[0], b[3] - b[1]], "storeys": []})
        ok_all &= ok
        entry["parts"].append({"name": p["name"], "id": mid, "centre": p["centre"], "doors": p.get("doors", []),
                               "components": len(comps)})
    scenes_file = Path(stage_dir) / "scenes.json"
    scenes = json.loads(scenes_file.read_text(encoding="utf-8")) if scenes_file.exists() else {}
    scenes[a.name] = entry
    scenes_file.write_text(json.dumps(scenes, indent=1), encoding="utf-8")
    print(f"[multi] scene {a.name}: {len(entry['parts'])} parts in {scenes_file}")
    return 0 if ok_all else 1


def cmd_scene_prove(cfg, a) -> int:
    import prove
    stage = (a.stage or cfg.build / "uodata" / "multi").resolve()
    out = (a.out or cfg.build / "multi_proof" / f"{a.name}-{time.strftime('%Y%m%d-%H%M%S')}").resolve()
    out.mkdir(parents=True, exist_ok=True)
    return prove.prove_scene(cfg, a.name, stage, out, a.clip, at=a.at, min_free_gb=a.min_free_gb,
                             caption=a.caption or "")


def cmd_storeys(cfg, a) -> int:
    """Raise storeys on buildings that stand in the statics (storeys.py): a world project, its
    record, previews (whole, and cut above each storey), and the offline walk of its tour."""
    import generate
    import render
    import storeys
    import walkcheck
    desc = json.loads(a.desc.read_text(encoding="utf-8"))
    cat = generate.Catalogue(a.catalogue or cfg.build / "multi" / "catalogue")
    try:
        built = storeys.build(desc, cat, cfg.client_data)
    except generate.DescriptionError as e:
        print(f"[multi] {a.desc}: {e}")
        return 1
    project = a.project
    written = storeys.write_project(built, project, desc["name"], cfg)
    from multifile import Component
    comps = [Component(sid, bx * 8 + sx, by * 8 + sy, z) for (bx, by), v in built["blocks"].items()
             for sid, sx, sy, z, _ in v["statics"]]
    ground = desc.get("ground", 0)
    problems = []
    tour = desc.get("tour", [])
    if tour:
        stops = [{"name": t["name"], "x": t["at"][0], "y": t["at"][1], "z": t["z"]} for t in tour]
        problems = walkcheck.check_tour([{"centre": [0, 0], "comps": comps}], stops, cat.pieces, ground)
    prev = project / "preview"
    prev.mkdir(exist_ok=True)
    render.render(comps, cfg.client_data, prev / "whole.png")
    for z in sorted({st["z"] for rec in built["buildings"] for st in rec["storeys"][:-1]}):
        render.render(comps, cfg.client_data, prev / f"cut_below_{z + 19}.png", max_z=z + 19)
    record = {"name": desc["name"], "facet": built["facet"], "blocks": sorted(map(list, built["blocks"])),
              "buildings": built["buildings"], "added": len(built["added"]), "removed": len(built["removed"]),
              "tour": tour, "problems": problems}
    (project / "storeys.json").write_text(json.dumps(record, indent=1) + "\n", encoding="utf-8")
    for rec in built["buildings"]:
        print(f"[multi]   {rec['name']}: storeys at z {[s['z'] for s in rec['storeys']]}, "
              f"{len(rec['footprint'])} cells, {len(rec['stairs'])} stair(s)")
    for p_ in problems:
        print(f"[multi]   PROBLEM {p_}")
    print(f"[multi] {desc['name']}: {len(written)} blocks, +{len(built['added'])} -{len(built['removed'])} statics "
          f"-> {project} ({'valid' if not problems else 'NOT valid'})")
    return 0 if not problems else 1


def cmd_show(cfg, a) -> int:
    import render
    from multifile import Multis
    data = a.data or cfg.client_data
    comps = Multis(data).get(int(a.id, 0))
    if not comps:
        print(f"[multi] no multi {a.id} in {data}")
        return 1
    png = a.png or cfg.build / "multi" / "show" / f"{int(a.id, 0):04x}.png"
    render.render(comps, cfg.client_data, png)          # a stage holds only what changed; art is the install's
    print(f"[multi] {a.id}: {len(comps)} components -> {png}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("mine")
    p.add_argument("--out", type=Path)
    p.add_argument("--facet", type=int, default=0)
    p.add_argument("--no-statics", action="store_true")
    p = sub.add_parser("sheets")
    p.add_argument("--catalogue", type=Path)
    p = sub.add_parser("build")
    p.add_argument("desc", type=Path)
    p.add_argument("--out", type=Path)
    p.add_argument("--catalogue", type=Path)
    p = sub.add_parser("write")
    p.add_argument("name")
    p.add_argument("--built", type=Path)
    p.add_argument("--stage", type=Path)
    p.add_argument("--ranges", type=Path)
    p = sub.add_parser("prove")
    p.add_argument("name")
    p.add_argument("--stage", type=Path)
    p.add_argument("--out", type=Path)
    p.add_argument("--clip", type=Path)
    p.add_argument("--caption")
    p.add_argument("--at", type=int, nargs="+", metavar="N")
    p.add_argument("--visit", action="append", metavar="X,Y,Z", help="a multi-local stop after the ground floor")
    p.add_argument("--min-free-gb", type=float, default=16)
    p = sub.add_parser("scene-build")
    p.add_argument("desc", type=Path)
    p.add_argument("--out", type=Path)
    p.add_argument("--catalogue", type=Path)
    p.add_argument("--cut", type=int, help="also render everything below this z")
    p = sub.add_parser("scene-write")
    p.add_argument("name")
    p.add_argument("--built", type=Path)
    p.add_argument("--stage", type=Path)
    p.add_argument("--ranges", type=Path)
    p = sub.add_parser("scene-prove")
    p.add_argument("name")
    p.add_argument("--stage", type=Path)
    p.add_argument("--out", type=Path)
    p.add_argument("--clip", type=Path)
    p.add_argument("--caption")
    p.add_argument("--at", type=int, nargs="+", metavar="N")
    p.add_argument("--min-free-gb", type=float, default=16)
    p = sub.add_parser("storeys")
    p.add_argument("desc", type=Path)
    p.add_argument("--project", type=Path, required=True)
    p.add_argument("--catalogue", type=Path)
    p = sub.add_parser("show")
    p.add_argument("id")
    p.add_argument("--data", type=Path)
    p.add_argument("--png", type=Path)
    a = ap.parse_args()
    cfg = load_config()
    return {"mine": cmd_mine, "sheets": cmd_sheets, "build": cmd_build, "write": cmd_write, "prove": cmd_prove, "show": cmd_show,
            "scene-build": cmd_scene_build, "storeys": cmd_storeys, "scene-write": cmd_scene_write, "scene-prove": cmd_scene_prove}[a.cmd](cfg, a)


if __name__ == "__main__":
    sys.exit(main())
