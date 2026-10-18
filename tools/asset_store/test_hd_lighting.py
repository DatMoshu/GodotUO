"""R3 packaging and native-path regression checks; no UO installation required."""
import json
from pathlib import Path
import subprocess
import zipfile

import pytest
from hd_lighting_pack import build
from pack import parse_manifest, verify

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
        with pytest.raises(ValueError, match="lighting shader"):
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


def test_v2_lighting_payload_contract_both_languages(tmp_path):
    """Fully hashed malicious payloads must fail in both installers, before mount."""
    import hashlib
    source = build(tmp_path / "source.zip")
    with zipfile.ZipFile(source) as archive:
        original = {n: archive.read(n) for n in archive.namelist()}
    corpus = tmp_path / "corpus"; corpus.mkdir()
    cases = {
        "valid": ({}, True),
        "include": ({"hd_sun.gdshader": b'# include "res://outside.gdshaderinc"'}, False),
        "resource": ({"hd_sun.gdshader": b'// user://mutable'}, False),
        "invalid-utf8": ({"hd_sun.gdshader": b'\xff'}, False),
        "empty-shader": ({"hd_sun.gdshader": b' '}, False),
        "oversize": ({"hd_sun.gdshader": b'x' * 65537}, False),
        "size-boundary": ({"hd_sun.gdshader": b'//' + b'x' * 65534}, True),
        "bad-preset": ({"hd_light.json": b'{'}, False),
        "escape-pass": ({"hd_light.json": b'{"passes":[{"shader":"../hd_tonemap"}]}'}, False),
        "duplicate-pass": ({"hd_light.json": b'{"passes":[{"shader":"hd_tonemap"},{"shader":"hd_tonemap"}]}'}, False),
        "double-composite": ({"hd_light.json": b'{"passes":[{"shader":"hd_tonemap"},{"shader":"hd_night_vision"}]}'}, False),
        "bad-bindings": ({"hd_light.json": b'{"passes":[{"shader":"hd_tonemap"}],"bindings":{}}'}, False),
        "duplicate-json-key": ({"hd_light.json": b'{"passes":[],"passes":[{"shader":"hd_tonemap"}]}'}, False),
        "disabled-composite": ({"hd_light.json": b'{"passes":[{"shader":"hd_tonemap","enabled":false}]}'}, False),
    }
    for name, parameters in {
        "param-resource": {"texture": "res://outside.png"},
        "param-relative": {"texture": "mutable.png"},
        "param-nested": {"color": [1, {"texture": "user://outside.png"}]},
        "param-number": {"strength": 1},
        "param-nonfinite": {"strength": float("inf")},
        "param-overflow": {"strength": 1e300},
        "param-null": None,
        "param-array": [],
        "empty-params": {},
    }.items():
        raw = json.dumps({"passes": [{"shader": "hd_tonemap", "params": parameters}]}).encode()
        cases[name] = ({"hd_light.json": raw}, name == "empty-params")
    cases["unknown-root"] = ({"hd_light.json": b'{"passes":[{"shader":"hd_tonemap"}],"texture":"res://outside.png"}'}, False)
    cases["unknown-pass"] = ({"hd_light.json": b'{"passes":[{"shader":"hd_tonemap","texture":"outside.png"}]}'}, False)
    cases["bad-name"] = ({"hd_light.json": b'{"name":{},"passes":[{"shader":"hd_tonemap"}]}'}, False)
    for name, (changes, accepted) in cases.items():
        payload = dict(original); payload.update(changes)
        m = json.loads(payload["manifest.json"])
        for entry in changes:
            m["files"][entry] = hashlib.sha256(payload[entry]).hexdigest()
        payload["manifest.json"] = json.dumps(m).encode()
        path = corpus / (name + ".zip")
        with zipfile.ZipFile(path, "w") as archive:
            for entry, raw in payload.items(): archive.writestr(entry, raw)
        if accepted: verify(path)
        else:
            with pytest.raises((ValueError, UnicodeError)): verify(path)
    # Legacy v1 PostFx still permits its pre-existing arbitrary shader contract.
    payload = dict(original)
    m = json.loads(payload["manifest.json"])
    m.update(schema="guo/store-pack@1", kind="postfx")
    for field in ("target", "components", "dependencies"): m.pop(field, None)
    payload["hd_sun.gdshader"] = b'#include "res://legacy.gdshaderinc"'
    payload["hd_light.json"] = b'{"passes":[{"shader":"hd_tonemap","params":{"texture":"res://legacy.png"}}]}'
    m["files"]["hd_sun.gdshader"] = hashlib.sha256(payload["hd_sun.gdshader"]).hexdigest()
    m["files"]["hd_light.json"] = hashlib.sha256(payload["hd_light.json"]).hexdigest()
    payload["manifest.json"] = json.dumps(m).encode()
    with zipfile.ZipFile(corpus / "legacy-include.zip", "w") as archive:
        for entry, raw in payload.items(): archive.writestr(entry, raw)
    verify(corpus / "legacy-include.zip")
    cases["legacy-include"] = ({}, True)
    # Hash tampering and undeclared ZIP entries exercise the common verifier too.
    for name, entry, raw in [("tampered", "hd_sun.gdshader", b"changed"), ("undeclared", "extra.gdshader", b"code")]:
        payload = dict(original); payload[entry] = raw
        path = corpus / (name + ".zip")
        with zipfile.ZipFile(path, "w") as archive:
            for entry, raw in payload.items(): archive.writestr(entry, raw)
        with pytest.raises(ValueError): verify(path)
        cases[name] = ({}, False)
    result = tmp_path / "result.json"
    subprocess.run(["dotnet", str(ROOT / "tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll"), "corpus", str(corpus), str(result)], check=True)
    verdicts = json.loads(result.read_text())
    for name, (_, accepted) in cases.items():
        assert verdicts[name + ".zip"]["result"] == ("accept" if accepted else "reject"), (name, verdicts[name + ".zip"])
