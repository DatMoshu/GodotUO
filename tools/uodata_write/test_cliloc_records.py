"""Native Cliloc byte preservation and last-record-wins edits."""
import struct
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import cliloc_records as cliloc
from guo.uorecord import AssetRecord
from uodata_write import uodata as writer


def row(ident, flag, text):
    raw = text.encode("utf-8")
    return struct.pack("<iBH", ident, flag, len(raw)) + raw


HEADER = bytes.fromhex("78563412cdab")


def test_duplicate_edit_preserves_header_earlier_duplicate_order_and_unknown_flags():
    first, other, last = row(7, 219, "first"), row(3, 255, "untouched 日本語"), row(7, 143, "last")
    source = HEADER + first + other + last
    output, report = cliloc.patch(source, [{"id": 7, "flag": 143, "text": "changed é", "deleted": False}])
    assert output == HEADER + first + other + row(7, 143, "changed é")
    assert report["preserved_records"] == 2 and report["edited_ids"] == [7]
    assert cliloc.entries(cliloc.records(output))[7].text == "changed é"


def test_delete_removes_all_duplicates_and_append_is_sorted_without_reordering_source():
    source = HEADER + row(9, 251, "keep") + row(7, 2, "older") + row(7, 77, "visible")
    output, report = cliloc.patch(source, [{"id": 7, "deleted": True}, {"id": 81, "flag": 0, "text": ""}, {"id": 20, "flag": 0, "text": "new"}])
    assert output == HEADER + row(9, 251, "keep") + row(20, 0, "new") + row(81, 0, "")
    assert report["removed_records"] == 2 and report["added_ids"] == [20, 81]
    assert 7 not in cliloc.entries(cliloc.records(output))


def test_identity_keeps_every_plain_byte_and_empty_text_is_not_delete():
    source = HEADER + row(5, 250, "") + row(2, 77, "old") + row(2, 88, "effective")
    assert cliloc.patch(source, [])[0] == source
    assert cliloc.patch(source, [{"id": 5, "flag": 250, "text": ""}])[0] == source


def test_untouched_nul_tail_is_retained_but_reader_digest_uses_visible_text():
    source = HEADER + row(1, 255, "visible\0opaque tail")
    assert cliloc.patch(source, [])[0] == source
    assert cliloc.text_digest(cliloc.records(source)) == cliloc.text_digest(cliloc.records(HEADER + row(1, 255, "visible")))


@pytest.mark.parametrize("value", [b"short", HEADER + b"x", HEADER + struct.pack("<iBH", -1, 0, 0),
    HEADER + struct.pack("<iBH", 1, 0, 32768) + bytes(32768), HEADER + struct.pack("<iBH", 1, 0, 2) + b"x",
    HEADER + struct.pack("<iBH", 1, 0, 1) + b"\xff", b"xxx\x8exx"],
    ids=["header", "record-header", "negative-id", "signed-length", "short-text", "utf8", "compressed"])
def test_malformed_native_records_refused(value):
    with pytest.raises((ValueError, UnicodeError)):
        cliloc.records(value)


@pytest.mark.parametrize("changes", [[{"id": True, "flag": 0, "text": "x"}], [{"id": 1, "flag": 9, "text": "x"}],
    [{"id": 2, "flag": 8, "text": "new"}], [{"id": 1, "flag": 7, "text": "nul\0"}],
    [{"id": 1, "flag": 7, "text": "é" * 16384}], [{"id": 1, "deleted": 1}],
    [{"id": 1, "deleted": True, "text": "hidden"}], [{"id": 1, "deleted": True}, {"id": 1, "deleted": True}]],
    ids=["bool-id", "changed-flag", "new-flag", "nul", "text-limit", "deletion-type", "deleted-text", "duplicate-change"])
def test_invalid_patch_refused_atomically(changes):
    source = HEADER + row(1, 7, "old")
    with pytest.raises((ValueError, UnicodeError)):
        cliloc.patch(source, changes)
    assert source == HEADER + row(1, 7, "old")


def test_typed_cliloc_copy_on_write_and_new_source_absence_binding(tmp_path):
    install = tmp_path / "source"; install.mkdir()
    source = HEADER + row(1, 251, "old"); (install / "Cliloc.enu").write_bytes(source)
    stage = writer.Stage(tmp_path / "stage", install)
    output = HEADER + row(1, 251, "changed é")
    record = AssetRecord("cliloc-file", 0, output, {"filename": "cliloc.enu"})
    writer.write_records(stage, [record], replace=True)
    assert writer.read_back(stage, record) == output and (install / "Cliloc.enu").read_bytes() == source
    assert writer.output_files(stage, [record]) == ["cliloc.enu"]
    custom = AssetRecord("cliloc-file", 0, HEADER + row(17, 0, "new"), {"filename": "Cliloc.custom1"})
    writer.write_records(stage, [custom], replace=True)
    assert writer.read_back(stage, custom) == custom.data and not (install / "Cliloc.custom1").exists()
    assert stage.check_install_unchanged() == []
    (install / "Cliloc.custom1").write_bytes(HEADER)
    assert stage.check_install_unchanged() == ["cliloc.custom1"]


@pytest.mark.parametrize("bad,replace", [
    (AssetRecord("cliloc-file", 0, HEADER, {"filename": "../Cliloc.enu"}), True),
    (AssetRecord("cliloc-file", 0, HEADER, {"filename": "art.mul"}), True),
    (AssetRecord("cliloc-file", 1, HEADER, {"filename": "Cliloc.enu"}), True),
    (AssetRecord("cliloc-file", 0, b"bad", {"filename": "Cliloc.enu"}), True),
    (AssetRecord("cliloc-file", 0, HEADER, {"filename": "Cliloc.enu", "extra": 1}), True),
    (AssetRecord("cliloc-file", 0, HEADER, {"filename": "Cliloc.enu"}), False),
], ids=["traversal", "domain", "id", "payload", "meta", "approval"])
def test_invalid_native_cliloc_batch_refused_before_any_copy(tmp_path, bad, replace):
    install = tmp_path / "source"; install.mkdir()
    stage = writer.Stage(tmp_path / "stage", install)
    good = AssetRecord("cliloc-file", 0, HEADER, {"filename": "Cliloc.custom1"})
    with pytest.raises(ValueError): writer.write_records(stage, [good, bad], replace=replace)
    assert list(stage.root.iterdir()) == [] and stage.meta["files"] == {}


def test_duplicate_output_names_case_insensitive_refused_before_copy(tmp_path):
    install = tmp_path / "source"; install.mkdir(); stage = writer.Stage(tmp_path / "stage", install)
    rows = [AssetRecord("cliloc-file", 0, HEADER, {"filename": name}) for name in ("Cliloc.enu", "cliloc.ENU")]
    with pytest.raises(ValueError, match="Duplicate"): writer.write_records(stage, rows, replace=True)
    assert list(stage.root.iterdir()) == []


def test_unregistered_and_hardlinked_cliloc_outputs_refused(tmp_path):
    import os
    install = tmp_path / "source"; install.mkdir(); (install / "Cliloc.enu").write_bytes(HEADER)
    stage = writer.Stage(tmp_path / "stage", install)
    target = stage.root / "Cliloc.enu"; target.write_bytes(b"user file")
    record = AssetRecord("cliloc-file", 0, HEADER, {"filename": "Cliloc.enu"})
    with pytest.raises(ValueError, match="unregistered"): writer.write_records(stage, [record], replace=True)
    assert target.read_bytes() == b"user file"
    target.unlink(); stage.path("Cliloc.enu"); target.unlink(); os.link(install / "Cliloc.enu", target)
    with pytest.raises(ValueError, match="Aliased"): writer.write_records(stage, [record], replace=True)
    assert (install / "Cliloc.enu").read_bytes() == HEADER
