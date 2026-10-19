"""Build the pixel, cel and realistic water styles as Store packs (ADR-0037 decision 6, WATER-04).

    python tools/asset_store/water_packs.py [--out DIR] [--store-dir DIR] [--deploy DIR]

Each pack under design/packs/water/<id>/ is a pack.json and one guo/water@1
style file: data for a model built into the client's shader, no code. The
ZIPs are byte-reproducible, their files hashed in the manifest, and checked by
the same verify the store uses. --store-dir publishes them into a local
catalogue; it is signed when UO_STORE_SIGNING_KEY is set (run.py index).
--deploy installs each through the C# installer (StoreSmoke extract-content)
into DIR/store and writes DIR/locks/<id>.json, one deployment per style, for
the client.render.water_pack scenario (UO_CONTENT_STORE / UO_CONTENT_LOCK).
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import zipfile
import zlib

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from asset_store.pack import verify  # noqa: E402
from asset_store.run import publish  # noqa: E402
from asset_store.water_style import validate_water  # noqa: E402

SOURCE = ROOT / "design/packs/water"
BAYER = (0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5)


def preview(style):
    """A 64 x 64 swatch drawn from the style's own numbers: bands of its tint
    in the model's manner (dithered, flat with a line, or smooth). Original,
    independent of any client installation."""
    tint = style["tint"]
    model = style["model"]
    grain = style["grain"]

    def pixel(x, y):
        v = y / 63.0
        if model == "pixel":
            d = BAYER[(y // grain) % 4 * 4 + (x // grain) % 4] / 16.0
            v = int(v * 4 + d) / 4.0
        elif model == "cel":
            v = int(v * 3) / 2.0
            if y % 21 == 20:
                v = 2.0
        k = 0.6 + 0.8 * v
        return tuple(max(0, min(255, int(c * k * 255))) for c in tint) + (255,)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    rows = b"".join(b"\0" + b"".join(bytes(pixel(x, y)) for x in range(64)) for y in range(64))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 64, 64, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def build_one(source, out_dir):
    manifest = json.loads((source / "pack.json").read_text(encoding="utf-8"))
    raw = (source / "water.json").read_bytes()
    style = validate_water(raw)
    assert [c["type"] for c in manifest["components"]] == ["water"], source
    payload = {"water.json": raw, "preview.png": preview(style)}
    manifest["files"] = {name: hashlib.sha256(data).hexdigest() for name, data in sorted(payload.items())}
    out = Path(out_dir) / f"{manifest['id']}-{manifest['version']}.zip"
    out.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in sorted({"manifest.json": json.dumps(manifest, indent=2).encode(), **payload}.items()):
            info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            info.external_attr = 0o100644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data)
    verify(out)
    return out


def build(out_dir):
    return [build_one(source, out_dir) for source in sorted(p for p in SOURCE.iterdir() if p.is_dir())]


def deploy(archives, target):
    """Install through the C# installer and pin one content lock per pack; returns {pack id: lock path}."""
    target = Path(target).resolve()
    store, locks = target / "store", target / "locks"
    locks.mkdir(parents=True, exist_ok=True)
    project = ROOT / "tools/asset_store/headless/StoreSmoke.csproj"
    dotnet = shutil.which("dotnet") or "dotnet"
    subprocess.run([dotnet, "build", str(project), "-v", "q", "-nologo"], check=True, capture_output=True)
    dll = project.parent / "bin/Debug/net8.0/StoreSmoke.dll"
    out = {}
    for archive in archives:
        with zipfile.ZipFile(archive) as z:
            manifest = json.loads(z.read("manifest.json"))
        if not (store / manifest["id"] / manifest["version"]).is_dir():
            subprocess.run([dotnet, str(dll), "extract-content", str(archive), str(store)], check=True, capture_output=True)
        lock = locks / f"{manifest['id']}.json"
        subprocess.run([dotnet, str(dll), "content-lock", str(store), manifest["id"], manifest["version"], str(lock)],
                       check=True, capture_output=True)
        out[manifest["id"]] = lock
    return store, out


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", type=Path, help="where the ZIPs go (default: <UO_BUILD_DIR>/asset_packs)")
    parser.add_argument("--store-dir", type=Path, help="Also publish to this local catalogue; no server or network")
    parser.add_argument("--deploy", type=Path, help="Also install each into DIR/store with a content lock in DIR/locks")
    args = parser.parse_args()
    if args.out is None:
        from guo.config import load_config
        args.out = load_config().build / "asset_packs"
    archives = build(args.out)
    for archive in archives:
        if args.store_dir:
            publish(archive, args.store_dir)
        print(f"Verified {archive}")
    if args.deploy:
        store, locks = deploy(archives, args.deploy)
        print(f"Installed into {store}. For each style, run the scenario with:")
        for pack_id, lock in locks.items():
            print(f"  UO_CONTENT_STORE={store}  UO_CONTENT_LOCK={lock}  --var style={pack_id}:sea")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
