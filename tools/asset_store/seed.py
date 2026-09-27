"""Reproducibly package the ten GUO-owned CC0 loops; never reads UO data."""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import load_config
from asset_store.pack import PACK_SCHEMA, SCREENSAVER_MIN_PROFILE
from asset_store.run import publish


def seed(root, media):
    entries = json.loads((media / "backgrounds.json").read_text(encoding="utf-8"))
    if len(entries) != 10:
        raise ValueError("Expected the ten shipped CC0 backgrounds")
    licence = (media / "LICENSE.md").read_bytes()
    with tempfile.TemporaryDirectory() as temporary:
        for item in entries:
            payload = {"loop.ogv": (media / item["video"]).read_bytes(), "still.png": (media / item["still"]).read_bytes(), "LICENSE.txt": licence}
            manifest = dict(schema=PACK_SCHEMA, id=item["name"], version="1.0.0", kind="background", title=item["title"], author="GUO contributors", licence="CC0-1.0", min_profile_version=6, preview="still.png", files={n: hashlib.sha256(data).hexdigest() for n, data in payload.items()})
            path = Path(temporary) / (item["name"] + ".zip")
            with zipfile.ZipFile(path, "w") as archive:
                for name, data in {"manifest.json": (json.dumps(manifest, indent=2) + "\n").encode(), **payload}.items():
                    info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
                    info.compress_type = zipfile.ZIP_DEFLATED
                    info.external_attr = 0o100644 << 16
                    archive.writestr(info, data)
            publish(path, root)
            print("Seeded", item["name"])


def seed_screensavers(root, media):
    """Publish the screensaver loops marked "store_only" in screensavers.json.

    The others ship inside the client (assets/screensavers); a store-only
    loop is installed from the Store, which is how a tester proves the
    screensaver pack kind end to end.
    """
    manifest_path = media / "screensavers.json"
    if not manifest_path.exists():
        return
    licence = (media / "LICENSE.md").read_bytes() if (media / "LICENSE.md").exists() else None
    with tempfile.TemporaryDirectory() as temporary:
        for item in json.loads(manifest_path.read_text(encoding="utf-8")):
            if not item.get("store_only"):
                continue
            payload = {"loop.ogv": (media / item["video"]).read_bytes(), "still.png": (media / item["still"]).read_bytes()}
            if licence:
                payload["LICENSE.txt"] = licence
            manifest = dict(schema=PACK_SCHEMA, id=item["name"], version="1.0.0", kind="screensaver", title=item["title"],
                            author=item.get("author", "GUO contributors"), licence=item.get("licence", "CC0-1.0"),
                            min_profile_version=SCREENSAVER_MIN_PROFILE, preview="still.png",
                            files={n: hashlib.sha256(data).hexdigest() for n, data in payload.items()})
            path = Path(temporary) / (item["name"] + ".zip")
            with zipfile.ZipFile(path, "w") as archive:
                for name, data in {"manifest.json": (json.dumps(manifest, indent=2) + "\n").encode(), **payload}.items():
                    info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
                    info.compress_type = zipfile.ZIP_DEFLATED
                    info.external_attr = 0o100644 << 16
                    archive.writestr(info, data)
            publish(path, root)
            print("Seeded screensaver", item["name"])


if __name__ == "__main__":
    config = load_config()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--store-dir", type=Path, default=config.store_dir)
    args = parser.parse_args()
    seed(args.store_dir, config.root / "godot/GUO/assets/backgrounds")
    seed_screensavers(args.store_dir, config.root / "godot/GUO/assets/screensavers")
