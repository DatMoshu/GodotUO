"""Authoring UO data files (ADR-0022): staged set, free slots, ranges, writers.

Nothing here writes into the UO install. A **staged set** is a folder that holds
copies of only the install files that were changed (copy on first write), plus
files_override.txt for the client and the list of those files for the shard.
The install's copied files are hashed when copied and checked unchanged after.

Containers written (append-only, so nothing existing moves):
  LegacyMUL UOP  artLegacyMUL.uop, gumpartLegacyMUL.uop: data appended, a new
                 entry block chained onto the table (the client walks the chain
                 and looks entries up by name hash)
  MUL + IDX      anim.mul / anim.idx (data appended, index entry pointed at it),
                 art.mul / artidx.mul and gumpart.mul / gumpidx.mul when an
                 install has them instead of UOPs
  in place       tiledata.mul item records (the 7.0.9.0+ layout only), hues.mul blocks

A MUL + IDX slot that already holds data is refused unless the caller asks to
replace it, as a UOP name that already exists is always refused: an id the client
or a shard already uses is never overwritten by accident (ADR-0022). Every record
is checked before anything is written, so a refused call leaves the stage as it was.
  multis         MultiCollection.uop: a new entry per multi (UOP layout, zlib, flag 1),
                 or multi.mul / multi.idx on installs without it (the index grown if needed)

Every path the stage writes is checked to resolve (after .. and links) under the stage root
first, and write_records checks all of a call's paths before its first byte.
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import struct
import sys
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.uomap import UOP_MAGIC, uop_entries, uop_hash  # noqa: E402
from guo.uorecord import AssetRecord  # noqa: E402

LAND_COUNT = 0x4000
TILE_LAND_BLOCK = 4 + 32 * 30          # new (7.0.9.0+) land group
TILE_LAND_BYTES = 512 * TILE_LAND_BLOCK
TILE_STATIC_RECORD = 41                # flags 8, weight 1, layer 1, count 4, anim 2, hue 2, light 2, height 1, name 20
TILE_STATIC_BLOCK = 4 + 32 * TILE_STATIC_RECORD
TILE_OLD_LAND_BYTES = 512 * (4 + 32 * 26)   # before 7.0.9.0: 4-byte flags
TILE_OLD_STATIC_BLOCK = 4 + 32 * 37
HUE_RECORD = 88
HUE_GROUP = 4 + 8 * HUE_RECORD
MALE_GUMP, FEMALE_GUMP = 50000, 60000
PEOPLE_FIRST = 400

ART_PATTERN = "build/artlegacymul/{0:08d}.tga"
GUMP_PATTERN = "build/gumpartlegacymul/{0:08d}.tga"
ANIM_UOP_PATTERN = "build/animationlegacyframe/{0:06d}/{1:02d}.bin"
MULTI_PATTERN = "build/multicollection/{0:06d}.bin"
MULTI_LIMIT = 0x4000                   # the shard masks multi ids with 0x3FFF

TILE_FIELDS = ("flags", "weight", "layer", "count", "anim", "hue", "light", "height", "name")


# --- anim.idx indexing (MUL), as the loader computes it ------------------------

def anim_group(body: int) -> tuple[int, int]:
    """(first index, entries per body) for a body in anim.mul."""
    if body < 200:
        return body * 110, 110
    if body < 400:
        return 22000 + (body - 200) * 65, 65
    return 35000 + (body - 400) * 175, 175


def anim_index(body: int, action: int, direction: int) -> int:
    first, _ = anim_group(body)
    return first + action * 5 + direction


# --- the staged set ------------------------------------------------------------

def sha1(path: Path) -> str:
    h = hashlib.sha1()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 22), b""):
            h.update(chunk)
    return h.hexdigest()


class Stage:
    """A staged data set: copies of changed install files, never the install itself."""

    def __init__(self, root: Path, install: Path):
        self.root = root.resolve()
        self.install = install.resolve()
        try:
            self.root.relative_to(self.install)
            raise ValueError(f"a staged set inside the install is refused: {self.root}")
        except ValueError as e:
            if "refused" in str(e):
                raise
        self.root.mkdir(parents=True, exist_ok=True)
        self.meta_path = self.root / "stage.json"
        self.meta = json.loads(self.meta_path.read_text(encoding="utf-8")) if self.meta_path.exists() else \
            {"format": 1, "install": str(self.install), "files": {}}

    def inside(self, path: Path) -> Path:
        """The path, resolved through .. and links (symlinks, junctions), if it lies under
        the stage root; refused otherwise. Every file the stage writes goes through this."""
        real = Path(os.path.realpath(path))
        try:
            real.relative_to(self.root)
        except ValueError:
            raise ValueError(f"{path} resolves outside the staged set ({real}); nothing is written there") from None
        return real

    def target(self, name: str) -> Path:
        """Where path(name) writes, checked under the root, without copying anything."""
        key = name.lower()
        if key in self.meta["files"]:
            return self.inside(self.root / self.meta["files"][key]["name"])
        src = self.install_file(name)
        if src is None:
            raise FileNotFoundError(f"{name} is not in the install")
        return self.inside(self.root / src.name)

    def own_files(self) -> list[Path]:
        """The stage's own bookkeeping files, written by save() and the registry."""
        return [self.root / n for n in ("stage.json", "files_override.txt", "guo_data.json", "slots.json")]

    def install_file(self, name: str) -> Path | None:
        for p in self.install.iterdir():
            if p.name.lower() == name.lower():
                return p
        return None

    def path(self, name: str) -> Path:
        """The staged copy of an install file, copied on first use (copy on write)."""
        key = name.lower()
        dst = self.target(name)
        if key in self.meta["files"]:
            return dst
        src = self.install_file(name)
        shutil.copyfile(src, dst)
        self.meta["files"][key] = {"name": src.name, "source_sha1": sha1(src), "source_size": src.stat().st_size}
        self.save()
        return dst

    def read_path(self, name: str) -> Path:
        """Where the current version of a file is: the staged copy if there is one, else the install."""
        key = name.lower()
        if key in self.meta["files"]:
            return self.inside(self.root / self.meta["files"][key]["name"])
        p = self.install_file(name)
        if p is None:
            raise FileNotFoundError(name)
        return p

    def save(self) -> None:
        for path in self.own_files()[:3]:
            self.inside(path)
        self.meta_path.write_text(json.dumps(self.meta, indent=2) + "\n", encoding="utf-8")
        lines = ["# GUO staged data set (tools/uodata_write, ADR-0022): UOFilesOverrideMap entries"]
        lines += [f"{k}={self.root / v['name']}" for k, v in sorted(self.meta["files"].items())]
        (self.root / "files_override.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
        # ADR-0021: a stage is a layered custom data folder (UO_CUSTOM_DATA). Its
        # files are modified copies of the user's install, so it is local only.
        manifest = {"format": "guo/data-folder@1", "name": self.root.name, "mode": "layered",
                    "files": {v["name"]: {"replaces": k} for k, v in sorted(self.meta["files"].items())},
                    "contains_ea_data": True, "source": "tools/uodata_write (ADR-0022)"}
        (self.root / "guo_data.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    def check_install_unchanged(self) -> list[str]:
        """The install files the stage copied must still hash as they did."""
        changed = []
        for key, info in self.meta["files"].items():
            src = self.install_file(key)
            if src is None or src.stat().st_size != info["source_size"] or sha1(src) != info["source_sha1"]:
                changed.append(key)
        return changed


# --- UOP -------------------------------------------------------------------------

def uop_has(path: Path, name: str) -> bool:
    return uop_hash(name) in uop_table(path)


_UOP_CACHE: dict[tuple[str, float], dict] = {}


def uop_table(path: Path) -> dict:
    key = (str(path), path.stat().st_mtime)
    if key not in _UOP_CACHE:
        with path.open("rb") as f:
            _UOP_CACHE[key] = uop_entries(f.read())
    return _UOP_CACHE[key]


def uop_append(path: Path, items: list[tuple[str, bytes]], compress: bool = False) -> None:
    """Append entries and chain a new block of them onto the table: uncompressed (flag 0),
    or with compress zlib-compressed (flag 1) as MultiCollection.uop's own entries are.
    Refuses a name that is already in the file: this writer never replaces."""
    table = uop_table(path)
    for name, _ in items:
        if uop_hash(name) in table:
            raise ValueError(f"{name} is already in {path.name}; the writer only adds")
    with path.open("r+b") as f:
        magic, version, stamp, first_block, block_size, count = struct.unpack("<IIIqIi", f.read(28))
        if magic != UOP_MAGIC:
            raise ValueError(f"{path} is not a UOP file")
        # the last block in the chain
        last = first_block
        while True:
            f.seek(last)
            _files, nxt = struct.unpack("<iq", f.read(12))
            if nxt == 0:
                break
            last = nxt
        f.seek(0, 2)
        entries = []
        for name, data in items:
            at = f.tell()
            stored = zlib.compress(data) if compress else data
            f.write(stored)
            entries.append(struct.pack("<qiiiQIh", at, 0, len(stored), len(data), uop_hash(name), 0,
                                       1 if compress else 0))
        block_at = f.tell()
        f.write(struct.pack("<iq", len(items), 0) + b"".join(entries))
        f.seek(last + 4)
        f.write(struct.pack("<q", block_at))
        f.seek(24)
        f.write(struct.pack("<i", count + len(items)))
    _UOP_CACHE.clear()


def uop_read(path: Path, name: str) -> bytes | None:
    entry = uop_table(path).get(uop_hash(name))
    if entry is None:
        return None
    with path.open("rb") as f:
        f.seek(entry[0])
        return f.read(entry[1])


# --- MUL + IDX -------------------------------------------------------------------

def idx_entry(idx: Path, index: int) -> tuple[int, int, int]:
    with idx.open("rb") as f:
        f.seek(index * 12)
        raw = f.read(12)
    return struct.unpack("<iii", raw) if len(raw) == 12 else (-1, -1, 0)


def mul_occupied(idx: Path, index: int) -> bool:
    """Whether an index entry already points at data."""
    return idx_entry(idx, index)[1] > 0


def mul_append(mul: Path, idx: Path, index: int, data: bytes, extra: int = 0, replace: bool = False) -> None:
    size = idx.stat().st_size // 12
    if index >= size:
        raise ValueError(f"index {index} is beyond {idx.name} ({size} entries)")
    if not replace and mul_occupied(idx, index):
        raise ValueError(f"{idx.name} entry {index} already holds data; the writer only adds (replace to overwrite)")
    with mul.open("r+b") as m:
        m.seek(0, 2)
        at = m.tell()
        m.write(data)
    with idx.open("r+b") as x:
        x.seek(index * 12)
        x.write(struct.pack("<iii", at, len(data), extra))


# --- tiledata --------------------------------------------------------------------

def tile_layout(tiledata: Path) -> str:
    """"new" (7.0.9.0+: 8-byte flags, 41-byte item records), "old" (4-byte flags,
    37-byte records) or "unknown", from the file's size, which only one layout fits
    for a whole number of groups (ClassicUO picks by client version instead)."""
    size = tiledata.stat().st_size
    new = size >= TILE_LAND_BYTES and (size - TILE_LAND_BYTES) % TILE_STATIC_BLOCK == 0
    old = size >= TILE_OLD_LAND_BYTES and (size - TILE_OLD_LAND_BYTES) % TILE_OLD_STATIC_BLOCK == 0
    return "new" if new and not old else "old" if old and not new else "unknown"


def check_tiledata(tiledata: Path) -> None:
    """Refuses a tiledata.mul that is not the 7.0.9.0+ layout, the only one written."""
    layout = tile_layout(tiledata)
    if layout != "new":
        what = "the pre-7.0.9.0 layout" if layout == "old" else "neither known layout"
        raise ValueError(f"{tiledata.name} ({tiledata.stat().st_size} bytes) is {what}; "
                         "only the 7.0.9.0+ tiledata is read or written")


def tile_offset(item: int) -> int:
    return TILE_LAND_BYTES + (item // 32) * TILE_STATIC_BLOCK + 4 + (item % 32) * TILE_STATIC_RECORD


def read_tile(tiledata: Path, item: int) -> dict:
    check_tiledata(tiledata)
    with tiledata.open("rb") as f:
        f.seek(tile_offset(item))
        raw = f.read(TILE_STATIC_RECORD)
    flags, weight, layer, count, anim, hue, light, height = struct.unpack("<QBBiHHHB", raw[:21])
    return {"flags": flags, "weight": weight, "layer": layer, "count": count, "anim": anim, "hue": hue,
            "light": light, "height": height, "name": raw[21:41].split(b"\0")[0].decode("latin-1")}


def write_tile(tiledata: Path, item: int, fields: dict) -> None:
    t = {**read_tile(tiledata, item), **fields}
    raw = struct.pack("<QBBiHHHB", t["flags"], t["weight"], t["layer"], t["count"], t["anim"], t["hue"], t["light"],
                      t["height"]) + t["name"].encode("latin-1")[:20].ljust(20, b"\0")
    with tiledata.open("r+b") as f:
        f.seek(tile_offset(item))
        f.write(raw)


def static_count(tiledata: Path) -> int:
    check_tiledata(tiledata)
    return (tiledata.stat().st_size - TILE_LAND_BYTES) // TILE_STATIC_BLOCK * 32


# --- free slots --------------------------------------------------------------------

def free_statics(stage: Stage) -> list[int]:
    """Item ids with no art (UOP or MUL) and an empty tiledata record."""
    tiledata = stage.read_path("tiledata.mul")
    try:
        art = uop_table(stage.read_path("artLegacyMUL.uop"))
        has_art = lambda i: uop_hash(ART_PATTERN.format(LAND_COUNT + i)) in art  # noqa: E731
    except FileNotFoundError:
        idx = stage.read_path("artidx.mul")
        has_art = lambda i: idx_entry(idx, LAND_COUNT + i)[1] > 0  # noqa: E731
    out = []
    raw = tiledata.read_bytes()
    for i in range(static_count(tiledata)):
        o = tile_offset(i)
        rec = raw[o:o + TILE_STATIC_RECORD]
        if rec.strip(b"\0") == b"" and not has_art(i):
            out.append(i)
    return out


def free_gumps(stage: Stage, limit: int = 0x10000) -> set[int]:
    try:
        table = uop_table(stage.read_path("gumpartLegacyMUL.uop"))
        return {i for i in range(limit) if uop_hash(GUMP_PATTERN.format(i)) not in table}
    except FileNotFoundError:
        idx = stage.read_path("gumpidx.mul")
        return {i for i in range(min(limit, idx.stat().st_size // 12)) if idx_entry(idx, i)[1] <= 0}


def free_multis(stage: Stage) -> list[int]:
    """Multi ids below MULTI_LIMIT with no entry (UOP, else multi.idx)."""
    try:
        table = uop_table(stage.read_path("MultiCollection.uop"))
        return [i for i in range(MULTI_LIMIT) if uop_hash(MULTI_PATTERN.format(i)) not in table]
    except FileNotFoundError:
        idx = stage.read_path("multi.idx")
        n = idx.stat().st_size // 12
        return [i for i in range(MULTI_LIMIT) if i >= n or idx_entry(idx, i)[1] <= 0]


def multi_to_mul(data: bytes) -> bytes:
    """A multi record in the UOP layout (uint32 id, int32 count, per component item, x, y, z,
    uint16 flags, uint32 n, n uint32) as multi.mul stores it (16 bytes a component,
    flags: 0 hidden, 1 shown)."""
    _id, count = struct.unpack_from("<Ii", data, 0)
    out, p = bytearray(), 8
    for _ in range(count):
        item, x, y, z, flags, n = struct.unpack_from("<HhhhHI", data, p)
        p += 14 + 4 * n
        out += struct.pack("<HhhhII", item, x, y, z, 0 if flags == 1 else 1, 0)
    return bytes(out)


def definition_bodies(stage: Stage) -> set[int]:
    """Bodies any .def file or mobtypes.txt names: not free, whatever the index says."""
    import re
    used = set()
    for name in ("Body.def", "Bodyconv.def", "mobtypes.txt", "Anim1.def", "Anim2.def", "Equipconv.def"):
        try:
            text = stage.read_path(name).read_text(encoding="latin-1", errors="replace")
        except FileNotFoundError:
            continue
        for line in text.splitlines():
            line = line.split("#")[0]
            used.update(int(n) for n in re.findall(r"\b\d+\b", line))
    return used


def free_people_bodies(stage: Stage) -> list[int]:
    """People-group bodies (400+) whose 175 anim.mul entries are all empty, with no
    AnimationFrame*.uop entry and not named by any definition file."""
    idx = stage.read_path("anim.idx")
    raw = idx.read_bytes()
    total = len(raw) // 12
    uops = []
    for n in range(1, 7):
        try:
            uops.append(uop_table(stage.read_path(f"AnimationFrame{n}.uop")))
        except FileNotFoundError:
            pass
    named = definition_bodies(stage)
    out = []
    body = PEOPLE_FIRST
    while True:
        first, per = anim_group(body)
        if first + per > total:
            break
        empty = all(struct.unpack_from("<ii", raw, (first + k) * 12)[1] <= 0 or
                    struct.unpack_from("<i", raw, (first + k) * 12)[0] < 0 for k in range(per))
        in_uop = any(uop_hash(ANIM_UOP_PATTERN.format(body, a)) in t for t in uops for a in range(35))
        if empty and not in_uop and body not in named:
            out.append(body)
        body += 1
    return out


# --- the registry ---------------------------------------------------------------------

POLICY = Path(__file__).resolve().parent / "ranges.json"


def load_policy(override: Path | str | None = None) -> dict:
    """The range policy (ADR-0022): tools/uodata_write/ranges.json, then a maintainer's file
    (--ranges, or UO_DATA_RANGES) merged over it. "never" lists add up; "packs" entries replace."""
    policy = json.loads(POLICY.read_text(encoding="utf-8"))
    override = override or os.environ.get("UO_DATA_RANGES")
    if override:
        extra = json.loads(Path(override).read_text(encoding="utf-8"))
        for ns, ranges in extra.get("never", {}).items():
            policy["never"].setdefault(ns, []).extend(ranges)
        for pack, ranges in extra.get("packs", {}).items():
            policy["packs"].setdefault(pack, {}).update(ranges)
    return policy


class Registry:
    """slots.json: pack -> reserved ranges per namespace -> ids used. Keeps a pack together."""

    def __init__(self, stage: Stage, policy: dict | None = None):
        self.stage = stage
        self.path = stage.root / "slots.json"
        self.data = json.loads(self.path.read_text(encoding="utf-8")) if self.path.exists() else {"format": 1, "packs": {}}
        self.policy = policy if policy is not None else load_policy()

    def never(self, namespace: str) -> set[int]:
        out = set()
        for lo, hi in self.policy.get("never", {}).get(namespace, []):
            out.update(range(lo, hi + 1))
        return out

    def save(self) -> None:
        self.stage.inside(self.path)
        self.path.write_text(json.dumps(self.data, indent=2) + "\n", encoding="utf-8")

    def reserved(self, namespace: str) -> set[int]:
        out = set()
        for pack in self.data["packs"].values():
            for lo, hi in pack["ranges"].get(namespace, []):
                out.update(range(lo, hi + 1))
        return out

    def reserve(self, pack: str, namespace: str, free: list[int], size: int, prefer_high: bool = True) -> tuple[int, int]:
        """The pack's range from the policy if it names one (every id must be free), else the
        first contiguous run of `size` free ids that no pack holds and the policy does not bar."""
        taken = self.reserved(namespace) | self.never(namespace)
        fixed = self.policy.get("packs", {}).get(pack, {}).get(namespace)
        if fixed:
            lo, hi = fixed
            bad = [i for i in range(lo, hi + 1) if i in taken or i not in set(free)]
            if bad:
                raise ValueError(f"pack {pack}'s {namespace} range {lo:#x}-{hi:#x} is not free: {[hex(i) for i in bad[:8]]}")
            return self._hold(pack, namespace, lo, hi)
        ids = sorted(set(free) - taken, reverse=prefer_high)
        pool = set(ids)
        for start in ids:
            lo = start - size + 1 if prefer_high else start
            if all(i in pool for i in range(lo, lo + size)):
                return self._hold(pack, namespace, lo, lo + size - 1)
        raise ValueError(f"no free run of {size} in {namespace}")

    def _hold(self, pack: str, namespace: str, lo: int, hi: int) -> tuple[int, int]:
        p = self.data["packs"].setdefault(pack, {"ranges": {}, "used": {}})
        p["ranges"].setdefault(namespace, []).append([lo, hi])
        self.save()
        return lo, hi

    def note(self, namespace: str, id: int, what: str) -> str | None:
        """Marks an id used in the pack whose range holds it; returns that pack, or None."""
        for name, p in self.data["packs"].items():
            if any(lo <= id <= hi for lo, hi in p["ranges"].get(namespace, [])):
                used = p["used"].setdefault(namespace, {})
                if id not in used.values():
                    used[what] = id
                    self.save()
                return name
        return None

    def take(self, pack: str, namespace: str, what: str) -> int:
        p = self.data["packs"][pack]
        used = p["used"].setdefault(namespace, {})
        if what in used:
            return used[what]
        for lo, hi in p["ranges"].get(namespace, []):
            for i in range(lo, hi + 1):
                if i not in used.values():
                    used[what] = i
                    self.save()
                    return i
        raise ValueError(f"pack {pack}'s {namespace} range is full")


# --- writers ---------------------------------------------------------------------------

def _exists(stage: Stage, name: str) -> bool:
    try:
        stage.read_path(name)
        return True
    except FileNotFoundError:
        return False


def check_records(stage: Stage, records: list[AssetRecord], replace: bool = False) -> None:
    """Refuses, before anything is written, a record the writers would refuse or
    get wrong: an existing UOP name, an occupied MUL slot (unless replace), the old
    tiledata layout, a hue record that is not one 88-byte hue inside hues.mul."""
    def slot(uop: str, pattern: str, mul_idx: str, index: int) -> None:
        if _exists(stage, uop):
            if uop_has(stage.read_path(uop), pattern.format(index)):
                raise ValueError(f"{pattern.format(index)} is already in {uop}; the writer only adds")
        elif not replace and mul_occupied(stage.read_path(mul_idx), index):
            raise ValueError(f"{mul_idx} entry {index} already holds data; the writer only adds (replace to overwrite)")

    for r in records:
        if r.kind in ("static", "land"):
            slot("artLegacyMUL.uop", ART_PATTERN, "artidx.mul", r.id if r.kind == "land" else LAND_COUNT + r.id)
        elif r.kind == "gump":
            slot("gumpartLegacyMUL.uop", GUMP_PATTERN, "gumpidx.mul", r.id)
        elif r.kind == "anim":
            index = anim_index(r.id, r.meta["action"], r.meta["direction"])
            if not replace and mul_occupied(stage.read_path("anim.idx"), index):
                raise ValueError(f"anim.idx entry {index} (body {r.id}, action {r.meta['action']}, direction "
                                 f"{r.meta['direction']}) already holds data; the writer only adds (replace to overwrite)")
        elif r.kind == "multi" and 0 <= r.id < MULTI_LIMIT:
            slot("MultiCollection.uop", MULTI_PATTERN, "multi.idx", r.id)
        elif r.kind == "tiledata-item":
            check_tiledata(stage.read_path("tiledata.mul"))
        elif r.kind == "hue":
            size = stage.read_path("hues.mul").stat().st_size
            h = r.id - 1
            at = (h // 8) * HUE_GROUP + 4 + (h % 8) * HUE_RECORD if h >= 0 else -1
            if len(r.data) != HUE_RECORD or r.id < 1 or at + HUE_RECORD > size:
                raise ValueError(f"hue {r.id} ({len(r.data)} bytes) is not one {HUE_RECORD}-byte hue inside hues.mul")


def output_files(stage: Stage, records: list[AssetRecord]) -> list[str]:
    """The install file names write_records writes for these records (staged copies)."""
    def either(uop: str, *mul: str) -> list[str]:
        return [uop] if _exists(stage, uop) else list(mul)

    kinds = {r.kind for r in records}
    names = []
    if kinds & {"static", "land"}:
        names += either("artLegacyMUL.uop", "art.mul", "artidx.mul")
    if "gump" in kinds:
        names += either("gumpartLegacyMUL.uop", "gumpart.mul", "gumpidx.mul")
    if "anim" in kinds:
        names += ["anim.mul", "anim.idx"]
    if "multi" in kinds:
        names += either("MultiCollection.uop", "multi.mul", "multi.idx")
    if "tiledata-item" in kinds:
        names.append("tiledata.mul")
    if "hue" in kinds:
        names.append("hues.mul")
    return names


def check_outputs(stage: Stage, records: list[AssetRecord]) -> None:
    """Refuses, before anything is written, a call any of whose output paths (the staged
    copies and the stage's own files) resolves outside the stage root."""
    for name in output_files(stage, records):
        stage.target(name)
    for path in stage.own_files():
        stage.inside(path)


def write_records(stage: Stage, records: list[AssetRecord], replace: bool = False) -> list[str]:
    """Writes the records into the staged set; returns one line per write. An
    output path outside the stage root (check_outputs) and an occupied MUL + IDX slot
    unless replace (check_records) are refused before any write."""
    check_outputs(stage, records)
    check_records(stage, records, replace)
    done = []
    art_items, gump_items = [], []
    for r in records:
        if r.kind in ("static", "land"):
            index = r.id if r.kind == "land" else LAND_COUNT + r.id
            art_items.append((index, r.data))
        elif r.kind == "gump":
            gump_items.append((r.id, r.data))
    if art_items:
        try:
            uop = stage.path("artLegacyMUL.uop")
            uop_append(uop, [(ART_PATTERN.format(i), d) for i, d in art_items])
        except FileNotFoundError:
            mul, idx = stage.path("art.mul"), stage.path("artidx.mul")
            for i, d in art_items:
                mul_append(mul, idx, i, d, replace=replace)
        done += [f"art {i:#x} ({len(d)} bytes)" for i, d in art_items]
    if gump_items:
        try:
            uop = stage.path("gumpartLegacyMUL.uop")
            uop_append(uop, [(GUMP_PATTERN.format(i), d) for i, d in gump_items])
        except FileNotFoundError:
            mul, idx = stage.path("gumpart.mul"), stage.path("gumpidx.mul")
            for i, d in gump_items:
                w, h = struct.unpack_from("<II", d, 0)
                mul_append(mul, idx, i, d[8:], (w << 16) | h, replace=replace)
        done += [f"gump {i} ({len(d)} bytes)" for i, d in gump_items]
    anims = [r for r in records if r.kind == "anim"]
    if anims:
        mul, idx = stage.path("anim.mul"), stage.path("anim.idx")
        for r in anims:
            mul_append(mul, idx, anim_index(r.id, r.meta["action"], r.meta["direction"]), r.data, replace=replace)
        done.append(f"anim: {len(anims)} body/action/direction payload(s) for bodies {sorted({r.id for r in anims})}")
    multis = [r for r in records if r.kind == "multi"]
    if multis:
        for r in multis:
            if not 0 <= r.id < MULTI_LIMIT:
                raise ValueError(f"multi id {r.id:#x} is beyond {MULTI_LIMIT - 1:#x}, which the shard cannot address")
        try:
            uop = stage.path("MultiCollection.uop")
            # Compressed, as the client's own are: ModernUO's MultiData.LoadUOP only reads
            # an entry from the file when it is compressed (an uncompressed one is parsed
            # from whatever its buffer last held).
            uop_append(uop, [(MULTI_PATTERN.format(r.id), r.data) for r in multis], compress=True)
        except FileNotFoundError:
            mul, idx = stage.path("multi.mul"), stage.path("multi.idx")
            for r in multis:
                n = idx.stat().st_size // 12
                if r.id >= n:
                    with idx.open("ab") as f:
                        f.write(struct.pack("<iii", -1, -1, 0) * (r.id + 1 - n))
                mul_append(mul, idx, r.id, multi_to_mul(r.data), replace=replace)
        done += [f"multi {r.id:#x} ({struct.unpack_from('<i', r.data, 4)[0]} components)" for r in multis]
    for r in records:
        if r.kind == "tiledata-item":
            write_tile(stage.path("tiledata.mul"), r.id, {k: v for k, v in r.meta.items() if k in TILE_FIELDS})
            done.append(f"tiledata item {r.id:#x}: {r.meta}")
        elif r.kind == "hue":
            h = r.id - 1
            path = stage.path("hues.mul")
            with path.open("r+b") as f:
                f.seek((h // 8) * HUE_GROUP + 4 + (h % 8) * HUE_RECORD)
                f.write(r.data)
            done.append(f"hue {r.id}")
    stage.save()
    # An id written inside a pack's range is marked used there, whoever wrote it
    # (uopack's pack path picks ids itself rather than through take()).
    reg = Registry(stage)
    for r in records:
        ns = {"static": "static", "tiledata-item": "static", "anim": "anim", "gump": "gump", "multi": "multi"}.get(r.kind)
        if ns:
            reg.note(ns, r.id, f"{r.kind}-{r.id:#x}")
    return done


def read_back(stage: Stage, r: AssetRecord) -> bytes | dict | None:
    """What the staged set now holds for a record (for verify)."""
    if r.kind in ("static", "land"):
        index = r.id if r.kind == "land" else LAND_COUNT + r.id
        try:
            return uop_read(stage.read_path("artLegacyMUL.uop"), ART_PATTERN.format(index))
        except FileNotFoundError:
            o, n, _ = idx_entry(stage.read_path("artidx.mul"), index)
            with stage.read_path("art.mul").open("rb") as f:
                f.seek(o)
                return f.read(n)
    if r.kind == "gump":
        return uop_read(stage.read_path("gumpartLegacyMUL.uop"), GUMP_PATTERN.format(r.id))
    if r.kind == "anim":
        o, n, _ = idx_entry(stage.read_path("anim.idx"), anim_index(r.id, r.meta["action"], r.meta["direction"]))
        with stage.read_path("anim.mul").open("rb") as f:
            f.seek(o)
            return f.read(n)
    if r.kind == "tiledata-item":
        return read_tile(stage.read_path("tiledata.mul"), r.id)
    if r.kind == "multi":
        try:
            raw = uop_read(stage.read_path("MultiCollection.uop"), MULTI_PATTERN.format(r.id))
            return zlib.decompress(raw) if raw else raw
        except FileNotFoundError:
            o, n, _ = idx_entry(stage.read_path("multi.idx"), r.id)
            if n <= 0:
                return None
            with stage.read_path("multi.mul").open("rb") as f:
                f.seek(o)
                return f.read(n)
    return None


def read_back_equal(stage: Stage, r: AssetRecord) -> bool:
    """Whether the stage holds what the record says (multis compare in the layout they were stored in)."""
    got = read_back(stage, r)
    if r.kind == "multi" and got is not None and got != r.data:
        return got == multi_to_mul(r.data)
    return got == r.data
