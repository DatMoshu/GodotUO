r"""Photograph any UO client on a tour the shard drives.

    python tools\ab_compare\tour.py --manifest DIR --character NAME --client "NAME|FOLDER|COMMAND LINE" [...]

For clients nothing can be compiled into, such as a binary from someone else.
The shard's dev tour (patch 0026) moves the listed characters from place to
place and writes a stamp at each stop; this writes the manifest, starts the
clients, and photographs each window when a stamp appears. Nothing types into
a client and no window needs focus. The shard must have been started with
GUO_PHOTO_TOUR set to DIR\tour.json. See README.md.
"""
from __future__ import annotations

import argparse
import ctypes
import importlib.util
import json
import shlex
import subprocess
import sys
import time
from ctypes import wintypes
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

MANIFEST = "tour.json"
STAMPS = "stamps"


def manifest(places, characters, settle_ms: int, capture_ms: int, start_ms: int) -> dict:
    """What the shard reads: who to move, where, and how long to hold each stop."""
    return {
        "characters": list(characters),
        "start_ms": start_ms,
        "settle_ms": settle_ms,
        "capture_ms": capture_ms,
        "stops": [{"name": p.name, "x": p.x, "y": p.y, "z": p.z, "light": p.light} for p in places],
    }


def parse_client(text: str) -> tuple[str, str, list[str]]:
    """'NAME|FOLDER|COMMAND LINE': a label, its working folder and what to run."""
    parts = text.split("|", 2)
    if len(parts) != 3 or not all(parts):
        raise ValueError(f"--client wants NAME|FOLDER|COMMAND LINE, not {text!r}")
    # posix=False keeps Windows backslashes, and keeps the quotes as well.
    return parts[0], parts[1], [word.strip('"') for word in shlex.split(parts[2], posix=False)]


def stamp_name(index: int, name: str) -> str:
    return f"{index:02d}_{name}.ready"


def wait_for_stamp(path: Path, timeout: float, alive=lambda: True) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if path.exists():
            return True
        if not alive():
            return False
        time.sleep(0.1)
    return False


def grab(hwnd, path: Path) -> None:
    """The window's own contents: it need not be in front or even visible."""
    from PIL import Image

    u32 = ctypes.windll.user32
    gdi = ctypes.windll.gdi32
    h = wintypes.HWND(int(hwnd) if not hasattr(hwnd, "value") else hwnd.value)
    r = wintypes.RECT()
    u32.GetClientRect(h, ctypes.byref(r))
    w, g = r.right, r.bottom
    hdc = u32.GetDC(h)
    mem = gdi.CreateCompatibleDC(hdc)
    bmp = gdi.CreateCompatibleBitmap(hdc, w, g)
    gdi.SelectObject(mem, bmp)
    u32.PrintWindow(h, mem, 3)
    buf = ctypes.create_string_buffer(w * g * 4)
    hdr = (ctypes.c_uint32 * 10)(40, w, (-g) & 0xFFFFFFFF, 1 | (32 << 16), 0, 0, 0, 0, 0, 0)
    gdi.GetDIBits(mem, bmp, 0, g, buf, hdr, 0)
    path.parent.mkdir(parents=True, exist_ok=True)
    Image.frombuffer("RGBA", (w, g), buf, "raw", "BGRA", 0, 1).convert("RGB").save(path)
    gdi.DeleteObject(bmp)
    gdi.DeleteDC(mem)
    u32.ReleaseDC(h, hdc)


def _ab():
    spec = importlib.util.spec_from_file_location("ab_compare_tour", HERE / "run.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest", required=True, help="the folder GUO_PHOTO_TOUR's tour.json is in")
    ap.add_argument("--character", action="append", required=True, help="a character to move; repeat for several")
    ap.add_argument("--client", action="append", required=True, help="NAME|FOLDER|COMMAND LINE; repeat for several")
    ap.add_argument("--out", required=True, help="pictures land in OUT\\PLACE\\NAME.png")
    ap.add_argument("--place", action="append", help="only this place; may be repeated")
    ap.add_argument("--settle", type=float, default=5.0, help="seconds after the jump before the stamp")
    ap.add_argument("--start", type=float, default=15.0, help="seconds after every character is online")
    ap.add_argument("--timeout", type=float, default=180.0, help="seconds to wait for the first stop")
    args = ap.parse_args()

    ab = _ab()
    places = ab.PLACES
    if args.place:
        places = [p for p in ab.PLACES if p.name in set(args.place)]
    if not places:
        print("[tour] no such place")
        return 1

    folder = Path(args.manifest)
    stamps = folder / STAMPS
    stamps.mkdir(parents=True, exist_ok=True)
    for old in stamps.glob("*"):
        old.unlink()
    plan = folder / MANIFEST
    plan.write_text(json.dumps(manifest(places, args.character, int(args.settle * 1000), 2000,
                                        int(args.start * 1000)), indent=2), encoding="utf-8")

    clients = [parse_client(c) for c in args.client]
    procs = []
    code = 0
    try:
        for name, cwd, command in clients:
            procs.append((name, subprocess.Popen(command, cwd=cwd, stdout=subprocess.DEVNULL,
                                                 stderr=subprocess.DEVNULL)))
        windows = {}
        end = time.time() + args.timeout
        while time.time() < end and len(windows) < len(procs):
            for name, proc in procs:
                if name not in windows:
                    hwnd = ab.find_window(proc.pid)
                    if hwnd:
                        windows[name] = hwnd
            time.sleep(0.5)
        if len(windows) < len(procs):
            print("[tour] not every client opened a window")
            return 1

        out = Path(args.out)
        wait = args.timeout
        for index, place in enumerate(places):
            if not wait_for_stamp(stamps / stamp_name(index, place.name), wait):
                print(f"[tour] no stamp for {place.name}: is the shard running with GUO_PHOTO_TOUR set?")
                return 1
            wait = args.settle + 30.0
            for name, hwnd in windows.items():
                grab(hwnd, out / place.name / f"{name}.png")
            print(f"[tour] {place.name}", flush=True)
    finally:
        plan.unlink(missing_ok=True)
        for _, proc in procs:
            proc.terminate()
    return code


if __name__ == "__main__":
    raise SystemExit(main())
