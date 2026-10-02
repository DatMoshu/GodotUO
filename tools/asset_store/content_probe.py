"""Build/install original examples and verify their real Godot consumers."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store.content_examples import build


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", required=True, help="Blocking godot-console executable")
    parser.add_argument("--shared-world", action="store_true", help="Verify the combined client/server map example")
    parser.add_argument("--collision-world", action="store_true", help="Verify combined maps and shared static collision metadata")
    parser.add_argument("--deployment-ui", action="store_true", help="Exercise real deployment controls and preservation failures")
    parser.add_argument("--visual", action="store_true", help="Render the deployment UI proof image instead of running headless")
    parser.add_argument("--compact", action="store_true", help="Use a 640x480 deployment UI proof viewport")
    parser.add_argument("--server-export", type=Path, help="Write a new ModernUO export from the same verified deployment")
    parser.add_argument("--data", default=os.environ.get("UO_CLIENT_DATA"), required=not bool(os.environ.get("UO_CLIENT_DATA")))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    tool = root / "tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll"
    with tempfile.TemporaryDirectory(prefix="content-probe-", dir=root / "build") as temporary:
        work = Path(temporary)
        examples, installed = work / "examples", work / "store"
        build(examples)
        def command(*arguments):
            subprocess.run(["dotnet", str(tool), *map(str, arguments)], check=True)
        command("extract-content", examples / "sample-content-art.zip", installed)
        command("extract-content", examples / "sample-content-font.zip", installed)
        command("extract-content", examples / "sample-content-map.zip", installed)
        command("extract-content", examples / "sample-content-world.zip", installed)
        command("extract-content", examples / "sample-content-collision.zip", installed)
        lock = work / "content-lock.json"
        selected = "sample-content-collision" if args.collision_world else "sample-content-world" if args.shared_world else "sample-content-map"
        command("content-lock", installed, selected, "1.0.0", lock)
        value = json.loads(lock.read_text())
        bindings = {"stone": ("static", 3701), "ground": ("land", 580), "slope": ("texmap", 1),
                    "panel": ("gump", 100), "palette": ("hue", 33), "chime": ("sound", 2000),
                    "ambience": ("music", 100), "lamp": ("light", 1), "platform": ("multi", 1),
                    "stone-data": ("tiledata", 3701), "figure": ("animation", 400), "paperdoll": ("gump", 50400)}
        value["bindings"] = {"sample-content-art:" + name: dict(type=kind, id=index) for name, (kind, index) in bindings.items()}
        value["bindings"]["sample-content-font:letters"] = dict(type="font", id=0)
        value["bindings"][selected + ":courtyard"] = dict(type="map", id=0)
        if args.collision_world:
            value["bindings"][selected + ":barrier"] = dict(type="static", id=3702)
            value["bindings"][selected + ":barrier-data"] = dict(type="tiledata", id=3702)
        lock.write_text(json.dumps(value, indent=2), encoding="utf-8")
        if args.server_export:
            command("export-server", installed, lock, args.server_export.resolve())
        proof = root / "build/asset_packs/runtime-pixels.png"
        proof.parent.mkdir(parents=True, exist_ok=True)
        env = dict(os.environ, UO_CLIENT_DATA=args.data, UO_CONTENT_STORE=str(installed), UO_CONTENT_LOCK=str(lock), UO_CONTENT_PROOF_IMAGE=str(proof), UO_CONTENT_PROBE_LAND="3" if args.shared_world or args.collision_world else "580", UO_CONTENT_PROBE_COLLISION="1" if args.collision_world else "0")
        if args.visual:
            if not args.deployment_ui: parser.error("--visual requires --deployment-ui")
            env["UO_DEPLOYMENT_PROOF_IMAGE"] = str(root / "build/asset_packs" / ("deployment-ui-compact.png" if args.compact else "deployment-ui.png"))
        if args.compact: env["UO_DEPLOYMENT_PROOF_COMPACT"] = "1"
        scene = "StoreDeploymentProbe" if args.deployment_ui else "StoreContentProbe"
        subprocess.run([args.godot, *([] if args.visual else ["--headless"]), "--path", str(root / "godot/GUO"), f"res://src/Store/{scene}.tscn"], env=env, check=True, timeout=90)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
