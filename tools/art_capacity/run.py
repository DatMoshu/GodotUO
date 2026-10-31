"""Run the offline Labs art-capacity contract; never reads an install or opens sockets."""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from guo.config import load_config


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("doctor", "test"))
    args = parser.parse_args()
    dotnet = shutil.which("dotnet")
    if not dotnet:
        print("Install the .NET 8 SDK, then run launchers/dev/art_capacity.bat doctor.")
        return 2
    if args.command == "doctor":
        result = subprocess.run([dotnet, "--list-sdks"], capture_output=True, text=True)
        if result.returncode or not any(line.startswith(("8.", "9.", "10.")) for line in result.stdout.splitlines()):
            print("Install a .NET SDK supporting net8.0; rerun launchers/dev/art_capacity.bat doctor.")
            return 2
        print("[art-capacity] doctor OK: .NET SDK available; synthetic CPU contract only.")
        return 0
    build = load_config(ROOT).build / "art-capacity-tests"
    build.mkdir(parents=True, exist_ok=True)
    return subprocess.run([dotnet, "run", "--project", str(Path(__file__).with_name("ArtCapacityTests.csproj")),
                           "--artifacts-path", str(build), "--nologo"]).returncode


if __name__ == "__main__":
    raise SystemExit(main())
