"""F03a: inspect a PNG folder, then prepare one independently verified stage batch."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import replacement
import run as parity


def filename_id(stem: str) -> int:
    """Decimal IDs or explicit 0x hexadecimal, with GUO's optional static_ prefix."""
    match = re.fullmatch(r"(?:static_)?(0x[0-9a-fA-F]{1,4}|[0-9]{1,5})", stem, re.IGNORECASE)
    if not match:
        raise ValueError("Use decimal IDs or 0x hexadecimal (for example 26.png or static_0x001A.png)")
    value = match[1]
    ident = int(value, 16 if value.lower().startswith("0x") else 10)
    if ident > 0xffff:
        raise ValueError("Static ID is outside 0x0000..0xFFFF")
    return ident


def scan(folder: Path, source: Path, stage: Path) -> dict:
    if folder.is_symlink() or (hasattr(folder, "is_junction") and folder.is_junction()):
        raise ValueError("Linked input folders are refused")
    folder, source, stage = (p.resolve() for p in (folder, source, stage))
    replacement.disjoint(folder, source, stage)
    parity.protect_install(stage)
    if not folder.is_dir():
        raise ValueError("Choose an existing PNG folder")
    if (source / "verdata.mul").exists():
        raise parity.Unsupported("verdata installs require separate source-layer certification")
    packaging = "uop" if (source / "artLegacyMUL.uop").exists() else "mul"
    # Match prepare's effective input: staged art when present, otherwise source.
    names = ["artLegacyMUL.uop"] if packaging == "uop" else ["art.mul", "artidx.mul"]
    if stage.exists():
        replacement.stage_hashes(stage)
        metadata = json.loads(parity.bounded(stage / "stage.json", 1024 * 1024))
        if Path(metadata["install"]).resolve() != source:
            raise ValueError("Stage belongs to another source")
        if any((stage / name).exists() for name in names) and not all((stage / name).is_file() for name in names):
            raise ValueError("Stage has incomplete art containers")
    root = stage if all((stage / name).is_file() for name in names) else source
    occupied, _ = parity.records(root, packaging)
    try:
        from PIL import Image
    except ImportError as e:
        raise RuntimeError("PNG support is missing. Run " + parity.DOCTOR) from e
    rows, errors, ignored = [], [], 0
    seen, pixels = {}, 0
    for path in sorted(folder.iterdir(), key=lambda p: p.name.lower()):
        if path.suffix.lower() != ".png":
            ignored += 1
            continue
        if len(rows) >= parity.MAX_RECORDS:
            errors.append("A batch contains at most 256 PNGs; split the folder")
            break
        row = {"file": path.name, "path": str(path), "id": None, "error": ""}
        try:
            if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()) or not path.is_file() or path.stat().st_nlink > 1:
                raise ValueError("Linked, hardlinked or non-file PNG input refused")
            ident = filename_id(path.stem)
            row["id"] = ident
            if ident in seen:
                raise ValueError(f"Duplicate ID 0x{ident:04X}; also used by {seen[ident]}")
            seen[ident] = path.name
            if ident + 0x4000 not in occupied:
                raise ValueError("Empty or absent static ID; this workflow replaces occupied records only")
            before = parity.digest(path)
            with Image.open(path) as image:
                if image.format != "PNG" or not (0 < image.width <= 1024 and 0 < image.height <= 1024):
                    raise ValueError("Expected a PNG no larger than 1024x1024")
                row.update(width=image.width, height=image.height)
                pixels += image.width * image.height
                image.verify()
            if before != parity.digest(path):
                raise ValueError("Image changed during scan; scan again")
            row.update(sha256=before, action="Replace occupied static")
        except (OSError, ValueError) as e:
            row["error"] = str(e)
            errors.append(f"{path.name}: {e}")
        rows.append(row)
    if not rows:
        errors.append("No PNGs found in the selected folder (subfolders are not scanned)")
    if pixels > 1_000_000:
        errors.append("Batch exceeds 1,000,000 pixels; split the folder")
    return {"schema": 1, "folder": str(folder), "source": str(source), "stage": str(stage),
        "packaging": packaging, "rows": rows, "errors": errors, "ignored": ignored,
        "count": len(rows), "pixels": pixels, "ready": not errors}


def prepare(scan_file: Path, plan: Path, version: str, oracle: Path) -> dict:
    preview = json.loads(parity.bounded(scan_file, 1024 * 1024))
    if preview.get("schema") != 1 or not preview.get("ready"):
        raise ValueError("Expected an error-free folder preview")
    current = scan(Path(preview["folder"]), Path(preview["source"]), Path(preview["stage"]))
    if current != preview:
        raise ValueError("Folder, IDs or images changed since preview; scan again")
    return replacement.prepare_many(Path(current["source"]), Path(current["stage"]),
        [(row["id"], Path(row["path"])) for row in current["rows"]], plan, version, oracle)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    view = sub.add_parser("scan")
    for name in ("folder", "source", "stage"):
        view.add_argument("--" + name, type=Path, required=True)
    prep = sub.add_parser("prepare")
    prep.add_argument("--scan", type=Path, required=True)
    prep.add_argument("--plan", type=Path, required=True)
    prep.add_argument("--client-version", required=True)
    prep.add_argument("--oracle", type=Path, default=parity.default_oracle())
    args = parser.parse_args()
    try:
        result = scan(args.folder, args.source, args.stage) if args.command == "scan" else prepare(args.scan, args.plan, args.client_version, args.oracle)
        print(json.dumps(result))
        return 0
    except (OSError, ValueError, RuntimeError, KeyError) as e:
        print(f"[fiddler-batch] {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
