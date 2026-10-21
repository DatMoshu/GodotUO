"""F09c: reviewed single-texture native staging, with upstream DEF semantics."""
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

NAMES = ("texidx.mul", "texmaps.mul", "texterr.def")


def file(root: Path, name: str, optional=False) -> Path | None:
    found = [p for p in root.iterdir() if p.name.lower() == name.lower()]
    if not found and optional:
        return None
    if len(found) != 1 or not found[0].is_file() or found[0].is_symlink():
        raise ValueError(f"Expected one regular {name}")
    return found[0]


def hashes(root: Path) -> dict:
    return {name: parity.digest(path) if (path := file(root, name, name == "texterr.def")) else None for name in NAMES}


def raw(text: str) -> bytes:
    value = base64.b64decode(text, validate=True)
    if len(value) not in (8192, 32768):
        raise ValueError("Expected one complete 64x64 or 128x128 native texture")
    return value


def read_record(root: Path, ident: int) -> bytes:
    mul, idx = file(root, "texmaps.mul"), file(root, "texidx.mul")
    writer.check_texmaps(mul, idx, [])
    if type(ident) is not int or not 0 <= ident < idx.stat().st_size // 12:
        raise ValueError("Texture ID is outside the existing index")
    at, size, _ = writer.idx_entry(idx, ident)
    if at < 0 or size <= 0:
        raise ValueError("Texture replacement requires an occupied record")
    with mul.open("rb") as stream:
        stream.seek(at)
        return stream.read(size)


def decoded(oracle: Path, root: Path, ident: int, definitions: Path | None, version: str) -> dict:
    writer.check_texmaps(file(root, "texmaps.mul"), file(root, "texidx.mul"), [])
    if definitions:
        hues.read(definitions, 2 * 1024 * 1024)
    result = parity.invoke(oracle, [str(root), "texmap", version, str(ident), str(definitions) if definitions else "-"])
    rows = result["records"]
    if len(rows) != 1 or rows[0]["id"] != ident:
        raise ValueError("Independent texture reader returned another ID")
    return result


def rgba_hash(payload: bytes) -> str:
    rgba = bytearray()
    for (color,) in struct.iter_unpack("<H", payload):
        rgba += bytes(((color >> 10 & 31) * 255 // 31, (color >> 5 & 31) * 255 // 31,
                       (color & 31) * 255 // 31, 255))
    return hashlib.sha256(rgba).hexdigest()


def verify(baseline: Path, candidate: Path, expected: Path, out: Path, oracle: Path) -> dict:
    baseline, expected = baseline.resolve(), expected.resolve()
    candidate, out = hues.output(candidate), hues.output(out)
    transaction.disjoint(baseline, candidate, expected, out)
    before_hashes, after_hashes = hashes(baseline), hashes(candidate)
    manifest_hash = parity.digest(expected)
    report = {"schema": 1, "domain": "texmap", "outcome": "error"}
    try:
        spec = json.loads(hues.read(expected, 512 * 1024))
        if spec.get("schema") != 1 or spec.get("domain") != "texmap":
            raise ValueError("Expected a texture verification manifest")
        ident, target, payload = spec["id"], spec["physical_id"], raw(spec["record_base64"])
        old = read_record(baseline, target)
        old_idx = file(baseline, "texidx.mul").read_bytes()
        new_idx = file(candidate, "texidx.mul").read_bytes()
        old_mul, new_mul = file(baseline, "texmaps.mul"), file(candidate, "texmaps.mul")
        tail = b"" if old == payload else payload
        wanted_idx = bytearray(old_idx)
        if tail:
            struct.pack_into("<ii", wanted_idx, target * 12, old_mul.stat().st_size, len(payload))
        if new_idx != wanted_idx or new_mul.stat().st_size != old_mul.stat().st_size + len(tail):
            raise ValueError("Texture index, opaque extra words or exact append length differ")
        with old_mul.open("rb") as source, new_mul.open("rb") as stage:
            while chunk := source.read(1024 * 1024):
                if stage.read(len(chunk)) != chunk:
                    raise ValueError("Existing texture MUL prefix changed")
            if stage.read() != tail:
                raise ValueError("Texture append differs from the reviewed raw record")
        definitions = file(baseline, "texterr.def", True)
        proof = decoded(oracle, candidate, ident, definitions, spec["client_version"])
        row = proof["records"][0]
        size = 64 if len(payload) == 8192 else 128
        if (row["physical_id"] != target or row["affected_ids"] != spec["affected_ids"]
            or row["raw_sha256"] != hashlib.sha256(payload).hexdigest()
            or row["rgba_sha256"] != rgba_hash(payload) or (row["width"], row["height"]) != (size, size)):
            raise ValueError("Independent texture decoding/mapping differs from the reviewed record")
        report.update(outcome="pass", id=ident, physical_id=target, affected_ids=row["affected_ids"],
            preserved_index_rows=len(old_idx) // 12 - bool(tail), preserved_prefix_bytes=old_mul.stat().st_size,
            appended_bytes=len(tail), oracle={"identity": proof["identity"], "runtime": proof["runtime"],
                "binary_sha256": parity.digest(oracle)})
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        report["error"] = str(error)
    report["inputs_unchanged"] = hashes(baseline) == before_hashes and hashes(candidate) == after_hashes and parity.digest(expected) == manifest_hash
    if not report["inputs_unchanged"]:
        report.update(outcome="error", error="Texture verification inputs changed")
    out.mkdir(parents=True, exist_ok=True); transaction.json_write(out / "report.json", report)
    return report


def prepare(source: Path, stage: Path, edits: Path, plan: Path, version: str, oracle: Path) -> dict:
    source, edits = source.resolve(), edits.resolve()
    stage, plan = hues.output(stage), hues.output(plan)
    transaction.disjoint(source, stage, edits, plan)
    if plan.exists():
        raise ValueError("Texture plan folder must be new")
    original_hashes, edit_hash = hashes(source), parity.digest(edits)
    edit = json.loads(hues.read(edits, 128 * 1024))
    if edit.get("schema") != 1 or edit.get("domain") != "texmap" or len(edit.get("records", [])) != 1:
        raise ValueError("Expected one reviewed texture replacement")
    row = edit["records"][0]; ident = row["id"]
    if type(ident) is not int or not 0 <= ident < 0x4000:
        raise ValueError("Texture ID must be in 0..16383")
    original = decoded(oracle, source, ident, file(source, "texterr.def", True), version)["records"][0]
    if original["raw_sha256"] != hashlib.sha256(raw(row["source_record_base64"])).hexdigest():
        raise ValueError("Texture document belongs to another source; reopen it")
    payload = raw(row["record_base64"])
    prior = transaction.stage_hashes(stage)
    if stage.exists():
        if not prior:
            raise ValueError("Existing empty stage refused")
        meta = json.loads(hues.read(stage / "stage.json", 1024 * 1024))
        if Path(meta["install"]).resolve() != source:
            raise ValueError("Stage belongs to another installation")
    plan.mkdir(parents=True)
    baseline, candidate = plan / "baseline", plan / "candidate"
    baseline.mkdir()
    if prior:
        shutil.copytree(stage, candidate)
    else:
        candidate.mkdir()
    staged = writer.Stage(candidate, source)
    for name in NAMES:
        try:
            path = staged.read_path(name)
        except FileNotFoundError:
            if name == "texterr.def": continue
            raise
        if name == "texterr.def": hues.read(path, 2 * 1024 * 1024)
        shutil.copyfile(path, baseline / name)
    mapped = decoded(oracle, baseline, ident, file(baseline, "texterr.def", True), version)["records"][0]
    target = mapped["physical_id"]
    writer.write_records(staged, [AssetRecord("texmap", target, payload)], replace=True)
    expected = {"schema": 1, "domain": "texmap", "client_version": version, "id": ident, "physical_id": target,
        "affected_ids": mapped["affected_ids"], "record_base64": row["record_base64"]}
    transaction.json_write(plan / "expected.json", expected)
    report = verify(baseline, candidate, plan / "expected.json", plan / "verification", oracle)
    if report["outcome"] != "pass":
        raise ValueError("Independent texture verification failed; see plan/verification/report.json")
    if hashes(source) != original_hashes or parity.digest(edits) != edit_hash or transaction.stage_hashes(stage) != prior:
        raise ValueError("Texture source, edits or stage changed during preparation")
    spec = {"schema": 1, "domain": "texmap", "state": "prepared", "source": str(source),
        "stage": str(stage), "plan": str(plan), "source_hashes": original_hashes,
        "prior_hashes": prior, "candidate_hashes": transaction.stage_hashes(candidate),
        "baseline_hashes": transaction.stage_hashes(baseline), "expected_sha256": parity.digest(plan / "expected.json"),
        "edit_file": str(edits), "edit_sha256": edit_hash, "client_version": version,
        "id": ident, "physical_id": target, "affected_ids": mapped["affected_ids"],
        "records": [{"id": ident, "before_base64": base64.b64encode(read_record(baseline, target)).decode(),
                     "after_base64": row["record_base64"]}],
        "width": 64 if len(payload) == 8192 else 128, "appended_bytes": report["appended_bytes"]}
    transaction.json_write(plan / "plan.json", spec)
    return spec


def apply(path: Path, oracle: Path) -> dict:
    spec, source, stage, plan = transaction.load_plan(path)
    if spec.get("domain") != "texmap":
        raise ValueError("Expected a reviewed texture plan")
    hues.output(Path(spec["stage"])); hues.output(plan)
    with transaction.stage_lock(stage):
        if (transaction.stage_hashes(stage) != spec["prior_hashes"]
            or transaction.stage_hashes(plan / "candidate") != spec["candidate_hashes"]
            or transaction.stage_hashes(plan / "baseline") != spec["baseline_hashes"]
            or hashes(source) != spec["source_hashes"]
            or parity.digest(Path(spec["edit_file"])) != spec["edit_sha256"]
            or parity.digest(plan / "expected.json") != spec["expected_sha256"]):
            raise ValueError("Texture review inputs changed; prepare again")
        if verify(plan / "baseline", plan / "candidate", plan / "expected.json", plan / "recheck", oracle)["outcome"] != "pass":
            raise ValueError("Independent texture recheck failed; stage is unchanged")
        return transaction.promote(spec, source, stage, plan)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare_parser = commands.add_parser("prepare")
    for name in ("source", "stage", "edits", "plan"):
        prepare_parser.add_argument("--" + name, required=True, type=Path)
    prepare_parser.add_argument("--client-version", required=True)
    prepare_parser.add_argument("--oracle", type=Path, default=parity.default_oracle())
    apply_parser = commands.add_parser("apply")
    apply_parser.add_argument("--plan", type=Path, required=True)
    apply_parser.add_argument("--confirm", action="store_true", required=True)
    apply_parser.add_argument("--oracle", type=Path, default=parity.default_oracle())
    recover_parser = commands.add_parser("recover")
    recover_parser.add_argument("--stage", type=Path, required=True)
    recover_parser.add_argument("--confirm", action="store_true", required=True)
    args = parser.parse_args()
    try:
        if args.command == "prepare":
            result = prepare(args.source, args.stage, args.edits, args.plan, args.client_version, args.oracle)
        elif args.command == "apply":
            result = apply(args.plan, args.oracle)
        else:
            result = transaction.recover(hues.output(args.stage))
        print(json.dumps(result, indent=2)); return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        print(f"[texture-stage] {error}", file=sys.stderr); return 1


if __name__ == "__main__":
    raise SystemExit(main())
