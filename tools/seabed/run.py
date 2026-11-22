#!/usr/bin/env python3
"""Seabed and shallows for a whole facet, staged outside the install (ADR-0022).

    python tools/seabed/run.py generate [--facet 0] [--seed 1] [--out DIR]
    python tools/seabed/run.py stats    [--facet 0] [--seed 1]

generate reads the install (read only), plans sea plants, corals, rocks and
sand patches on every shallow cell (see seabed.py for the rule), and writes
staidx<N>.mul, statics<N>.mul, files_override.txt and seabed.json into --out
(default <UO_BUILD_DIR>/seabed/map<N>). Give the client --files-override
<out>/files_override.txt. The install is never written.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))

import calibrate  # noqa: E402
import seabed as S  # noqa: E402
from guo import load_config, uomap  # noqa: E402


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=("generate", "stats"))
    ap.add_argument("--facet", type=int, default=0)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--out")
    ap.add_argument("--opaque-water", action="store_true", help="leave the water statics opaque (the seabed is then hidden)")
    ap.add_argument("--no-floor", action="store_true", help="experiment: leave out the floor tiles")
    ap.add_argument("--calibrate", action="store_true", help="experiment (calibrate.py): 64 hues in an 8x8 pattern, no objects")
    ap.add_argument("--shallow-max", type=int, default=S.SHALLOW_MAX)
    a = ap.parse_args(argv)
    cfg = load_config()
    install = Path(cfg.client_data)
    out = Path(a.out) if a.out else cfg.build / "seabed" / f"map{a.facet}"
    t0 = time.time()
    with uomap.open_facet(install, a.facet) as f:
        land, lz, wet, occ, full = S.scan_facet(f)
        p = S.plan(land, lz, wet, occ, seed=a.seed, shallow_max=a.shallow_max, full_water=full,
                   calibrate=calibrate.pattern(S.HUE_RAMP) if a.calibrate else None)
        stats = {
            "facet": a.facet, "width": land.shape[0], "height": land.shape[1], "seed": a.seed,
            "shallow_max": a.shallow_max,
            "water_chunks": p.water_blocks, "eligible_chunks": p.eligible_blocks, "covered_chunks": p.covered_blocks,
            "water_cells": p.water_cells, "shallow_cells": p.shallow_cells, "extended_cells": p.extended_cells, "statics_added": len(p.placed),
        }
        print(json.dumps(stats, indent=2))
        if a.cmd == "generate":
            placed = [q for q in p.placed if not (a.no_floor and q[2] in S.FLOOR)]
            lines = S.write_stage(f, placed, out, install, p.new_land, p.new_lz, land, lz)
            if not a.opaque_water:
                lines.append(S.stage_translucent_water(install, out))
                lines.append(S.stage_seamless_water_art(install, out))
            (out / "files_override.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
            stats["seconds"] = round(time.time() - t0, 1)
            (out / "seabed.json").write_text(json.dumps(stats, indent=2) + "\n", encoding="utf-8")
            print(f"[seabed] staged in {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
