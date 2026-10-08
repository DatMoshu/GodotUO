"""Export an install's art to an extracted set, and verify a set against the install (data_formats section 36)."""

from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
import shutil
import sys
import time
from pathlib import Path

import art_atlas as atlas
import art_png as pngio
import art_sources as sources
from art_sources import CLASSES, Skip

TOOLS = Path(__file__).resolve().parent.parent
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

from guo import config as guo_config  # noqa: E402

TOOL = "art_extract 1"
SCHEMA_SET = "guo/art_set@1"
SCHEMA_INDEX = "guo/art_index@1"
MISMATCH_LIMIT = 10


class ArtExtractError(Exception):
    """A usage or data problem that should end the run with a message, not a traceback."""


# --- settings ----------------------------------------------------------------------------

def settings(root: Path | None = None) -> dict:
    """client_data, client_version and art_dir, resolved as every GUO tool does."""
    cfg = guo_config.load_config(root)
    values = {"UO_ROOT": str(cfg.root)}
    local = cfg.root / "launchers" / "_shared" / "config.local.bat"
    if local.is_file():
        values = guo_config.parse_config_bat(local, values)
    values = guo_config.parse_config_bat(cfg.root / "launchers" / "_shared" / "config.bat", values)
    raw = os.environ.get("UO_ART_EXTRACT_DIR") or values.get("UO_ART_EXTRACT_DIR") or ""
    raw = guo_config.native_path(os.path.expandvars(raw))
    art_dir = Path(raw) if raw and "%" not in raw else (cfg.workspace_dir / "art_extract")
    return {"root": cfg.root, "client_data": cfg.client_data, "client_version": cfg.client_version, "art_dir": art_dir}


def check_out(out: Path, art_dir: Path, root: Path) -> Path:
    """`--out` may only be the art folder itself, somewhere inside it, or inside the repository's build/."""
    out = Path(out).resolve()
    for allowed in (Path(art_dir).resolve(), (Path(root) / "build").resolve()):
        if out == allowed or allowed in out.parents:
            return out
    raise ArtExtractError(f"--out {out} is outside UO_ART_EXTRACT_DIR ({art_dir}) and build/")


# --- fingerprint -------------------------------------------------------------------------

def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def set_id_of(files: list[dict]) -> str:
    text = "".join(f"{f['name']}\t{f['size']}\t{f['sha256']}\n" for f in files)
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def fingerprint(data: Path, names: list[str]) -> dict:
    files = []
    for name in sorted(set(names)):
        p = Path(data) / name
        files.append({"name": name, "size": p.stat().st_size, "sha256": sha256_file(p)})
    return {"files": files, "set_id": set_id_of(files)}


def _generated(data: Path, names: list[str]) -> str:
    """Reproducible: the newest modification time among the source files, not the clock."""
    newest = max(int((Path(data) / n).stat().st_mtime) for n in names)
    return dt.datetime.fromtimestamp(newest, dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# --- export ------------------------------------------------------------------------------

def _dump(path: Path, doc: dict) -> None:
    path.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8", newline="\n")


def _clear(out: Path) -> None:
    """Remove what an earlier export left (set.json first, so a half-written set is never mistaken for a set)."""
    (out / "set.json").unlink(missing_ok=True)
    for c in CLASSES:
        d = out / c
        if d.is_dir():
            for f in list(d.glob("page_*.png")) + [d / "index.json"]:
                f.unlink(missing_ok=True)
            try:
                d.rmdir()
            except OSError:
                pass


def decode_class(src, log=None) -> tuple[dict[int, tuple[int, int, bytes]], list[dict]]:
    images, skipped = {}, []
    for i in src.ids():
        try:
            got = src.decode(i)
        except Skip as why:
            skipped.append({"id": i, "reason": str(why)[:200]})
            continue
        if got is not None:
            images[i] = got
    return images, skipped


def export(data: Path, out: Path, what: tuple[str, ...] = CLASSES, *, client_version: str = "0.0",
           generated: str | None = None, log=print) -> dict:
    """Write pages and indexes for `what` into `out`. Returns the set.json document."""
    data, out = Path(data), Path(out)
    srcs = sources.open_sources(data, what)
    names = sorted({n for s in srcs for n in s.files})
    out.mkdir(parents=True, exist_ok=True)
    _clear(out)
    log(f"fingerprinting {len(names)} source file(s)")
    fp = fingerprint(data, names)
    classes = {}
    for src in srcs:
        t0 = time.time()
        images, skipped = decode_class(src)
        placed, pages = atlas.pack(images)
        folder = out / src.cls
        folder.mkdir(parents=True, exist_ok=True)
        page_docs, total = [], 0
        for n, buf in enumerate(pages):
            png = pngio.encode_rgba(atlas.PAGE, atlas.PAGE, buf)
            name = f"page_{n:04d}.png"
            (folder / name).write_bytes(png)
            page_docs.append({"file": name, "sha256": hashlib.sha256(png).hexdigest()})
            total += len(png)
        _dump(folder / "index.json", {
            "schema": SCHEMA_INDEX, "version": 1, "class": src.cls, "set_id": fp["set_id"],
            "page_size": atlas.PAGE, "pixel_format": "rgba8", "pages": page_docs,
            "entries": {str(i): {"page": p.page, "x": p.x, "y": p.y, "w": p.w, "h": p.h,
                                 "pixels_sha256": p.pixels_sha256} for i, p in placed.items()},
            "skipped": skipped})
        classes[src.cls] = {"pages": len(pages), "count": len(placed), "skipped": len(skipped), "bytes": total}
        log(f"{src.cls:7} {len(placed):6} stored, {len(skipped):4} skipped, {len(pages):3} page(s), "
            f"{total / 1e6:8.1f} MB, {time.time() - t0:6.1f} s")
    doc = {"schema": SCHEMA_SET, "version": 1, "generated": generated or _generated(data, names), "tool": TOOL,
           "client_version": client_version, "fingerprint": fp, "classes": classes}
    _dump(out / "set.json", doc)
    return doc


# --- verify ------------------------------------------------------------------------------

def _schema_errors(doc: dict, schema_name: str) -> list[str]:
    try:
        import jsonschema
    except ImportError:
        return []
    schema = json.loads((Path(__file__).resolve().parent / "schema" / schema_name).read_text(encoding="utf-8"))
    return [f"{'/'.join(map(str, e.path)) or '<root>'}: {e.message}"
            for e in jsonschema.Draft202012Validator(schema).iter_errors(doc)][:MISMATCH_LIMIT]


def verify(data: Path, folder: Path, what: tuple[str, ...] | None = None, log=print) -> dict:
    """Compare every id of the set with the install's own decode. Returns {ok, classes, mismatches}."""
    data, folder = Path(data), Path(folder)
    problems: list[str] = []
    counts: dict[str, dict] = {}

    def bad(msg: str) -> None:
        problems.append(msg)

    try:
        set_doc = json.loads((folder / "set.json").read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        raise ArtExtractError(f"{folder}: no readable set.json ({e})") from e
    for e in _schema_errors(set_doc, "set.schema.json"):
        bad(f"set.json: {e}")
    fp = set_doc.get("fingerprint", {})
    if set_id_of(fp.get("files", [])) != fp.get("set_id"):
        bad("set.json: fingerprint.set_id does not match its files")
    for f in fp.get("files", []):
        p = data / f["name"]
        if not p.is_file():
            bad(f"source file {f['name']} is missing from the install")
        elif p.stat().st_size != f["size"] or sha256_file(p) != f["sha256"]:
            bad(f"source file {f['name']} differs from the one the set was made from")
    classes = [c for c in CLASSES if c in set_doc.get("classes", {}) and (what is None or c in what)]
    for src in sources.open_sources(data, tuple(classes)):
        c = src.cls
        stat = counts[c] = {"stored": 0, "skipped": 0, "absent": 0, "ok": 0, "bad": 0}
        before = len(problems)
        _verify_class(src, folder / c, fp.get("set_id"), set_doc["classes"][c], stat, bad)
        log(f"{c:7} {stat['ok']:6} of {stat['stored']} match, {stat['skipped']} skipped, "
            f"{stat['absent']} not in the install, {len(problems) - before} problem(s)")
    ok = not problems
    for m in problems[:MISMATCH_LIMIT]:
        log(f"  MISMATCH {m}")
    if len(problems) > MISMATCH_LIMIT:
        log(f"  ... and {len(problems) - MISMATCH_LIMIT} more")
    return {"ok": ok, "classes": counts, "mismatches": problems}


def _verify_class(src, folder: Path, set_id: str, summary: dict, stat: dict, bad) -> None:
    c = src.cls
    try:
        index = json.loads((folder / "index.json").read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        bad(f"{c}/index.json unreadable ({e})")
        return
    for e in _schema_errors(index, "index.schema.json"):
        bad(f"{c}/index.json: {e}")
    if index.get("set_id") != set_id:
        bad(f"{c}/index.json: set_id differs from set.json")
    if (index.get("class"), index.get("page_size")) != (c, atlas.PAGE):
        bad(f"{c}/index.json: wrong class or page size")
    entries, skipped = index.get("entries", {}), {s["id"] for s in index.get("skipped", [])}
    if (summary.get("count"), summary.get("skipped"), summary.get("pages")) != \
            (len(entries), len(skipped), len(index.get("pages", []))):
        bad(f"{c}: set.json summary disagrees with index.json")
    for n, page in enumerate(index.get("pages", [])):
        p = folder / page["file"]
        if page["file"] != f"page_{n:04d}.png":
            bad(f"{c}: page {n} is named {page['file']}")
        if not p.is_file() or hashlib.sha256(p.read_bytes()).hexdigest() != page["sha256"]:
            bad(f"{c}/{page['file']} is missing or damaged")

    seen = set()
    by_page: dict[int, list[tuple[int, dict]]] = {}
    for i in src.ids():
        try:
            got = src.decode(i)
        except Skip:
            stat["skipped"] += 1
            if i not in skipped:
                bad(f"{c} {i}: cannot be decoded but is not listed as skipped")
            continue
        entry = entries.get(str(i))
        if got is None:
            stat["absent"] += 1
            if entry is not None:
                bad(f"{c} {i}: stored, but the install has no such image")
            continue
        stat["stored"] += 1
        w, h, rgba = got
        seen.add(str(i))
        if entry is None:
            bad(f"{c} {i}: in the install but missing from the set")
            stat["bad"] += 1
            continue
        if (entry["w"], entry["h"]) != (w, h) or entry["pixels_sha256"] != atlas.pixels_digest(w, h, rgba):
            bad(f"{c} {i}: index says {entry['w']}x{entry['h']} {entry['pixels_sha256'][:12]}, "
                f"install decodes to {w}x{h} {atlas.pixels_digest(w, h, rgba)[:12]}")
            stat["bad"] += 1
            continue
        by_page.setdefault(entry["page"], []).append((i, entry))
    for extra in sorted(set(entries) - seen, key=int):
        bad(f"{c} {extra}: in the set, but the install does not provide it")
        stat["bad"] += 1

    for n, listed in sorted(by_page.items()):
        try:
            png = (folder / index["pages"][n]["file"]).read_bytes()
            w, h, pixels = pngio.decode_rgba(png)
            if (w, h) != (atlas.PAGE, atlas.PAGE):
                raise ValueError(f"page is {w}x{h}")
        except (OSError, ValueError, IndexError) as e:
            bad(f"{c} page {n}: {e}")
            stat["bad"] += len(listed)
            continue
        for i, e in listed:
            if e["x"] + e["w"] > atlas.PAGE or e["y"] + e["h"] > atlas.PAGE:
                bad(f"{c} {i}: rectangle leaves the page")
                stat["bad"] += 1
                continue
            crop = atlas.crop(pixels, e["x"], e["y"], e["w"], e["h"])
            if atlas.pixels_digest(e["w"], e["h"], crop) != e["pixels_sha256"]:
                bad(f"{c} {i}: page {n} rectangle ({e['x']},{e['y']}) {e['w']}x{e['h']} does not hold the pixels")
                stat["bad"] += 1
            else:
                stat["ok"] += 1
