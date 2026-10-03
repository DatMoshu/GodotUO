"""guo-mapgen entry point: build the map generator CLI if needed, then run it.

    python tools/mapgen/run.py schema
    python tools/mapgen/run.py run --out build/mapgen/runs/first --size 1024 --seed 42 --step-previews
    python tools/mapgen/run.py export --run build/mapgen/runs/first
    python tools/mapgen/run.py --build-only

The CLI's contract (commands, JSON lines, the run folder) is docs/data_formats.md section 26.
UO_CLIENT_DATA and UO_MAPGEN_DATA come from the usual config (environment, config.local.bat,
config.bat) and are passed to the generator, so a run here matches a run from the editor.
"""
from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from guo.config import load_config  # noqa: E402

PROJECT = HERE / "cli" / "GuoMapGen.Cli.csproj"


def output_dir(root: Path) -> Path:
    return root / "build" / "mapgen" / "cli"


def cli_path(root: Path) -> Path:
    exe = "guo-mapgen.exe" if os.name == "nt" else "guo-mapgen"
    return output_dir(root) / exe


def newest_source() -> float:
    newest = 0.0
    for folder in ("cli", "MapGen", "data"):
        for path in (HERE / folder).rglob("*"):
            if path.is_file() and "bin" not in path.parts and "obj" not in path.parts:
                newest = max(newest, path.stat().st_mtime)
    return newest


def build(root: Path, force: bool = False) -> Path:
    exe = cli_path(root)
    if not force and exe.exists() and exe.stat().st_mtime >= newest_source():
        return exe
    print("[mapgen] building guo-mapgen ...", file=sys.stderr)
    result = subprocess.run(
        ["dotnet", "build", str(PROJECT), "-c", "Release", "-o", str(output_dir(root)), "-nologo", "-v", "q"],
        stdout=sys.stderr, stderr=sys.stderr,
    )
    if result.returncode != 0:
        sys.exit(f"[mapgen] build failed (exit {result.returncode})")
    exe.touch()
    return exe


def main(argv: list[str]) -> int:
    cfg = load_config()
    if argv[:1] == ["--build-only"]:
        print(build(cfg.root, force=True))
        return 0
    exe = build(cfg.root)
    env = dict(os.environ)
    env.setdefault("UO_CLIENT_DATA", str(cfg.client_data))
    if cfg.mapgen_data is not None:
        env.setdefault("MAPGEN_DATA_DIR", str(cfg.mapgen_data))
    return subprocess.run([str(exe), *argv], env=env).returncode


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
