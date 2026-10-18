"""Build the opt-in, typed JarJar HD lighting pack without network access."""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from asset_store.pack import verify
from asset_store.postfx_pack import preview
from guo.config import load_config

SOURCE = ROOT / "design/packs/jarjar-hd-lighting"


def build(out):
    manifest = json.loads((SOURCE / "pack.json").read_text(encoding="utf-8"))
    payload = {p.name: p.read_bytes() for p in SOURCE.iterdir() if p.suffix == ".gdshader" or p.name in {"hd_light.json", "LICENSE.txt", "PROVENANCE.txt"}}
    payload["preview.png"] = preview()
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
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    print(build(args.out or load_config(ROOT).build / "asset_packs/jarjar-hd-lighting-1.0.0.zip"))


if __name__ == "__main__":
    main()
