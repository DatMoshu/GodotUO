"""The shared pack validation corpus, run by both validators (S6, ADR-0019). Exit 0/1.

    python tools/asset_store/test_corpus.py [--out DIR] [--dotnet PATH]

Builds tools/asset_store/corpus/cases.json into one folder of manifests and
ZIPs, then:

  Python  pack.parse_manifest (a .json) or pack.verify (a .zip) on each file;
  C#      StorePack.Parse / StorePack.Extract on the same files, through the
          headless StoreSmoke ("corpus" mode).

Every file must be accepted or rejected as cases.json says, by both. A C#
rejection must be an InvalidDataException or a JsonException, the two its
callers handle: anything else would escape the installer's error handling.
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE / "corpus"))

import pack  # noqa: E402
from build import build  # noqa: E402

CLEAN = ("InvalidDataException", "JsonException")


def python_verdicts(folder: Path) -> dict:
    out = {}
    for f in sorted(folder.iterdir()):
        if f.name == "expect.json":
            continue
        try:
            if f.suffix == ".zip":
                pack.verify(f)
            else:
                pack.parse_manifest(f.read_bytes())
            out[f.name] = {"result": "accept"}
        except Exception as e:  # every failure is a rejection; the type is reported
            out[f.name] = {"result": "reject", "error": f"{type(e).__name__}: {e}"}
    return out


def main() -> int:
    # Case names and reasons carry non-ASCII; never let a console code page fail the run.
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--out", type=Path, default=HERE.parents[1] / "build" / "asset_store_corpus")
    ap.add_argument("--dotnet", default=shutil.which("dotnet") or "dotnet")
    args = ap.parse_args()

    expect = build(args.out)
    py = python_verdicts(args.out)
    cs_file = args.out.with_name(args.out.name + "_cs.json")
    r = subprocess.run([args.dotnet, "run", "--project", str(HERE / "headless" / "StoreSmoke.csproj"), "--",
                        "corpus", str(args.out), str(cs_file)], capture_output=True, text=True, timeout=300)
    if r.returncode != 0 or not cs_file.is_file():
        print(r.stdout[-2000:], r.stderr[-2000:])
        print("FAIL: the C# corpus run did not complete")
        return 1
    cs = json.loads(cs_file.read_text(encoding="utf-8"))

    failures = 0
    for name, e in expect.items():
        p, c = py.get(name, {}), cs.get(name, {})
        problems = []
        if p.get("result") != e["expect"]:
            problems.append(f"Python {p.get('result')} ({p.get('error', '')})")
        if c.get("result") != e["expect"]:
            problems.append(f"C# {c.get('result')} ({c.get('error', '')})")
        if c.get("result") == "reject" and not c.get("error", "").startswith(CLEAN):
            problems.append(f"C# rejects with an unhandled exception type: {c.get('error')}")
        if problems:
            failures += 1
            print(f"FAIL {name}: expected {e['expect']} ({e['why']}); " + "; ".join(problems))
    agree = sum(1 for n in expect if py.get(n, {}).get("result") == cs.get(n, {}).get("result"))
    print(f"corpus: {len(expect)} files, Python and C# agree on {agree}, {failures} failing")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
