"""Tests for tools/world: the texmap patch set on a made-up install (no game data needed).

    python tools/world/test_world.py
"""

from __future__ import annotations

import importlib.util
import struct
import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("world_run", HERE / "run.py")
world = importlib.util.module_from_spec(spec)
spec.loader.exec_module(world)
uoart = world.uoart

FAILS: list[str] = []


def check(ok: bool, what: str) -> None:
    print(("ok   " if ok else "FAIL ") + what)
    if not ok:
        FAILS.append(what)


def fake_install(folder: Path, entries: int = 8) -> None:
    """texidx.mul and texmaps.mul with `entries` 64x64 textures, each one colour (its index)."""
    idx, mul = bytearray(), bytearray()
    for n in range(entries):
        idx += struct.pack("<iii", len(mul), 0x2000, 0)
        mul += struct.pack("<H", n + 1) * (64 * 64)
    (folder / "texidx.mul").write_bytes(bytes(idx))
    (folder / "texmaps.mul").write_bytes(bytes(mul))
    (folder / "TexTerr.def").write_text("# index {group}\n6 {2} 1\n", encoding="latin-1")


def main() -> int:
    from PIL import Image

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        data, out, project = tmp / "install", tmp / "export", tmp / "project"
        for f in (data, out, project / "assets" / "art" / "texmaps"):
            f.mkdir(parents=True)
        fake_install(data)
        before = {f: (data / f).read_bytes() for f in ("texidx.mul", "texmaps.mul")}
        # one texmap: a 64x64 gradient, replacing entry 3
        png = project / "assets" / "art" / "texmaps" / "0x0003.png"
        im = Image.new("RGB", (64, 64))
        im.putdata([(x * 4, y * 4, 128) for y in range(64) for x in range(64)])
        im.save(png)
        texmaps = uoart.project_assets(project)["texmaps"]
        check([i for i, _ in texmaps] == [3], "the project's texmaps are found by index")

        lines, files = [], {}
        check(world.export_texmaps(data, texmaps, out, lines, files) == 0, "a 64x64 texmap exports")
        check(any(line.startswith("texmaps.mul=") for line in lines) and any(line.startswith("texidx.mul=") for line in lines),
              "files_override gains texmaps.mul and texidx.mul")
        cfg = SimpleNamespace(client_data=data)
        check(world.verify_texmaps(cfg, texmaps, out) == 0, "verify decodes it back and finds every other entry untouched")
        check(all((data / f).read_bytes() == b for f, b in before.items()), "the install is not written")

        # the loader's reading of the patched entry: offset past the install's data, 0x2000 long
        at, length, _ = struct.unpack_from("<iii", (out / "texidx.mul").read_bytes(), 3 * uoart.TEXIDX_RECORD)
        size, px = uoart.decode_texmap((out / "texmaps.mul").read_bytes()[at:at + length])
        check(at == len(before["texmaps.mul"]) and length == 0x2000 and size == 64, "the entry is appended, 64x64")
        check(px[0] == uoart.to16(0, 0, 128, 255, True) and px[63] == uoart.to16(252, 0, 128, 255, True),
              "its pixels are the PNG's in UO colour, row by row")

        # a changed byte is caught
        raw = bytearray((out / "texmaps.mul").read_bytes())
        raw[at + 10] ^= 0xFF
        (out / "texmaps.mul").write_bytes(bytes(raw))
        check(world.verify_texmaps(cfg, texmaps, out) == 1, "a changed pixel fails verify")

        # a wrong size or an index past texidx is refused
        bad = project / "assets" / "art" / "texmaps" / "0x0004.png"
        Image.new("RGB", (44, 44)).save(bad)
        check(world.export_texmaps(data, [(4, bad)], out, [], {}) == 1, "a texmap that is not 64 or 128 square is refused")
        check(world.export_texmaps(data, [(99, png)], out, [], {}) == 1, "an index past the install's texidx is refused")
        check(world.texterr_redirected(data) == {6}, "TexTerr.def's redirected indices are read")

    print(f"test_world: {'OK' if not FAILS else 'FAILED'} ({len(FAILS)} failing)")
    return 0 if not FAILS else 1


if __name__ == "__main__":
    sys.exit(main())
