"""Build and publish an original, permissively licensed Razor CE starter pack.

python tools/asset_store/razor_scripts.py [--store-dir DIR]
Standard library only. Reads no game data and executes no scripts.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
import sys
import tempfile
import zipfile
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import load_config
from asset_store.run import publish

SCRIPTS = {
    "scripts/welcome.razor": "// A private journal message; no speech is sent to other players.\nsysmsg 'Hello from your GUO script pack'\npause 1000\nsysmsg 'Ready for adventure'\n",
    "scripts/open-backpack.razor": "// Open your own backpack using the normal client action.\ndclick 'backpack'\n",
    "scripts/heal-self.razor": "// Requires Heal, mana and reagents (or shard equivalents).\ncast 'heal'\nwaitfortarget 5000\ntarget 'self'\n",
}
LICENCE = """BSD 2-Clause License

Copyright (c) 2026, GUO contributors
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
"""


def preview() -> bytes:
    """Original code-window icon, rasterized without client art or image libraries."""
    width, height = 480, 270
    pixels = bytearray(bytes((20, 25, 23)) * width * height)

    def rect(x, y, w, h, color):
        for row in range(y, y + h):
            start = (row * width + x) * 3
            pixels[start:start + w * 3] = bytes(color) * w

    rect(36, 30, 408, 210, (69, 83, 66))
    rect(39, 33, 402, 204, (32, 41, 34))
    rect(39, 33, 402, 33, (48, 61, 46))
    for x in (52, 72, 92):
        rect(x, 45, 9, 9, (223, 187, 119))
    for y, indent, length in ((91, 0, 130), (126, 25, 220), (161, 25, 165), (196, 0, 95)):
        rect(60, y, 13, 9, (171, 181, 172))
        rect(96 + indent, y, length, 9, (181, 214, 155))
    raw = b"".join(b"\0" + pixels[y * width * 3:(y + 1) * width * 3] for y in range(height))

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xffffffff)

    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def make_pack(path: Path, version="1.0.0", managed=False) -> Path:
    files = {name: source.encode("utf-8") for name, source in SCRIPTS.items()}
    files.update({"preview.png": preview(), "LICENSE.txt": LICENCE.encode("utf-8"),
                  "README.txt": b"Original GUO Razor CE starters. Install, Browse scripts, preview, then Add to my scripts.\nInstalling does not execute anything. Heal depends on your character and shard.\nPersonal copies survive updates and uninstall; retain their accompanying licence files when sharing.\nThese are not Razor Enhanced Python or ASP.NET templates.\n"})
    manifest = dict(schema="guo/store-pack@1", id="guo-razor-starters", version=version,
                    kind="razor-script", title="Razor CE essentials", author="GUO contributors",
                    licence="BSD-2-Clause", min_profile_version=6, preview="preview.png",
                    files={name: hashlib.sha256(data).hexdigest() for name, data in files.items()})
    if managed:
        capabilities = {"welcome": ["client.message"], "open-backpack": ["player.inventory.use"],
                        "heal-self": ["player.spell.cast", "player.target"]}
        manifest.update(schema="guo/store-pack@2", kind="content", target="client", dependencies={},
                        components=[dict(id=Path(name).stem, type="script", target="client", entry=name,
                                         language="razor-ce", runtime="guo-razor", runtime_version="0.1.0",
                                         capabilities=capabilities[Path(name).stem]) for name in SCRIPTS])
    files["manifest.json"] = json.dumps(manifest, sort_keys=True, indent=2).encode("utf-8")
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in sorted(files.items()):
            info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--store-dir", type=Path)
    parser.add_argument("--managed", action="store_true", help="Publish v2 managed components at version 2.0.0")
    args = parser.parse_args()
    root = args.store_dir or load_config().store_dir
    with tempfile.TemporaryDirectory(prefix="guo-razor-pack-") as temporary:
        publish(make_pack(Path(temporary) / "razor-starters.zip", "2.0.0" if args.managed else "1.0.0", args.managed), root)
    print(f"Published guo-razor-starters to {root}")


if __name__ == "__main__":
    main()
