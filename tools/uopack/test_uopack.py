"""tools/uopack tests on synthetic data only (CI has no client data, rule 8).

    python tools/uopack/test_uopack.py        (or: python tools/uopack/run.py selftest)

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
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))

from guo import uoread  # noqa: E402
import uocodecs as C  # noqa: E402

RUN = [sys.executable, str(HERE / "run.py")]
FAILED: list[str] = []


def check(ok: bool, what: str) -> None:
    print(f"  {'ok  ' if ok else 'FAIL'} {what}", flush=True)
    if not ok:
        FAILED.append(what)


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

def test_codecs(rng: random.Random) -> None:
    print("codecs")
    ok = True
    for _ in range(40):
        w, h = rng.randrange(1, 70), rng.randrange(1, 70)
        px = rnd_static(rng, w, h)
        raw = C.encode_static(px, w, h, header=rng.randrange(0, 1 << 32))
        dw, dh, back, hdr = C.decode_static(raw)
        ok &= (dw, dh, back) == (w, h, px) and C.encode_static(back, dw, dh, hdr) == raw
    check(ok, "static art: encode -> decode -> encode is stable, header kept")
    px = rnd_land(rng)
    tail = bytes(range(24))
    raw = C.encode_land(px, tail)
    back, t = C.decode_land(raw)
    check(back == px and t == tail and len(raw) == 2048, "land: 1,012 diamond pixels plus the UOP padding kept")
    ok = True
    for _ in range(20):
        g = rnd_anim(rng, rng.randrange(1, 12))
        raw = C.encode_anim(g)
        d = C.decode_anim(raw)
        ok &= d.palette == g.palette and [(f.center_x, f.center_y, f.width, f.height, f.index) for f in d.frames] == \
            [(f.center_x, f.center_y, f.width, f.height, f.index) for f in g.frames]
        ok &= C.encode_anim(d) == raw
    check(ok, "animation groups: palette, centres, sizes and coverage round trip")
    wide = C.AnimFrame(0, 0, 511, 1, [7] * 511)
    raw = C.encode_anim(C.AnimGroup([0] * 256, [wide]))
    check(C.decode_anim(raw).frames[0].index == [7] * 511, "a 511-pixel run, the widest a frame can hold")
    try:
        C.encode_anim(C.AnimGroup([0] * 256, [C.AnimFrame(0, 0, 600, 1, [-1] * 560 + [7] * 40)]))
        check(False, "a frame past the format's 10-bit offsets is refused")
    except ValueError:
        check(True, "a frame past the format's 10-bit offsets is refused")
    check(C.uoart.to16(0, 0, 0, 255, False) == C.uoart.NEAR_BLACK and C.uoart.to16(0, 0, 0, 0, False) == 0,
          "transparency: alpha 0 -> 0, opaque black -> near-black 0x0421")
    names = ["build/artlegacymul/00007028.tga", "a", "exactly12chr", "build/gumpartlegacymul/00050581.tga"]
    check(len({uoread.uop_hash(n) for n in names}) == len(names), "UOP name hashes are distinct")
    check(uoread.uop_hash("build/artlegacymul/00007028.tga") == uoread.uop_hash("build/artlegacymul/00007028.tga"),
          "UOP name hash is deterministic")


def test_uop(tmp: Path, rng: random.Random) -> None:
    print("uop reader")
    entries = {f"build/artlegacymul/{i:08d}.tga": bytes(rng.randrange(256) for _ in range(rng.randrange(1, 300)))
               for i in (0, 5, 0x4000 + 7)}
    write_uop(tmp / "artLegacyMUL.uop", entries)
    art = uoread.Art(tmp)
    check(all(art._raw(int(n[-12:-4])) == d for n, d in entries.items()), "entries found by name hash, bytes equal")
    check(art._raw(1) is None, "a missing entry is None")


def test_roundtrip(tmp: Path, rng: random.Random) -> None:
    print("unpack -> pack")
    data = tmp / "install"
    truth = fake_install(data, rng)
    out = tmp / "unpacked"
    for what, ids in (("art", "5,7,200,201,9"), ("land", "0,3,100"), ("gumps", "1,50,51,2"),
                      ("anim", "400,1"), ("tiledata", "7")):
        subprocess.run(RUN + ["unpack", "--from", str(data), "--what", what, "--ids", ids, "--out", str(out)],
                       check=True, capture_output=True)
    sides = sorted(p.relative_to(out).as_posix() for p in out.rglob("*.json") if p.name != "uopack.json")
    check(len(sides) == 4 + 3 + 3 + 4 + 1, f"one sidecar per asset ({len(sides)})")
    tile = json.loads((out / "art" / "static_0x0007.json").read_text())
    check(tile["tiledata"]["name"] == "test shield" and tile["tiledata"]["anim"] == 400,
          "art sidecars carry the item's tiledata")
    r = subprocess.run(RUN + ["roundtrip", str(out), "--from", str(data)], capture_output=True, text=True)
    check(r.returncode == 0 and "15/15" in r.stdout, "every record byte-identical to the source: " + r.stdout.strip()[-80:])
    r = subprocess.run(RUN + ["roundtrip", str(out), "--from", str(data), "--no-reuse"], capture_output=True, text=True)
    check(r.returncode == 0, "the fresh encoders alone reproduce the synthetic entries: " + r.stdout.strip()[-60:])

    # An edit: repaint a static and a gump pixel; pack must re-encode and carry the edit.
    from PIL import Image

    p = out / "art" / "static_0x0005.png"
    with Image.open(p) as im:
        im = im.convert("RGBA")
        im.putpixel((10, 15), (255, 0, 0, 255))
        im.save(p)
    recs = tmp / "records"
    r = subprocess.run(RUN + ["pack", str(out), "--source", str(data), "--records", str(recs)],
                       capture_output=True, text=True)
    check(r.returncode == 0 and "13 reused" in r.stdout and "1 encoded" in r.stdout,
          "an edited asset is re-encoded, the rest reused: " + r.stdout.strip().splitlines()[0][-90:])
    index = json.loads((recs / "records.json").read_text())["records"]
    rec = next(x for x in index if x["kind"] == "static" and x["id"] == 5)
    w, h, px, _ = C.decode_static((recs / rec["bin"]).read_bytes())
    check(px[15 * w + 10] == 0x7C00, "the edit is in the encoded record (pure red = 0x7C00)")


def test_new_animation(tmp: Path) -> None:
    print("new art (RGBA animation frames)")
    from PIL import Image

    folder = tmp / "newanim" / "anim" / "body_0849"
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
    recs = tmp / "newrecords"
    r = subprocess.run(RUN + ["pack", str(tmp / "newanim"), "--records", str(recs)], capture_output=True, text=True)
    check(r.returncode == 0, "RGBA frames pack through a palette built from them")
    rec = json.loads((recs / "records.json").read_text())["records"][0]
    g = C.decode_anim((recs / rec["bin"]).read_bytes())
    f = g.frames[1]
    check((f.center_x, f.center_y, f.width, f.height) == (3, -12, 12, 20), "centres and sizes carried")
    check(f.index[0] == -1 and f.index[5 * 12 + 5] >= 0 and g.palette[f.index[5 * 12 + 5]] == 0,
          "transparent pixels uncovered; opaque black kept as colour 0 (anim palettes have no transparent colour)")


# SpriteMotion's synthetic transfer artifact (CC0), copied byte for byte from its
# tests/fixtures/transfer/ at commit 480b43f (their #25, which carries #24's schema and
# fixture; their v0.2.0 release changes neither). Kind spritemotion.transfer-artifact v1.
TRANSFER_FIXTURE = HERE / "fixtures" / "spritemotion_transfer_v1"


def tree_hash(folder: Path) -> str:
    import hashlib

    h = hashlib.sha256()
    for p in sorted(folder.rglob("*")):
        if p.is_file():
            h.update(p.relative_to(folder).as_posix().encode() + b"\0" + p.read_bytes())
    return h.hexdigest()


def case_from_job(tmp: Path) -> None:
    print("from-job (SpriteMotion transfer artifact)")
    import hashlib
    from PIL import Image
    from run import pixels as flat

    fixture = TRANSFER_FIXTURE
    before = tree_hash(fixture)
    manifest = json.loads((fixture / "transfer.json").read_text(encoding="utf-8"))
    out = tmp / "job"
    r = subprocess.run(RUN + ["from-job", str(fixture), "--out", str(out), "--body", "849"],
                       capture_output=True, text=True)
    check(r.returncode == 2 and "preview" in r.stdout and not out.exists(),
          "a preview export is refused in plain words, nothing written: " + r.stdout.strip()[-70:])
    r = subprocess.run(RUN + ["from-job", str(fixture), "--out", str(out), "--body", "849", "--allow-preview"],
                       capture_output=True, text=True)
    check(r.returncode == 0 and "10 animation groups, 24 frames" in r.stdout,
          "the fixture imports with --allow-preview: " + r.stdout.strip().splitlines()[-1][-70:])
    folder = out / "anim" / "body_0849"
    sides = {p.stem: json.loads(p.read_text()) for p in folder.glob("*.json")}
    ok = len(sides) == 10
    for f in manifest["frames"]:
        side = sides.get(f"a{f['action']:02d}_d{f['direction']}")
        e = side["frames"][f["index"]] if side else None
        if not e:
            ok = False
            continue
        ok &= side["body"] == 849 and side["action"] == f["action"] and side["direction"] == f["direction"]
        if f["empty"]:
            ok &= (e["width"], e["height"]) == (0, 0)
            continue
        c = f["crop"]
        ok &= (e["center_x"], e["center_y"]) == (128 - c["left"], 192 - c["bottom"]) == (f["centre"]["x"], f["centre"]["y"])
        ok &= (e["width"], e["height"]) == (c["right"] - c["left"], c["bottom"] - c["top"])
        with Image.open(folder / e["png"]) as a, Image.open(fixture / f["png"]) as b:
            ok &= a.mode == "RGBA" and flat(a) == flat(b.convert("RGBA"))
    check(ok, "fields match: one group per (action, direction), centres from crops (-22,-12 kept), "
              "empty frame 0x0, RGBA pixels as exported")
    info = json.loads((out / "uopack.json").read_text())
    check(info["identity"]["item_id"] == "synthetic-flag" and info["provenance"]["source"]["license"] == "CC0-1.0",
          "uopack.json carries identity and provenance")
    check(tree_hash(fixture) == before, "the artifact is only read")
    recs = tmp / "job_records"
    r = subprocess.run(RUN + ["pack", str(out), "--records", str(recs)], capture_output=True, text=True)
    index = json.loads((recs / "records.json").read_text())["records"] if r.returncode == 0 else []
    rec = next((x for x in index if x["meta"] == {"action": 4, "direction": 4}), None)
    g = C.decode_anim((recs / rec["bin"]).read_bytes()) if rec else None
    check(len(index) == 10 and g is not None and (g.frames[0].center_x, g.frames[0].center_y,
                                                  g.frames[0].width, g.frames[0].height) == (-22, -12, 20, 54),
          "the frames land: pack makes 10 animation records, the negative centre decodes back")

    # Refusals: an unknown schema version, a frame changed after export.
    for name, edit, words in (
            ("v2", lambda m, d: m.update(schema_version=2), "version 2"),
            ("tampered", lambda m, d: (d / "frames" / "a00-d0-f0.png").write_bytes(
                (d / "frames" / "a00-d0-f0.png").read_bytes() + b"x"), "sha256")):
        bad = tmp / f"artifact_{name}"
        shutil.copytree(fixture, bad)
        m = json.loads((bad / "transfer.json").read_text())
        edit(m, bad)
        (bad / "transfer.json").write_text(json.dumps(m))
        r = subprocess.run(RUN + ["from-job", str(bad), "--out", str(tmp / f"out_{name}"), "--body", "849",
                                  "--allow-preview"], capture_output=True, text=True)
        check(r.returncode == 2 and words in r.stdout and not (tmp / f"out_{name}").exists(),
              f"refused ({name}): " + r.stdout.strip()[-90:])

    # A full export whose group has more than 256 colours, plus item art and tiledata:
    # pack's one quantizer brings it to 256, transparency kept.
    many = tmp / "artifact_many"
    shutil.copytree(fixture, many)
    m = json.loads((many / "transfer.json").read_text())
    m["animation"]["coverage"] = "full"
    f0 = next(f for f in m["frames"] if (f["action"], f["direction"], f["index"]) == (0, 0, 0))
    w, h = f0["crop"]["right"] - f0["crop"]["left"], f0["crop"]["bottom"] - f0["crop"]["top"]
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    for y in range(h):
        for x in range(w):
            if (x + y) % 5:
                im.putpixel((x, y), (x * 10 % 256, y * 2 % 256, (x * y) % 256, 255))
    im.save(many / f0["png"])
    f0["sha256"] = hashlib.sha256((many / f0["png"]).read_bytes()).hexdigest()
    Image.new("RGBA", (12, 30), (90, 60, 30, 255)).save(many / "item.png")
    m["equipment"].update(item_art={"path": "item.png", "sha256": hashlib.sha256((many / "item.png").read_bytes()).hexdigest()},
                          tiledata={"name": "synthetic flag", "flags": 0x400000, "layer": 1, "weight": 3})
    (many / "transfer.json").write_text(json.dumps(m))
    colours = {((r_ >> 3), (g_ >> 3), (b_ >> 3)) for r_, g_, b_, a in flat(im) if a >= 128}
    out2, recs2 = tmp / "job_many", tmp / "job_many_records"
    r = subprocess.run(RUN + ["from-job", str(many), "--out", str(out2), "--body", "849", "--item", "0xFFF1"],
                       capture_output=True, text=True)
    ok = r.returncode == 0
    r = subprocess.run(RUN + ["pack", str(out2), "--records", str(recs2)], capture_output=True, text=True)
    ok &= r.returncode == 0
    index = json.loads((recs2 / "records.json").read_text())["records"] if ok else []
    rec = next((x for x in index if x["kind"] == "anim" and x["meta"] == {"action": 0, "direction": 0}), None)
    g = C.decode_anim((recs2 / rec["bin"]).read_bytes()) if rec else None
    if g:
        fr = g.frames[0]
        alpha = [a for *_, a in flat(im)]
        ok &= len(g.palette) <= 256 and all(0 <= i < 256 for i in fr.index if i >= 0)
        ok &= all((i < 0) == (a < 128) for i, a in zip(fr.index, alpha))
    check(ok and g is not None and len(colours) > 256,
          f"a group of {len(colours)} colours packs to one 256-colour palette, transparent pixels uncovered")
    tile = next((x for x in index if x["kind"] == "tiledata-item"), None)
    check(any(x["kind"] == "static" and x["id"] == 0xFFF1 for x in index) and tile is not None
          and tile["meta"].get("anim") == 849 and tile["meta"].get("name") == "synthetic flag",
          "--item brings the item art and its tiledata (anim = the body)")


def main() -> int:
    rng = random.Random(20260927)
    tmp = Path(tempfile.mkdtemp(prefix="uopack_test_"))
    try:
        test_codecs(rng)
        test_uop(tmp, rng)
        test_roundtrip(tmp, rng)
        test_new_animation(tmp)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    tmp = Path(tempfile.mkdtemp(prefix="uopack_job_test_"))
    try:
        case_from_job(tmp)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print("PASS" if not FAILED else f"FAIL ({len(FAILED)})")
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())
