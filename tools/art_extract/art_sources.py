"""The install's art as the client's loaders decode it, one source per class (data_formats section 36).

No decoder of its own for the .mul/.uop layouts: the readers are tools/guo/uoread.py and the row decoders are
tools/guo/uoart.py, the ones tools/world and tools/uopack already use (they mirror ArtLoader, GumpsLoader and
TexmapsLoader). What lives here is only what turns a loader's 15-bit result into the loader's RGBA bytes, and the
loader's own rules for which ids exist (verdata.mul patches, TexTerr.def aliases).
"""

from __future__ import annotations

import re
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path

TOOLS = Path(__file__).resolve().parent.parent
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

from guo import uoart, uoread  # noqa: E402

CLASSES = ("land", "static", "gump", "texmap", "light")

# HuesHelper._table: a 5-bit channel to 8 bits.
_EXPAND = (0x00, 0x08, 0x10, 0x18, 0x20, 0x29, 0x31, 0x39, 0x41, 0x4A, 0x52, 0x5A, 0x62, 0x6A, 0x73, 0x7B, 0x83, 0x8B,
           0x94, 0x9C, 0xA4, 0xAC, 0xB4, 0xBD, 0xC5, 0xCD, 0xD5, 0xDE, 0xE6, 0xEE, 0xF6, 0xFF)
CLEAR = b"\x00\x00\x00\x00"
# Color16To32 | 0xFF000000 as bytes R, G, B, A. DRAWN has no transparent value (land, texmaps).
DRAWN = [bytes((_EXPAND[(c >> 10) & 31], _EXPAND[(c >> 5) & 31], _EXPAND[c & 31], 255)) for c in range(0x10000)]
# KEYED: colour 0 is "not drawn" (statics, gumps).
KEYED = [CLEAR] + DRAWN[1:]
DIAMOND = [uoart.in_diamond(i % 44, i // 44) for i in range(44 * 44)]
MAX_SIDE = 2048

Image = tuple[int, int, bytes]


class Skip(Exception):
    """The id exists but is not stored; the message is the reason written to `skipped`."""


@dataclass
class Verdata:
    art: dict[int, bytes] = field(default_factory=dict)
    gumps: dict[int, tuple[int, bytes]] = field(default_factory=dict)   # block -> (extra, data)
    path: Path | None = None


def _verdata(data: Path) -> Verdata:
    path = uoread._find(data, "verdata.mul")
    v = Verdata()
    if path is None or path.stat().st_size < 4:
        return v
    for p in uoart.read_verdata(path):
        if p.file_id == uoart.VERDATA_ART:
            v.art[p.block] = p.data
        elif p.file_id == uoart.VERDATA_GUMP:
            v.gumps[p.block] = (p.extra, p.data)
    v.path = path
    return v


def _art_files(data: Path, art: uoread.Art, verdata: Verdata) -> list[str]:
    if art.uop:
        names = [art.uop.path.name]
    else:
        names = [art.mul.mul.name, uoread._find(data, "artidx.mul").name]
    return names + ([verdata.path.name] if verdata.art and verdata.path else [])


class LandSource:
    cls = "land"

    def __init__(self, data: Path, art: uoread.Art, verdata: Verdata):
        self.art, self.verdata = art, verdata
        self.files = _art_files(data, art, verdata)

    def ids(self):
        return range(uoart.LAND_COUNT)

    def decode(self, i: int) -> Image | None:
        raw = self.verdata.art.get(i) or self.art.get_land(i)
        if not raw:
            return None
        try:
            px = uoart.decode_land(raw)
        except (struct.error, IndexError) as e:
            raise Skip(f"land tile does not decode ({type(e).__name__})") from e
        return 44, 44, b"".join(DRAWN[v] if d else CLEAR for v, d in zip(px, DIAMOND))


class StaticSource:
    cls = "static"
    MAX_ID = 0x14000 - uoart.LAND_COUNT        # ArtLoader.MAX_STATIC_DATA_INDEX_COUNT less the land block

    def __init__(self, data: Path, art: uoread.Art, verdata: Verdata):
        self.art, self.verdata = art, verdata
        self.files = _art_files(data, art, verdata)

    def ids(self):
        return range(self.MAX_ID)

    def decode(self, i: int) -> Image | None:
        raw = self.verdata.art.get(uoart.LAND_COUNT + i) or self.art.get_static(i)
        if not raw:
            return None
        if len(raw) < 8:
            raise Skip("static entry shorter than its header")
        try:
            w, h, px = uoart.decode_static(raw)
        except (struct.error, IndexError, ValueError, MemoryError) as e:
            raise Skip(f"static does not decode ({type(e).__name__})") from e
        return _keyed(w, h, px)


class GumpSource:
    cls = "gump"
    MAX_ID = 0x10000                            # GumpsLoader.MAX_GUMP_DATA_INDEX_COUNT

    def __init__(self, data: Path, verdata: Verdata):
        self.gumps = uoread.Gumps(data)
        self.verdata = verdata
        if self.gumps.uop:
            names = [self.gumps.uop.path.name]
        else:
            names = [self.gumps.mul.mul.name, uoread._find(data, "gumpidx.mul").name]
        self.files = names + ([verdata.path.name] if verdata.gumps and verdata.path else [])

    def ids(self):
        return range(self.MAX_ID)

    def decode(self, i: int) -> Image | None:
        patch = self.verdata.gumps.get(i)
        if patch is not None:
            extra, raw = patch
            got = ((extra >> 16) & 0xFFFF, extra & 0xFFFF, raw)
        else:
            got = self.gumps.get(i)
        if got is None:
            return None
        w, h, raw = got
        if w <= 0 or h <= 0:
            return None
        if w > MAX_SIDE or h > MAX_SIDE:
            raise Skip(f"gump {w}x{h} does not fit a page")
        try:
            px = uoart.decode_gump(raw, w, h)
        except (struct.error, IndexError) as e:
            raise Skip(f"gump does not decode ({type(e).__name__})") from e
        return _keyed(w, h, px)


class TexmapSource:
    cls = "texmap"

    def __init__(self, data: Path):
        mul, idx = uoread._find(data, "texmaps.mul"), uoread._find(data, "texidx.mul")
        if not (mul and idx):
            raise FileNotFoundError(f"no texmaps.mul/texidx.mul in {data}")
        self.index = uoread.MulIndex(mul, idx)
        self.files = [mul.name, idx.name]
        self.alias = list(range(len(self.index.records)))
        tdef = uoread._find(data, "TexTerr.def")
        if tdef is not None:
            self.files.append(tdef.name)
            self._read_def(tdef)

    def _read_def(self, path: Path) -> None:
        """TexmapsLoader.Load + DefReader: `index {a,b,...}`, each group member overwriting the entry in turn."""
        for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
            line = line.strip()
            if not line or not line[0].isdigit():
                continue
            line = line.split("#", 1)[0]
            m = re.match(r"(\d+)\s+\{([^}]*)\}", line)
            if not m:
                continue
            index = int(m.group(1))
            if index >= len(self.alias):
                continue
            for tok in re.split(r"[,\s]+", m.group(2).strip()):
                if tok.isdigit():
                    check = int(tok)
                    if check < len(self.alias):
                        self.alias[index] = self.alias[check]

    def ids(self):
        return range(len(self.alias))

    def decode(self, i: int) -> Image | None:
        e = self.index.entry(self.alias[i])
        if e is None:
            return None
        try:
            size, px = uoart.decode_texmap(e[0])
        except struct.error as err:
            raise Skip("texmap shorter than its size") from err
        return size, size, b"".join(DRAWN[v] for v in px)


class LightSource:
    cls = "light"

    def __init__(self, data: Path):
        mul, idx = uoread._find(data, "light.mul"), uoread._find(data, "lightidx.mul")
        if not (mul and idx):
            raise FileNotFoundError(f"no light.mul/lightidx.mul in {data}")
        self.index = uoread.MulIndex(mul, idx)
        self.files = [mul.name, idx.name]

    def ids(self):
        return range(len(self.index.records))

    def decode(self, i: int) -> Image | None:
        e = self.index.entry(i)
        if e is None:
            return None
        raw, extra = e
        w, h = (extra >> 16) & 0xFFFF, extra & 0xFFFF
        if extra <= 0 or (w == 0 and h == 0):
            return None
        if w == 0 or h == 0 or w > MAX_SIDE or h > MAX_SIDE:
            raise Skip(f"light {w}x{h} is not storable")
        if len(raw) < w * h:
            raise Skip("light shorter than its size")
        out = []
        for v in raw[:w * h]:
            if v > 0x1F:
                v = ~v & 0x1F          # LightsLoader: below zero they are bit inverted
            out.append(bytes((v << 3, v << 3, v << 3, 255)) if v else CLEAR)
        return w, h, b"".join(out)


def _keyed(w: int, h: int, px: list[int]) -> Image:
    if w <= 0 or h <= 0:
        raise Skip("empty image")
    if w > MAX_SIDE or h > MAX_SIDE:
        raise Skip(f"{w}x{h} does not fit a page")
    return w, h, b"".join(KEYED[v] for v in px)


def open_sources(data: Path, what: tuple[str, ...]) -> list:
    """The sources for the requested classes, in CLASSES order."""
    data = Path(data)
    needs_art = "land" in what or "static" in what
    verdata = _verdata(data) if needs_art or "gump" in what else Verdata()
    art = uoread.Art(data) if needs_art else None
    out = []
    for c in CLASSES:
        if c not in what:
            continue
        if c == "land":
            out.append(LandSource(data, art, verdata))
        elif c == "static":
            out.append(StaticSource(data, art, verdata))
        elif c == "gump":
            out.append(GumpSource(data, verdata))
        elif c == "texmap":
            out.append(TexmapSource(data))
        else:
            out.append(LightSource(data))
    return out
