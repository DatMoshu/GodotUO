"""Bounded native Cliloc records; compressed input is decoded by the pinned oracle.

Keep untouched records verbatim, including order, duplicate IDs and unknown flags.
Edits replace the last occurrence (the client-visible value); deletion removes all
occurrences so an older duplicate cannot unexpectedly become visible again.
"""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import struct

MAX_FILE = 64 * 1024 * 1024
MAX_RECORDS = 500_000
MAX_CHANGES = 10_000
MAX_TEXT = 2 * 1024 * 1024


def check_container(raw: bytes) -> None:
    """Validate plain records or bound a BWT container before independent decoding."""
    if not isinstance(raw, bytes) or not 6 <= len(raw) <= MAX_FILE:
        raise ValueError("Cliloc must contain 6 bytes to 64 MiB")
    if raw[3] != 0x8e:
        records(raw); return
    if len(raw) < 1029:
        raise ValueError("Truncated compressed Cliloc")
    table, prefix = list(range(256)), bytearray()
    for index in raw[4:1028]:
        value = table.pop(index); table.insert(0, value); prefix.append(value)
    frequencies = struct.unpack("<256i", prefix)
    size = sum(frequencies)
    if min(frequencies) < 0 or not 6 <= size <= MAX_FILE or size + 1024 > len(raw) - 4:
        raise ValueError("Compressed Cliloc exceeds its bounded input or 64 MiB")


@dataclass(frozen=True)
class Record:
    id: int
    flag: int
    text: str
    raw: bytes


def records(plain: bytes) -> list[Record]:
    """Read a complete plain six-byte-header Cliloc, retaining each raw record."""
    if not isinstance(plain, bytes) or not 6 <= len(plain) <= MAX_FILE:
        raise ValueError("Cliloc must contain 6 bytes to 64 MiB")
    if plain[3] == 0x8e:
        raise ValueError("Decode compressed Cliloc with the pinned reader before editing")
    result, at = [], 6
    while at < len(plain):
        if len(plain) - at < 7 or len(result) >= MAX_RECORDS:
            raise ValueError("Truncated or oversized Cliloc record table")
        ident, flag, length = struct.unpack_from("<iBH", plain, at)
        end = at + 7 + length
        if ident < 0 or length > 32767 or end > len(plain):
            raise ValueError("Cliloc record exceeds the pinned reader's ID/length bounds")
        text = plain[at + 7:end].decode("utf-8", errors="strict")
        result.append(Record(ident, flag, text, plain[at:end])); at = end
    return result


def entries(rows: list[Record]) -> dict[int, Record]:
    return {row.id: row for row in rows}


def text_digest(rows: list[Record]) -> str:
    """Canonical effective ID/text hash, independently reproducible by a client reader."""
    digest = hashlib.sha256()
    for ident, row in sorted(entries(rows).items()):
        # StackDataReader.ReadUTF8(length) stops at the first NUL even in a
        # fixed-length source record. Retain its opaque tail in Record.raw.
        raw = row.text.split("\0", 1)[0].encode("utf-8")
        digest.update(struct.pack("<iI", ident, len(raw))); digest.update(raw)
    return digest.hexdigest()


def patch(plain: bytes, changes: list[dict]) -> tuple[bytes, dict]:
    """Apply reviewed JSON changes to native records without normalizing other bytes."""
    source = records(plain); current = entries(source)
    if not isinstance(changes, list) or len(changes) > MAX_CHANGES:
        raise ValueError("Cliloc patch exceeds 10,000 changes")
    edits, text_bytes = {}, 0
    for change in changes:
        ident = change["id"]
        if type(ident) is not int or not 0 <= ident <= 0x7fffffff or ident in edits:
            raise ValueError("Invalid or duplicate Cliloc change ID")
        before = current.get(ident)
        deleted = change.get("deleted", False)
        if type(deleted) is not bool:
            raise ValueError("Cliloc deletion must be a boolean")
        if deleted:
            if change.get("text") is not None or change.get("flag") is not None:
                raise ValueError("Deleted Cliloc change must not contain text or flags")
            edits[ident] = None
            continue
        text, flag = change.get("text"), change.get("flag")
        if not isinstance(text, str) or "\0" in text or type(flag) is not int or flag != (before.flag if before else 0):
            raise ValueError("Invalid Cliloc text or changed source flag")
        raw = text.encode("utf-8", errors="strict"); text_bytes += len(raw)
        if len(raw) > 32767 or text_bytes > MAX_TEXT:
            raise ValueError("Cliloc changed text exceeds 32,767 bytes per row or 2 MiB total")
        edits[ident] = struct.pack("<iBH", ident, flag, len(raw)) + raw
    last = {row.id: i for i, row in enumerate(source)}
    output, preserved, removed = [plain[:6]], 0, 0
    for i, row in enumerate(source):
        if row.id not in edits:
            output.append(row.raw); preserved += 1
        elif edits[row.id] is None:
            removed += 1
        elif last[row.id] == i:
            output.append(edits[row.id]); preserved += edits[row.id] == row.raw
        else:
            output.append(row.raw); preserved += 1
    additions = sorted(ident for ident, raw in edits.items() if ident not in current and raw is not None)
    output.extend(edits[ident] for ident in additions)
    result = b"".join(output)
    decoded = records(result)
    return result, {"source_records": len(source), "output_records": len(decoded),
        "preserved_records": preserved, "removed_records": removed, "added_ids": additions,
        "edited_ids": sorted(ident for ident, raw in edits.items() if ident in current and raw is not None and raw != current[ident].raw),
        "removed_ids": sorted(ident for ident, raw in edits.items() if ident in current and raw is None),
        "effective_records": len(entries(decoded)), "text_sha256": text_digest(decoded)}
