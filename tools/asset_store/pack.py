"""Shared GUO pack validation. Standard library only; see docs/data_formats.md."""
from __future__ import annotations

import hashlib
import json
import re
import stat
import zipfile
from pathlib import Path

PACK_SCHEMA = "guo/store-pack@1"
INDEX_SCHEMA = "guo/store-index@1"
KINDS = {"background", "theme", "sound", "profile-preset"}
LICENCES = {"CC0-1.0", "CC-BY-4.0", "CC-BY-SA-4.0", "MIT", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0"}
EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".ogv", ".ogg", ".wav", ".json", ".txt"}
IMAGES = {".png", ".jpg", ".jpeg", ".webp"}
MAX_ZIP = 512 * 1024 * 1024
MAX_TOTAL = 1024 * 1024 * 1024
MAX_FILE = 256 * 1024 * 1024
MAX_MANIFEST = 1024 * 1024
DEVICES = {"con", "prn", "aux", "nul", *(f"com{i}" for i in range(1, 10)), *(f"lpt{i}" for i in range(1, 10))}


def require(ok, message):
    if not ok:
        raise ValueError(message)


def version(value):
    require(isinstance(value, str) and re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", value), "version must be major.minor.patch")
    parts = tuple(map(int, value.split(".")))
    require(max(parts) <= 2147483647, "version component too large")
    return parts


def identifier(value):
    require(isinstance(value, str) and re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", value) and value not in DEVICES, "invalid pack id")
    return value


def safe_path(value):
    require(isinstance(value, str) and 0 < len(value) <= 240, "invalid payload path")
    require(not any(ord(c) < 32 or c in '\\:<>"|?*' for c in value), "unsafe payload path")
    for part in value.split("/"):
        require(part not in {"", ".", ".."} and len(part) <= 100 and not part.endswith((".", " ")) and part.split(".")[0].lower() not in DEVICES, "unsafe payload component")
        require(not part.lower().startswith("cliloc") and not any(suffix in {".mul", ".uop", ".idx", ".def"} for suffix in Path(part.lower()).suffixes), "UO client data is forbidden")
    return value


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate JSON key")
        result[key] = value
    return result


def parse_manifest(raw):
    require(len(raw) <= MAX_MANIFEST, "manifest too large")
    m = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object)
    require(isinstance(m, dict), "manifest must be an object")
    require(m.get("schema") == PACK_SCHEMA, "unsupported pack schema")
    identifier(m.get("id"))
    version(m.get("version"))
    require(m.get("kind") in KINDS, "unsupported pack kind (art-override is disabled)")
    require(m.get("licence") in LICENCES, "licence is not allowed")
    for key in ("title", "author"):
        require(isinstance(m.get(key), str) and 0 < len(m[key].strip()) <= 200, f"invalid {key}")
    require(type(m.get("min_profile_version")) is int and 0 <= m["min_profile_version"] <= 2147483647, "invalid min_profile_version")
    files = m.get("files")
    require(isinstance(files, dict) and 0 < len(files) <= 1024, "invalid files map")
    seen = set()
    for name, digest in files.items():
        safe_path(name)
        require(name.lower() != "manifest.json" and Path(name).suffix.lower() in EXTENSIONS, "unsupported payload type")
        require(name.lower() not in seen, "case-alias payload")
        seen.add(name.lower())
        require(isinstance(digest, str) and re.fullmatch("[0-9a-f]{64}", digest), "invalid SHA-256")
    for name in seen:
        require(not any("/".join(name.split("/")[:i]) in seen for i in range(1, len(name.split("/")))), "file/directory collision")
    require(isinstance(m.get("preview"), str) and m["preview"] in files and Path(m["preview"]).suffix.lower() in IMAGES, "preview must name a declared image")
    require(m["licence"] == "CC0-1.0" or "LICENSE.txt" in files, "attribution requires LICENSE.txt")
    return m


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify(path):
    path = Path(path)
    require(path.stat().st_size <= MAX_ZIP, "ZIP too large")
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        require(1 < len(entries) <= 1025, "invalid entry count")
        names = set()
        total = 0
        for item in entries:
            safe_path(item.filename)
            require(item.filename.lower() not in names, "duplicate ZIP entry")
            names.add(item.filename.lower())
            mode = item.external_attr >> 16
            require(not item.is_dir() and not stat.S_ISLNK(mode) and stat.S_IFMT(mode) in {0, stat.S_IFREG}, "non-file ZIP entry")
            require(not item.flag_bits & 1, "encrypted ZIP entry")
            require(item.compress_type in {zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED}, "unsupported ZIP compression")
            require(item.file_size <= (MAX_MANIFEST if item.filename == "manifest.json" else MAX_FILE), "entry too large")
            total += item.file_size
        require(total <= MAX_TOTAL + MAX_MANIFEST, "expanded ZIP too large")
        require("manifest.json" in archive.namelist(), "root manifest missing")
        manifest = parse_manifest(archive.read("manifest.json"))
        require(set(archive.namelist()) == {"manifest.json", *manifest["files"]}, "undeclared or missing ZIP payload")
        for name, expected in manifest["files"].items():
            digest = hashlib.sha256()
            count = 0
            with archive.open(name) as stream:
                while chunk := stream.read(1024 * 1024):
                    count += len(chunk)
                    require(count <= MAX_FILE, "expanded entry too large")
                    digest.update(chunk)
            require(digest.hexdigest() == expected, f"hash mismatch: {name}")
    return manifest
