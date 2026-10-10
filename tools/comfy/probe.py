#!/usr/bin/env python3
"""End-to-end proof for staged ComfyUI generations: pack -> store mount -> client consumers.

Installs the stage pack into a scratch store (the headless StoreSmoke tool),
writes a content lock with the stage's numeric bindings, then runs the real
client headless on the ComfyContentProbe scene: SoundsLoader must return the
generated SFX bytes, GetMusic the staged music, and SplatLodChain the staged
LOD PLYs. Nothing touches the UO install; the store, lock and report all live
under build/.

    python tools/comfy/probe.py --godot <godot-console> --stage build/staged/guo-comfy-gen --data <client>
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", required=True, help="Blocking godot-console executable")
    parser.add_argument("--stage", required=True, type=Path, help="stage dir from stage.py")
    parser.add_argument("--data", default=os.environ.get("UO_CLIENT_DATA"),
                        required=not bool(os.environ.get("UO_CLIENT_DATA")))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    args.stage = args.stage.resolve()
    tool = root / "tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll"
    zips = sorted(args.stage.glob("*.zip"))
    assert len(zips) == 1, f"expected one pack in {args.stage}, found {len(zips)}"
    bindings = json.loads((args.stage / "bindings.json").read_text(encoding="utf-8"))
    with tempfile.TemporaryDirectory(prefix="comfy-probe-", dir=root / "build") as temporary:
        work = Path(temporary)
        installed = work / "store"
        lock = work / "content-lock.json"

        def command(*arguments):
            subprocess.run(["dotnet", str(tool), *map(str, arguments)], check=True)

        command("extract-content", zips[0], installed)
        pack_id = zips[0].stem
        command("content-lock", installed, pack_id, "1.0.0", lock)
        value = json.loads(lock.read_text(encoding="utf-8"))
        value["bindings"] = {k: dict(type=v["type"], id=v["id"]) for k, v in bindings.items()}
        lock.write_text(json.dumps(value, indent=2), encoding="utf-8")
        sfx = next(v["id"] for k, v in bindings.items() if v["type"] == "sound")
        music = next(v["id"] for k, v in bindings.items() if v["type"] == "music")
        env = dict(os.environ, UO_CLIENT_DATA=args.data, UO_CONTENT_STORE=str(installed),
                   UO_CONTENT_LOCK=str(lock), GUO_SPLAT_STAGE=str(args.stage),
                   GUO_PROBE_SFX=str(sfx), GUO_PROBE_MUSIC=str(music))
        subprocess.run([args.godot, "--headless", "--path", str(root / "godot/GUO"),
                        "res://src/Store/ComfyContentProbe.tscn"],
                       env=env, check=True, timeout=300)
    print("[comfy] probe: PASS")
    raise SystemExit(0)
