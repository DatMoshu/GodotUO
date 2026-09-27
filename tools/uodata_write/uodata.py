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
  in place       tiledata.mul item records, hues.mul blocks
"""
from __future__ import annotations

import hashlib
import json
import shutil
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.uomap import UOP_MAGIC, uop_entries, uop_hash  # noqa: E402
from guo.uorecord import AssetRecord  # noqa: E402

LAND_COUNT = 0x4000
TILE_LAND_BLOCK = 4 + 32 * 30          # new (7.0.9.0+) land group
TILE_LAND_BYTES = 512 * TILE_LAND_BLOCK
TILE_STATIC_RECORD = 41                # flags 8, weight 1, layer 1, count 4, anim 2, hue 2, light 2, height 1, name 20
TILE_STATIC_BLOCK = 4 + 32 * TILE_STATIC_RECORD
HUE_GROUP = 4 + 8 * 88
MALE_GUMP, FEMALE_GUMP = 50000, 60000
PEOPLE_FIRST = 400

ART_PATTERN = "build/artlegacymul/{0:08d}.tga"
GUMP_PATTERN = "build/gumpartlegacymul/{0:08d}.tga"
ANIM_UOP_PATTERN = "build/animationlegacyframe/{0:06d}/{1:02d}.bin"

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

    def install_file(self, name: str) -> Path | None:
        for p in self.install.iterdir():
            if p.name.lower() == name.lower():
                return p
        return None

    def path(self, name: str) -> Path:
        """The staged copy of an install file, copied on first use (copy on write)."""
        key = name.lower()
        if key in self.meta["files"]:
            return self.root / self.meta["files"][key]["name"]
        src = self.install_file(name)
        if src is None:
            raise FileNotFoundError(f"{name} is not in the install")
        dst = self.root / src.name
        shutil.copyfile(src, dst)
        self.meta["files"][key] = {"name": src.name, "source_sha1": sha1(src), "source_size": src.stat().st_size}
        self.save()
        return dst

    def read_path(self, name: str) -> Path:
        """Where the current version of a file is: the staged copy if there is one, else the install."""
        key = name.lower()
        if key in self.meta["files"]:
            return self.root / self.meta["files"][key]["name"]
        p = self.install_file(name)
        if p is None:
            raise FileNotFoundError(name)
        return p

    def save(self) -> None:
        self.meta_path.write_text(json.dumps(self.meta, indent=2) + "\n", encoding="utf-8")
        lines = ["# GUO staged data set (tools/uodata_write, ADR-0022): UOFilesOverrideMap entries"]
        lines += [f"{k}={self.root / v['name']}" for k, v in sorted(self.meta["files"].items())]
        (self.root / "files_override.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")

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


def uop_append(path: Path, items: list[tuple[str, bytes]]) -> None:
    """Append entries (uncompressed, flag 0) and chain a new block of them onto the table.
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
            f.write(data)
            entries.append(struct.pack("<qiiiQIh", at, 0, len(data), len(data), uop_hash(name), 0, 0))
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


def mul_append(mul: Path, idx: Path, index: int, data: bytes, extra: int = 0) -> None:
    size = idx.stat().st_size // 12
    if index >= size:
        raise ValueError(f"index {index} is beyond {idx.name} ({size} entries)")
    with mul.open("r+b") as m:
        m.seek(0, 2)
        at = m.tell()
        m.write(data)
    with idx.open("r+b") as x:
        x.seek(index * 12)
        x.write(struct.pack("<iii", at, len(data), extra))


# --- tiledata --------------------------------------------------------------------

def tile_offset(item: int) -> int:
    return TILE_LAND_BYTES + (item // 32) * TILE_STATIC_BLOCK + 4 + (item % 32) * TILE_STATIC_RECORD


def read_tile(tiledata: Path, item: int) -> dict:
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

class Registry:
    """slots.json: pack -> reserved ranges per namespace -> ids used. Keeps a pack together."""

    def __init__(self, stage: Stage):
        self.path = stage.root / "slots.json"
        self.data = json.loads(self.path.read_text(encoding="utf-8")) if self.path.exists() else {"format": 1, "packs": {}}

    def save(self) -> None:
        self.path.write_text(json.dumps(self.data, indent=2) + "\n", encoding="utf-8")

    def reserved(self, namespace: str) -> set[int]:
        out = set()
        for pack in self.data["packs"].values():
            for lo, hi in pack["ranges"].get(namespace, []):
                out.update(range(lo, hi + 1))
        return out

    def reserve(self, pack: str, namespace: str, free: list[int], size: int, prefer_high: bool = True) -> tuple[int, int]:
        """The first contiguous run of `size` free ids nobody has reserved."""
        taken = self.reserved(namespace)
        ids = sorted(set(free) - taken, reverse=prefer_high)
        pool = set(ids)
        for start in ids:
            lo = start - size + 1 if prefer_high else start
            if all(i in pool for i in range(lo, lo + size)):
                p = self.data["packs"].setdefault(pack, {"ranges": {}, "used": {}})
                p["ranges"].setdefault(namespace, []).append([lo, lo + size - 1])
                self.save()
                return lo, lo + size - 1
        raise ValueError(f"no free run of {size} in {namespace}")

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

def write_records(stage: Stage, records: list[AssetRecord]) -> list[str]:
    """Writes the records into the staged set; returns one line per write."""
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
                mul_append(mul, idx, i, d)
        done += [f"art {i:#x} ({len(d)} bytes)" for i, d in art_items]
    if gump_items:
        try:
            uop = stage.path("gumpartLegacyMUL.uop")
            uop_append(uop, [(GUMP_PATTERN.format(i), d) for i, d in gump_items])
        except FileNotFoundError:
            mul, idx = stage.path("gumpart.mul"), stage.path("gumpidx.mul")
            for i, d in gump_items:
                w, h = struct.unpack_from("<II", d, 0)
                mul_append(mul, idx, i, d[8:], (w << 16) | h)
        done += [f"gump {i} ({len(d)} bytes)" for i, d in gump_items]
    anims = [r for r in records if r.kind == "anim"]
    if anims:
        mul, idx = stage.path("anim.mul"), stage.path("anim.idx")
        for r in anims:
            mul_append(mul, idx, anim_index(r.id, r.meta["action"], r.meta["direction"]), r.data)
        done.append(f"anim: {len(anims)} body/action/direction payload(s) for bodies {sorted({r.id for r in anims})}")
    for r in records:
        if r.kind == "tiledata-item":
            write_tile(stage.path("tiledata.mul"), r.id, {k: v for k, v in r.meta.items() if k in TILE_FIELDS})
            done.append(f"tiledata item {r.id:#x}: {r.meta}")
        elif r.kind == "hue":
            h = r.id - 1
            path = stage.path("hues.mul")
            with path.open("r+b") as f:
                f.seek((h // 8) * HUE_GROUP + 4 + (h % 8) * 88)
                f.write(r.data)
            done.append(f"hue {r.id}")
    stage.save()
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
    return None
