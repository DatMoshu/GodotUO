r"""Photograph the same places in ClassicUO and in GUO, side by side.

Every visual fault found so far was found by eye, and arguing about one is
slow while nobody has the original in front of them. This stands both clients
in the same spot on the same shard with the same profile -- the same gumps
open, the same render options, the same game-window size -- and takes a
picture with each.

    launchers\dev\ab_compare.bat

Output lands in build\screenshots\ab\<place>\: guo.png, cuo.png and ab.png,
the two stacked with a label on each.

HOW EACH CLIENT IS DRIVEN

GUO has --play, --shard-command and --screenshot-name already, so a run is one
process per place and no window handling at all.

ClassicUO has none of that, so it is driven the way a person would: -autologin
with the owner account, then the place typed into the chat line as [go X Y and
the window photographed off the screen. That means the CUO pass needs the
desktop to itself for a minute -- it brings its window to the front and types
into it.

Both read the same profile. CUO's is a copy of the one GUO saved for the same
character, so the comparison is not confounded by one client having roofs off
or a different game-window size. Unknown keys are ignored by both.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.config import Config, load_config  # noqa: E402


# LightCycle.DayLevel on the shard: no darkening at all.
DAYLIGHT = 0
# LightCycle.NightLevel on ModernUO: what an outdoor night looks like.
NIGHT = 12


@dataclass(frozen=True)
class Place:
    name: str
    x: int
    y: int
    # None lets the shard put the character on the ground; a floor inside a
    # building needs it said.
    z: int | None = None
    light: int = DAYLIGHT
    # Typed into the game after arriving, as a player would: speech, not
    # shard commands.
    say: tuple[str, ...] = ()

    @property
    def go(self) -> str:
        return f"[go {self.x} {self.y}" + ("" if self.z is None else f" {self.z}")


# Chosen to be unlike each other: towns by day and by night, a forest with
# buildings in it, a coastline, a dungeon mouth, a street among houses with
# roofs, the ground floor of a two-storey house, and a shop. The paperdoll,
# backpack and status gumps are open in every one: the profile both clients
# share keeps them open.
PLACES: list[Place] = [
    Place("minoc-town", 2500, 560),
    Place("yew-forest", 633, 858),
    Place("britain-coast", 1497, 1790),
    Place("despise-mouth", 5401, 629),
    Place("britain-street", 1602, 1591),
    Place("britain-street-night", 1602, 1591, light=NIGHT),
    # A floor at z=20 over a 5x5 room at z=0 (statics0.mul): the storey above
    # and the roof must both come off while the character stands inside.
    Place("britain-interior", 1434, 1686, z=0),
    # A provisioner stands frozen one tile east of this spot. The shard keeps
    # him; if he is ever gone, put him back once from either client with
    #   [go 1605 1543
    #   [TileRXYZ 1 0 1 1 0 Provisioner set CantWalk true
    # Last, because ClassicUO plays every place in one session and the shop
    # gump would otherwise stay open over the rest.
    Place("britain-shop", 1605, 1543, say=("vendor buy",)),
]


# --------------------------------------------------------------------------
# GUO
# --------------------------------------------------------------------------


def guo_environment(cfg: Config) -> dict[str, str]:
    """The settings GUO reads from its environment, resolved here.

    GUO takes UO_CLIENT_DATA and the rest from environment variables and
    nowhere else, and the launcher is what normally sets them. Passing the
    resolved config down means this runs the same from a launcher, a shell or
    CI, which is the order config.bat promises.
    """
    return {
        **os.environ,
        "UO_CLIENT_DATA": str(cfg.client_data),
        "UO_CACHE_DIR": str(cfg.cache_dir),
        "UO_CLIENT_VERSION": cfg.client_version,
        "UO_SHARD_HOST": cfg.shard_host,
        "UO_SHARD_PORT": str(cfg.shard_port),
    }


def shoot_guo(cfg: Config, place: Place, out_dir: Path) -> Path:
    """One client run per place, which is what --play is built for."""
    out_dir.mkdir(parents=True, exist_ok=True)

    # Last run's picture, gone before this one starts. Without this a
    # client that never reached the world leaves the old file in place
    # and the run looks like it worked.
    shot = out_dir / "guo.png"
    shot.unlink(missing_ok=True)

    proc = subprocess.run(
        [
            str(cfg.godot_console_exe),
            "--path",
            str(cfg.godot_project),
            "--",
            "--play",
            "--screenshot-dir",
            str(out_dir),
            "--screenshot-name",
            "guo",
            # Daylight, pinned. Without it the shard's clock runs on between
            # the two passes and one client gets a duller world than the
            # other, which reads as a difference in the renderer and is not
            # one. LightCycle.LevelOverride outranks the clock and stays put
            # until the shard restarts, so setting it on every run is free.
            "--shard-command",
            f"[globallight {place.light}",
            "--shard-command",
            place.go,
            *[arg for line in place.say for arg in ("--shard-command", line)],
        ],
        capture_output=True,
        text=True,
        env=guo_environment(cfg),
    )

    if not shot.exists():
        tail = "\n".join((proc.stdout or "").splitlines()[-12:])
        raise RuntimeError(f"GUO took no picture at {place.name}:\n{tail}")

    return shot


# --------------------------------------------------------------------------
# ClassicUO
# --------------------------------------------------------------------------


def build_cuo(cfg: Config) -> None:
    """Build ClassicUO out of tree. sources\\ is read-only and stays pristine."""
    artifacts = cfg.build / "cuo-artifacts"
    project = (
        cfg.upstream
        / "src"
        / "ClassicUO.Client"
        / "ClassicUO.Client.csproj"
    )

    print(f"[ab] building ClassicUO -> {cfg.upstream_build}")
    subprocess.run(
        [
            "dotnet",
            "build",
            str(project),
            "-c",
            "Release",
            # Both of these matter: the csproj hard-codes an OutputPath inside
            # sources\, and without an artifacts path every project drops an
            # obj\ there too.
            "--artifacts-path",
            str(artifacts),
            f"-p:OutputPath={cfg.upstream_build}{os.sep}",
            "-v",
            "q",
            "--nologo",
        ],
        check=True,
    )


def target_size(root: Path, places: list[Place]) -> tuple[int, int]:
    """Match whatever GUO photographed, so the two sheets line up.

    GUO takes the size of its own window, which is whatever the desktop gave
    it. Nothing here can ask it in advance, so the first picture it took
    answers for all of them.
    """
    from PIL import Image

    for place in places:
        shot = root / place.name / "guo.png"
        if shot.exists():
            with Image.open(shot) as im:
                return im.size

    return (1920, 1080)


def display_scale() -> float:
    """What Windows is magnifying by on the monitor the window will use.

    ClassicUO honours it: GameController.Draw renders the whole frame to a
    target of back buffer / DpiScale and blows that up again, so on a scaled
    display it draws at a fraction of the window's real pixels. That is right
    for a person -- the UI stays legible -- and wrong for a comparison, where
    it looks like ClassicUO is zoomed in and soft. DpiScale is display scale
    times the screen_scale setting, so dividing one out of the other gets a
    frame drawn at the window's true size, like Godot's.
    """
    import ctypes

    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except OSError:
        pass

    class POINT(ctypes.Structure):
        _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

    # MONITOR_DEFAULTTOPRIMARY: the window is placed at the origin.
    monitor = ctypes.windll.user32.MonitorFromPoint(POINT(0, 0), 1)
    x = ctypes.c_uint()
    y = ctypes.c_uint()
    # MDT_EFFECTIVE_DPI, which is what SDL reports as the display scale.
    ctypes.windll.shcore.GetDpiForMonitor(
        monitor, 0, ctypes.byref(x), ctypes.byref(y)
    )
    return (x.value or 96) / 96.0


def guo_profiles_dir(cfg: Config) -> Path:
    return (
        Path(os.environ["LOCALAPPDATA"])
        / "GUO"
        / "Data"
        / "Profiles"
        / cfg.shard_owner
        / cfg.shard_name
    )


def last_played(cfg: Config) -> str | None:
    """The character GUO last played, by the profile it last wrote.

    Neither client is told which character to be: GUO's --play logs in
    whichever the character list has selected, and CUO falls back to the same
    one when -lastcharactername matches nothing. Copying the profile GUO wrote
    most recently is what keeps the two pictures the same character.
    """
    root = guo_profiles_dir(cfg)
    if not root.is_dir():
        return None

    saved = [d for d in root.iterdir() if (d / "profile.json").exists()]
    if not saved:
        return None

    return max(saved, key=lambda d: (d / "profile.json").stat().st_mtime).name


def write_cuo_config(
    cfg: Config, character: str, size: tuple[int, int]
) -> Path:
    """settings.json plus a copy of GUO's profile for the same character."""
    conf = cfg.build / "cuo-config"
    profiles = conf / "Profiles"
    profiles.mkdir(parents=True, exist_ok=True)

    settings = {
        "username": cfg.shard_owner,
        "password": "",
        "ip": cfg.shard_host,
        "port": cfg.shard_port,
        "ultimaonlinedirectory": str(cfg.client_data),
        "profilespath": str(profiles),
        "clientversion": cfg.client_version,
        "lang": "ENU",
        "lastservernum": 1,
        "last_server_name": cfg.shard_name,
        "fps": 60,
        # Cancels the display scaling out of ClassicUO's DpiScale, so it
        # draws one pixel per pixel of its window -- see display_scale().
        "screen_scale": round(1.0 / display_scale(), 6),
        "window_position": {"X": 0, "Y": 0},
        # Explicit, not maximized: the back buffer follows this, and it has to
        # be the size GUO's picture already is or the two will not line up.
        "window_size": {"X": size[0], "Y": size[1]},
        "is_win_maximized": False,
        "saveaccount": False,
        "autologin": True,
        "reconnect": False,
        "login_music": False,
        "encryption": 0,
        # The default is Razor, which is not here and would be one more
        # window in the photograph.
        "plugins": [],
    }

    (conf / "settings.json").write_text(
        json.dumps(settings, indent=2), encoding="utf-8"
    )

    # The profile GUO saved for this character, so both clients draw with the
    # same options and the same gumps open. CUO ignores the keys it does not
    # know, and GUO's profile is a port of CUO's, so there are few of those.
    guo_profiles = guo_profiles_dir(cfg)
    source = guo_profiles / character
    if not (source / "profile.json").exists():
        print(
            f"[ab] no GUO profile at {source} -- ClassicUO will use its own "
            "defaults, so the two pictures will differ in their gumps and in "
            "the size of the game window as well as in the world"
        )
        return conf

    # The account here has more than one character and each client picks its
    # own; whichever ClassicUO lands on has to come up with this profile or
    # the game window is 640x480 in a 4K frame. default.json is the template
    # CUO makes a new profile from, so writing it covers every character,
    # including one nobody has logged in as yet.
    shutil.copyfile(source / "profile.json", profiles / "default.json")

    for name in sorted(p.name for p in guo_profiles.iterdir() if p.is_dir()):
        dest = profiles / cfg.shard_owner / cfg.shard_name / name
        dest.mkdir(parents=True, exist_ok=True)
        for f in ("profile.json", "gumps.xml", "macros.xml", "skillsgroups.xml"):
            if (source / f).exists():
                shutil.copyfile(source / f, dest / f)

    print(f"[ab] CUO profile: GUO's {character} profile, and as the default")

    return conf


# --- window handling ------------------------------------------------------


def _win32():
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.SetProcessDPIAware()
    return ctypes, wintypes, user32


def find_window(pid: int):
    """The one visible top-level window belonging to pid, or None."""
    ctypes, wintypes, user32 = _win32()

    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def each(hwnd, _lparam):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            length = user32.GetWindowTextLengthW(hwnd)
            if length > 0:
                found.append(hwnd)
        return True

    user32.EnumWindows(each, 0)
    return found[0] if found else None


def focus(hwnd) -> None:
    _ctypes, _wintypes, user32 = _win32()
    user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.4)


def window_rect(hwnd) -> tuple[int, int, int, int]:
    ctypes, wintypes, user32 = _win32()

    class RECT(ctypes.Structure):
        _fields_ = [
            ("left", wintypes.LONG),
            ("top", wintypes.LONG),
            ("right", wintypes.LONG),
            ("bottom", wintypes.LONG),
        ]

    rect = RECT()
    user32.GetClientRect(hwnd, ctypes.byref(rect))

    class POINT(ctypes.Structure):
        _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG)]

    origin = POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(origin))

    return (
        origin.x,
        origin.y,
        origin.x + rect.right,
        origin.y + rect.bottom,
    )


def send_keys(text: str) -> None:
    """Type into whatever has focus. WScript.Shell, so no assembly to load."""
    subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-Command",
            "$s = New-Object -ComObject WScript.Shell; "
            f"$s.SendKeys('{text}')",
        ],
        check=True,
        capture_output=True,
    )


def nudge(hwnd) -> None:
    """Resize the window by two pixels and back.

    ClassicUO only stretches the world viewport to fill the window when the
    window's size changes -- WindowOnClientSizeChanged in GameController. The
    window is already its final size before the world gump exists, so that
    never fires and a full-size game window comes up at whatever the gump was
    built at. Two spurious resizes after login do fire it.
    """
    _ctypes, _wintypes, user32 = _win32()

    left, top, right, bottom = outer_rect(hwnd)
    width, height = right - left, bottom - top

    # SWP_NOZORDER | SWP_NOACTIVATE
    flags = 0x0004 | 0x0010
    user32.SetWindowPos(hwnd, 0, left, top, width, height - 2, flags)
    time.sleep(0.6)
    user32.SetWindowPos(hwnd, 0, left, top, width, height, flags)
    time.sleep(0.6)


def outer_rect(hwnd) -> tuple[int, int, int, int]:
    ctypes, wintypes, user32 = _win32()

    class RECT(ctypes.Structure):
        _fields_ = [
            ("left", wintypes.LONG),
            ("top", wintypes.LONG),
            ("right", wintypes.LONG),
            ("bottom", wintypes.LONG),
        ]

    rect = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    return (rect.left, rect.top, rect.right, rect.bottom)


def grab(cfg: Config, hwnd, path: Path) -> None:
    """Ask ClassicUO for the picture; photograph the screen if it will not.

    Print Screen makes CUO write its own back buffer out, which is exact and
    does not care whether the window is wholly on the monitor. Windows can
    steal that key for the snipping tool, so a screen grab stands behind it.
    """
    path.parent.mkdir(parents=True, exist_ok=True)

    shots = cfg.upstream_build / "Data" / "Client" / "Screenshots"
    before = set(shots.glob("*.png")) if shots.exists() else set()

    send_keys("{PRTSC}")

    deadline = time.time() + 6
    while time.time() < deadline:
        time.sleep(0.5)
        fresh = (set(shots.glob("*.png")) - before) if shots.exists() else set()
        if fresh:
            newest = max(fresh, key=lambda f: f.stat().st_mtime)
            shutil.move(str(newest), str(path))
            return

    print("[ab] Print Screen did not reach ClassicUO; grabbing the screen")
    from PIL import ImageGrab

    ImageGrab.grab(bbox=window_rect(hwnd), all_screens=True).save(path)


def shoot_cuo(
    cfg: Config,
    places: list[Place],
    root: Path,
    character: str,
    login_wait: float,
    settle: float,
) -> None:
    """One session, walked around with [go, photographed at each stop."""
    size = target_size(root, places)
    print(f"[ab] CUO window: {size[0]}x{size[1]}")
    conf = write_cuo_config(cfg, character, size)
    log = root / "cuo.log"

    proc = subprocess.Popen(
        [
            str(cfg.upstream_exe),
            "-settings",
            str(conf / "settings.json"),
            "-username",
            cfg.shard_owner,
            "-password",
            cfg.shard_owner_password,
            "-ip",
            cfg.shard_host,
            "-port",
            str(cfg.shard_port),
            "-autologin",
            "true",
            "-skiploginscreen",
            "-lastcharactername",
            character,
            "-music",
            "false",
        ],
        cwd=str(cfg.upstream_build),
        stdout=log.open("w", encoding="utf-8", errors="replace"),
        stderr=subprocess.STDOUT,
        # Without this FNA makes a window in logical pixels and Windows
        # magnifies it. On a 4K screen at 200% that renders ClassicUO at half
        # GUO's resolution and blows it up, which looks exactly like a
        # difference in zoom and is not one. Godot is DPI aware already.
        env={
            **os.environ,
            "FNA_GRAPHICS_ENABLE_HIGHDPI": "1",
            # And this, which is what actually settles it: the process is
            # marked DPI aware before it starts, so Windows hands SDL real
            # pixels instead of magnifying a half-size window afterwards.
            "__COMPAT_LAYER": "HighDpiAware",
        },
    )

    try:
        print(f"[ab] ClassicUO starting (pid {proc.pid}); logging in")

        hwnd = None
        deadline = time.time() + 60
        while time.time() < deadline and hwnd is None:
            time.sleep(1.0)
            hwnd = find_window(proc.pid)

        if hwnd is None:
            raise RuntimeError("ClassicUO never showed a window")

        # Autologin walks the login screen, the server list and the character
        # list, all of them on their own timers. There is nothing to poll, so
        # this waits.
        time.sleep(login_wait)
        focus(hwnd)
        nudge(hwnd)

        for place in places:
            print(f"[ab] cuo: {place.name} at {place.x} {place.y}")
            focus(hwnd)
            # [ and ] have to be braced for SendKeys even though they are not
            # otherwise special.
            send_keys(f"{{[}}globallight {place.light}{{ENTER}}")
            time.sleep(1.0)
            focus(hwnd)
            send_keys(f"{{[}}{place.go[1:]}{{ENTER}}")
            for line in place.say:
                time.sleep(1.5)
                focus(hwnd)
                send_keys(f"{line}{{ENTER}}")
            time.sleep(settle)
            focus(hwnd)
            grab(cfg, hwnd, root / place.name / "cuo.png")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            proc.kill()


# --------------------------------------------------------------------------
# The comparison sheet
# --------------------------------------------------------------------------


def compose(place_dir: Path, width: int = 1600) -> Path | None:
    """One PNG per place: CUO above, GUO below, each labelled."""
    from PIL import Image, ImageDraw

    cuo_path = place_dir / "cuo.png"
    guo_path = place_dir / "guo.png"

    if not (cuo_path.exists() and guo_path.exists()):
        return None

    bar = 28
    panels = []
    for label, path in (("ClassicUO", cuo_path), ("GUO", guo_path)):
        im = Image.open(path).convert("RGB")
        im = im.resize(
            (width, max(1, round(im.height * width / im.width))), Image.LANCZOS
        )
        panels.append((label, im))

    total = sum(im.height + bar for _, im in panels)
    sheet = Image.new("RGB", (width, total), (16, 16, 16))
    draw = ImageDraw.Draw(sheet)

    y = 0
    for label, im in panels:
        draw.text((8, 7), f"{label} -- {place_dir.name}", fill=(235, 235, 235))
        y += bar
        sheet.paste(im, (0, y))
        y += im.height

    out = place_dir / "ab.png"
    sheet.save(out)
    return out


# --------------------------------------------------------------------------


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--only",
        choices=("guo", "cuo", "both", "compose"),
        default="both",
        help="which client to photograph; compose only redraws the sheets",
    )
    parser.add_argument(
        "--place",
        action="append",
        help="photograph only this place; may be repeated",
    )
    parser.add_argument(
        "--character",
        help="whose GUO profile ClassicUO should borrow; the default is "
        "whichever character GUO played last",
    )
    parser.add_argument("--build", action="store_true", help="build ClassicUO first")
    parser.add_argument("--login-wait", type=float, default=35.0)
    parser.add_argument("--settle", type=float, default=7.0)
    parser.add_argument("--no-open", action="store_true")
    args = parser.parse_args()

    cfg = load_config()
    root = cfg.build / "screenshots" / "ab"
    root.mkdir(parents=True, exist_ok=True)

    places = PLACES
    if args.place:
        wanted = set(args.place)
        places = [p for p in PLACES if p.name in wanted]
        unknown = wanted - {p.name for p in PLACES}
        if unknown:
            print(f"[ab] no such place: {', '.join(sorted(unknown))}")
            return 1

    if args.only in ("guo", "both"):
        for place in places:
            print(f"[ab] guo: {place.name} at {place.x} {place.y}")
            shoot_guo(cfg, place, root / place.name)

    if args.only in ("cuo", "both"):
        if args.build or not cfg.upstream_exe.exists():
            build_cuo(cfg)
        character = args.character or last_played(cfg) or "Guoprobe"
        print(f"[ab] character: {character}")
        shoot_cuo(cfg, places, root, character, args.login_wait, args.settle)

    for place in places:
        sheet = compose(root / place.name)
        if sheet:
            print(f"[ab] {sheet}")

    print(f"[ab] Output: {root}")

    if not args.no_open:
        os.startfile(root)  # noqa: S606 -- opening a folder for the user

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
