"""F06b: reviewed modern item metadata, native verification and recoverable staging."""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
import shutil
import struct
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hues
import replacement as transaction
import run as parity
from guo.uorecord import AssetRecord
from uodata_write import uodata as writer

MAX_BYTES = writer.TILE_LAND_BYTES + 2048 * writer.TILE_STATIC_BLOCK


def file(root: Path, name="tiledata.mul", optional=False) -> Path | None:
    matches = [p for p in root.iterdir() if p.name.lower() == name.lower()]
    if not matches and optional: return None
    if len(matches) != 1 or not matches[0].is_file() or matches[0].is_symlink():
        raise ValueError(f"Expected one regular {name}")
    return matches[0]


def source_hashes(root: Path) -> dict:
    return {name: parity.digest(path) if (path := file(root, name, name == "verdata.mul")) else None
            for name in ("tiledata.mul", "verdata.mul")}


def data(path: Path) -> bytes:
    value = hues.read(path, MAX_BYTES)
    writer.check_tiledata(path)
    return value


def offset(ident: int, value: bytes) -> int:
    if type(ident) is not int or not 0 <= ident < min(0x10000, (len(value) - writer.TILE_LAND_BYTES) // writer.TILE_STATIC_BLOCK * 32):
        raise ValueError("Item ID must be inside existing modern tiledata")
    return writer.tile_offset(ident)


def raw(text: str) -> bytes:
    value = base64.b64decode(text, validate=True)
    if len(value) != 41: raise ValueError("Item metadata must contain exactly 41 native bytes")
    return value


def guard_patch(path: Path | None, ident: int) -> None:
    """An unrelated art patch is preserved; a winning metadata patch cannot be silently ignored."""
    if path is None: return
    size = path.stat().st_size
    if size == 0: return
    with path.open("rb") as stream:
        header = stream.read(4)
        if len(header) != 4: raise ValueError("Truncated verdata header")
        count = struct.unpack("<i", header)[0]
        if not 0 <= count <= 1000000 or 4 + count * 20 > size:
            raise ValueError("Invalid bounded verdata index")
        for _ in range(count):
            kind, block, position, length, extra = struct.unpack("<5i", stream.read(20))
            if kind == 30 and length == 1188 and block == 0x200 + ident // 32:
                raise ValueError("A verdata tiledata patch overrides this item group; resolve that source layer before native tiledata export")


def fields(payload: bytes) -> dict:
    values = struct.unpack("<QBBiHHHB", payload[:21])
    return dict(zip(("flags", "weight", "layer", "count", "anim", "hue", "light", "height"), values),
        name=payload[21:].decode("utf-8", errors="replace").strip("\0"))


def verify(baseline: Path, candidate: Path, expected: Path, out: Path, oracle: Path) -> dict:
    baseline, expected = baseline.resolve(), expected.resolve()
    candidate, out = hues.output(candidate), hues.output(out)
    transaction.disjoint(baseline, candidate, expected, out)
    paths = [file(baseline), file(candidate), expected]
    patch = file(baseline, "verdata.mul", True)
    if patch: paths.append(patch)
    if paths[1].stat().st_nlink > 1: raise ValueError("Hardlinked metadata candidate refused")
    hashes = {str(path): parity.digest(path) for path in paths}
    report = {"schema": 1, "domain": "item-metadata", "outcome": "error", "input_sha256": hashes}
    try:
        before, after = data(paths[0]), data(paths[1])
        spec = json.loads(hues.read(expected, 128 * 1024))
        if spec.get("schema") != 1 or spec.get("domain") != "item-metadata":
            raise ValueError("Expected an item metadata verification manifest")
        ident, payload = spec["id"], raw(spec["record_base64"])
        at = offset(ident, before); guard_patch(patch, ident)
        if payload[8:20] != before[at + 8:at + 20]:
            raise ValueError("Unexposed staged item fields changed")
        intended = bytearray(before); intended[at:at + 41] = payload
        if after != intended:
            raise ValueError("Tiledata differs from the intended item or changes untouched records/headers/length")
        proof = parity.invoke(oracle, [str(candidate), "tiledata-item", spec["client_version"], str(ident)])
        rows = proof["records"]
        if len(rows) != 1 or rows[0]["id"] != ident:
            raise ValueError("Independent metadata reader returned another item")
        row = rows[0]
        if raw(row["raw_base64"]) != payload or row["raw_sha256"] != hashlib.sha256(payload).hexdigest() or any(row[k] != v for k, v in fields(payload).items()):
            raise ValueError("Independent metadata fields or raw bytes differ")
        report.update(outcome="pass", id=ident, raw_sha256=row["raw_sha256"], fields=fields(payload),
            preserved_bytes=len(before) - 41, preserved_item_records=(len(before) - writer.TILE_LAND_BYTES) // writer.TILE_STATIC_BLOCK * 32 - 1,
            preserved_group_headers=512 + (len(before) - writer.TILE_LAND_BYTES) // writer.TILE_STATIC_BLOCK,
            oracle={"identity": proof["identity"], "runtime": proof["runtime"], "binary_sha256": parity.digest(oracle)})
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        report["error"] = str(error)
    report["inputs_unchanged"] = all(path.is_file() and parity.digest(path) == hashes[str(path)] for path in paths)
    if not report["inputs_unchanged"]: report.update(outcome="error", error="Metadata verification inputs changed")
    out.mkdir(parents=True, exist_ok=True); transaction.json_write(out / "report.json", report)
    return report


def prepare(source: Path, stage: Path, edits: Path, plan: Path, version: str, oracle: Path) -> dict:
    source, edits = source.resolve(), edits.resolve()
    stage, plan = hues.output(stage), hues.output(plan)
    transaction.disjoint(source, stage, edits, plan)
    if plan.exists(): raise ValueError("Metadata plan folder must be new")
    source_path = file(source); original = data(source_path)
    original_hashes, edit_hash = source_hashes(source), parity.digest(edits)
    if hashlib.sha256(original).hexdigest() != original_hashes["tiledata.mul"]:
        raise ValueError("Metadata source changed while capturing the review")
    edit = json.loads(hues.read(edits, 128 * 1024))
    if edit.get("schema") != 1 or edit.get("domain") != "item-metadata" or len(edit.get("records", [])) != 1:
        raise ValueError("Expected one reviewed item metadata record")
    row = edit["records"][0]; ident = row["id"]; at = offset(ident, original)
    source_record, desired = raw(row["source_record_base64"]), raw(row["record_base64"])
    if source_record != original[at:at + 41]: raise ValueError("Metadata document belongs to another source; reopen it")
    if desired[8:20] != source_record[8:20]: raise ValueError("This document may edit only flags, height and name")
    if desired[21:] != source_record[21:]:
        name = desired[21:].split(b"\0", 1)[0]
        name.decode("utf-8", errors="strict")
        if desired[21:] != name.ljust(20, b"\0"):
            raise ValueError("Changed item names must be UTF-8 with zero padding")
    guard_patch(file(source, "verdata.mul", True), ident)
    prior = transaction.stage_hashes(stage)
    if stage.exists():
        if not prior: raise ValueError("Existing empty stage refused")
        meta = json.loads(hues.read(stage / "stage.json", 1024 * 1024))
        if Path(meta["install"]).resolve() != source: raise ValueError("Stage belongs to another source")
    plan.mkdir(parents=True)
    baseline, candidate = plan / "baseline", plan / "candidate"; baseline.mkdir()
    if prior: shutil.copytree(stage, candidate)
    else: candidate.mkdir()
    staged = writer.Stage(candidate, source)
    effective = staged.read_path("tiledata.mul"); current = data(effective)
    if len(current) != len(original): raise ValueError("Stage and source tiledata layouts differ")
    shutil.copyfile(effective, baseline / "tiledata.mul")
    try: patch = staged.read_path("verdata.mul")
    except FileNotFoundError: patch = None
    guard_patch(patch, ident)
    if patch:
        if patch.stat().st_size > parity.MAX_FILE: raise ValueError("Metadata patch context exceeds verification budget")
        shutil.copyfile(patch, baseline / "verdata.mul")
    before = current[at:at + 41]
    merged = bytearray(before); merged[:8] = desired[:8]; merged[20:] = desired[20:]
    payload = bytes(merged)
    writer.write_records(staged, [AssetRecord("tiledata-item", ident, payload)], replace=True)
    expected = {"schema": 1, "domain": "item-metadata", "id": ident, "client_version": version,
        "record_base64": base64.b64encode(payload).decode()}
    transaction.json_write(plan / "expected.json", expected)
    report = verify(baseline, candidate, plan / "expected.json", plan / "verification", oracle)
    if report["outcome"] != "pass": raise ValueError("Independent metadata verification failed; see plan/verification/report.json")
    if source_hashes(source) != original_hashes or parity.digest(edits) != edit_hash or transaction.stage_hashes(stage) != prior:
        raise ValueError("Metadata source, edits or stage changed during preparation")
    spec = {"schema": 1, "domain": "item-metadata", "state": "prepared", "source": str(source), "stage": str(stage),
        "plan": str(plan), "source_hashes": original_hashes, "prior_hashes": prior,
        "candidate_hashes": transaction.stage_hashes(candidate), "baseline_hashes": transaction.stage_hashes(baseline),
        "expected_sha256": parity.digest(plan / "expected.json"), "edit_file": str(edits), "edit_sha256": edit_hash,
        "client_version": version, "id": ident,
        "records": [{"id": ident, "before_base64": base64.b64encode(before).decode(), "after_base64": base64.b64encode(payload).decode()}]}
    transaction.json_write(plan / "plan.json", spec); return spec


def apply(path: Path, oracle: Path) -> dict:
    spec, source, stage, plan = transaction.load_plan(path)
    if spec.get("domain") != "item-metadata": raise ValueError("Expected a reviewed item metadata plan")
    hues.output(Path(spec["stage"])); hues.output(plan)
    with transaction.stage_lock(stage):
        if (transaction.stage_hashes(stage) != spec["prior_hashes"] or transaction.stage_hashes(plan / "candidate") != spec["candidate_hashes"]
            or transaction.stage_hashes(plan / "baseline") != spec["baseline_hashes"] or source_hashes(source) != spec["source_hashes"]
            or parity.digest(Path(spec["edit_file"])) != spec["edit_sha256"] or parity.digest(plan / "expected.json") != spec["expected_sha256"]):
            raise ValueError("Metadata review inputs changed; prepare again")
        if verify(plan / "baseline", plan / "candidate", plan / "expected.json", plan / "recheck", oracle)["outcome"] != "pass":
            raise ValueError("Independent metadata recheck failed; stage is unchanged")
        return transaction.promote(spec, source, stage, plan)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__); sub = parser.add_subparsers(dest="command", required=True)
    prep = sub.add_parser("prepare")
    for name in ("source", "stage", "edits", "plan"): prep.add_argument("--" + name, type=Path, required=True)
    prep.add_argument("--client-version", required=True)
    commit = sub.add_parser("apply"); commit.add_argument("--plan", type=Path, required=True); commit.add_argument("--confirm", action="store_true", required=True)
    recover = sub.add_parser("recover"); recover.add_argument("--stage", type=Path, required=True); recover.add_argument("--confirm", action="store_true", required=True)
    for command in (prep, commit): command.add_argument("--oracle", type=Path, default=parity.default_oracle())
    args = parser.parse_args()
    try:
        if args.command == "prepare": result = prepare(args.source, args.stage, args.edits, args.plan, args.client_version, args.oracle)
        elif args.command == "apply": result = apply(args.plan, args.oracle)
        else: result = transaction.recover(hues.output(args.stage))
        print(json.dumps(result)); return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        print(f"[metadata-stage] {error}", file=sys.stderr); return 1


if __name__ == "__main__": raise SystemExit(main())
