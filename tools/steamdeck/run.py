r"""Export, push, run and smoke-test GUO on a Steam Deck.

    launchers\steamdeck\doctor.bat      what is missing, here and on the Deck
    launchers\steamdeck\export.bat      the Linux x86_64 build, headless, into
                                        build\steamdeck
    launchers\steamdeck\push.bat        copy it onto the Deck, with guo.sh
    launchers\steamdeck\run.bat         start it in the Deck's desktop session
    launchers\steamdeck\screenshot.bat  photograph the Deck's screen
    launchers\steamdeck\smoke.bat       export + push + run + wait for the login
                                        gump + screenshot; non-zero on failure

    python tools\steamdeck\run.py <doctor|templates|preset|export|push|run|
                                   stop|log|screenshot|shortcut|smoke> [...]

The Deck is an ordinary x86_64 Linux box (SteamOS 3, Arch, glibc, KDE
Plasma on Wayland in Desktop mode, gamescope in Game mode), so the build is
Godot's own Linux export of the .NET project: the executable, its .pck and
the self-contained .NET data folder. Nothing is compiled on the Deck and
nothing is installed on it beyond that folder and a launcher script. The
transport is ssh: rsync over ssh when both ends have it, a tar stream
otherwise. Read docs\steamdeck.md for the one-time Deck setup and ADR-0018
for why the pieces are what they are.

WHERE THE VALUES COME FROM

  UO_DECK_HOST          the Deck's LAN address    } your network: set these in
  UO_DECK_SSH_KEY       the private key           } config.local.bat, never in
  UO_DECK_KNOWN_HOSTS   a known_hosts file        } a committed file
  UO_DECK_USER          deck
  UO_DECK_INSTALL_DIR   ~/GUO on the Deck
  UO_DECK_CLIENT_DATA   the UO install on the Deck; copied there by YOU, never
                        by this tool (it is proprietary and it is large)
  UO_SHARD_HOST/PORT    baked into guo.sh as --host/--port; 127.0.0.1 would
                        mean the Deck itself, so pass --host to push/smoke
                        or set the shard's LAN address in config.local.bat

WHAT IT TOUCHES

  %APPDATA%\Godot\export_templates\   the mono templates, if `templates`
  godot\GUO\export_presets.cfg        rendered; gitignored
  godot\GUO\GUO.sln                   generated if missing; gitignored
  build\steamdeck\                    the export, its log, screenshots
  <UO_DECK_INSTALL_DIR> on the Deck   the build, guo.sh, guo.log
  ~/.local/share/applications/guo.desktop on the Deck, if `shortcut`
"""

from __future__ import annotations

import argparse
import io
import os
import re
import shutil
import subprocess
import sys
import tarfile
import time
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import Config, godot_config_dir, godot_data_dir, load_config  # noqa: E402

HERE = Path(__file__).resolve().parent
TEMPLATE = HERE / "export_presets.template.cfg"
PRESET_NAME = "Linux"
ARCH = "x86_64"
EXE_NAME = f"GUO.{ARCH}"
LAUNCHER = "guo.sh"
LOG_NAME = "guo.log"

# What the client prints when the login probe has drawn the login gump, and
# when it has not; see src/Bootstrap/LoginProbe.cs.
LOGIN_OK = "[GUO] login probe: ok"
LOGIN_FAIL = "[GUO] login probe: FAIL"

# The environment a process started over ssh needs to reach the Deck's
# desktop session. Plasma's Xwayland is :0 but wants the session's xauth
# cookie (XAUTHORITY, a random name under the runtime dir), the Wayland
# socket and the session bus live in that dir too; the running plasmashell
# has all of them, so copy them from its environment, with the usual values
# as the fallback when there is no plasmashell (then nothing graphical will
# work anyway, and the client says so). The panel sleeps between uses and a
# sleeping panel is neither drawn to nor photographed, so wake it first.
SESSION_ENV = (
    'export XDG_RUNTIME_DIR="/run/user/$(id -u)"; '
    'export DBUS_SESSION_BUS_ADDRESS="unix:path=$XDG_RUNTIME_DIR/bus"; '
    "export DISPLAY=:0 WAYLAND_DISPLAY=wayland-0 LANG=C.UTF-8; "
    "P=$(pgrep -x plasmashell | head -1); "
    'if [ -n "$P" ]; then eval "$(tr \'\\0\' \'\\n\' < /proc/$P/environ '
    '| grep -E \'^(XAUTHORITY|DISPLAY|WAYLAND_DISPLAY|DBUS_SESSION_BUS_ADDRESS|XDG_RUNTIME_DIR)=\' '
    '| sed \'s/^/export /\')"; fi; '
    "kscreen-doctor --dpms on >/dev/null 2>&1; "
)


# ---------------------------------------------------------------------------
# paths
# ---------------------------------------------------------------------------


class Paths:
    def __init__(self, cfg: Config):
        self.cfg = cfg
        self.project = cfg.godot_project
        self.out_dir = cfg.build / "steamdeck"
        self.exe = self.out_dir / EXE_NAME
        self.pck = self.out_dir / "GUO.pck"
        self.preset_file = self.project / "export_presets.cfg"
        self.solution = self.project / "GUO.sln"

        self.godot_config = godot_config_dir()
        # "4.7.2-stable" + mono -> "4.7.2.stable.mono", Godot's folder name.
        self.templates_version = cfg.godot_version.replace("-", ".") + ".mono"
        self.templates_dir = godot_data_dir() / "export_templates" / self.templates_version
        self.template_bin = self.templates_dir / f"linux_debug.{ARCH}"
        self.templates_tpz = (
            cfg.tools / "godot" / "templates" / f"Godot_v{cfg.godot_version}_mono_export_templates.tpz"
        )

    def godot_console(self) -> Path:
        env = os.environ.get("GODOT_CONSOLE")
        if env and Path(env).exists():
            return Path(env)
        return self.cfg.godot_console_exe

    def data_dir(self) -> Path | None:
        """The .NET data folder the export writes next to the executable."""
        for d in sorted(self.out_dir.glob("data_*")):
            if d.is_dir():
                return d
        return None


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------


def say(msg: str) -> None:
    print(f"[steamdeck] {msg}", flush=True)


def run(cmd: list, **kw) -> subprocess.CompletedProcess:
    say("$ " + " ".join(f'"{c}"' if " " in str(c) else str(c) for c in cmd))
    return subprocess.run([str(c) for c in cmd], **kw)


def remote_path(path: str) -> str:
    """A Deck path, quoted for its shell; a leading ~ becomes $HOME."""
    if path == "~":
        return '"$HOME"'
    if path.startswith("~/"):
        return '"$HOME/' + path[2:].replace('"', '\\"') + '"'
    return "'" + path.replace("'", "'\\''") + "'"


# ---------------------------------------------------------------------------
# the Deck, over ssh
# ---------------------------------------------------------------------------


class Deck:
    def __init__(self, cfg: Config):
        self.cfg = cfg
        self.host = cfg.deck_host
        self.user = cfg.deck_user
        self.key = cfg.deck_ssh_key
        self.known_hosts = cfg.deck_known_hosts
        self.install_dir = cfg.deck_install_dir
        self.client_data = cfg.deck_client_data
        self.account = cfg.deck_account

    @property
    def target(self) -> str:
        return f"{self.user}@{self.host}"

    def require(self) -> None:
        if not self.host:
            sys.exit("[steamdeck] UO_DECK_HOST is not set; put the Deck's address in "
                     "launchers\\_shared\\config.local.bat (see docs\\steamdeck.md)")

    def opts(self) -> list[str]:
        # BatchMode: never prompt for a password; a missing key fails fast
        # instead of hanging a launcher.
        o = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10"]
        if self.known_hosts:
            o += ["-o", f"UserKnownHostsFile={self.known_hosts}", "-o", "StrictHostKeyChecking=accept-new"]
        else:
            o += ["-o", "StrictHostKeyChecking=accept-new"]
        if self.key:
            o += ["-i", str(self.key)]
        return o

    def ssh(self, cmd: str, timeout: int = 600, check: bool = True, quiet: bool = False) -> str:
        self.require()
        argv = ["ssh", *self.opts(), self.target, cmd]
        if not quiet:
            say(f"$ ssh {self.target} {cmd[:120]}{'...' if len(cmd) > 120 else ''}")
        p = subprocess.run(argv, capture_output=True, timeout=timeout)
        out = p.stdout.decode("utf-8", "replace")
        if check and p.returncode != 0:
            raise RuntimeError(f"ssh failed ({p.returncode}): {p.stderr.decode('utf-8', 'replace')[-400:]}")
        return out

    def reachable(self) -> tuple[bool, str]:
        try:
            out = self.ssh("uname -m; . /etc/os-release 2>/dev/null; echo ${PRETTY_NAME:-unknown}",
                           timeout=20, quiet=True)
            return True, " ".join(out.split())
        except (RuntimeError, subprocess.TimeoutExpired, OSError) as e:  # noqa: BLE001
            return False, str(e)[-200:]

    def stream_tar(self, dest: str, adder, timeout: int = 3600) -> None:
        """Open `tar -x` in dest on the Deck and let adder(tarfile) write into it."""
        self.require()
        d = remote_path(dest)
        argv = ["ssh", *self.opts(), self.target, f"mkdir -p {d} && tar -xf - -C {d}"]
        say(f"$ ssh {self.target} tar -x -C {dest}   (streaming)")
        proc = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        assert proc.stdin is not None
        with tarfile.open(fileobj=proc.stdin, mode="w|") as tf:
            adder(tf)
        proc.stdin.close()
        err = proc.stderr.read().decode("utf-8", "replace") if proc.stderr else ""
        rc = proc.wait(timeout=timeout)
        if rc != 0:
            raise RuntimeError(f"tar stream failed ({rc}): {err[-400:]}")

    def scp_from(self, remote: str, local: Path) -> bool:
        self.require()
        local.parent.mkdir(parents=True, exist_ok=True)
        r = run(["scp", *self.opts(), f"{self.target}:{remote}", local],
                stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        if r.returncode != 0:
            say(f"scp failed: {r.stderr.decode('utf-8', 'replace')[-200:]}")
            return False
        return local.exists() and local.stat().st_size > 0


def add_bytes(tf: tarfile.TarFile, arcname: str, data: bytes, mode: int = 0o644) -> None:
    ti = tarfile.TarInfo(arcname)
    ti.size = len(data)
    ti.mtime = int(time.time())
    ti.mode = mode
    tf.addfile(ti, io.BytesIO(data))


# ---------------------------------------------------------------------------
# doctor
# ---------------------------------------------------------------------------


class Doctor:
    def __init__(self, p: Paths, deck: Deck):
        self.p = p
        self.deck = deck
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
        p, cfg, deck = self.p, self.p.cfg, self.deck
        print("[steamdeck] doctor -- what a Godot 4.7 .NET Linux export needs, here and on the Deck\n")

        # --- the build machine ---------------------------------------------
        dotnet = shutil.which("dotnet")
        self.check("dotnet SDK", dotnet is not None, dotnet or "not on PATH",
                   "install the .NET 8 SDK (or newer) and put dotnet on PATH")

        console = p.godot_console()
        self.check("Godot console (mono)", console.exists(), str(console),
                   r"launchers\dev\fetch_godot.bat, or set GODOT_EXE in config.local.bat")

        self.check("Linux export template", p.template_bin.exists(),
                   str(p.template_bin) if p.template_bin.exists() else f"{p.template_bin.name} not in {p.templates_dir}",
                   f"put Godot_v{cfg.godot_version}_mono_export_templates.tpz in tools\\godot\\templates "
                   "(from the Godot 4.7.2-stable release page) and run python tools\\steamdeck\\run.py templates")
        self.check("solution file (GUO.sln)", p.solution.exists(), str(p.solution), "generated on the first export")
        self.check("preset template", TEMPLATE.exists(), str(TEMPLATE))

        ssh = shutil.which("ssh")
        self.check("ssh client", ssh is not None, ssh or "not on PATH",
                   "Windows 10+: Settings > Apps > Optional features > OpenSSH Client; or Git for Windows")
        rsync = shutil.which("rsync")
        print(f"  info rsync (this machine)         {rsync or 'none: push streams a tar over ssh instead'}")

        # --- the configuration ---------------------------------------------
        self.check("UO_DECK_HOST", bool(deck.host), deck.host or "(empty)",
                   "set UO_DECK_HOST in launchers\\_shared\\config.local.bat (docs\\steamdeck.md)")
        if deck.key:
            self.check("UO_DECK_SSH_KEY", deck.key.exists(), str(deck.key),
                       "ssh-keygen -t ed25519 -f <that path>, then install the .pub on the Deck")
        else:
            print("  info UO_DECK_SSH_KEY              (empty: ssh's default keys)")
        if deck.known_hosts:
            print(f"  info UO_DECK_KNOWN_HOSTS          {deck.known_hosts}"
                  f"{'' if deck.known_hosts.exists() else ' (will be created on first contact)'}")
        print(f"  info UO_DECK_INSTALL_DIR          {deck.install_dir}")
        print(f"  info UO_DECK_CLIENT_DATA          {deck.client_data}")
        loop = cfg.shard_host in ("127.0.0.1", "localhost", "::1")
        print(f"  {'warn' if loop else 'info'} shard for guo.sh             {cfg.shard_host}:{cfg.shard_port}"
              f"{'  (loopback = the Deck itself; pass --host to push, or set UO_SHARD_HOST)' if loop else ''}")

        # --- the Deck --------------------------------------------------------
        if not deck.host or not ssh:
            print()
            say(f"{self.missing} of the checks above failed; the Deck was not contacted")
            return 1
        ok, detail = deck.reachable()
        self.check("Deck over ssh", ok, f"{deck.target}: {detail}" if ok else detail,
                   "enable sshd on the Deck and install your key there (docs\\steamdeck.md)")
        if not ok:
            print()
            say(f"{self.missing} of the checks above failed")
            return 1
        self.check("Deck is x86_64", detail.startswith("x86_64"), detail)

        facts = deck.ssh(
            "test -f " + remote_path(deck.client_data + "/tiledata.mul") + " && echo DATA=yes || echo DATA=no; "
            "for t in spectacle grim rsync; do command -v $t >/dev/null && echo HAVE=$t; done; "
            "pgrep -x gamescope >/dev/null && echo MODE=game || echo MODE=desktop; "
            "test -S /tmp/.X11-unix/X0 && echo X0=yes || echo X0=no; "
            "test -x " + remote_path(deck.install_dir + "/" + EXE_NAME) + " && echo INSTALLED=yes || echo INSTALLED=no; "
            "df -Ph " + remote_path("~") + " | awk 'NR==2{print \"FREE=\"$4}'",
            timeout=30, quiet=True,
        )
        have = set(re.findall(r"^HAVE=(\w+)$", facts, re.MULTILINE))
        # ADR-0021: no data on the Deck is not a failure. The client opens the
        # first-run wizard there; the tools never push the install.
        if "DATA=yes" in facts:
            self.check("UO client data on the Deck", True, f"{deck.client_data}/tiledata.mul")
        else:
            print(f"  info UO client data on the Deck   none at {deck.client_data}: GUO opens the first-run "
                  "wizard there, or copy your UO install to it yourself (docs\\steamdeck.md)")
        self.check("screenshot tool on the Deck", bool(have & {"spectacle", "grim"}),
                   ", ".join(sorted(have & {"spectacle", "grim"})) or "neither spectacle nor grim",
                   "Desktop mode: Discover > Spectacle (SteamOS ships it)")
        mode = re.search(r"^MODE=(\w+)$", facts, re.MULTILINE)
        x0 = "X0=yes" in facts
        self.check("Deck in Desktop mode", (mode and mode.group(1) == "desktop") and x0,
                   f"{mode.group(1) if mode else '?'} mode, Xwayland :0 {'up' if x0 else 'down'}",
                   "switch the Deck to Desktop mode (Power > Switch to Desktop); run/screenshot need its session")
        print(f"  info rsync (the Deck)             {'yes' if 'rsync' in have else 'no'}")
        inst = re.search(r"^INSTALLED=(\w+)$", facts, re.MULTILINE)
        free = re.search(r"^FREE=(\S+)$", facts, re.MULTILINE)
        print(f"  info build on the Deck            {'installed' if inst and inst.group(1) == 'yes' else 'not yet'} "
              f"at {deck.install_dir}; {free.group(1) if free else '?'} free in the home partition")

        print()
        if self.missing:
            say(f"{self.missing} of the checks above failed")
            return 1
        say("everything a Steam Deck build needs is here")
        return 0


# ---------------------------------------------------------------------------
# templates / preset / export
# ---------------------------------------------------------------------------


def install_templates(p: Paths) -> int:
    if not p.templates_tpz.exists():
        sys.exit(f"[steamdeck] no templates archive at {p.templates_tpz}\n"
                 f"  download Godot_v{p.cfg.godot_version}_mono_export_templates.tpz from the Godot "
                 "release page and put it there")
    p.templates_dir.mkdir(parents=True, exist_ok=True)
    count = 0
    with zipfile.ZipFile(p.templates_tpz) as z:
        for member in z.infolist():
            # The archive is one folder, "templates/", holding the files.
            name = member.filename.split("/", 1)[1] if "/" in member.filename else member.filename
            if member.is_dir() or not name:
                continue
            target = p.templates_dir / name
            target.parent.mkdir(parents=True, exist_ok=True)
            with z.open(member) as src, open(target, "wb") as dst:
                shutil.copyfileobj(src, dst)
            count += 1
    say(f"unpacked {count} template files into {p.templates_dir}")
    return 0


def render_preset(p: Paths, export_path: Path) -> Path:
    text = TEMPLATE.read_text(encoding="utf-8")
    values = {"EXPORT_PATH": str(export_path).replace("\\", "/")}
    for key, value in values.items():
        text = text.replace("{{" + key + "}}", value)
    leftover = re.findall(r"{{\w+}}", text)
    if leftover:
        sys.exit(f"[steamdeck] template placeholders without a value: {leftover}")
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
        sys.exit("[steamdeck] could not produce GUO.sln")


def export(p: Paths) -> int:
    console = p.godot_console()
    if not console.exists():
        sys.exit(f"[steamdeck] Godot console not found at {console}; run doctor")
    if not p.template_bin.exists():
        sys.exit(f"[steamdeck] no Linux export template at {p.template_bin}; run doctor")
    p.out_dir.mkdir(parents=True, exist_ok=True)
    ensure_solution(p)
    render_preset(p, p.exe)
    # A stale data folder from an earlier export would be pushed along with
    # the new one; the exporter does not clear it.
    old = p.data_dir()
    if old:
        shutil.rmtree(old, ignore_errors=True)
    for f in (p.exe, p.pck):
        if f.exists():
            f.unlink()

    log = p.out_dir / "export.log"
    with open(log, "w", encoding="utf-8") as f:
        result = run(
            [console, "--headless", "--path", p.project, "--export-debug", PRESET_NAME, p.exe],
            stdout=f, stderr=subprocess.STDOUT, text=True,
        )
    text = log.read_text(encoding="utf-8", errors="replace")
    for line in text.splitlines():
        # The pack progress names every file, ServerErrorMessages.cs among
        # them; only the exporter's own complaints are worth repeating.
        if "savepack" in line or "Storing File" in line or "godot_variant" in line:
            continue
        if re.search(r"\berror\b|failed|not found|required|not support", line, re.IGNORECASE):
            print("  " + line.strip()[:200])
    data = p.data_dir()
    if result.returncode != 0 or not p.exe.exists() or not p.pck.exists() or not data:
        say(f"export FAILED (exit {result.returncode}); full log: {log}")
        return 1
    size = sum(f.stat().st_size for f in data.rglob("*") if f.is_file())
    say(f"exported {p.exe.name} ({p.exe.stat().st_size // 1024 // 1024} MB), "
        f"{p.pck.name} ({p.pck.stat().st_size // 1024 // 1024} MB), "
        f"{data.name}/ ({size // 1024 // 1024} MB); log: {log}")
    return 0


# ---------------------------------------------------------------------------
# push / run / screenshot / shortcut
# ---------------------------------------------------------------------------


def launcher_script(deck: Deck, shard_host: str, shard_port: int, data_default: bool = False) -> str:
    data = deck.client_data
    if data.startswith("~/"):
        data = '"$HOME/' + data[2:] + '"'
    elif data == "~":
        data = '"$HOME"'
    else:
        data = "'" + data + "'"
    account = f" --account '{deck.account}'" if deck.account else ""
    # --data-default: no --client-data, so the client resolves its data itself
    # by ADR-0021 (saved setting, then ~/UO, else the first-run screen).
    client_data = "" if data_default else f" --client-data {data}"
    # The client falls back to 7.0.107.76 without it, and a shard that reads
    # a newer version from its own copy of the install kicks an older one.
    version = f" --client-version {deck.cfg.client_version}" if deck.cfg.client_version else ""
    return (
        "#!/bin/bash\n"
        "# GUO on the Steam Deck. Written by tools/steamdeck/run.py push; edit\n"
        "# UO_DECK_CLIENT_DATA / UO_SHARD_HOST on the PC and push again instead.\n"
        "# Extra flags go through: ./guo.sh --login-probe-stay, ./guo.sh --sound.\n"
        "# After \"--\": the client reads only what follows it (OS.GetCmdlineUserArgs).\n"
        'cd "$(dirname "$(readlink -f "$0")")" || exit 1\n'
        f'exec ./{EXE_NAME} -- --play{client_data}{version} --host {shard_host} --port {shard_port}{account} "$@"\n'
    )


def push(p: Paths, deck: Deck, shard_host: str | None, shard_port: int | None, data_default: bool = False) -> int:
    deck.require()
    data = p.data_dir()
    if not (p.exe.exists() and p.pck.exists() and data):
        say(f"nothing to push in {p.out_dir}; export first")
        return 1
    host = shard_host or p.cfg.shard_host
    port = shard_port or p.cfg.shard_port
    if host in ("127.0.0.1", "localhost", "::1"):
        say(f"note: guo.sh will point at {host}:{port}, which on the Deck is the Deck itself; "
            "pass --host <the shard's LAN address> to push, or set UO_SHARD_HOST in config.local.bat")
    script = launcher_script(deck, host, port, data_default).encode("utf-8")
    files = [p.exe, p.pck, *[f for f in data.rglob("*") if f.is_file()]]
    total = sum(f.stat().st_size for f in files)
    say(f"pushing {len(files) + 1} files ({total // 1024 // 1024} MB) to {deck.target}:{deck.install_dir}")

    rsync = shutil.which("rsync")
    if rsync:
        ssh_cmd = " ".join(["ssh", *(f'"{o}"' if " " in o else o for o in deck.opts())])
        r = run([rsync, "-az", "--delete", "--chmod=Fu+x", "-e", ssh_cmd,
                 f"{p.out_dir}/", f"{deck.target}:{deck.install_dir}/"])
        if r.returncode != 0:
            say("rsync failed; falling back to a tar stream")
            rsync = None
    if not rsync:
        def add(tf: tarfile.TarFile) -> None:
            for f in files:
                arc = f.relative_to(p.out_dir).as_posix()
                info = tf.gettarinfo(str(f), arcname=arc)
                info.mode = 0o755 if f == p.exe else 0o644
                with open(f, "rb") as fh:
                    tf.addfile(info, fh)
        t0 = time.time()
        deck.stream_tar(deck.install_dir, add)
        say(f"streamed in {time.time() - t0:.0f}s")

    # The launcher goes last, on its own, so a rsync --delete never removes
    # it and its mode is what it must be.
    deck.stream_tar(deck.install_dir, lambda tf: add_bytes(tf, LAUNCHER, script, 0o755))
    d = remote_path(deck.install_dir)
    out = deck.ssh(f"chmod +x {d}/{EXE_NAME} {d}/{LAUNCHER} && ls -la {d} | head -20", quiet=True)
    for line in out.splitlines():
        print("  " + line)
    say(f"installed; on the Deck: {deck.install_dir}/{LAUNCHER}")
    return 0


# The client runs as a transient unit of the Deck's user systemd, not as a
# child of the ssh session: SteamOS sets logind's KillUserProcesses=True, so
# everything in an ssh session's scope is killed the moment the session
# closes, nohup and setsid included (measured: a launch that way lived long
# enough to print Godot's banner and no more). The user manager already
# carries the desktop session's DISPLAY, WAYLAND_DISPLAY and XAUTHORITY, so
# the unit reaches the screen without any of SESSION_ENV.
UNIT = "guo-client"


def start(deck: Deck, extra: str) -> int:
    """Start guo.sh in the Deck's desktop session, as a user unit; its output goes to guo.log."""
    deck.require()
    d = remote_path(deck.install_dir)
    cmd = (SESSION_ENV + f'D=$(cd {d} 2>/dev/null && pwd) || {{ echo "no {deck.install_dir}: push first" >&2; exit 2; }}; '
           f'test -x "$D/{LAUNCHER}" || {{ echo "no {LAUNCHER}: push first" >&2; exit 2; }}; '
           f"systemctl --user stop {UNIT} 2>/dev/null; rm -f \"$D/{LOG_NAME}\"; "
           f"systemd-run --user --collect --quiet --unit={UNIT} "
           f'-p WorkingDirectory="$D" -p StandardOutput="append:$D/{LOG_NAME}" -p StandardError="append:$D/{LOG_NAME}" '
           f'--setenv=LANG=C.UTF-8 "$D/{LAUNCHER}" {extra} && echo started')
    out = deck.ssh(cmd, timeout=30, check=False)
    if "started" not in out:
        say(f"could not start the client: {out.strip()[-200:]}")
        return 1
    say(f"started as the Deck's user unit {UNIT}; output in {deck.install_dir}/{LOG_NAME}")
    return 0


def alive(deck: Deck) -> bool:
    state = deck.ssh(f"systemctl --user is-active {UNIT} 2>/dev/null; pgrep -x {EXE_NAME} >/dev/null && echo proc",
                     timeout=20, check=False, quiet=True).split()
    return "active" in state or "activating" in state or "proc" in state


def stop(deck: Deck) -> None:
    deck.ssh(f"systemctl --user stop {UNIT} 2>/dev/null; pkill -x {EXE_NAME}; true",
             timeout=20, check=False, quiet=True)


def read_log(deck: Deck) -> str:
    return deck.ssh(f"cat {remote_path(deck.install_dir + '/' + LOG_NAME)} 2>/dev/null", timeout=20,
                    check=False, quiet=True)


def run_app(p: Paths, deck: Deck, extra: str, wait: int) -> int:
    if start(deck, extra) != 0:
        return 1
    if wait <= 0:
        return 0
    say(f"following {LOG_NAME} for {wait}s (Ctrl+C stops following; the client keeps running)")
    seen = 0
    deadline = time.time() + wait
    try:
        while time.time() < deadline:
            time.sleep(3)
            text = read_log(deck)
            lines = text.splitlines()
            for line in lines[seen:]:
                print("  " + line)
            seen = len(lines)
            if not alive(deck):
                # Its last lines land in the log as it exits; show them.
                for line in read_log(deck).splitlines()[seen:]:
                    print("  " + line)
                say("the client exited")
                return 0
    except KeyboardInterrupt:
        pass
    return 0


def screenshot(p: Paths, deck: Deck, local: Path) -> int:
    deck.require()
    remote = "/tmp/guo_screen.png"
    cmd = (SESSION_ENV + f"rm -f {remote}; "
           f"if command -v spectacle >/dev/null; then timeout 30 spectacle -b -n -o {remote} >/dev/null 2>&1; "
           f"elif command -v grim >/dev/null; then grim {remote}; "
           "else echo 'no screenshot tool (spectacle or grim)' >&2; exit 2; fi; "
           f"test -s {remote} && echo shot || {{ echo 'no image written' >&2; exit 3; }}")
    try:
        deck.ssh(cmd, timeout=60)
    except RuntimeError as e:
        say(f"screenshot failed on the Deck: {e}")
        return 1
    if not deck.scp_from(remote, local):
        return 1
    say(f"screenshot {local} ({local.stat().st_size // 1024} KB)")
    return 0


def shortcut(p: Paths, deck: Deck) -> int:
    """A .desktop entry in the Deck's app menu, and the steps that put it in Steam."""
    deck.require()
    d = deck.install_dir
    if d.startswith("~/"):
        d = "/home/" + deck.user + d[1:]
    desktop = (
        "[Desktop Entry]\n"
        "Type=Application\n"
        "Name=GUO\n"
        "Comment=Ultima Online Classic on Godot\n"
        f"Exec={d}/{LAUNCHER}\n"
        f"Path={d}\n"
        "Terminal=false\n"
        "Categories=Game;\n"
    )
    deck.stream_tar("~/.local/share/applications", lambda tf: add_bytes(tf, "guo.desktop", desktop.encode(), 0o755))
    deck.ssh("update-desktop-database ~/.local/share/applications 2>/dev/null; true", check=False, quiet=True)
    say("wrote ~/.local/share/applications/guo.desktop on the Deck (it is now in the Plasma app menu)")
    print(
        "\n  To play in Game mode, add it to Steam ONCE, from Desktop mode:\n"
        "    1. Open Steam (Desktop mode)  >  Games  >  Add a Non-Steam Game to My Library...\n"
        "    2. Browse...  >  file type: All Files  >  pick " + f"{d}/{LAUNCHER}" + "  >  Add Selected Programs\n"
        "    3. Right-click the new 'guo.sh' entry  >  Properties: name it GUO; Launch options can carry\n"
        "       client flags (e.g. --sound); Controller: Keyboard (WASD) and Mouse layout, or the\n"
        "       'Mouse and Keyboard (Trackpad)' template -- the client is mouse-driven.\n"
        "    4. Return to Gaming Mode; GUO is under Library > Non-Steam.\n"
        "  Steam's shortcuts.vdf is binary and Steam rewrites it on exit, so this tool does not edit it.\n"
    )
    return 0


# ---------------------------------------------------------------------------
# smoke
# ---------------------------------------------------------------------------


def smoke(p: Paths, deck: Deck, timeout: int, skip_export: bool, skip_push: bool,
          shard_host: str | None, shard_port: int | None, sound: bool) -> int:
    if not skip_export and export(p) != 0:
        return 1
    if not skip_push and push(p, deck, shard_host, shard_port) != 0:
        return 1

    stop(deck)
    deck.ssh(f"rm -f {remote_path(deck.install_dir + '/' + LOG_NAME)}", check=False, quiet=True)
    # The smoke run says on the log when the login gump is drawn, and stays
    # up so the screen can be photographed with it showing.
    # The Classic look: a look saved on the Deck is not the build under test.
    extra = "--login-probe-stay --postfx off" + ("" if sound else " --silent")
    if start(deck, extra) != 0:
        return 1

    say(f"waiting up to {timeout}s for '{LOGIN_OK}' in {LOG_NAME}")
    deadline = time.time() + timeout
    verdict = None
    text = ""
    while time.time() < deadline:
        time.sleep(3)
        text = read_log(deck)
        if LOGIN_OK in text:
            verdict = True
            break
        if LOGIN_FAIL in text or "[GUO] FATAL" in text or "Unhandled exception" in text:
            verdict = False
            break
        if not alive(deck):
            verdict = False
            text = read_log(deck)
            break

    p.out_dir.mkdir(parents=True, exist_ok=True)
    log_file = p.out_dir / "smoke_guo.log"
    log_file.write_text(text, encoding="utf-8")
    shot = p.out_dir / "smoke.png"
    if verdict:
        time.sleep(2)  # let the gump's first frames settle before the photograph
    screenshot(p, deck, shot)
    stop(deck)

    for line in text.splitlines():
        if "[GUO]" in line or "No UO client data" in line:
            print("  " + line.strip()[:200])

    if verdict:
        say(f"OK login gump rendered on the Deck; screenshot {shot}, log {log_file}")
        return 0
    say("FAILED: " + ("the client reported a failure or exited" if verdict is False
                      else f"no '{LOGIN_OK}' within {timeout}s") + f"; log {log_file}, screenshot {shot}")
    return 1


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("doctor", help="check the toolchain here and the Deck over ssh; say what is missing")
    sub.add_parser("templates", help="unpack the mono export templates for the pinned Godot")
    sub.add_parser("preset", help="render export_presets.cfg from the template")
    sub.add_parser("export", help="export the Linux x86_64 build, headless, into build\\steamdeck")
    pu = sub.add_parser("push", help="copy the export onto the Deck and write guo.sh")
    pu.add_argument("--host", default=None, help="shard address guo.sh connects to (default UO_SHARD_HOST)")
    pu.add_argument("--port", type=int, default=None, help="shard port (default UO_SHARD_PORT)")
    pu.add_argument("--data-default", action="store_true",
                    help="guo.sh passes no --client-data: the client resolves its data itself (ADR-0021)")
    ru = sub.add_parser("run", help="start the client in the Deck's desktop session")
    ru.add_argument("--args", default="", help="extra client flags (e.g. --sound, --login-probe-stay)")
    ru.add_argument("--wait", type=int, default=0, help="seconds to follow guo.log afterwards (0 = return at once)")
    sub.add_parser("stop", help="kill the client on the Deck")
    sub.add_parser("log", help="print the Deck's guo.log")
    sc = sub.add_parser("screenshot", help="photograph the Deck's screen into build\\steamdeck")
    sc.add_argument("--out", default=None, help="PNG path (default build\\steamdeck\\screenshot.png)")
    sub.add_parser("shortcut", help="a menu entry on the Deck, and the steps that add it to Steam")
    sm = sub.add_parser("smoke", help="export, push, run, wait for the login gump, screenshot")
    sm.add_argument("--timeout", type=int, default=180, help="seconds to wait for the login gump")
    sm.add_argument("--no-export", action="store_true", help="reuse build\\steamdeck")
    sm.add_argument("--no-push", action="store_true", help="reuse what is on the Deck")
    sm.add_argument("--host", default=None, help="shard address for guo.sh (default UO_SHARD_HOST)")
    sm.add_argument("--port", type=int, default=None, help="shard port (default UO_SHARD_PORT)")
    sm.add_argument("--sound", action="store_true", help="audible run (default passes --silent)")

    args = parser.parse_args(argv)
    cfg = load_config()
    p = Paths(cfg)
    deck = Deck(cfg)

    if args.command == "doctor":
        return Doctor(p, deck).run()
    if args.command == "templates":
        return install_templates(p)
    if args.command == "preset":
        render_preset(p, p.exe)
        return 0
    if args.command == "export":
        return export(p)
    if args.command == "push":
        return push(p, deck, args.host, args.port, args.data_default)
    if args.command == "run":
        return run_app(p, deck, args.args, args.wait)
    if args.command == "stop":
        stop(deck)
        say("stopped")
        return 0
    if args.command == "log":
        print(read_log(deck), end="")
        return 0
    if args.command == "screenshot":
        return screenshot(p, deck, Path(args.out) if args.out else p.out_dir / "screenshot.png")
    if args.command == "shortcut":
        return shortcut(p, deck)
    if args.command == "smoke":
        return smoke(p, deck, args.timeout, args.no_export, args.no_push, args.host, args.port, args.sound)
    return 2


if __name__ == "__main__":
    sys.exit(main())
