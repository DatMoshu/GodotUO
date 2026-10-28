"""Bounded native sound records; PCM is mono signed 16-bit LE at 22050 Hz."""
from __future__ import annotations

from dataclasses import dataclass
from bisect import bisect_left
import hashlib
import struct
import zlib

from guo.uomap import uop_hash

MAX_FILE = 512 * 1024 * 1024
MAX_PCM = 64 * 1024 * 1024
LIMIT = 0xFFFF
PATTERN = 'build/soundlegacymul/{0:08d}.dat'


def filenames(packaging: str) -> tuple[str, ...]:
    if packaging == 'uop': return ('soundLegacyMUL.uop',)
    if packaging == 'mul': return ('sound.mul', 'soundidx.mul')
    raise ValueError('Sound packaging must be mul or uop')


def validate(raw: bytes) -> None:
    if not isinstance(raw, bytes) or not 42 <= len(raw) <= MAX_PCM + 40 or len(raw) % 2:
        raise ValueError('Sound requires a 40-byte name and nonempty even PCM, at most 64 MiB')


def name(raw: bytes) -> str:
    validate(raw)
    return raw[:40].decode('utf-8', errors='replace').split('\0', 1)[0].rstrip(' ')


def fingerprint(raw: bytes) -> str:
    validate(raw)
    decoded = raw[:40].decode('utf-8', errors='replace').encode('utf-8')
    return hashlib.sha256(str(len(decoded)).encode('ascii') + b':' + decoded + raw[40:]).hexdigest()


def encode(original: bytes, edited_name: str, pcm: bytes) -> bytes:
    validate(original)
    if not isinstance(edited_name, str) or '\0' in edited_name:
        raise ValueError('Sound name must be text without NUL')
    if edited_name == name(original):
        header = original[:40]
    else:
        header = edited_name.encode('utf-8')
        if len(header) > 32:
            raise ValueError('Edited sound name must fit 32 UTF-8 bytes')
        header = header.ljust(40, b'\0')
    result = header + pcm
    validate(result)
    return result


@dataclass
class Library:
    packaging: str
    files: dict[str, bytes]
    records: dict[int, bytes]
    # UOP entry table position, original header bytes and original entry tuple.
    entries: dict[int, tuple]


def parse(files: dict[str, bytes], packaging: str) -> Library:
    if any(not isinstance(v, bytes) or len(v) > MAX_FILE for v in files.values()):
        raise ValueError('Sound library exceeds 512 MiB per file')
    records, entries = {}, {}
    if packaging == 'mul':
        data, index = files['sound.mul'], files['soundidx.mul']
        if not index or len(index) % 12 or len(index) > LIMIT * 12:
            raise ValueError('Invalid sound index length')
        for ident, (offset, length, extra) in enumerate(struct.iter_unpack('<iii', index)):
            if length <= 0: continue
            if offset < 0 or offset + length > len(data):
                raise ValueError('Sound index points outside its MUL')
            payload = data[offset:offset + length]; validate(payload)
            records[ident] = payload; entries[ident] = (ident * 12, b'', (offset, length, extra))
    elif packaging == 'uop':
        data = files['soundLegacyMUL.uop']
        if len(data) < 28 or struct.unpack_from('<I', data)[0] != 0x50594D:
            raise ValueError('Invalid sound UOP header')
        keys = {uop_hash(PATTERN.format(i)): i for i in range(LIMIT)}
        block = struct.unpack_from('<q', data, 12)[0]
        seen, tables, ranges = set(), [], []
        slots = 0
        while block:
            if block in seen or block < 28 or block + 12 > len(data):
                raise ValueError('Invalid or cyclic sound UOP block')
            seen.add(block)
            if len(seen) > LIMIT: raise ValueError('Sound UOP block count exceeds the native ID budget')
            count, following = struct.unpack_from('<iq', data, block)
            slots += count
            if count < 0 or slots > LIMIT or block + 12 + count * 34 > len(data):
                raise ValueError('Invalid sound UOP table length')
            tables.append((block, block + 12 + count * 34))
            for n in range(count):
                at = block + 12 + n * 34
                row = struct.unpack_from('<qiiiQIh', data, at)
                offset, header, stored, length, key, checksum, flag = row
                if offset == 0: continue
                if flag != 0:
                    raise ValueError('Compressed sound UOP entries are not certified by the pinned sound loader')
                if min(offset, header, stored, length) < 0 or stored != length or offset + header + length > len(data):
                    raise ValueError('Invalid sound UOP payload bounds')
                if key not in keys or keys[key] in records:
                    raise ValueError('Unknown or duplicate sound UOP hash')
                payload = data[offset + header:offset + header + length]; validate(payload)
                ident = keys[key]; records[ident] = payload
                entries[ident] = (at, data[offset:offset + header], row)
                ranges.append((offset, offset + header + length))
            block = following
        ordered = sorted(tables)
        if any(b > c for (a, b), (c, d) in zip(ordered, ordered[1:])):
            raise ValueError('Sound UOP tables overlap')
        starts = [a for a, b in ordered]
        for a, b in ranges:
            previous = bisect_left(starts, b) - 1
            if a < 28 or previous >= 0 and ordered[previous][1] > a:
                raise ValueError('Sound UOP payload overlaps a table')
    else:
        raise ValueError('Sound packaging must be mul or uop')
    return Library(packaging, files, records, entries)


def patch(library: Library, changes: dict[int, bytes]) -> dict[str, bytes]:
    for ident, payload in changes.items():
        if type(ident) is not int or not 0 <= ident < LIMIT or ident not in library.records:
            raise ValueError('Sound replacement requires an existing occupied physical row')
        validate(payload)
    result = dict(library.files)
    if library.packaging == 'mul':
        data, index = bytearray(result['sound.mul']), bytearray(result['soundidx.mul'])
        for ident, payload in changes.items():
            if payload == library.records[ident]: continue
            if len(data) + len(payload) > MAX_FILE: raise ValueError('Sound MUL append exceeds 512 MiB')
            struct.pack_into('<ii', index, ident * 12, len(data), len(payload)); data += payload
        result.update({'sound.mul': bytes(data), 'soundidx.mul': bytes(index)})
    else:
        data = bytearray(result['soundLegacyMUL.uop'])
        for ident, payload in changes.items():
            if payload == library.records[ident]: continue
            at, header, row = library.entries[ident]
            if len(data) + len(header) + len(payload) > MAX_FILE: raise ValueError('Sound UOP append exceeds 512 MiB')
            offset = len(data); data += header + payload
            struct.pack_into('<qiiiQIh', data, at, offset, len(header), len(payload), len(payload), row[4], zlib.adler32(payload), 0)
        result['soundLegacyMUL.uop'] = bytes(data)
    return result
