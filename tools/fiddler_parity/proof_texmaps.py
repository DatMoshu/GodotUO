"""Independent ClassicUO texture decoding and DEF provenance proofs."""
import hashlib
import base64
import json
from pathlib import Path
import struct
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run as parity
import textures
import replacement


def payload(seed, size=64):
    return b"".join(struct.pack("<H", (seed + i * 127) & 65535) for i in range(size * size))


def rgba_hash(raw):
    decoded = bytearray()
    for (color,) in struct.iter_unpack("<H", raw):
        decoded += bytes(((color >> 10 & 31) * 255 // 31,
                          (color >> 5 & 31) * 255 // 31, (color & 31) * 255 // 31, 255))
    return hashlib.sha256(decoded).hexdigest()


def fixture(root):
    source = root / "source"; source.mkdir()
    records = [payload(0), payload(0x8000), payload(77, 128), payload(1234)]
    at = 0; index = bytearray()
    for ident, raw in enumerate(records):
        index += struct.pack("<iii", at, len(raw), -100 - ident)
        at += len(raw)
    (source / "texidx.mul").write_bytes(index)
    (source / "texmaps.mul").write_bytes(b"".join(records))
    return source, records


@pytest.fixture(scope="session")
def oracle():
    path = parity.default_oracle(); parity.doctor(path); return path


def read(oracle, source, ids, definitions="-"):
    return parity.invoke(oracle, [str(source), "texmap", "7.0.107.76", ",".join(map(str, ids)), str(definitions)])["records"]


def test_independent_native_sizes_high_bits_zero_and_literal_colors(tmp_path, oracle):
    source, records = fixture(tmp_path)
    rows = read(oracle, source, range(4))
    for row, raw in zip(rows, records):
        size = 64 if len(raw) == 8192 else 128
        assert (row["width"], row["height"]) == (size, size)
        assert row["physical_id"] == row["id"] and row["affected_ids"] == [row["id"]]
        assert row["raw_sha256"] == hashlib.sha256(raw).hexdigest()
        assert row["rgba_sha256"] == rgba_hash(raw)
    assert rows[0]["rgba_sha256"] == rows[1]["rgba_sha256"]
    assert rows[0]["raw_sha256"] != rows[1]["raw_sha256"]


@pytest.mark.parametrize("text,physical", [
    ("0 { 1 } 0\n1 { 2 } 0\n", [1, 2, 2, 3]),
    ("1 { 2 } 0\n0 { 1 } 0\n", [2, 2, 2, 3]),
    ("0 { 1, 2, 3 } 0 # last valid wins\n", [3, 1, 2, 3]),
    ("# comment\n99 { 1 } 0\n0 { 9999, 2, 9999 } 0\n", [2, 1, 2, 3]),
])
def test_upstream_def_order_groups_and_affected_aliases(tmp_path, oracle, text, physical):
    source, records = fixture(tmp_path)
    definitions = source / "TexTerr.def"; definitions.write_text(text)
    for row in read(oracle, source, range(4), definitions):
        target = physical[row["id"]]
        assert row["physical_id"] == target
        assert row["affected_ids"] == [i for i, value in enumerate(physical) if value == target]
        assert row["raw_sha256"] == hashlib.sha256(records[target]).hexdigest()
        assert row["rgba_sha256"] == rgba_hash(records[target])
    # Explicit absence excludes an unregistered loose DEF in a staged directory.
    assert [row["physical_id"] for row in read(oracle, source, range(4))] == list(range(4))


@pytest.mark.parametrize("damage", ["partial-index", "bad-record-size", "past-end", "oversize-def", "missing-id"])
def test_oracle_refuses_unsafe_inputs_before_decode(tmp_path, oracle, damage):
    source, _ = fixture(tmp_path)
    idx = source / "texidx.mul"; raw = bytearray(idx.read_bytes())
    definitions = "-"
    if damage == "partial-index": raw += b"x"
    elif damage == "bad-record-size": struct.pack_into("<i", raw, 4, 8)
    elif damage == "past-end": struct.pack_into("<i", raw, 0, 0x7fffffff)
    elif damage == "oversize-def":
        definitions = source / "TexTerr.def"; definitions.write_bytes(b" " * (2 * 1024 * 1024 + 1))
    idx.write_bytes(raw)
    with pytest.raises(RuntimeError):
        read(oracle, source, [4 if damage == "missing-id" else 0], definitions)


def prepared(root, oracle, *, identity=False, definitions=False, prior=False):
    source, records = fixture(root)
    if definitions:
        (source / "TexTerr.def").write_text("0 { 1 } 0\n1 { 2 } 0\n")
    original = records[1 if definitions else 0]
    replacement_record = original if identity else payload(0xabcd, 128)
    edits = root / "edits.json"
    edits.write_text(json.dumps({"schema": 1, "domain": "texmap", "records": [{"id": 0,
        "source_record_base64": base64.b64encode(original).decode(),
        "record_base64": base64.b64encode(replacement_record).decode()}]}))
    stage, plan = root / "stage", root / "plan"
    if prior:
        staged = textures.writer.Stage(stage, source)
        staged.path("texidx.mul"); staged.path("texmaps.mul"); staged.save()
        (stage / "notes.txt").write_bytes(b"keep unrelated files")
    before = replacement.stage_hashes(stage)
    spec = textures.prepare(source, stage, edits, plan, "7.0.107.76", oracle)
    return source, stage, edits, plan, spec, replacement_record, before


@pytest.mark.parametrize("identity,definitions,prior", [(False, False, False), (False, True, True), (True, False, True)])
def test_native_texture_prepare_apply_independent_readback_recovery(tmp_path, oracle, identity, definitions, prior):
    source, stage, edits, plan, spec, changed, before = prepared(tmp_path, oracle,
        identity=identity, definitions=definitions, prior=prior)
    source_hashes = textures.hashes(source)
    report = json.loads((plan / "verification/report.json").read_text())
    assert report["outcome"] == "pass" and report["inputs_unchanged"]
    assert spec["physical_id"] == (1 if definitions else 0)
    assert spec["affected_ids"] == [0]
    assert report["appended_bytes"] == (0 if identity else len(changed))
    assert replacement.stage_hashes(stage) == before
    textures.apply(plan / "plan.json", oracle)
    assert textures.read_record(stage, spec["physical_id"]) == changed
    assert read(oracle, stage, [0], source / "TexTerr.def" if definitions else "-")[0]["raw_sha256"] == hashlib.sha256(changed).hexdigest()
    assert textures.hashes(source) == source_hashes
    replacement.recover(stage)
    assert replacement.stage_hashes(stage) == before


@pytest.mark.parametrize("damage", ["prefix", "tail", "bit15", "extra", "untouched-index", "truncate", "append"])
def test_candidate_corruption_refused_without_promotion(tmp_path, oracle, damage):
    source, stage, edits, plan, spec, changed, before = prepared(tmp_path, oracle)
    path = plan / "candidate" / ("texidx.mul" if damage in ("extra", "untouched-index") else "texmaps.mul")
    raw = bytearray(path.read_bytes())
    if damage == "truncate": raw.pop()
    elif damage == "append": raw += b"x"
    else:
        at = {"prefix": 0, "tail": len(raw) - 2, "bit15": len(raw) - 1,
              "extra": 8, "untouched-index": 20}[damage]
        raw[at] ^= 0x80 if damage == "bit15" else 1
    path.write_bytes(raw)
    report = textures.verify(plan / "baseline", plan / "candidate", plan / "expected.json", tmp_path / "bad-proof", oracle)
    assert report["outcome"] == "error"
    with pytest.raises(ValueError): textures.apply(plan / "plan.json", oracle)
    assert replacement.stage_hashes(stage) == before


@pytest.mark.parametrize("change", ["source", "new-def", "edits", "baseline", "manifest", "stage"])
def test_stale_texture_review_refused(tmp_path, oracle, change):
    source, stage, edits, plan, spec, changed, before = prepared(tmp_path, oracle)
    if change == "stage": stage.mkdir(); (stage / "new.txt").write_text("external")
    elif change == "new-def": (source / "TexTerr.def").write_text("0 { 2 } 0\n")
    else:
        path = {"source": source / "texmaps.mul", "edits": edits,
                "baseline": plan / "baseline/texidx.mul", "manifest": plan / "expected.json"}[change]
        path.write_bytes(path.read_bytes() + b" ")
    before = replacement.stage_hashes(stage)
    with pytest.raises(ValueError): textures.apply(plan / "plan.json", oracle)
    assert replacement.stage_hashes(stage) == before


def test_registered_stage_def_and_shared_aliases_are_preserved(tmp_path, oracle):
    source, records = fixture(tmp_path)
    (source / "TexTerr.def").write_text("0 { 1 } 0\n")
    stage = tmp_path / "stage"
    staged = textures.writer.Stage(stage, source)
    staged.path("TexTerr.def").write_text("0 { 2 } 0\n1 { 2 } 0\n")
    staged.save()
    prior = replacement.stage_hashes(stage)
    edits = tmp_path / "edits.json"
    changed = payload(0xface)
    edits.write_text(json.dumps({"schema": 1, "domain": "texmap", "records": [{"id": 0,
        "source_record_base64": base64.b64encode(records[1]).decode(),
        "record_base64": base64.b64encode(changed).decode()}]}))
    plan = tmp_path / "plan"
    spec = textures.prepare(source, stage, edits, plan, "7.0.107.76", oracle)
    assert spec["physical_id"] == 2 and spec["affected_ids"] == [0, 1, 2]
    textures.apply(plan / "plan.json", oracle)
    assert (stage / "TexTerr.def").read_text() == "0 { 2 } 0\n1 { 2 } 0\n"
    for row in read(oracle, stage, [0, 1, 2], stage / "TexTerr.def"):
        assert row["raw_sha256"] == hashlib.sha256(changed).hexdigest()
    replacement.recover(stage)
    assert replacement.stage_hashes(stage) == prior


def test_complete_native_alias_range_fits_verification_manifest(tmp_path, oracle):
    source, records = fixture(tmp_path)
    idx = source / "texidx.mul"
    with idx.open("ab") as stream:
        stream.write(struct.pack("<iii", -1, -1, 0) * (0x4000 - 4))
    (source / "TexTerr.def").write_text("".join(f"{i} {{ 0 }} 0\n" for i in range(1, 0x4000)))
    edits = tmp_path / "edits.json"
    edits.write_text(json.dumps({"schema": 1, "domain": "texmap", "records": [{"id": 0,
        "source_record_base64": base64.b64encode(records[0]).decode(),
        "record_base64": base64.b64encode(payload(0xcafe, 128)).decode()}]}))
    plan = tmp_path / "plan"
    spec = textures.prepare(source, tmp_path / "stage", edits, plan, "7.0.107.76", oracle)
    assert spec["affected_ids"] == list(range(0x4000))
    assert (plan / "expected.json").stat().st_size > 128 * 1024
    assert json.loads((plan / "verification/report.json").read_text())["outcome"] == "pass"
