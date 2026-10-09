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
import os
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


def multi_record(multi_id: int, comps: list[tuple[int, int, int, int, int]]) -> bytes:
    out = struct.pack("<Ii", multi_id, len(comps))
    for item, x, y, z, flags in comps:
        out += struct.pack("<HhhhHI", item, x, y, z, flags, 0)
    return out


def test_multis(tmp: Path) -> None:
    """Multis: MultiCollection.uop on a UOP install, multi.mul/idx (grown) on a MUL one."""
    install = tmp / "install-multi"
    fake_install(install)
    make_uop(install / "MultiCollection.uop", {U.MULTI_PATTERN.format(i): multi_record(i, [(0x63, 0, 0, 0, 0)])
                                               for i in range(3)})
    before = digest(install)
    stage = U.Stage(tmp / "stage-multi", install)
    fm = U.free_multis(stage)
    check(fm[:2] == [3, 4] and len(fm) == U.MULTI_LIMIT - 3, "free multis: every id below 0x4000 without an entry")
    policy = {"never": {}, "packs": {"multi": {"multi": [0x3F00, 0x3FFF]}}}
    reg = U.Registry(stage, policy)
    check(reg.reserve("multi", "multi", fm, 1) == (0x3F00, 0x3FFF), "the multi pack takes its fixed range")
    rec = AssetRecord("multi", reg.take("multi", "multi", "cottage"),
                      multi_record(0x3F00, [(0x203, -1, -1, 7, 0), (0x6A5, 0, 1, 7, 1), (1, 0, 0, 0, 1)]))
    U.write_records(stage, [rec])
    check(U.read_back(stage, rec) == rec.data and U.read_back_equal(stage, rec), "a multi reads back equal from the UOP")
    raw = stage.read_path("MultiCollection.uop").read_bytes()
    _m, _v, _s, first, _bs, _n = struct.unpack_from("<IIIqIi", raw, 0)
    _f, block = struct.unpack_from("<iq", raw, first)
    _o, _h, comp, decomp, _hash, _a, flag = struct.unpack_from("<qiiiQIh", raw, block + 12)
    check(flag == 1 and decomp == len(rec.data) and comp != decomp,
          "a multi entry is zlib-compressed (flag 1), as the shard needs")
    check(0x3F00 not in U.free_multis(stage), "a written multi id is no longer free")
    check(U.Registry(stage, {}).data["packs"]["multi"]["used"]["multi"] == {"cottage": 0x3F00}, "slots.json records the multi")
    try:
        U.write_records(stage, [AssetRecord("multi", 0x4000, rec.data)])
        check(False, "a multi id the shard cannot address is refused")
    except ValueError:
        check(True, "a multi id the shard cannot address is refused")

    mul_install = tmp / "install-multi-mul"
    fake_install(mul_install)
    (mul_install / "multi.idx").write_bytes(struct.pack("<iii", 0, 16, 0) + struct.pack("<iii", -1, -1, 0))
    (mul_install / "multi.mul").write_bytes(struct.pack("<HhhhII", 0x63, 0, 0, 0, 1, 0))
    mul_before = digest(mul_install)
    mstage = U.Stage(tmp / "stage-multi-mul", mul_install)
    rec = AssetRecord("multi", 40, multi_record(40, [(0x203, 2, 3, 7, 0), (0x6A5, 0, 1, 7, 1)]))
    U.write_records(mstage, [rec])
    got = U.read_back(mstage, rec)
    check(got == U.multi_to_mul(rec.data) and U.read_back_equal(mstage, rec), "a multi reads back from multi.mul")
    check(struct.unpack_from("<HhhhI", got, 16)[4] == 0 and struct.unpack_from("<HhhhI", got, 0)[4] == 1,
          "multi.mul flags: shown 1, hidden 0")
    check(mstage.read_path("multi.idx").stat().st_size == 41 * 12, "multi.idx grew to hold id 40")
    check(digest(install) == before and digest(mul_install) == mul_before, "both installs are byte for byte unchanged")


def refused(what: str, fn) -> None:
    try:
        fn()
        check(False, what)
    except ValueError:
        check(True, what)


def test_refusals(tmp: Path) -> None:
    """Review M3/M4/L7: occupied MUL slots, the old tiledata layout, bad hue records;
    each refused before anything is written."""
    install = tmp / "install-mul"
    fake_install(install)
    # a MUL art install: artidx.mul/art.mul (static 0 used), gumpidx.mul/gumpart.mul (gump 0 used)
    (install / "artLegacyMUL.uop").unlink()
    (install / "gumpartLegacyMUL.uop").unlink()
    art_idx = [(-1, -1, 0)] * (U.LAND_COUNT + 64)
    art_idx[U.LAND_COUNT] = (0, 4, 0)
    (install / "artidx.mul").write_bytes(b"".join(struct.pack("<iii", *e) for e in art_idx))
    (install / "art.mul").write_bytes(b"ART0")
    (install / "gumpidx.mul").write_bytes(struct.pack("<iii", 0, 4, 0x00020002) + struct.pack("<iii", -1, -1, 0) * 15)
    (install / "gumpart.mul").write_bytes(b"GMP0")
    (install / "hues.mul").write_bytes(bytes(2 * U.HUE_GROUP))
    before = digest(install)

    stage = U.Stage(tmp / "stage-mul", install)
    used_anim = AssetRecord("anim", 400, b"new-frames", {"action": 0, "direction": 0})
    refused("MUL art: an occupied artidx slot is refused",
            lambda: U.write_records(stage, [AssetRecord("static", 0, b"NEW")]))
    refused("MUL gumps: an occupied gumpidx slot is refused",
            lambda: U.write_records(stage, [AssetRecord("gump", 0, struct.pack("<II", 1, 1) + b"gg")]))
    refused("anims: an occupied anim.idx slot is refused",
            lambda: U.write_records(stage, [used_anim]))
    refused("a refused record stops the whole call: the free one before it is not written either",
            lambda: U.write_records(stage, [AssetRecord("static", 20, b"FREE"), AssetRecord("static", 0, b"NEW")]))
    check(stage.meta["files"] == {}, "after the refusals nothing was copied into the stage")

    U.write_records(stage, [AssetRecord("static", 0, b"NEW!"), used_anim], replace=True)
    check(U.read_back(stage, AssetRecord("static", 0, b"")) == b"NEW!" and U.read_back(stage, used_anim) == used_anim.data,
          "with replace, occupied MUL art and anim slots are overwritten")
    U.write_records(stage, [AssetRecord("static", 20, b"FREE")])
    check(U.read_back(stage, AssetRecord("static", 20, b"")) == b"FREE", "a free MUL slot is written without replace")

    # hues: exactly one 88-byte record, id 1..count
    refused("a hue record longer than 88 bytes is refused", lambda: U.write_records(stage, [AssetRecord("hue", 1, bytes(176))]))
    refused("hue id 0 is refused", lambda: U.write_records(stage, [AssetRecord("hue", 0, bytes(88))]))
    refused("a hue past the end of hues.mul is refused", lambda: U.write_records(stage, [AssetRecord("hue", 17, bytes(88))]))
    U.write_records(stage, [AssetRecord("hue", 16, b"" * 88)])
    raw = stage.read_path("hues.mul").read_bytes()
    check(raw[U.HUE_GROUP + 4 + 7 * 88:U.HUE_GROUP + 4 + 8 * 88] == b"" * 88 and raw.count(1) == 88,
          "the last hue in hues.mul is written in place, nothing else")

    # tiledata: the pre-7.0.9.0 layout is refused
    old = tmp / "install-oldtile"
    fake_install(old)
    (old / "tiledata.mul").write_bytes(bytes(U.TILE_OLD_LAND_BYTES + 2 * U.TILE_OLD_STATIC_BLOCK))
    check(U.tile_layout(old / "tiledata.mul") == "old" and U.tile_layout(install / "tiledata.mul") == "new",
          "the tiledata layout is told from the file's size")
    ostage = U.Stage(tmp / "stage-oldtile", old)
    refused("writing a tiledata item into the old layout is refused",
            lambda: U.write_records(ostage, [AssetRecord("tiledata-item", 5, b"", {"name": "x"})]))
    refused("reading the old layout is refused", lambda: U.read_tile(old / "tiledata.mul", 5))
    refused("counting items in the old layout is refused", lambda: U.static_count(old / "tiledata.mul"))
    check(ostage.meta["files"] == {}, "the refused tiledata write copied nothing")
    check(digest(install) == before, "the MUL install is byte for byte unchanged")


def link_dir(link: Path, target: Path) -> bool:
    """A directory link (a symlink, else on Windows a junction); False if neither can be made."""
    try:
        os.symlink(target, link, target_is_directory=True)
        return True
    except (OSError, NotImplementedError):
        pass
    try:
        import _winapi
        _winapi.CreateJunction(str(target), str(link))
        return True
    except (ImportError, OSError):
        return False


def test_stage_root(tmp: Path) -> None:
    """SF4: every path written resolves under the stage root (after .. and links), checked
    before the first byte; one bad path in a call writes nothing at all."""
    install = tmp / "install-sf4"
    fake_install(install)
    (install / "hues.mul").write_bytes(bytes(2 * U.HUE_GROUP))
    outside = tmp / "outside"
    outside.mkdir()
    before = digest(install)

    def stage_with(name: str, entry: str) -> U.Stage:
        st = U.Stage(tmp / name, install)
        st.meta["files"]["tiledata.mul"] = {"name": entry, "source_sha1": "", "source_size": 0}
        st.save()
        return st

    def nothing_written(st: U.Stage, what: str) -> None:
        names = sorted(p.name for p in st.root.iterdir())
        check(list(outside.iterdir()) == [] and names == ["files_override.txt", "guo_data.json", "stage.json"]
              and set(st.meta["files"]) == {"tiledata.mul"}, what)

    tile = AssetRecord("tiledata-item", 20, b"", {"name": "escape"})
    hue = AssetRecord("hue", 1, bytes(88))

    dotdot = stage_with("stage-dotdot", "../outside/tiledata.mul")
    refused("a staged name with .. that leaves the root is refused",
            lambda: U.write_records(dotdot, [hue, tile]))
    nothing_written(dotdot, "the .. refusal wrote nothing: no file outside, hues.mul not copied, no slots.json")
    refused("copy on write to a .. name is refused", lambda: dotdot.path("tiledata.mul"))
    nothing_written(dotdot, "the refused copy on write wrote nothing")

    absolute = stage_with("stage-absolute", str(outside / "tiledata.mul"))
    refused("an absolute staged name outside the root is refused", lambda: U.write_records(absolute, [hue, tile]))
    nothing_written(absolute, "the absolute-path refusal wrote nothing")

    inner = U.Stage(tmp / "stage-inner", install)
    (inner.root / "sub").mkdir()
    inner.meta["files"]["tiledata.mul"] = {"name": "sub/../tiledata.mul", "source_sha1": "", "source_size": 0}
    check(inner.target("tiledata.mul") == inner.root / "tiledata.mul", "a .. that stays under the root is allowed")

    linked = U.Stage(tmp / "stage-linked", install)
    if link_dir(linked.root / "sub", outside):
        linked.meta["files"]["tiledata.mul"] = {"name": "sub/tiledata.mul", "source_sha1": "", "source_size": 0}
        linked.save()
        refused("a staged name through a directory link that leaves the root is refused",
                lambda: U.write_records(linked, [hue, tile]))
        check(list(outside.iterdir()) == [] and "hues.mul" not in linked.meta["files"],
              "the link refusal wrote nothing, through the link or beside it")
    else:
        print("skip a directory link could not be made here")

    fstage = U.Stage(tmp / "stage-filelink", install)
    try:
        os.symlink(outside / "slots.json", fstage.root / "slots.json")
        refused("a slots.json that links outside the root is refused", lambda: U.write_records(fstage, [hue]))
        check(list(outside.iterdir()) == [] and fstage.meta["files"] == {}, "the slots.json refusal wrote nothing")
        refused("the registry will not save through it either", lambda: U.Registry(fstage, {}).save())
    except (OSError, NotImplementedError):
        print("skip a file symlink could not be made here (no privilege)")

    good = U.Stage(tmp / "stage-good", install)
    U.write_records(good, [hue, tile])
    check(U.read_back(good, tile)["name"] == "escape" and set(good.meta["files"]) == {"hues.mul", "tiledata.mul"},
          "a call whose paths are all under the root writes as before")
    check(digest(install) == before, "the install is byte for byte unchanged")


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

        test_multis(tmp)
        test_refusals(tmp)
        test_stage_root(tmp)

    print(f"test_uodata: {'OK' if not FAILS else 'FAILED'} ({len(FAILS)} failing)")
    return 0 if not FAILS else 1


if __name__ == "__main__":
    raise SystemExit(main())
