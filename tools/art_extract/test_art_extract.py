"""tools/art_extract tests on a synthetic install only (rule 8: no game data in tests).

    python -m pytest tools/art_extract -q
"""

from __future__ import annotations

import hashlib
import json
import random
import struct
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))

sys.path.append(str(HERE.parent / "uopack"))

import art_anim as anim  # noqa: E402
import art_atlas as atlas  # noqa: E402
import art_export as ex  # noqa: E402
import art_png as pngio  # noqa: E402
import art_sources as srcs  # noqa: E402
from guo import uoart, uoread  # noqa: E402
import uocodecs as codecs  # noqa: E402
import run as cli  # noqa: E402

NEAR_BLACK = uoart.NEAR_BLACK


# --- a fake install ------------------------------------------------------------------------

def write_mul(folder: Path, mul: str, idx: str, entries: dict[int, tuple[bytes, int]], count: int) -> None:
    data = bytearray()
    records = [(-1, -1, 0)] * count
    for i, (payload, extra) in sorted(entries.items()):
        records[i] = (len(data), len(payload), extra)
        data += payload
    (folder / mul).write_bytes(bytes(data))
    (folder / idx).write_bytes(b"".join(struct.pack("<iii", *r) for r in records))


def write_uop(path: Path, entries: dict[str, bytes]) -> None:
    names = list(entries)
    table_at = 512
    at = table_at + 12 + 34 * len(names)
    rows, blob = [], bytearray()
    for n in names:
        rows.append(struct.pack("<qiiiQIh", at + len(blob), 0, len(entries[n]), len(entries[n]),
                                uoread.uop_hash(n), 0, 0))
        blob += entries[n]
    out = bytearray(struct.pack("<IIIqIi", uoread.UOP_MAGIC, 5, 0xFD23EC43, table_at, len(names), len(names)))
    out += bytes(table_at - len(out))
    out += struct.pack("<iq", len(names), 0) + b"".join(rows) + blob
    path.write_bytes(bytes(out))


def rnd_static(rng: random.Random, w: int, h: int) -> list[int]:
    return [rng.choice([0, 0, NEAR_BLACK, rng.randrange(1, 0x8000)]) for _ in range(w * h)]


def rnd_land(rng: random.Random) -> list[int]:
    return [rng.randrange(0, 0x8000) if uoart.in_diamond(i % 44, i // 44) else 0 for i in range(44 * 44)]


def rgba(c: int, keyed: bool = True) -> bytes:
    if keyed and c == 0:
        return b"\0\0\0\0"
    t = srcs._EXPAND
    return bytes((t[(c >> 10) & 31], t[(c >> 5) & 31], t[c & 31], 255))


class Truth:
    """What the fake install holds, as RGBA, built without going through the code under test."""

    def __init__(self):
        self.land, self.static, self.gump, self.texmap, self.light = {}, {}, {}, {}, {}
        self.anim: dict[str, list[tuple]] = {}      # block key -> [(num, cx, cy, w, h, rgba or None)]


def fake_install(root: Path, rng: random.Random, uop: bool = False, extra_statics: int = 0) -> Truth:
    root.mkdir(parents=True, exist_ok=True)
    t = Truth()
    art, art_names = {}, {}
    for lid in (0, 3, 100):
        px = rnd_land(rng)
        px[22 * 44 + 22] = 0      # inside the diamond, colour 0: black, drawn
        art[lid] = (uoart.encode_land(px), 0)
        t.land[lid] = b"".join(rgba(v, False) if uoart.in_diamond(i % 44, i // 44) else b"\0\0\0\0"
                               for i, v in enumerate(px))
    sizes = {5: (20, 30), 7: (44, 48), 9: (20, 30), 200: (1, 1), 201: (60, 9)}
    for k in range(extra_statics):
        sizes[300 + k] = (200, 100)
    for sid, (w, h) in sizes.items():
        px = rnd_static(rng, w, h)
        art[uoart.LAND_COUNT + sid] = (uoart.encode_static(px, w, h), 0)
        t.static[sid] = (w, h, b"".join(rgba(v) for v in px))
    gumps = {}
    for gid, (w, h) in {1: (10, 10), 50: (33, 7), 51: (1, 200)}.items():
        px = [rng.choice([0, rng.randrange(1, 0x8000)]) for _ in range(w * h)]
        gumps[gid] = (uoart.encode_gump(px, w, h), (w << 16) | h)
        t.gump[gid] = (w, h, b"".join(rgba(v) for v in px))
    if uop:
        write_uop(root / "artLegacyMUL.uop", {f"build/artlegacymul/{i:08d}.tga": raw for i, (raw, _) in art.items()})
        write_uop(root / "gumpartLegacyMUL.uop", {f"build/gumpartlegacymul/{i:08d}.tga":
                                                  struct.pack("<ii", e >> 16, e & 0xFFFF) + raw
                                                  for i, (raw, e) in gumps.items()})
    else:
        write_mul(root, "art.mul", "artidx.mul", art, uoart.LAND_COUNT + 0x500)
        write_mul(root, "gumpart.mul", "gumpidx.mul", gumps, 64)
    # texmaps: 64x64 and 128x128, plus TexTerr.def aliasing 5 to 1
    tex = {}
    for n, size in ((0, 64), (1, 128), (2, 64)):
        px = [rng.randrange(0, 0x8000) for _ in range(size * size)]
        tex[n] = (uoart.encode_texmap(px), 0)
        t.texmap[n] = (size, size, b"".join(rgba(v, False) for v in px))
    write_mul(root, "texmaps.mul", "texidx.mul", tex, 8)
    (root / "TexTerr.def").write_text("# aliases\n5  {1}  1\n6 {2, 0} 3\n99 {1}\n", encoding="utf-8")
    t.texmap[5] = t.texmap[1]
    t.texmap[6] = t.texmap[0]            # the last member of the group wins
    # lights: values 0, 5, 0x1F, and a negative (bit inverted) one
    lights = {0: (bytes([0, 5, 0x1F, 0xE0, 0, 1]), (3 << 16) | 2)}
    write_mul(root, "light.mul", "lightidx.mul", lights, 4)

    def lv(v):
        v = ~v & 0x1F if v > 0x1F else v
        return bytes((v << 3,) * 3 + (255,)) if v else b"\0\0\0\0"

    t.light[0] = (3, 2, b"".join(lv(v) for v in (0, 5, 0x1F, 0xE0, 0, 1)))
    add_animations(root, rng, t)
    return t

# --- fake animations -----------------------------------------------------------------------

def rnd_palette(rng: random.Random) -> list[int]:
    return [0] + [rng.randrange(1, 0x8000) for _ in range(255)]


def rnd_frame(rng: random.Random, palette: list[int], w: int, h: int) -> codecs.AnimFrame:
    """A frame with centre inside the run limits and about half its pixels covered."""
    index = [rng.randrange(0, 256) if rng.random() < 0.55 else -1 for _ in range(w * h)]
    return codecs.AnimFrame(w // 2, h - 1 if h > 1 else 0, w, h, index)


def truth_pixels(f: codecs.AnimFrame, palette: list[int], keyed: bool) -> bytes:
    return b"".join(rgba(palette[v], keyed) if v >= 0 else b"\x00\x00\x00\x00" for v in f.index)


def uop_group_bytes(table: list[tuple[int, codecs.AnimFrame, list[int]]]) -> bytes:
    """table: (frame id, frame, palette). The layout ReadUOPAnimationFrames reads: 32 bytes, count, start, 16-byte rows."""
    fc = len(table)
    head = bytearray(32) + struct.pack("<iI", fc, 40)
    rows, blobs, at = bytearray(), bytearray(), 40 + 16 * fc
    for n, (frame_id, frame, palette) in enumerate(table):
        start = 40 + 16 * n
        rows += struct.pack("<HHQI", 1, frame_id, 0, at + len(blobs) - start)
        blobs += struct.pack("<256H", *palette) + codecs._encode_frame(frame)
    return bytes(head + rows + blobs)


def add_animations(root: Path, rng: random.Random, t: Truth) -> None:
    # anim.mul (file 0): two directions of one group, and a third index record that points at the first block again
    pal = rnd_palette(rng)
    blocks = {}
    for d in range(2):
        frames = [rnd_frame(rng, pal, rng.randrange(1, 30), rng.randrange(1, 40)) for _ in range(3)]
        frames.append(codecs.AnimFrame(0, 0, 0, 0, []))                        # a frame with no size
        blocks[d] = (codecs.encode_anim(codecs.AnimGroup(pal, frames)), 0)
    write_mul(root, "anim.mul", "anim.idx", blocks, 6)
    idx = bytearray((root / "anim.idx").read_bytes())
    idx[12 * 4:12 * 5] = idx[0:12]                                              # record 4 repeats record 0
    (root / "anim.idx").write_bytes(bytes(idx))
    size_of = {d: len(blocks[d][0]) for d in blocks}
    pos = 0
    for d in range(2):
        g = codecs.decode_anim(blocks[d][0])
        t.anim[anim.mul_key(0, pos, size_of[d])] = [
            (i, f.center_x, f.center_y, f.width, f.height, truth_pixels(f, pal, False) if f.width > 0 else None)
            for i, f in enumerate(g.frames)]
        pos += size_of[d]
    # anim2.mul (file 1): one block
    pal2 = rnd_palette(rng)
    f2 = [rnd_frame(rng, pal2, 7, 9)]
    write_mul(root, "anim2.mul", "anim2.idx", {2: (codecs.encode_anim(codecs.AnimGroup(pal2, f2)), 0)}, 3)
    t.anim[anim.mul_key(1, 0, len(codecs.encode_anim(codecs.AnimGroup(pal2, f2))))] = [
        (0, f2[0].center_x, f2[0].center_y, 7, 9, truth_pixels(f2[0], pal2, False))]
    # AnimationFrame1.uop: ten frame ids over five directions = two frames each, id 4 missing (a gap the loader fills)
    table, truth = [], {}
    for fid in (1, 2, 3, 5, 6, 7, 8, 9, 10):
        p = rnd_palette(rng)
        f = rnd_frame(rng, p, rng.randrange(1, 20), rng.randrange(1, 25))
        table.append((fid, f, p))
        truth[fid] = (f.center_x, f.center_y, f.width, f.height, truth_pixels(f, p, True))
    data = uop_group_bytes(table)
    write_uop(root / "AnimationFrame1.uop", {"build/animationlegacyframe/000001/00.bin": data})
    entry = [e for e in uoread.UopFile(root / "AnimationFrame1.uop").entries.values()][0]

    def direction(d: int, real: int) -> list[tuple]:
        out = [(0, 0, 0, 0, 0, None)] * real                                   # uncovered frames keep Num 0
        for fid in range(1, 11):
            if (fid - 1) // real != d:
                continue
            if fid == 4:                                                       # the gap the loader fills: Num is its index
                out[(fid - 1) % real] = ((fid - 1) % real, 0, 0, 0, 0, None)
            else:
                cx, cy, w, h, px = truth[fid]
                out[(fid - 1) % real] = (fid - 1) % real, cx, cy, w, h, px
        return out

    for d in range(5):
        plain = direction(d, 2)
        t.anim[anim.uop_key(0, entry.offset, d)] = plain
        eq = direction(d, 10)
        t.anim[anim.uop_key(0, entry.offset, d, True)] = eq


def tree_hash(root: Path) -> dict[str, str]:
    return {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(root.rglob("*")) if p.is_file()}


GEN = "2026-01-01T00:00:00Z"


@pytest.fixture(params=["mul", "uop"])
def install(request, tmp_path):
    rng = random.Random(7)
    data = tmp_path / "data"
    truth = fake_install(data, rng, uop=request.param == "uop")
    return data, truth


def export_to(data: Path, out: Path, what=srcs.CLASSES, **kw) -> dict:
    return ex.export(data, out, what, client_version="7.0.1", generated=GEN, log=lambda *_: None, **kw)


def read_class(out: Path, cls: str):
    index = json.loads((out / cls / "index.json").read_text(encoding="utf-8"))
    pages = [pngio.decode_rgba((out / cls / p["file"]).read_bytes())[2] for p in index["pages"]]
    return index, pages


# --- tests ---------------------------------------------------------------------------------

def test_export_matches_the_install_pixel_for_pixel(install, tmp_path):
    data, truth = install
    out = tmp_path / "set"
    doc = export_to(data, out)
    assert set(doc["classes"]) == set(srcs.CLASSES)
    for cls, expected in (("land", truth.land), ("static", truth.static), ("gump", truth.gump),
                          ("texmap", truth.texmap), ("light", truth.light)):
        index, pages = read_class(out, cls)
        assert {int(i) for i in index["entries"]} == set(expected), cls
        for i, want in expected.items():
            e = index["entries"][str(i)]
            wh = (44, 44, want) if cls == "land" else want
            assert (e["w"], e["h"]) == wh[:2]
            assert atlas.crop(pages[e["page"]], e["x"], e["y"], e["w"], e["h"]) == wh[2], (cls, i)
            assert e["pixels_sha256"] == atlas.pixels_digest(wh[0], wh[1], wh[2])


# --- animations ----------------------------------------------------------------------------

def rows_to_frames(rows: list, pages: list[bytes]) -> list[tuple]:
    out = []
    for r in rows:
        if len(r) == 3:
            out.append((r[0], r[1], r[2], 0, 0, None))
        else:
            page, x, y, w, h, cx, cy = r
            out.append((-1, cx, cy, w, h, atlas.crop(pages[page], x, y, w, h)))
    return out


def test_animation_blocks_match_the_install_frame_for_frame(install, tmp_path):
    data, truth = install
    out = tmp_path / "set"
    doc = export_to(data, out, ("anim",))
    index, pages = read_class(out, "anim") if False else (None, None)
    index = json.loads((out / "anim" / "index.json").read_text(encoding="utf-8"))
    pages = [pngio.decode_rgba((out / "anim" / p["file"]).read_bytes())[2] for p in index["pages"]]
    assert set(index["blocks"]) == set(truth.anim)
    assert doc["classes"]["anim"]["count"] == len(truth.anim) and index["skipped"] == []
    for key, want in truth.anim.items():
        got = rows_to_frames(index["blocks"][key]["f"], pages)
        assert len(got) == len(want), key
        for n, (g, w) in enumerate(zip(got, want)):
            if w[5] is None:                                  # an empty frame carries its Num and centre
                assert g == w, (key, n)
            else:
                assert (g[1], g[2], g[3], g[4], g[5]) == (w[1], w[2], w[3], w[4], w[5]), (key, n)


def test_uop_equipment_reads_differ_only_where_the_loader_differs(tmp_path):
    data = tmp_path / "data"
    truth = fake_install(data, random.Random(21))
    keys = set(truth.anim)
    assert any(k.endswith(".e") for k in keys)
    # the plain read of direction 1 has the filled gap as an empty frame with its own index
    plain = next(v for k, v in truth.anim.items() if k.startswith("u0.") and k.endswith(".1"))
    assert plain[1] == (1, 0, 0, 0, 0, None)
    mul_keys = [k for k in keys if k.startswith("m")]
    assert len(mul_keys) == 3                                 # record 4 repeats record 0: one block, not two


def test_animation_verify_accepts_then_names_a_changed_frame(install, tmp_path):
    data, truth = install
    out = tmp_path / "set"
    export_to(data, out)
    result = ex.verify(data, out, ("anim",), log=lambda *_: None)
    assert result["ok"], result["mismatches"]
    assert result["classes"]["anim"]["ok"] == len(truth.anim)

    path = out / "anim" / "index.json"
    index = json.loads(path.read_text(encoding="utf-8"))
    key = next(k for k in index["blocks"] if k.startswith("m1."))
    index["blocks"][key]["f"][0][5] += 1                      # a centre that is not the install's
    path.write_text(json.dumps(index), encoding="utf-8")
    result = ex.verify(data, out, ("anim",), log=lambda *_: None)
    assert not result["ok"] and any(m.startswith(f"anim {key}:") for m in result["mismatches"])

    page = out / "anim" / "page_0000.png"
    raw = bytearray(page.read_bytes())
    raw[-20] ^= 0xFF
    page.write_bytes(bytes(raw))
    result = ex.verify(data, out, ("anim",), log=lambda *_: None)
    assert any("anim/page_0000.png is missing or damaged" in m for m in result["mismatches"])


def test_animation_blocks_that_cannot_be_read_are_skipped_not_fatal(tmp_path):
    data = tmp_path / "data"
    fake_install(data, random.Random(23))
    mul = data / "anim.mul"
    raw = bytearray(mul.read_bytes())
    # first frame of the first block: cut its run list short by making the first header claim a long run
    pos = struct.unpack_from("<I", raw, 516)[0] + 512 + 8
    struct.pack_into("<I", raw, pos, (0x3FF << 22) | (0x3FF << 12) | 0xFFF)
    mul.write_bytes(bytes(raw))
    out = tmp_path / "set"
    doc = export_to(data, out, ("anim",))
    index = json.loads((out / "anim" / "index.json").read_text(encoding="utf-8"))
    assert doc["classes"]["anim"]["skipped"] == 1 and index["skipped"][0]["key"].startswith("m0.0.")
    assert ex.verify(data, out, ("anim",), log=lambda *_: None)["ok"]


def test_sequence_packer_keeps_a_blocks_frames_together_and_shares_twins():
    pages = []
    packer = anim.SequencePacker(lambda n, buf: pages.append(n))
    a = anim.Frame(0, 0, 0, 40, 30, bytes([1, 2, 3, 255]) * 1200)
    b = anim.Frame(1, 0, 0, 40, 30, bytes([9, 9, 9, 255]) * 1200)
    first, second, twin = packer.place(a), packer.place(b), packer.place(a)
    assert first == (0, 0, 0) and second == (0, 40, 0) and twin == first
    wide = anim.Frame(2, 0, 0, 2000, 10, bytes([5, 5, 5, 255]) * 20000)
    assert packer.place(wide) == (0, 0, 30)                    # no room on the row: the next row, same page
    tall = anim.Frame(3, 0, 0, 100, 2040, bytes([6, 6, 6, 255]) * 204000)
    assert packer.place(tall)[0] == 1 and pages == [0]         # the page was written when the next one began
    packer.finish()
    assert pages == [0, 1]


def test_colour_rules_of_the_loaders(tmp_path):
    data = tmp_path / "data"
    fake_install(data, random.Random(1))
    out = tmp_path / "set"
    export_to(data, out)
    index, pages = read_class(out, "land")
    e = index["entries"]["0"]
    tile = atlas.crop(pages[0], e["x"], e["y"], 44, 44)
    assert tile[:4] == b"\0\0\0\0"                                      # a corner is not part of the tile
    centre = (22 * 44 + 22) * 4
    assert tile[centre:centre + 4] == bytes((0, 0, 0, 255))            # colour 0 inside the diamond is black, drawn
    assert rgba(NEAR_BLACK) == bytes((8, 8, 8, 255))                    # opaque black in a static is near black
    index, pages = read_class(out, "light")
    e = index["entries"]["0"]
    got = atlas.crop(pages[0], e["x"], e["y"], 3, 2)
    assert got[:4] == b"\0\0\0\0" and got[4:8] == bytes((40, 40, 40, 255))
    assert got[8:12] == bytes((248, 248, 248, 255))
    assert got[12:16] == bytes((248, 248, 248, 255))                    # 0xE0 is below zero: bit inverted
    assert got[20:24] == bytes((8, 8, 8, 255))


def test_two_exports_are_byte_identical(install, tmp_path):
    data, _ = install
    a, b = tmp_path / "a", tmp_path / "b"
    export_to(data, a)
    export_to(data, b)
    assert tree_hash(a) == tree_hash(b)
    assert (a / "set.json").read_text(encoding="utf-8") == (b / "set.json").read_text(encoding="utf-8")


def test_default_generated_stamp_does_not_depend_on_the_clock(install, tmp_path):
    data, _ = install
    a, b = tmp_path / "a", tmp_path / "b"
    ex.export(data, a, ("light",), client_version="7.0.1", log=lambda *_: None)
    ex.export(data, b, ("light",), client_version="7.0.1", log=lambda *_: None)
    assert (a / "set.json").read_bytes() == (b / "set.json").read_bytes()


def test_documents_follow_the_schemas(install, tmp_path):
    jsonschema = pytest.importorskip("jsonschema")
    data, _ = install
    out = tmp_path / "set"
    export_to(data, out)
    schema_dir = HERE / "schema"
    jsonschema.validate(json.loads((out / "set.json").read_text()),
                        json.loads((schema_dir / "set.schema.json").read_text()))
    for cls in srcs.CLASSES:
        name = "anim_index.schema.json" if cls == "anim" else "index.schema.json"
        jsonschema.validate(json.loads((out / cls / "index.json").read_text()),
                            json.loads((schema_dir / name).read_text()))


def test_verify_accepts_a_good_set_and_counts(install, tmp_path):
    data, truth = install
    out = tmp_path / "set"
    export_to(data, out)
    result = ex.verify(data, out, log=lambda *_: None)
    assert result["ok"], result["mismatches"]
    assert result["classes"]["static"]["ok"] == len(truth.static)
    assert result["classes"]["texmap"]["ok"] == len(truth.texmap)


def test_verify_names_a_damaged_page_and_a_changed_pixel(install, tmp_path):
    data, _ = install
    out = tmp_path / "set"
    export_to(data, out)
    page = out / "static" / "page_0000.png"
    raw = bytearray(page.read_bytes())
    raw[-20] ^= 0xFF
    page.write_bytes(bytes(raw))
    result = ex.verify(data, out, ("static",), log=lambda *_: None)
    assert not result["ok"] and any("damaged" in m for m in result["mismatches"])

    # a page that is a valid PNG but holds a different pixel: the index hash catches it
    out2 = tmp_path / "set2"
    export_to(data, out2)
    index, pages = read_class(out2, "gump")
    e = index["entries"]["1"]
    buf = bytearray(pages[0])
    buf[(e["y"] * atlas.PAGE + e["x"]) * 4 + 3] ^= 0xFF
    png = pngio.encode_rgba(atlas.PAGE, atlas.PAGE, buf)
    (out2 / "gump" / "page_0000.png").write_bytes(png)
    index["pages"][0]["sha256"] = hashlib.sha256(png).hexdigest()
    (out2 / "gump" / "index.json").write_text(json.dumps(index), encoding="utf-8")
    result = ex.verify(data, out2, ("gump",), log=lambda *_: None)
    assert not result["ok"] and any(m.startswith("gump 1:") for m in result["mismatches"])


def test_verify_notices_an_install_that_changed(install, tmp_path):
    data, _ = install
    out = tmp_path / "set"
    export_to(data, out)
    victim = data / ("texmaps.mul")
    victim.write_bytes(victim.read_bytes()[:-2] + b"\x01\x02")
    result = ex.verify(data, out, ("texmap",), log=lambda *_: None)
    assert not result["ok"]
    assert any("texmaps.mul" in m and "differs" in m for m in result["mismatches"])


def test_verify_notices_ids_missing_or_extra(tmp_path):
    data = tmp_path / "data"
    fake_install(data, random.Random(3))
    out = tmp_path / "set"
    export_to(data, out, ("gump",))
    path = out / "gump" / "index.json"
    index = json.loads(path.read_text())
    del index["entries"]["50"]
    index["entries"]["60"] = dict(index["entries"]["1"])
    path.write_text(json.dumps(index))
    result = ex.verify(data, out, log=lambda *_: None)
    texts = "\n".join(result["mismatches"])
    assert "gump 50: in the install but missing from the set" in texts
    assert "gump 60: in the set, but the install does not provide it" in texts


def test_broken_entries_are_skipped_not_fatal(tmp_path):
    data = tmp_path / "data"
    fake_install(data, random.Random(4))
    art = data / "art.mul"
    idx = data / "artidx.mul"
    records = [list(struct.unpack_from("<iii", idx.read_bytes(), i)) for i in range(0, idx.stat().st_size, 12)]
    garbage = b"\xff" * 64
    pos = art.stat().st_size
    art.write_bytes(art.read_bytes() + garbage)
    records[uoart.LAND_COUNT + 77] = [pos, len(garbage), 0]
    records[44] = [pos, 10, 0]               # a land tile with fewer bytes than a tile
    idx.write_bytes(b"".join(struct.pack("<iii", *r) for r in records))
    out = tmp_path / "set"
    doc = export_to(data, out, ("land", "static"))
    assert doc["classes"]["static"]["skipped"] == 1 and doc["classes"]["land"]["skipped"] == 1
    index, _ = read_class(out, "static")
    assert index["skipped"][0]["id"] == 77 and "77" not in index["entries"]
    assert ex.verify(data, out, log=lambda *_: None)["ok"]


def test_identical_images_share_a_rectangle(tmp_path):
    data = tmp_path / "data"
    fake_install(data, random.Random(5))
    out = tmp_path / "set"
    export_to(data, out, ("static",))
    index, _ = read_class(out, "static")
    a, b = index["entries"]["5"], index["entries"]["9"]
    # 5 and 9 are different random images here, so they must not share
    assert (a["page"], a["x"], a["y"]) != (b["page"], b["x"], b["y"])
    img = (3, 3, bytes(range(36)))
    placed, pages = atlas.pack({1: img, 2: img, 3: (3, 3, bytes(reversed(range(36))))})
    assert (placed[1].x, placed[1].y) == (placed[2].x, placed[2].y) and placed[3].x != placed[1].x
    assert len(pages) == 1


def test_pack_is_shelf_ordered_and_never_overlaps():
    rng = random.Random(9)
    images = {i: (rng.randrange(1, 300), rng.randrange(1, 300), bytes(4)) for i in range(400)}
    # make pixels unique so nothing is shared
    images = {i: (w, h, struct.pack("<I", i) * (w * h)) for i, (w, h, _) in images.items()}
    placed, pages = atlas.pack(images)
    assert len(pages) > 1
    rects = []
    for i, p in placed.items():
        assert p.x + p.w <= atlas.PAGE and p.y + p.h <= atlas.PAGE
        rects.append((p.page, p.x, p.y, p.x + p.w, p.y + p.h, i))
    for n, (pg, x0, y0, x1, y1, i) in enumerate(rects):
        for (pg2, a0, b0, a1, b1, j) in rects[n + 1:]:
            assert pg != pg2 or x1 <= a0 or a1 <= x0 or y1 <= b0 or b1 <= y0, (i, j)
    # the first image placed is the tallest, lowest id
    tallest = min(images, key=lambda i: (-images[i][1], i))
    assert (placed[tallest].page, placed[tallest].x, placed[tallest].y) == (0, 0, 0)


def test_more_than_a_page_of_statics(tmp_path):
    data = tmp_path / "data"
    truth = fake_install(data, random.Random(11), extra_statics=240)
    out = tmp_path / "set"
    doc = export_to(data, out, ("static",))
    assert doc["classes"]["static"]["pages"] >= 2
    result = ex.verify(data, out, log=lambda *_: None)
    assert result["ok"] and result["classes"]["static"]["ok"] == len(truth.static)


def test_verdata_patches_win_like_in_the_loader(tmp_path):
    data = tmp_path / "data"
    fake_install(data, random.Random(13))
    px = [NEAR_BLACK] * 6
    patches = [uoart.Patch(uoart.VERDATA_ART, uoart.LAND_COUNT + 7, uoart.encode_static(px, 3, 2)),
               uoart.Patch(uoart.VERDATA_GUMP, 1, uoart.encode_gump([1, 2, 3, 4], 2, 2), (2 << 16) | 2)]
    uoart.write_verdata(data / "verdata.mul", patches)
    out = tmp_path / "set"
    doc = export_to(data, out)
    assert "verdata.mul" in {f["name"] for f in doc["fingerprint"]["files"]}
    index, pages = read_class(out, "static")
    e = index["entries"]["7"]
    assert (e["w"], e["h"]) == (3, 2)
    assert atlas.crop(pages[e["page"]], e["x"], e["y"], 3, 2) == rgba(NEAR_BLACK) * 6
    index, _ = read_class(out, "gump")
    assert (index["entries"]["1"]["w"], index["entries"]["1"]["h"]) == (2, 2)
    assert ex.verify(data, out, log=lambda *_: None)["ok"]


def test_a_partial_export_leaves_no_old_classes_behind(tmp_path):
    data = tmp_path / "data"
    fake_install(data, random.Random(15))
    out = tmp_path / "set"
    export_to(data, out)
    export_to(data, out, ("light",))
    assert [p.name for p in sorted(out.iterdir())] == ["light", "set.json"]
    assert set(json.loads((out / "set.json").read_text())["classes"]) == {"light"}


def test_set_id_is_the_digest_of_the_file_lines(install, tmp_path):
    data, _ = install
    doc = export_to(data, tmp_path / "set", ("light",))
    lines = "".join(f"{f['name']}\t{f['size']}\t{f['sha256']}\n" for f in doc["fingerprint"]["files"])
    assert doc["fingerprint"]["set_id"] == hashlib.sha256(lines.encode()).hexdigest()
    assert [f["name"] for f in doc["fingerprint"]["files"]] == ["light.mul", "lightidx.mul"]


def test_out_must_stay_inside_the_art_folder_or_build(tmp_path):
    art, root = tmp_path / "art", tmp_path / "repo"
    (root / "build").mkdir(parents=True)
    assert ex.check_out(art, art, root) == art.resolve()
    assert ex.check_out(art / "x" / "y", art, root)
    assert ex.check_out(root / "build" / "t", art, root)
    for bad in (tmp_path / "elsewhere", root / "docs", art / ".." / "oops", root):
        with pytest.raises(ex.ArtExtractError):
            ex.check_out(bad, art, root)


def test_png_reader_is_strict():
    px = bytes(range(16)) * 4
    png = pngio.encode_rgba(4, 4, px)
    assert pngio.decode_rgba(png) == (4, 4, px)
    assert pngio.encode_rgba(4, 4, px) == png
    text = png[:33] + struct.pack(">I", 4) + b"tEXt" + b"abcd" + struct.pack(">I", 0) + png[33:]
    with pytest.raises(ValueError):
        pngio.decode_rgba(text)
    with pytest.raises(ValueError):
        pngio.decode_rgba(png[:-4])


def test_what_words():
    assert cli.parse_what("art") == ("land", "static")
    assert cli.parse_what("gumps,lights") == ("gump", "light")
    assert cli.parse_what(None) == srcs.CLASSES
    with pytest.raises(ex.ArtExtractError):
        cli.parse_what("hues")


def test_cli_export_then_verify(tmp_path, monkeypatch, capsys):
    data = tmp_path / "data"
    fake_install(data, random.Random(17))
    root = tmp_path / "repo"
    (root / "build").mkdir(parents=True)
    art = tmp_path / "art"
    monkeypatch.setattr(ex, "settings", lambda *_: {"root": root, "client_data": data, "client_version": "7.0.1",
                                                    "art_dir": art})
    assert cli.main(["export", "--what", "gumps,texmaps"]) == 0
    assert cli.main(["verify"]) == 0
    assert cli.main(["export", "--out", str(tmp_path / "nope")]) == 2
    assert "outside UO_ART_EXTRACT_DIR" in capsys.readouterr().err
    (art / "gump" / "page_0000.png").write_bytes(b"x")
    assert cli.main(["verify"]) == 1
