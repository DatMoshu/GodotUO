"""Run real profile migrations in headless Godot, without client data or profiles."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import load_config


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--dotnet", default=shutil.which("dotnet") or "dotnet")
    args = parser.parse_args()
    config = load_config()
    if not args.no_build:
        result = subprocess.run([args.dotnet, "build", str(config.godot_project / "GUO.csproj"), "-v", "q"])
        if result.returncode:
            return 1
    for platform in ("desktop", "mobile", "web"):
        result = subprocess.run([
            str(config.godot_console_exe), "--headless", "--path", str(config.godot_project),
            "res://src/Tests/ProfileMigrationProbe.tscn",
        ], env=dict(os.environ, UO_PROFILE_PLATFORM=platform), capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
        summary = [line for line in result.stdout.splitlines() if "PROFILE MIGRATIONS" in line]
        print("\n".join(summary))
        if result.returncode or not any("PASS:" in line for line in summary):
            print(result.stdout, result.stderr)
            return 1
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, subprocess.TimeoutExpired) as exc:
        print("PROFILE MIGRATIONS FAIL:", exc)
        raise SystemExit(1)
