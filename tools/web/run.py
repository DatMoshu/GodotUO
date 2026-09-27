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
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import urllib.parse
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

        # The fork that can export C# to the web (tools/godot_web, ADR-0008
        # amendment 1), self-contained: its templates live in its own
        # editor_data, not in %APPDATA%.
        self.web_godot = cfg.web_godot if cfg.web_godot and cfg.web_godot.exists() else None
        self.godot_web_dir = self.web_godot.parent.parent if self.web_godot else None
        if self.web_godot:
            self.templates_dir = self.web_godot.parent / "editor_data" / "export_templates" / self.templates_version
            self.web_templates = [self.templates_dir / "web_debug.zip"]

    def godot_console(self) -> Path:
        env = os.environ.get("GODOT_CONSOLE")
        if env and Path(env).exists():
            return Path(env)
        return self.web_godot or self.cfg.godot_console_exe

    def private_dotnet(self) -> Path | None:
        d = self.godot_web_dir / "dotnet" if self.godot_web_dir else None
        return d if d and (d / "dotnet.exe").exists() else None

    def web_env(self) -> dict[str, str]:
        """The environment a web export runs in: the fork's private .NET SDK
        and wasm workload, and a NuGet cache of its own, so the fork's
        rebuilt Godot.NET.Sdk 4.7.2 never meets the official 4.7.2 in the
        user-wide cache. Nothing here is set outside the child process."""
        env = dict(os.environ)
        dotnet = self.private_dotnet()
        if dotnet:
            env["DOTNET_ROOT"] = str(dotnet)
            env["DOTNET_MULTILEVEL_LOOKUP"] = "0"
            env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
            env["DOTNET_NOLOGO"] = "1"
            env["PATH"] = str(dotnet) + os.pathsep + env.get("PATH", "")
            env["NUGET_PACKAGES"] = str(self.godot_web_dir / "nuget-packages")
        return env

    def browsers_env(self) -> dict[str, str]:
        env = dict(os.environ)
        if self.godot_web_dir and (self.godot_web_dir / "browsers").exists():
            env["PLAYWRIGHT_BROWSERS_PATH"] = str(self.godot_web_dir / "browsers")
        return env


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------


def say(msg: str) -> None:
    print(f"[web] {msg}", flush=True)


def run(cmd: list[str], **kw) -> subprocess.CompletedProcess:
    say("$ " + " ".join(f'"{c}"' if " " in str(c) else str(c) for c in cmd))
    return subprocess.run([str(c) for c in cmd], **kw)


def dotnet_wasm_workloads(private: Path | None = None) -> tuple[bool, str]:
    """Whether the .NET SDK has a wasm workload installed, and what it said.

    `dotnet workload list` crashes on some Windows SDK installs (a COM error,
    0x8007007E) before printing anything; that is reported as-is rather than
    guessed around. The private SDK in tools/godot_web is asked first.
    """
    if private:
        packs = private / "packs"
        found = sorted(d.name for d in packs.iterdir() if "browser-wasm" in d.name) if packs.exists() else []
        if found:
            return True, f"{private} (private): {', '.join(found)}"
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
        self.check("Godot console (C# web export)", p.web_godot is not None, str(console),
                   r"set up tools\godot_web (its README); the pinned engine refuses a C# web export (ADR-0008)")

        have = [t for t in p.web_templates if t.exists()]
        self.check(
            "Web export templates", bool(have),
            str(have[0]) if have else f"none of {', '.join(t.name for t in p.web_templates)} in {p.templates_dir}",
            r"the fork's web_debug.zip goes in its own editor_data\export_templates (tools\godot_web\README.md); "
            "the official mono templates have none (godotengine/godot#70796)",
        )

        listed = sorted(f.name for f in p.templates_dir.iterdir()) if p.templates_dir.exists() else []
        web_any = [n for n in listed if n.startswith("web")]
        print(f"       templates folder holds {len(listed)} files, {len(web_any)} of them web_*")

        ok, detail = dotnet_wasm_workloads(p.private_dotnet())
        self.check("dotnet wasm workload", ok, detail,
                   r"see tools\godot_web\README.md: a private SDK with wasm-tools / wasm-tools-net9")

        data = p.cfg.client_data
        self.check("client data to serve", (data / "tiledata.mul").exists(), str(data),
                   "set UO_CLIENT_DATA; serve hands it to the page from this PC only")

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
    # The .NET 10 SDK writes GUO.slnx unless told the classic format, and
    # the export needs GUO.sln. SDKs before 9.0.200 have no --format and
    # write .sln anyway, so they get the plain call.
    new_sln = ["dotnet", "new", "sln", "-n", "GUO", "-o", p.project]
    if subprocess.run([*new_sln, "--format", "sln"], stdout=subprocess.DEVNULL,
                      stderr=subprocess.DEVNULL).returncode != 0:
        run(new_sln, stdout=subprocess.DEVNULL)
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
    started = time.time()
    with open(log, "w", encoding="utf-8") as f:
        result = run(
            [console, "--headless", "--path", p.project, "--export-debug", PRESET_NAME, page],
            stdout=f, stderr=subprocess.STDOUT, text=True, env=p.web_env(),
        )
    text = log.read_text(encoding="utf-8", errors="replace")
    for line in text.splitlines():
        if re.search(r"^(ERROR|WARNING)|failed|not found|required|not support", line.strip(), re.IGNORECASE):
            print("  " + line.strip()[:200])
    # The engine logs a failed C# build as an error but still writes a page
    # (with no game assembly in it), so an ERROR line fails the export too.
    if result.returncode != 0 or not page.exists() or re.search(r"^ERROR:", text, re.MULTILINE):
        say(f"export FAILED (exit {result.returncode}); full log: {log}")
        return 1
    rc = patch_page(p, page)
    if rc:
        return rc
    say(f"exported {page} in {time.time() - started:.0f} s; log: {log}")
    return 0


# The two edits the exported engine JS needs, each anchored on text the
# template must contain exactly once; a template that changes fails loudly
# here instead of loading a page that cannot mount the client data.
PAGE_PATCHES = [
    # Emscripten's FS, which the template does not export, next to the one
    # FS helper it does; guo_data.js mounts the install through it.
    ('Module["copyToFS"]=GodotFS.copy_to_fs;',
     'Module["copyToFS"]=GodotFS.copy_to_fs;Module["guoFS"]=FS;'),
    # A hook right before main(), once the runtime and FS exist: mount /uo
    # and add the client's arguments.
    ("me.rtenv['callMain'](me.config.args);",
     "if (window.guoBeforeMain) { window.guoBeforeMain(me.rtenv, me.config.args); }"
     " me.rtenv['callMain'](me.config.args);"),
]


def patch_page(p: Paths, page: Path) -> int:
    js = page.with_suffix(".js")
    text = js.read_text(encoding="utf-8")
    for anchor, replacement in PAGE_PATCHES:
        count = text.count(anchor)
        if count != 1:
            say(f"export FAILED: {js.name} holds {count} copies of {anchor!r}, expected 1; "
                "the web template changed, update PAGE_PATCHES")
            return 1
        text = text.replace(anchor, replacement)
    js.write_text(text, encoding="utf-8")
    shutil.copy2(HERE / "guo_data.js", p.out_dir / "guo_data.js")
    say(f"patched {js.name} (FS + guoBeforeMain) and copied guo_data.js")
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


DATA_PREFIX = "/uo/"


def data_index(p: Paths, data: Path) -> bytes:
    """What guo_data.js mounts: every file of the install with its size, and
    the settings the page would otherwise have to be told (client version,
    the shard's WebSocket bridge)."""
    files = []
    for f in sorted(data.rglob("*")):
        if f.is_file():
            files.append({"path": f.relative_to(data).as_posix(), "size": f.stat().st_size})
    return json.dumps({
        "client_version": p.cfg.client_version,
        "shard": {"host": "ws://127.0.0.1", "port": p.cfg.ws_bridge_port},
        "files": files,
    }).encode("utf-8")


class DataHandler(IsolatedHandler):
    """The page's files, plus the player's own UO install under /uo/: its
    index at /uo/_index.json and each file with HTTP Range, which is how the
    page reads it lazily. The server only listens on 127.0.0.1."""

    data_dir: Path | None = None
    index: bytes = b""

    def do_HEAD(self):
        if self.path.startswith(DATA_PREFIX) and self.data_dir:
            return self.send_data(head=True)
        return super().do_HEAD()

    def do_GET(self):
        if self.path.startswith(DATA_PREFIX) and self.data_dir:
            return self.send_data(head=False)
        return super().do_GET()

    def send_data(self, head: bool):
        rel = urllib.parse.unquote(self.path[len(DATA_PREFIX):].split("?", 1)[0])
        if rel == "_index.json":
            return self.send_bytes(self.index, "application/json", head)
        target = (self.data_dir / rel).resolve()
        if self.data_dir not in target.parents or not target.is_file():
            return self.send_error(404)
        size = target.stat().st_size
        start, end = 0, size - 1
        rng = self.headers.get("Range")
        partial = False
        if rng:
            m = re.fullmatch(r"bytes=(\d+)-(\d*)", rng.strip())
            if not m or int(m.group(1)) >= size:
                self.send_response(416)
                self.send_header("Content-Range", f"bytes */{size}")
                self.end_headers()
                return
            start = int(m.group(1))
            end = min(int(m.group(2)) if m.group(2) else size - 1, size - 1)
            partial = True
        self.send_response(206 if partial else 200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        if partial:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        if head:
            return
        with open(target, "rb") as f:
            f.seek(start)
            remaining = end - start + 1
            while remaining > 0:
                chunk = f.read(min(1 << 20, remaining))
                if not chunk:
                    break
                self.wfile.write(chunk)
                remaining -= len(chunk)

    def send_bytes(self, body: bytes, ctype: str, head: bool):
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if not head:
            self.wfile.write(body)

    def log_message(self, fmt, *args):
        # A ranged read per chunk would drown the log; only failures show.
        if len(args) > 1 and str(args[1]).startswith(("2", "3")) and self.path.startswith(DATA_PREFIX):
            return
        super().log_message(fmt, *args)


def make_server(p: Paths, root: Path, data: Path | None = None) -> http.server.ThreadingHTTPServer:
    if data:
        data = data.resolve()
        cls = type("BoundDataHandler", (DataHandler,), {"data_dir": data, "index": data_index(p, data)})
        say(f"serving the client data in {data} at {DATA_PREFIX} (this PC only)")
    else:
        cls = IsolatedHandler
    handler = functools.partial(cls, directory=str(root))
    return http.server.ThreadingHTTPServer(("127.0.0.1", p.port), handler)


def default_data(p: Paths) -> Path | None:
    data = p.cfg.client_data
    return data if (data / "tiledata.mul").exists() else None


def serve(p: Paths, root: Path, data: Path | None = None) -> int:
    if not root.exists():
        say(f"nothing to serve: {root} does not exist (run export first)")
        return 1
    server = make_server(p, root, data)
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


LOGIN_OK = "[GUO] login probe: ok"
LOGIN_FAIL = "[GUO] login probe: FAIL"
FAILURE = re.compile(r"\[GUO\] FATAL|login probe: FAIL|SharedArrayBuffer|Uncaught|RuntimeError|Aborted\(|ERROR: System\.")


def smoke(p: Paths, timeout: int, skip_export: bool, browsers: list[str], data: Path | None,
          wait_for: str, query: str) -> int:
    """Export, serve (with the client data), load the page headless in each
    browser, and wait for the client to say it drew the login gump.

    The browser's console is what a web build has instead of stdout. The page
    runs with --login-probe-stay (the switch the Android smoke bakes into its
    APK), so the client logs LOGIN_OK once the login gump is on screen; a
    FATAL line, a page error or the timeout fails it. Each browser leaves
    build/web/smoke_<browser>.png and smoke_<browser>.txt.
    """
    # Browser console lines carry any character; the Windows console does not.
    sys.stdout.reconfigure(errors="replace")
    try:
        from playwright.sync_api import sync_playwright
    except ImportError:
        say("smoke FAILED: needs Python Playwright (pip install playwright)")
        return 1
    if not skip_export:
        rc = export(p, p.page)
        if rc != 0:
            say("smoke FAILED at export; nothing to load")
            return rc
    if not p.page.exists():
        say(f"smoke FAILED: {p.page} does not exist")
        return 1

    server = make_server(p, p.out_dir, data)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = f"http://127.0.0.1:{p.port}/{p.page.name}?{query}"
    say(f"page: {url}")
    os.environ.update(p.browsers_env())

    failed = 0
    with sync_playwright() as pw:
        for kind in browsers:
            lines: list[str] = []
            shot = p.out_dir / f"smoke_{kind}.png"
            log = p.out_dir / f"smoke_{kind}.txt"
            try:
                if kind == "firefox":
                    browser = pw.firefox.launch(headless=True)
                else:
                    browser = pw.chromium.launch(channel="chrome", headless=True)
            except Exception as e:  # a missing browser is a failed smoke, not a crash
                say(f"{kind}: could not launch: {e}")
                failed += 1
                continue
            page = browser.new_page(viewport={"width": 1280, "height": 800})
            page.on("console", lambda m: lines.append(f"[{m.type}] {m.text}"))
            page.on("pageerror", lambda e: lines.append(f"[pageerror] {e}"))
            started = time.time()
            page.goto(url)
            outcome = "timeout"
            while time.time() - started < timeout:
                if any(wait_for in line for line in lines):
                    outcome = "ok"
                    break
                if any(FAILURE.search(line) for line in lines):
                    outcome = "failed"
                    break
                page.wait_for_timeout(500)
            page.wait_for_timeout(2000)  # let the frame after the marker present
            page.screenshot(path=str(shot))
            mounted = page.evaluate("window.guoWeb && window.guoWeb.mounted ? "
                                    "window.guoWeb.mounted.cache.stats : null")
            browser.close()
            log.write_text("\n".join(lines) + "\n", encoding="utf-8")
            elapsed = time.time() - started
            for line in lines:
                if "[GUO" in line or "error" in line.lower():
                    print("  " + line[:200])
            if mounted:
                say(f"{kind}: data read {mounted['requests']} range requests, "
                    f"{mounted['bytes'] / 1048576:.1f} MiB, {mounted['hits']} cache hits")
            say(f"{kind}: {outcome} after {elapsed:.1f} s; screenshot {shot}; console {log}")
            if outcome != "ok":
                failed += 1
    server.shutdown()
    if failed:
        say(f"smoke FAILED in {failed} of {len(browsers)} browser(s)")
        return 1
    say(f"smoke ok in {', '.join(browsers)}: {wait_for!r}")
    return 0


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
    sv.add_argument("--data", default=None, help="the UO install to serve at /uo/ (default UO_CLIENT_DATA)")
    sv.add_argument("--no-data", action="store_true", help="serve the page only")
    sm = sub.add_parser("smoke", help="export, serve, load headless, wait for the login gump")
    sm.add_argument("--timeout", type=int, default=240, help="seconds to give each browser")
    sm.add_argument("--no-export", action="store_true", help="reuse build\\web")
    sm.add_argument("--browser", action="append", choices=["chrome", "firefox"],
                    help="chrome and/or firefox (default: both)")
    sm.add_argument("--wait-for", default=LOGIN_OK, help="the console line that passes the smoke")
    sm.add_argument("--query", default="arg=--login-probe-stay", help="the page URL's query string")

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
        data = None if args.no_data else (Path(args.data) if args.data else default_data(p))
        return serve(p, Path(args.root) if args.root else p.out_dir, data)
    if args.command == "smoke":
        return smoke(p, args.timeout, args.no_export, args.browser or ["chrome", "firefox"],
                     default_data(p), args.wait_for, args.query)
    return 2


if __name__ == "__main__":
    sys.exit(main())
