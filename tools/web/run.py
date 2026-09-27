r"""Export, serve and smoke-test GUO as a web page.

    launchers\web\doctor.bat      what a Godot 4.7 .NET web export needs, and
                                  what this machine has
    launchers\web\export.bat      headless --export-debug Web into build\web
    launchers\web\serve.bat       serve build\web with the cross-origin
                                  isolation headers a threaded export needs
    launchers\web\smoke.bat       export, serve, open it in a headless browser
                                  and wait for the engine's first log line

    python tools\web\run.py <doctor|preset|export|serve|smoke> [...]

READ THIS FIRST: Godot 4.7.2 mono cannot export a C# project to the web. The
mono export templates ship no web template, and the engine refuses the
export before it starts (`doctor` and `export` print the exact refusal).
ADR-0008 records what was found and what would have to change. This tool
exists so the day the templates arrive, the pipeline is a config change; and
so `doctor` says precisely why it is not today.

The export preset is rendered from export_presets.template.cfg into
godot\GUO\export_presets.cfg, which is gitignored; the Android tool renders
the same file from its own template, and each tool re-renders before it
exports.

WHAT IT TOUCHES

  godot\GUO\export_presets.cfg     rendered; gitignored
  godot\GUO\GUO.sln                generated if missing; gitignored
  build\web\                       the export, its log, the smoke's output
"""

from __future__ import annotations

import argparse
import functools
import http.server
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import Config, load_config  # noqa: E402

HERE = Path(__file__).resolve().parent
TEMPLATE = HERE / "export_presets.template.cfg"
PRESET_NAME = "Web"

# Godot's threaded web template is only allowed to run in a cross-origin
# isolated page (SharedArrayBuffer). These two headers are what make one.
ISOLATION_HEADERS = {
    "Cross-Origin-Opener-Policy": "same-origin",
    "Cross-Origin-Embedder-Policy": "require-corp",
    "Cross-Origin-Resource-Policy": "same-origin",
    "Cache-Control": "no-store",
}


# ---------------------------------------------------------------------------
# paths
# ---------------------------------------------------------------------------


class Paths:
    def __init__(self, cfg: Config):
        self.cfg = cfg
        self.project = cfg.godot_project
        self.out_dir = cfg.build / "web"
        self.page = self.out_dir / "GUO.html"
        self.preset_file = self.project / "export_presets.cfg"
        self.solution = self.project / "GUO.sln"
        self.port = cfg.web_port

        appdata = Path(os.environ.get("APPDATA", str(Path.home() / "AppData" / "Roaming")))
        self.godot_config = appdata / "Godot"
        # "4.7.2-stable" + mono -> "4.7.2.stable.mono", Godot's folder name.
        self.templates_version = cfg.godot_version.replace("-", ".") + ".mono"
        self.templates_dir = self.godot_config / "export_templates" / self.templates_version
        # What a web export needs from the templates folder. The mono .tpz
        # for 4.7.2 ships none of these (see doctor).
        self.web_templates = [
            self.templates_dir / "web_dlink_debug.zip",
            self.templates_dir / "web_debug.zip",
        ]

    def godot_console(self) -> Path:
        env = os.environ.get("GODOT_CONSOLE")
        if env and Path(env).exists():
            return Path(env)
        return self.cfg.godot_console_exe


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------


def say(msg: str) -> None:
    print(f"[web] {msg}", flush=True)


def run(cmd: list[str], **kw) -> subprocess.CompletedProcess:
    say("$ " + " ".join(f'"{c}"' if " " in str(c) else str(c) for c in cmd))
    return subprocess.run([str(c) for c in cmd], **kw)


def dotnet_wasm_workloads() -> tuple[bool, str]:
    """Whether the .NET SDK has a wasm workload installed, and what it said.

    `dotnet workload list` crashes on some Windows SDK installs (a COM error,
    0x8007007E) before printing anything; that is reported as-is rather than
    guessed around.
    """
    dotnet = shutil.which("dotnet")
    if not dotnet:
        return False, "dotnet not on PATH"
    try:
        r = subprocess.run([dotnet, "workload", "list"], capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired) as e:
        return False, f"dotnet workload list did not run: {e}"
    out = (r.stdout or "") + (r.stderr or "")
    if r.returncode != 0:
        first = next((line for line in out.splitlines() if line.strip()), "no output")
        return False, f"dotnet workload list exited {r.returncode}: {first.strip()[:120]}"
    have = [line.split()[0] for line in out.splitlines() if line.strip().startswith("wasm")]
    if have:
        return True, ", ".join(have)
    return False, "no wasm-* workload installed"


# ---------------------------------------------------------------------------
# doctor
# ---------------------------------------------------------------------------


class Doctor:
    def __init__(self, p: Paths):
        self.p = p
        self.missing = 0

    def check(self, what: str, ok: bool, detail: str, fix: str | None = None) -> bool:
        mark = "ok  " if ok else "MISS"
        print(f"  {mark} {what:<28} {detail}")
        if not ok:
            self.missing += 1
            if fix:
                print(f"       fix: {fix}")
        return ok

    def run(self) -> int:
        p, cfg = self.p, self.p.cfg
        print("[web] doctor -- what a Godot 4.7 .NET web export needs on this machine\n")

        dotnet = shutil.which("dotnet")
        self.check("dotnet SDK", dotnet is not None, dotnet or "not on PATH",
                   "install the .NET 8 SDK (or newer) and put dotnet on PATH")

        console = p.godot_console()
        self.check("Godot console (mono)", console.exists(), str(console),
                   r"launchers\dev\fetch_godot.bat, or set GODOT_EXE in config.bat")

        have = [t for t in p.web_templates if t.exists()]
        self.check(
            "Web export templates", bool(have),
            str(have[0]) if have else f"none of {', '.join(t.name for t in p.web_templates)} in {p.templates_dir}",
            "none exist for a mono build of Godot 4.x: the .NET web export is unsupported upstream "
            "(godotengine/godot#70796). See ADR-0008.",
        )

        listed = sorted(f.name for f in p.templates_dir.iterdir()) if p.templates_dir.exists() else []
        web_any = [n for n in listed if n.startswith("web")]
        print(f"       templates folder holds {len(listed)} files, {len(web_any)} of them web_*")

        ok, detail = dotnet_wasm_workloads()
        self.check("dotnet wasm workload", ok, detail,
                   "dotnet workload install wasm-tools   (only useful once the engine can export)")

        self.check("preset template", TEMPLATE.exists(), str(TEMPLATE))
        self.check("serve port", 0 < p.port < 65536, f"UO_WEB_PORT={p.port}")

        browser = find_browser()
        self.check("headless browser", browser is not None, browser or "no Chrome/Edge/Chromium found",
                   "install Chrome or Edge, or set UO_WEB_BROWSER in config.bat to a Chromium binary")

        print()
        if self.missing:
            say(f"{self.missing} of the checks above failed")
            return 1
        say("everything a web export needs is here")
        return 0


# ---------------------------------------------------------------------------
# preset / export
# ---------------------------------------------------------------------------


def render_preset(p: Paths, export_path: Path) -> Path:
    text = TEMPLATE.read_text(encoding="utf-8")
    values = {"EXPORT_PATH": str(export_path).replace("\\", "/")}
    for key, value in values.items():
        text = text.replace("{{" + key + "}}", value)
    leftover = re.findall(r"{{\w+}}", text)
    if leftover:
        sys.exit(f"[web] template placeholders without a value: {leftover}")
    p.preset_file.write_text(text, encoding="utf-8")
    say(f"rendered {p.preset_file}")
    return p.preset_file


def ensure_solution(p: Paths) -> None:
    if p.solution.exists():
        return
    say("no GUO.sln; generating one (the .NET export needs it)")
    run(["dotnet", "new", "sln", "-n", "GUO", "-o", p.project], stdout=subprocess.DEVNULL)
    run(["dotnet", "sln", p.solution, "add", p.project / "GUO.csproj"], stdout=subprocess.DEVNULL)
    if not p.solution.exists():
        sys.exit("[web] could not produce GUO.sln")


def export(p: Paths, page: Path) -> int:
    console = p.godot_console()
    if not console.exists():
        sys.exit(f"[web] Godot console not found at {console}; run doctor")
    p.out_dir.mkdir(parents=True, exist_ok=True)
    ensure_solution(p)
    render_preset(p, page)
    for old in p.out_dir.glob(page.stem + ".*"):
        old.unlink()

    log = p.out_dir / "export.log"
    with open(log, "w", encoding="utf-8") as f:
        result = run(
            [console, "--headless", "--path", p.project, "--export-debug", PRESET_NAME, page],
            stdout=f, stderr=subprocess.STDOUT, text=True,
        )
    text = log.read_text(encoding="utf-8", errors="replace")
    for line in text.splitlines():
        if re.search(r"error|failed|not found|required|not support", line, re.IGNORECASE):
            print("  " + line.strip())
    if result.returncode != 0 or not page.exists():
        say(f"export FAILED (exit {result.returncode}); full log: {log}")
        return 1
    say(f"exported {page}; log: {log}")
    return 0


# ---------------------------------------------------------------------------
# serve
# ---------------------------------------------------------------------------


class IsolatedHandler(http.server.SimpleHTTPRequestHandler):
    """Static files plus the headers that make the page cross-origin isolated."""

    extensions_map = {
        **http.server.SimpleHTTPRequestHandler.extensions_map,
        ".wasm": "application/wasm",
        ".js": "text/javascript",
        ".pck": "application/octet-stream",
    }

    def end_headers(self):
        for k, v in ISOLATION_HEADERS.items():
            self.send_header(k, v)
        super().end_headers()

    def log_message(self, fmt, *args):
        say(f"http {self.address_string()} {fmt % args}")


def make_server(p: Paths, root: Path) -> http.server.ThreadingHTTPServer:
    handler = functools.partial(IsolatedHandler, directory=str(root))
    return http.server.ThreadingHTTPServer(("127.0.0.1", p.port), handler)


def serve(p: Paths, root: Path) -> int:
    if not root.exists():
        say(f"nothing to serve: {root} does not exist (run export first)")
        return 1
    server = make_server(p, root)
    say(f"serving {root} at http://127.0.0.1:{p.port}/{p.page.name}  (Ctrl+C stops)")
    say("headers: " + "; ".join(f"{k}: {v}" for k, v in ISOLATION_HEADERS.items()))
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


# ---------------------------------------------------------------------------
# smoke
# ---------------------------------------------------------------------------


def find_browser() -> str | None:
    env = os.environ.get("UO_WEB_BROWSER")
    if env and Path(env).exists():
        return env
    candidates = [
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    ]
    for c in candidates:
        if Path(c).exists():
            return c
    for name in ("chrome", "msedge", "chromium", "chromium-browser", "google-chrome"):
        found = shutil.which(name)
        if found:
            return found
    return None


def smoke(p: Paths, timeout: int, skip_export: bool) -> int:
    """Export, serve, load the page headless, and wait for the engine to speak.

    The browser's console is what a web build has instead of stdout, so it
    is what is watched: the page passes once a "[GUO]" line (the client's own
    log prefix) or Godot's "Godot Engine v" banner shows up in it, and fails
    on a page error, a missing SharedArrayBuffer, or the timeout.
    """
    if not skip_export:
        rc = export(p, p.page)
        if rc != 0:
            say("smoke FAILED at export; nothing to load")
            return rc
    if not p.page.exists():
        say(f"smoke FAILED: {p.page} does not exist")
        return 1

    browser = find_browser()
    if not browser:
        say("smoke FAILED: no headless browser (set UO_WEB_BROWSER)")
        return 1

    server = make_server(p, p.out_dir)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    url = f"http://127.0.0.1:{p.port}/{p.page.name}"
    say(f"serving {p.out_dir} at {url}")

    console_log = p.out_dir / "smoke_console.txt"
    shot = p.out_dir / "smoke.png"
    if shot.exists():
        shot.unlink()
    # --enable-logging=stderr --v=1 makes Chromium print every console.log
    # line as CONSOLE(n) to stderr; that is the whole test harness.
    cmd = [
        browser, "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
        "--window-size=1280,720", "--enable-logging=stderr", "--v=1",
        f"--virtual-time-budget={timeout * 1000}", f"--screenshot={shot}", url,
    ]
    say("$ " + " ".join(cmd))
    with open(console_log, "w", encoding="utf-8") as f:
        try:
            proc = subprocess.run(cmd, stdout=f, stderr=subprocess.STDOUT, text=True, timeout=timeout + 60)
            rc = proc.returncode
        except subprocess.TimeoutExpired:
            rc = -1
    server.shutdown()

    text = console_log.read_text(encoding="utf-8", errors="replace")
    console_lines = [line for line in text.splitlines() if "CONSOLE" in line]
    ok = any("[GUO]" in line or "Godot Engine v" in line for line in console_lines)
    bad = [line for line in console_lines if re.search(r"SharedArrayBuffer|Uncaught|is not defined|failed to", line)]
    for line in console_lines[:40]:
        print("  " + line.strip()[:200])
    say(f"console log: {console_log}; screenshot: {shot if shot.exists() else 'none'}")
    if ok and not bad:
        say("smoke ok: the engine started in the page")
        return 0
    say(f"smoke FAILED (browser exit {rc}; engine spoke: {ok}; errors: {len(bad)})")
    return 1


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("doctor", help="check the toolchain and say what is missing")
    sub.add_parser("preset", help="render export_presets.cfg from the template")
    ex = sub.add_parser("export", help="export the web build, headless")
    ex.add_argument("--out", default=None, help="page path (default build\\web\\GUO.html)")
    sv = sub.add_parser("serve", help="serve build\\web with cross-origin isolation headers")
    sv.add_argument("--root", default=None, help="folder to serve (default build\\web)")
    sm = sub.add_parser("smoke", help="export, serve, load headless, wait for the engine's first log line")
    sm.add_argument("--timeout", type=int, default=120, help="seconds to give the page")
    sm.add_argument("--no-export", action="store_true", help="reuse build\\web")

    args = parser.parse_args(argv)
    p = Paths(load_config())

    if args.command == "doctor":
        return Doctor(p).run()
    if args.command == "preset":
        render_preset(p, p.page)
        return 0
    if args.command == "export":
        return export(p, Path(args.out) if args.out else p.page)
    if args.command == "serve":
        return serve(p, Path(args.root) if args.root else p.out_dir)
    if args.command == "smoke":
        return smoke(p, args.timeout, args.no_export)
    return 2


if __name__ == "__main__":
    sys.exit(main())
