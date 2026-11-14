#!/usr/bin/env python3
"""Competition benchmark runner (task 1155): drives the Labs CompetitionBench probe and scores it.

    python tools/competition_bench/run.py --label L --out EVIDENCE_DIR --builds common=PATH e1=PATH e2=PATH
        [--variants enhanced-off terrain ...] [--scenes ...] [--times ...] [--mode timed|capture]
        [--rounds 3] [--repeats 1] [--warmup-frames 1200] [--sample-frames 18000]
        [--precondition-s 20] [--rewarm-s 5] [--seed N]
        [--baseline-build common] [--baseline-label FROZEN_LABEL | --baseline-summary FILE]
    python tools/competition_bench/run.py --score SUMMARY_FILE --baseline-build common [--score-variant V ...]
    python tools/competition_bench/run.py --offparity FROZEN_STILLS_DIR ENTRANT_STILLS_DIR
    python tools/competition_bench/run.py --check-entrant COMMON_SHA ENTRANT_CHECKOUT

It builds nothing: it prints the build command. One Godot process per (round, build, scene, time,
variant), seeded-shuffled per round, Latin-square-like over builds. Debug GuoLabs only, never Release.
Warmup and sample lengths are FIXED NUMBERS OF SIMULATION FRAMES (1/60 s each); a wall-clock precondition and rewarm
precede them for thermal steady state, each followed by a sim-clock rebase.
Outputs go under --out (an explicit external folder; in-repo paths are refused). No shard is contacted.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import random
import re
import statistics
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from guo.config import find_repo_root  # noqa: E402
from guo.process import no_activate  # noqa: E402

SCENES = ("town-roofs", "sloped-terrain", "forest-edge")  # names only; coordinates live in CompetitionBenchScenes.cs
TIMES = ("noon", "dusk")
VARIANTS = ("classic", "enhanced-off", "terrain", "terrain-cloud-wind")
BASELINE_VARIANTS = ("classic", "enhanced-off")
BUILD_CMD = r"dotnet build godot\GUO\GUO.csproj -c Debug -p:GuoLabs=true -nologo -v:q"
LABS_ERROR = "the build has no CompetitionBenchProbe (built without GuoLabs?). Build it with: " + BUILD_CMD
EXIT_PROBE_MISSING, EXIT_TIMEOUT = 97, 98

# Scoring contract (frozen).
FREE_MS, DQ_MS, MAX_PENALTY = 2.5, 4.0, 10.0
NOISE_FLOOR_MS, NOISE_FRACTION = 0.3, 0.10
ENV_KEYS = ("adapter", "adapterApiVersion", "driver", "method", "build")

# Child environment allow-list (upper case). Everything else is dropped.
ENV_ALLOW = {"SYSTEMROOT", "WINDIR", "SYSTEMDRIVE", "TEMP", "TMP", "PATH", "PATHEXT", "COMSPEC", "USERPROFILE", "APPDATA",
             "LOCALAPPDATA", "PROGRAMDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "HOMEDRIVE", "HOMEPATH", "USERNAME",
             "COMPUTERNAME", "OS", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "HOME", "LANG", "DISPLAY",
             "UO_CLIENT_DATA", "UO_CLIENT_VERSION"}


def scrubbed_env(extra: dict[str, str]) -> dict[str, str]:
    env = {k: v for k, v in os.environ.items() if k.upper() in ENV_ALLOW or k.upper().startswith("GUO_BENCH_")}
    env.update(extra)
    return env


def penalty(added_ms: float) -> float | None:
    """0 up to 2.5 ms, linear 0..10 points between 2.5 and 4.0 ms, None (= DQ) above 4.0 ms."""
    if added_ms <= FREE_MS:
        return 0.0
    if added_ms > DQ_MS:
        return None
    return MAX_PENALTY * (added_ms - FREE_MS) / (DQ_MS - FREE_MS)


def band(added_ms: float) -> int:
    return 0 if added_ms <= FREE_MS else (1 if added_ms <= DQ_MS else 2)


def outcome_changes(worst_added: float, other_high: float) -> bool:
    """Would a non-worst key at (added + noise) change the penalty band or the points by more than 0.5?"""
    effective = max(worst_added, other_high)
    if band(effective) != band(worst_added):
        return True
    a, b = penalty(worst_added), penalty(effective)
    return a is not None and b is not None and abs(b - a) > 0.5


def noise_gate(repeat_medians: list[float]) -> tuple[float | None, bool | None]:
    """Noise = max - min of repeat medians; unstable above max(0.3 ms, 10%). One repeat: (None, None) = unknown."""
    if len(repeat_medians) < 2:
        return None, None
    noise = max(repeat_medians) - min(repeat_medians)
    return noise, noise > max(NOISE_FLOOR_MS, NOISE_FRACTION * statistics.median(repeat_medians))


def repeat_stats(bench: dict, metric: str = "gpu_ms", key: str = "median") -> list[float]:
    return [r[metric][key] for r in bench.get("repeatResults", []) if r.get(metric, {}).get("count")]


def summarize(benches: list[dict]) -> dict:
    """One config (all rounds): median of repeat medians, p95/p99, noise and stability (None = unknown)."""
    medians = [m for b in benches for m in repeat_stats(b)]
    row = {"processes": len(benches), "repeat_medians_gpu_ms": medians}
    if not medians:
        return {**row, "median_gpu_ms": None}
    noise, unstable = noise_gate(medians)
    row.update(median_gpu_ms=statistics.median(medians),
               p95_gpu_ms=statistics.median([m for b in benches for m in repeat_stats(b, "gpu_ms", "p95")]),
               p99_gpu_ms=statistics.median([m for b in benches for m in repeat_stats(b, "gpu_ms", "p99")]),
               median_frame_ms=statistics.median([m for b in benches for m in repeat_stats(b, "frame_ms")]),
               median_cpu_ms=statistics.median([m for b in benches for m in repeat_stats(b, "cpu_ms")]),
               noise_ms=noise, unstable=unstable,
               clock_drift=any(r.get("gpu_clock_drift_flag") for b in benches for r in b.get("repeatResults", [])))
    return row


def score(entry: dict, baseline: dict, planned: list[str] | None = None, entry_env: dict | None = None,
          baseline_env: dict | None = None, processes_ok: bool = True) -> dict:
    """entry/baseline: {"scene|time": summarize(...)}.

    INVALID unless every planned key has valid samples on both sides, every planned process passed and the
    adapter/driver/method/build match. PROVISIONAL is decided by the noise gate of the worst (DQ-driving) key
    only. Unknown noise (a single repeat) on the worst key refuses to certify a pass or a DQ (UNCERTIFIED).
    """
    planned = planned if planned is not None else sorted(entry)
    invalid = []
    if not processes_ok:
        invalid.append("a planned process did not pass")
    if entry_env is not None and baseline_env is not None:
        for k in ENV_KEYS:
            if entry_env.get(k) != baseline_env.get(k):
                invalid.append(f"environment mismatch on {k}: {entry_env.get(k)!r} vs {baseline_env.get(k)!r}")
    rows, worst_key = {}, None
    for key in planned:
        e, b = entry.get(key), baseline.get(key)
        if not e or e.get("median_gpu_ms") is None:
            invalid.append(f"{key}: no valid entry samples"); continue
        if not b or b.get("median_gpu_ms") is None:
            invalid.append(f"{key}: no valid baseline samples"); continue
        added = e["median_gpu_ms"] - b["median_gpu_ms"]
        rows[key] = {"entry_ms": e["median_gpu_ms"], "baseline_ms": b["median_gpu_ms"], "added_ms": added,
                     "p95_ms": e.get("p95_gpu_ms"), "p99_ms": e.get("p99_gpu_ms"), "noise_ms": e.get("noise_ms"),
                     "unstable": e.get("unstable"), "baseline_noise_ms": b.get("noise_ms"), "baseline_unstable": b.get("unstable"),
                     "clock_drift": bool(e.get("clock_drift") or b.get("clock_drift"))}
        if worst_key is None or added > rows[worst_key]["added_ms"]:
            worst_key = key
    out = {"per_scene_time": rows, "planned": planned, "invalid_reasons": invalid, "max_added_ms": None,
           "penalty_points": None, "disqualified": False, "provisional": False, "rerun": [], "worst_key": worst_key}
    out["rerun"] = [k for k, r in rows.items() if r["unstable"] or r["baseline_unstable"]]
    if invalid or worst_key is None:
        out["status"] = "INVALID"
        return out
    w = rows[worst_key]
    out["max_added_ms"] = w["added_ms"]
    pen = penalty(w["added_ms"])
    out["penalty_points"], out["disqualified"] = pen, pen is None
    unknown = w["unstable"] is None or w["baseline_unstable"] is None
    tripped = bool(w["unstable"] or w["baseline_unstable"])
    # Another key is a contender only if its added + noise could change the outcome: the penalty band
    # (crossing 2.5 or 4.0 ms) or the points by more than 0.5.
    contenders = [k for k, r in rows.items() if k != worst_key and outcome_changes(w["added_ms"], r["added_ms"] + (r["noise_ms"] or 0))]
    out["contenders"] = contenders
    drifted = [k for k, r in rows.items() if r["clock_drift"]]
    out["clock_drift_keys"] = drifted
    tripped = tripped or bool(drifted)  # any drifted repeat: bounded rerun or paired-control evidence before certification
    tripped = tripped or bool(contenders)
    if unknown:
        out["status"] = "UNCERTIFIED (single repeat: noise unknown)"
    elif tripped:
        out["provisional"] = True
        out["status"] = "PROVISIONAL pending bounded rerun" if out["disqualified"] else "scored (worst key noisy: rerun advised)"
    else:
        out["status"] = "DQ" if out["disqualified"] else "scored"
    return out


def group(results: list[dict], build: str, variant: str) -> dict:
    keys = sorted({f'{r["scene"]}|{r["time"]}' for r in results if r["build"] == build and r["variant"] == variant})
    return {k: summarize([r["bench"] for r in results if r["build"] == build and r["variant"] == variant
                          and f'{r["scene"]}|{r["time"]}' == k and r["bench"].get("status") == "pass"]) for k in keys}


def capture_group(results: list[dict], build: str, variant: str) -> dict:
    """Capture results carry no timing keys: summarise the still and its pixel hash."""
    return {f'{r["scene"]}|{r["time"]}': {"status": r["bench"].get("status"), "image_sha256": r["bench"].get("image_sha256"),
                                          "still": r["bench"].get("still")}
            for r in results if r["build"] == build and r["variant"] == variant}


def budget_text(warmup: int, sample: int, repeats: int, precondition_s: float, rewarm_s: float,
                ms_per_frame: float, processes: int) -> str:
    """Frames per process and an ESTIMATED wall time from the supplied ms/frame (never a claim in sim seconds)."""
    frames = repeats * (warmup + sample)
    est = precondition_s + (repeats - 1) * rewarm_s + frames * ms_per_frame / 1000.0
    return (f"per process: precondition {precondition_s:g} s wall, then {repeats} x ({warmup} warmup frames + {sample} sample frames), "
            f"rewarm {rewarm_s:g} s wall between repeats; {frames} frames at {ms_per_frame:g} ms/frame is about {est:.0f} s wall plus ~5 s startup; "
            f"{processes} processes is about {processes * (est + 5) / 60:.0f} min (estimate, not a measurement)")


def plan_order(builds: list[str], cells: list[tuple], rounds: int, seed: int) -> list[list[tuple]]:
    """Seeded shuffle per round. Cells are shuffled; builds inside a cell follow a Latin-square row
    (a seeded base permutation cyclically shifted by round + cell index), so each build takes every position."""
    base = random.Random(seed).sample(builds, len(builds))
    plan = []
    for rnd in range(rounds):
        rng = random.Random(seed * 1000003 + rnd)
        shuffled = cells[:]
        rng.shuffle(shuffled)
        row = []
        for i, cell in enumerate(shuffled):
            shift = (rnd + i) % len(base)
            for b in base[shift:] + base[:shift]:
                row.append((b, *cell))
        plan.append(row)
    return plan


def offparity(a: Path, b: Path) -> int:
    """Exact pixel equality of enhanced-off/classic stills between two stills folders (same GPU).

    Stills pair by the filename from the scene on (<scene>-<time>-<variant>), ignoring the label/build prefix,
    so fp1-common-town-roofs-noon-classic.png pairs with fp2-common-town-roofs-noon-classic.png. Keep one set per folder.
    """
    from PIL import Image
    import numpy as np

    def index(folder: Path) -> dict[str, Path]:
        out = {}
        for p in sorted(folder.glob("*.png")):
            if not p.stem.endswith(("-enhanced-off", "-classic")):
                continue
            for sc in SCENES:
                if f"-{sc}-" in p.stem:
                    out[p.stem[p.stem.index(f"-{sc}-") + 1:]] = p
        return out

    ia, ib = index(a), index(b)
    rows, ok = [], bool(ia) and set(ia) == set(ib)
    for name in sorted(set(ia) | set(ib)):
        if name not in ia or name not in ib:
            rows.append({"still": name, "equal": False, "error": "missing on one side"}); ok = False; continue
        x = np.asarray(Image.open(ia[name]).convert("RGBA")); y = np.asarray(Image.open(ib[name]).convert("RGBA"))
        if x.shape != y.shape:
            rows.append({"still": name, "equal": False, "error": f"size {x.shape} vs {y.shape}"}); ok = False; continue
        diff = int(np.any(x != y, axis=-1).sum())
        rows.append({"still": name, "equal": diff == 0, "different_pixels": diff}); ok &= diff == 0
    print(json.dumps({"offparity": "PASS" if ok else "FAIL", "rows": rows}, indent=2))
    return 0 if ok else 1


# ---- entrant check (checklist items 1-2) ----
HARNESS_FILES = [
    "godot/GUO/src/Labs/CompetitionBench/CompetitionBenchProbe.cs", "godot/GUO/src/Labs/CompetitionBench/CompetitionBenchProbe.tscn",
    "godot/GUO/src/Labs/CompetitionBench/CompetitionBenchClock.cs", "godot/GUO/src/Labs/CompetitionBench/CompetitionBenchScenes.cs",
    "godot/GUO/src/Labs/CompetitionBench/CompetitionBenchFrozen.cs", "godot/GUO/project.godot", "godot/GUO/GUO.csproj",
    "godot/GUO/src/Labs/CompetitionBench/README.md", "tools/competition_bench/run.py", "tools/competition_bench/test_run.py",
    "tools/competition_bench/README.md", "launchers/dev/competition_bench.bat", "launchers/dev/competition_bench.sh",
]
SEAM_FILES = ["godot/GUO/src/Client/GameController.cs", "godot/GUO/src/Render/DepthWorld/OrderedWorldCapture.cs"]
LABS_DIR = "godot/GUO/src/Labs/CompetitionBench/"
VARIANTS_FILE = LABS_DIR + "CompetitionVariants.cs"
BANNED_APIS = re.compile(r"\b(Engine\.|DisplayServer|RenderingServer|OS\.|Time\.|Environment|System\.Reflection|Task\b|Timer\b|Thread\b|"
                         r"AddChild|GetTree|Node\b|Stopwatch|DateTime|File\.|Process\b|Godot\.|CompetitionBench|BenchGpuMarkers|GUO_BENCH)")
BEGIN_RE = re.compile(r"private void RenderThread\(\)\s*\{\s*#if GUO_LABS\s*GUO\.Labs\.CompetitionBench\.BenchGpuMarkers\.PreDrawBegin\(\);[^\n]*\n\s*#endif")
TAIL_RE = re.compile(r"RecordTicks \+= [^\n]*;\s*Volatile\.Write\(ref _pending, 0\);\s*\}\s*\}\s*\n")
END_RE = re.compile(r"#if GUO_LABS\s*GUO\.Labs\.CompetitionBench\.BenchGpuMarkers\.PreDrawEnd\(\);\s*#endif\s*RecordTicks")
ALLOWED_SYMBOL_FILES = ("godot/GUO/src/Labs/CompetitionBench/", "tools/competition_bench/", "launchers/dev/competition_bench.",
                        "docs/data_formats.md", *SEAM_FILES)
SYMBOLS = ("CompetitionBench", "BenchGpuMarkers", "GUO_BENCH_")


def git_show(repo: Path, sha: str, rel: str) -> bytes | None:
    r = subprocess.run(["git", "-C", str(repo), "show", f"{sha}:{rel}"], capture_output=True)
    return r.stdout if r.returncode == 0 else None


def seam_lines(text: str) -> list[str]:
    return [ln.strip() for ln in text.splitlines() if any(s in ln for s in SYMBOLS)]


def doc_section(text: str) -> str:
    """The benchmark results section only, from its heading to the next '## ' heading (or the end).

    The section number is normalised away: it was 53 in the frozen common harness and is 54 on main."""
    m = re.search(r"^## \d+\. Competition benchmark results", text, re.M)
    if not m:
        return ""
    i = m.start()
    j = text.find("\n## ", i + 3)
    body = text[i:] if j < 0 else text[i:j]
    return re.sub(r"^## \d+\. ", "## N. ", body, count=1)


def seam_blocks(text: str) -> list[str]:
    """Every '#if GUO_LABS ... #endif' block that mentions a bench symbol, whitespace-normalised."""
    blocks = re.findall(r"#if GUO_LABS.*?#endif", text, flags=re.S)
    return [" ".join(b.split()) for b in blocks if any(sym in b for sym in SYMBOLS)]


def banned_api_hits(text: str) -> list[str]:
    code = re.sub(r"//[^\n]*", "", text)
    code = re.sub(r"namespace GUO\.Labs\.CompetitionBench;", "", code)
    code = re.sub(r"/\*.*?\*/", "", code, flags=re.S)
    return sorted({m.group(0) for m in BANNED_APIS.finditer(code)})


def check_entrant(sha: str, entrant: Path) -> int:
    problems = []
    for rel in HARNESS_FILES:
        want = git_show(entrant, sha, rel)
        path = entrant / rel
        got = path.read_bytes() if path.is_file() else None
        if want is None:
            problems.append(f"{rel}: not in {sha}")
        elif got is None:
            problems.append(f"{rel}: missing from the entrant checkout")
        elif got.replace(b"\r\n", b"\n") != want.replace(b"\r\n", b"\n"):  # line endings are checkout noise
            problems.append(f"{rel}: differs from the common harness (must be byte-identical)")
    labs = entrant / LABS_DIR
    allowed = {Path(r).name for r in HARNESS_FILES if r.startswith(LABS_DIR)} | {"CompetitionVariants.cs"}
    if labs.is_dir():
        for f in sorted(labs.iterdir()):
            if f.name not in allowed and f.suffix != ".uid":
                problems.append(f"{LABS_DIR}{f.name}: not a harness file (only CompetitionVariants.cs may be added or edited here)")
    variants = entrant / VARIANTS_FILE
    if not variants.is_file():
        problems.append(f"{VARIANTS_FILE}: missing")
    else:
        hits = banned_api_hits(variants.read_text(encoding="utf-8", errors="replace"))
        if hits:
            problems.append(f"{VARIANTS_FILE}: banned APIs {hits} (it may only map variants to option switches)")
    for rel in SEAM_FILES:
        want = git_show(entrant, sha, rel)
        path = entrant / rel
        got = path.read_text(encoding="utf-8", errors="replace") if path.is_file() else ""
        w = want.decode("utf-8", "replace") if want else ""
        if want is None or seam_blocks(w) != seam_blocks(got) or seam_lines(w) != seam_lines(got):
            problems.append(f"{rel}: harness seam blocks missing, altered or added")
        if rel.endswith("OrderedWorldCapture.cs") and (not BEGIN_RE.search(got) or not END_RE.search(got)):
            problems.append(f"{rel}: PreDrawBegin must be the first statement of RenderThread and PreDrawEnd must sit immediately before RecordTicks")
        if rel.endswith("OrderedWorldCapture.cs"):
            tail_a, tail_b = TAIL_RE.search(w), TAIL_RE.search(got)
            if not tail_b or not tail_a or " ".join(tail_a.group(0).split()) != " ".join(tail_b.group(0).split()):
                problems.append(f"{rel}: nothing may sit between the RecordTicks line and the end of the finally block (compared to common)")
    doc = git_show(entrant, sha, "docs/data_formats.md")
    dpath = entrant / "docs/data_formats.md"
    mine = dpath.read_text(encoding="utf-8", errors="replace") if dpath.is_file() else ""
    if doc is None or doc_section(doc.decode("utf-8", "replace")).replace("\r\n", "\n") != doc_section(mine).replace("\r\n", "\n"):
        problems.append("docs/data_formats.md: the benchmark results section differs from the common harness")
    skip = {".git", ".godot", "bin", "obj", "__pycache__", "node_modules"}
    for top in ("godot/GUO/src", "godot/GUO/scenes", "tools", "launchers", "docs"):
        for dirpath, dirs, files in os.walk(entrant / top):
            dirs[:] = [d for d in dirs if d not in skip]
            for f in files:
                rel = (Path(dirpath) / f).relative_to(entrant).as_posix()
                if rel.startswith(ALLOWED_SYMBOL_FILES) or rel.endswith((".png", ".dll", ".uid", ".pyc")):
                    continue
                try:
                    text = (Path(dirpath) / f).read_text(encoding="utf-8")
                except (UnicodeDecodeError, OSError):
                    continue
                hits = [s for s in SYMBOLS if s in text]
                if hits:
                    problems.append(f"{rel}: bench symbols {hits} outside the allowed files")
    print(json.dumps({"check_entrant": "PASS" if not problems else "FAIL", "common": sha, "entrant": str(entrant),
                      "problems": problems,
                      "coordinator_step": "rebuild the checked source (Debug GuoLabs) yourself and pass the DLL hash via --expect-dll NAME=SHA256"}, indent=2))
    return 0 if not problems else 1


# ---- fail-fast build check and launching ----
def newest_source(build_path: Path) -> float:
    newest = 0.0
    for dirpath, dirs, files in os.walk(build_path / "godot" / "GUO" / "src"):
        for f in files:
            if f.endswith(".cs"):
                newest = max(newest, (Path(dirpath) / f).stat().st_mtime)
    return newest


def check_labs_build(build_path: Path) -> None:
    """Fail fast: the Debug GuoLabs DLL must exist, be newer than the sources and contain the probe type."""
    dll = build_path / "godot" / "GUO" / ".godot" / "mono" / "temp" / "bin" / "Debug" / "GUO.dll"
    if not dll.is_file():
        sys.exit(f"[competition-bench] {build_path}: no built GUO.dll; {LABS_ERROR}")
    if b"CompetitionBenchProbe" not in dll.read_bytes():
        sys.exit(f"[competition-bench] {build_path}: {LABS_ERROR}")
    if dll.stat().st_mtime < newest_source(build_path):
        sys.exit(f"[competition-bench] {build_path}: GUO.dll is older than its newest source file (stale build). Rebuild with: {BUILD_CMD}")


def refuse_in_repo(out: Path, roots: list[Path]) -> None:
    for root in roots:
        if out == root or root in out.parents:
            sys.exit(f"[competition-bench] refusing --out inside a repository: {out}")


def kill_tree(proc: subprocess.Popen) -> None:
    if sys.platform == "win32":
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)], capture_output=True)
    else:
        proc.kill()
    proc.wait()


def launch(cfg, build_path: Path, env_extra: dict, log: Path, out: Path, timeout: int) -> tuple[int, float]:
    cache = out / "cache"
    cache.mkdir(parents=True, exist_ok=True)
    env = scrubbed_env({"UO_CLIENT_DATA": str(cfg.client_data), "UO_CLIENT_VERSION": cfg.client_version,
                        "GUO_BENCH_TIMEOUT_S": str(max(30, timeout - 30)), **env_extra})
    cmd = [str(cfg.godot_console_exe), "--path", str(build_path / "godot" / "GUO"), "--rendering-method", "forward_plus",
           "--rendering-driver", "vulkan", "res://src/Labs/CompetitionBench/CompetitionBenchProbe.tscn", "--",
           "--no-focus", "--no-splash", "--silent", "--pregame-classic", "--cache-dir", str(cache)]
    started = time.monotonic()
    with log.open("w", encoding="utf-8", errors="replace") as fh:
        proc = subprocess.Popen(cmd, stdout=fh, stderr=subprocess.STDOUT, env=env, **no_activate())
        offset = 0
        while proc.poll() is None:
            time.sleep(1)
            with log.open("rb") as rd:  # incremental tail: only the bytes written since the last look
                rd.seek(offset)
                chunk = rd.read()
                offset += len(chunk)
            if b"Cannot instantiate C# script" in chunk:
                kill_tree(proc)
                print(f"[competition-bench] FAIL probe did not start: {LABS_ERROR}", file=sys.stderr)
                return EXIT_PROBE_MISSING, time.monotonic() - started
            if time.monotonic() - started > timeout:
                kill_tree(proc)
                print(f"[competition-bench] FAIL TIMEOUT after {timeout} s wall (the probe was running; this is not a missing probe)", file=sys.stderr)
                return EXIT_TIMEOUT, time.monotonic() - started
    return proc.returncode, time.monotonic() - started


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git_dirty(path: Path) -> list[str]:
    """Tracked-file changes only (untracked files, e.g. under build/, are ignored)."""
    r = subprocess.run(["git", "-C", str(path), "status", "--porcelain", "--untracked-files=no"], capture_output=True, text=True)
    return r.stdout.splitlines()[:50] if r.returncode == 0 else ["<git status failed>"]


def valid_common_sha(value: str | None) -> bool:
    return bool(value) and re.fullmatch(r"[0-9a-f]{12,40}", value) is not None


def nvidia_driver_version() -> str:
    try:
        r = subprocess.run(["nvidia-smi", "--query-gpu=driver_version", "--format=csv,noheader"], capture_output=True, text=True, timeout=5)
        return r.stdout.strip().splitlines()[0] if r.returncode == 0 and r.stdout.strip() else "unavailable"
    except (OSError, subprocess.TimeoutExpired):
        return "unavailable"


def expect_problems(builds: dict, expect: list[str] | None, dev: bool) -> list[str]:
    """Every scored build needs --expect-dll (the coordinator rebuild's hash) unless --dev."""
    if dev:
        return []
    want = dict(item.partition("=")[::2] for item in expect or [])
    problems = [f"{b}: no --expect-dll (the coordinator's rebuild hash is required for scoring; --dev bypasses for local e2e)"
                for b in builds if b not in want]
    problems += [f"{b}: DLL sha256 {info.get('dll_sha256')} is not the coordinator rebuild {want[b]}"
                 for b, info in builds.items() if b in want and info.get("dll_sha256") != want[b]]
    return problems


def git_head(path: Path) -> str | None:
    r = subprocess.run(["git", "-C", str(path), "rev-parse", "HEAD"], capture_output=True, text=True)
    return r.stdout.strip() if r.returncode == 0 else None


def load_baseline(args, out: Path | None) -> tuple[dict | None, str | None]:
    path = args.baseline_summary
    if not path and args.baseline_label:
        folder = args.baseline_dir or out
        if not folder:
            sys.exit("[competition-bench] --baseline-label needs --baseline-dir or --out")
        path = folder / f"summary-{args.baseline_label}.json"
    if not path:
        return None, None
    if not path.is_file():
        sys.exit(f"[competition-bench] baseline summary not found: {path}")
    return json.loads(path.read_text(encoding="utf-8")), sha256_file(path)


def score_summary(summary: dict, args, frozen: dict | None, frozen_sha: str | None) -> dict:
    """Primary: each entry's variant against --baseline-build's enhanced-off in the SAME session. The frozen
    cross-session baseline is reported as a sanity delta (and used as primary only when no session build is named)."""
    result = {"baseline_build": args.baseline_build, "frozen_baseline_sha256": frozen_sha, "entries": {}, "control": {}}
    planned = summary["planned_keys"]
    control_problems = []
    if args.baseline_build:
        head = (summary.get("builds", {}).get(args.baseline_build) or {}).get("git_head")
        if not valid_common_sha(args.common_sha):
            control_problems.append("--common-sha is required: at least 12 hex characters")
        elif not head or not head.startswith(args.common_sha) and not args.common_sha.startswith(head):
            control_problems.append(f"control build {args.baseline_build} is at {head}, not the pinned common {args.common_sha}")
        if (summary.get("builds", {}).get(args.baseline_build) or {}).get("dirty"):
            control_problems.append(f"control build {args.baseline_build} has tracked-file changes (dirty working tree)")
        control_problems += expect_problems(summary.get("builds", {}), args.expect_dll, args.dev)
        result["control"] = {"git_head": head, "dll_sha256": (summary.get("builds", {}).get(args.baseline_build) or {}).get("dll_sha256"),
                             "pinned_common": args.common_sha}
    procs_ok = {b: all(r["exit"] == 0 and r["status"] in ("pass", "unsupported") for r in summary["run_order"] if r["build"] == b)
                for b in summary["groups"]}
    frozen_group = None
    if frozen:
        frozen_group = next(iter(frozen["groups"].values())).get("enhanced-off")
    if args.baseline_build and frozen and frozen.get("nvidia_driver_version") and summary.get("nvidia_driver_version") \
            and frozen["nvidia_driver_version"] != summary["nvidia_driver_version"]:
        control_problems.append(f"NVIDIA driver {summary['nvidia_driver_version']} differs from the frozen baseline's {frozen['nvidia_driver_version']}")
    if args.baseline_build and frozen:
        fg = next(iter(frozen["groups"].values())).get("enhanced-off", {})
        cg = summary["groups"][args.baseline_build].get("enhanced-off", {})
        for k in planned:
            f, c = fg.get(k, {}).get("median_gpu_ms"), cg.get(k, {}).get("median_gpu_ms")
            if f is None or c is None:
                control_problems.append(f"{k}: control or frozen baseline has no samples"); continue
            if abs(c - f) > max(NOISE_FLOOR_MS, NOISE_FRACTION * f, 0.3):
                control_problems.append(f"{k}: control enhanced-off {c:.3f} ms differs from the frozen baseline {f:.3f} ms beyond noise")
    result["control"]["problems"] = control_problems
    for build, variants in summary["groups"].items():
        if args.baseline_build and build != args.baseline_build and "enhanced-off" in variants:
            ctl = summary["groups"][args.baseline_build].get("enhanced-off", {})
            deltas = {}
            for k in planned:
                a, b = variants["enhanced-off"].get(k, {}).get("median_gpu_ms"), ctl.get(k, {}).get("median_gpu_ms")
                if a is None or b is None:
                    continue
                d = a - b
                deltas[k] = {"own_enhanced_off_minus_control_ms": d,
                             **({"flag": "shared-path speedup: review"} if d < -max(NOISE_FLOOR_MS, NOISE_FRACTION * b) else {})}
            result.setdefault("entrant_enhanced_off_vs_control", {})[build] = deltas
        for variant, g in variants.items():
            if args.score_variant:
                if variant not in args.score_variant:
                    continue
            elif variant in BASELINE_VARIANTS:
                continue
            if args.baseline_build:
                base = summary["groups"][args.baseline_build].get("enhanced-off", {})
                base_env, base_ok = summary["env"].get(args.baseline_build), procs_ok[args.baseline_build]
            elif frozen_group is not None:
                base, base_env, base_ok = frozen_group, next(iter(frozen["env"].values()), None), True
            else:
                sys.exit("[competition-bench] scoring needs --baseline-build (same session) or a frozen baseline summary")
            entry = score(g, base, planned, summary["env"].get(build), base_env, procs_ok[build] and base_ok)
            if control_problems:
                entry["status"] = "INVALID"
                entry["invalid_reasons"] = control_problems + entry["invalid_reasons"]
            if frozen_group is not None:
                entry["cross_session_sanity"] = {
                    k: {"entry_minus_frozen_ms": g[k]["median_gpu_ms"] - frozen_group[k]["median_gpu_ms"]}
                    for k in planned if g.get(k, {}).get("median_gpu_ms") is not None
                    and frozen_group.get(k, {}).get("median_gpu_ms") is not None}
            result["entries"][f"{build}/{variant}"] = entry
    return result


def doctor() -> int:
    """Check the runner/test dependencies without starting a game or a benchmark."""
    missing = [package for module, package in (("numpy", "numpy"), ("PIL", "Pillow"), ("pytest", "pytest"))
               if importlib.util.find_spec(module) is None]
    if missing:
        print("[competition-bench] install dependencies: python -m pip install " + " ".join(missing))
        return 1
    print("[competition-bench] Python dependencies OK (numpy, Pillow, pytest)")
    cfg = load_config()
    if not cfg.godot_console_exe.is_file():
        print("[competition-bench] engine missing; run launchers/pipeline/00_bootstrap")
        return 1
    print("[competition-bench] engine found; build the optional Labs probe before a native run:\n  " + BUILD_CMD)
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--doctor", action="store_true", help="check dependencies; no game or benchmark is launched")
    ap.add_argument("--label", default="bench")
    ap.add_argument("--out", type=Path, help="external evidence run folder (required to run)")
    ap.add_argument("--builds", nargs="+", metavar="NAME=PATH", help="checkouts/worktrees with their own built Debug GuoLabs DLL (default: this checkout)")
    ap.add_argument("--variants", nargs="+", default=["enhanced-off"], choices=VARIANTS)
    ap.add_argument("--scenes", nargs="+", default=list(SCENES), choices=SCENES)
    ap.add_argument("--times", nargs="+", default=list(TIMES), choices=TIMES)
    ap.add_argument("--mode", choices=("timed", "capture"), default="timed")
    ap.add_argument("--rounds", type=int, default=3, help="timed: rounds; each runs every config once, seeded shuffle per round")
    ap.add_argument("--repeats", type=int, default=1, help="GUO_BENCH_REPEATS inside each process")
    ap.add_argument("--warmup-frames", type=int, default=1200, help="simulation frames (1/60 s each), not wall seconds")
    ap.add_argument("--sample-frames", type=int, default=18000, help="simulation frames sampled per repeat (about 36-54 s wall at 2-3 ms/frame)")
    ap.add_argument("--precondition-s", type=float, default=20, help="WALL seconds of normal rendering before each process's first warmup (thermal state)")
    ap.add_argument("--rewarm-s", type=float, default=5, help="WALL seconds between repeats before the frame-count warmup")
    ap.add_argument("--ms-per-frame", type=float, default=3.0, help="measured ms/frame, used only for the budget estimate")
    ap.add_argument("--seed", type=int, default=1155)
    ap.add_argument("--raw", action="store_true")
    ap.add_argument("--baseline-build", help="build in THIS session whose enhanced-off is the denominator (primary scoring)")
    ap.add_argument("--common-sha", help="pinned commit of the common (control) build; the --baseline-build checkout must be at it")
    ap.add_argument("--expect-dll", nargs="+", metavar="NAME=SHA256", help="DLL hashes the coordinator got from rebuilding the checked source")
    ap.add_argument("--dev", action="store_true", help="local e2e: bypass the required --expect-dll hashes")
    ap.add_argument("--baseline-label", help="frozen common run label: summary-<label>.json in --baseline-dir (or --out)")
    ap.add_argument("--baseline-dir", type=Path)
    ap.add_argument("--baseline-summary", type=Path, help="explicit frozen common summary file (sha256 is recorded)")
    ap.add_argument("--score", type=Path, help="score an existing summary file")
    ap.add_argument("--score-variant", nargs="+", help="variants to score (default: all but classic/enhanced-off)")
    ap.add_argument("--offparity", nargs=2, type=Path, metavar=("FROZEN_STILLS", "ENTRANT_STILLS"))
    ap.add_argument("--check-entrant", nargs=2, metavar=("COMMON_SHA", "ENTRANT_PATH"))
    ap.add_argument("--timeout", type=int, default=3600, help="wall seconds per process (timeout only; exit 98)")
    args = ap.parse_args()

    if args.doctor:
        return doctor()
    if args.offparity:
        return offparity(*args.offparity)
    if args.check_entrant:
        return check_entrant(args.check_entrant[0], Path(args.check_entrant[1]).resolve())
    frozen, frozen_sha = load_baseline(args, args.out)
    if args.score:
        summary = json.loads(args.score.read_text(encoding="utf-8"))
        print(json.dumps(score_summary(summary, args, frozen, frozen_sha), indent=2))
        return 0
    if not args.out:
        ap.error("--out (external evidence folder) is required")
    root = find_repo_root()
    out = args.out.resolve()
    builds = {}
    for item in args.builds or [f"common={root}"]:
        name, _, path = item.partition("=")
        builds[name] = Path(path).resolve()
    if args.baseline_build and args.baseline_build not in builds:
        ap.error("--baseline-build must name one of --builds")
    refuse_in_repo(out, [root, *builds.values()])
    print(f"[competition-bench] build each checkout first (Debug GuoLabs, never Release):\n  {BUILD_CMD}")
    for path in builds.values():
        check_labs_build(path)
    if args.baseline_build and not valid_common_sha(args.common_sha):
        sys.exit("[competition-bench] --common-sha (at least 12 hex characters) is required with --baseline-build")
    dll_of = lambda p: p / "godot" / "GUO" / ".godot" / "mono" / "temp" / "bin" / "Debug" / "GUO.dll"  # noqa: E731
    if args.mode == "timed" and (args.baseline_build or args.baseline_label or args.baseline_summary):
        bad = expect_problems({b: {"dll_sha256": sha256_file(dll_of(p))} for b, p in builds.items()}, args.expect_dll, args.dev)
        if bad:
            sys.exit("[competition-bench] " + "; ".join(bad))
    cfg = load_config()
    out.mkdir(parents=True, exist_ok=True)
    cells = [(s, t, v) for s in args.scenes for t in args.times for v in args.variants]
    plan = plan_order(list(builds), cells, args.rounds if args.mode == "timed" else 1, args.seed)
    if args.mode == "timed":
        print("[competition-bench] budget: " + budget_text(args.warmup_frames, args.sample_frames, args.repeats, args.precondition_s,
                                                           args.rewarm_s, args.ms_per_frame, sum(len(r) for r in plan)))
    runs, results = [], []
    for rnd, order in enumerate(plan):
        for build, scene, tm, variant in order:
            label = f"{args.label}-{build}-r{rnd}" if args.mode == "timed" else f"{args.label}-{build}"
            env = {"GUO_BENCH_OUT": str(out), "GUO_BENCH_LABEL": label, "GUO_BENCH_SCENE": scene, "GUO_BENCH_TIME": tm,
                   "GUO_BENCH_VARIANT": variant, "GUO_BENCH_MODE": args.mode,
                   "GUO_BENCH_WARMUP_FRAMES": str(args.warmup_frames), "GUO_BENCH_SAMPLE_FRAMES": str(args.sample_frames),
                   "GUO_BENCH_PRECONDITION_S": str(args.precondition_s), "GUO_BENCH_REWARM_S": str(args.rewarm_s),
                   "GUO_BENCH_REPEATS": str(args.repeats),
                   "GUO_BENCH_RAW": "1" if args.raw else "0"}
            log = out / "logs" / f"{label}-{scene}-{tm}-{variant}.log"
            log.parent.mkdir(parents=True, exist_ok=True)
            began = datetime.now(timezone.utc).isoformat()
            code, wall = launch(cfg, builds[build], env, log, out, args.timeout)
            data = out / "data" / label / f"{scene}-{tm}-{variant}" / ("capture.json" if args.mode == "capture" else "bench.json")
            bench = json.loads(data.read_text(encoding="utf-8")) if data.is_file() else {"status": "missing"}
            runs.append({"round": rnd, "position": len([r for r in runs if r["round"] == rnd]), "build": build, "scene": scene,
                         "time": tm, "variant": variant, "label": label, "started_utc": began, "wall_s": wall,
                         "startup_s": bench.get("startup_s"), "exit": code, "status": bench.get("status")})
            results.append({"build": build, "scene": scene, "time": tm, "variant": variant, "bench": bench})
            print(f"[competition-bench] round {rnd} {build} {scene} {tm} {variant}: exit {code} {bench.get('status')} ({wall:.0f}s)", flush=True)
    make = capture_group if args.mode == "capture" else group
    groups = {b: {v: make(results, b, v) for v in args.variants} for b in builds}
    env_by_build = {}
    for r in results:
        e = r["bench"].get("environment")
        if e and r["build"] not in env_by_build:
            env_by_build[r["build"]] = {k: e.get(k) for k in ENV_KEYS} | {"windowSize": e.get("windowSize")}

    def dll(p: Path) -> Path:
        return p / "godot" / "GUO" / ".godot" / "mono" / "temp" / "bin" / "Debug" / "GUO.dll"

    summary = {"label": args.label, "mode": args.mode, "build": "Debug GuoLabs", "seed": args.seed, "rounds": len(plan),
               "frames": {"warmup": args.warmup_frames, "sample": args.sample_frames, "precondition_s": args.precondition_s, "rewarm_s": args.rewarm_s,
                          "repeats": args.repeats, "sim_step_s": 1 / 60},
               "planned_keys": sorted({f"{s}|{t}" for s, t, _ in cells}), "env": env_by_build,
               "builds": {b: {"path": str(p), "git_head": git_head(p), "dirty": git_dirty(p), "dll_sha256": sha256_file(dll(p))} for b, p in builds.items()},
               "nvidia_driver_version": nvidia_driver_version(),
               "run_order": runs, "groups": groups, "failures": [r for r in runs if r["exit"] != 0],
               "unsupported": [r for r in runs if r["status"] == "unsupported"]}
    if args.mode == "timed" and (args.baseline_build or frozen):
        summary["scores"] = score_summary(summary, args, frozen, frozen_sha)
    target = out / f"summary-{args.label}.json"
    target.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(f"[competition-bench] summary: {target}")
    return 1 if summary["failures"] else 0


if __name__ == "__main__":
    sys.exit(main())
