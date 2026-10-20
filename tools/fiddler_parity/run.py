"""Independent, read-only static-art stage verification (F01a)."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
from guo.config import load_config
from guo.uomap import uop_hash

MAX_FILE = 512 * 1024 * 1024
MAX_RECORDS = 256
DOCTOR = "launchers/dev/fiddler_parity_doctor.bat --build (or .sh)"


class Unsupported(ValueError):
    pass


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def overlap(a: Path, b: Path) -> bool:
    return a == b or a in b.parents or b in a.parents


def protect_install(*outputs: Path) -> None:
    config = load_config()
    for value in (config.client_data, config.client_data_env, config.client_data_setting):
        if not str(value).strip() or str(value) == ".":
            continue
        install = Path(value).resolve()
        if any(overlap(install, path.resolve()) for path in outputs):
            raise ValueError("Output overlaps the configured read-only client installation")


def bounded(path: Path, limit: int = MAX_FILE) -> bytes:
    if path.stat().st_size > limit:
        raise ValueError(f"Input exceeds {limit} bytes: {path.name}")
    return path.read_bytes()


def check_art(raw: bytes) -> None:
    """Bounds-check RLE before calling the upstream unsafe decoder; no pixel decode."""
    if len(raw) < 8:
        raise ValueError("Truncated static header")
    width, height = struct.unpack_from("<hh", raw, 4)
    if not (0 < width <= 1024 and 0 < height <= 1024):
        raise ValueError("Invalid static dimensions")
    base = 8 + height * 2
    if base > len(raw):
        raise ValueError("Truncated static row table")
    for row in range(height):
        at = base + struct.unpack_from("<H", raw, 8 + row * 2)[0] * 2
        x = 0
        while True:
            if at + 4 > len(raw):
                raise ValueError(f"Truncated static run in row {row}")
            skip, count = struct.unpack_from("<HH", raw, at)
            at += 4
            if skip == count == 0:
                break
            x += skip + count
            if skip + count >= 2048 or x > width or at + count * 2 > len(raw):
                raise ValueError(f"Invalid static run in row {row}")
            at += count * 2


def records(stage: Path, packaging: str) -> tuple[dict[int, bytes], dict[str, str]]:
    """Validate selected container, then extract raw payloads for preservation checks."""
    result = {}
    if packaging == "mul":
        names = ["art.mul", "artidx.mul"]
        mul, idx = (bounded(stage / name) for name in names)
        if len(idx) % 12:
            raise ValueError("Truncated artidx.mul entry")
        if len(idx) // 12 > 0x14000:
            raise Unsupported("Index exceeds supported art ID range")
        for index in range(len(idx) // 12):
            offset, size, extra = struct.unpack_from("<iii", idx, index * 12)
            if size <= 0:
                continue
            if offset < 0 or offset + size > len(mul):
                raise ValueError(f"Index {index} points outside art.mul")
            result[index] = mul[offset:offset + size]
    elif packaging == "uop":
        names = ["artLegacyMUL.uop"]
        data = bounded(stage / names[0])
        if len(data) < 28 or struct.unpack_from("<I", data)[0] != 0x50594D:
            raise ValueError("Invalid UOP header")
        block = struct.unpack_from("<q", data, 12)[0]
        seen, entries = set(), {}
        while block:
            if block in seen or block < 28 or block + 12 > len(data):
                raise ValueError("Invalid or cyclic UOP block")
            seen.add(block)
            count, nxt = struct.unpack_from("<iq", data, block)
            if count < 0 or block + 12 + count * 34 > len(data):
                raise ValueError("Invalid UOP table length")
            for n in range(count):
                offset, header, stored, size, key, checksum, flag = struct.unpack_from("<qiiiQIh", data, block + 12 + n * 34)
                if offset == 0:
                    continue
                if flag != 0:
                    raise Unsupported("Compressed art UOP entries are not certified by this oracle")
                if header != 0:
                    raise Unsupported("UOP per-entry headers require separate preservation certification")
                if min(offset, header, stored, size) < 0 or stored != size or offset + header + size > len(data):
                    raise ValueError("Invalid UOP payload range")
                if key in entries:
                    raise ValueError("Duplicate UOP hash")
                entries[key] = data[offset + header:offset + header + size]
            block = nxt
        keys = {uop_hash(f"build/artlegacymul/{index:08d}.tga"): index for index in range(0x14000)}
        if entries.keys() - keys.keys():
            raise Unsupported("Unknown UOP entries; preservation cannot be certified")
        result = {keys[key]: value for key, value in entries.items()}
    else:
        raise Unsupported(f"Unsupported packaging: {packaging}")
    for index, raw in result.items():
        if index >= 0x4000:
            check_art(raw)
    return result, {name: digest(stage / name) for name in names}


def source_identity(upstream: Path) -> tuple[str, str]:
    prefix = ["git", "-c", f"safe.directory={upstream.as_posix()}", "-C", str(upstream)]
    commit = subprocess.check_output(prefix + ["rev-parse", "HEAD"], text=True).strip()
    if subprocess.check_output(prefix + ["status", "--porcelain"], text=True).strip():
        raise ValueError("Oracle upstream checkout is modified; restore it through its owner before building")
    h = hashlib.sha256()
    for area in ("ClassicUO.Assets", "ClassicUO.IO", "ClassicUO.Utility"):
        for path in sorted((upstream / "src" / area).rglob("*.cs")):
            if "obj" in path.parts or "bin" in path.parts:
                continue
            h.update(path.relative_to(upstream).as_posix().encode())
            h.update(path.read_bytes())
    return commit, h.hexdigest()


def default_oracle() -> Path:
    return load_config().build / "fiddler-parity" / "bin" / "Oracle" / "release" / "guo-static-oracle.dll"


def invoke(oracle: Path, args: list[str]) -> dict:
    if not oracle.is_file() or not shutil.which("dotnet"):
        raise RuntimeError(f"Missing oracle/.NET runtime. Run {DOCTOR}")
    result = subprocess.run(["dotnet", str(oracle), *args], text=True, capture_output=True, timeout=60)
    if result.returncode:
        raise RuntimeError(f"Oracle failed ({result.returncode}): {result.stderr.strip()}. Run {DOCTOR}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as e:
        raise RuntimeError("Oracle did not produce a JSON verdict") from e


def doctor(oracle: Path, build: bool = False) -> dict:
    try:
        import PIL
    except ImportError as e:
        raise RuntimeError("Install the project's Python requirements (Pillow), then rerun " + DOCTOR) from e
    config = load_config()
    if build:
        if not shutil.which("dotnet"):
            raise RuntimeError("Install .NET 10 SDK, then rerun " + DOCTOR)
        commit, source_hash = source_identity(config.upstream)
        result = subprocess.run(["dotnet", "build", str(Path(__file__).with_name("Oracle.csproj")),
            "-c", "Release", "--artifacts-path", str(config.build / "fiddler-parity"),
            f"-p:UpstreamDir={config.upstream}", f"-p:OracleCommit={commit}",
            f"-p:OracleSourceHash={source_hash}", "--nologo", "-v", "q"], timeout=180, capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError("Oracle build failed: " + result.stdout[-6000:] + result.stderr[-2000:])
    return {**invoke(oracle, ["identity"]), "pillow": PIL.__version__}


def verify(stage: Path, source: Path, manifest: Path, out: Path, oracle: Path) -> dict:
    stage, source, manifest, out = (p.resolve() for p in (stage, source, manifest, out))
    protect_install(out)
    for item in (stage, source, manifest):
        if overlap(item, out):
            raise ValueError("Report output overlaps an input")
    if overlap(stage, source):
        raise ValueError("Source and stage must be separate, non-nested directories")
    # Reject file links/hardlinks and directory aliases before reading/creating reports.
    for root in (stage, source):
        if not root.is_dir() or (root / "Art").exists():
            raise ValueError("Expected explicit container directory without loose Art overrides")
        for path in root.iterdir():
            if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()):
                raise ValueError("Linked input entries are refused")
    spec = json.loads(bounded(manifest, 16 * 1024 * 1024))
    if spec.get("schema") != 1 or spec.get("packaging") not in ("mul", "uop"):
        raise Unsupported("Expected manifest schema 1 and mul/uop packaging")
    expected = spec.get("records", [])
    if not 0 < len(expected) <= MAX_RECORDS:
        raise ValueError("Expected 1..256 static records")
    ids = []
    for item in expected:
        ident, width, height = item["id"], item["width"], item["height"]
        if type(ident) is not int or not 0 <= ident < 0x10000 or ident in ids:
            raise ValueError("Invalid/duplicate static ID")
        if type(width) is not int or type(height) is not int or not (0 < width <= 1024 and 0 < height <= 1024):
            raise ValueError("Invalid expected dimensions")
        if len(item["rgba"]) != width * height or any(type(p) is not int or not 0 <= p <= 0xffffffff for p in item["rgba"]):
            raise ValueError("Expected packed RGBA pixels (R in low byte)")
        if not isinstance(item.get("raw_sha256"), str) or len(item["raw_sha256"]) != 64:
            raise ValueError("Expected static payload SHA256, including unknown header bytes")
        ids.append(ident)
    packaging = spec["packaging"]
    report = {"schema": 1, "outcome": "error", "packaging": packaging,
        "client_version": spec["client_version"], "checked_ids": ids, "mismatches": [], "unsupported": []}
    # Capture hashes even for malformed containers; compare again in finally.
    names = ["art.mul", "artidx.mul"] if packaging == "mul" else ["artLegacyMUL.uop"]
    paths = [root / name for root in (source, stage) for name in names] + [manifest]
    for path in paths:
        if path.is_symlink() or path.resolve().parent not in (source, stage, manifest.parent) or path.stat().st_nlink > 1:
            raise ValueError("Aliased container/manifest is refused")
    hashes = {str(p): digest(p) for p in paths}
    report["input_hashes"] = hashes
    try:
        original, _ = records(source, packaging)
        staged, _ = records(stage, packaging)
        changed = {ident + 0x4000 for ident in ids}
        preserved = sorted(index for index in original.keys() | staged.keys() if index not in changed)
        for index in preserved:
            if original.get(index) != staged.get(index):
                report["mismatches"].append({"kind": "untouched_record", "container_index": index})
        report["preserved_records"] = len(preserved)
        if packaging == "mul":
            old_idx, new_idx = (bounded(root / "artidx.mul") for root in (source, stage))
            for index in range(max(len(old_idx), len(new_idx)) // 12):
                if index in changed:
                    continue
                old, new = old_idx[index * 12:index * 12 + 12], new_idx[index * 12:index * 12 + 12]
                # Offsets may move for populated records; extra and empty slots
                # retain their exact bytes, even when the reader ignores them.
                metadata_equal = old[8:] == new[8:] if index in original and index in staged else old == new
                if not metadata_equal:
                    report["mismatches"].append({"kind": "untouched_index_metadata", "container_index": index})
        report["container_layout_changed"] = any(hashes[str(source / name)] != hashes[str(stage / name)] for name in names)
        if sum(item["width"] * item["height"] for item in expected) > 4 * 1024 * 1024:
            raise ValueError("Requested decode exceeds four million pixels")
        for wanted in expected:
            raw = staged.get(wanted["id"] + 0x4000)
            size = struct.unpack_from("<hh", raw, 4) if raw is not None else None
            if size != (wanted["width"], wanted["height"]):
                report["mismatches"].append({"id": wanted["id"], "expected_dimensions": [wanted["width"], wanted["height"]],
                    "actual_dimensions": size})
        if any("expected_dimensions" in row for row in report["mismatches"]):
            report["outcome"] = "mismatch"
            return report
        actual = invoke(oracle, [str(stage), packaging, spec["client_version"], ",".join(map(str, ids))])
        report["oracle"] = {k: actual[k] for k in ("identity", "runtime")}
        report["oracle"]["adapter_sha256"] = digest(oracle)
        actual_rows = {r["id"]: r for r in actual["records"]}
        report["records"] = []
        for wanted in expected:
            got = actual_rows.get(wanted["id"])
            row = {"id": wanted["id"], "differences": {}}
            for field in ("width", "height", "rgba", "raw_sha256"):
                if got is None or wanted[field] != got[field]:
                    row["differences"][field] = {"expected": wanted[field], "actual": got.get(field) if got else None}
            if row["differences"]:
                report["mismatches"].append(row)
            report["records"].append(row)
        report["outcome"] = "mismatch" if report["mismatches"] else "pass"
    except Unsupported as e:
        report["outcome"] = "unsupported"
        report["unsupported"].append(str(e))
    except (ValueError, OSError, RuntimeError, subprocess.TimeoutExpired) as e:
        report["error"] = str(e)
    finally:
        report["inputs_unchanged"] = all(p.exists() and digest(p) == hashes[str(p)] for p in paths)
        if not report["inputs_unchanged"]:
            report["outcome"] = "error"
        out.mkdir(parents=True, exist_ok=True)
        report_path = out / "report.json"
        if report_path.exists() and (report_path.is_symlink() or report_path.stat().st_nlink > 1):
            raise ValueError("Aliased report destination refused")
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    doc = sub.add_parser("doctor")
    doc.add_argument("--build", action="store_true")
    check = sub.add_parser("verify")
    for name in ("stage", "source", "manifest", "out"):
        check.add_argument("--" + name, type=Path, required=True)
    for command in (doc, check):
        command.add_argument("--oracle", type=Path, default=default_oracle())
    args = parser.parse_args()
    try:
        if args.command == "doctor":
            print(json.dumps(doctor(args.oracle, args.build), indent=2))
            return 0
        result = verify(args.stage, args.source, args.manifest, args.out, args.oracle)
        print(json.dumps({"outcome": result["outcome"], "report": str(args.out / "report.json")}))
        return 0 if result["outcome"] == "pass" else 1
    except (ValueError, OSError, RuntimeError, subprocess.SubprocessError, KeyError) as e:
        print(f"[fiddler-parity] {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
