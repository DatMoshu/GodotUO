"""Room decoration: learn how UO's buildings are furnished, then furnish generated multis.

    python tools/decorate/run.py mine     [--facets 0] [--no-multis] [--db FILE]
    python tools/decorate/run.py stats    [--db FILE]
    python tools/decorate/run.py decorate SIDECAR_OR_NAME [--seed N] [--out FILE] [--desc DESC.json]
    python tools/decorate/run.py preview  DESC.json [--seed N] [--out DIR]
    python tools/decorate/run.py demo     [--seed N]          preview the t_manor, l_townhouse, courtyard_house examples

decorate, preview and demo take --planner ai: an OpenAI model plans each house (planner.py), the
decorator places and checks it; answers are cached in build/decorate/plans/, --offline uses only
the cache. --brief says what the house is; --type STOREY:X,Y=TYPE forces a room's type.

The database (default build/decorate/decor.sqlite) and the previews
(build/decorate/previews/) are derived from client data: they stay in build/.
The UO install is read in place, never written.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE.parent / "multi"))
sys.path.insert(0, str(HERE))

from guo import load_config  # noqa: E402

DEMO = ("t_manor", "l_townhouse", "courtyard_house")


def db_path(cfg, a) -> Path:
    return a.db or cfg.build / "decorate" / "decor.sqlite"


def decoration_dir(cfg, a) -> Path | None:
    """The shard's Data/Decoration folder: --decoration, else GUO_SHARD_DECORATION, else this
    checkout's ModernUO source, else the main checkout's (a worktree has no shard source)."""
    import os
    import subprocess
    if a.no_decoration:
        return None
    rel = Path("Distribution") / "Data" / "Decoration"
    cands = [a.decoration, os.environ.get("GUO_SHARD_DECORATION"), cfg.shard_src / rel]
    try:
        common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=HERE,
                                capture_output=True, text=True, timeout=10).stdout.strip()
        if common:
            cands.append(Path(common).parent / "tools" / "modernuo" / "src" / rel)
    except (OSError, subprocess.SubprocessError):
        pass
    for c in cands:
        if c and Path(c).is_dir():
            return Path(c)
    print("[decorate] no shard decoration folder found: mining the statics alone")
    return None


def cmd_mine(cfg, a) -> int:
    import mine_decor
    facets = tuple(int(f) for f in a.facets.split(",") if f != "")
    deco = decoration_dir(cfg, a)
    if deco:
        print(f"[decorate] shard decoration: {deco}")
    counts = mine_decor.mine(cfg.client_data, db_path(cfg, a), facets=facets, multis=not a.no_multis,
                             decoration=deco)
    print(json.dumps(counts, indent=1))
    print(f"[decorate] database: {db_path(cfg, a)}")
    return cmd_stats(cfg, a)


def cmd_stats(cfg, a) -> int:
    import db
    con = db.open_ro(db_path(cfg, a))
    print("room types (enclosed rooms / all):")
    for r in con.execute("SELECT type, COUNT(*) n, SUM(enclosed) e, ROUND(AVG(area),1) area, SUM(storey>0) up "
                         "FROM room GROUP BY type ORDER BY n DESC"):
        print(f"  {r['type']:<18} {r['e']:>5} / {r['n']:<5} mean area {r['area']:<6} upstairs {r['up']}")
    print("templates used most (uses, cells, against, kinds):")
    for r in con.execute("SELECT id, uses, cells, pieces, against, kinds, room_types FROM template "
                         "WHERE pieces > 1 ORDER BY uses DESC LIMIT 15"):
        print(f"  #{r['id']:<6} x{r['uses']:<4} {r['cells']} cells {r['pieces']} pieces against '{r['against']}' "
              f"{r['kinds']} in {list(json.loads(r['room_types']))[:3]}")
    print("kinds side by side most often:")
    for r in con.execute("SELECT a, b, adjacent, rooms FROM cooccur ORDER BY adjacent DESC LIMIT 12"):
        print(f"  {r['a']} + {r['b']}: adjacent in {r['adjacent']} rooms, together in {r['rooms']}")
    return 0


def library(cfg, a):
    """The learned library, keeping only templates whose every item has art in this install."""
    import db
    import decorate
    from guo.uoread import Art
    art = Art(cfg.client_data)
    known: dict[int, bool] = {}

    def has_art(item: int) -> bool:
        if item not in known:
            known[item] = art.get_static(item) is not None
        return known[item]
    return decorate.Library.from_db(db.open_ro(db_path(cfg, a)), has_art=has_art, facings=facings(a))


def facings(a):
    import decorate
    return decorate.load_facings(a.facings) if getattr(a, "facings", None) else None


def load_sidecar(cfg, ref: str) -> tuple[dict, Path]:
    p = Path(ref)
    if p.is_dir():
        p = p / "multi.json"
    if not p.exists():
        p = cfg.build / "multi" / "built" / ref / "multi.json"
    if not p.exists():
        raise SystemExit(f"[decorate] no sidecar for '{ref}' (build it first: python tools/multi/run.py build DESC.json)")
    return json.loads(p.read_text(encoding="utf-8")), p


def forced_types(a) -> dict:
    out = {}
    for t in getattr(a, "type", None) or []:
        key, _eq, rtype = t.partition("=")
        out[key] = rtype
    return out


def furnish(cfg, a, side: dict, lib, seed: int, density: float) -> tuple[list, list]:
    """The decor list and report for a built house, by the rules or by the AI planner."""
    import decorate
    types = forced_types(a)
    if getattr(a, "planner", "rules") != "ai":
        return decorate.decorate(side, lib, seed=seed, density=density, types=types)
    import db
    import planner
    decor, report, meta = planner.decorate_ai(
        side, lib, db.open_ro(db_path(cfg, a)), seed=seed, brief=a.brief or "", density=density, types=types,
        cache_dir=cfg.build / "decorate" / "plans", model=a.model or planner.MODEL, offline=a.offline)
    tokens = sum((u.get("input_tokens") or 0) + (u.get("output_tokens") or 0) for u in meta["usage"])
    report = report + [f"planner: {meta['model']}, " + ("cached" if meta["cached"] else f"{meta['calls']} call(s)")
                       + f", {tokens} tokens; refused at first {len(meta['errors_first'])}, "
                       f"after the retry {len(meta['errors_final'])} (moved {len(meta['moved'])}); by the rules: "
                       + (", ".join(meta["fallback_rooms"]) or "none")] + [f"planner refused: {e}" for e in meta["errors_final"]]
    return decor, report


def cmd_decorate(cfg, a) -> int:
    import decorate
    side, _where = load_sidecar(cfg, a.sidecar)
    decor, report = furnish(cfg, a, side, library(cfg, a), a.seed, a.density)
    for line in report:
        print(line)
    problems = decorate.check(side, decor, facings(a))
    for pr in problems:
        print(f"[decorate] PROBLEM {pr}")
    out = {"name": side["name"], "seed": a.seed, "decor": decor}
    if a.desc:
        desc = json.loads(a.desc.read_text(encoding="utf-8"))
        desc["decor"] = [d for d in desc.get("decor", []) if "storey" not in d] + decor
        out = desc
    text = json.dumps(out, indent=1)
    if a.out:
        a.out.parent.mkdir(parents=True, exist_ok=True)
        a.out.write_text(text, encoding="utf-8")
        print(f"[decorate] {len(decor)} items -> {a.out}")
    else:
        print(text)
    return 1 if problems else 0


def preview(cfg, desc_path: Path, seed: int, out_dir: Path, lib, density: float = 1.0, a=None) -> dict:
    """Build the description as it is and furnished, and render both (roof off)."""
    import decorate
    import generate
    import render
    import validate
    desc = json.loads(desc_path.read_text(encoding="utf-8"))
    cat = generate.Catalogue(cfg.build / "multi" / "catalogue")
    comps, side = generate.build(desc, cat)
    decor, report = furnish(cfg, a, side, lib, seed, density)
    furnished = dict(desc, decor=[d for d in desc.get("decor", []) if "storey" not in d] + decor)
    comps2, side2 = generate.build(furnished, generate.Catalogue(cfg.build / "multi" / "catalogue"))
    problems = decorate.check(side, decor, facings(a)) + validate.validate(comps2, side2, cfg.client_data)
    report = report + [f"PROBLEM {p}" for p in problems] + [f"{len(decor)} items; " + (
        "check and validate: clean" if not problems else f"{len(problems)} problem(s)")]
    name = desc["name"] + ("_ai" if getattr(a, "planner", "rules") == "ai" else "")
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / f"{name}_decorated.json").write_text(json.dumps(furnished, indent=1), encoding="utf-8")
    (out_dir / f"{name}_report.txt").write_text("\n".join(report) + "\n", encoding="utf-8")
    shots = {}
    for n, z in enumerate(side["storeys"]):
        # everything below the next floor (below the top storey's wall tops for the last): the
        # storeys above and the roofs cut away
        cut = (side["storeys"][n + 1] - 1) if n + 1 < len(side["storeys"]) else z + 19
        for tag, cs in (("before", comps), ("after", comps2)):
            png = out_dir / f"{name}_s{n}_{tag}.png"
            render.render(cs, cfg.client_data, png, max_z=cut)
            shots[f"s{n}_{tag}"] = png
        # the plan of this storey with the furniture marked
        png = out_dir / f"{name}_s{n}_plan.png"
        decorate.plan_image(side, decor, n, png)
        shots[f"s{n}_plan"] = png
    return {"name": name, "items": len(decor), "report": report, "shots": shots,
            "components": [len(comps), len(comps2)], "problems": problems}


def cmd_preview(cfg, a) -> int:
    lib = library(cfg, a)
    out = a.out or cfg.build / "decorate" / "previews"
    r = preview(cfg, a.desc, a.seed, out, lib, a.density, a)
    print("\n".join(r["report"]))
    for k, p in r["shots"].items():
        print(f"[decorate] {k}: {p}")
    return 1 if r["problems"] else 0


def cmd_demo(cfg, a) -> int:
    import db
    import decorate
    lib = decorate.Library.from_db(db.open_ro(db_path(cfg, a)), facings=facings(a))
    out = a.out or cfg.build / "decorate" / "previews"
    for name in DEMO:
        r = preview(cfg, HERE.parent / "multi" / "examples" / f"{name}.json", a.seed, out, lib, a.density, a)
        print("\n".join(r["report"]))
        print(f"[decorate] {name}: {r['items']} items, components {r['components'][0]} -> {r['components'][1]}")
    print(f"[decorate] previews: {out}")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--db", type=Path, help="the decor database (default build/decorate/decor.sqlite)")
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("mine", help="mine furnished interiors into the database")
    s.add_argument("--facets", default="0", help="comma list of facets to scan (default 0, Felucca)")
    s.add_argument("--no-multis", action="store_true", help="skip the client's multis")
    s.add_argument("--decoration", type=Path, help="the shard's Data/Decoration folder (ModernUO)")
    s.add_argument("--no-decoration", action="store_true", help="the statics alone, without the shard's decoration")
    sub.add_parser("stats", help="summarise the database")
    s = dec = sub.add_parser("decorate", help="a decor list for a built multi")
    s.add_argument("sidecar", help="a built multi's name, folder or multi.json")
    s.add_argument("--seed", type=int, default=1)
    s.add_argument("--density", type=float, default=1.0, help="scale the learned furnishing density")
    s.add_argument("--out", type=Path)
    s.add_argument("--desc", type=Path, help="write the whole description with the decor merged in")
    s = pre = sub.add_parser("preview", help="before/after renders of one description")
    s.add_argument("desc", type=Path)
    s.add_argument("--seed", type=int, default=1)
    s.add_argument("--density", type=float, default=1.0, help="scale the learned furnishing density")
    s.add_argument("--out", type=Path)
    s = dem = sub.add_parser("demo", help="previews of the three demo examples")
    s.add_argument("--seed", type=int, default=1)
    s.add_argument("--density", type=float, default=1.0, help="scale the learned furnishing density")
    s.add_argument("--out", type=Path)
    for s in (dec, pre, dem):
        s.add_argument("--planner", choices=("rules", "ai"), default="rules",
                       help="who lays out the rooms: the learned rules, or an OpenAI model checked by them")
        s.add_argument("--brief", help="what the house is, for the AI planner (e.g. 'a weaver's home and shop')")
        s.add_argument("--type", action="append", metavar="STOREY:X,Y=TYPE", help="force the type of the room holding a cell")
        s.add_argument("--model", help="the planner's model (default GUO_DECORATE_MODEL, else gpt-5.4-mini)")
        s.add_argument("--offline", action="store_true", help="AI planner: cached plans only, never call the API")
        s.add_argument("--facings", type=Path, help="which wall sides each item's art may face away from "
                       "({\"items\": {\"0x0a2c\": [\"N\"]}}); pieces never face a wall")
    a = p.parse_args()
    cfg = load_config()
    return {"mine": cmd_mine, "stats": cmd_stats, "decorate": cmd_decorate, "preview": cmd_preview,
            "demo": cmd_demo}[a.cmd](cfg, a)


if __name__ == "__main__":
    sys.exit(main())
