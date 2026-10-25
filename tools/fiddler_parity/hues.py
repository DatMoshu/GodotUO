"""F08f: reviewed native hue candidates, independent read-back and recoverable promotion."""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run as parity
import replacement as transaction
from guo.uorecord import AssetRecord
from uodata_write import uodata as writer

GROUP, RECORD, MAX_BYTES = 708, 88, 8192 * 708


def read(path: Path, limit: int) -> bytes:
    with path.open("rb") as stream:
        value = stream.read(limit + 1)
    if len(value) > limit:
        raise ValueError(f"{path.name} exceeds {limit} bytes")
    return value


def output(path: Path) -> Path:
    if ".." in path.parts:
        raise ValueError("Parent traversal is refused")
    absolute = Path(os.path.abspath(path))
    if absolute.resolve() != absolute:
        raise ValueError("Linked output paths are refused")
    parity.protect_install(absolute)
    return absolute


def hue_file(root: Path) -> Path:
    matches = [p for p in root.iterdir() if p.name.lower() == "hues.mul"]
    if len(matches) != 1 or not matches[0].is_file() or matches[0].is_symlink():
        raise ValueError("Expected one regular hues.mul file")
    return matches[0]


def data(path: Path) -> bytes:
    value = read(path, MAX_BYTES)
    if not value or len(value) % GROUP:
        raise ValueError("hues.mul must contain complete 708-byte groups of eight hues")
    return value


def offset(ident: int, length: int) -> int:
    if type(ident) is not int or not 1 <= ident <= min(65535, length // GROUP * 8):
        raise ValueError("Hue ID must be one-based and inside the existing file")
    return (ident - 1) // 8 * GROUP + 4 + (ident - 1) % 8 * RECORD


def raw(text: str) -> bytes:
    value = base64.b64decode(text, validate=True)
    if len(value) != RECORD:
        raise ValueError("Each hue must retain exactly 88 bytes")
    return value


def edits(path: Path, original: bytes) -> list[dict]:
    spec = json.loads(read(path, 256 * 1024))
    rows = spec.get("records")
    if spec.get("schema") != 1 or spec.get("domain") != "hue" or not isinstance(rows, list) or not 0 < len(rows) <= 256:
        raise ValueError("Expected schema 1 hue edits with 1..256 records")
    seen = set()
    for row in rows:
        ident = row["id"]; at = offset(ident, len(original))
        if ident in seen:
            raise ValueError("Duplicate hue ID")
        seen.add(ident)
        if raw(row["source_record_base64"]) != original[at:at + RECORD]:
            raise ValueError(f"Hue {ident} belongs to another base library; reopen it")
        raw(row["record_base64"])
    return rows


def verify(source: Path, stage: Path, expected: Path, out: Path, oracle: Path) -> dict:
    source, expected = source.resolve(), expected.resolve()
    stage = output(stage)
    out = output(out)
    transaction.disjoint(source, stage, expected, out)
    paths = [hue_file(source), hue_file(stage), expected]
    if paths[1].stat().st_nlink > 1:
        raise ValueError("Hardlinked stage output refused")
    hashes = {str(p): parity.digest(p) for p in paths}
    report = {"schema": 1, "domain": "hue", "outcome": "error", "input_sha256": hashes}
    try:
        before, after = data(paths[0]), data(paths[1])
        spec = json.loads(read(expected, 256 * 1024))
        if spec.get("schema") != 1 or spec.get("domain") != "hue":
            raise ValueError("Expected a hue verification manifest")
        rows = spec["records"]
        if not isinstance(rows, list) or not 0 < len(rows) <= 256:
            raise ValueError("Expected 1..256 hue records")
        ids = [row["id"] for row in rows]
        if len(set(ids)) != len(ids):
            raise ValueError("Duplicate hue ID")
        wanted = {row["id"]: raw(row["record_base64"]) for row in rows}
        intended = bytearray(before)
        for ident, payload in wanted.items():
            at = offset(ident, len(before)); intended[at:at + RECORD] = payload
        if bytes(intended) != after:
            raise ValueError("Stage differs from intended records or changes untouched bytes/headers/length")
        decoded = parity.invoke(oracle, [str(stage), "hue", spec["client_version"], ",".join(map(str, ids))])
        report["oracle"] = {"identity": decoded["identity"], "runtime": decoded["runtime"], "binary_sha256": parity.digest(oracle)}
        actual = decoded["records"]
        if len(actual) != len(rows) or {row["id"] for row in actual} != set(ids):
            raise ValueError("Independent reader returned different IDs")
        for row in actual:
            payload = wanted[row["id"]]; values = struct.unpack_from("<34H", payload)
            header = struct.unpack_from("<I", before, (row["id"] - 1) // 8 * GROUP)[0]
            if (raw(row["raw_base64"]) != payload or row["colors"] != list(values[:32])
                or (row["start"], row["end"]) != values[32:] or row["header"] != header):
                raise ValueError(f"Independent hue read-back differs for {row['id']}")
        report.update(outcome="pass", checked_ids=ids, preserved_headers=len(before) // GROUP,
            preserved_records=len(before) // GROUP * 8 - len(rows))
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        report["error"] = str(error)
    report["inputs_unchanged"] = all(p.is_file() and parity.digest(p) == hashes[str(p)] for p in paths)
    if not report["inputs_unchanged"]:
        report.update(outcome="error", error="Inputs changed during independent verification")
    out.mkdir(parents=True, exist_ok=True); transaction.json_write(out / "report.json", report)
    return report


def prepare(source: Path, stage: Path, edit_file: Path, plan: Path, version: str, oracle: Path) -> dict:
    source, edit_file = source.resolve(), edit_file.resolve()
    stage, plan = output(stage), output(plan)
    transaction.disjoint(source, stage, edit_file, plan)
    if plan.exists():
        raise ValueError("Plan folder must be new")
    if any(p.name.lower() == "verdata.mul" for p in source.iterdir()):
        raise ValueError("verdata libraries require separate hue source-layer certification")
    source_path = hue_file(source); original = data(source_path)
    source_hash = hashlib.sha256(original).hexdigest(); edit_hash = parity.digest(edit_file)
    rows = edits(edit_file, original)
    prior = transaction.stage_hashes(stage)
    if stage.exists():
        if not prior:
            raise ValueError("Existing empty stage refused; choose a new destination")
        meta = json.loads(read(stage / "stage.json", 1024 * 1024))
        if Path(meta["install"]).resolve() != source:
            raise ValueError("Stage belongs to another source")
    plan.mkdir(parents=True)
    baseline = plan / "baseline"; baseline.mkdir()
    candidate = plan / "candidate"
    if prior:
        shutil.copytree(stage, candidate)
    else:
        candidate.mkdir()
    staged = writer.Stage(candidate, source)
    effective = staged.read_path("hues.mul")
    old = data(effective)
    if len(old) != len(original):
        raise ValueError("Existing stage and install hue layouts differ")
    shutil.copyfile(effective, baseline / "hues.mul")
    # Reuse the typed native record writer, not a second hue encoder.
    writer.write_records(staged, [AssetRecord("hue", row["id"], raw(row["record_base64"])) for row in rows])
    staged.save()
    expected = {"schema": 1, "domain": "hue", "client_version": version,
        "records": [{"id": row["id"], "record_base64": row["record_base64"]} for row in rows]}
    transaction.json_write(plan / "expected.json", expected)
    report = verify(baseline, candidate, plan / "expected.json", plan / "verification", oracle)
    if report["outcome"] != "pass":
        raise ValueError("Independent hue verification failed; see plan/verification/report.json")
    if parity.digest(source_path) != source_hash or parity.digest(edit_file) != edit_hash or transaction.stage_hashes(stage) != prior:
        raise ValueError("Source, edits or stage changed during preparation; prepare again")
    spec = {"schema": 1, "domain": "hue", "state": "prepared", "source": str(source), "stage": str(stage),
        "plan": str(plan), "source_hashes": {source_path.name: source_hash}, "prior_hashes": prior,
        "candidate_hashes": transaction.stage_hashes(candidate), "expected_sha256": parity.digest(plan / "expected.json"),
        "edit_file": str(edit_file), "edit_sha256": edit_hash, "client_version": version,
        "records": [{"id": row["id"], "before_base64": base64.b64encode(old[offset(row["id"], len(old)):offset(row["id"], len(old)) + RECORD]).decode(),
            "after_base64": row["record_base64"]} for row in rows]}
    transaction.json_write(plan / "plan.json", spec)
    return spec


def apply(path: Path, oracle: Path) -> dict:
    spec, source, stage, plan = transaction.load_plan(path)
    if spec.get("domain") != "hue":
        raise ValueError("Expected a reviewed hue plan")
    # Validate the recorded spelling before load_plan's resolved path can hide
    # a stage that was replaced by a directory link after preparation.
    output(Path(spec["stage"])); output(plan)
    with transaction.stage_lock(stage):
        if transaction.stage_hashes(stage) != spec["prior_hashes"] or transaction.stage_hashes(plan / "candidate") != spec["candidate_hashes"]:
            raise ValueError("Stage/candidate changed since review; prepare again")
        if parity.digest(Path(spec["edit_file"])) != spec["edit_sha256"] or parity.digest(plan / "expected.json") != spec["expected_sha256"]:
            raise ValueError("Hue input/manifest changed since review; prepare again")
        if any(parity.digest(source / name) != value for name, value in spec["source_hashes"].items()):
            raise ValueError("Source changed since review; prepare again")
        result = verify(plan / "baseline", plan / "candidate", plan / "expected.json", plan / "recheck", oracle)
        if result["outcome"] != "pass":
            raise ValueError("Independent hue recheck failed; current stage is unchanged")
        return transaction.promote(spec, source, stage, plan)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__); sub = parser.add_subparsers(dest="command", required=True)
    prep = sub.add_parser("prepare")
    for key in ("source", "stage", "edits", "plan"):
        prep.add_argument("--" + key, type=Path, required=True)
    prep.add_argument("--client-version", required=True)
    commit = sub.add_parser("apply"); commit.add_argument("--plan", type=Path, required=True); commit.add_argument("--confirm", action="store_true", required=True)
    restore = sub.add_parser("recover"); restore.add_argument("--stage", type=Path, required=True); restore.add_argument("--confirm", action="store_true", required=True)
    for item in (prep, commit):
        item.add_argument("--oracle", type=Path, default=parity.default_oracle())
    args = parser.parse_args()
    try:
        if args.command == "prepare":
            result = prepare(args.source, args.stage, args.edits, args.plan, args.client_version, args.oracle)
        elif args.command == "apply":
            result = apply(args.plan, args.oracle)
        else:
            result = transaction.recover(output(args.stage))
        print(json.dumps(result)); return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        print(f"[hue-stage] {error}", file=sys.stderr); return 1


if __name__ == "__main__":
    raise SystemExit(main())
