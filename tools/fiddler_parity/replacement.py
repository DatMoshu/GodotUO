"""Prepare, independently verify and promote one occupied static replacement."""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run as parity
from guo import uoart
from uodata_write import uodata as writer


def disjoint(*paths: Path) -> None:
    for n, a in enumerate(paths):
        for b in paths[n + 1:]:
            if parity.overlap(a, b):
                raise ValueError("Source, stage, image and plan must be separate, non-nested paths")


def checked_stage(path: Path) -> Path:
    """Keep the reviewed destination spelling; never hide a replaced directory link."""
    absolute = Path(os.path.abspath(path))
    if (absolute.resolve() != absolute or absolute.is_symlink()
        or (hasattr(absolute, "is_junction") and absolute.is_junction())):
        raise ValueError("Linked stage destination refused; prepare a fresh review")
    return absolute


def json_write(path: Path, data: dict) -> None:
    if path.exists() and (path.is_symlink() or path.stat().st_nlink > 1):
        raise ValueError("Aliased output is refused")
    temporary = path.with_name(path.name + ".tmp-" + uuid.uuid4().hex)
    with temporary.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(data, indent=2) + "\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def stage_hashes(stage: Path) -> dict:
    if not stage.exists():
        return {}
    if stage.resolve() != stage or stage.is_symlink() or (hasattr(stage, "is_junction") and stage.is_junction()):
        raise ValueError("Aliased stage root refused")
    hashes = {}
    for path in stage.rglob("*"):
        if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()):
            raise ValueError("Linked stage entries are refused")
        if path.is_file():
            if path.stat().st_nlink > 1:
                raise ValueError("Hardlinked stage entries are refused")
            hashes[path.relative_to(stage).as_posix()] = parity.digest(path)
    return hashes


@contextmanager
def stage_lock(stage: Path):
    """OS-held lock releases on process death; no stale lock-directory recovery."""
    stage.parent.mkdir(parents=True, exist_ok=True)
    path = stage.with_name(stage.name + ".fiddler-lock")
    if path.exists() and (path.is_symlink() or path.stat().st_nlink > 1):
        raise ValueError("Aliased stage lock refused")
    with path.open("a+b") as stream:
        stream.seek(0, 2)
        if stream.tell() == 0:
            stream.write(b"0")
            stream.flush()
        stream.seek(0)
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream, fcntl.LOCK_UN)


def prepare(source: Path, stage: Path, image: Path, ident: int, plan: Path, version: str, oracle: Path) -> dict:
    return prepare_many(source, stage, [(ident, image)], plan, version, oracle)


def prepare_many(source: Path, stage: Path, images: list[tuple[int, Path]], plan: Path,
                 version: str, oracle: Path) -> dict:
    """One verified candidate and one promotion for the entire selected batch."""
    stage = checked_stage(stage)
    source, plan = source.resolve(), plan.resolve()
    images = [(ident, image.resolve()) for ident, image in images]
    disjoint(source, stage, plan, *(image for _, image in images))
    parity.protect_install(stage, plan)
    if not 0 < len(images) <= parity.MAX_RECORDS or len({ident for ident, _ in images}) != len(images):
        raise ValueError("Expected 1..256 distinct static IDs")
    if any(not 0 <= ident < 0x10000 for ident, _ in images):
        raise ValueError("Expected static IDs from 0x0000 to 0xFFFF")
    if plan.exists():
        raise ValueError("Plan folder must be new; existing plans are never overwritten")
    if (source / "verdata.mul").exists():
        raise parity.Unsupported("verdata installs require separate source-layer certification")
    packaging = "uop" if (source / "artLegacyMUL.uop").exists() else "mul"
    _, source_hashes = parity.records(source, packaging)
    names = list(source_hashes)
    prior = stage_hashes(stage)
    if stage.exists() and not prior:
        raise ValueError("Existing empty stage is refused; choose a new stage path")
    if prior:
        metadata = json.loads((stage / "stage.json").read_text(encoding="utf-8"))
        if Path(metadata["install"]).resolve() != source:
            raise ValueError("Stage belongs to another source")
    # Existing stage may contain unrelated files. Preserve all of them, but
    # read missing art containers directly from the explicit source.
    effective = {}
    for name in names:
        path = stage / name if (stage / name).is_file() else source / name
        effective[name] = path
    # Read-only install files may be hardlinked to a user's backups. Copy bytes
    # into fresh, unlinked baseline/candidate files; never use those links as
    # stage outputs. A hardlinked staged override is still refused.
    if any(path.is_symlink() or (path.parent != source and path.stat().st_nlink > 1) for path in effective.values()):
        raise ValueError("Aliased art containers are refused")
    try:
        from PIL import Image
    except ImportError as e:
        raise RuntimeError("PNG support is missing. Run " + parity.DOCTOR) from e
    prepared = []
    pixel_count = 0
    for ident, image in images:
        if image.is_symlink() or image.stat().st_nlink > 1:
            raise ValueError("Aliased replacement image refused")
        before = parity.digest(image)
        with Image.open(image) as img:
            if img.format != "PNG" or not (0 < img.width <= 1024 and 0 < img.height <= 1024):
                raise ValueError("Choose PNGs no larger than 1024x1024")
        width, height, pixels = uoart.png_pixels(image, land=False)
        pixel_count += width * height
        if len(images) > 1 and pixel_count > 1_000_000:
            raise ValueError("Batch exceeds 1,000,000 pixels; split the folder")
        if before != parity.digest(image):
            raise ValueError("Image changed while preparing; scan again")
        prepared.append((ident, image, before, width, height, pixels))
    # Quantized expected pixels derive from the intended PNG, not a decode of
    # our encoder. Expand 5-bit channels with the upstream lookup convention.
    def expand(c):
        return c * 255 // 31
    plan.mkdir(parents=True)
    baseline = plan / "baseline"
    baseline.mkdir()
    for name, path in effective.items():
        shutil.copyfile(path, baseline / name)
    existing, _ = parity.records(baseline, packaging)
    if any(ident + 0x4000 not in existing for ident, _ in images):
        raise ValueError("Choose occupied static records; adding IDs is a separate workflow")
    candidate = plan / "candidate"
    if prior:
        shutil.copytree(stage, candidate)
    else:
        candidate.mkdir()
    staged = writer.Stage(candidate, source)
    for name, path in effective.items():
        shutil.copyfile(path, candidate / name)
        staged.meta["files"][name.lower()] = {"name": name,
            "source_sha1": writer.sha1(source / name), "source_size": (source / name).stat().st_size}
    staged.save()
    records, payloads = [], {}
    for ident, image, image_hash, width, height, pixels in prepared:
        encoded = existing[ident + 0x4000][:4] + uoart.encode_static(pixels, width, height)[4:]
        parity.check_art(encoded)
        payloads[ident] = encoded
        rgba = [0 if value == 0 else (expand((value >> 10) & 31) | expand((value >> 5) & 31) << 8
            | expand(value & 31) << 16 | 0xff000000) for value in pixels]
        records.append({"id": ident, "width": width, "height": height, "rgba": rgba,
            "raw_sha256": hashlib.sha256(encoded).hexdigest()})
    writer.replace_static_records(staged, payloads)
    expected = {"schema": 1, "packaging": packaging, "client_version": version,
        "records": records}
    json_write(plan / "expected.json", expected)
    report = parity.verify(candidate, baseline, plan / "expected.json", plan / "verification", oracle)
    if report["outcome"] != "pass":
        raise ValueError("Candidate verification failed; see plan/verification/report.json")
    # Stage metadata is generated again after promotion so its override paths
    # point to the final destination. Everything else is retained byte-for-byte.
    result = {"schema": 1, "state": "prepared", "id": ident, "width": width, "height": height,
        "source": str(source), "stage": str(stage), "plan": str(plan), "packaging": packaging,
        "source_hashes": source_hashes, "prior_hashes": prior, "candidate_hashes": stage_hashes(candidate),
        "image_sha256": image_hash, "image": str(image), "client_version": version,
        "images": [{"id": ident, "image": str(image), "sha256": image_hash}
            for ident, image, image_hash, _, _, _ in prepared],
        "count": len(images)}
    json_write(plan / "plan.json", result)
    return result


def load_plan(path: Path) -> tuple[dict, Path, Path, Path]:
    path = path.resolve()
    spec = json.loads(parity.bounded(path, 16 * 1024 * 1024))
    if spec.get("schema") != 1 or spec.get("state") != "prepared" or Path(spec["plan"]).resolve() != path.parent:
        raise ValueError("Expected a prepared plan in its original folder")
    source, stage = Path(spec["source"]).resolve(), checked_stage(Path(spec["stage"]))
    disjoint(source, stage, path.parent)
    parity.protect_install(stage, path.parent)
    return spec, source, stage, path.parent


def _apply(path: Path, oracle: Path) -> dict:
    spec, source, stage, plan = load_plan(path)
    candidate = plan / "candidate"
    if stage_hashes(stage) != spec["prior_hashes"] or stage_hashes(candidate) != spec["candidate_hashes"]:
        raise ValueError("Stage/candidate changed since preview; prepare again")
    for image in spec.get("images", [{"image": spec["image"], "sha256": spec["image_sha256"]}]):
        if parity.digest(Path(image["image"])) != image["sha256"]:
            raise ValueError("Replacement image changed since preview; prepare again")
    for name, wanted in spec["source_hashes"].items():
        if parity.digest(source / name) != wanted:
            raise ValueError("Source changed since preview; prepare again")
    report = parity.verify(candidate, plan / "baseline", plan / "expected.json", plan / "recheck", oracle)
    if report["outcome"] != "pass":
        raise ValueError("Independent recheck failed; current stage is unchanged")
    return promote(spec, source, stage, plan)


def promote(spec: dict, source: Path, stage: Path, plan: Path) -> dict:
    """Shared promotion after a domain adapter's locked, independent recheck."""
    candidate = plan / "candidate"
    stage.parent.mkdir(parents=True, exist_ok=True)
    journal = stage.with_name(stage.name + ".fiddler-journal.json")
    if journal.exists():
        previous = json.loads(parity.bounded(journal, 1024 * 1024))
        if previous.get("state") not in ("complete", "recovered"):
            raise ValueError("An unfinished promotion exists; recover it before applying another plan")
        journal.rename(journal.with_name(journal.name + ".history-" + uuid.uuid4().hex))
    backup = stage.with_name(stage.name + ".previous-" + uuid.uuid4().hex)
    entry = {"schema": 1, "stage": str(stage), "backup": str(backup), "plan": str(plan),
        "source": str(source), "prior_exists": stage.exists(), "state": "promoting"}
    json_write(journal, entry)
    try:
        if stage.exists():
            stage.rename(backup)
        candidate.rename(stage)
        writer.Stage(stage, source).save()
    except BaseException:
        # Preserve even a partially promoted candidate for diagnosis.
        if stage.exists() and (backup.exists() or not entry["prior_exists"]):
            stage.rename(stage.with_name(stage.name + ".failed-" + uuid.uuid4().hex))
        if backup.exists():
            backup.rename(stage)
        entry["state"] = "recovered"
        json_write(journal, entry)
        raise
    entry["state"] = "complete"
    json_write(journal, entry)
    spec["state"] = "applied"
    json_write(plan / "plan.json", spec)
    return entry


def apply(path: Path, oracle: Path) -> dict:
    _, _, stage, _ = load_plan(path)
    with stage_lock(stage):
        return _apply(path, oracle)


def _recover(stage: Path) -> dict:
    stage = checked_stage(stage)
    parity.protect_install(stage)
    journal = stage.with_name(stage.name + ".fiddler-journal.json")
    if journal.is_symlink() or journal.stat().st_nlink > 1:
        raise ValueError("Aliased recovery journal refused")
    entry = json.loads(parity.bounded(journal, 1024 * 1024))
    if entry.get("schema") != 1 or entry.get("state") not in ("promoting", "complete", "recovered"):
        raise ValueError("Invalid recovery journal schema/state")
    if entry.get("state") == "recovered":
        raise ValueError("This promotion has already been recovered")
    backup = Path(entry["backup"])
    parity.protect_install(backup)
    if entry.get("source") and (parity.overlap(Path(entry["source"]).resolve(), backup.resolve())
        or parity.overlap(Path(entry["source"]).resolve(), stage)):
        raise ValueError("Recovery would move or replace the source installation")
    if (Path(entry["stage"]).resolve() != stage or backup.parent.resolve() != stage.parent
        or not backup.name.startswith(stage.name + ".previous-") or backup.resolve() != backup or backup.is_symlink()
        or (hasattr(backup, "is_junction") and backup.is_junction())):
        raise ValueError("Invalid recovery journal")
    if not backup.is_dir():
        if entry.get("prior_exists", True):
            raise ValueError("Prior stage is unavailable; inspect the journal before continuing")
        if stage.exists():
            stage.rename(stage.with_name(stage.name + ".recovered-candidate-" + uuid.uuid4().hex))
        entry["state"] = "recovered"
        json_write(journal, entry)
        return entry
    if stage.exists():
        stage.rename(stage.with_name(stage.name + ".recovered-candidate-" + uuid.uuid4().hex))
    backup.rename(stage)
    entry["state"] = "recovered"
    json_write(journal, entry)
    return entry


def recover(stage: Path) -> dict:
    parity.protect_install(stage)
    stage = checked_stage(stage)
    with stage_lock(stage):
        return _recover(stage)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    prep = sub.add_parser("prepare")
    for name in ("source", "stage", "image", "plan"):
        prep.add_argument("--" + name, type=Path, required=True)
    prep.add_argument("--id", type=lambda value: int(value, 0), required=True)
    prep.add_argument("--client-version", required=True)
    commit = sub.add_parser("apply")
    commit.add_argument("--plan", type=Path, required=True)
    commit.add_argument("--confirm", action="store_true", required=True)
    restore = sub.add_parser("recover")
    restore.add_argument("--stage", type=Path, required=True)
    restore.add_argument("--confirm", action="store_true", required=True)
    for item in (prep, commit):
        item.add_argument("--oracle", type=Path, default=parity.default_oracle())
    args = parser.parse_args()
    try:
        if args.command == "prepare":
            result = prepare(args.source, args.stage, args.image, args.id, args.plan, args.client_version, args.oracle)
        elif args.command == "apply":
            result = apply(args.plan, args.oracle)
        else:
            result = recover(args.stage)
        print(json.dumps(result))
        return 0
    except (OSError, ValueError, RuntimeError, KeyError) as e:
        print(f"[fiddler-parity] {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
