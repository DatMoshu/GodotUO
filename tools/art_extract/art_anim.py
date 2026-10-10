"""Animation frames of the install as AnimationsLoader reads them (data_formats section 36, class `anim`).

The set is keyed by the *block* the loader reads, not by body: the client resolves a body (Body.def, Bodyconv.def,
Corpse.def, mobtypes, AnimationSequence.uop) to a file and a position first, and only then calls
ReadMULAnimationFrames or ReadUOPAnimationFrames. Keying below that resolution means body conversion needs no copy
of its logic here and cannot disagree with the loader.

    m<file>.<position>.<size>            a MUL direction block (anim.mul = file 0, anim2.mul = 1, ...)
    u<file>.<position>.<direction>       a UOP group (AnimationFrame1.uop = file 0, ...), one direction of it
    u<file>.<position>.<direction>.e     the same read for an Equipment animation, only where it differs

The decoders follow AnimationsLoader.ReadMULAnimationFrames, ReadUOPAnimationFrames and ReadSpriteData line by line
(tools/guo/uoread.py supplies the UOP container, the BWT codec and the MUL index). The client's C# probe
(ArtSetParityProbe) checks them against the real loader, which is the parity bar; `verify` checks the pages against
this decode.
"""

from __future__ import annotations

import hashlib
import struct
import sys
import zlib
from dataclasses import dataclass
from pathlib import Path

import numpy as np

TOOLS = Path(__file__).resolve().parent.parent
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

from guo import uoread  # noqa: E402
import art_sources as srcs  # noqa: E402

CLASS = "anim"
MAX_DIRECTIONS = 5
MAX_FILES = 10                 # AnimationsLoader._files / _filesUop
END = 0x7FFF7FFF
PAGE = 2048


class BlockSkip(Exception):
    """The block exists but cannot be stored; the message is the reason written to `skipped`."""


@dataclass
class Frame:
    num: int
    cx: int
    cy: int
    w: int = 0
    h: int = 0
    rgba: bytes = b""

    @property
    def drawn(self) -> bool:
        return self.w > 0 and self.h > 0


def frame_digest(f: Frame) -> bytes:
    """What goes into a block digest for one frame: its size and pixels (an empty frame is 0 x 0)."""
    return struct.pack("<II", f.w, f.h) + f.rgba if f.drawn else struct.pack("<II", 0, 0)


def block_digest(frames: list[Frame]) -> str:
    h = hashlib.sha256()
    for f in frames:
        h.update(frame_digest(f))
    return h.hexdigest()


# --- keys -------------------------------------------------------------------------------------

def mul_key(file: int, position: int, size: int) -> str:
    return f"m{file}.{position}.{size}"


def uop_key(file: int, position: int, direction: int, equipment: bool = False) -> str:
    return f"u{file}.{position}.{direction}" + (".e" if equipment else "")


# --- ReadSpriteData ---------------------------------------------------------------------------

def _palette32(raw: bytes | memoryview, keyed: bool) -> np.ndarray:
    table = srcs.KEYED if keyed else srcs.DRAWN
    return np.frombuffer(b"".join(table[v] for v in struct.unpack_from("<256H", raw, 0)), dtype="<u4")


def read_sprite(raw: bytes, p: int, palette: np.ndarray, num: int) -> Frame:
    """AnimationsLoader.ReadSpriteData at offset p of raw. The palette is already RGBA (keyed or not by the caller)."""
    try:
        cx, cy, w, h = struct.unpack_from("<hhhh", raw, p)
    except struct.error as e:
        raise BlockSkip("frame header runs past the block") from e
    p += 8
    f = Frame(num, cx, cy)
    if w <= 0 or h <= 0:
        return f
    size = w * h
    out = np.zeros(size, dtype="<u4")
    n = len(raw)
    unpack = struct.unpack_from
    try:
        header = unpack("<I", raw, p)[0]
        p += 4
        while header != END and p < n:
            run = header & 0x0FFF
            x = (header >> 22) & 0x3FF
            if x & 0x200:
                x -= 0x400
            y = (header >> 12) & 0x3FF
            if y & 0x200:
                y -= 0x400
            x += cx
            y += cy + h
            at = y * w + x
            if at < 0 or at + run > size or p + run > n:
                # the C# would write into the unused tail of its shared buffer or throw: leave it to the loader
                raise BlockSkip("a run lies outside its frame")
            if run:
                out[at:at + run] = palette[np.frombuffer(raw, dtype=np.uint8, count=run, offset=p)]
            p += run
            header = unpack("<I", raw, p)[0]
            p += 4
    except struct.error as e:
        raise BlockSkip("frame data runs past the block") from e
    if w > PAGE or h > PAGE:
        raise BlockSkip(f"frame {w}x{h} does not fit a page")
    f.w, f.h, f.rgba = w, h, out.tobytes()
    return f


# --- ReadMULAnimationFrames -------------------------------------------------------------------

def decode_mul(raw: bytes) -> list[Frame]:
    if len(raw) < 516:
        raise BlockSkip("block shorter than its palette")
    palette = _palette32(raw, keyed=False)
    count = struct.unpack_from("<I", raw, 512)[0]
    if 516 + 4 * count > len(raw):
        raise BlockSkip("frame offset table runs past the block")
    offsets = struct.unpack_from(f"<{count}I", raw, 516)
    return [read_sprite(raw, 512 + off, palette, i) for i, off in enumerate(offsets)]


# --- ReadUOPAnimationFrames -------------------------------------------------------------------

def uop_frame_table(data: bytes) -> list[tuple[int, int, int]]:
    """(position, frame id, pixel offset) per frame, with the gaps filled the way the loader fills them."""
    if len(data) < 40:
        raise BlockSkip("group shorter than its header")
    fc = struct.unpack_from("<i", data, 32)[0]
    data_start = struct.unpack_from("<I", data, 36)[0]
    if fc < 0 or data_start + 16 * fc > len(data):
        raise BlockSkip("frame table runs past the group")
    rows = []
    for i in range(fc):
        start = data_start + 16 * i
        _group, frame_id, _skip, pixel_offset = struct.unpack_from("<HHQI", data, start)
        rows.append((start, frame_id, pixel_offset))
    out: list[tuple[int, int, int]] = []
    last = 1
    for start, frame_id, pixel_offset in rows:
        while frame_id - last > 1:
            last += 1
            out.append((0, last, 0))      # Position 0: a missing frame
        out.append((start, frame_id, pixel_offset))
        last = frame_id
    return out


def real_frame_count(table_len: int, equipment: bool) -> int:
    n = int(round(table_len / 5.0))       # Math.Round: to even; a .5 cannot occur for an integer over 5
    return max(10, n) if equipment else n


def decode_uop_direction(data: bytes, table: list, direction: int, equipment: bool) -> list[Frame]:
    real = real_frame_count(len(table), equipment)
    if real <= 0:
        raise BlockSkip("group holds too few frames to divide into directions")
    frames = [Frame(0, 0, 0) for _ in range(real)]      # uncovered frames keep Num 0, as the cleared C# array does
    for position, frame_id, pixel_offset in table:
        frame_direction = (frame_id - 1) // real
        if frame_direction < direction:
            continue
        if frame_direction > direction:
            break
        idx = (frame_id - 1) % real
        if position == 0:
            frames[idx] = Frame(idx, 0, 0)
            continue
        at = position + pixel_offset
        if at + 512 > len(data):
            raise BlockSkip("frame palette runs past the group")
        palette = _palette32(data[at:at + 512], keyed=True)
        frames[idx] = read_sprite(data, at + 512, palette, idx)
    return frames


# --- the source -------------------------------------------------------------------------------

def _mul_name(i: int) -> str:
    return "anim" + ("" if i == 0 else str(i + 1))


class AnimSource:
    """Every animation block the install's anim*.mul/idx and AnimationFrame*.uop files hold."""

    cls = CLASS

    def __init__(self, data: Path):
        data = Path(data)
        self.mul: dict[int, tuple[Path, list]] = {}
        self.uop: dict[int, uoread.UopFile] = {}
        self.files: list[str] = []
        for i in range(MAX_FILES):
            mul, idx = uoread._find(data, _mul_name(i) + ".mul"), uoread._find(data, _mul_name(i) + ".idx")
            if mul and idx:
                self.mul[i] = (mul, uoread.MulIndex(mul, idx).records)
                self.files += [mul.name, idx.name]
            uop = uoread._find(data, f"AnimationFrame{i + 1}.uop")
            if uop:
                self.uop[i] = uoread.UopFile(uop)
                self.files.append(uop.name)
        if not self.files:
            raise FileNotFoundError(f"no animation files in {data}")

    def blocks(self):
        """Yield (file, kind, position, size, loader) in a fixed order: MUL files by index record, then UOP files by offset."""
        for i in sorted(self.mul):
            path, records = self.mul[i]
            length = path.stat().st_size
            seen = set()
            with open(path, "rb") as f:
                for pos, size, _extra in records:
                    if pos < 0 or size <= 0 or pos + size > length or (pos, size) in seen:
                        continue
                    seen.add((pos, size))
                    f.seek(pos)
                    yield i, "m", pos, size, f.read(size)
        for i in sorted(self.uop):
            uop = self.uop[i]
            with open(uop.path, "rb") as f:
                for e in sorted(uop.entries.values(), key=lambda e: e.offset):
                    f.seek(e.offset)
                    raw = f.read(e.compressed)
                    yield i, "u", e.offset, e.compressed, (raw, e.flag, e.decompressed)

    def decode(self, file: int, kind: str, position: int, size: int, payload) -> dict[str, list[Frame]]:
        """The frames for every key this block answers, in key order. Raises BlockSkip."""
        if kind == "m":
            return {mul_key(file, position, size): decode_mul(payload)}
        raw, flag, decompressed = payload
        try:
            if flag == 0:
                data = raw
            else:
                data = zlib.decompress(raw)
                if flag == 3:
                    data = uoread.bwt_decompress(data)
        except (zlib.error, ValueError, IndexError) as e:
            raise BlockSkip(f"group does not decompress ({type(e).__name__})") from e
        table = uop_frame_table(data)
        out: dict[str, list[Frame]] = {}
        for d in range(MAX_DIRECTIONS):
            plain = decode_uop_direction(data, table, d, False)
            out[uop_key(file, position, d)] = plain
            if real_frame_count(len(table), True) != real_frame_count(len(table), False):
                out[uop_key(file, position, d, True)] = decode_uop_direction(data, table, d, True)
        return out


# --- packing ----------------------------------------------------------------------------------

class SequencePacker:
    """Next-fit shelves in the order frames arrive, so a block's frames sit together and one page serves a walk cycle.

    A row starts at the left edge of the page; a frame that does not fit the row's remaining width starts the next row;
    a row is as tall as its tallest frame; a row that does not fit the page's remaining height starts a new page.
    Frames with byte-identical pixels share the first one's rectangle. Full pages go to `sink(index, rgba)` at once.
    """

    def __init__(self, sink):
        self.sink = sink
        self.page = -1
        self.buf: np.ndarray | None = None
        self.row_y = self.row_h = self.cursor = 0
        self.owner: dict[tuple[int, int, bytes], tuple[int, int, int]] = {}
        self.pages = 0

    def _flush(self) -> None:
        if self.buf is not None:
            self.sink(self.page, self.buf.tobytes())
            self.pages += 1
            self.buf = None

    def _new_page(self) -> None:
        self._flush()
        self.page += 1
        self.buf = np.zeros(PAGE * PAGE * 4, dtype=np.uint8)
        self.row_y = self.row_h = self.cursor = 0

    def place(self, f: Frame) -> tuple[int, int, int]:
        digest = hashlib.sha256(f.rgba).digest()
        twin = self.owner.get((f.w, f.h, digest))
        if twin is not None:
            return twin
        if self.buf is None:
            self._new_page()
        if self.cursor + f.w > PAGE:
            self.row_y += self.row_h
            self.row_h = self.cursor = 0
        if self.row_y + max(self.row_h, f.h) > PAGE:
            self._new_page()
        self.row_h = max(self.row_h, f.h)
        pixels = np.frombuffer(f.rgba, dtype=np.uint8).reshape(f.h, f.w * 4)
        for row in range(f.h):
            at = ((self.row_y + row) * PAGE + self.cursor) * 4
            self.buf[at:at + f.w * 4] = pixels[row]
        where = (self.page, self.cursor, self.row_y)
        self.owner[(f.w, f.h, digest)] = where
        self.cursor += f.w
        return where

    def finish(self) -> None:
        self._flush()


# --- export and verify ------------------------------------------------------------------------

def _base_key(file: int, kind: str, position: int, size: int) -> str:
    return mul_key(file, position, size) if kind == "m" else f"u{file}.{position}"


def export_anim(src: AnimSource, folder: Path, set_id: str, write_page, dump_index, log=print) -> dict:
    """Decode every block and write pages as they fill. write_page(folder, n, rgba) -> {file, sha256, bytes}."""
    pages: list[dict] = []

    def sink(n: int, rgba: bytes) -> None:
        pages.append(write_page(folder, n, rgba))

    packer = SequencePacker(sink)
    blocks: dict[str, dict] = {}
    skipped: list[dict] = []
    frames_stored = 0
    for file, kind, position, size, payload in src.blocks():
        try:
            decoded = src.decode(file, kind, position, size, payload)
        except BlockSkip as why:
            skipped.append({"key": _base_key(file, kind, position, size), "reason": str(why)[:200]})
            continue
        for key, frames in decoded.items():
            if not frames:
                continue
            rows = []
            for f in frames:
                if f.drawn:
                    page, x, y = packer.place(f)
                    rows.append([page, x, y, f.w, f.h, f.cx, f.cy])
                    frames_stored += 1
                else:
                    rows.append([f.num, f.cx, f.cy])
            blocks[key] = {"f": rows, "sha": block_digest(frames)}
    packer.finish()
    index = {"schema": "guo/art_anim_index@1", "version": 1, "class": CLASS, "set_id": set_id, "page_size": PAGE,
             "pixel_format": "rgba8", "pages": [{"file": p["file"], "sha256": p["sha256"]} for p in pages],
             "blocks": blocks, "skipped": skipped}
    dump_index(folder / "index.json", index)
    log(f"anim    {frames_stored} drawn frames written")
    return {"pages": len(pages), "count": len(blocks), "skipped": len(skipped),
            "bytes": sum(p["bytes"] for p in pages)}


class _Pages:
    """A few decoded pages of one class, least recently used out first."""

    def __init__(self, folder: Path, listed: list[dict], png_decode, keep: int = 6):
        self.folder, self.listed, self.png_decode, self.keep = folder, listed, png_decode, keep
        self.cache: dict[int, np.ndarray] = {}

    def get(self, n: int) -> np.ndarray:
        if n in self.cache:
            self.cache[n] = self.cache.pop(n)
            return self.cache[n]
        w, h, pixels = self.png_decode((self.folder / self.listed[n]["file"]).read_bytes())
        if (w, h) != (PAGE, PAGE):
            raise ValueError(f"page {n} is {w}x{h}")
        page = np.frombuffer(pixels, dtype=np.uint8).reshape(PAGE, PAGE * 4)
        self.cache[n] = page
        while len(self.cache) > self.keep:
            self.cache.pop(next(iter(self.cache)))
        return page


def verify_anim(src: AnimSource, folder: Path, set_id: str, summary: dict, index_errors, png_decode, sha256_file,
                stat: dict, bad) -> None:
    import json
    try:
        index = json.loads((folder / "index.json").read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        bad(f"anim/index.json unreadable ({e})")
        return
    for e in index_errors(index):
        bad(f"anim/index.json: {e}")
    if index.get("set_id") != set_id:
        bad("anim/index.json: set_id differs from set.json")
    if (index.get("class"), index.get("page_size")) != (CLASS, PAGE):
        bad("anim/index.json: wrong class or page size")
    blocks, skipped = index.get("blocks", {}), {s["key"] for s in index.get("skipped", [])}
    listed = index.get("pages", [])
    if (summary.get("count"), summary.get("skipped"), summary.get("pages")) != (len(blocks), len(skipped), len(listed)):
        bad("anim: set.json summary disagrees with index.json")
    pages_ok = True
    for n, page in enumerate(listed):
        p = folder / page["file"]
        if page["file"] != f"page_{n:04d}.png":
            bad(f"anim: page {n} is named {page['file']}")
        if not p.is_file() or sha256_file(p) != page["sha256"]:
            bad(f"anim/{page['file']} is missing or damaged")
            pages_ok = False
    pages = _Pages(folder, listed, png_decode)
    seen = set()
    for file, kind, position, size, payload in src.blocks():
        try:
            decoded = src.decode(file, kind, position, size, payload)
        except BlockSkip:
            stat["skipped"] += 1
            if _base_key(file, kind, position, size) not in skipped:
                bad(f"anim {_base_key(file, kind, position, size)}: cannot be decoded but is not listed as skipped")
            continue
        for key, frames in decoded.items():
            if not frames:
                stat["absent"] += 1
                if key in blocks:
                    bad(f"anim {key}: stored, but the install has no frames there")
                continue
            stat["stored"] += 1
            seen.add(key)
            entry = blocks.get(key)
            if entry is None:
                bad(f"anim {key}: in the install but missing from the set")
                stat["bad"] += 1
                continue
            if entry["sha"] != block_digest(frames) or len(entry["f"]) != len(frames):
                bad(f"anim {key}: index says {entry['sha'][:12]} ({len(entry['f'])} frames), "
                    f"install decodes to {block_digest(frames)[:12]} ({len(frames)} frames)")
                stat["bad"] += 1
                continue
            if not pages_ok:
                stat["bad"] += 1
                continue
            problem = _check_rows(entry["f"], frames, pages)
            if problem:
                bad(f"anim {key}: {problem}")
                stat["bad"] += 1
            else:
                stat["ok"] += 1
    for extra in sorted(set(blocks) - seen):
        bad(f"anim {extra}: in the set, but the install does not provide it")
        stat["bad"] += 1


def _check_rows(rows: list, frames: list[Frame], pages: _Pages) -> str | None:
    for r, f in zip(rows, frames):
        if f.drawn:
            if len(r) != 7 or r[3:5] != [f.w, f.h] or r[5:7] != [f.cx, f.cy]:
                return f"frame {f.num} size or centre differs"
            page, x, y = r[0], r[1], r[2]
            if not (0 <= page < len(pages.listed)) or x < 0 or y < 0 or x + f.w > PAGE or y + f.h > PAGE:
                return f"frame {f.num} rectangle leaves the page"
            try:
                crop = pages.get(page)[y:y + f.h, x * 4:(x + f.w) * 4].tobytes()
            except (OSError, ValueError) as e:
                return f"page {page}: {e}"
            if crop != f.rgba:
                return f"frame {f.num} page {page} rectangle ({x},{y}) does not hold the pixels"
        elif list(r) != [f.num, f.cx, f.cy]:
            return f"empty frame {f.num} differs"
    return None
