"""Capture path A: Godot's own MovieWriter, in the engine, then ffmpeg for the master.

`--write-movie run.avi --fixed-fps 60` renders every frame whatever the machine can do, so the video is smooth and
repeatable (slower than real time, never choppy). The AVI holds MJPEG frames and PCM audio. ffmpeg turns it into the
master: H.264 High, CRF 16, AAC, faststart. There is no desktop or OBS capture here; that is path B and a later step.

The movie is only complete when the engine exits cleanly, so a run asks the client to quit (guo_quit) before it
kills anything; a killed engine leaves an AVI without an index, which ffmpeg can usually still read.
"""

from __future__ import annotations

import shutil
import socket
import subprocess
from pathlib import Path

FPS = 60
STALL_S = 20                 # the movie file did not grow for this long: the engine is wedged
MIN_FREE_GB_RECORD = 10      # same floor as every heavy job (a 4K minute is several GB of MJPEG)
MIN_FREE_GB = 1


def engine_args(avi: Path, fps: int = FPS) -> str:
    """The engine flags, as one string for GUO_ENGINE_ARGS (launchers/game/play.bat puts it before the --)."""
    return f'--write-movie "{avi}" --fixed-fps {fps}'


def find_ffmpeg() -> str | None:
    return shutil.which("ffmpeg")


def transcode(ffmpeg: str, avi: Path, mp4: Path, crf: int = 16) -> tuple[bool, str]:
    """AVI to the master mp4. Returns (ok, one line of what happened)."""
    if not avi.is_file() or avi.stat().st_size == 0:
        return False, "the engine wrote no movie"
    cmd = [ffmpeg, "-y", "-loglevel", "error", "-i", str(avi), "-c:v", "libx264", "-profile:v", "high", "-crf", str(crf),
           "-preset", "slow", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", str(mp4)]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=3600)
    except (OSError, subprocess.SubprocessError) as ex:
        return False, f"ffmpeg did not run: {type(ex).__name__}"
    if r.returncode != 0 or not mp4.is_file():
        return False, "ffmpeg failed: " + (r.stderr.strip().splitlines() or ["no message"])[-1][:200]
    return True, f"{mp4.name}, {mp4.stat().st_size // 1024} KiB"


def free_gb(path: Path) -> float:
    probe = path
    while not probe.exists() and probe.parent != probe:
        probe = probe.parent
    return shutil.disk_usage(probe).free / 2**30


def preflight(*, record: bool, build_dir: Path, video_dir: str, shard: tuple[str, int] | None, need_client_files: bool = True) -> list[str]:
    """What the run needs, checked before anything launches. Returns the problems found (empty: go)."""
    problems: list[str] = []
    floor = MIN_FREE_GB_RECORD if record else MIN_FREE_GB
    if free_gb(build_dir) < floor:
        problems.append(f"less than {floor} GB free where the run folder goes")
    if record and find_ffmpeg() is None:
        problems.append("recording needs ffmpeg on PATH (the master is transcoded from the engine's movie)")
    if video_dir and not Path(video_dir).is_dir():
        problems.append("GUO_RUNS_VIDEO_DIR is set but that folder is not mounted (the master would stay local; unset it to accept that)")
    if shard is not None:
        try:
            with socket.create_connection(shard, timeout=3):
                pass
        except OSError:
            problems.append(f"the shard at {shard[0]}:{shard[1]} does not answer (launchers/shard/run.bat)")
    return problems


class StallWatch:
    """Notices a movie file that stopped growing. `now` is any monotonic clock; `size` reads the file."""

    def __init__(self, path: Path, now, limit_s: float = STALL_S):
        self.path = path
        self._now = now
        self._limit = limit_s
        self._size = -1
        self._since = 0.0

    def stalled(self) -> bool:
        try:
            size = self.path.stat().st_size
        except OSError:
            return False                 # not created yet: the launch step's own timeout covers that
        t = self._now()
        if size != self._size:
            self._size, self._since = size, t
            return False
        return t - self._since > self._limit
