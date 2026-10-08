"""The encrypted art container (AX6): round trip, tamper, wrong key, header change, no plain files, no key leaks.

    python -m pytest tools/art_extract -q

Synthetic install only (rule 8). The keys here are made up for the test and belong to no shard.
"""

from __future__ import annotations

import json
import os
import random
import stat
import struct
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
sys.path.append(str(HERE.parent / "uopack"))

import art_container as ac  # noqa: E402
import art_export as ex  # noqa: E402
import run as cli  # noqa: E402
from test_art_extract import GEN, fake_install, tree_hash  # noqa: E402

KEY = bytes(range(32))
WHAT = ("gump", "light", "land")
QUIET = dict(client_version="7.0.1", generated=GEN, log=lambda *_: None)


@pytest.fixture
def made(tmp_path):
    data = tmp_path / "data"
    fake_install(data, random.Random(11))
    plain = tmp_path / "plain"
    ex.export(data, plain, WHAT, **QUIET)
    path = tmp_path / "out" / "demo.guoart"
    ex.export(data, path, WHAT, sink=ex.ContainerSink(path, "demo", KEY), **QUIET)
    return data, plain, path


def test_round_trip_holds_exactly_the_plain_sets_files(made):
    _, plain, path = made
    box = ac.Container(path, KEY)
    expect = {name: (plain / name).read_bytes() for name in tree_hash(plain)}
    assert box.names() == sorted(expect)
    for name, data in expect.items():
        assert box.read(name) == data, name
    assert box.info["shard_id"] == "demo"
    assert box.info["set_id"] == json.loads(expect["set.json"])["fingerprint"]["set_id"]


def test_only_the_container_reaches_the_disk(made):
    _, _, path = made
    assert sorted(p.name for p in path.parent.iterdir()) == ["demo.guoart"]
    assert b"\x89PNG" not in path.read_bytes()  # sealed, not stored


def test_a_tampered_byte_is_refused(made):
    _, _, path = made
    raw = path.read_bytes()
    box = ac.Container(path, KEY)
    victim = next(n for n in box.names() if n.endswith(".png"))
    other = next(n for n in box.names() if n.endswith("index.json"))
    at, length = box.entries[victim]
    body = at + 2 + len(victim) + 12 + 4
    for pos in (body + 5, body + length - 1, at + 2 + len(victim) + 3):  # ciphertext, tag, nonce
        bad = bytearray(raw)
        bad[pos] ^= 1
        path.write_bytes(bytes(bad))
        broken = ac.Container(path, KEY)
        with pytest.raises(ac.ContainerError, match="integrity"):
            broken.read(victim)
        assert broken.read(other)  # the damage stays in its own chunk


def test_a_damaged_table_of_contents_or_footer_is_refused(made):
    _, _, path = made
    raw = path.read_bytes()
    path.write_bytes(raw[:-20])
    with pytest.raises(ac.ContainerError):
        ac.Container(path, KEY)
    (toc_at,) = struct.unpack_from("<Q", raw, len(raw) - 16)
    bad = bytearray(raw)
    bad[toc_at + 40] ^= 1
    path.write_bytes(bytes(bad))
    with pytest.raises(ac.ContainerError):
        ac.Container(path, KEY)


def test_a_wrong_key_names_the_shard_and_never_the_key(made):
    _, _, path = made
    wrong = bytes(reversed(KEY))
    with pytest.raises(ac.ContainerError) as err:
        ac.Container(path, wrong)
    assert "demo" in str(err.value)
    for form in (KEY.hex(), wrong.hex(), repr(KEY), repr(wrong)):
        assert form not in str(err.value)


def test_a_header_only_change_is_refused(made):
    _, _, path = made
    raw = bytearray(path.read_bytes())
    (hlen,) = struct.unpack_from("<I", raw, len(ac.MAGIC))
    start = len(ac.MAGIC) + 4
    header = bytes(raw[start:start + hlen])
    assert b'"shard_id":"demo"' in header
    raw[start:start + hlen] = header.replace(b'"shard_id":"demo"', b'"shard_id":"demx"')  # same length, same key id
    path.write_bytes(bytes(raw))
    with pytest.raises(ac.ContainerError, match="integrity"):
        ac.Container(path, KEY)


def test_a_chunk_cannot_be_passed_off_under_another_name(made):
    _, _, path = made
    box = ac.Container(path, KEY)
    a, b = [n for n in box.names() if n.endswith("index.json")][:2]
    with pytest.raises(ac.ContainerError):
        box._open(box.entries[a][0], b)  # the name sealed inside the chunk must be the one asked for
    # same-length rename inside the file: the name is part of the associated data, so the tag fails
    raw = bytearray(path.read_bytes())
    at = box.entries[a][0]
    twin = a.replace("gump", "light") if "gump" in a else a.replace("light", "gump")
    if len(twin) == len(a) and twin in box.entries:
        raw[at + 2:at + 2 + len(a)] = twin.encode()
        path.write_bytes(bytes(raw))
        with pytest.raises(ac.ContainerError):
            ac.Container(path, KEY)._open(at, twin)


def test_nothing_secret_is_in_the_file_or_the_output(made, tmp_path, monkeypatch, capsys):
    data, _, path = made
    blob = path.read_bytes()
    assert KEY not in blob and KEY.hex().encode() not in blob
    keyfile = tmp_path / "shard.key"
    assert cli.main(["keygen", "--shard", "demo", "--key-file", str(keyfile)]) == 0
    key = ac.read_key_file(keyfile)
    root = tmp_path / "repo"
    (root / "build").mkdir(parents=True)
    art = tmp_path / "art"
    monkeypatch.setattr(ex, "settings", lambda *_: {"root": root, "client_data": data, "client_version": "7.0.1",
                                                    "art_dir": art})
    assert cli.main(["pack-container", "--shard", "demo", "--key-file", str(keyfile), "--what", "gumps"]) == 0
    out = art / "containers" / "demo.guoart"
    assert cli.main(["info", str(out)]) == 0
    assert cli.main(["pack-container", "--shard", "Bad Id", "--key-file", str(keyfile)]) == 2
    assert cli.main(["pack-container", "--shard", "demo", "--key-file", str(tmp_path / "missing.key")]) == 2
    shown = capsys.readouterr()
    everything = (shown.out + shown.err).encode() + out.read_bytes()
    assert key not in everything and key.hex().encode() not in everything
    assert ac.key_id_of(key) in shown.out  # the one-way tag is shown, the key is not
    assert not (art / "gump").exists()  # no plain set beside it
    if os.name != "nt":
        assert stat.S_IMODE(keyfile.stat().st_mode) == 0o600


def test_keygen_will_not_overwrite_a_key(tmp_path):
    keyfile = tmp_path / "k.key"
    assert cli.main(["keygen", "--shard", "demo", "--key-file", str(keyfile)]) == 0
    first = keyfile.read_text()
    assert cli.main(["keygen", "--shard", "demo", "--key-file", str(keyfile)]) == 2
    assert keyfile.read_text() == first


def test_key_files_and_shard_ids_are_strict(tmp_path):
    f = tmp_path / "k"
    f.write_text("abc")
    with pytest.raises(ac.ContainerError, match="64 hex"):
        ac.read_key_file(f)
    with pytest.raises(ac.ContainerError):
        ac.read_key_file(tmp_path / "none")
    for bad in ("", "Up", "../x", "a b", "x" * 49):
        with pytest.raises(ac.ContainerError):
            ac.check_shard_id(bad)


def test_a_failed_export_leaves_no_container(tmp_path, monkeypatch):
    data = tmp_path / "data"
    fake_install(data, random.Random(3))
    path = tmp_path / "x.guoart"

    def boom(*a, **k):
        raise ex.ArtExtractError("boom")

    monkeypatch.setattr(ex, "_export_into", boom)
    with pytest.raises(ex.ArtExtractError):
        ex.export(data, path, ("gump",), sink=ex.ContainerSink(path, "demo", KEY), **QUIET)
    assert not path.exists() and not path.with_name("x.guoart.part").exists()


def test_the_documents_inside_follow_the_schemas(made):
    _, _, path = made
    box = ac.Container(path, KEY)
    assert not ex._schema_errors(json.loads(box.read("set.json")), "set.schema.json")
    assert not ex._schema_errors(json.loads(box.read("gump/index.json")), "index.schema.json")


def test_the_header_follows_its_schema(made):
    _, _, path = made
    box = ac.Container(path, KEY)
    assert not ex._schema_errors(box.info, "container.schema.json")
    assert ex._schema_errors({**box.info, "extra": 1}, "container.schema.json")
