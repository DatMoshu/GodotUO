"""An invisible per-shard mark in the art, and the tool that reads it back (AX7, data_formats section 36, ADR-0034).

For a shard owner's custom art. The mark proves where a copied *file* came from; it cannot stop anything being copied, and
what the client draws can always be captured. A player's own extracted set is never marked.

Where it lives. A 1555 pixel has 5 bits per channel; the loaders expand each to 8 bits, so the low 3 bits of an RGBA8
channel carry nothing the game reads. The mark writes its bits there. The hue shader looks colours up by the top 5 bits
(uo_hue_core.gdshaderinc: get_rgb), so hued output is unchanged, and an unhued pixel moves by at most 7/255 per channel.
Three places in that shader decide something from the exact value rather than the 5-bit one, so the mark keeps clear
of them (see `eligible`):

    PARTIAL_HUED hues a pixel only when r == g == b      -> a grey pixel gets the same low bits in all three channels
    gump/text thresholds at 0.02 and 0.04 (5.1, 10.2/255) -> pixels with a 5-bit channel of 0 or 1 are not marked
    the top texel of the hue table (255)                   -> pixels with a 5-bit channel of 31 are not marked

SPECTRAL draws alpha = 1 - 1.5 * r from the unhued red, so a spectral sprite can differ by up to 4% in alpha.

Layout. A 16 x 16 tile of "cells" (pixel x, pixel y, channel, bit) repeats over the picture. A fixed permutation sends
every cell to one of 1024 coded bits and each cell is XORed with a fixed whitening bit; the coded bits are the payload
repeated by the tile, so a decoder takes a majority vote per bit over all the cells it can see. Because the tile
repeats, a crop only shifts the phase; the reader tries all 256 phases. The pattern is a constant of the format, not a
secret: `prove` must work from a public key alone, and the signature is what makes a payload believable.

Payload (124 bytes, zero padded, then CRC-32 of those 124 bytes):

    version u8 = 1 | shard id length u8 | shard id (ASCII) | serial u32 big-endian | Ed25519 signature (64)

The signature is over  b"guo-art-mark@1\\0" + shard id + b"\\0" + serial (4 bytes, big-endian)  with the owner's ADR-0019
signing key (tools/asset_store/ed25519.py).
"""

from __future__ import annotations

import hashlib
import struct
import sys
import zlib
from dataclasses import dataclass
from pathlib import Path

import numpy as np

import art_container as container

TOOLS = Path(__file__).resolve().parent.parent
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

from asset_store import catalogue, ed25519  # noqa: E402

TILE = 16
CODED_BITS = 1024
PAYLOAD_BYTES = 124
VERSION = 1
DOMAIN = b"guo-art-mark@1\x00"
LOW_MASK = 0xF8
K_MIN, K_MAX = 2, 30           # 5-bit channel values that may carry the mark
SEARCH_SIDE = 128              # the window the phase is looked for in


class MarkError(Exception):
    """A mark that cannot be written or an input that cannot be read. Messages never carry key material."""


# --- the cell pattern (a constant of the format) ----------------------------------------------------

def _pattern():
    cells = TILE * TILE * 9
    order = sorted(range(cells), key=lambda i: hashlib.sha256(b"guo-art-mark@1 map " + i.to_bytes(4, "big")).digest())
    mapping = np.zeros(cells, dtype=np.int32)
    for rank, cell in enumerate(order):
        mapping[cell] = rank % CODED_BITS
    white = np.zeros(cells, dtype=np.uint8)
    stream = b""
    n = 0
    while len(stream) * 8 < cells:
        stream += hashlib.sha256(b"guo-art-mark@1 white " + n.to_bytes(4, "big")).digest()
        n += 1
    bits = np.unpackbits(np.frombuffer(stream, dtype=np.uint8))[:cells]
    white[:] = bits
    return mapping, white


MAP, WHITE = _pattern()
# cell number for (v, u, channel, bit), laid out as the tile
_CELL = np.arange(TILE * TILE * 9, dtype=np.int32).reshape(TILE, TILE, 3, 3)


# --- payload ------------------------------------------------------------------------------------------

def message(shard_id: str, serial: int) -> bytes:
    return DOMAIN + shard_id.encode("ascii") + b"\x00" + struct.pack(">I", serial)


def build_payload(shard_id: str, serial: int, secret: bytes) -> bytes:
    container.check_shard_id(shard_id)
    if not 0 <= serial < 1 << 32:
        raise MarkError("a serial is a number from 0 to 4294967295")
    sid = shard_id.encode("ascii")
    body = bytes([VERSION, len(sid)]) + sid + struct.pack(">I", serial) + ed25519.sign(secret, message(shard_id, serial))
    body = body.ljust(PAYLOAD_BYTES, b"\x00")
    return body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def parse_payload(raw: bytes) -> dict | None:
    """The fields of a payload whose CRC and layout hold, else None."""
    if len(raw) != PAYLOAD_BYTES + 4:
        return None
    body, (crc,) = raw[:PAYLOAD_BYTES], struct.unpack(">I", raw[PAYLOAD_BYTES:])
    if zlib.crc32(body) & 0xFFFFFFFF != crc or body[0] != VERSION:
        return None
    n = body[1]
    if not 1 <= n <= 48 or 2 + n + 4 + 64 > PAYLOAD_BYTES:
        return None
    try:
        shard = body[2:2 + n].decode("ascii")
        container.check_shard_id(shard)
    except (UnicodeDecodeError, container.ContainerError):
        return None
    (serial,) = struct.unpack(">I", body[2 + n:6 + n])
    return {"shard_id": shard, "serial": serial, "signature": body[6 + n:6 + n + 64]}


def _coded_bits(payload: bytes) -> np.ndarray:
    return np.unpackbits(np.frombuffer(payload, dtype=np.uint8))


# --- which pixels carry it ----------------------------------------------------------------------------

def eligible(rgba: np.ndarray) -> np.ndarray:
    """Opaque pixels whose three 5-bit channels all lie in K_MIN..K_MAX. Marking never changes the 5-bit values, so the
    same mask is found on the marked picture."""
    k = rgba[..., :3] >> 3
    return (rgba[..., 3] == 255) & np.all((k >= K_MIN) & (k <= K_MAX), axis=-1)


def _grey(rgba: np.ndarray) -> np.ndarray:
    k = rgba[..., :3] >> 3
    return (k[..., 0] == k[..., 1]) & (k[..., 0] == k[..., 2])


def unsafe_pixels(rgba: np.ndarray) -> int:
    """Eligible grey pixels (equal 5-bit channels) whose 8-bit channels are not equal: marking would equalise them and
    change what PARTIAL_HUED does. Art that came from 1555 has none."""
    e = eligible(rgba) & _grey(rgba)
    unequal = (rgba[..., 0] != rgba[..., 1]) | (rgba[..., 0] != rgba[..., 2])
    return int(np.count_nonzero(e & unequal))


def _grid(h: int, w: int, pu: int, pv: int) -> tuple[np.ndarray, np.ndarray]:
    return (np.arange(h) + pv) % TILE, (np.arange(w) + pu) % TILE


def _cells(h: int, w: int, pu: int, pv: int) -> np.ndarray:
    vs, us = _grid(h, w, pu, pv)
    return _CELL[vs][:, us]                      # (h, w, 3, 3)


# --- embed --------------------------------------------------------------------------------------------

def mark_rgba(rgba: np.ndarray, payload: bytes) -> np.ndarray:
    """A copy of an (h, w, 4) uint8 picture with the payload written into its eligible pixels."""
    if rgba.ndim != 3 or rgba.shape[2] != 4 or rgba.dtype != np.uint8:
        raise MarkError("expected an RGBA8 picture")
    coded = _coded_bits(payload)
    h, w = rgba.shape[:2]
    cells = _cells(h, w, 0, 0)
    target = coded[MAP[cells]] ^ WHITE[cells]                       # (h, w, 3, 3) the bit each cell must hold
    low = (target[..., 0] | (target[..., 1] << 1) | (target[..., 2] << 2)).astype(np.uint8)   # (h, w, 3)
    grey = _grey(rgba)
    low[grey] = low[grey][:, :1]                                    # a grey pixel keeps r == g == b
    new = (rgba[..., :3] & LOW_MASK) | low
    out = rgba.copy()
    sel = eligible(rgba)
    out[..., :3][sel] = new[sel]
    return out


# --- read ---------------------------------------------------------------------------------------------

def _votes(rgba: np.ndarray, pu: int, pv: int) -> tuple[np.ndarray, np.ndarray]:
    """(ones, counts) per coded bit for the cells seen at this phase."""
    h, w = rgba.shape[:2]
    sel = eligible(rgba)
    if not sel.any():
        zero = np.zeros(CODED_BITS, dtype=np.int64)
        return zero, zero
    cells = _cells(h, w, pu, pv)[sel]                               # (n, 3, 3)
    obs = np.stack([(rgba[..., :3][sel] >> b) & 1 for b in range(3)], axis=-1)   # (n, 3, 3)
    grey = _grey(rgba)[sel]
    use = np.ones(cells.shape, dtype=bool)
    use[grey, 1:, :] = False                                        # a grey pixel's channels 1 and 2 are copies
    val = (obs ^ WHITE[cells])[use]
    idx = MAP[cells[use]]
    ones = np.bincount(idx, weights=val, minlength=CODED_BITS).astype(np.int64)
    counts = np.bincount(idx, minlength=CODED_BITS).astype(np.int64)
    return ones, counts


def _decode_at(rgba: np.ndarray, pu: int, pv: int) -> bytes:
    ones, counts = _votes(rgba, pu, pv)
    bits = (ones * 2 > counts).astype(np.uint8)
    return np.packbits(bits).tobytes()


def _windows(rgba: np.ndarray):
    """The densest SEARCH_SIDE windows first (opaque eligible pixels), as (x0, y0, x1, y1)."""
    h, w = rgba.shape[:2]
    side = SEARCH_SIDE
    if h <= side and w <= side:
        yield 0, 0, w, h
        return
    sel = eligible(rgba)
    scored = []
    for y0 in range(0, max(h - side, 0) + 1, side // 2):
        for x0 in range(0, max(w - side, 0) + 1, side // 2):
            n = int(np.count_nonzero(sel[y0:y0 + side, x0:x0 + side]))
            scored.append((n, x0, y0))
    scored.sort(reverse=True)
    for n, x0, y0 in scored[:6]:
        if n > 0:
            yield x0, y0, min(x0 + side, w), min(y0 + side, h)


def read_rgba(rgba: np.ndarray) -> dict | None:
    """Look for a mark in an (h, w, 4) uint8 picture. Returns the parsed payload (with `phase`) or None."""
    h, w = rgba.shape[:2]
    for x0, y0, x1, y1 in _windows(rgba):
        win = np.ascontiguousarray(rgba[y0:y1, x0:x1])
        scores = []
        for pv in range(TILE):
            for pu in range(TILE):
                ones, counts = _votes(win, pu, pv)
                d = 2 * ones - counts
                scores.append((int((d * d).sum() - counts.sum()), pu, pv))
        scores.sort(reverse=True)
        for _, pu, pv in scores[:3]:
            gu, gv = (pu - x0) % TILE, (pv - y0) % TILE             # the phase in whole-picture coordinates
            parsed = parse_payload(_decode_at(rgba, gu, gv))
            if parsed:
                parsed["phase"] = (gu, gv)
                return parsed
    return None


def check_signature(found: dict, public: bytes | None) -> str:
    """'valid', 'invalid', or 'unchecked' when no public key was given."""
    if public is None:
        return "unchecked"
    return "valid" if ed25519.verify(public, message(found["shard_id"], found["serial"]), found["signature"]) else "invalid"


# --- keys ----------------------------------------------------------------------------------------------

def read_sign_key(path: Path) -> bytes:
    try:
        return catalogue.read_secret(Path(path))
    except Exception as err:                                        # the catalogue's own message names the file, not the key
        raise MarkError(f"{path} is not a usable ADR-0019 signing key ({type(err).__name__})") from None


def read_public_key(value: str) -> bytes:
    p = Path(value)
    text = p.read_text(encoding="ascii") if len(value) < 260 and p.is_file() else value
    try:
        return ed25519.decode(text.strip().splitlines()[0], 32)
    except Exception:
        raise MarkError("the public key is an 'ed25519:' value, or a file holding one") from None


@dataclass
class Marker:
    """What the exporter calls on every page it writes."""
    payload: bytes
    count: int = 0

    def apply(self, rgba: bytes | bytearray, width: int, height: int) -> bytes:
        arr = np.frombuffer(bytes(rgba), dtype=np.uint8).reshape(height, width, 4)
        self.count += 1
        return mark_rgba(arr, self.payload).tobytes()

    @classmethod
    def make(cls, shard_id: str, serial: int, key_file: Path) -> "Marker":
        return cls(build_payload(shard_id, serial, read_sign_key(key_file)))
