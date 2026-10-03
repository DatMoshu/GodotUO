#!/usr/bin/env python3
"""Prove the GUO editor addon works: open the real editor and check it.

Builds the C#, starts the pinned Godot editor on the project with the addon's
smoke flag, and lets the addon check itself against the real client install
(`addons/guo_editor/EditorSmoke.cs`): the UO docks are in the editor, the
client data loaded through the ported loaders, an art search selects the art
and the inspector decoded real pixels. It writes a report, the decoded art
and a capture of the editor window to build/editor_smoke/<mode>/.

    python tools/editor_smoke/run.py                 # windowed: with a screenshot
    python tools/editor_smoke/run.py --headless      # no window, no screenshot
    python tools/editor_smoke/run.py --reload        # also rebuild + hot-reload
    python tools/editor_smoke/run.py --art 0x0E75

Or through the launcher:

    launchers\\dev\\editor_smoke.bat

--reload is open question 1 of docs/editor_plan.md made mechanical: after
the first pass the C# is rebuilt with the editor still open, the editor is
asked to reload the assembly, and the checks run again against the reloaded
addon. The editor log must say the old assembly context unloaded.

The windowed editor rewrites project.godot on exit (it drops the comments;
it does so with or without the addon). The file is restored afterwards so a
smoke run never leaves the tree dirty.

Exit codes:
    0  every check passed
    1  a check failed (see report.json and editor.log)
    2  the editor could not be started or timed out
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from guo.process import no_activate  # noqa: E402

TIMEOUT_S = 600


def build(project: Path) -> bool:
    print("[editor_smoke] building C#")
    result = subprocess.run(
        ["dotnet", "build", str(project / "GUO.csproj"), "-nologo", "-v", "q"],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        print(result.stdout[-4000:])
        print(result.stderr[-4000:])
    return result.returncode == 0


def world_tool(*args: str) -> tuple[int, str]:
    tool = Path(__file__).resolve().parents[1] / "world" / "run.py"
    r = subprocess.run([sys.executable, str(tool), *args], capture_output=True, text=True)
    return r.returncode, r.stdout + r.stderr


def asset_export_checks(cfg, project_dir: Path, out: Path) -> list[str]:
    """Phase 5 after the editor: export the smoke's asset project, verify it,
    prove verify catches a damaged patch, and prove an export into the install
    is refused without anything being written there."""
    failures = []
    export = out / "asset_export"
    code, text = world_tool("export", "--project", str(project_dir), "--out", str(export))
    ok_export = code == 0 and (export / "verdata.mul").is_file() and (export / "hues.mul").is_file()
    code, text = world_tool("verify", "--project", str(project_dir), "--out", str(export))
    ok_verify = code == 0
    if not ok_export or not ok_verify:
        failures.append(f"asset export/verify failed:\n{text}")

    # A damaged copy must fail verify: flip the last colour of the first patch.
    tampered = out / "asset_export_tampered"
    if tampered.exists():
        shutil.rmtree(tampered)
    shutil.copytree(export, tampered)
    raw = bytearray((tampered / "verdata.mul").read_bytes())
    count = int.from_bytes(raw[0:4], "little")
    pos = int.from_bytes(raw[12:16], "little")
    length = int.from_bytes(raw[16:20], "little")
    if count > 0:
        raw[pos + length - 2] ^= 0x1F
    (tampered / "verdata.mul").write_bytes(bytes(raw))
    code, _ = world_tool("verify", "--project", str(project_dir), "--out", str(tampered))
    caught = code != 0
    if not caught:
        failures.append("verify passed a damaged verdata.mul")

    # An export into UO_CLIENT_DATA is refused, and nothing is created there.
    inside = cfg.client_data / "guo_export_refusal_probe"
    before = sorted(p.name for p in cfg.client_data.iterdir())
    code, text = world_tool("export", "--project", str(project_dir), "--out", str(inside))
    after = sorted(p.name for p in cfg.client_data.iterdir())
    refused = code == 1 and "REFUSED" in text and not inside.exists() and before == after
    if not refused:
        failures.append(f"an export into UO_CLIENT_DATA was not refused cleanly (exit {code}):\n{text}")

    # A pack (what a player sends a shard owner) holds no install data, and
    # exports and verifies from wherever it is unzipped.
    import zipfile
    pack = out / "asset_pack" / "pack.zip"
    code, text = world_tool("pack", "--project", str(project_dir), "--out", str(pack))
    packed = code == 0 and pack.is_file()
    if packed:
        with zipfile.ZipFile(pack) as z:
            names = z.namelist()
            z.extractall(out / "asset_pack" / "unpacked")
        derived = [n for n in names if n.lower().endswith((".mul", ".uop", ".idx", ".def", ".bin"))]
        unpacked = out / "asset_pack" / "unpacked" / project_dir.name
        c1, _ = world_tool("export", "--project", str(unpacked), "--out", str(out / "asset_pack" / "export"))
        c2, text = world_tool("verify", "--project", str(unpacked), "--out", str(out / "asset_pack" / "export"))
        packed = not derived and c1 == 0 and c2 == 0
        if derived:
            failures.append(f"the pack holds files derived from the install: {derived}")
    if not packed:
        failures.append(f"pack, unzip, export and verify failed:\n{text}")

    # The client half: a headless client reads the export through
    # files_override and decodes the same pixels (tools/editor_asset_roundtrip).
    roundtrip = Path(__file__).resolve().parents[1] / "editor_asset_roundtrip" / "run.py"
    r = subprocess.run([sys.executable, str(roundtrip), "--project", str(project_dir),
                        "--out", str(out / "asset_roundtrip"), "--no-build"], capture_output=True, text=True)
    lines = [ln for ln in r.stdout.splitlines() if ln.startswith("[roundtrip]")]
    client_ok = r.returncode == 0
    if not client_ok:
        failures.append("the client did not read the export back unchanged:\n" + "\n".join(lines[-8:]))

    mark = "ok  " if not failures else "FAIL"
    print(f"[editor_smoke]   {'ok  ' if client_ok else 'FAIL'} Client  {lines[-1][len('[roundtrip] '):] if lines else 'no output'}")
    print(f"[editor_smoke]   {mark} Export  verdata.mul + hues.mul: export {'ok' if ok_export else 'FAILED'}, "
          f"verify {'ok' if ok_verify else 'FAILED'}, damaged copy caught: {caught}, "
          f"export into the install refused: {refused}, pack unzipped elsewhere exports: {packed}")
    return failures


def ultimalive_stale_checks(out: Path) -> list[str]:
    """tools/world notices a client's stale UltimaLive map copy after a re-export (and
    clears it when asked). Uses a scratch root, never the real %ProgramData%."""
    import json as _json
    failures = []
    project = out / "world_project"
    block = project / "blocks" / "0" / "187_203.json"
    if not block.is_file():
        return ["the overlay stage left no world project block to export"]
    root = out / "ultimalive_root"
    shard = root / "GUO-Smoke"
    exp1, exp2 = out / "ul_export1", out / "ul_export2"
    common = ["--ultimalive-root", str(root), "--ultimalive-shard", "GUO-Smoke"]
    code, text = world_tool("export", "--project", str(project), "--out", str(exp1), *common)
    if code != 0:
        return [f"export for the UltimaLive check failed: {text}"]
    # A client that played on the shard after that export: its copy is the exported map.
    shard.mkdir(parents=True)
    for name in ("map0.mul", "staidx0.mul", "statics0.mul"):
        shutil.copyfile(exp1 / name, shard / name)
    code, text = world_tool("ultimalive", "--project", str(project), "--out", str(exp1), *common)
    fresh = code == 0 and "already has this export" in text and "WARNING" not in text
    # The project is edited and exported again: the client's copy is now stale.
    j = _json.loads(block.read_text(encoding="utf-8"))
    j["statics"].append({"id": "0x0CE3", "x": 7, "y": 7, "z": 0, "hue": "0x0000"})
    block.write_text(_json.dumps(j, indent=2), encoding="utf-8")
    code, text = world_tool("export", "--project", str(project), "--out", str(exp2), *common)
    warned = code == 0 and "WARNING" in text and "lacks 1 of this export's 1 block(s)" in text and "--clear-ultimalive" in text
    code, text = world_tool("ultimalive", "--project", str(project), "--out", str(exp2), *common, "--clear-ultimalive")
    cleared = code == 0 and "removed" in text and not (shard / "map0.mul").exists()
    for ok, what in ((fresh, "a current copy was reported stale"), (warned, "a stale copy after a re-export was not reported"),
                     (cleared, "--clear-ultimalive did not remove the stale copy")):
        if not ok:
            failures.append(f"UltimaLive: {what}")
    print(f"[editor_smoke]   {'ok  ' if not failures else 'FAIL'} UltLive stale client map copy: current one accepted {fresh}, "
          f"stale one after a re-export reported {warned}, --clear-ultimalive removed it {cleared}")
    return failures


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--headless", action="store_true", help="no window (the default; kept for old command lines)")
    ap.add_argument("--windowed", action="store_true",
                    help="open an editor window for screenshots. It takes the desktop's focus while it runs: "
                         "only when the person at the machine agrees")
    ap.add_argument("--reload", action="store_true", help="also rebuild and hot-reload the assembly")
    ap.add_argument("--art", default="0x0E75", help="art id (static) the dock searches for")
    ap.add_argument("--multi-dump", default="",
                    help="comma list of multi ids whose panel composite is also saved as multi_XXXX.png")
    ap.add_argument("--out", type=Path, help="output folder (default build/editor_smoke/<mode>)")
    ap.add_argument("--no-build", action="store_true", help="skip the first C# build")
    args = ap.parse_args()

    cfg = load_config()
    project = cfg.godot_project
    # Headless unless asked: a window takes the foreground from whoever is
    # working at this machine (docs/editor_plan.md; the owner asked for this).
    args.headless = not args.windowed
    mode = ("headless" if args.headless else "windowed") + ("_reload" if args.reload else "")
    out = (args.out or cfg.build / "editor_smoke" / mode).resolve()

    # Old markers from an earlier run would make the addon think it is past a
    # reload already.
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    if not args.no_build and not build(project):
        print("[editor_smoke] FAILED: the C# does not build")
        return 1

    project_godot = project / "project.godot"
    before = project_godot.read_bytes()

    cmd = [str(cfg.godot_console_exe), "--editor", "--path", str(project)]
    if args.headless:
        cmd.insert(1, "--headless")
    cmd += ["--", "--guo-editor-smoke", str(out), "--guo-editor-smoke-art", args.art]
    if args.multi_dump:
        cmd += ["--guo-editor-multi-dump", args.multi_dump]
    if args.reload:
        cmd.append("--guo-editor-smoke-reload")

    log_path = out / "editor.log"
    print(f"[editor_smoke] {mode}: {' '.join(cmd)}")
    started = time.monotonic()
    rebuilt = None
    try:
        with log_path.open("w", encoding="utf-8", errors="replace") as log:
            proc = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, **no_activate())
            while proc.poll() is None:
                if time.monotonic() - started > TIMEOUT_S:
                    proc.kill()
                    print(f"[editor_smoke] FAILED: editor still running after {TIMEOUT_S} s; killed")
                    return 2
                if args.reload and rebuilt is None and (out / "reload.request").exists():
                    # Change the assembly on disk the way an edit would: touch a
                    # source so the incremental build recompiles and rewrites it.
                    (project / "addons" / "guo_editor" / "EditorSmoke.cs").touch()
                    rebuilt = build(project)
                    (out / "reload.go").write_text("rebuilt" if rebuilt else "build failed")
                time.sleep(0.5)
            code = proc.returncode
    except OSError as ex:
        print(f"[editor_smoke] FAILED: could not start {cmd[0]}: {ex}")
        return 2
    finally:
        if project_godot.read_bytes() != before:
            project_godot.write_bytes(before)
            print("[editor_smoke] restored project.godot (the editor rewrote it on exit)")

    report_path = out / "report.json"
    if not report_path.exists():
        print(f"[editor_smoke] FAILED: no report (editor exit {code}); see {log_path}")
        return 1

    report = json.loads(report_path.read_text(encoding="utf-8"))
    failures = list(report.get("failures", []))

    if args.reload:
        log = log_path.read_text(encoding="utf-8", errors="replace")
        if rebuilt is not True:
            failures.append("the rebuild between the two passes failed or never ran")
        if not report.get("reloaded"):
            failures.append("the second pass never ran: no reload reached the addon")
        if "Assembly load context unloaded successfully" not in log:
            failures.append("editor log does not show the old assembly context unloading")
        if "Failed to unload assemblies" in log:
            failures.append("editor log says the old assemblies failed to unload")

    if code not in (0, None) and not failures:
        failures.append(f"editor exited with {code}")

    print(f"[editor_smoke] client data : {report.get('client_data')} (loaded in {report.get('load_ms')} ms)")
    panels = report.get("panels", {})
    if args.reload and report.get("before_reload"):
        panels_before = report["before_reload"].get("panels", {})
    else:
        panels_before = {}
    for name, p in panels.items():
        what = f"{p.get('frames', 0)} frame(s) {p.get('image_size') or ''}".strip() if p.get("image_size") else "text only"
        extra = "  played" if p.get("played") else ""
        mark = "ok  " if p.get("ok") else "FAIL"
        print(f"[editor_smoke]   {mark} {name:<7} {p.get('query')!s:<12} -> {p.get('id')!s:<10} {what}, {p.get('ms')} ms{extra}")
        if name in panels_before and not panels_before[name].get("ok"):
            print(f"[editor_smoke]        (failed before the reload)")
    world = report.get("world") or {}
    if world:
        print(f"[editor_smoke]   {'ok  ' if world.get('ok', True) else 'FAIL'} World   map{(world.get('position') or ['?'])[0]} "
              f"{(world.get('position') or [0, '?', '?'])[1]},{(world.get('position') or [0, '?', '?'])[2]} "
              f"boot {world.get('boot_ms')} ms, {world.get('rendered_objects')} objects drawn, "
              f"{world.get('distinct_colours', 'no')} colours, picked: {world.get('picked')}")
    search = report.get("search") or {}
    if search:
        print(f"[editor_smoke]   {'ok  ' if search.get('ok') else 'FAIL'} Search  F3 index ready in {search.get('index_ms')} ms, "
              f"{search.get('menu_items')} editor menu items, {search.get('history_entries')} history entries")
        for q in search.get("queries", []):
            if "top_kind" in q:
                print(f"[editor_smoke]        {'ok  ' if q.get('ok') else 'FAIL'} {q['query']!r:<20} -> {q['top_kind']} {q['top']!r} ({q['ms']} ms)")
            else:
                print(f"[editor_smoke]        {'ok  ' if q.get('finds_backpack') else 'FAIL'} {q['query']!r:<20} finds the backpack: {q.get('finds_backpack')}")
    ai = report.get("ai") or {}
    if ai:
        checks = [k for k, v in ai.items() if v is True or v is False]
        bad = [k for k in checks if ai[k] is False]
        print(f"[editor_smoke]   {'ok  ' if ai.get('ok') else 'FAIL'} AI      {len(checks) - len(bad)}/{len(checks)} checks: ACP vs fake agent "
              f"({ai.get('chunks_streamed')} chunks streamed), Ollama + OpenAI-compatible stubs, queue in a temp db, "
              f"dock agent + permission dialog + chat + queue tab, children killed")
        for k in bad:
            print(f"[editor_smoke]        failed: {k}")
    overlay = world.get("overlay") or {}
    if overlay:
        print(f"[editor_smoke]   {'ok  ' if overlay.get('ok') else 'FAIL'} Overlay block 187,203: "
              f"{overlay.get('trees_in_chunk')} trees (install {overlay.get('base_trees')}), "
              f"{overlay.get('water_in_chunk')} water cells, after close {overlay.get('trees_after_close')} trees, "
              f"install untouched: {overlay.get('install_untouched')}")
    objects = world.get("objects") or {}
    if objects:
        print(f"[editor_smoke]   {'ok  ' if objects.get('ok') else 'FAIL'} Objects anvil + Horse spawner: placed "
              f"{objects.get('placed_drawn')} drawn, moved {objects.get('moved')}, deleted {objects.get('deleted')}, "
              f"file {objects.get('file_lines_with_ids')} objects, reopened {objects.get('reopened_drawn')} drawn")
    edit = world.get("edit") or {}
    if edit:
        checks = [k for k, v in edit.items() if v is True or v is False]
        passed = [k for k in checks if edit[k] is True]
        print(f"[editor_smoke]   {'ok  ' if edit.get('ok') else 'FAIL'} Edit    block 145,208: {len(passed)}/{len(checks)} checks "
              f"(stamp, hue, raise, erase, undo x4, redo, radar, layers, guides {edit.get('guide_cells')} cells)")
        for k in checks:
            if edit[k] is False:
                print(f"[editor_smoke]        failed: {k}")
    assets = report.get("assets") or {}
    if assets:
        print(f"[editor_smoke]   {'ok  ' if assets.get('ok') else 'FAIL'} Assets  land 0x0244, static 0x0E75, gump 0x0064, "
              f"hue 33: {assets.get('applied')} applied, pixels differing "
              f"{assets.get('land_pixels_differing')}/{assets.get('static_pixels_differing')}/{assets.get('gump_pixels_differing')}, "
              f"hue {assets.get('hue_colours_matching')}/32, revert {assets.get('static_reverted_pixels_differing')} differing, "
              f"World tab sees it: {assets.get('world_loader_sees_import')}")
        project_dir = Path(assets["project"])
        if assets.get("ok") and project_dir.is_dir():
            failures += asset_export_checks(cfg, project_dir, out)

    if (world.get("overlay") or {}).get("ok"):
        failures += ultimalive_stale_checks(out)

    shots = sorted(out.glob("editor_*.png"))
    if shots:
        print(f"[editor_smoke] screenshots : {len(shots)} in {out}")
    if args.reload and report.get("reloaded"):
        print("[editor_smoke] reload      : assembly rebuilt and reloaded; checks passed again")
    print(f"[editor_smoke] report      : {report_path}")

    if failures:
        for f in failures:
            print(f"[editor_smoke] FAIL: {f}")
        return 1

    print("[editor_smoke] OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
