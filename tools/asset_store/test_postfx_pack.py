"""Store-ready content remains reproducible and outside client built-ins."""
import json
from pathlib import Path
import sys
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store.postfx_pack import build, SOURCE, ROOT


def test_postfx_pack_is_reproducible_and_complete(tmp_path):
    first, second = build(tmp_path / "first.zip"), build(tmp_path / "second.zip")
    assert first.read_bytes() == second.read_bytes()
    with zipfile.ZipFile(first) as archive:
        manifest = json.loads(archive.read("manifest.json"))
        assert manifest["kind"] == "postfx" and manifest["author"].startswith("MeesaJarJar")
        presets = [name for name in manifest["files"] if name.endswith(".json")]
        shaders = [name for name in manifest["files"] if name.endswith(".gdshader")]
        assert len(presets) == 32 and len(shaders) == 29
        for name in presets + shaders:
            assert archive.read(name) == (SOURCE / name).read_bytes()
            folder = "presets" if name.endswith(".json") else "shaders"
            assert not (ROOT / "godot/GUO/postfx" / folder / name).exists()
        assert "LICENSE.txt" in manifest["files"]
        assert archive.read("preview.png").startswith(b"\x89PNG\r\n\x1a\n")
