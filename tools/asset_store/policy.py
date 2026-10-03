"""Pack inspection and the automatic half of the content policy (docs/store/content_policy.md).

`inspect_pack` is what the editor's UO Store tab shows for a ZIP, and what
`run.py check` prints. It never writes anything. It reports:

- the manifest facts (kind, version, target, licence, components, dependencies),
- whether every dependency is published in a local store folder,
- a policy verdict: `refused`, `review` or `pass`.

The verdict is only the part a program can see: file types that are not what
their name says (an executable renamed .png), client file names, a title that
claims to be official Ultima Online content, scripts. Whether art started from
the client's pixels cannot be seen from the ZIP; the catalogue's reviewers
decide that, and `pass` says so.
"""
from __future__ import annotations

import json
import re
import zipfile
from pathlib import Path

try:
    from .pack import EXTENSIONS, IMAGES, require, sha256, verify
except ImportError:  # run as a script from this folder
    from pack import EXTENSIONS, IMAGES, require, sha256, verify

# Names of the client's own files. A payload called like one is a sign it is one.
CLIENT_STEMS = {
    "art", "artidx", "artlegacymul", "artlegacymul.uop", "gumpart", "gumpidx", "gumpartlegacymul", "tiledata",
    "cliloc", "hues", "radarcol", "texmaps", "texidx", "anim", "anim2", "anim3", "anim4", "anim5", "animdata",
    "animidx", "unifont", "fonts", "light", "lightidx", "multi", "multi.idx", "skills", "speech", "verdata",
    "sound", "soundidx", "soundlegacymul", "map0", "map1", "map2", "map3", "map4", "map5", "mapdif0", "staidx0",
    "statics0", "stadif0", "mapdifl0", "stadifi0", "bodyconv", "body", "mobtypes", "equipconv",
}
OFFICIAL = re.compile(r"official\s+(ultima\s+online|uo)\b|\b(electronic\s+arts|origin\s+systems|broadsword)\b", re.I)
MENTIONS = re.compile(r"ultima\s+online", re.I)

PNG = b"\x89PNG\r\n\x1a\n"


def magic_ok(name: str, data: bytes) -> bool:
    ext = Path(name).suffix.lower()
    if ext == ".png":
        return data.startswith(PNG)
    if ext in (".jpg", ".jpeg"):
        return data.startswith(b"\xff\xd8\xff")
    if ext == ".webp":
        return data[:4] == b"RIFF" and data[8:12] == b"WEBP"
    if ext in (".ogg", ".ogv"):
        return data.startswith(b"OggS")
    if ext == ".wav":
        return data[:4] == b"RIFF" and data[8:12] == b"WAVE"
    if ext == ".json":
        try:
            json.loads(data.decode("utf-8"))
            return True
        except (UnicodeDecodeError, ValueError):
            return False
    if ext in (".txt", ".razor", ".gdshader"):
        return b"\x00" not in data[:4096] and not data.startswith((b"MZ", b"\x7fELF", b"PK\x03\x04"))
    return True


def assess(path: Path, manifest: dict) -> dict:
    """The policy verdict for a pack ZIP whose manifest already verified."""
    findings: list[dict] = []

    def add(level: str, text: str) -> None:
        findings.append({"level": level, "text": text})

    for key in ("title", "author", "id"):
        value = str(manifest.get(key, ""))
        if OFFICIAL.search(value):
            add("refused", f"The {key} claims to be official Ultima Online content (Names rule): {value!r}")
        elif MENTIONS.search(value):
            add("review", f"The {key} mentions Ultima Online; a reviewer checks it does not imply the pack is official: {value!r}")
    with zipfile.ZipFile(path) as archive:
        for name in manifest["files"]:
            stem = Path(name).stem.lower()
            ext = Path(name).suffix.lower()
            if ext not in EXTENSIONS:
                add("refused", f"{name}: a file type packs may not carry")
                continue
            if stem in CLIENT_STEMS:
                add("review", f"{name}: named like one of the client's files; it must be your own work")
            size = archive.getinfo(name).file_size
            # A JSON file is parsed whole (up to 16 MB); anything else is judged by its first bytes.
            with archive.open(name) as stream:
                head = stream.read(16 * 1024 * 1024 if ext == ".json" else 4096)
            if (ext != ".json" or size <= 16 * 1024 * 1024) and not magic_ok(name, head):
                add("refused", f"{name}: the content is not a {ext} file, whatever its name says")
            if ext == ".razor":
                add("review", f"{name}: a script; a reviewer reads what it does before it is listed")
    if manifest.get("licence") != "CC0-1.0":
        add("info", f"Licence {manifest['licence']}: credit is in LICENSE.txt, which the catalogue shows")
    verdict = "refused" if any(f["level"] == "refused" for f in findings) else "review" if any(f["level"] == "review" for f in findings) else "pass"
    kinds_with_art = manifest.get("kind") != "razor-script"
    note = ("Checked here: file types, names, licence allowlist, hashes. Not checkable here: whether the art, sound or text "
            "started from the Ultima Online client's files. The catalogue's reviewers decide that from the listing's provenance line."
            if kinds_with_art else
            "Checked here: file types, names, licence. A reviewer reads every script before it is listed.")
    return {"verdict": verdict, "findings": findings, "note": note}


def published(store: Path | None, pack_id: str, ver: str) -> bool:
    return bool(store) and (Path(store) / "packs" / pack_id / f"{ver}.zip").is_file()


def inspect_pack(path: Path, store: Path | None = None) -> dict:
    """Everything the editor shows about a ZIP. Raises ValueError, zipfile.BadZipFile, OSError for a pack that does not verify."""
    path = Path(path)
    manifest = verify(path)
    deps = {}
    for dep, ver in (manifest.get("dependencies") or {}).items():
        deps[dep] = {"version": ver, "status": "published in the local store" if published(store, dep, ver) else "not in the local store"}
    return {
        "ok": True,
        "id": manifest["id"], "version": manifest["version"], "kind": manifest["kind"], "schema": manifest["schema"],
        "title": manifest["title"], "author": manifest["author"], "licence": manifest["licence"],
        "target": manifest.get("target", ""),
        "min_profile_version": manifest["min_profile_version"],
        "files": len(manifest["files"]), "size": path.stat().st_size, "sha256": sha256(path),
        "components": [{"id": c["id"], "type": c["type"], "target": c["target"]} for c in manifest.get("components", [])],
        "dependencies": deps,
        "hashes": "every payload matches its manifest hash",
        "policy": assess(path, manifest),
    }


def listing_for(path: Path, urls: list[str], provenance: str) -> tuple[dict, dict]:
    """The catalogue repository's packs/<id>/<version>.json for this ZIP, and the manifest facts."""
    try:
        from .catalogue import MAX_PROVENANCE, check_url
    except ImportError:
        from catalogue import MAX_PROVENANCE, check_url
    info = inspect_pack(path)
    require(info["policy"]["verdict"] != "refused", "The content policy refuses this pack: " + "; ".join(
        f["text"] for f in info["policy"]["findings"] if f["level"] == "refused"))
    require(1 <= len(urls) <= 16, "Give one to sixteen HTTPS addresses where the ZIP will be hosted")
    for url in urls:
        check_url(url)
        require(url.startswith("https://"), f"A public listing needs HTTPS addresses: {url}")
    require(0 < len(provenance.strip()) <= MAX_PROVENANCE and not any(ord(c) < 32 for c in provenance),
            f"Say how the content was made, in 1 to {MAX_PROVENANCE} characters")
    return {"urls": urls, "sha256": info["sha256"], "size": info["size"], "provenance": provenance.strip()}, info
