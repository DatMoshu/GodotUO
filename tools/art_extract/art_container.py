"""The encrypted art container (AX6, data_formats section 36, ADR-0034 addendum).

One file, `<shard id>.guoart`, holding what a plain set holds (set.json, one index.json per class, the PNG pages), each
piece sealed with AES-256-GCM. It is for a shard owner's custom art: it keeps the art from being copied off the disk
by anyone who does not hold the shard's key. It does not stop a determined person: what the client draws can always be
captured. Layout (all integers little-endian):

    magic        8   "GUOART\\x01\\x00"
    header_len   4
    header       header_len bytes of UTF-8 JSON (sorted keys, no spaces): schema, version, cipher, shard_id, key_id, set_id
    chunks       name_len u16, name (UTF-8), nonce 12, sealed_len u32, sealed (ciphertext + 16 byte tag)
    toc chunk    the same shape, name "\\x00toc", plaintext {"entries": {name: [offset, sealed_len]}}
    footer       toc offset u64, "GUOEND\\x01\\x00"

Every chunk's associated data is `header_len || header || name`, so a changed header, a renamed chunk or a chunk
moved to another name fails its tag. The key never appears in the file; `key_id` is a one-way tag of it.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import secrets
import struct
from pathlib import Path

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

MAGIC = b"GUOART\x01\x00"
END = b"GUOEND\x01\x00"
SCHEMA = "guo/art_container@1"
CIPHER = "aes-256-gcm"
TOC = "\x00toc"
KEY_BYTES = 32
NONCE_BYTES = 12
TAG_BYTES = 16
SHARD_ID = re.compile(r"^[a-z0-9][a-z0-9_-]{0,47}$")


class ContainerError(Exception):
    """A container or key that cannot be used. Messages never carry key material."""


def check_shard_id(shard_id: str) -> str:
    if not SHARD_ID.match(shard_id or ""):
        raise ContainerError("a shard id is 1-48 characters of a-z, 0-9, '_' or '-', starting with a letter or digit")
    return shard_id


# --- keys --------------------------------------------------------------------------------

def new_key() -> bytes:
    return secrets.token_bytes(KEY_BYTES)


def key_id_of(key: bytes) -> str:
    """A tag the header carries so a wrong key is named before any chunk is tried. One way; not the key."""
    return hashlib.sha256(b"guo/art_key_id\0" + key).hexdigest()[:16]


def write_key_file(path: Path, key: bytes) -> None:
    """64 hex characters and a newline, created readable by the owner only where the OS has such a mode."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="ascii", newline="\n") as f:
        f.write(key.hex() + "\n")


def read_key_file(path: Path) -> bytes:
    try:
        text = Path(path).read_text(encoding="ascii").strip()
    except OSError as e:
        raise ContainerError(f"key file {path} cannot be read ({e.strerror or 'error'})") from None
    except UnicodeDecodeError:
        raise ContainerError(f"key file {path} is not a key file") from None
    if not re.fullmatch(r"[0-9a-fA-F]{64}", text):
        raise ContainerError(f"key file {path} is not 64 hex characters")
    return bytes.fromhex(text)


# --- sealing -----------------------------------------------------------------------------

def _header_bytes(shard_id: str, key: bytes, set_id: str) -> bytes:
    doc = {"schema": SCHEMA, "version": 1, "cipher": CIPHER, "shard_id": shard_id,
           "key_id": key_id_of(key), "set_id": set_id}
    return json.dumps(doc, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _aad(header: bytes, name: str) -> bytes:
    return struct.pack("<I", len(header)) + header + name.encode("utf-8")


class ContainerWriter:
    """Streams sealed chunks to `<path>.part` and renames it into place on close, so a failed run leaves no container."""

    def __init__(self, path: Path, shard_id: str, key: bytes, set_id: str):
        if len(key) != KEY_BYTES:
            raise ContainerError("a key is 32 bytes")
        self.path = Path(path)
        self.part = self.path.with_name(self.path.name + ".part")
        self.key = key
        self.aes = AESGCM(key)
        self.header = _header_bytes(check_shard_id(shard_id), key, set_id)
        self.entries: dict[str, list[int]] = {}
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.f = open(self.part, "wb")
        self.f.write(MAGIC + struct.pack("<I", len(self.header)) + self.header)

    def _chunk(self, name: str, data: bytes) -> list[int]:
        raw = name.encode("utf-8")
        nonce = secrets.token_bytes(NONCE_BYTES)
        sealed = self.aes.encrypt(nonce, data, _aad(self.header, name))
        offset = self.f.tell()
        self.f.write(struct.pack("<H", len(raw)) + raw + nonce + struct.pack("<I", len(sealed)) + sealed)
        return [offset, len(sealed)]

    def put(self, name: str, data: bytes) -> None:
        if name in self.entries or name == TOC:
            raise ContainerError(f"chunk {name!r} written twice")
        self.entries[name] = self._chunk(name, data)

    def close(self) -> None:
        toc_at, _ = self._chunk(TOC, json.dumps({"entries": self.entries}, sort_keys=True).encode("utf-8"))
        self.f.write(struct.pack("<Q", toc_at) + END)
        self.f.close()
        os.replace(self.part, self.path)

    def abort(self) -> None:
        self.f.close()
        self.part.unlink(missing_ok=True)


class Container:
    """Reads a container: `names()` and `read(name)`. A tampered byte, header or wrong key raises ContainerError."""

    def __init__(self, path: Path, key: bytes):
        self.path = Path(path)
        self.aes = AESGCM(key)
        self.data = self.path.read_bytes()
        d = self.data
        if len(d) < len(MAGIC) + 4 + 16 or d[:len(MAGIC)] != MAGIC or d[-len(END):] != END:
            raise ContainerError("not an art container")
        (hlen,) = struct.unpack_from("<I", d, len(MAGIC))
        self.header = d[len(MAGIC) + 4:len(MAGIC) + 4 + hlen]
        try:
            self.info = json.loads(self.header)
        except ValueError:
            raise ContainerError("the container header is damaged") from None
        if self.info.get("schema") != SCHEMA or self.info.get("version") != 1 or self.info.get("cipher") != CIPHER:
            raise ContainerError("the container is a format version this tool does not know")
        if self.info.get("key_id") != key_id_of(key):
            raise ContainerError(f"this key is not the one for shard {self.info.get('shard_id')!r}")
        (toc_at,) = struct.unpack_from("<Q", d, len(d) - len(END) - 8)
        self.entries = json.loads(self._open(toc_at, TOC))["entries"]

    def _open(self, offset: int, expect_name: str) -> bytes:
        d = self.data
        try:
            (nlen,) = struct.unpack_from("<H", d, offset)
            name = d[offset + 2:offset + 2 + nlen].decode("utf-8")
            at = offset + 2 + nlen
            nonce = d[at:at + NONCE_BYTES]
            (slen,) = struct.unpack_from("<I", d, at + NONCE_BYTES)
            sealed = d[at + NONCE_BYTES + 4:at + NONCE_BYTES + 4 + slen]
            if name != expect_name or len(sealed) != slen:
                raise ContainerError("the container is damaged")
            return self.aes.decrypt(nonce, sealed, _aad(self.header, name))
        except (struct.error, UnicodeDecodeError):
            raise ContainerError("the container is damaged") from None
        except InvalidTag:
            raise ContainerError(f"{expect_name!r} failed its integrity check (wrong key, or the file was changed)") from None

    def names(self) -> list[str]:
        return sorted(self.entries)

    def read(self, name: str) -> bytes:
        if name not in self.entries:
            raise ContainerError(f"{name!r} is not in the container")
        return self._open(self.entries[name][0], name)
