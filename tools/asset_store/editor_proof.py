"""Capture the real editor displaying previously verified Store runtime evidence.

Run after the Store client and installed-background screenshots exist.
No desktop capture or keyboard/mouse automation is used.
"""
import os
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import load_config
from guo.process import no_activate


def main():
    config = load_config()
    directory = config.build / "screenshots"
    for name in ("store-client.png", "store-installed-background.png"):
        if not (directory / name).is_file():
            print(f"Missing screenshot: {name}")
            return 1
    environment = dict(os.environ, GUO_STORE_EDITOR_PROOF=str(directory))
    project = config.godot_project / "project.godot"
    original = project.read_bytes()
    try:
        with (directory / "store-editor.log").open("w", encoding="utf-8") as log:
            result = subprocess.run([
                str(config.godot_console_exe), "--editor", "--path", str(config.godot_project),
                "res://src/Store/StoreEditorProof.tscn",
            ], env=environment, stdout=log, stderr=subprocess.STDOUT, timeout=120, **no_activate())
        output = directory / "store-godot-editor.png"
        if result.returncode or not output.is_file():
            print("Editor capture failed; see build/screenshots/store-editor.log")
            return 1
        print(f"Editor evidence captured: {output}")
        return 0
    finally:
        if project.read_bytes() != original:
            project.write_bytes(original)


if __name__ == "__main__":
    raise SystemExit(main())
