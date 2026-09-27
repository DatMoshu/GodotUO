#!/usr/bin/env python3
"""Author UO data files into a staged set, never the install (ADR-0022).

    python tools/uodata_write/run.py scan      --stage DIR
    python tools/uodata_write/run.py reserve   --stage DIR --pack NAME [--statics N] [--bodies N]
    python tools/uodata_write/run.py dreadcrest --stage DIR --source DIR [--pack moshu]
    python tools/uodata_write/run.py verify    --stage DIR

All take --ranges FILE (or UO_DATA_RANGES): a shard maintainer's range policy merged over
ranges.json (ADR-0022).

scan       free slots per namespace in the stage (or, where not staged yet, the install)
reserve    a pack's contiguous ranges: item ids, animation bodies, and those bodies'
           paperdoll gumps (50000/60000 + body); recorded in <stage>/slots.json
dreadcrest Codex's Dreadcrest candidate (build/uo_original_expansion/dreadcrest_wearable)
           as records: item art, paperdoll gumps, the 175 equipment animation payloads,
           and a tiledata item (the kite shield's record with the new animation id);
           written, read back, and the install checked unchanged
verify     every copied install file still hashes as when it was copied

The stage holds files_override.txt for the client (settings.json files_override) and is
the folder a shard lists first in its data directories.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))

from guo import load_config  # noqa: E402
from guo.uorecord import AssetRecord  # noqa: E402
import uodata as U  # noqa: E402


def cmd_scan(stage: U.Stage) -> int:
    fs = U.free_statics(stage)
    fb = U.free_people_bodies(stage)
    fg = U.free_gumps(stage)
    both = [b for b in fb if U.MALE_GUMP + b in fg and U.FEMALE_GUMP + b in fg]
    print(f"[uodata] item ids: {len(fs)} free of {U.static_count(stage.read_path('tiledata.mul'))} (highest {max(fs):#06x})")
    print(f"[uodata] people bodies (400+): {len(fb)} free; with both paperdoll gumps free: {len(both)} ({both[:12]})")
    print(f"[uodata] gumps: {len(fg)} free ids below 0x10000")
    return 0


def reserve(stage: U.Stage, reg: U.Registry, pack: str, statics: int, bodies: int) -> None:
    if pack in reg.data["packs"]:
        return
    fs = U.free_statics(stage)
    reg.reserve(pack, "static", fs, statics)
    fg = U.free_gumps(stage)
    fb = [b for b in U.free_people_bodies(stage) if U.MALE_GUMP + b in fg and U.FEMALE_GUMP + b in fg]
    lo, hi = reg.reserve(pack, "anim", fb, bodies, prefer_high=False)
    p = reg.data["packs"][pack]["ranges"]
    p["gump"] = [[U.MALE_GUMP + lo, U.MALE_GUMP + hi], [U.FEMALE_GUMP + lo, U.FEMALE_GUMP + hi]]
    reg.save()


def dreadcrest(stage: U.Stage, source: Path, pack: str, policy: dict) -> int:
    reg = U.Registry(stage, policy)
    reserve(stage, reg, pack, 16, 4)
    item = reg.take(pack, "static", "dreadcrest")
    body = reg.take(pack, "anim", "dreadcrest")
    gumps = (U.MALE_GUMP + body, U.FEMALE_GUMP + body)
    used = reg.data["packs"][pack]["used"].setdefault("gump", {})
    used["dreadcrest-male"], used["dreadcrest-female"] = gumps
    reg.save()
    print(f"[uodata] pack {pack}: ranges {reg.data['packs'][pack]['ranges']}")
    print(f"[uodata] dreadcrest: item {item:#06x}, animation body {body}, paperdoll gumps {gumps}")

    src_tile = U.read_tile(stage.read_path("tiledata.mul"), 0x1B74)  # the kite shield it was fitted to
    records = [AssetRecord("static", item, (source / "item-ui" / "item-7028.bin").read_bytes())]
    for gid, name in zip(gumps, ("paperdoll-50581.bin", "paperdoll-60581.bin")):
        data = (source / "item-ui" / name).read_bytes()
        records.append(AssetRecord("gump", gid, data, {"width": int.from_bytes(data[0:4], "little"),
                                                       "height": int.from_bytes(data[4:8], "little")}))
    for m in json.loads((source / "animation-index-map.json").read_text(encoding="utf-8")):
        records.append(AssetRecord("anim", body, (source / m["payload"]).read_bytes(),
                                   {"action": m["action"], "direction": m["direction"]}))
    records.append(AssetRecord("tiledata-item", item, b"", {**src_tile, "anim": body, "name": "dreadcrest shield"}))

    for line in U.write_records(stage, records):
        print(f"[uodata] wrote {line}")

    bad = 0
    for r in records:
        got = U.read_back(stage, r)
        want = {k: v for k, v in r.meta.items() if k in U.TILE_FIELDS} if r.kind == "tiledata-item" else r.data
        if r.kind == "tiledata-item":
            got = {k: got[k] for k in want}
        if got != want:
            bad += 1
            print(f"[uodata] FAIL read-back of {r.kind} {r.id} {r.meta.get('action', '')}")
    changed = stage.check_install_unchanged()
    print(f"[uodata] read back: {len(records) - bad}/{len(records)} records equal what was written")
    print(f"[uodata] install files copied from: {', '.join(sorted(stage.meta['files']))}; unchanged: {not changed}")
    (stage.root / "dreadcrest.json").write_text(json.dumps({"item": item, "body": body, "gumps": gumps}, indent=2) + "\n",
                                                encoding="utf-8")
    return 0 if bad == 0 and not changed else 1


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("command", choices=["scan", "reserve", "dreadcrest", "verify"])
    ap.add_argument("--stage", type=Path, required=True)
    ap.add_argument("--source", type=Path)
    ap.add_argument("--pack", default="moshu")
    ap.add_argument("--statics", type=int, default=16)
    ap.add_argument("--bodies", type=int, default=4)
    ap.add_argument("--ranges", type=Path, help="a range policy merged over ranges.json (ADR-0022)")
    args = ap.parse_args()
    cfg = load_config()
    stage = U.Stage(args.stage, cfg.client_data)
    policy = U.load_policy(args.ranges)
    if args.command == "scan":
        return cmd_scan(stage)
    if args.command == "reserve":
        reserve(stage, U.Registry(stage, policy), args.pack, args.statics, args.bodies)
        print(json.dumps(U.Registry(stage, policy).data["packs"][args.pack], indent=1))
        return 0
    if args.command == "dreadcrest":
        return dreadcrest(stage, args.source, args.pack, policy)
    changed = stage.check_install_unchanged()
    print(f"[uodata] install unchanged: {not changed}" + (f" (changed: {changed})" if changed else ""))
    return 0 if not changed else 1


if __name__ == "__main__":
    sys.exit(main())
