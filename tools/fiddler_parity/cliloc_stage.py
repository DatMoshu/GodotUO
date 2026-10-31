"""F15b: source-bound native Cliloc candidates and recoverable staged promotion."""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hues
import replacement as transaction
import run as parity
from guo import cliloc_records as codec
from guo.uorecord import AssetRecord
from uodata_write import uodata as writer


def identity(path: Path) -> str:
    return os.path.normcase(str(path.resolve()))


def read_native(path: Path, version: str, oracle: Path) -> tuple[bytes, bytes, dict]:
    raw = hues.read(path, codec.MAX_FILE); codec.check_container(raw)
    result = parity.invoke(oracle, [str(path.parent), "cliloc-read", version, path.name])
    plain = base64.b64decode(result["plain_base64"], validate=True); rows = codec.records(plain)
    digest = hashlib.sha256(raw).hexdigest()
    if (result["source_sha256"] != digest or parity.digest(path) != digest
        or codec.text_digest(rows) != result["text_sha256"] or len(rows) != result["record_count"]
        or len(codec.entries(rows)) != result["effective_count"]):
        raise ValueError("Independent Cliloc source read differs or source changed")
    result.pop("plain_base64")
    return raw, plain, result


def install_target(source: Path, filename: str) -> Path | None:
    matches = [p for p in source.iterdir() if p.name.lower() == filename.lower()]
    if len(matches) > 1 or (matches and not matches[0].is_file()):
        raise ValueError("Ambiguous or non-file native Cliloc target in source installation")
    return matches[0] if matches else None


def target_hash(source: Path, filename: str) -> str | None:
    path = install_target(source, filename)
    return parity.digest(path) if path else None


def validate_document(edit: dict, raw: bytes, plain: bytes, input_file: Path) -> list[dict]:
    if (edit.get("schema") != 1 or edit.get("kind") != "guo/cliloc-document" or edit.get("encoding") != "UTF-8"
        or edit.get("source_sha256") != hashlib.sha256(raw).hexdigest()
        or edit.get("header") != base64.b64encode(plain[:6]).decode()
        or type(edit.get("source_compressed")) is not bool or edit["source_compressed"] != (raw[3] == 0x8e)):
        raise ValueError("Cliloc document belongs to another source/header/encoding")
    if edit.get("language") in ("custom1", "custom2"):
        binding = edit.get("source_identity")
        if not isinstance(binding, str) or len(binding) != 64 or any(c not in "0123456789abcdef" for c in binding):
            raise ValueError("Invalid custom Cliloc source identity")
        if not isinstance(edit.get("native_source_file"), str) or identity(Path(edit["native_source_file"])) != identity(input_file):
            raise ValueError("Custom Cliloc document belongs to another source path")
    changes = edit.get("changes"); codec.patch(plain, changes)
    return changes


def verify(baseline: Path, candidate: Path, expected: Path, out: Path, oracle: Path) -> dict:
    baseline, expected = baseline.resolve(), expected.resolve()
    candidate, out = hues.output(candidate), hues.output(out)
    transaction.disjoint(baseline, candidate, expected, out)
    spec = json.loads(hues.read(expected, 16 * 1024 * 1024))
    if spec.get("schema") != 1 or spec.get("domain") != "cliloc": raise ValueError("Expected native Cliloc manifest")
    staged = writer.Stage(candidate, Path(spec["source"]))
    after_path = staged.read_path(spec["filename"])
    paths = [baseline / "current.bin", baseline / "document.bin", after_path, expected]
    if after_path.stat().st_nlink > 1: raise ValueError("Hardlinked Cliloc candidate refused")
    hashes = {str(p): parity.digest(p) for p in paths}
    report = {"schema": 1, "domain": "cliloc", "outcome": "error", "input_sha256": hashes}
    try:
        before_raw, before, _ = read_native(paths[0], spec["client_version"], oracle)
        wanted, counts = codec.patch(before, spec["changes"])
        intended = before_raw if wanted == before else wanted
        after_raw, after, proof = read_native(after_path, spec["client_version"], oracle)
        if after_raw != intended or after != wanted or proof["text_sha256"] != counts["text_sha256"]:
            raise ValueError("Native Cliloc differs from intended changes or alters untouched bytes/records/header")
        report.update(outcome="pass", filename=spec["filename"], counts=counts, header_preserved=after[:6] == before[:6],
            byte_identity=after_raw == before_raw, output_encoding="BWT preserved" if after_raw[3] == 0x8e else "plain UTF-8",
            oracle={"identity": proof["identity"], "runtime": proof["runtime"], "binary_sha256": parity.digest(oracle)})
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        report["error"] = str(error)
    report["inputs_unchanged"] = all(p.is_file() and parity.digest(p) == hashes[str(p)] for p in paths)
    if not report["inputs_unchanged"]: report.update(outcome="error", error="Cliloc verification inputs changed")
    out.mkdir(parents=True, exist_ok=True); transaction.json_write(out / "report.json", report)
    return report


def prepare(source: Path, input_file: Path, stage: Path, filename: str, edits: Path, plan: Path, version: str, oracle: Path) -> dict:
    source, input_file, edits = source.resolve(), input_file.resolve(), edits.resolve()
    stage, plan = hues.output(stage), hues.output(plan)
    transaction.disjoint(source, stage, edits, plan)
    if any(parity.overlap(input_file, p) for p in (stage, edits, plan)):
        raise ValueError("Cliloc source file overlaps an output")
    writer.cliloc_name(AssetRecord("cliloc-file", 0, b"", {"filename": filename}))
    if plan.exists(): raise ValueError("Cliloc plan folder must be new")
    source_raw, source_plain, source_proof = read_native(input_file, version, oracle)
    source_hash, edit_hash = hashlib.sha256(source_raw).hexdigest(), parity.digest(edits)
    edit = json.loads(hues.read(edits, 16 * 1024 * 1024))
    changes = validate_document(edit, source_raw, source_plain, input_file)
    installed_hash = target_hash(source, filename)
    prior = transaction.stage_hashes(stage)
    origin = {"source_file": identity(input_file), "source_sha256": source_hash}
    if stage.exists():
        if not prior: raise ValueError("Existing empty stage refused")
        meta = json.loads(hues.read(stage / "stage.json", 1024 * 1024))
        if Path(meta["install"]).resolve() != source: raise ValueError("Stage belongs to another installation")
        if not isinstance(meta.get("files"), dict) or not isinstance(meta.get("cliloc_sources", {}), dict):
            raise ValueError("Malformed Cliloc stage file/source bindings")
        if filename.lower() in meta["files"]:
            binding = meta.get("cliloc_sources", {}).get(filename.lower())
            if binding is not None and binding != origin: raise ValueError("Staged Cliloc belongs to another document source")
    plan.mkdir(parents=True); baseline, candidate = plan / "baseline", plan / "candidate"; baseline.mkdir()
    (baseline / "document.bin").write_bytes(source_raw)
    if prior: shutil.copytree(stage, candidate)
    else: candidate.mkdir()
    staged = writer.Stage(candidate, source)
    if filename.lower() in staged.meta["files"]:
        current_path = staged.read_path(filename)
        current_raw, current, _ = read_native(current_path, version, oracle)
        if filename.lower() not in staged.meta.get("cliloc_sources", {}) and hashlib.sha256(current_raw).hexdigest() != source_hash:
            raise ValueError("Unbound staged Cliloc differs from the document source; use a fresh output filename")
    else:
        if staged.target(filename, allow_new=True).exists(): raise ValueError("Unregistered staged Cliloc file would be overwritten")
        if installed_hash is not None and installed_hash != source_hash:
            raise ValueError("Native output filename belongs to a different installed Cliloc; choose a source-matched or new custom filename")
        current_raw, current = source_raw, source_plain
    (baseline / "current.bin").write_bytes(current_raw)
    output, counts = codec.patch(current, changes)
    payload = current_raw if output == current else output
    record = AssetRecord("cliloc-file", 0, payload, {"filename": filename})
    writer.write_records(staged, [record], replace=True)
    staged.meta.setdefault("cliloc_sources", {})[filename.lower()] = origin; staged.save()
    expected = {"schema": 1, "domain": "cliloc", "source": str(source), "filename": filename, "client_version": version, "changes": changes}
    transaction.json_write(plan / "expected.json", expected)
    report = verify(baseline, candidate, plan / "expected.json", plan / "verification", oracle)
    if report["outcome"] != "pass": raise ValueError("Independent Cliloc verification failed; see plan/verification/report.json")
    if (parity.digest(input_file) != source_hash or parity.digest(edits) != edit_hash
        or target_hash(source, filename) != installed_hash or transaction.stage_hashes(stage) != prior):
        raise ValueError("Cliloc source, target context, edits or prior stage changed during preparation")
    before, after = codec.entries(codec.records(current)), codec.entries(codec.records(output))
    reviews = [{"id": row["id"], "before": before[row["id"]].text if row["id"] in before else None,
        "after": after[row["id"]].text if row["id"] in after else None,
        "flag": after[row["id"]].flag if row["id"] in after else before[row["id"]].flag if row["id"] in before else None} for row in changes]
    transaction.json_write(plan / "review.json", {"schema": 1, "records": reviews})
    spec = {"schema": 1, "domain": "cliloc", "state": "prepared", "source": str(source), "stage": str(stage), "plan": str(plan),
        "input_file": str(input_file), "input_sha256": source_hash, "filename": filename, "installed_target_sha256": installed_hash,
        "edit_file": str(edits), "edit_sha256": edit_hash, "expected_sha256": parity.digest(plan / "expected.json"),
        "baseline_hashes": transaction.stage_hashes(baseline), "candidate_hashes": transaction.stage_hashes(candidate),
        "prior_hashes": prior, "client_version": version, "counts": counts, "review_sha256": parity.digest(plan / "review.json"),
        "output_encoding": report["output_encoding"], "source_compressed": source_proof["compressed"]}
    transaction.json_write(plan / "plan.json", spec); return {**spec, "records": reviews}


def apply(path: Path, oracle: Path) -> dict:
    spec, source, stage, plan = transaction.load_plan(path)
    if spec.get("domain") != "cliloc": raise ValueError("Expected a reviewed native Cliloc plan")
    hues.output(Path(spec["stage"])); hues.output(plan)
    with transaction.stage_lock(stage):
        if (transaction.stage_hashes(stage) != spec["prior_hashes"] or transaction.stage_hashes(plan / "candidate") != spec["candidate_hashes"]
            or transaction.stage_hashes(plan / "baseline") != spec["baseline_hashes"] or parity.digest(Path(spec["input_file"])) != spec["input_sha256"]
            or parity.digest(Path(spec["edit_file"])) != spec["edit_sha256"] or parity.digest(plan / "expected.json") != spec["expected_sha256"]
            or parity.digest(plan / "review.json") != spec["review_sha256"]
            or target_hash(source, spec["filename"]) != spec["installed_target_sha256"]):
            raise ValueError("Cliloc review inputs changed; prepare again")
        if verify(plan / "baseline", plan / "candidate", plan / "expected.json", plan / "recheck", oracle)["outcome"] != "pass":
            raise ValueError("Independent Cliloc recheck failed; stage is unchanged")
        return transaction.promote(spec, source, stage, plan)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__); sub = parser.add_subparsers(dest="command", required=True)
    prep = sub.add_parser("prepare")
    for name in ("source", "input-file", "stage", "edits", "plan"): prep.add_argument("--" + name, type=Path, required=True)
    prep.add_argument("--filename", required=True); prep.add_argument("--client-version", required=True)
    commit = sub.add_parser("apply"); commit.add_argument("--plan", type=Path, required=True); commit.add_argument("--confirm", action="store_true", required=True)
    restore = sub.add_parser("recover"); restore.add_argument("--stage", type=Path, required=True); restore.add_argument("--confirm", action="store_true", required=True)
    for command in (prep, commit): command.add_argument("--oracle", type=Path, default=parity.default_oracle())
    args = parser.parse_args()
    try:
        if args.command == "prepare": result = prepare(args.source, args.input_file, args.stage, args.filename, args.edits, args.plan, args.client_version, args.oracle)
        elif args.command == "apply": result = apply(args.plan, args.oracle)
        else: result = transaction.recover(hues.output(args.stage))
        print(json.dumps(result)); return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        print(f"[cliloc-stage] {error}", file=sys.stderr); return 1


if __name__ == "__main__": raise SystemExit(main())
