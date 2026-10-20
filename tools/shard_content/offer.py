"""A shard's pack offer, guo/shard-offer@1 (docs/data_formats.md section 12, ADR-0026 section 4a).

The GUO bridge sends <shard>/Data/GUO/shard-offer.json to every client at login; this writes it from the shard's
descriptor and any feature blocks. The checks mirror the client's (godot/GUO/src/Store/StoreShardOffer.cs), so an
offer this writes is one the client reads.
"""
from __future__ import annotations

import json
from pathlib import Path
from asset_store.pack import identifier, version, units, unique_object, no_constant

SCHEMA = "guo/shard-offer@1"
MAX_JSON, MAX_FEATURE, MAX_PACKS, MAX_FEATURES = 16384, 4096, 64, 32


def _text(value, what: str) -> str:
    if not isinstance(value, str) or not value.strip() or units(value) > 200 or any(ord(c) < 32 or 0x7F <= ord(c) < 0xA0 for c in value):
        raise ValueError(f"invalid {what}")
    return value


def build(name: str, descriptor: dict | None, titles: dict[str, str] | None = None,
          features: dict[str, dict] | None = None) -> dict:
    """The offer: the descriptor inline (or none: features only), the packs to show, and the feature blocks."""
    offer: dict = {"schema": SCHEMA, "shard": {"name": _text(name, "shard name")}}
    if descriptor is not None:
        if descriptor.get("schema") != "guo/shard-content@1":
            raise ValueError("the descriptor is not guo/shard-content@1")
        lock = descriptor["lock"]
        identifier(lock["pack"])
        version(lock["version"])
        offer["content"] = descriptor
        titles = titles or {}
        unknown = set(titles) - {lock["pack"]}
        if unknown:
            raise ValueError(f"--title names a pack the lock does not install: {sorted(unknown)}")
        offer["packs"] = [{"id": lock["pack"], "version": lock["version"], "title": _text(titles.get(lock["pack"], lock["pack"]), "pack title")}]
    blocks = features or {}
    if len(blocks) > MAX_FEATURES:
        raise ValueError(f"at most {MAX_FEATURES} feature blocks")
    for key, block in blocks.items():
        identifier(key)
        if not isinstance(block, dict):
            raise ValueError(f"feature {key}: a block is a JSON object")
        if len(json.dumps(block, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")) > MAX_FEATURE:
            raise ValueError(f"feature {key}: over {MAX_FEATURE} bytes")
    if blocks:
        offer["features"] = blocks
    if "content" not in offer and not blocks:
        raise ValueError("an offer with no content and no features says nothing")
    return offer


def encode(offer: dict) -> bytes:
    raw = json.dumps(offer, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")
    if len(raw) > MAX_JSON:
        raise ValueError(f"the offer is {len(raw)} bytes; the limit is {MAX_JSON}")
    return raw


def packet(raw: bytes) -> bytes:
    """What the bridge sends: 0xBF, its length, subcommand 0x4755, format 1, the JSON."""
    if len(raw) > MAX_JSON:
        raise ValueError("offer too large")
    length = 6 + len(raw)
    return bytes([0xBF, length >> 8, length & 0xFF, 0x47, 0x55, 1]) + raw


def read_features(items: list[str]) -> dict[str, dict]:
    out = {}
    for item in items:
        name, sep, path = item.partition("=")
        if not sep:
            raise ValueError(f"--feature needs NAME=FILE, got {item}")
        if name in out:
            raise ValueError(f"duplicate feature {name!r}")
        out[name] = json.loads(Path(path).read_text(encoding="utf-8"), object_pairs_hook=unique_object, parse_constant=no_constant)
    return out
