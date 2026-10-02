#!/usr/bin/env python3
"""Inspect and verify the UO client data the port reads at runtime.

The port never copies or converts the client install: it reads it in place,
exactly as ClassicUO does. This tool is the gate that proves the install is
complete and readable before the runtime tries to use it, and it emits the
manifest the runtime and the port audit both consume.

    python tools/uodata/run.py verify [--data-dir DIR] [--out manifest.json]
    python tools/uodata/run.py list [--subsystem render/art]
    python tools/uodata/run.py where

Run it through the launcher so configuration resolves the same way it does
for the game:

    launchers\\pipeline\\01_verify_client_data.bat

Exit codes:
    0  every required file present and readable
    1  a required file is missing or unreadable
    2  the data directory itself does not exist
"""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

# Make the shared package importable when run as a plain script.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import FILE_REGISTRY, DataFile, load_config  # noqa: E402
from guo.formats import by_subsystem, index_dir  # noqa: E402
from guo.datasources import WIZARD_EXIT  # noqa: E402


def _probe(entry: DataFile, data_dir: Path) -> dict:
    """Describe one registry entry against the install on disk."""
    forms = entry.present_forms(data_dir)
    satisfied = entry.is_satisfied(data_dir)

    # Prefer UOP when both forms exist: that is what a modern client uses,
    # and a stale leftover .mul beside it would otherwise shadow the truth.
    if forms["uop"]:
        resolved, fmt = forms["uop"], "uop"
    elif forms["mul"]:
        resolved, fmt = forms["mul"], "mul"
    else:
        resolved, fmt = [], None

    sizes: dict[str, int] = {}
    unreadable: list[str] = []
    for name in resolved + forms["index"]:
        path = data_dir / name
        try:
            sizes[name] = path.stat().st_size
        except OSError:
            # Present in the listing but not statable: permissions, a broken
            # link, or a file being written. Treat as a hard failure.
            unreadable.append(name)

    present_index = {n.lower() for n in forms["index"]}
    missing_index = [
        n for n in entry.indexed_by if n.lower() not in present_index
    ] if fmt == "mul" else []

    return {
        "key": entry.key,
        "description": entry.description,
        "subsystem": entry.subsystem,
        "required": entry.required,
        "satisfied": satisfied and not unreadable,
        "format": fmt,
        "files": resolved,
        "index_files": forms["index"],
        "missing_index": missing_index,
        "sizes": sizes,
        "unreadable": unreadable,
        "notes": entry.notes,
    }


def cmd_verify(args: argparse.Namespace) -> int:
    cfg = load_config()
    data_dir = Path(args.data_dir) if args.data_dir else cfg.client_data
    client_version = args.client_version or cfg.client_version
    quiet = args.quiet

    if not data_dir.is_dir():
        # ADR-0021: no data yet is the first-run wizard's case, not a crash.
        print(f"[uodata] no UO client data at {data_dir or '(not set)'}: run the first-run wizard, "
              "or set UO_CLIENT_DATA in launchers/_shared/config.local.bat")
        return WIZARD_EXIT

    results = [_probe(e, data_dir) for e in FILE_REGISTRY]
    present = [r for r in results if r["satisfied"]]
    missing_required = [
        r for r in results if r["required"] and not r["satisfied"]
    ]
    missing_optional = [
        r for r in results if not r["required"] and not r["satisfied"]
    ]
    unreadable = [r for r in results if r["unreadable"]]

    # A UOP anywhere means the install is a modern client, which changes how
    # the runtime opens several archives.
    uop_count = sum(1 for r in results if r["format"] == "uop")
    packaging = "uop" if uop_count else "mul"

    if not quiet:
        print(f"[uodata] Data dir       : {data_dir}")
        print(f"[uodata] Client version : {client_version}")
        print(f"[uodata] Packaging      : {packaging} ({uop_count} uop entries)")
        print(
            f"[uodata] Satisfied      : {len(present)}/{len(results)} "
            f"({len(missing_required)} required missing, "
            f"{len(missing_optional)} optional missing)"
        )
        print()

        if missing_required:
            print("  MISSING (required):")
            for r in missing_required:
                want = " | ".join(r["files"] or []) or _expected(r)
                print(f"    - {r['key']:<12} {r['description']}")
                print(f"      expected: {want}")
                if r["missing_index"]:
                    print(f"      missing index: {', '.join(r['missing_index'])}")
            print()

        if unreadable:
            print("  UNREADABLE:")
            for r in unreadable:
                print(f"    - {r['key']}: {', '.join(r['unreadable'])}")
            print()

        if args.verbose and missing_optional:
            print("  missing (optional, client still boots):")
            for r in missing_optional:
                print(f"    - {r['key']:<12} {r['description']}")
            print()

    manifest = {
        "schema": "guo/client_manifest@1",
        "generated": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "data_dir": str(data_dir),
        "client_version": client_version,
        "packaging": packaging,
        "counts": {
            "total": len(results),
            "satisfied": len(present),
            "missing_required": len(missing_required),
            "missing_optional": len(missing_optional),
        },
        "ok": not missing_required and not unreadable,
        "entries": results,
        "stray_files": sorted(_stray(data_dir)),
    }

    if args.out:
        out = Path(args.out)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(manifest, indent=2), encoding="utf-8", newline="\n")
        if not quiet:
            print(f"[uodata] Manifest -> {out}")

    if missing_required or unreadable:
        if quiet:
            print(
                f"[uodata] FAILED: {len(missing_required)} required missing, "
                f"{len(unreadable)} unreadable"
            )
        return 1

    if not quiet:
        print("[uodata] OK - all required client data present.")
    return 0


def _expected(result: dict) -> str:
    """Human-readable description of what would have satisfied an entry."""
    for entry in FILE_REGISTRY:
        if entry.key == result["key"]:
            opts = list(entry.mul) + list(entry.uop)
            return " or ".join(opts) if opts else "(nothing declared)"
    return "?"


def _stray(data_dir: Path) -> set[str]:
    """Data-looking files in the install that the registry does not know about.

    Useful when supporting a new client version or a shard with custom
    content: anything listed here is data the port currently ignores.
    """
    known = set()
    for entry in FILE_REGISTRY:
        known.update(n.lower() for n in entry.mul)
        known.update(n.lower() for n in entry.uop)
        known.update(n.lower() for n in entry.indexed_by)
    interesting = (".mul", ".uop", ".idx", ".def", ".enu")
    return {
        name
        for name in index_dir(data_dir)
        if name.endswith(interesting) and name not in known
    }


def cmd_list(args: argparse.Namespace) -> int:
    grouped = by_subsystem()
    for subsystem in sorted(grouped):
        if args.subsystem and subsystem != args.subsystem:
            continue
        print(f"\n{subsystem}")
        for entry in grouped[subsystem]:
            flag = "required" if entry.required else "optional"
            forms = " | ".join(list(entry.mul) + list(entry.uop))
            print(f"  {entry.key:<12} [{flag}] {entry.description}")
            print(f"  {'':<12} files: {forms}")
    return 0


def cmd_where(args: argparse.Namespace) -> int:
    cfg = load_config()
    print(f"repo root    : {cfg.root}")
    print(f"client data  : {cfg.client_data}  (exists={cfg.client_data.is_dir()})")
    print(f"client ver   : {cfg.client_version}")
    print(f"cache dir    : {cfg.cache_dir}")
    print(f"godot project: {cfg.godot_project}")
    print(f"godot exe    : {cfg.godot_exe}  (exists={cfg.godot_exe.is_file()})")
    print(f"upstream     : {cfg.upstream}  (exists={cfg.upstream.is_dir()})")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="uodata",
        description="Verify and inspect the UO client data used by GUO.",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_verify = sub.add_parser("verify", help="check the install is complete")
    p_verify.add_argument("--data-dir", help="override UO_CLIENT_DATA")
    p_verify.add_argument("--client-version", help="override UO_CLIENT_VERSION")
    p_verify.add_argument("--out", help="write the JSON manifest here")
    p_verify.add_argument("--quiet", action="store_true", help="only report failure")
    p_verify.add_argument(
        "--verbose", action="store_true", help="also list missing optional data"
    )
    p_verify.set_defaults(func=cmd_verify)

    p_list = sub.add_parser("list", help="show the known data-file registry")
    p_list.add_argument("--subsystem", help="filter to one subsystem")
    p_list.set_defaults(func=cmd_list)

    p_where = sub.add_parser("where", help="print resolved configuration paths")
    p_where.set_defaults(func=cmd_where)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
