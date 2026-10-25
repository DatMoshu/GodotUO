"""Build JarJar's opt-in screen effects as an ADR-0023 Store pack, never built-ins."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys
import zipfile
import zlib

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from asset_store.pack import verify
from asset_store.run import publish
from guo.config import load_config

SOURCE = ROOT / "design/packs/jarjar-postfx"


def preview():
    """A reproducible original checker, independent of any client installation."""
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    rows = b"".join(b"\0" + b"".join(bytes((26, 128, 128, 255) if (x // 8 + y // 8) % 2 else (30, 37, 40, 255))
                                    for x in range(64)) for y in range(64))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 64, 64, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b"")


def build(out):
    manifest = json.loads((SOURCE / "pack.json").read_text())
    presets = sorted(p for p in SOURCE.glob("*.json") if p.name != "pack.json")
    shaders = sorted(SOURCE.glob("*.gdshader"))
    assert len(presets) == 32 and len(shaders) == 29
    available = {p.stem for p in shaders} | {p.stem for p in (ROOT / "godot/GUO/postfx/shaders").glob("*.gdshader")}
    # The framework leaves a parameter manual when a state value is unavailable.
    # Facing is optional in this content pack; adding its client provider is out of scope.
    bindings = {"hp_ratio", "hp_missing", "stamina_ratio", "mana_ratio", "light_level", "war_mode", "weather", "time", "facing"}
    for path in presets:
        preset = json.loads(path.read_text())
        assert 0 < len(preset["passes"]) <= 8, path
        assert all(p["shader"] in available for p in preset["passes"]), path
        assert all(b["from"] in bindings for b in preset.get("bindings", [])), path
    payload = {p.name: p.read_bytes() for p in presets + shaders}
    payload["preview.png"] = preview()
    payload["LICENSE.txt"] = (ROOT / "LICENSE").read_text().split("-------------------------------------------------------------------------------")[0].encode()
    manifest["files"] = {name: hashlib.sha256(data).hexdigest() for name, data in sorted(payload.items())}
    out = Path(out)
    out.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in sorted({"manifest.json": json.dumps(manifest, indent=2).encode(), **payload}.items()):
            info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            info.external_attr = 0o100644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data)
    verify(out)
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=load_config(ROOT).build / "asset_packs/jarjar-postfx-1.0.0.zip")
    parser.add_argument("--store-dir", type=Path, help="Also publish to this local catalogue; no server or network")
    args = parser.parse_args()
    archive = build(args.out)
    if args.store_dir:
        publish(archive, args.store_dir)
    print(f"Verified {archive}: 32 presets, 29 shaders, opt-in postfx pack")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
