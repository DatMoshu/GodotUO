"""Build the shared pack validation corpus (cases.json) into one folder of manifests and ZIPs.

    python tools/asset_store/corpus/build.py OUT_DIR

Each case becomes OUT_DIR/<name>.json (a manifest's raw bytes) or
OUT_DIR/<name>.zip (a pack), plus OUT_DIR/expect.json. ZIPs are written byte
by byte here, not with zipfile, so a case can set exactly what a hostile pack
would: raw name bytes, the UTF-8 flag, the encryption bit, the compression
method, Unix modes. Standard library only; nothing is derived from UO data.
"""
from __future__ import annotations

import base64
import hashlib
import json
import struct
import sys
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent

BASE = {
    "schema": "guo/store-pack@1",
    "version": "1.0.0",
    "kind": "background",
    "title": "Corpus pack",
    "author": "GUO",
    "licence": "CC0-1.0",
    "min_profile_version": 6,
    "preview": "still.png",
}


def data(value) -> bytes:
    if isinstance(value, dict):
        if "repeat" in value:
            return (value["repeat"] * value["count"]).encode("utf-8")
        return base64.b64decode(value["b64"])
    return value.encode("utf-8")


def units(s: str) -> int:
    return len(s.encode("utf-16-le")) // 2


def repeat_to(char: str, n: int) -> str:
    return char * (n // units(char))


def long_path(n: int, char: str) -> str:
    """A path of exactly n UTF-16 units ending in .png, in components of at most 100 units."""
    body = n - 4
    parts, left = [], body
    while left > 0:
        take = min(99, left if left <= 99 else 99)
        # leave room for the "/" that joins the next component
        if left > 99:
            parts.append(char * 99)
            left -= 100
        else:
            parts.append(char * left)
            left = 0
    return "/".join(parts) + ".png"


def manifest_bytes(case: dict) -> tuple[bytes, dict]:
    payload = {k: data(v) for k, v in case.get("payload", {"still.png": "png"}).items()}
    if "long_path" in case:
        payload[long_path(case["long_path"]["units"], case["long_path"]["char"])] = b"x"
    if "long_component" in case:
        payload[repeat_to(case["long_component"]["char"], case["long_component"]["units"]) + ".png"] = b"x"
    m = dict(BASE, id="corpus-" + case["name"])
    if "long_title" in case:
        m["title"] = repeat_to(case["long_title"]["char"], case["long_title"]["units"])
    for k, v in case.get("manifest", {}).items():
        if v is None:
            m.pop(k, None)
        else:
            m[k] = v
    m["files"] = case.get("files") or {n: hashlib.sha256(b).hexdigest() for n, b in payload.items()}
    if "upper_hash" in case:
        m["files"][case["upper_hash"]] = m["files"][case["upper_hash"]].upper()
    raw_patch = case.get("manifest_raw_patch", {})
    for k in raw_patch:
        m[k] = f"__RAW_{k}__"
    text = json.dumps(m, ensure_ascii=False, indent=1)
    for k, raw in raw_patch.items():
        text = text.replace(f'"__RAW_{k}__"', raw)
    raw = text.encode("utf-8", "surrogatepass")
    if case.get("bom"):
        raw = b"\xef\xbb\xbf" + raw
    return raw, payload


def write_zip(path: Path, entries: list[dict]) -> None:
    """entries: name (str), data (bytes), and optional utf8_flag, flags, method, mode."""
    out = bytearray()
    central = bytearray()
    for e in entries:
        name = e["name"]
        ascii_only = all(ord(c) < 128 for c in name)
        raw_name = name.encode("utf-8")
        flags = e.get("flags", 0)
        if not ascii_only and e.get("utf8_flag", True):
            flags |= 0x800
        method = e.get("method", 8)
        body = e["data"]
        stored = zlib.compress(body, 9)[2:-4] if method == 8 else body
        crc = zlib.crc32(body) & 0xFFFFFFFF
        mode = e.get("mode", 0o100644)
        attr = (mode << 16) & 0xFFFFFFFF
        made_by = (3 << 8) | 20
        offset = len(out)
        out += struct.pack("<IHHHHHIIIHH", 0x04034B50, 20, flags, method, 0, 0x21, crc, len(stored), len(body), len(raw_name), 0)
        out += raw_name + stored
        central += struct.pack("<IHHHHHHIIIHHHHHII", 0x02014B50, made_by, 20, flags, method, 0, 0x21, crc, len(stored), len(body),
                               len(raw_name), 0, 0, 0, 0, attr, offset)
        central += raw_name
    start = len(out)
    out += central
    out += struct.pack("<IHHHHIIH", 0x06054B50, 0, 0, len(entries), len(entries), len(central), start, 0)
    path.write_bytes(bytes(out))


def build(out: Path) -> dict:
    cases = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))["cases"]
    out.mkdir(parents=True, exist_ok=True)
    for f in out.iterdir():
        if f.suffix in {".json", ".zip"}:
            f.unlink()
    expect = {}
    for case in cases:
        raw, payload = manifest_bytes(case)
        name = case["name"]
        if "zip" not in case:
            (out / f"{name}.json").write_bytes(raw)
            expect[f"{name}.json"] = {"expect": case["expect"], "why": case["why"]}
            continue
        z = case["zip"]
        for n, text in z.get("replace", {}).items():
            payload[n] = data(text)
        for n in z.get("drop", []):
            payload.pop(n, None)
        entries = [{"name": z.get("manifest_name", "manifest.json"), "data": raw}]
        for n, b in payload.items():
            n = z.get("rename", {}).get(n, n)
            entries.append({"name": n, "data": b, **z.get("entry", {}).get(n, {})})
        for n, text in z.get("extra", {}).items():
            entries.append({"name": n, "data": data(text)})
        for e in z.get("extra_raw", []):
            entries.append({"name": e["name"], "data": data(e["data"])})
        write_zip(out / f"{name}.zip", entries)
        expect[f"{name}.zip"] = {"expect": case["expect"], "why": case["why"]}
    (out / "expect.json").write_text(json.dumps(expect, indent=1, ensure_ascii=False), encoding="utf-8")
    return expect


if __name__ == "__main__":
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE.parents[2] / "build" / "asset_store_corpus"
    print(f"{len(build(target))} corpus files -> {target}")
