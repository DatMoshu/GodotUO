r"""One login per client: every place in one session, nothing typed.

Compiled into both clients, ShotDump (godot\GUO\src\Bootstrap\ShotDump.cs, reached
through GUO_SHOT_DUMP) already stands in a place and photographs it with the
client's own screenshot code. A single session of shard commands walks it round
every place, so a client logs in once instead of once per place, and neither
window needs focus or the desktop's keyboard. See README.md.
"""
from __future__ import annotations

import importlib.util
import os
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
SESSION = "one-session"
GUO_VARIANT = "guo-default"

# The look GUO puts its best work into. Water style is not here: changing it
# needs a re-bake the profile setter does not trigger.
BEST_LIGHTING = ("LightingSpeedMode=True", "SpeedModeLampShadows=10")


def _ab():
    """tools/ab_compare/run.py, already loaded when run from there."""
    main = sys.modules.get("__main__")
    if main is not None and hasattr(main, "write_cuo_config"):
        return main
    spec = importlib.util.spec_from_file_location("ab_compare_session", HERE / "run.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def _visual_ab():
    """tools/visual_ab/run.py, for the helpers that keep a client off the desktop."""
    spec = importlib.util.spec_from_file_location("visual_ab_session", HERE.parent / "visual_ab" / "run.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def steps(places, settle_ms: int, before=(), hours=None) -> str:
    """ShotDump's step list for one session through every place.

    hours is a function from a place to the test-clock hour to set before its
    shot, or None. Each place sets the light, jumps, waits, hides the gumps and
    shoots: only the world is being compared.
    """
    out = ["set:HighlightGameObjects=False", *[f"set:{item}" for item in before]]
    for place in places:
        if hours is not None:
            out.append(f"set:LocalLightingTestHour={hours(place)}")
        out.append(f"say:[globallight {place.light}")
        out.append(f"say:{place.go}")
        out.append(f"wait:{settle_ms}")
        out.extend(f"say:{line}" for line in place.say)
        # Before every shot: gumps restored at login can open after the first.
        out.append("hide")
        out.append(f"shot:{place.name}")
    return "|".join(out)


def spec_for(out: Path, places, step_list: str, zoom: float = 0) -> str:
    """GUO_SHOT_DUMP: DIR;LABEL;X;Y;Z;ZOOM;STEPS. The first place is where it starts."""
    first = places[0]
    z = "" if first.z is None else first.z
    return f"{out};{SESSION};{first.x};{first.y};{z};{zoom};{step_list}"


def collect(out: Path, root: Path, variant: str, name: str, places) -> list[str]:
    """Moves each session shot to ROOT\\PLACE\\NAME.png; returns the places with no shot."""
    missing = []
    for place in places:
        shot = out / SESSION / f"{variant}_{place.name}.png"
        if not shot.exists():
            missing.append(place.name)
            continue
        target = root / place.name / f"{name}.png"
        target.parent.mkdir(parents=True, exist_ok=True)
        os.replace(shot, target)
    return missing


def _clear(out: Path, variant: str) -> Path:
    """Last run's marker and shots, gone first, so a failure cannot look like a pass."""
    folder = out / SESSION
    folder.mkdir(parents=True, exist_ok=True)
    (folder / f"{variant}.done").unlink(missing_ok=True)
    for old in folder.glob(f"{variant}_*.png"):
        old.unlink()
    return folder


def run_cuo(cfg, places, out: Path, character: str, settle_ms: int, size, timeout: float, zoom: float = 1.0) -> bool:
    """Stock ClassicUO, built with tools\\render_dump\\inject.targets."""
    ab, vab = _ab(), _visual_ab()
    folder = _clear(out, "cuo")
    conf = ab.write_cuo_config(cfg, character, size)
    with (folder / "cuo.log").open("w", encoding="utf-8", errors="replace") as log:
        proc = subprocess.Popen(
            [str(cfg.upstream_exe), "-settings", str(conf / "settings.json"),
             "-username", cfg.shard_owner, "-password", cfg.shard_owner_password,
             "-ip", cfg.shard_host, "-port", str(cfg.shard_port), "-autologin", "true", "-skiploginscreen",
             "-lastcharactername", character, "-music", "false"],
            cwd=str(cfg.upstream_build), stdout=log, stderr=subprocess.STDOUT,
            env={**os.environ, "FNA_GRAPHICS_ENABLE_HIGHDPI": "1", "__COMPAT_LAYER": "HighDpiAware",
                 "GUO_SHOT_DUMP": spec_for(out, places, steps(places, settle_ms), zoom),
                 "SDL_WINDOW_ACTIVATE_WHEN_SHOWN": "0"},
        )
    tucked = vab.Tucked(proc)
    tucked.start()
    try:
        return vab.wait_for(folder / "cuo.done", proc, timeout)
    finally:
        tucked.done.set()
        vab.end(proc)
        scrub_password(folder / "cuo.log")


def scrub_password(log: Path) -> None:
    """ClassicUO writes its command line, password included, into its own log."""
    if log.exists():
        text = log.read_text(encoding="utf-8", errors="replace")
        log.write_text(re.sub(r"(ARG: -?password, VALUE: )\S+", r"\1[scrubbed]", text, flags=re.I), encoding="utf-8")


def run_guo(cfg, places, out: Path, character: str, settle_ms: int, size, timeout: float,
            before=(), hours=None, cache_dir: str | None = None, zoom: float = 1.0) -> bool:
    """GUO, launched once, with ShotDump walking the same list."""
    ab, vab = _ab(), _visual_ab()
    folder = _clear(out, GUO_VARIANT)
    step_list = steps(places, settle_ms, before, hours)
    env = {**ab.guo_environment(cfg), "GUO_SHOT_DUMP": spec_for(out, places, step_list, zoom),
           "GUO_SHOT_VARIANT": GUO_VARIANT}
    command = [str(cfg.godot_console_exe), "--path", str(cfg.godot_project), "--", "--play", "--silent",
               "--no-focus", "--window-size", f"{size[0]},{size[1]}", "--autologin",
               "--account", cfg.shard_owner, "--password", cfg.shard_owner_password,
               "--character", character, "--postfx", "off", "--stay"]
    if cache_dir:
        command += ["--cache-dir", cache_dir]
    with (folder / f"{GUO_VARIANT}.log").open("w", encoding="utf-8", errors="replace") as log:
        proc = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, env=env)
    tucked = vab.Tucked(proc)
    tucked.start()
    try:
        return vab.wait_for(folder / f"{GUO_VARIANT}.done", proc, timeout)
    finally:
        tucked.done.set()
        vab.end(proc)


def hours_for(day: int | None, night: int | None, night_light: int):
    """A place-to-hour function for the Labs test clock, or None when no hour was asked for."""
    if day is None and night is None:
        return None
    day_hour = 12 if day is None else day
    night_hour = 22 if night is None else night
    return lambda place: night_hour if place.light == night_light else day_hour
