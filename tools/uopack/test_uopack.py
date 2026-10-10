"""tools/uopack tests on synthetic data only (CI has no client data, rule 8).

    python -m pytest tools/uopack             (also part of the pooled run, launchers/dev/pytest_all)
    python tools/uopack/test_uopack.py        (or: python tools/uopack/run.py selftest; the same tests, exit 0/1)

A fake install is written to a temp folder: art.mul/artidx.mul (land + statics),
gumpart.mul/gumpidx.mul, anim.mul/anim.idx, a LegacyMUL-style UOP archive and a
new-format tiledata.mul. Then: codec round trips and edge cases; unpack -> pack
-> byte-identical records; an edited PNG re-encodes and decodes to the edit; an
RGBA animation (new art) packs through a built palette; the UOP reader finds
entries by name hash.
"""

from __future__ import annotations

import json
import random
import shutil
import struct
import subprocess
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))

from guo import uoread  # noqa: E402
import uocodecs as C  # noqa: E402

RUN = [sys.executable, str(HERE / "run.py")]
SEED = 20260927


@pytest.fixture
def rng() -> random.Random:
    return random.Random(SEED)


# --- synthetic content -------------------------------------------------------------

def rnd_static(rng: random.Random, w: int, h: int) -> list[int]:
    px = []
    for y in range(h):
        for x in range(w):
            inside = (x - w / 2) ** 2 / (w / 2) ** 2 + (y - h / 2) ** 2 / (h / 2) ** 2 < 1
            px.append(rng.choice([C.uoart.NEAR_BLACK, rng.randrange(1, 0x8000)]) if inside else 0)
    return px


def rnd_land(rng: random.Random) -> list[int]:
    return [rng.randrange(0, 0x8000) if C.uoart.in_diamond(i % 44, i // 44) else 0 for i in range(44 * 44)]


def rnd_anim(rng: random.Random, frames: int) -> C.AnimGroup:
    palette = [rng.randrange(0, 0x8000) for _ in range(256)]
    out = []
    for _ in range(frames):
        w, h = rng.randrange(1, 40), rng.randrange(1, 60)
        index = [rng.randrange(0, 200) if rng.random() < 0.7 else -1 for _ in range(w * h)]
        out.append(C.AnimFrame(rng.randrange(-10, 10), rng.randrange(-20, 5), w, h, index))
    return C.AnimGroup(palette, out)


def write_mul(folder: Path, mul: str, idx: str, entries: dict[int, tuple[bytes, int]], count: int) -> None:
    data = bytearray()
    records = [(-1, -1, 0)] * count
    for i, (payload, extra) in sorted(entries.items()):
        records[i] = (len(data), len(payload), extra)
        data += payload
    (folder / mul).write_bytes(bytes(data))
    (folder / idx).write_bytes(b"".join(struct.pack("<iii", *r) for r in records))


def write_uop(path: Path, entries: dict[str, bytes]) -> None:
    """A minimal UOP archive: header, one table block, stored (flag 0) entries."""
    names = list(entries)
    header = 28
    table_at = 512
    table_size = 12 + 34 * len(names)
    at = table_at + table_size
    rows, blob = [], bytearray()
    for n in names:
        rows.append(struct.pack("<qiiiQIh", at + len(blob), 0, len(entries[n]), len(entries[n]),
                                uoread.uop_hash(n), 0, 0))
        blob += entries[n]
    out = bytearray(struct.pack("<IIIqIi", uoread.UOP_MAGIC, 5, 0xFD23EC43, table_at, len(names), len(names)))
    out += bytes(table_at - len(out))
    out += struct.pack("<iq", len(names), 0) + b"".join(rows) + blob
    del header
    path.write_bytes(bytes(out))


def write_tiledata(path: Path, statics: dict[int, dict]) -> None:
    land = bytes(512 * (4 + 32 * 30))
    groups = 8
    out = bytearray()
    for g in range(groups):
        out += struct.pack("<I", 0)
        for j in range(32):
            t = statics.get(g * 32 + j, {})
            name = t.get("name", "").encode("latin-1")[:20].ljust(20, b"\0")
            out += struct.pack("<QBBiHHHB", t.get("flags", 0), t.get("weight", 0), t.get("layer", 0),
                               t.get("count", 0), t.get("anim", 0), t.get("hue", 0), t.get("light", 0),
                               t.get("height", 0)) + name
    path.write_bytes(land + bytes(out))


def fake_install(root: Path, rng: random.Random) -> dict:
    root.mkdir(parents=True, exist_ok=True)
    statics = {5: (20, 30), 7: (44, 48), 200: (1, 1), 201: (60, 9)}
    art = {}
    truth = {"static": {}, "land": {}, "gump": {}, "anim": {}}
    for lid in (0, 3, 100):
        px = rnd_land(rng)
        art[lid] = (C.encode_land(px), 0)
        truth["land"][lid] = px
    for sid, (w, h) in statics.items():
        px = rnd_static(rng, w, h)
        raw = C.encode_static(px, w, h, header=0x5F8)
        art[C.uoart.LAND_COUNT + sid] = (raw, 0)
        truth["static"][sid] = raw
    write_mul(root, "art.mul", "artidx.mul", art, C.uoart.LAND_COUNT + 256)
    gumps = {}
    for gid, (w, h) in {1: (10, 10), 50: (33, 7), 51: (1, 200)}.items():
        px = [rng.choice([0, rng.randrange(1, 0x8000)]) for _ in range(w * h)]
        gumps[gid] = (C.encode_gump(px, w, h), (w << 16) | h)
        truth["gump"][gid] = px
    write_mul(root, "gumpart.mul", "gumpidx.mul", gumps, 64)
    anims = {}
    for body, action, direction in ((400, 0, 0), (400, 0, 3), (400, 34, 4), (1, 5, 2)):
        raw = C.encode_anim(rnd_anim(rng, rng.randrange(1, 8)))
        anims[uoread.anim_group_index(body, action, direction)] = (raw, 0)
        truth["anim"][(body, action, direction)] = raw
    write_mul(root, "anim.mul", "anim.idx", anims, 35000 + 175 * 2)
    write_tiledata(root / "tiledata.mul", {7: {"name": "test shield", "flags": 0x444002, "layer": 2, "anim": 400}})
    return truth


# --- the tests -----------------------------------------------------------------------

def test_static_codec_round_trip(rng: random.Random) -> None:
    for _ in range(40):
        w, h = rng.randrange(1, 70), rng.randrange(1, 70)
        px = rnd_static(rng, w, h)
        raw = C.encode_static(px, w, h, header=rng.randrange(0, 1 << 32))
        dw, dh, back, hdr = C.decode_static(raw)
        assert (dw, dh, back) == (w, h, px) and C.encode_static(back, dw, dh, hdr) == raw, \
            "static art: encode -> decode -> encode is stable, header kept"


def test_land_codec(rng: random.Random) -> None:
    px = rnd_land(rng)
    tail = bytes(range(24))
    raw = C.encode_land(px, tail)
    back, t = C.decode_land(raw)
    assert back == px and t == tail and len(raw) == 2048, "land: 1,012 diamond pixels plus the UOP padding kept"


def test_anim_codec_round_trip(rng: random.Random) -> None:
    for _ in range(20):
        g = rnd_anim(rng, rng.randrange(1, 12))
        raw = C.encode_anim(g)
        d = C.decode_anim(raw)
        assert d.palette == g.palette and [(f.center_x, f.center_y, f.width, f.height, f.index) for f in d.frames] == \
            [(f.center_x, f.center_y, f.width, f.height, f.index) for f in g.frames], \
            "animation groups: palette, centres, sizes and coverage round trip"
        assert C.encode_anim(d) == raw, "animation groups re-encode to the same bytes"


def test_anim_codec_limits() -> None:
    wide = C.AnimFrame(0, 0, 511, 1, [7] * 511)
    raw = C.encode_anim(C.AnimGroup([0] * 256, [wide]))
    assert C.decode_anim(raw).frames[0].index == [7] * 511, "a 511-pixel run, the widest a frame can hold"
    with pytest.raises(ValueError):
        C.encode_anim(C.AnimGroup([0] * 256, [C.AnimFrame(0, 0, 600, 1, [-1] * 560 + [7] * 40)]))
        pytest.fail("a frame past the format's 10-bit offsets is refused")


def test_transparency() -> None:
    assert C.uoart.to16(0, 0, 0, 255, False) == C.uoart.NEAR_BLACK and C.uoart.to16(0, 0, 0, 0, False) == 0, \
        "transparency: alpha 0 -> 0, opaque black -> near-black 0x0421"


def test_uop_hash() -> None:
    names = ["build/artlegacymul/00007028.tga", "a", "exactly12chr", "build/gumpartlegacymul/00050581.tga"]
    assert len({uoread.uop_hash(n) for n in names}) == len(names), "UOP name hashes are distinct"
    assert uoread.uop_hash("build/artlegacymul/00007028.tga") == uoread.uop_hash("build/artlegacymul/00007028.tga"), \
        "UOP name hash is deterministic"


def test_uop_reader(tmp_path: Path, rng: random.Random) -> None:
    entries = {f"build/artlegacymul/{i:08d}.tga": bytes(rng.randrange(256) for _ in range(rng.randrange(1, 300)))
               for i in (0, 5, 0x4000 + 7)}
    write_uop(tmp_path / "artLegacyMUL.uop", entries)
    art = uoread.Art(tmp_path)
    assert all(art._raw(int(n[-12:-4])) == d for n, d in entries.items()), "entries found by name hash, bytes equal"
    assert art._raw(1) is None, "a missing entry is None"


# --- unpack -> pack, on one fake install shared by the module ------------------------------

@pytest.fixture(scope="module")
def unpacked(tmp_path_factory: pytest.TempPathFactory) -> tuple[Path, Path]:
    """The fake install and every asset in it unpacked: (install, unpacked folder). Tests only read it."""
    tmp = tmp_path_factory.mktemp("uopack")
    data = tmp / "install"
    fake_install(data, random.Random(SEED))
    out = tmp / "unpacked"
    for what, ids in (("art", "5,7,200,201,9"), ("land", "0,3,100"), ("gumps", "1,50,51,2"),
                      ("anim", "400,1"), ("tiledata", "7")):
        subprocess.run(RUN + ["unpack", "--from", str(data), "--what", what, "--ids", ids, "--out", str(out)],
                       check=True, capture_output=True)
    return data, out


def test_unpack_sidecars(unpacked: tuple[Path, Path]) -> None:
    _data, out = unpacked
    sides = sorted(p.relative_to(out).as_posix() for p in out.rglob("*.json") if p.name != "uopack.json")
    assert len(sides) == 4 + 3 + 3 + 4 + 1, f"one sidecar per asset ({len(sides)})"
    tile = json.loads((out / "art" / "static_0x0007.json").read_text())
    assert tile["tiledata"]["name"] == "test shield" and tile["tiledata"]["anim"] == 400, \
        "art sidecars carry the item's tiledata"


def test_roundtrip_byte_identical(unpacked: tuple[Path, Path]) -> None:
    data, out = unpacked
    r = subprocess.run(RUN + ["roundtrip", str(out), "--from", str(data)], capture_output=True, text=True)
    assert r.returncode == 0 and "15/15" in r.stdout, "every record byte-identical to the source: " + r.stdout.strip()[-80:]


def test_roundtrip_fresh_encoders(unpacked: tuple[Path, Path]) -> None:
    data, out = unpacked
    r = subprocess.run(RUN + ["roundtrip", str(out), "--from", str(data), "--no-reuse"], capture_output=True, text=True)
    assert r.returncode == 0, "the fresh encoders alone reproduce the synthetic entries: " + r.stdout.strip()[-60:]


def test_edit_is_reencoded(tmp_path: Path, unpacked: tuple[Path, Path]) -> None:
    """An edit: repaint a static pixel; pack must re-encode it and carry the edit, reusing the rest."""
    from PIL import Image

    data, source = unpacked
    out = tmp_path / "unpacked"
    shutil.copytree(source, out)
    p = out / "art" / "static_0x0005.png"
    with Image.open(p) as im:
        im = im.convert("RGBA")
        im.putpixel((10, 15), (255, 0, 0, 255))
        im.save(p)
    recs = tmp_path / "records"
    r = subprocess.run(RUN + ["pack", str(out), "--source", str(data), "--records", str(recs)],
                       capture_output=True, text=True)
    assert r.returncode == 0 and "13 reused" in r.stdout and "1 encoded" in r.stdout, \
        "an edited asset is re-encoded, the rest reused: " + r.stdout.strip()[-90:]
    index = json.loads((recs / "records.json").read_text())["records"]
    rec = next(x for x in index if x["kind"] == "static" and x["id"] == 5)
    w, h, px, _ = C.decode_static((recs / rec["bin"]).read_bytes())
    assert px[15 * w + 10] == 0x7C00, "the edit is in the encoded record (pure red = 0x7C00)"


def test_new_animation(tmp_path: Path) -> None:
    """New art: RGBA animation frames pack through a palette built from them."""
    from PIL import Image

    folder = tmp_path / "newanim" / "anim" / "body_0849"
    folder.mkdir(parents=True)
    frames = []
    for k in range(3):
        im = Image.new("RGBA", (12, 20), (0, 0, 0, 0))
        for y in range(4, 16):
            for x in range(2, 10):
                im.putpixel((x, y), ((x * 20 + k) % 256, y * 12, 200, 255))
        im.putpixel((5, 5), (0, 0, 0, 255))
        png = f"a00_d0_f{k:02d}.png"
        im.save(folder / png)
        frames.append({"png": png, "center_x": 3, "center_y": -12, "width": 12, "height": 20})
    (folder / "a00_d0.json").write_text(json.dumps(
        {"kind": "anim", "body": 849, "action": 0, "direction": 0, "frames": frames}))
    recs = tmp_path / "newrecords"
    r = subprocess.run(RUN + ["pack", str(tmp_path / "newanim"), "--records", str(recs)], capture_output=True, text=True)
    assert r.returncode == 0, "RGBA frames pack through a palette built from them: " + r.stdout.strip()[-90:]
    rec = json.loads((recs / "records.json").read_text())["records"][0]
    g = C.decode_anim((recs / rec["bin"]).read_bytes())
    f = g.frames[1]
    assert (f.center_x, f.center_y, f.width, f.height) == (3, -12, 12, 20), "centres and sizes carried"
    assert f.index[0] == -1 and f.index[5 * 12 + 5] >= 0 and g.palette[f.index[5 * 12 + 5]] == 0, \
        "transparent pixels uncovered; opaque black kept as colour 0 (anim palettes have no transparent colour)"


def main() -> int:
    """Script entry (and run.py selftest): runs this module's tests through pytest (pytest.ini applies)."""
    code = pytest.main([__file__, "-q"])
    print("PASS" if code == 0 else "FAIL")
    return 0 if code == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
