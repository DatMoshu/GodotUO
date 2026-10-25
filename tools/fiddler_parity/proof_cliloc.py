"""Pinned upstream exact-file Cliloc decoding and native byte preservation."""
import base64
import hashlib
import json
import os
from pathlib import Path
import struct
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run as parity
import cliloc_stage as native
import replacement
from guo import cliloc_records as codec


@pytest.fixture(scope="session")
def oracle():
    path = parity.default_oracle(); parity.doctor(path); return path


def row(ident, flag, text):
    raw = text.encode("utf-8")
    return struct.pack("<iBH", ident, flag, len(raw)) + raw


def test_independent_exact_file_reader_duplicate_nul_unicode_and_empty(tmp_path, oracle):
    source = bytes.fromhex("78563412cdab") + row(7, 219, "first") + row(3, 255, "日本語 é") + row(7, 143, "last\0opaque") + row(0x7fffffff, 199, "")
    path = tmp_path / "Cliloc.enu"; path.write_bytes(source)
    # Neither loose overrides nor another language participates in exact-file proof.
    (tmp_path / "Clilocs.txt").write_text("7 loose override")
    result = parity.invoke(oracle, [str(tmp_path), "cliloc-read", "7.0.107.76", path.name])
    assert base64.b64decode(result["plain_base64"]) == source
    assert result["source_sha256"] == hashlib.sha256(source).hexdigest()
    assert result["text_sha256"] == codec.text_digest(codec.records(source))
    assert result["record_count"] == 4 and result["effective_count"] == 3 and not result["compressed"]


def test_native_changes_independently_decode_and_keep_untouched_records(tmp_path, oracle):
    header = bytes.fromhex("78563412cdab")
    source = header + row(10, 255, "keep") + row(7, 219, "first") + row(7, 143, "last") + row(5, 111, "remove")
    output, report = codec.patch(source, [{"id": 7, "flag": 143, "text": "changed 日本語"},
        {"id": 5, "deleted": True}, {"id": 31, "flag": 0, "text": "new é"}])
    assert output == header + row(10, 255, "keep") + row(7, 219, "first") + row(7, 143, "changed 日本語") + row(31, 0, "new é")
    path = tmp_path / "Cliloc.custom"; path.write_bytes(output)
    result = parity.invoke(oracle, [str(tmp_path), "cliloc-read", "7.0.107.76", path.name])
    assert result["text_sha256"] == report["text_sha256"]
    assert result["record_count"] == report["output_records"] and result["effective_count"] == report["effective_records"]


@pytest.mark.parametrize("source", [b"short", bytes(6) + b"x", bytes(6) + struct.pack("<iBH", -1, 0, 0),
    bytes(6) + struct.pack("<iBH", 1, 0, 32768) + bytes(32768), bytes(6) + struct.pack("<iBH", 1, 0, 1) + b"\xff",
    b"xxx\x8exx"], ids=["header", "record-header", "negative-id", "signed-length", "utf8", "compressed-header"])
def test_independent_reader_refuses_malformed_before_loader(tmp_path, oracle, source):
    path = tmp_path / "Cliloc.enu"; path.write_bytes(source)
    with pytest.raises(RuntimeError):
        parity.invoke(oracle, [str(tmp_path), "cliloc-read", "7.0.107.76", path.name])


def fixture(root, custom=False):
    source = root / "install"; source.mkdir()
    content = bytes.fromhex("78563412cdab") + row(9, 255, "keep") + row(7, 219, "earlier") + row(7, 143, "last") + row(5, 77, "remove")
    path = root / "arbitrary-name.dat" if custom else source / "Cliloc.enu"
    path.write_bytes(content)
    changes = [{"id": 7, "flag": 143, "text": "changed 日本語"}, {"id": 5, "deleted": True}, {"id": 31, "flag": 0, "text": "new é"}]
    document = {"schema": 1, "kind": "guo/cliloc-document", "language": "custom1" if custom else "enu", "encoding": "UTF-8",
        "source_sha256": hashlib.sha256(content).hexdigest(), "source_compressed": False,
        "header": base64.b64encode(content[:6]).decode(), "changes": changes}
    if custom:
        text = str(path.resolve()); text = text.upper() if os.name == "nt" else text
        document["source_identity"] = hashlib.sha256(text.encode()).hexdigest()
        document["native_source_file"] = str(path.resolve())
    edits = root / "edits.json"; edits.write_text(json.dumps(document), encoding="utf-8")
    return source, path, root / "stage", "Cliloc.custom1" if custom else "Cliloc.enu", edits, root / "plan", content


@pytest.mark.parametrize("custom", [False, True], ids=["installed", "custom-arbitrary-filename"])
def test_native_cliloc_prepare_apply_exact_bytes_and_recover_absence(tmp_path, oracle, custom):
    source, path, stage, filename, edits, plan, original = fixture(tmp_path, custom)
    spec = native.prepare(source, path, stage, filename, edits, plan, "7.0.107.76", oracle)
    assert not stage.exists() and spec["counts"]["removed_records"] == 1
    native.apply(plan / "plan.json", oracle)
    expected = codec.patch(original, json.loads(edits.read_text())["changes"])[0]
    assert (stage / filename).read_bytes() == expected and path.read_bytes() == original
    replacement.recover(stage); assert not stage.exists()


def test_existing_stage_untouched_edits_and_other_files_survive_then_exact_recovery(tmp_path, oracle):
    source, path, stage, filename, edits, plan, original = fixture(tmp_path)
    native.prepare(source, path, stage, filename, edits, plan, "7.0.107.76", oracle); native.apply(plan / "plan.json", oracle)
    current = (stage / filename).read_bytes()
    (stage / filename).write_bytes(codec.patch(current, [{"id": 9, "flag": 255, "text": "other staged edit"}])[0])
    (stage / "notes.txt").write_bytes(b"unrelated content")
    before = replacement.stage_hashes(stage)
    second = tmp_path / "second-plan"
    native.prepare(source, path, stage, filename, edits, second, "7.0.107.76", oracle); native.apply(second / "plan.json", oracle)
    assert codec.entries(codec.records((stage / filename).read_bytes()))[9].text == "other staged edit"
    assert (stage / "notes.txt").read_bytes() == b"unrelated content"
    replacement.recover(stage); assert replacement.stage_hashes(stage) == before


@pytest.mark.parametrize("damage", ["text", "flag", "header", "untouched", "truncate", "append"], ids=str)
def test_native_cliloc_corruption_refused(tmp_path, oracle, damage):
    source, path, stage, filename, edits, plan, original = fixture(tmp_path)
    native.prepare(source, path, stage, filename, edits, plan, "7.0.107.76", oracle)
    candidate = plan / "candidate" / filename; payload = bytearray(candidate.read_bytes())
    if damage == "truncate": payload.pop()
    elif damage == "append": payload.append(1)
    else: payload[{"text": len(payload) - 1, "flag": 10, "header": 0, "untouched": 13}[damage]] ^= 1
    candidate.write_bytes(payload)
    report = native.verify(plan / "baseline", plan / "candidate", plan / "expected.json", tmp_path / "damaged-proof", oracle)
    assert report["outcome"] == "error"
    with pytest.raises(ValueError): native.apply(plan / "plan.json", oracle)
    assert not stage.exists()


@pytest.mark.parametrize("change", ["source", "edits", "baseline", "manifest", "review", "stage", "new-install-target"])
def test_native_cliloc_stale_review_refused(tmp_path, oracle, change):
    source, path, stage, filename, edits, plan, original = fixture(tmp_path, custom=True)
    native.prepare(source, path, stage, filename, edits, plan, "7.0.107.76", oracle)
    if change == "stage": stage.mkdir(); (stage / "user.txt").write_bytes(b"new")
    elif change == "new-install-target": (source / filename).write_bytes(original)
    else:
        target = {"source": path, "edits": edits, "baseline": plan / "baseline/current.bin", "manifest": plan / "expected.json", "review": plan / "review.json"}[change]
        target.write_bytes(target.read_bytes() + b" ")
    before = replacement.stage_hashes(stage)
    with pytest.raises(ValueError): native.apply(plan / "plan.json", oracle)
    assert replacement.stage_hashes(stage) == before


def test_large_source_text_review_does_not_exceed_transaction_plan_budget(tmp_path, oracle):
    source = tmp_path / "install"; source.mkdir(); path = source / "Cliloc.enu"
    header = bytes.fromhex("78563412cdab")
    original = header + b"".join(row(ident, 251, "x" * 30000) for ident in range(600)); path.write_bytes(original)
    document = {"schema": 1, "kind": "guo/cliloc-document", "language": "enu", "encoding": "UTF-8",
        "source_sha256": hashlib.sha256(original).hexdigest(), "source_compressed": False, "header": base64.b64encode(header).decode(),
        "changes": [{"id": ident, "deleted": True} for ident in range(600)]}
    edits = tmp_path / "edits.json"; edits.write_text(json.dumps(document))
    stage, plan = tmp_path / "stage", tmp_path / "plan"
    result = native.prepare(source, path, stage, path.name, edits, plan, "7.0.107.76", oracle)
    assert len(result["records"]) == 600 and (plan / "review.json").stat().st_size > 16 * 1024 * 1024
    assert (plan / "plan.json").stat().st_size < 16 * 1024 * 1024
    native.apply(plan / "plan.json", oracle)
    assert (stage / path.name).read_bytes() == header and path.read_bytes() == original


def test_custom_source_path_identity_refused_before_plan(tmp_path, oracle):
    source, path, stage, filename, edits, plan, original = fixture(tmp_path, custom=True)
    another = tmp_path / "same-bytes-other-path.dat"; another.write_bytes(original)
    with pytest.raises(ValueError, match="source path"):
        native.prepare(source, another, stage, filename, edits, plan, "7.0.107.76", oracle)
    assert not stage.exists() and not plan.exists()


def test_custom_unicode_path_uses_exact_capture_without_unicode_case_hash_reimplementation(tmp_path, oracle):
    source, path, stage, filename, edits, plan, original = fixture(tmp_path, custom=True)
    unicode_path = tmp_path / "custom-ß-日本.dat"; path.rename(unicode_path)
    document = json.loads(edits.read_text()); document["native_source_file"] = str(unicode_path.resolve())
    # The editor's path identity is opaque here; the exact captured source path
    # and file hash bind this transaction without duplicating .NET's case rules.
    document["source_identity"] = "a" * 64; edits.write_text(json.dumps(document))
    native.prepare(source, unicode_path, stage, filename, edits, plan, "7.0.107.76", oracle)
    native.apply(plan / "plan.json", oracle)
    assert unicode_path.read_bytes() == original and (stage / filename).is_file()


def test_custom_source_cannot_silently_replace_another_installed_language(tmp_path, oracle):
    source, path, stage, filename, edits, plan, original = fixture(tmp_path, custom=True)
    installed = source / "Cliloc.enu"; installed.write_bytes(bytes(6) + row(99, 0, "different language"))
    with pytest.raises(ValueError, match="different installed Cliloc"):
        native.prepare(source, path, stage, installed.name, edits, plan, "7.0.107.76", oracle)
    assert not stage.exists() and installed.read_bytes() == bytes(6) + row(99, 0, "different language")


@pytest.mark.parametrize("damage", ["source-binding", "selected-flag", "unregistered-collision"])
def test_conflicting_prior_stage_refused_without_mutation(tmp_path, oracle, damage):
    source, path, stage, filename, edits, plan, original = fixture(tmp_path)
    if damage == "unregistered-collision":
        staged = native.writer.Stage(stage, source); staged.save(); (stage / filename).write_bytes(b"unregistered user content")
    else:
        native.prepare(source, path, stage, filename, edits, plan, "7.0.107.76", oracle); native.apply(plan / "plan.json", oracle)
        if damage == "source-binding":
            meta = json.loads((stage / "stage.json").read_text()); meta["cliloc_sources"][filename.lower()]["source_sha256"] = "0" * 64
            (stage / "stage.json").write_text(json.dumps(meta))
        else:
            payload = bytearray((stage / filename).read_bytes()); at = 6
            while at < len(payload):
                ident, flag, count = struct.unpack_from("<iBH", payload, at)
                if ident == 7 and flag == 143: payload[at + 4] = 17; break
                at += 7 + count
            (stage / filename).write_bytes(payload)
    before = replacement.stage_hashes(stage)
    with pytest.raises(ValueError):
        native.prepare(source, path, stage, filename, edits, tmp_path / "refused-plan", "7.0.107.76", oracle)
    assert replacement.stage_hashes(stage) == before


@pytest.mark.parametrize("destination", ["install", "traversal", "source-file-parent"])
def test_overlapping_output_paths_refused(tmp_path, oracle, destination):
    source, path, stage, filename, edits, plan, original = fixture(tmp_path)
    stage = {"install": source / "stage", "traversal": tmp_path / "x" / ".." / "stage", "source-file-parent": source}[destination]
    with pytest.raises(ValueError): native.prepare(source, path, stage, filename, edits, plan, "7.0.107.76", oracle)
    assert path.read_bytes() == original and not plan.exists()
