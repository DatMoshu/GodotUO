"""Legacy multi formats: read and write the interchange files other tools use.

Written from the format descriptions in the research notes (layouts only; no code from any of the tools).
A tile is `Tile(item, x, y, z, visible, hue, level)`: hue and level are carried where a format has them
(UOA binary: both; WSC, CentrED# CSV: hue) and are 0 elsewhere.

  txt       GUO legacy text: decimal IDs unless explicitly prefixed with 0x
  txt-fiddler Fiddler 4.24 text: hexadecimal IDs, int16 offsets, preserved int32 flags
  uoa       UO Architect's text: 4 header lines, then `id x y z flags` (also the Sphere multi text)
  uoab      UO Architect's binary designs: version 1 (one design) or 2 (a count of them)
  wsc       `SECTION WORLDITEM i { ID X Y Z Color }` blocks
  csv-punt  `TileID,OffsetX,OffsetY,OffsetZ,Flag,Unk1` (an older variant has a Cliloc column in place of Unk1)
  csv-swerv `TileID,OffsetX,OffsetY,OffsetZ,Flag,Cliloc` with a 64-bit hex flag and clilocs joined by `:`
  centred   CentrED# blueprints and selections: `id,x,y,z,hue,flags` (column 5 is the hue)
  uox3      `[HOUSE ITEM i]` blocks of `ITEM=0x.. X= Y= Z=`

`detect` picks a format from a file's name and head; `read`/`write` take a format name. Multi components
use boolean visibility; txt-fiddler additionally retains the original signed flags.
"""
from __future__ import annotations

import re
import struct
from dataclasses import dataclass, replace
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
    text_flags: int | None = None
    text_extra: int | None = None

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


def read_fiddler_txt(text: str, *, check_size: bool = True) -> list[Tile]:
    """Fiddler 4.24: hexadecimal item IDs, signed decimal offsets and int32 flags."""
    if check_size and len(text.encode('utf-8')) > 4 * 1024 * 1024:
        raise FormatError('Fiddler TXT exceeds 4 MiB')
    out = []
    for number, line in enumerate(text.splitlines(), 1):
        if not line.strip(): continue
        fields = line.split()
        if len(fields) != 5:
            raise FormatError(f'Fiddler TXT line {number}: expected hexadecimal ID, X, Y, Z and flags')
        try:
            ident = fields[0].removeprefix('0x').removeprefix('0X')
            if not re.fullmatch('[0-9a-fA-F]+', ident): raise ValueError('ID must be hexadecimal')
            ident = ident.lstrip('0') or '0'
            if len(ident) > 4: raise ValueError('ID must fit uint16')
            item = int(ident, 16)
            if any(not re.fullmatch('[+-]?[0-9]+', field) for field in fields[1:]): raise ValueError('offsets/flags must be decimal')
            values = []
            for field in fields[1:]:
                digits = field.lstrip('+-').lstrip('0') or '0'
                if len(digits) > 10: raise ValueError('offsets/flags are outside signed bounds')
                values.append(int(digits) * (-1 if field.startswith('-') else 1))
            x, y, z, flags = values
            if any(not -32768 <= value <= 32767 for value in (x, y, z)) or not -(2**31) <= flags < 2**31:
                raise ValueError('offsets must fit int16 and flags int32')
            out.append(Tile(item, x, y, z, flags != 0, text_flags=flags))
            if len(out) > 65535: raise ValueError('at most 65535 components')
        except ValueError as error:
            raise FormatError(f'Fiddler TXT line {number}: {error}') from error
    if not out: raise FormatError('Fiddler TXT contains no components')
    return out


def write_fiddler_txt(tiles: list[Tile], *, allow_extra: bool = False) -> str:
    if not allow_extra and any(t.text_extra not in (None,0) for t in tiles):
        raise FormatError('This text format cannot preserve nonzero CSV Unk1; choose csv-fiddler')
    out = []
    if not tiles or len(tiles) > 65535: raise FormatError('Fiddler TXT requires 1 to 65535 components')
    for tile in tiles:
        flags = tile.text_flags if tile.text_flags is not None and (tile.text_flags != 0) == tile.visible else int(tile.visible)
        values = (tile.item, tile.x, tile.y, tile.z, flags)
        if any(type(value) is not int for value in values) or not 0 <= tile.item <= 65535 or any(not -32768 <= value <= 32767 for value in values[1:4]) or not -(2**31) <= flags < 2**31:
            raise FormatError('Fiddler TXT requires uint16 IDs, int16 offsets and int32 flags')
        out.append(f'0x{tile.item:X} {tile.x} {tile.y} {tile.z} {flags}\r\n')
    result = ''.join(out)
    if len(result) > 4 * 1024 * 1024: raise FormatError('Fiddler TXT exceeds 4 MiB')
    return result


def read_fiddler_uoa(text: str) -> list[Tile]:
    """Fiddler UOA text: four header lines, decimal IDs, retained signed flags."""
    if len(text.encode('utf-8')) > 4 * 1024 * 1024: raise FormatError('Fiddler UOA exceeds 4 MiB')
    ls = text.splitlines()
    if len(ls) < 5: raise FormatError('Fiddler UOA requires four header lines and components')
    count_field = ls[3].split()
    if not count_field or not re.fullmatch('[+]?[0-9]+', count_field[0]): raise FormatError('Fiddler UOA fourth header must start with a decimal component count')
    digits = count_field[0].lstrip('+0') or '0'
    if len(digits) > 5 or not 1 <= int(digits) <= 65535: raise FormatError('Fiddler UOA requires 1 to 65535 components')
    count = int(digits)
    if len(ls) - 4 != count: raise FormatError(f'Fiddler UOA header declares {count} components, found {len(ls)-4}')
    canonical = []
    for number, line in enumerate(ls[4:], 5):
        fields = line.split()
        if len(fields) != 5 or not re.fullmatch('[+]?[0-9]+', fields[0]): raise FormatError(f'Fiddler UOA line {number}: expected decimal ID X Y Z Flags')
        ident = fields[0].lstrip('+0') or '0'
        if len(ident) > 5 or int(ident) > 65535: raise FormatError(f'Fiddler UOA line {number}: ID must fit uint16')
        canonical.append(f'0x{int(ident):X} ' + ' '.join(fields[1:]))
    try: return read_fiddler_txt('\n'.join(canonical), check_size=False)
    except FormatError as error:
        message = re.sub(r'line (\d+)', lambda match: 'line '+str(int(match[1])+4), str(error))
        raise FormatError(message.replace('Fiddler TXT','Fiddler UOA')) from error


def write_fiddler_uoa(tiles: list[Tile]) -> str:
    canonical = write_fiddler_txt(tiles)
    records = [line.split(' ', 1) for line in canonical.splitlines()]
    return f'6 version\r\n1 template id\r\n-1 item version\r\n{len(tiles)} num components\r\n' + ''.join(f'{int(ident,16)} {rest}\r\n' for ident,rest in records)


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


def read_fiddler_wsc(text: str, dynamic_ids=()) -> list[Tile]:
    """Bounded complete WSC records with Fiddler's coordinate/dynamic-ID semantics.

    Refuse its 0xFFFF sentinel defect and incomplete/duplicate fields instead of
    returning zero-filled or inherited components. Unknown world fields are inert.
    """
    if len(text.encode('utf-8')) > 4 * 1024 * 1024: raise FormatError('Fiddler WSC exceeds 4 MiB')
    out, fields = [], None
    def finish():
        if fields is None: return
        if not {'ID','X','Y','Z'} <= fields.keys(): raise FormatError('Fiddler WSC requires ID, X, Y and Z in every section')
        ident = fields['ID']
        if not 0 <= ident < 65535: raise FormatError('Fiddler WSC ID must be 0..65534; 65535 is the reader sentinel')
        if any(not -32768 <= fields[k] <= 32767 for k in ('X','Y','Z')): raise FormatError('Fiddler WSC offsets must fit int16')
        out.append(Tile(ident, fields['X'], fields['Y'], fields['Z'], ident not in dynamic_ids,
                        text_flags=0 if ident in dynamic_ids else 1))
        if len(out) > 65535: raise FormatError('Fiddler WSC exceeds 65535 components')
    for number, line in enumerate(text.splitlines(),1):
        line = line.strip()
        if line.startswith('SECTION WORLDITEM'):
            finish(); fields = {}
        elif line and line not in ('{','}'):
            words = line.split()
            if words[0] in ('ID','X','Y','Z'):
                if fields is None or len(words) != 2 or words[0] in fields or not re.fullmatch('[+-]?[0-9]+',words[1]):
                    raise FormatError(f'Fiddler WSC line {number}: expected one decimal {words[0]} field per section')
                digits = words[1].lstrip('+-0') or '0'
                if len(digits)>5: raise FormatError(f'Fiddler WSC line {number}: numeric overflow')
                fields[words[0]] = int(digits) * (-1 if words[1].startswith('-') else 1)
    finish()
    if not out: raise FormatError('Fiddler WSC requires 1 to 65535 components')
    # Preserve the pinned reader's initial 10000 minimum and zero maximum.
    min_x, min_y = min(10000,min(t.x for t in out)), min(10000,min(t.y for t in out))
    max_x, max_y = max(0,max(t.x for t in out)), max(0,max(t.y for t in out))
    if min_x>0 and min_y>0 and min_x>max_x-min_x and min_y>max_y-min_y:
        out = [replace(t,x=t.x-min_x,y=t.y-min_y) for t in out]
    return out


def write_fiddler_wsc(tiles: list[Tile]) -> str:
    write_fiddler_txt(tiles)  # shared strict ID, coordinate, flag and count validation
    if any(t.item == 65535 for t in tiles): raise FormatError('Fiddler WSC cannot export sentinel ID 65535')
    result = ''.join(f'SECTION WORLDITEM {i}\r\n{{\r\n\tID\t{t.item}\r\n\tX\t{t.x}\r\n\tY\t{t.y}\r\n\tZ\t{t.z}\r\n\tColor\t0\r\n}}\r\n' for i,t in enumerate(tiles))
    if len(result) > 4 * 1024 * 1024: raise FormatError('Fiddler WSC output exceeds 4 MiB')
    return result


# --- CSV ---------------------------------------------------------------------------------------

def _fiddler_hex32(token: str) -> int:
    digits=token.removeprefix('0x').removeprefix('0X')
    if not re.fullmatch('[0-9a-fA-F]+',digits): raise FormatError('expected hexadecimal int32')
    digits=digits.lstrip('0') or '0'
    if len(digits)>8: raise FormatError('hexadecimal value exceeds int32 bits')
    value=int(digits,16)
    return value if value<2**31 else value-2**32


def read_fiddler_csv(text: str) -> list[Tile]:
    if len(text.encode('utf-8'))>4*1024*1024: raise FormatError('Fiddler CSV exceeds 4 MiB')
    out=[]
    for number,line in enumerate(text.splitlines(),1):
        if line.strip().startswith('TileID,OffsetX'): continue
        try:
            fields=[f.strip() for f in line.split(',')]
            if len(fields) not in (5,6): raise FormatError('expected ID,X,Y,Z,Flag and optional Unk1')
            flags=_fiddler_hex32(fields[4]); extra=fields[5] if len(fields)>5 else ''
            if not extra: value=0
            elif extra.lower().startswith('0x'): value=_fiddler_hex32(extra)
            else:
                if not re.fullmatch('[+-]?[0-9]+',extra): raise FormatError('Unk1 must be decimal or 0x-prefixed int32')
                digits=extra.lstrip('+-0') or '0'
                if len(digits)>10: raise FormatError('Unk1 exceeds int32')
                value=int(digits)*(-1 if extra.startswith('-') else 1)
                if not -(2**31)<=value<2**31: raise FormatError('Unk1 exceeds int32')
            tile=read_fiddler_txt(' '.join(fields[:4])+f' {flags}',check_size=False)[0]
            out.append(replace(tile,text_extra=value))
            if len(out)>65535: raise FormatError('at most 65535 components')
        except FormatError as error: raise FormatError(f'Fiddler CSV line {number}: {error}') from error
    if not out: raise FormatError('Fiddler CSV contains no components')
    return out


def write_fiddler_csv(tiles: list[Tile]) -> str:
    canonical=write_fiddler_txt(tiles,allow_extra=True)
    records=[line.split() for line in canonical.splitlines()]
    out=['TileID,OffsetX,OffsetY,OffsetZ,Flag,Unk1\r\n']
    for tile,record in zip(tiles,records):
        extra=tile.text_extra if tile.text_extra is not None else 0
        if type(extra) is not int or not -(2**31)<=extra<2**31: raise FormatError('Fiddler CSV Unk1 must be int32')
        out.append(f'0x{tile.item:04x},{tile.x},{tile.y},{tile.z},0x{int(record[4]) & 0xffffffff:x},0x{extra & 0xffffffff:x}\r\n')
    result=''.join(out)
    if len(result)>4*1024*1024: raise FormatError('Fiddler CSV output exceeds 4 MiB')
    return result

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


def write_fiddler_uox3(tiles: list[Tile]) -> str:
    write_fiddler_txt(tiles)  # shared scalar/count validation and nonzero Unk1 guard
    result = ''.join(f'[HOUSE ITEM {i}]\r\n{{\r\nITEM=0x{t.item:04X}\r\nX={t.x}\r\nY={t.y}\r\nZ={t.z}\r\n}}\r\n\r\n'
                     for i,t in enumerate(tiles))
    if len(result)>4*1024*1024: raise FormatError('Fiddler UOX3 output exceeds 4 MiB')
    return result


# --- dispatch ----------------------------------------------------------------------------------

FORMATS = {
    "uox3-fiddler": "Fiddler UOX3 (reviewed export only)",
    "csv-fiddler": "Fiddler CSV (hex IDs/flags, retained Unk1)",
    "wsc-fiddler": "Fiddler WSC (reviewed world items)",
    "uoa-fiddler": "UO Architect text (Fiddler)",
    "txt-fiddler": "Fiddler TXT (hexadecimal IDs)",
    "txt": "GUO legacy text (decimal IDs unless 0x-prefixed)", "uoa": "UO Architect text", "uoab": "UO Architect binary",
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
    if fmt=='uox3-fiddler': raise FormatError('Fiddler UOX3 is export-only; use legacy uox3 to read house-item blocks')
    if fmt == "uoab":
        ds = read_uoab(data)
        return [t for d in ds for t in d.tiles]
    text = data.decode("utf-8-sig", "replace")
    readers = {"txt": read_txt, "txt-fiddler": read_fiddler_txt, "uoa-fiddler": read_fiddler_uoa, "wsc-fiddler": read_fiddler_wsc, "csv-fiddler":read_fiddler_csv, "uoa": read_uoa, "wsc": read_wsc, "csv-punt": read_csv, "csv-swerv": read_csv,
               "centred": read_csv, "uox3": read_uox3}
    if fmt not in readers:
        raise FormatError(f"format '{fmt}': {', '.join(FORMATS)}")
    return readers[fmt](text)


def write(fmt: str, tiles: list[Tile], name: str = "multi") -> bytes:
    if fmt=='uox3-fiddler': return write_fiddler_uox3(tiles).encode('ascii')
    if fmt!='csv-fiddler' and any(t.text_extra not in (None,0) for t in tiles):
        raise FormatError('This format cannot preserve nonzero CSV Unk1; choose csv-fiddler')
    if fmt=='csv-fiddler': return write_fiddler_csv(tiles).encode('ascii')
    if fmt == "uoab":
        return write_uoab([Design(name, "GUO", "Generated", tiles)])
    if fmt.startswith("csv-"):
        return write_csv(tiles, fmt[4:]).encode("utf-8")
    if fmt == "centred":
        return write_csv(tiles, "centred").encode("utf-8")
    writers = {"txt": write_txt, "txt-fiddler": write_fiddler_txt, "uoa-fiddler": write_fiddler_uoa, "wsc-fiddler": write_fiddler_wsc, "uoa": write_uoa, "wsc": write_wsc, "uox3": write_uox3}
    if fmt not in writers:
        raise FormatError(f"format '{fmt}': {', '.join(FORMATS)}")
    return writers[fmt](tiles).encode("utf-8")


def recentre(tiles: list[Tile]) -> list[Tile]:
    """Tiles shifted so the middle of their bounds is (0, 0), as the client's multis are centred."""
    if not tiles:
        return tiles
    cx = (min(t.x for t in tiles) + max(t.x for t in tiles)) // 2
    cy = (min(t.y for t in tiles) + max(t.y for t in tiles)) // 2
    return [replace(t,x=t.x-cx,y=t.y-cy) for t in tiles]
