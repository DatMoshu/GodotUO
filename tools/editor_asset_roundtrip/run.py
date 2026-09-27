#!/usr/bin/env python3
"""Prove a world project's asset edits reach the client: export, verify, read back.

The editor's phase 5 round trip (ADR-0020): replaced art, gumps and hues in a
world project's assets/ folder are exported as a patch set (tools/world:
verdata.mul, hues.mul, files_override.txt) and verified against the project.
Then the real GUO client starts headless with a scratch home whose
settings.json points upstream's files_override at the export. Its
--asset-probe decodes the replaced ids through the client's own file manager
and writes them out, and this tool compares them with the project's files,
pixel for pixel.

    python tools/editor_asset_roundtrip/run.py [--project DIR] [--out DIR] [--no-build]

The project defaults to UO_WORLD_PROJECT. Output goes to
build/editor_asset_roundtrip/: the export, the client's home and log, and
what it decoded. All of it derives from the install, so none of it is
committed (build/ and *.mul are gitignored).

A control run without files_override must decode the install's art instead.
That shows the match comes from the export and not from something else.

No shard and no window: the client reads its files at start, before the
login screen, so the probe runs headless and quits.

Exit codes: 0 every asset round-tripped, 1 a mismatch or a failed step,
2 bad input.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config, uoart  # noqa: E402
from guo.process import no_activate  # noqa: E402

TOOLS = Path(__file__).resolve().parents[1]


def world(*args: str) -> tuple[int, str]:
    r = subprocess.run([sys.executable, str(TOOLS / "world" / "run.py"), *args], capture_output=True, text=True)
    return r.returncode, (r.stdout + r.stderr).strip()


def probe_ids(assets: dict) -> str:
    items = [f"land:0x{i:04X}" for i, _ in assets["land"]]
    items += [f"static:0x{i:04X}" for i, _ in assets["statics"]]
    items += [f"gump:0x{i:04X}" for i, _ in assets["gumps"]]
    items += [f"hue:{i}" for i, _ in assets["hues"]]
    return ",".join(items)


def run_client(cfg, home: Path, out: Path, ids: str, override: Path | None) -> tuple[int, Path]:
    if home.exists():
        shutil.rmtree(home)
    (home / "cache").mkdir(parents=True)
    (home / "profiles").mkdir()
    settings = {"profilespath": str(home / "profiles")}
    if override is not None:
        settings["files_override"] = str(override)
    (home / "settings.json").write_text(json.dumps(settings, indent=2), encoding="utf-8")

    cmd = [str(cfg.godot_console_exe), "--headless", "--path", str(cfg.godot_project), "--",
           "--play", "--silent", "--asset-probe", str(out), "--asset-probe-ids", ids]
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version}
    log = home.parent / f"{home.name}.log"
    with log.open("w", encoding="utf-8", errors="replace") as f:
        code = subprocess.run(cmd, stdout=f, stderr=subprocess.STDOUT, env=env, timeout=600, **no_activate()).returncode
    return code, log


def client_pixels(path: Path, land: bool) -> tuple[int, int, list[int]]:
    """The probe's PNG back to UO colour; transparent stays 0 (land has none inside the diamond)."""
    return uoart.png_pixels(path, land)


def compare(assets: dict, got: Path) -> tuple[list[str], list[str]]:
    """Returns (matches, mismatches), one line each."""
    ok, bad = [], []
    for kind, stem in (("land", "land"), ("statics", "static"), ("gumps", "gump")):
        for id_, png in assets[kind]:
            land = kind == "land"
            w, h, want = uoart.png_pixels(png, land)
            f = got / f"{stem}_0x{id_:04X}.png"
            if not f.is_file():
                bad.append(f"{stem} 0x{id_:04X}: the client decoded nothing")
                continue
            gw, gh, have = client_pixels(f, land)
            if (gw, gh) != (w, h):
                bad.append(f"{stem} 0x{id_:04X}: client {gw}x{gh}, project {w}x{h}")
                continue
            diff = sum(a != b for a, b in zip(have, want))
            (ok if diff == 0 else bad).append(f"{stem} 0x{id_:04X} ({w}x{h}): {diff} pixels differ")
    report = json.loads((got / "asset_probe.json").read_text(encoding="utf-8")) if (got / "asset_probe.json").is_file() else {}
    for _, path in assets["hues"]:
        e = uoart.read_hue(path)
        have = [int(c, 16) for c in report.get(f"hue_{e.hue}", [])]
        n = sum(a == b for a, b in zip(have, e.colors))
        (ok if n == 32 else bad).append(f"hue {e.hue}: {n}/32 colours match")
    return ok, bad


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--project", type=Path, help="world project (default UO_WORLD_PROJECT)")
    ap.add_argument("--out", type=Path, help="output folder (default build/editor_asset_roundtrip)")
    ap.add_argument("--no-build", action="store_true", help="skip the C# build")
    args = ap.parse_args()

    cfg = load_config()
    project = (args.project or cfg.world_project).resolve()
    if not (project / "project.json").is_file():
        print(f"[roundtrip] not a world project: {project}")
        return 2
    assets = uoart.project_assets(project)
    if not any(assets.values()):
        print(f"[roundtrip] {project} replaces no assets")
        return 2
    out = (args.out or cfg.build / "editor_asset_roundtrip").resolve()
    out.mkdir(parents=True, exist_ok=True)

    if not args.no_build:
        r = subprocess.run(["dotnet", "build", str(cfg.godot_project / "GUO.csproj"), "-nologo", "-v", "q"],
                           capture_output=True, text=True)
        if r.returncode != 0:
            print(r.stdout[-3000:])
            print("[roundtrip] FAILED: the C# does not build")
            return 1

    export = out / "export"
    code, text = world("export", "--project", str(project), "--out", str(export))
    print("\n".join(f"  {line}" for line in text.splitlines()))
    if code != 0:
        print("[roundtrip] FAILED: export")
        return 1
    code, text = world("verify", "--project", str(project), "--out", str(export))
    print("\n".join(f"  {line}" for line in text.splitlines()))
    if code != 0:
        print("[roundtrip] FAILED: verify")
        return 1

    ids = probe_ids(assets)
    code, log = run_client(cfg, out / "client_home", out / "client", ids, export / "files_override.txt")
    print(f"[roundtrip] client (files_override -> export): exit {code}, log {log}")
    ok, bad = compare(assets, out / "client")

    # Control: the same probe without the override must NOT match.
    ccode, clog = run_client(cfg, out / "control_home", out / "control", ids, None)
    cok, _ = compare(assets, out / "control")
    print(f"[roundtrip] control (install only): exit {ccode}, {len(cok)} of {len(ok) + len(bad)} match the project")

    for line in ok:
        print(f"[roundtrip]   ok   {line}")
    for line in bad:
        print(f"[roundtrip]   FAIL {line}")

    summary = {
        "project": str(project), "export": str(export), "client_exit": code, "control_exit": ccode,
        "matches": ok, "mismatches": bad, "control_matches": cok,
    }
    (out / "report.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")

    if code != 0 or bad or cok:
        why = "the client probe failed" if code != 0 else "mismatches" if bad else "the control matched without the export"
        print(f"[roundtrip] FAILED: {why}")
        return 1
    print(f"[roundtrip] OK: {len(ok)} asset(s) exported, verified, and read back by the client unchanged")
    return 0


if __name__ == "__main__":
    sys.exit(main())
