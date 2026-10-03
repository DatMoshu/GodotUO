"""Legacy multi formats: read and write the interchange files other tools use.

Written from the format descriptions in the research notes (layouts only; no code from any of the tools).
A tile is `Tile(item, x, y, z, visible, hue, level)`: hue and level are carried where a format has them
(UOA binary: both; WSC, CentrED# CSV: hue) and are 0 elsewhere.

  txt       one `0xID x y z flags` per line: UOFiddler's text export, the Ultima SDK text (decimal ids read too)
  uoa       UO Architect's text: 4 header lines, then `id x y z flags` (also the Sphere multi text)
  uoab      UO Architect's binary designs: version 1 (one design) or 2 (a count of them)
  wsc       `SECTION WORLDITEM i { ID X Y Z Color }` blocks
  csv-punt  `TileID,OffsetX,OffsetY,OffsetZ,Flag,Unk1` (an older variant has a Cliloc column in place of Unk1)
  csv-swerv `TileID,OffsetX,OffsetY,OffsetZ,Flag,Cliloc` with a 64-bit hex flag and clilocs joined by `:`
  centred   CentrED# blueprints and selections: `id,x,y,z,hue,flags` (column 5 is the hue)
  uox3      `[HOUSE ITEM i]` blocks of `ITEM=0x.. X= Y= Z=`

`detect` picks a format from a file's name and head; `read`/`write` take a format name. Multi components
use flags 1 visible / 0 hidden in every text format.
"""
from __future__ import annotations

import re
import struct
from dataclasses import dataclass
from pathlib import Path

import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))

from multifile import Component  # noqa: E402


@dataclass(frozen=True)
class Tile:
    item: int
    x: int
    y: int
    z: int
    visible: bool = True
    hue: int = 0
    level: int = 0

    def component(self) -> Component:
        return Component(self.item, self.x, self.y, self.z, self.visible)


def tiles_of(comps: list[Component]) -> list[Tile]:
    return [Tile(c.item, c.x, c.y, c.z, c.visible) for c in comps]


def comps_of(tiles: list[Tile]) -> list[Component]:
    return [t.component() for t in tiles]


class FormatError(ValueError):
    pass


def num(s: str) -> int:
    s = s.strip()
    try:
        return int(s, 0) if s.lower().startswith(("0x", "-0x")) else int(s)
    except ValueError as e:
        raise FormatError(f"not a number: {s!r}") from e


def lines(text: str) -> list[str]:
    return [ln.strip() for ln in text.replace("\r\n", "\n").replace("\r", "\n").split("\n") if ln.strip()]


# --- txt (UOFiddler / Ultima SDK) --------------------------------------------------------------

def read_txt(text: str) -> list[Tile]:
    out = []
    for ln in lines(text):
        if ln.startswith(("#", ";", "//")):
            continue
        p = ln.replace(",", " ").split()
        if len(p) < 4:
            raise FormatError(f"txt: a line needs id x y z: {ln!r}")
        flags = num(p[4]) if len(p) > 4 else 1
        out.append(Tile(num(p[0]), num(p[1]), num(p[2]), num(p[3]), bool(flags & 1) if flags in (0, 1) else flags != 0))
    return out


def write_txt(tiles: list[Tile]) -> str:
    return "".join(f"0x{t.item:04X} {t.x} {t.y} {t.z} {1 if t.visible else 0}\n" for t in tiles)


# --- UOA text ----------------------------------------------------------------------------------

UOA_VERSION = 6


def read_uoa(text: str) -> list[Tile]:
    ls = lines(text)
    if len(ls) < 4:
        raise FormatError("uoa: the 4 header lines are missing")
    head = [num(ln.split()[0]) for ln in ls[:4]]
    body = ls[4:]
    if head[3] != len(body):
        raise FormatError(f"uoa: the header says {head[3]} components, the file has {len(body)}")
    out = []
    for ln in body:
        p = ln.split()
        if len(p) < 4:
            raise FormatError(f"uoa: a component needs id x y z: {ln!r}")
        flags = num(p[4]) if len(p) > 4 else 1
        out.append(Tile(num(p[0]), num(p[1]), num(p[2]), num(p[3]), flags != 0))
    return out


def write_uoa(tiles: list[Tile]) -> str:
    head = f"{UOA_VERSION} version\n1 template id\n-1 item version\n{len(tiles)} num components\n"
    return head + "".join(f"{t.item} {t.x} {t.y} {t.z} {1 if t.visible else 0}\n" for t in tiles)


# --- UOA binary --------------------------------------------------------------------------------

def _read7(b: bytes, p: int) -> tuple[int, int]:
    n = shift = 0
    while True:
        c = b[p]
        p += 1
        n |= (c & 0x7F) << shift
        if not c & 0x80:
            return n, p
        shift += 7


def _str(b: bytes, p: int) -> tuple[str, int]:
    flag = b[p]
    p += 1
    if not flag:
        return "", p
    n, p = _read7(b, p)
    return b[p:p + n].decode("utf-8"), p + n


def _wstr(s: str) -> bytes:
    raw = s.encode("utf-8")
    n, out = len(raw), bytearray()
    while True:
        out.append((n & 0x7F) | (0x80 if n > 0x7F else 0))
        n >>= 7
        if not n:
            break
    return b"\x01" + bytes(out) + raw


@dataclass
class Design:
    name: str
    category: str
    subsection: str
    tiles: list[Tile]
    width: int = 0
    height: int = 0


def read_uoab(b: bytes) -> list[Design]:
    try:
        ver = struct.unpack_from("<h", b, 0)[0]
        if ver not in (1, 2):
            raise FormatError(f"uoab: version {ver} is not 1 or 2")
        p, count = 2, 1
        if ver == 2:
            count = struct.unpack_from("<h", b, p)[0]
            p += 2
        designs = []
        for _ in range(count):
            name, p = _str(b, p)
            cat, p = _str(b, p)
            sub, p = _str(b, p)
            w, h, _uw, _uh, n = struct.unpack_from("<5i", b, p)
            p += 20
            tiles = []
            for _i in range(n):
                item, x, y, z, level, hue = struct.unpack_from("<6h", b, p)
                p += 12
                tiles.append(Tile(item & 0xFFFF, x, y, z, True, hue & 0xFFFF, level))
            designs.append(Design(name, cat, sub, tiles, w, h))
        return designs
    except (struct.error, IndexError, UnicodeDecodeError) as e:
        raise FormatError(f"uoab: truncated or malformed ({e})") from e


def write_uoab(designs: list[Design], version: int | None = None) -> bytes:
    version = version or (1 if len(designs) == 1 else 2)
    out = bytearray(struct.pack("<h", version))
    if version == 2:
        out += struct.pack("<h", len(designs))
    elif len(designs) != 1:
        raise FormatError("uoab: version 1 holds one design")
    for d in designs:
        out += _wstr(d.name) + _wstr(d.category) + _wstr(d.subsection)
        xs, ys = [t.x for t in d.tiles], [t.y for t in d.tiles]
        w = d.width or (max(xs) - min(xs) + 1 if xs else 0)
        h = d.height or (max(ys) - min(ys) + 1 if ys else 0)
        out += struct.pack("<5i", w, h, w, h, len(d.tiles))
        for t in d.tiles:
            hue = t.hue - 0x10000 if t.hue > 0x7FFF else t.hue
            out += struct.pack("<6h", t.item - 0x10000 if t.item > 0x7FFF else t.item, t.x, t.y, t.z, t.level, hue)
    return bytes(out)


# --- WSC ---------------------------------------------------------------------------------------

def read_wsc(text: str) -> list[Tile]:
    out = []
    for m in re.finditer(r"SECTION\s+WORLDITEM\s+\S+\s*\{(.*?)\}", text.replace("\r", ""), re.S | re.I):
        fields = {}
        for ln in m.group(1).splitlines():
            p = ln.split()
            if len(p) >= 2:
                fields[p[0].upper()] = num(p[1])
        if not {"ID", "X", "Y", "Z"} <= set(fields):
            raise FormatError(f"wsc: a WORLDITEM needs ID, X, Y and Z: {m.group(0)[:60]!r}")
        out.append(Tile(fields["ID"], fields["X"], fields["Y"], fields["Z"], True, fields.get("COLOR", 0)))
    return out


def write_wsc(tiles: list[Tile]) -> str:
    return "".join(f"SECTION WORLDITEM {i}\n{{\n\tID {t.item}\n\tX {t.x}\n\tY {t.y}\n\tZ {t.z}\n\tColor {t.hue}\n}}\n\n"
                   for i, t in enumerate(tiles))


# --- CSV ---------------------------------------------------------------------------------------

def _csv_rows(text: str) -> list[list[str]]:
    return [[c.strip() for c in ln.split(",")] for ln in lines(text)]


def read_csv(text: str) -> list[Tile]:
    """punt, swerv and centred are told apart by the header (a header row is optional for centred)."""
    rows = _csv_rows(text)
    if not rows:
        return []
    head = [c.lower() for c in rows[0]]
    has_header = not re.match(r"^-?(0x)?[0-9a-f]+$", head[0])
    kind = "centred"
    if has_header:
        rows = rows[1:]
        if head[0] == "tileid":
            kind = "swerv" if head[-1] == "cliloc" and "unk1" not in head else "punt"
            if "cliloc" in head and len(head) == 7:
                kind = "punt"
        else:
            kind = "centred"
    out = []
    for r in rows:
        if len(r) < 4:
            raise FormatError(f"csv: a row needs id,x,y,z: {r}")
        if kind == "centred":
            hue = num(r[4]) if len(r) > 4 else 0
            flags = num(r[5]) if len(r) > 5 else 1
        else:
            hue = 0
            flags = num(r[4]) if len(r) > 4 else 1
        out.append(Tile(num(r[0]), num(r[1]), num(r[2]), num(r[3]), flags != 0, hue))
    return out


def write_csv(tiles: list[Tile], kind: str = "punt") -> str:
    if kind == "punt":
        return "TileID,OffsetX,OffsetY,OffsetZ,Flag,Unk1\n" + "".join(
            f"{t.item},{t.x},{t.y},{t.z},{1 if t.visible else 0},0\n" for t in tiles)
    if kind == "swerv":
        return "TileID,OffsetX,OffsetY,OffsetZ,Flag,Cliloc\n" + "".join(
            f"{t.item},{t.x},{t.y},{t.z},0x{1 if t.visible else 0:x},\n" for t in tiles)
    if kind == "centred":
        return "".join(f"{t.item},{t.x},{t.y},{t.z},{t.hue},{1 if t.visible else 0}\n" for t in tiles)
    raise FormatError(f"csv kind '{kind}': punt, swerv or centred")


# --- UOX3 --------------------------------------------------------------------------------------

def read_uox3(text: str) -> list[Tile]:
    out = []
    for m in re.finditer(r"\[HOUSE\s+ITEM\s+\S+\s*\]\s*\{(.*?)\}", text.replace("\r", ""), re.S | re.I):
        f = {}
        for ln in m.group(1).splitlines():
            if "=" in ln:
                k, v = ln.split("=", 1)
                f[k.strip().upper()] = v.split("//")[0].strip()
        if not {"ITEM", "X", "Y", "Z"} <= set(f):
            raise FormatError(f"uox3: a HOUSE ITEM needs ITEM, X, Y and Z: {m.group(0)[:60]!r}")
        out.append(Tile(num(f["ITEM"]), num(f["X"]), num(f["Y"]), num(f["Z"])))
    return out


def write_uox3(tiles: list[Tile]) -> str:
    return "".join(f"[HOUSE ITEM {i}]\n{{\n\tITEM=0x{t.item:04X}\n\tX={t.x}\n\tY={t.y}\n\tZ={t.z}\n}}\n\n"
                   for i, t in enumerate(tiles))


# --- dispatch ----------------------------------------------------------------------------------

FORMATS = {
    "txt": "UOFiddler / Ultima SDK text", "uoa": "UO Architect text", "uoab": "UO Architect binary",
    "wsc": "WSC worldfile", "csv-punt": "CSV (Punt)", "csv-swerv": "CSV (SwervUO)",
    "centred": "CentrED# blueprint / selection CSV", "uox3": "UOX3 house items",
}
EXT = {".txt": "txt", ".uoa": "uoa", ".wsc": "wsc", ".csv": "csv-punt", ".dfn": "uox3"}


def detect(name: str, head: bytes) -> str:
    """The format of a file from its name and first bytes."""
    if head[:2] in (b"\x01\x00", b"\x02\x00") and name.lower().endswith(".uoa") and len(head) > 3 and head[2] in (0, 1):
        return "uoab"
    text = head.decode("utf-8", "replace")
    if name.lower().endswith(".uoa") or re.match(r"^\s*-?\d+\s+version", text):
        return "uoa"
    if re.search(r"SECTION\s+WORLDITEM", text, re.I):
        return "wsc"
    if re.search(r"\[HOUSE\s+ITEM", text, re.I):
        return "uox3"
    first = text.strip().splitlines()[0].lower() if text.strip() else ""
    if first.startswith("tileid"):
        return "csv-swerv" if first.endswith("cliloc") and "unk1" not in first else "csv-punt"
    if name.lower().endswith(".csv"):
        return "centred" if not first.startswith("tileid") else "csv-punt"
    return EXT.get(Path(name).suffix.lower(), "txt")


def read(fmt: str, data: bytes) -> list[Tile]:
    if fmt == "uoab":
        ds = read_uoab(data)
        return [t for d in ds for t in d.tiles]
    text = data.decode("utf-8-sig", "replace")
    readers = {"txt": read_txt, "uoa": read_uoa, "wsc": read_wsc, "csv-punt": read_csv, "csv-swerv": read_csv,
               "centred": read_csv, "uox3": read_uox3}
    if fmt not in readers:
        raise FormatError(f"format '{fmt}': {', '.join(FORMATS)}")
    return readers[fmt](text)


def write(fmt: str, tiles: list[Tile], name: str = "multi") -> bytes:
    if fmt == "uoab":
        return write_uoab([Design(name, "GUO", "Generated", tiles)])
    if fmt.startswith("csv-"):
        return write_csv(tiles, fmt[4:]).encode("utf-8")
    if fmt == "centred":
        return write_csv(tiles, "centred").encode("utf-8")
    writers = {"txt": write_txt, "uoa": write_uoa, "wsc": write_wsc, "uox3": write_uox3}
    if fmt not in writers:
        raise FormatError(f"format '{fmt}': {', '.join(FORMATS)}")
    return writers[fmt](tiles).encode("utf-8")


def recentre(tiles: list[Tile]) -> list[Tile]:
    """Tiles shifted so the middle of their bounds is (0, 0), as the client's multis are centred."""
    if not tiles:
        return tiles
    cx = (min(t.x for t in tiles) + max(t.x for t in tiles)) // 2
    cy = (min(t.y for t in tiles) + max(t.y for t in tiles)) // 2
    return [Tile(t.item, t.x - cx, t.y - cy, t.z, t.visible, t.hue, t.level) for t in tiles]
