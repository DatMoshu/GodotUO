#!/usr/bin/env python3
# SPDX-License-Identifier: BSD-2-Clause
r"""Compare a ClassicUO render dump with a GUO one taken in the same place.

    launchers\dev\render_diff.bat NAME          build\render_dump\NAME\{cuo,guo}.json
    launchers\dev\render_diff.bat --list

The dumps come from godot\GUO\src\Bootstrap\RenderDump.cs, which is compiled
into both clients (ClassicUO's copy through tools\render_dump\inject.targets).
Say "renderdump NAME" in GUO with both standing together -- side_by_side.bat
puts them there, or with --cuo-only leaves ClassicUO there for a scenario's
renderdump step -- and each writes what it drew last frame plus every object
on the tiles around it.

What this reports, written to build\render_dump\NAME\diff.md as well:

  map       objects on a tile in one client and not the other (world objects
            by serial, the rest by kind, graphic, hue and z)
  fields    the same object with a different priority z, depth, alpha or
            allowed-to-draw
  drawn     drawn in one client and not the other; how it was drawn (a mesh or
            a render list) is shown but a different route alone is not a
            difference
  below     drawn objects whose z is below the land of their own tile: a
            cellar, a sunk foundation. ClassicUO's depth buffer hides them
            under the land; GUO's canvas has no depth test and draws land
            first, so they show through the ground.
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import load_config  # noqa: E402

WORLD_KINDS = {"Mobile", "Item", "PlayerMobile", "GameEffect"}
LAND = "Land"


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def key(obj: dict, x: int, y: int) -> tuple:
    """How one object is recognised in both dumps."""
    if "serial" in obj:
        return ("serial", obj["serial"])
    return (obj["kind"], x, y, obj["graphic"], obj["hue"], obj["z"])


def index(dump: dict) -> dict[tuple, list[dict]]:
    out: dict[tuple, list[dict]] = defaultdict(list)
    for tile in dump["tiles"]:
        for obj in tile["objects"]:
            out[key(obj, tile["x"], tile["y"])].append({**obj, "tx": tile["x"], "ty": tile["y"]})
    return out


def describe(obj: dict) -> str:
    serial = f" 0x{obj['serial']:08X}" if "serial" in obj else ""
    return (
        f"{obj['kind']}{serial} g=0x{obj['graphic']:04X} hue={obj['hue']} "
        f"at {obj.get('tx', obj['x'])},{obj.get('ty', obj['y'])},{obj['z']}"
    )


def below_ground(dump: dict) -> list[tuple[dict, dict]]:
    """Drawn objects whose z is below the land of their own tile.

    A cellar, a foundation sunk under the street. ClassicUO draws them and
    lets the land hide them: it is nearer in the depth buffer. GUO's canvas
    has no depth test and draws land first, so whatever of them it draws
    shows through the ground.
    """
    hits = []
    for tile in dump["tiles"]:
        ground = next((o for o in tile["objects"] if o["kind"] == LAND), None)
        if ground is None:
            continue
        for obj in tile["objects"]:
            if obj["kind"] != LAND and obj.get("drawn") and obj["z"] < ground["z"]:
                hits.append(({**obj, "tx": tile["x"], "ty": tile["y"]}, ground))
    return hits


def compare(cuo: dict, guo: dict) -> list[str]:
    out: list[str] = []
    say = out.append

    say(f"# Render diff: {cuo['label']}")
    say("")
    say("| | ClassicUO | GUO |")
    say("|---|---|---|")
    for k in ("x", "y", "z"):
        say(f"| player {k} | {cuo['player'][k]} | {guo['player'][k]} |")
    say(f"| map | {cuo['map']} | {guo['map']} |")
    say(f"| max ground z | {cuo['max_ground_z']} | {guo['max_ground_z']} |")
    for name in sorted(set(cuo["lists"]) | set(guo["lists"])):
        say(f"| list `{name}` | {len(cuo['lists'].get(name, []))} | {len(guo['lists'].get(name, []))} |")

    def drawn_count(d: dict) -> Counter:
        c: Counter = Counter()
        for tile in d["tiles"]:
            for obj in tile["objects"]:
                if obj.get("drawn"):
                    c[obj["drawn"]] += 1
        return c

    cd, gd = drawn_count(cuo), drawn_count(guo)
    for route in sorted(set(cd) | set(gd)):
        say(f"| drawn via `{route}` | {cd[route]} | {gd[route]} |")
    say("")

    if (cuo["player"]["x"], cuo["player"]["y"]) != (guo["player"]["x"], guo["player"]["y"]):
        say("**The players are not on the same tile; the tile window differs, so edges will too.**")
        say("")

    ci, gi = index(cuo), index(guo)

    # --- map ---------------------------------------------------------------
    only_cuo = [ci[k][0] for k in ci if k not in gi]
    only_guo = [gi[k][0] for k in gi if k not in ci]

    # The tile windows are centred on each player; compare only the overlap.
    def inside(obj: dict, other: dict) -> bool:
        r = 24
        return abs(obj["tx"] - other["player"]["x"]) <= r and abs(obj["ty"] - other["player"]["y"]) <= r

    only_cuo = [o for o in only_cuo if inside(o, guo)]
    only_guo = [o for o in only_guo if inside(o, cuo)]

    say(f"## Map: {len(only_cuo)} only in ClassicUO, {len(only_guo)} only in GUO")
    say("")
    for o in only_cuo[:60]:
        say(f"- ClassicUO only: {describe(o)}")
    for o in only_guo[:60]:
        say(f"- GUO only: {describe(o)}")
    say("")

    # --- fields and drawn ----------------------------------------------------
    field_diffs, drawn_diffs, route_diffs = [], [], Counter()
    for k in ci.keys() & gi.keys():
        for a, b in zip(ci[k], gi[k]):
            changed = [
                f"{f} {a[f]} vs {b[f]}"
                for f in ("priority_z", "alpha", "allowed")
                if a.get(f) != b.get(f)
            ]
            if abs(a["depth"] - b["depth"]) > 1e-3:
                changed.append(f"depth {a['depth']:.3f} vs {b['depth']:.3f}")
            if changed and k[0] != "serial":
                field_diffs.append(f"- {describe(a)}: " + ", ".join(changed))

            if bool(a.get("drawn")) != bool(b.get("drawn")):
                drawn_diffs.append(
                    f"- {describe(a)}: ClassicUO {a.get('drawn') or 'not drawn'}, "
                    f"GUO {b.get('drawn') or 'not drawn'}"
                )
            elif a.get("drawn") and a.get("drawn") != b.get("drawn"):
                route_diffs[(a["kind"], a["drawn"], b["drawn"])] += 1

    say(f"## Fields: {len(field_diffs)} static objects differ")
    say("")
    out.extend(sorted(field_diffs)[:80])
    say("")
    say(f"## Drawn: {len(drawn_diffs)} drawn by one client only")
    say("")
    out.extend(sorted(drawn_diffs)[:80])
    if route_diffs:
        say("")
        say("Drawn by both, by a different route (not a difference in itself):")
        say("")
        for (kind, a, b), n in route_diffs.most_common():
            say(f"- {n} {kind}: ClassicUO `{a}`, GUO `{b}`")
    say("")

    # --- below the ground -------------------------------------------------
    hits = below_ground(guo)
    say(f"## Below the ground: {len(hits)} drawn objects sit below their tile's land")
    say("")
    say(
        f"ClassicUO draws {len(below_ground(cuo))} such objects and its depth buffer hides "
        "them under the land. GUO draws land first with no depth test, so these show "
        "through the ground in GUO."
    )
    say("")
    by_kind = Counter((o["kind"], o["graphic"]) for o, _ in hits)
    for (kind, graphic), n in by_kind.most_common(30):
        say(f"- {n} x {kind} 0x{graphic:04X}")
    say("")
    for o, g in hits[:40]:
        say(
            f"- {describe(o)} via `{o['drawn']}`, under land 0x{g['graphic']:04X} at z {g['z']}"
        )

    return out


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="render_diff", description=__doc__.splitlines()[0])
    parser.add_argument("label", nargs="?", help="the NAME said after renderdump")
    parser.add_argument("--list", action="store_true", help="list the dumps there are")
    args = parser.parse_args(argv)

    root = load_config().build / "render_dump"

    if args.list or not args.label:
        for d in sorted(root.iterdir()) if root.exists() else []:
            have = ", ".join(p.stem for p in sorted(d.glob("*.json")))
            print(f"{d.name:<30} {have}")
        return 0

    d = root / args.label
    cuo_path, guo_path = d / "cuo.json", d / "guo.json"
    missing = [p.name for p in (cuo_path, guo_path) if not p.exists()]
    if missing:
        sys.exit(f"[render_diff] {d} has no {' or '.join(missing)}")

    report = compare(load(cuo_path), load(guo_path))
    (d / "diff.md").write_text("\n".join(report) + "\n", encoding="utf-8")
    print("\n".join(report[:60]))
    print(f"\n[render_diff] full report: {d / 'diff.md'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
