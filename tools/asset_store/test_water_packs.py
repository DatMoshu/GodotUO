"""The water style packs (WATER-04) build reproducibly, carry data only and publish into a signed store."""
import hashlib
import json
from pathlib import Path
import sys
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store import catalogue  # noqa: E402
from asset_store.run import make_index, publish  # noqa: E402
from asset_store.water_packs import SOURCE, build  # noqa: E402
from asset_store.water_style import MODELS, validate_water  # noqa: E402


def test_four_styles_reproducible_and_data_only(tmp_path):
    first, second = build(tmp_path / "a"), build(tmp_path / "b")
    assert [p.name for p in first] == ["guo-water-cel-1.0.0.zip", "guo-water-classicplus-1.0.0.zip", "guo-water-pixel-1.0.1.zip", "guo-water-realistic-1.0.0.zip"]
    models = set()
    for a, b in zip(first, second):
        assert a.read_bytes() == b.read_bytes()
        with zipfile.ZipFile(a) as archive:
            manifest = json.loads(archive.read("manifest.json"))
            assert manifest["schema"] == "guo/store-pack@2" and manifest["licence"] == "CC0-1.0"
            assert set(archive.namelist()) == {"manifest.json", "water.json", "preview.png"}
            for name, digest in manifest["files"].items():
                assert hashlib.sha256(archive.read(name)).hexdigest() == digest
            [component] = manifest["components"]
            assert component == {"id": "sea", "type": "water", "target": "client", "entry": "water.json"}
            style = validate_water(archive.read("water.json"))
            assert archive.read("water.json") == (SOURCE / manifest["id"] / "water.json").read_bytes()
            models.add(style["model"])
    assert models == set(MODELS) - {"uo"}


def test_published_into_a_signed_store(tmp_path):
    key = tmp_path / "throwaway.key"
    public = catalogue.keygen(key)
    signer = (catalogue.read_secret(key), {"id": "water-test", "title": "Water test"}, None)
    store = tmp_path / "store"
    for archive in build(tmp_path / "zips"):
        publish(archive, store, signer)
    make_index(store, signer)
    index = catalogue.check((store / "index.json").read_bytes(), (store / "index.json.sig").read_text(), public)
    listed = {e["manifest"]["id"] for e in index["packs"]}
    assert listed == {"guo-water-cel", "guo-water-classicplus", "guo-water-pixel", "guo-water-realistic"}
    for entry in index["packs"]:
        zip_path = store / "packs" / entry["manifest"]["id"] / (entry["manifest"]["version"] + ".zip")
        assert hashlib.sha256(zip_path.read_bytes()).hexdigest() == entry["sha256"]
