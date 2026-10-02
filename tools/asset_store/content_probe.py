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
        lock = work / "content-lock.json"
        command("content-lock", installed, "sample-content-art", "1.0.0", lock)
        value = json.loads(lock.read_text())
        bindings = {"stone": ("static", 3701), "ground": ("land", 580), "slope": ("texmap", 1),
                    "panel": ("gump", 100), "palette": ("hue", 33), "chime": ("sound", 2000),
                    "ambience": ("music", 100), "lamp": ("light", 1), "platform": ("multi", 1),
                    "stone-data": ("tiledata", 3701), "figure": ("animation", 400)}
        value["bindings"] = {"sample-content-art:" + name: dict(type=kind, id=index) for name, (kind, index) in bindings.items()}
        lock.write_text(json.dumps(value, indent=2), encoding="utf-8")
        proof = root / "build/asset_packs/runtime-pixels.png"
        proof.parent.mkdir(parents=True, exist_ok=True)
        env = dict(os.environ, UO_CLIENT_DATA=args.data, UO_CONTENT_STORE=str(installed), UO_CONTENT_LOCK=str(lock), UO_CONTENT_PROOF_IMAGE=str(proof))
        subprocess.run([args.godot, "--headless", "--path", str(root / "godot/GUO"), "res://src/Store/StoreContentProbe.tscn"], env=env, check=True, timeout=90)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
