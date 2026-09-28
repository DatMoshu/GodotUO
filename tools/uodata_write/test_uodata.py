"""Tests for tools/uodata_write on synthetic fixtures (no UO client data), for CI. Exit 0/1.

    python tools/uodata_write/test_uodata.py

Builds a tiny fake install in a temporary folder: tiledata.mul (land groups
plus two static groups), anim.idx/anim.mul covering people bodies 400-402,
and art and gump LegacyMUL UOPs with a few entries each. Then checks the
staged set, the free-slot scanners, the range registry, the append-only
writers (the chained UOP block, MUL+IDX, tiledata in place) and the
read-back, and that the fake install is byte for byte unchanged.
"""
from __future__ import annotations

import hashlib
import struct
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))

import uodata as U  # noqa: E402
from guo.uomap import uop_entries, uop_hash  # noqa: E402
from guo.uorecord import AssetRecord  # noqa: E402

FAILS: list[str] = []


def check(ok: bool, what: str) -> None:
    print(f"{'ok  ' if ok else 'FAIL'} {what}")
    if not ok:
        FAILS.append(what)


def make_uop(path: Path, entries: dict[str, bytes], gump: bool = False) -> None:
    """A minimal LegacyMUL UOP: header, data, one block of entries."""
    out = bytearray(struct.pack("<IIIqIi", U.UOP_MAGIC, 5, 0xFD23EC43, 0, 100, len(entries)))
    records = []
    for name, data in entries.items():
        at = len(out)
        out += data
        records.append(struct.pack("<qiiiQIh", at, 0, len(data), len(data), uop_hash(name), 0, 0))
    block = len(out)
    out += struct.pack("<iq", len(entries), 0) + b"".join(records)
    struct.pack_into("<q", out, 12, block)
    path.write_bytes(bytes(out))


def fake_install(root: Path) -> None:
    root.mkdir()
    # tiledata: 512 land groups, then two static groups (items 0-63); items 0-9 used
    tile = bytearray(U.TILE_LAND_BYTES + 2 * U.TILE_STATIC_BLOCK)
    for i in range(10):
        o = U.tile_offset(i)
        struct.pack_into("<QBBiHHHB", tile, o, 1, 1, 0, 0, 0, 0, 0, 1)
        tile[o + 21:o + 25] = b"used"
    (root / "tiledata.mul").write_bytes(bytes(tile))
    # anim.idx: people bodies 400-402; body 400 used, 401-402 empty
    idx = bytearray()
    mul = bytearray(b"\x00" * 16)
    for i in range(35000 + 3 * 175):
        if 35000 <= i < 35000 + 175:
            idx += struct.pack("<iii", len(mul), 8, 0)
            mul += b"animdata"
        else:
            idx += struct.pack("<iii", -1, -1, 0)
    (root / "anim.idx").write_bytes(bytes(idx))
    (root / "anim.mul").write_bytes(bytes(mul))
    # art: land 0 and statics 0-9; gumps 0-4 (with the 8-byte width/height prefix)
    make_uop(root / "artLegacyMUL.uop", {U.ART_PATTERN.format(i): b"L" * 2048 for i in range(1)}
             | {U.ART_PATTERN.format(U.LAND_COUNT + i): b"S" * 40 for i in range(10)})
    make_uop(root / "gumpartLegacyMUL.uop", {U.GUMP_PATTERN.format(i): struct.pack("<II", 2, 2) + b"G" * 16 for i in range(5)})


def digest(root: Path) -> dict:
    return {p.name: hashlib.sha1(p.read_bytes()).hexdigest() for p in sorted(root.iterdir())}


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="uodata-test-") as tmp:
        tmp = Path(tmp)
        install = tmp / "install"
        fake_install(install)
        before = digest(install)

        # the stage refuses to live inside the install
        try:
            U.Stage(install / "stage", install)
            check(False, "a stage inside the install is refused")
        except ValueError:
            check(True, "a stage inside the install is refused")

        stage = U.Stage(tmp / "stage", install)
        check(stage.meta["files"] == {}, "a new stage copies nothing until written")

        # free slots
        fs = U.free_statics(stage)
        check(fs == list(range(10, 64)), f"free item ids are 10-63 (got {fs[:3]}...{fs[-2:]}, {len(fs)})")
        fb = U.free_people_bodies(stage)
        check(fb == [401, 402], f"free people bodies are 401 and 402 (got {fb})")
        fg = U.free_gumps(stage, limit=16)
        check(fg == set(range(5, 16)), "free gumps are 5-15")

        # the range policy: a pack's fixed range, ids barred, a maintainer's override (ADR-0022)
        policy = {"never": {"static": [[60, 63]]}, "packs": {"fixed": {"static": [40, 43]}}}
        reg = U.Registry(U.Stage(tmp / "policy-stage", install), policy)
        check(reg.reserve("fixed", "static", fs, 99) == (40, 43), "a pack's range from the policy is used as given")
        check(reg.reserve("free", "static", fs, 4) == (56, 59), "ids the policy bars are never handed out")
        try:
            clash = {"never": {}, "packs": {"clash": {"static": [8, 11]}}}
            U.Registry(U.Stage(tmp / "clash-stage", install), clash).reserve("clash", "static", fs, 4)
            check(False, "a policy range over used ids is refused")
        except ValueError:
            check(True, "a policy range over used ids is refused")
        override = tmp / "shard-ranges.json"
        override.write_text('{"never": {"static": [[50, 50]]}, "packs": {"moshu": {"static": [20, 29]}}}', encoding="utf-8")
        merged = U.load_policy(override)
        check(merged["packs"]["moshu"]["static"] == [20, 29] and [50, 50] in merged["never"]["static"]
              and [65535, 65535] in merged["never"]["static"], "a maintainer's file replaces pack ranges and adds to never")
        default = U.load_policy()
        check(default["packs"]["moshu"]["static"] == [0xFFF0, 0xFFFE] and [0xFFFF, 0xFFFF] in default["never"]["static"],
              "the default policy: moshu at 0xFFF0-0xFFFE, 0xFFFF never")

        # the registry: contiguous ranges, packs kept apart
        reg = U.Registry(stage, {"never": {}, "packs": {}})
        a = reg.reserve("alpha", "static", fs, 8)
        b = reg.reserve("beta", "static", fs, 8)
        check(a == (56, 63) and b == (48, 55), f"packs get contiguous, separate ranges from the top (alpha {a}, beta {b})")
        low = reg.reserve("gamma", "static", fs, 4, prefer_high=False)
        check(low == (10, 13), f"prefer_high=False takes the lowest run (got {low})")
        first, again = reg.take("alpha", "static", "one"), reg.take("alpha", "static", "one")
        check(first == 56 and again == 56 and reg.take("alpha", "static", "two") == 57, "take() is stable and fills the range in order")
        check(U.Registry(stage, {}).data["packs"]["alpha"]["used"]["static"] == {"one": 56, "two": 57}, "slots.json persists the ids used")

        # writers
        art_before = uop_entries((install / "artLegacyMUL.uop").read_bytes())
        records = [
            AssetRecord("static", 56, b"NEWSTATIC" * 3),
            AssetRecord("gump", 7, struct.pack("<II", 3, 1) + b"row!"),
            AssetRecord("anim", 401, b"frames-a0d0", {"action": 0, "direction": 0}),
            AssetRecord("anim", 401, b"frames-a1d4", {"action": 1, "direction": 4}),
            AssetRecord("tiledata-item", 56, b"", {"name": "test shield", "layer": 2, "anim": 401, "flags": 0x400000}),
        ]
        U.write_records(stage, records)
        for r in records:
            got = U.read_back(stage, r)
            if r.kind == "tiledata-item":
                got = {k: got[k] for k in ("name", "layer", "anim", "flags")}
                check(got == r.meta, "tiledata item written in place and read back")
            else:
                check(got == r.data, f"{r.kind} {r.id} read back equal")

        art = uop_entries(stage.read_path("artLegacyMUL.uop").read_bytes())
        check(all(art.get(h) == v for h, v in art_before.items()), "the UOP's existing entries are unchanged (append-only)")
        check(len(art) == len(art_before) + 1, "the UOP gained exactly one entry, through a chained block")
        raw = stage.read_path("artLegacyMUL.uop").read_bytes()
        _m, _v, _s, first_block, _bs, count = struct.unpack_from("<IIIqIi", raw, 0)
        _files, nxt = struct.unpack_from("<iq", raw, first_block)
        check(nxt != 0 and struct.unpack_from("<iq", raw, nxt) == (1, 0), "the new block is chained after the old one")
        check(count == 12, f"the header's file count grew by one (got {count})")
        try:
            U.uop_append(stage.read_path("artLegacyMUL.uop"), [(U.ART_PATTERN.format(U.LAND_COUNT + 56), b"x")])
            check(False, "writing an existing UOP name is refused")
        except ValueError:
            check(True, "writing an existing UOP name is refused")

        idx = stage.read_path("anim.idx").read_bytes()
        check(all(struct.unpack_from("<iii", idx, i * 12) == struct.unpack_from("<iii", (install / "anim.idx").read_bytes(), i * 12)
                  for i in range(35000, 35175)), "anim.idx entries of the used body are unchanged")
        n0 = U.anim_index(401, 0, 0)
        check(struct.unpack_from("<iii", idx, (n0 + 1) * 12)[0] == -1, "an unwritten direction stays empty")
        tile = stage.read_path("tiledata.mul").read_bytes()
        orig = (install / "tiledata.mul").read_bytes()
        o = U.tile_offset(56)
        check(tile[:o] == orig[:o] and tile[o + U.TILE_STATIC_RECORD:] == orig[o + U.TILE_STATIC_RECORD:],
              "tiledata: only the one record changed")
        check(U.read_tile(stage.read_path("tiledata.mul"), 56)["weight"] == 0, "tiledata: fields not given keep their value")

        used = U.Registry(stage, {}).data["packs"]["alpha"]["used"]["static"]
        check(sorted(used.values()) == [56, 57] and "one" in used,
              "a write inside a pack's range is recorded once, under the name take() gave it")
        U.write_records(stage, [AssetRecord("static", 58, b"THIRD")])
        check(U.Registry(stage, {}).data["packs"]["alpha"]["used"]["static"].get("static-0x3a") == 58,
              "a write that bypassed take() is recorded in slots.json")

        # after writing, the slot is no longer free
        check(56 not in U.free_statics(stage) and 401 not in U.free_people_bodies(stage), "written slots are no longer free")

        check(set(stage.meta["files"]) == {"artlegacymul.uop", "gumpartlegacymul.uop", "anim.idx", "anim.mul", "tiledata.mul"},
              "the stage copied exactly the five files it wrote")
        check((stage.root / "files_override.txt").read_text().count("=") == 5, "files_override.txt lists them")
        check(stage.check_install_unchanged() == [] and digest(install) == before, "the install is byte for byte unchanged")

    print(f"test_uodata: {'OK' if not FAILS else 'FAILED'} ({len(FAILS)} failing)")
    return 0 if not FAILS else 1


if __name__ == "__main__":
    raise SystemExit(main())
