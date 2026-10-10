"""The AX7 mark: invisible to the hue path, read back from a page or a crop, signed, never in the player's own set.

    python -m pytest tools/art_extract -q

Synthetic art only (rule 8). The keys here are made up for the test and belong to no shard.
"""

from __future__ import annotations

import io
import json
import random
import struct
import sys
import zlib
from pathlib import Path

import numpy as np
import pytest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
sys.path.append(str(HERE.parent / "uopack"))

import art_container as ac  # noqa: E402
import art_export as ex  # noqa: E402
import art_mark as m  # noqa: E402
import art_png as png  # noqa: E402
import mark_measure as mm  # noqa: E402
import run as cli  # noqa: E402
from asset_store import catalogue, ed25519  # noqa: E402
from test_art_extract import GEN, fake_install  # noqa: E402

SECRET = bytes(range(9, 41))
PUBLIC = ed25519.public_key(SECRET)
OTHER = ed25519.public_key(bytes(range(50, 82)))
KEY = bytes(range(32))
QUIET = dict(client_version="7.0.1", generated=GEN, log=lambda *_: None)


@pytest.fixture(scope="module")
def page():
    return mm.sprite_page(256, seed=2)


@pytest.fixture(scope="module")
def payload():
    return m.build_payload("demo-shard", 4242, SECRET)


@pytest.fixture(scope="module")
def marked(page, payload):
    return m.mark_rgba(page, payload)


# --- payload -----------------------------------------------------------------------------------

def test_payload_round_trip_and_signature(payload):
    got = m.parse_payload(payload)
    assert (got["shard_id"], got["serial"]) == ("demo-shard", 4242)
    assert m.check_signature(got, PUBLIC) == "valid"
    assert m.check_signature(got, OTHER) == "invalid"
    assert m.check_signature(got, None) == "unchecked"


def test_a_bad_crc_or_version_is_not_a_mark(payload):
    flipped = bytearray(payload)
    flipped[10] ^= 1
    assert m.parse_payload(bytes(flipped)) is None
    body = bytearray(payload[:m.PAYLOAD_BYTES])
    body[0] = 9
    assert m.parse_payload(bytes(body) + struct.pack(">I", zlib.crc32(bytes(body)) & 0xFFFFFFFF)) is None


def test_a_changed_serial_fails_the_signature(payload):
    got = m.parse_payload(payload)
    forged = dict(got, serial=got["serial"] + 1)
    assert m.check_signature(forged, PUBLIC) == "invalid"


# --- invisible to the hue path -----------------------------------------------------------------

def test_the_five_bit_values_alpha_and_bounds_are_untouched(page, marked):
    assert marked.dtype == np.uint8 and marked.shape == page.shape
    assert (marked[..., 3] == page[..., 3]).all()
    assert (marked[..., :3] >> 3 == page[..., :3] >> 3).all()           # what the hue lookup reads
    assert int(np.abs(marked.astype(int) - page).max()) <= 7
    assert (marked != page).any()
    clear = page[..., 3] == 0
    assert (marked[clear] == page[clear]).all()


def test_the_shader_decisions_that_read_exact_values_do_not_change(page, marked):
    """uo_hue_core.gdshaderinc: PARTIAL_HUED needs r == g == b, the gump and text tests are r < 0.02 and any > 0.04,
    and the hue table is read at floor(gray * 32) clamped to its last texel."""
    a, b = page[..., :3].astype(int), marked[..., :3].astype(int)
    assert ((a[..., 0] == a[..., 1]) & (a[..., 0] == a[..., 2]) == ((b[..., 0] == b[..., 1]) & (b[..., 0] == b[..., 2]))).all()
    assert ((a[..., 0] / 255 < 0.02) == (b[..., 0] / 255 < 0.02)).all()
    assert (((a / 255) > 0.04).any(axis=-1) == ((b / 255) > 0.04).any(axis=-1)).all()
    texel = lambda v: np.minimum(np.floor(v / 255 * 32), 31)           # noqa: E731
    assert (texel(a) == texel(b)).all()


def test_every_value_a_carrier_can_take_keeps_its_hue_texel():
    for k in range(m.K_MIN, m.K_MAX + 1):
        for low in range(8):
            v = k * 8 + low
            assert min(int(v / 255 * 32), 31) == k
            assert v / 255 >= 0.04 or k < 2


def test_grey_pixels_stay_grey_and_dark_or_bright_pixels_are_left_alone():
    rgba = np.zeros((32, 32, 4), np.uint8)
    rgba[..., 3] = 255
    rgba[..., :3] = mm.EXPAND[12]                                       # grey, eligible
    rgba[:4, :, :3] = mm.EXPAND[0]                                      # black: never marked
    rgba[4:8, :, :3] = mm.EXPAND[1]                                     # the 0.04 threshold: never marked
    rgba[8:12, :, :3] = mm.EXPAND[31]                                   # the top texel: never marked
    out = m.mark_rgba(rgba, m.build_payload("demo", 1, SECRET))
    assert (out[:12] == rgba[:12]).all()
    body = out[12:, :, :3].astype(int)
    assert (body[..., 0] == body[..., 1]).all() and (body[..., 0] == body[..., 2]).all()
    assert (out[12:] != rgba[12:]).any()


def test_art_that_did_not_come_from_1555_is_refused_unless_forced(tmp_path):
    rgba = np.zeros((16, 16, 4), np.uint8)
    rgba[..., 3] = 255
    rgba[..., :3] = 100                                                 # equal 5-bit channels...
    rgba[0, 0, 1] = 103                                                 # ...with unequal 8-bit ones
    assert m.unsafe_pixels(rgba) == 1
    src = tmp_path / "a.png"
    src.write_bytes(png.encode_rgba(16, 16, rgba.tobytes()))
    keyfile = tmp_path / "sign.key"
    catalogue.keygen(keyfile)
    argv = ["mark", "--shard", "demo", "--sign-key", str(keyfile), "--serial", "1", "--in", str(src),
            "--out", str(tmp_path / "out.png")]
    # the tool only writes under UO_ART_EXTRACT_DIR or build/; point both at the temp folder
    cfg = {"art_dir": tmp_path, "root": tmp_path, "client_data": "", "client_version": "7.0.1"}
    import unittest.mock as mock
    with mock.patch.object(ex, "settings", return_value=cfg):
        assert cli.main(argv) == 2
        assert not (tmp_path / "out.png").exists()
        assert cli.main(argv + ["--force"]) == 0
        assert (tmp_path / "out.png").exists()


# --- reading it back ---------------------------------------------------------------------------

def test_the_whole_picture_reads_and_an_unmarked_one_does_not(page, marked):
    found = m.read_rgba(marked)
    assert (found["shard_id"], found["serial"]) == ("demo-shard", 4242)
    assert m.check_signature(found, PUBLIC) == "valid"
    assert m.read_rgba(page) is None


def test_a_crop_at_any_offset_reads(marked):
    for y, x, n in ((0, 0, 128), (37, 51, 96), (101, 13, 110), (130, 120, 126)):
        found = m.read_rgba(np.ascontiguousarray(marked[y:y + n, x:x + n]))
        assert found and found["serial"] == 4242, (y, x, n)
        assert found["phase"] == (x % m.TILE, y % m.TILE)


def test_a_small_fully_opaque_crop_reads():
    rng = np.random.default_rng(4)
    rgba = np.zeros((96, 96, 4), np.uint8)
    rgba[..., 3] = 255
    rgba[..., :3] = mm.EXPAND[rng.integers(3, 29, (96, 96, 3))]
    out = m.mark_rgba(rgba, m.build_payload("demo", 5, SECRET))
    found = m.read_rgba(np.ascontiguousarray(out[11:11 + 24, 29:29 + 24]))
    assert found and found["serial"] == 5


def test_a_png_resaved_with_other_filters_still_reads(marked):
    """Another tool re-saves the page: every row filter, extra chunks. The mark is in the pixels, so it survives."""
    h, w = marked.shape[:2]
    raw, stride = bytearray(), w * 4
    flat = marked.reshape(h, stride)
    for y in range(h):
        row, prev = flat[y].astype(int), (flat[y - 1].astype(int) if y else np.zeros(stride, int))
        kind = y % 5
        if kind == 0:
            body = row
        elif kind == 1:
            body = row - np.concatenate([np.zeros(4, int), row[:-4]])
        elif kind == 2:
            body = row - prev
        elif kind == 3:
            left = np.concatenate([np.zeros(4, int), row[:-4]])
            body = row - ((left + prev) >> 1)
        else:
            left = np.concatenate([np.zeros(4, int), row[:-4]])
            ul = np.concatenate([np.zeros(4, int), prev[:-4]])
            p = left + prev - ul
            pa, pb, pc = abs(p - left), abs(p - prev), abs(p - ul)
            pred = np.where((pa <= pb) & (pa <= pc), left, np.where(pb <= pc, prev, ul))
            body = row - pred
        raw += bytes([kind]) + (body & 255).astype(np.uint8).tobytes()

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    data = (png.SIGNATURE + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"tEXt", b"Software\x00some editor") + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))
    with pytest.raises(ValueError):
        png.decode_rgba(data)                                           # the strict page reader refuses it
    rw, rh, rgba = png.decode_any(data)
    assert (rw, rh) == (w, h) and rgba == marked.tobytes()
    assert m.read_rgba(np.frombuffer(rgba, np.uint8).reshape(h, w, 4).copy())["serial"] == 4242


def test_known_non_survivors_are_really_not_read(marked):
    assert m.read_rgba(mm.requantise(marked)) is None                   # the 5-bit value is all that is left
    lit = marked.copy()
    lit[..., :3] = (lit[..., :3].astype(int) * 0.85).astype(np.uint8)
    assert m.read_rgba(lit) is None
    big = np.repeat(np.repeat(marked, 2, axis=0), 2, axis=1)
    assert m.read_rgba(big) is None


# --- the command line --------------------------------------------------------------------------

@pytest.fixture
def work(tmp_path, monkeypatch):
    cfg = {"art_dir": tmp_path / "set", "root": tmp_path, "client_data": str(tmp_path / "data"), "client_version": "7.0.1"}
    monkeypatch.setattr(ex, "settings", lambda root=None: cfg)
    keyfile = tmp_path / "sign.key"
    catalogue.keygen(keyfile)
    return tmp_path, keyfile, ed25519.encode(ed25519.public_key(catalogue.read_secret(keyfile)))


def test_mark_a_folder_then_prove_a_file_and_a_crop(work, marked, capsys):
    tmp, keyfile, public = work
    src = tmp / "art"
    (src / "sub").mkdir(parents=True)
    h, w = marked.shape[:2]
    clean = mm.sprite_page(192, seed=8)
    (src / "sub" / "one.png").write_bytes(png.encode_rgba(192, 192, clean.tobytes()))
    out = tmp / "set" / "marked"
    assert cli.main(["mark", "--shard", "demo-shard", "--sign-key", str(keyfile), "--serial", "17", "--in", str(src),
                     "--out", str(out)]) == 0
    done = out / "sub" / "one.png"
    capsys.readouterr()
    assert cli.main(["prove", str(done), "--sign-key-pub", public]) == 0
    text = capsys.readouterr().out
    assert "shard demo-shard" in text and "serial 17" in text and "signature valid" in text
    rgba = np.frombuffer(png.decode_any(done.read_bytes())[2], np.uint8).reshape(192, 192, 4)
    crop = tmp / "set" / "crop.png"
    crop.write_bytes(png.encode_rgba(120, 120, np.ascontiguousarray(rgba[40:160, 30:150]).tobytes()))
    assert cli.main(["prove", str(crop), "--sign-key-pub", public, "--json"]) == 0
    assert json.loads(capsys.readouterr().out) == {"found": True, "shard_id": "demo-shard", "serial": 17, "signature": "valid"}


def test_prove_reports_a_wrong_key_an_unmarked_picture_and_a_bad_file(work, capsys):
    tmp, keyfile, _ = work
    clean = tmp / "clean.png"
    clean.write_bytes(png.encode_rgba(128, 128, mm.sprite_page(128, seed=9).tobytes()))
    out = tmp / "set" / "m.png"
    assert cli.main(["mark", "--shard", "demo", "--sign-key", str(keyfile), "--serial", "1", "--in", str(clean), "--out", str(out)]) == 0
    capsys.readouterr()
    assert cli.main(["prove", str(out), "--sign-key-pub", ed25519.encode(OTHER)]) == 1
    assert "signature invalid" in capsys.readouterr().out
    assert cli.main(["prove", str(clean)]) == 1
    assert "no mark found" in capsys.readouterr().out
    (tmp / "bad.png").write_bytes(b"not a png")
    assert cli.main(["prove", str(tmp / "bad.png")]) == 2


def test_mark_only_writes_where_the_set_or_build_lives(work):
    tmp, keyfile, _ = work
    src = tmp / "a.png"
    src.write_bytes(png.encode_rgba(32, 32, mm.sprite_page(32, seed=1).tobytes()))
    away = tmp.parent / "elsewhere.png"
    assert cli.main(["mark", "--shard", "demo", "--sign-key", str(keyfile), "--serial", "1", "--in", str(src), "--out", str(away)]) == 2
    assert not away.exists()


def test_the_signing_key_is_in_no_output(work, capsys):
    tmp, keyfile, _ = work
    secret = catalogue.read_secret(keyfile)
    src = tmp / "a.png"
    src.write_bytes(png.encode_rgba(128, 128, mm.sprite_page(128, seed=3).tobytes()))
    out = tmp / "set" / "a.png"
    cli.main(["mark", "--shard", "demo", "--sign-key", str(keyfile), "--serial", "1", "--in", str(src), "--out", str(out)])
    cli.main(["prove", str(out), "--sign-key-pub", ed25519.encode(ed25519.public_key(secret))])
    spoken = capsys.readouterr().out + capsys.readouterr().err
    seen = out.read_bytes()
    for form in (secret, secret.hex().encode(), ed25519.encode(secret).encode(), ed25519.encode(secret)[8:].encode()):
        assert form not in seen
        assert form.decode("latin-1") not in spoken
    cli.main(["mark", "--shard", "demo", "--sign-key", str(tmp / "nope.key"), "--serial", "1", "--in", str(src), "--out", str(out)])
    assert str(secret.hex()) not in capsys.readouterr().err


# --- through the exporter ----------------------------------------------------------------------

def test_a_container_can_carry_the_mark_and_a_plain_set_never_does(tmp_path):
    data = tmp_path / "data"
    fake_install(data, random.Random(11))
    what = ("land", "gump")
    plain = tmp_path / "plain"
    ex.export(data, plain, what, **QUIET)
    path = tmp_path / "out" / "demo.guoart"
    marker = m.Marker(m.build_payload("demo", 77, SECRET))
    ex.export(data, path, what, sink=ex.ContainerSink(path, "demo", KEY), marker=marker, **QUIET)
    assert marker.count >= 2
    box = ac.Container(path, KEY)
    pages = [n for n in box.names() if n.endswith(".png")]
    assert pages
    for name in pages:
        w, h, rgba = png.decode_rgba(box.read(name))                    # still the strict page shape
        clean = png.decode_rgba((plain / name).read_bytes())[2]
        a = np.frombuffer(rgba, np.uint8).reshape(h, w, 4)
        b = np.frombuffer(clean, np.uint8).reshape(h, w, 4)
        assert (a[..., 3] == b[..., 3]).all() and (a[..., :3] >> 3 == b[..., :3] >> 3).all()
        assert int(np.abs(a.astype(int) - b).max()) <= 7
        assert m.read_rgba(b) is None                                   # the player's own set carries nothing
    land = png.decode_rgba(box.read("land/page_0000.png"))[2]
    found = m.read_rgba(np.frombuffer(land, np.uint8).reshape(2048, 2048, 4).copy())
    assert found and (found["shard_id"], found["serial"]) == ("demo", 77)
    # the pages the index names still hash as the index says (it is made from the marked bytes)
    index = json.loads(box.read("land/index.json"))
    import hashlib
    for p in index["pages"]:
        assert hashlib.sha256(box.read("land/" + p["file"])).hexdigest() == p["sha256"]


def test_pack_container_with_a_mark_key_writes_it_and_needs_a_serial(tmp_path, monkeypatch, capsys):
    data = tmp_path / "data"
    fake_install(data, random.Random(11))
    cfg = {"art_dir": tmp_path / "set", "root": tmp_path, "client_data": str(data), "client_version": "7.0.1"}
    monkeypatch.setattr(ex, "settings", lambda root=None: cfg)
    keyfile, signfile = tmp_path / "shard.key", tmp_path / "sign.key"
    assert cli.main(["keygen", "--shard", "demo", "--key-file", str(keyfile)]) == 0
    catalogue.keygen(signfile)
    base = ["pack-container", "--shard", "demo", "--key-file", str(keyfile), "--what", "land", "--from", str(data),
            "--out", str(tmp_path / "set" / "demo.guoart")]
    assert cli.main(base + ["--mark-key", str(signfile)]) == 2          # no serial
    assert "--serial" in capsys.readouterr().err
    assert cli.main(base + ["--serial", "3"]) == 2                      # a serial with no key
    assert cli.main(base + ["--mark-key", str(signfile), "--serial", "3"]) == 0
    box = ac.Container(tmp_path / "set" / "demo.guoart", ac.read_key_file(keyfile))
    land = png.decode_rgba(box.read("land/page_0000.png"))[2]
    found = m.read_rgba(np.frombuffer(land, np.uint8).reshape(2048, 2048, 4).copy())
    assert found and found["serial"] == 3
    spoken = capsys.readouterr().out
    assert ed25519.encode(catalogue.read_secret(signfile)) not in spoken
