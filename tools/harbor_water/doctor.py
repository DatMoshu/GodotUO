"""Check the opt-in Rust ripple backend on this checkout."""
import ctypes
from pathlib import Path
import shutil
import sys


def main():
    root = Path(__file__).resolve().parents[2]
    name = {"win32": "guo_harbor_water.dll", "darwin": "libguo_harbor_water.dylib"}.get(sys.platform, "libguo_harbor_water.so")
    library = root / "godot/GUO/.godot/mono/temp/bin/Debug" / name
    if not shutil.which("cargo"):
        print("Install the Rust toolchain with Cargo and a platform linker to build the optional water solver.")
        return 1
    try:
        kernel = ctypes.CDLL(str(library))
        kernel.guo_water_step_v1
    except (OSError, AttributeError) as exc:
        print(f"Build and stage the native library beside the Debug GUO.dll. See src/Labs/HarborWater/README.md. {exc}")
        return 1
    print(f"Rust water backend ready: {library}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
