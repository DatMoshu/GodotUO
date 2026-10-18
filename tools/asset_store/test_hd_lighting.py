"""R3 packaging and native-path regression checks; no UO installation required."""
import json
from pathlib import Path
import subprocess
import zipfile

import pytest
from hd_lighting_pack import build
from pack import parse_manifest

ROOT = Path(__file__).resolve().parents[2]


def test_hd_lighting_pack_is_hashed_typed_and_reproducible(tmp_path):
    a, b = build(tmp_path / "a.zip"), build(tmp_path / "b.zip")
    assert a.read_bytes() == b.read_bytes()
    with zipfile.ZipFile(a) as archive:
        manifest = parse_manifest(archive.read("manifest.json"))
        assert manifest["components"] == [{"id": "lighting", "type": "postfx", "target": "client", "entry": "hd_light.json"}]
        assert len([p for p in manifest["files"] if p.endswith(".gdshader")]) == 6
        assert b"e4a3cd35" in archive.read("PROVENANCE.txt")
        assert all(b"hint_screen_texture, filter_nearest" in archive.read(p) for p in manifest["files"] if p.endswith(".gdshader"))
        manifest["components"][0]["type"] = "presentation"
        with pytest.raises(ValueError, match="shader files"):
            parse_manifest(json.dumps(manifest).encode())
        manifest["components"][0]["type"] = "postfx"
        manifest["components"][0]["target"] = "shared"
        with pytest.raises(ValueError, match="client JSON"):
            parse_manifest(json.dumps(manifest).encode())


def method(source, signature):
    start = source.index(signature)
    start = source.index("{", start)
    depth = 1
    end = start + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def test_classic_light_collection_body_is_preserved():
    # Frozen base, independent of a future branch HEAD: the Classic method
    # must retain every rule after the one guarded delegation to the L1 seam.
    path = "godot/GUO/src/Game/Scenes/GameScene.cs"
    before = subprocess.check_output(["git", "show", f"f7f05bd6:{path}"]).decode()
    after = (ROOT / path).read_text(encoding="utf-8")
    native = method(after, "public void AddLight(")
    hook = "\n            // PORT DEVIATION (GUO): ADR-0027 optional, verified-pack HD path.\n            if (UseHDLight) { AddHdLight(obj, lightObject, x, y); return; }\n"
    assert native.replace(hook, "", 1) == method(before, "public void AddLight(")


def test_hd_shaders_are_pack_content_only():
    assert not list((ROOT / "godot/GUO/postfx/shaders").glob("hd_*.gdshader"))
