"""Room decoration: learn how UO's buildings are furnished, then furnish generated multis.

    python tools/decorate/run.py mine     [--facets 0] [--no-multis] [--db FILE]
    python tools/decorate/run.py stats    [--db FILE]
    python tools/decorate/run.py decorate SIDECAR_OR_NAME [--seed N] [--out FILE] [--desc DESC.json]
    python tools/decorate/run.py preview  DESC.json [--seed N] [--out DIR]
    python tools/decorate/run.py demo     [--seed N]          preview the t_manor, l_townhouse, courtyard_house examples

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
    return decorate.Library.from_db(db.open_ro(db_path(cfg, a)), has_art=has_art)


def load_sidecar(cfg, ref: str) -> tuple[dict, Path]:
    p = Path(ref)
    if p.is_dir():
        p = p / "multi.json"
    if not p.exists():
        p = cfg.build / "multi" / "built" / ref / "multi.json"
    if not p.exists():
        raise SystemExit(f"[decorate] no sidecar for '{ref}' (build it first: python tools/multi/run.py build DESC.json)")
    return json.loads(p.read_text(encoding="utf-8")), p


def cmd_decorate(cfg, a) -> int:
    import decorate
    side, _where = load_sidecar(cfg, a.sidecar)
    decor, report = decorate.decorate(side, library(cfg, a), seed=a.seed, density=a.density)
    for line in report:
        print(line)
    problems = decorate.check(side, decor)
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


def preview(cfg, desc_path: Path, seed: int, out_dir: Path, lib, density: float = 1.0) -> dict:
    """Build the description as it is and furnished, and render both (roof off)."""
    import decorate
    import generate
    import render
    import validate
    desc = json.loads(desc_path.read_text(encoding="utf-8"))
    cat = generate.Catalogue(cfg.build / "multi" / "catalogue")
    comps, side = generate.build(desc, cat)
    decor, report = decorate.decorate(side, lib, seed=seed, density=density)
    furnished = dict(desc, decor=[d for d in desc.get("decor", []) if "storey" not in d] + decor)
    comps2, side2 = generate.build(furnished, generate.Catalogue(cfg.build / "multi" / "catalogue"))
    problems = decorate.check(side, decor) + validate.validate(comps2, side2, cfg.client_data)
    report = report + [f"PROBLEM {p}" for p in problems] + [f"{len(decor)} items; " + (
        "check and validate: clean" if not problems else f"{len(problems)} problem(s)")]
    name = desc["name"]
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
    r = preview(cfg, a.desc, a.seed, out, lib, a.density)
    print("\n".join(r["report"]))
    for k, p in r["shots"].items():
        print(f"[decorate] {k}: {p}")
    return 1 if r["problems"] else 0


def cmd_demo(cfg, a) -> int:
    import db
    import decorate
    lib = decorate.Library.from_db(db.open_ro(db_path(cfg, a)))
    out = a.out or cfg.build / "decorate" / "previews"
    for name in DEMO:
        r = preview(cfg, HERE.parent / "multi" / "examples" / f"{name}.json", a.seed, out, lib)
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
    s = sub.add_parser("decorate", help="a decor list for a built multi")
    s.add_argument("sidecar", help="a built multi's name, folder or multi.json")
    s.add_argument("--seed", type=int, default=1)
    s.add_argument("--density", type=float, default=1.0, help="scale the learned furnishing density")
    s.add_argument("--out", type=Path)
    s.add_argument("--desc", type=Path, help="write the whole description with the decor merged in")
    s = sub.add_parser("preview", help="before/after renders of one description")
    s.add_argument("desc", type=Path)
    s.add_argument("--seed", type=int, default=1)
    s.add_argument("--density", type=float, default=1.0, help="scale the learned furnishing density")
    s.add_argument("--out", type=Path)
    s = sub.add_parser("demo", help="previews of the three demo examples")
    s.add_argument("--seed", type=int, default=1)
    s.add_argument("--density", type=float, default=1.0, help="scale the learned furnishing density")
    s.add_argument("--out", type=Path)
    a = p.parse_args()
    cfg = load_config()
    return {"mine": cmd_mine, "stats": cmd_stats, "decorate": cmd_decorate, "preview": cmd_preview,
            "demo": cmd_demo}[a.cmd](cfg, a)


if __name__ == "__main__":
    sys.exit(main())
