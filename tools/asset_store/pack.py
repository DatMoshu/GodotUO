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
KINDS = {"background", "theme", "sound", "profile-preset", "screensaver", "postfx", "razor-script"}
# A screensaver is played by the client from profile version 11 on.
SCREENSAVER_MIN_PROFILE = 11
LICENCES = {"CC0-1.0", "CC-BY-4.0", "CC-BY-SA-4.0", "MIT", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0"}
EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".ogv", ".ogg", ".wav", ".json", ".txt", ".gdshader", ".razor"}
MAX_SCRIPT_BYTES = 262144
MAX_SCRIPT_CHARS = 65536
IMAGES = {".png", ".jpg", ".jpeg", ".webp"}
MAX_ZIP = 512 * 1024 * 1024
MAX_TOTAL = 1024 * 1024 * 1024
MAX_FILE = 256 * 1024 * 1024
MAX_MANIFEST = 1024 * 1024
# Windows reserves COM1-9 and LPT1-9, and the superscript digits too (COM\u00b9 is a device).
_DIGITS = [str(i) for i in range(1, 10)] + ["\u00b9", "\u00b2", "\u00b3"]
DEVICES = {"con", "prn", "aux", "nul", *(f"com{d}" for d in _DIGITS), *(f"lpt{d}" for d in _DIGITS)}


def units(value: str) -> int:
    """Length in UTF-16 code units: what the C# installer and the file system count."""
    return len(value.encode("utf-16-le", "surrogatepass")) // 2


def fold_keys(value: str) -> set[str]:
    """The one case rule both validators apply (StorePack.FoldKeys is the same):
    Turkish dotted and dotless i are i, then each character folds by its simple
    one-to-one lower and upper mapping (as NTFS and .NET do; no "ß" -> "SS").
    Two names that share a key are one name."""
    value = value.replace("ı", "i").replace("İ", "i")
    lower = "".join(c.lower() if len(c.lower()) == 1 else c for c in value)
    upper = "".join(c.upper() if len(c.upper()) == 1 else c for c in value)
    return {lower, upper}


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
    require(isinstance(value, str) and 0 < units(value) <= 240, "invalid payload path")
    require(not any(ord(c) < 32 or c in '\\:<>"|?*' for c in value), "unsafe payload path")
    for part in value.split("/"):
        require(part not in {"", ".", ".."} and units(part) <= 100 and not part.endswith((".", " ")) and part.split(".")[0].lower() not in DEVICES, "unsafe payload component")
        require(not part.startswith("."), "a name must have a stem, not only an extension")
        require(not part.lower().startswith("cliloc") and not re.search(r"\.(mul|uop|idx|def)(\.|$)", part, re.IGNORECASE), "UO client data is forbidden")
    return value


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate JSON key")
        result[key] = value
    return result


def no_constant(name):
    raise ValueError(f"{name} is not JSON")


def valid_unicode(value):
    """Every string in the manifest must be encodable UTF-8: no lone surrogates."""
    if isinstance(value, str):
        try:
            value.encode("utf-8")
        except UnicodeEncodeError:
            raise ValueError("invalid Unicode in the manifest") from None
    elif isinstance(value, dict):
        for k, v in value.items():
            valid_unicode(k)
            valid_unicode(v)
    elif isinstance(value, list):
        for v in value:
            valid_unicode(v)


def parse_manifest(raw):
    require(len(raw) <= MAX_MANIFEST, "manifest too large")
    require(not raw.startswith(b"\xef\xbb\xbf"), "manifest has a byte order mark")
    m = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object, parse_constant=no_constant)
    valid_unicode(m)
    require(isinstance(m, dict), "manifest must be an object")
    require(m.get("schema") in (PACK_SCHEMA, "guo/store-pack@2"), "unsupported pack schema")
    identifier(m.get("id"))
    version(m.get("version"))
    require(m.get("kind") in KINDS if m["schema"] == PACK_SCHEMA else m.get("kind") == "content", "unsupported pack kind")
    require(m.get("licence") in LICENCES, "licence is not allowed")
    for key in ("title", "author"):
        value = m.get(key)
        require(isinstance(value, str) and units(value) <= 200 and not any(ord(c) < 32 or ord(c) == 127 for c in value)
                and value.strip(), f"invalid {key}")
    require(type(m.get("min_profile_version")) is int and 0 <= m["min_profile_version"] <= 2147483647, "invalid min_profile_version")
    files = m.get("files")
    require(isinstance(files, dict) and 0 < len(files) <= 1024, "invalid files map")
    seen = set()
    for name, digest in files.items():
        safe_path(name)
        require(name.lower() != "manifest.json" and Path(name).suffix.lower() in EXTENSIONS, "unsupported payload type")
        require(not (fold_keys(name) & seen), "case-alias payload")
        seen |= fold_keys(name)
        require(isinstance(digest, str) and re.fullmatch("[0-9a-f]{64}", digest), "invalid SHA-256")
    for name in seen:
        require(not any("/".join(name.split("/")[:i]) in seen for i in range(1, len(name.split("/")))), "file/directory collision")
    require(isinstance(m.get("preview"), str) and m["preview"] in files and Path(m["preview"]).suffix.lower() in IMAGES, "preview must name a declared image")
    require(m["licence"] == "CC0-1.0" or "LICENSE.txt" in files, "attribution requires LICENSE.txt")
    scripts = [n for n in files if Path(n).suffix.lower() == ".razor"]
    if m["schema"] == "guo/store-pack@2":
        try:
            from .content import validate_content
        except ImportError:
            from content import validate_content
        validate_content(m, require, identifier, version)
        entries = {c["entry"] for c in m["components"] if c["type"] == "script"}
        require(all(Path(n).suffix.lower() == ".razor" for n in entries) and set(scripts) <= entries,
                "Every Razor payload must be a declared script component entry")
    else:
        require(bool(scripts) if m["kind"] == "razor-script" else not scripts,
                "Razor scripts require their own kind and at least one .razor file")
    # Screen-effect packs (ADR-0023): presets and shaders; shader code only in this kind.
    if m["kind"] == "postfx":
        require(any(Path(n).suffix.lower() == ".json" for n in files), "a postfx pack has at least one preset (.json)")
    else:
        require(not any(Path(n).suffix.lower() == ".gdshader" for n in files), "shader files are only allowed in a postfx pack")
    if m["kind"] == "screensaver":
        require(sum(Path(n).suffix.lower() == ".ogv" for n in files) == 1, "a screensaver has exactly one .ogv loop")
        require(m["min_profile_version"] >= SCREENSAVER_MIN_PROFILE, f"a screensaver needs min_profile_version {SCREENSAVER_MIN_PROFILE} or later")
    if m["schema"] == "guo/store-pack@2":
        try:
            from .content import validate_content
        except ImportError:
            from content import validate_content
        validate_content(m, require, identifier, version)
    return m


def script_text(raw):
    require(len(raw) <= MAX_SCRIPT_BYTES, "script exceeds byte limit")
    text = raw.decode("utf-8-sig", errors="strict")
    require(text.strip() and units(text) <= MAX_SCRIPT_CHARS, "script is empty or exceeds character limit")
    require(not any((ord(c) < 32 and c not in "\t\r\n") or ord(c) == 127 for c in text), "script contains control characters")
    return text


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
            require(not (fold_keys(item.filename) & names), "duplicate ZIP entry")
            names |= fold_keys(item.filename)
            mode = item.external_attr >> 16
            require(not item.is_dir() and not stat.S_ISLNK(mode) and stat.S_IFMT(mode) in {0, stat.S_IFREG}, "non-file ZIP entry")
            require(not item.flag_bits & 1, "encrypted ZIP entry")
            require(item.filename.isascii() or item.flag_bits & 0x800, "a non-ASCII entry name without the UTF-8 flag")
            require(item.compress_type in {zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED}, "unsupported ZIP compression")
            require(item.file_size <= (MAX_MANIFEST if item.filename == "manifest.json" else MAX_FILE), "entry too large")
            if Path(item.filename).suffix.lower() == ".razor":
                require(item.file_size <= MAX_SCRIPT_BYTES, "script exceeds byte limit")
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
            if Path(name).suffix.lower() == ".razor":
                script_text(archive.read(name))
    return manifest
