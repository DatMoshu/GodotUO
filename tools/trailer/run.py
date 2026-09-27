#!/usr/bin/env python3
r"""The going-public trailer: cut from the clips the agents recorded, plus
the procedural loops, into a 1080p30 master and a Discord cut under 10 MB.

    python tools\trailer\run.py                 build both into build\trailer
    python tools\trailer\run.py --only-cut      re-encode the Discord cut from the master

The sources are read only. By default they are the recording worktrees'
build folders (--clips points elsewhere):

    ui-macro/build/ui/day   *_top.mp4 / *_low.mp4: the handheld's two
                            screens, recorded in step (pinch, window menu,
                            seam, the idle scene)
    editor/build/editor_clip/guo_editor_live_objects.mp4
    web/build/web/guoweb_world.mp4  GUOWeb's run in Chrome on the shard
                            (the login screen at its start is not used)
    build/trailer/work/splash/f_*.png  the splash at 1080p60, from
                            SplashProof --size 1920x1080 --plain --all

The Store loops and the screensaver are rendered here from
tools/bg_videos (pure functions of the loop phase). The music is a
procedural pad written by this file: CC0, like everything it makes. The
captions use the lower-third style of the UI clips: a black 62% bar, the
line in Segoe UI Bold, the detail in gold. Game footage is scaled with
nearest-neighbour so the pixel art stays sharp; only the brand sigil and the
loops are filtered.
"""
import argparse
import subprocess
import sys
import wave
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
WT = ROOT.parent if ROOT.parent.name == "worktrees" else ROOT / ".claude" / "worktrees"
OUT = ROOT / "build" / "trailer"
W, H, FPS = 1920, 1080, 30
BG = np.array([0x0c, 0x0a, 0x08], np.float32) / 255
GOLD = (0xE6, 0xC8, 0x78)
FONT_B = "C:/Windows/Fonts/segoeuib.ttf"
FONT = "C:/Windows/Fonts/segoeui.ttf"
FONT_L = "C:/Windows/Fonts/segoeuil.ttf"
REPO = "github.com/DatMoshu/GodotUO"

sys.path.insert(0, str(ROOT / "tools" / "bg_videos"))


def font(path, size):
    return ImageFont.truetype(path, size)


# ---------------------------------------------------------------- sources

def video(path, start, dur, w, h, nearest=True, speed=1.0, crop=None):
    """Frames (h, w, 3) float32 of a clip from start for dur seconds (output time), at FPS."""
    vf = []
    if crop:
        vf.append("crop=%d:%d:%d:%d" % crop)
    if speed != 1.0:
        vf.append(f"setpts=PTS/{speed}")
    vf += [f"fps={FPS}", f"scale={w}:{h}:flags={'neighbor' if nearest else 'lanczos'}"]
    cmd = ["ffmpeg", "-v", "error", "-ss", str(start), "-i", str(path), "-t", str(dur * speed),
           "-vf", ",".join(vf), "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]
    raw = subprocess.run(cmd, capture_output=True, check=True).stdout
    fr = np.frombuffer(raw, np.uint8).reshape(-1, h, w, 3)
    n = int(round(dur * FPS))
    if len(fr) < n:     # hold the last frame
        fr = np.concatenate([fr, np.repeat(fr[-1:], n - len(fr), 0)])
    return fr[:n]


def fit_nearest(w, h, box_w, box_h):
    s = min(box_w / w, box_h / h)
    return int(w * s) // 2 * 2, int(h * s) // 2 * 2


# ---------------------------------------------------------------- drawing

def caption_layer(title, detail):
    """The lower third as an RGBA overlay."""
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rectangle((0, H - 132, W, H), fill=(0, 0, 0, 158))
    d.text((56, H - 120), title, font=font(FONT_B, 42), fill=(255, 255, 255, 255))
    if detail:
        d.text((56, H - 62), detail, font=font(FONT, 26), fill=GOLD + (255,))
    return np.asarray(im, np.float32) / 255


def over(base, layer):
    a = layer[..., 3:4]
    return base * (1 - a) + layer[..., :3] * a


def label(img, x, y, text, size=22, fill=(170, 160, 140), anchor="la"):
    im = Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8))
    ImageDraw.Draw(im).text((x, y), text, font=font(FONT, size), fill=fill, anchor=anchor)
    return np.asarray(im, np.float32) / 255


def backdrop():
    y = np.linspace(0, 1, H, dtype=np.float32)[:, None, None]
    x = np.linspace(-1, 1, W, dtype=np.float32)[None, :, None]
    v = 1 - 0.5 * np.clip(np.sqrt(x * x * 0.6 + (y - 0.45) ** 2 * 2.2), 0, 1)
    return np.broadcast_to(BG * 1.6 * v, (H, W, 3)).copy()


def screen_frame(img, x, y, w, h):
    """A thin bezel around a pasted screen."""
    img[y - 3:y + h + 3, x - 3:x + w + 3] = np.array([0x2a, 0x24, 0x1c], np.float32) / 255
    return img


# ---------------------------------------------------------------- segments
# Each returns (frame count, iterator of float32 (H, W, 3) frames), so a
# segment streams to the encoder instead of sitting in memory.

def seg_splash(frames_dir):
    fs = sorted(Path(frames_dir).glob("f_*.png"))
    if not fs:
        sys.exit(f"no splash frames in {frames_dir}: run SplashProof --size 1920x1080 --plain --all first")
    fs = fs[::2]
    black = np.zeros((H, W, 3), np.float32)
    return len(fs) + 9, (np.asarray(Image.open(f).convert("RGB"), np.float32) / 255 if f else black for f in fs + [None] * 9)


def seg_title(seconds, loop_cls):
    th = loop_cls(W, H, 7)
    cap = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(cap)
    d.text((W // 2, H // 2 - 30), "One client. Every screen.", font=font(FONT_B, 92), fill=(255, 255, 255, 255), anchor="mm")
    d.text((W // 2, H // 2 + 60), "Windows  \u00b7  Android handhelds  \u00b7  Steam Deck  \u00b7  the browser",
           font=font(FONT, 34), fill=GOLD + (255,), anchor="mm")
    cap = np.asarray(cap, np.float32) / 255
    n = int(seconds * FPS)
    return n, (over(np.clip(th.frame(0.1 + 0.25 * i / n), 0, 1) * 0.42, cap) for i in range(n))


def seg_dual(day, name, start, dur, title, detail):
    """The handheld's two screens side by side: the world, then the windows."""
    hh = 640
    tw, lw = int(1920 * hh / 1080) // 2 * 2, int(1240 * hh / 1080) // 2 * 2
    gap = 28
    x0 = (W - tw - lw - gap) // 2
    y0 = 150
    top = video(day / f"{name}_top.mp4", start, dur, tw, hh)
    low = video(day / f"{name}_low.mp4", start, dur, lw, hh)
    base = backdrop()
    base = screen_frame(base, x0, y0, tw, hh)
    base = screen_frame(base, x0 + tw + gap, y0, lw, hh)
    base = label(base, x0, y0 - 38, "Top screen", 24)
    base = label(base, x0 + tw + gap, y0 - 38, "Bottom screen", 24)
    cap = caption_layer(title, detail)

    def frames():
        for t, l in zip(top, low):
            f = base.copy()
            f[y0:y0 + hh, x0:x0 + tw] = t / 255
            f[y0:y0 + hh, x0 + tw + gap:x0 + tw + gap + lw] = l / 255
            yield over(f, cap)
    return len(top), frames()


def seg_browser(clip, parts):
    """GUOWeb's run in Chrome (a 1280x800 page) in a drawn browser frame; parts are (start, seconds) cuts."""
    sw, sh = 1216, 760
    base = backdrop()
    bw, bh = sw + 2, sh + 58
    bx, by = (W - bw) // 2, 62
    im = Image.fromarray((base * 255).astype(np.uint8))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((bx - 1, by, bx + bw, by + bh), 10, fill=(0x2b, 0x2d, 0x31))
    d.rounded_rectangle((bx + 110, by + 12, bx + bw - 20, by + 44), 16, fill=(0x1e, 0x1f, 0x22))
    d.text((bx + 130, by + 28), "127.0.0.1:8061/GUO.html", font=font(FONT, 20), fill=(200, 200, 205), anchor="lm")
    for i, c in enumerate(((0xff, 0x5f, 0x57), (0xfe, 0xbc, 0x2e), (0x28, 0xc8, 0x40))):
        d.ellipse((bx + 18 + 26 * i, by + 20, bx + 32 + 26 * i, by + 34), fill=c)
    base = np.asarray(im, np.float32) / 255
    cap = caption_layer("In the browser", "The same client, playing on a live shard")
    fr = np.concatenate([video(clip, t, dur, sw, sh, nearest=False) for t, dur in parts])

    def frames():
        for e in fr:
            f = base.copy()
            f[by + 56:by + 56 + sh, bx + 1:bx + 1 + sw] = e / 255
            yield over(f, cap)
    return len(fr), frames()


def seg_store(seconds):
    from store_loops import STORE_LOOPS
    cw, ch, gx, gy = 560, 315, 24, 60
    x0 = (W - 3 * cw - 2 * gx) // 2
    y0 = 70
    ths = [c(cw, ch, 11) for c in STORE_LOOPS]
    base = backdrop()
    for k, c in enumerate(STORE_LOOPS):
        x, y = x0 + (k % 3) * (cw + gx), y0 + (k // 3) * (ch + gy)
        base = screen_frame(base, x, y, cw, ch)
        base = label(base, x, y + ch + 10, c.title, 24, (215, 205, 185))
    cap = caption_layer("The Store", "Backgrounds, themes, sounds and screensavers, as packs the client installs")
    n = int(seconds * FPS)

    def frames():
        for i in range(n):
            f = base.copy()
            for k, th in enumerate(ths):
                x, y = x0 + (k % 3) * (cw + gx), y0 + (k // 3) * (ch + gy)
                f[y:y + ch, x:x + cw] = np.clip(th.frame(0.2 + 0.3 * i / n), 0, 1)
            yield over(f, cap)
    return n, frames()


def seg_saver(seconds):
    from screensavers import SigilTrace
    th = SigilTrace(W, H, 5)
    cap = caption_layer("Screensavers for OLED screens", "Mostly true black, and nothing stays lit in one place")
    n = int(seconds * FPS)
    return n, (over(np.clip(th.frame(0.25 + 0.2 * i / n), 0, 1), cap) for i in range(n))


def seg_editor(clip, seconds):
    ew, eh = 1536, 1035     # the 1024 x 690 picture above the clip's own caption, 1.5x
    fr = video(clip, 0, seconds, ew, eh, crop=(1024, 690, 0, 0))
    base = backdrop()
    x0, y0 = (W - ew) // 2, 0
    caps = [(0, caption_layer("The editor, live on a running shard", "No restart: the world changes while you play")),
            (3.0, caption_layer("The editor, live on a running shard", "Place an anvil and a horse spawner")),
            (6.5, caption_layer("The editor, live on a running shard", "Move the anvil")),
            (11.8, caption_layer("The editor, live on a running shard", "Delete both, and they are gone"))]

    def frames():
        for i, e in enumerate(fr):
            f = base.copy()
            f[y0:y0 + eh, x0:x0 + ew] = e / 255
            yield over(f, [c for t, c in caps if i / FPS >= t][-1])
    return len(fr), frames()


def seg_end(seconds):
    sig = Image.open(ROOT / "godot" / "GUO" / "assets" / "brand" / "splash_sigil.png").convert("RGBA").resize((380, 380), Image.LANCZOS)
    im = Image.new("RGBA", (W, H), (0x14, 0x10, 0x0C, 255))
    im.alpha_composite(sig, ((W - 380) // 2, 170))
    d = ImageDraw.Draw(im)
    d.text((W // 2, 640), "GodotUO", font=font(FONT_B, 96), fill=(255, 255, 255, 255), anchor="mm")
    d.text((W // 2, 725), "Classic Ultima Online on Godot", font=font(FONT_L, 44), fill=(225, 220, 210, 255), anchor="mm")
    d.text((W // 2, 810), REPO, font=font(FONT, 34), fill=GOLD + (255,), anchor="mm")
    f = np.asarray(im.convert("RGB"), np.float32) / 255
    return int(seconds * FPS), (f for _ in range(int(seconds * FPS)))


def fade(seg, fin=8, fout=8):
    n, frames = seg
    return n, (f * min(1.0, (i + 1) / fin, (n - i) / fout) for i, f in enumerate(frames))


# ---------------------------------------------------------------- music

def music(seconds, path, sr=48000):
    """A slow procedural pad (A minor, F, C, G) with a sparse bell: CC0."""
    t = np.arange(int(seconds * sr)) / sr
    chords = [[57, 60, 64, 69], [53, 57, 60, 65], [48, 55, 60, 64], [55, 59, 62, 67]]
    bar = 4.0
    y = np.zeros_like(t)
    rng = np.random.default_rng(2026)
    for b in range(int(seconds / bar) + 2):
        notes = chords[b % 4]
        t0 = b * bar - 0.8
        env = np.clip((t - t0) / 1.6, 0, 1) * np.clip((t0 + bar + 2.2 - t) / 2.2, 0, 1)
        env = env * env * (3 - 2 * env)
        m = env > 0
        for n in notes:
            f = 440 * 2 ** ((n - 69) / 12)
            for det in (-0.12, 0.0, 0.13):
                ph = rng.random() * 6.28
                y[m] += env[m] * 0.05 * (np.sin(2 * np.pi * f * (1 + det / 100) * t[m] + ph)
                                          + 0.3 * np.sin(4 * np.pi * f * (1 + det / 100) * t[m] + ph))
        if b % 2 == 1:
            n = notes[-1] + 12
            tb = b * bar + 1.0
            mb = (t >= tb) & (t < tb + 3)
            y[mb] += 0.06 * np.exp(-(t[mb] - tb) * 1.6) * np.sin(2 * np.pi * 440 * 2 ** ((n - 69) / 12) * (t[mb] - tb))
    # one-pole low-pass, then a gentle fade at both ends
    a = np.exp(-2 * np.pi * 1800 / sr)
    from scipy.signal import lfilter
    y = lfilter([1 - a], [1, -a], y)
    y *= np.clip(t / 1.5, 0, 1) * np.clip((seconds - t) / 3.0, 0, 1)
    y = y / np.abs(y).max() * 0.35
    st = np.stack([y, np.roll(y, int(0.011 * sr))], 1)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes((st * 32767).astype(np.int16).tobytes())


# ---------------------------------------------------------------- build

def build(clips):
    from store_loops import BritainLanterns
    day = clips / "ui-macro" / "build" / "ui" / "day"
    OUT.mkdir(parents=True, exist_ok=True)
    video_path = OUT / "trailer_video.mp4"
    ff = subprocess.Popen(["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}",
                           "-r", str(FPS), "-i", "-", "-c:v", "libx264", "-preset", "slow", "-crf", "16",
                           "-pix_fmt", "yuv420p", str(video_path)], stdin=subprocess.PIPE)
    total = 0
    plan = [
        ("splash", lambda: fade(seg_splash(OUT / "work" / "splash"), 1, 1)),
        ("title", lambda: fade(seg_title(3.6, BritainLanterns), 12, 10)),
        ("handheld", lambda: fade(seg_dual(day, "guo", 2.0, 8.0, "Android handheld, two screens",
                                           "The world on the top screen, your windows on the bottom one"))),
        ("browser", lambda: fade(seg_browser(clips / "web" / "build" / "web" / "guoweb_world.mp4",
                                              [(27.0, 5.0), (41.0, 3.8)]))),
        ("pinch", lambda: fade(seg_dual(day, "pd", 0.5, 8.5, "Pinch any window to scale it",
                                        "From 75% to 300%. A pinch on the world still zooms the world"))),
        ("menu", lambda: fade(seg_dual(day, "wm", 3.0, 10.0, "Hold a window for its menu",
                                       "Size it, lock the pinch, or move it to the other screen in one tap"))),
        ("seam", lambda: fade(seg_dual(day, "seam", 0.0, 4.5, "Send a window to the other screen",
                                       "Drag it across the seam between the two screens"))),
        ("store", lambda: fade(seg_store(6.5))),
        ("saver", lambda: fade(seg_saver(4.0))),
        ("editor", lambda: fade(seg_editor(clips / "editor" / "build" / "editor_clip" / "guo_editor_live_objects.mp4", 13.6))),
        ("end", lambda: fade(seg_end(5.5), 15, 20)),
    ]
    marks = []
    for name, make in plan:
        n, frames = make()
        marks.append((name, round(total / FPS, 2), round(n / FPS, 2)))
        for f in frames:
            ff.stdin.write((np.clip(f, 0, 1) * 255 + 0.5).astype(np.uint8).tobytes())
        total += n
        print(f"[trailer] {name:9} at {marks[-1][1]:6.2f} s, {marks[-1][2]:5.2f} s", flush=True)
    ff.stdin.close()
    ff.wait()
    seconds = total / FPS
    music(seconds, OUT / "trailer_music.wav")
    master = OUT / "GodotUO_trailer_1080p.mp4"
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(video_path), "-i", str(OUT / "trailer_music.wav"),
                    "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", str(master)], check=True)
    print(f"[trailer] master {master} ({seconds:.1f} s, {master.stat().st_size / 1e6:.1f} MB)")
    return master, seconds


def discord_cut(master, seconds, limit_mb=9.5):
    """Two-pass x264 at 720p, sized to land under limit_mb."""
    out = OUT / "GodotUO_trailer_discord.mp4"
    abr = 96
    vbr = int(limit_mb * 8e3 / seconds - abr - 20)
    common = ["-vf", "scale=1280:720:flags=lanczos", "-c:v", "libx264", "-preset", "slow", "-b:v", f"{vbr}k", "-pix_fmt", "yuv420p"]
    passlog = str(OUT / "work" / "x264pass")
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(master), *common, "-pass", "1", "-passlogfile", passlog, "-an", "-f", "mp4", "NUL"], check=True)
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(master), *common, "-pass", "2", "-passlogfile", passlog,
                    "-c:a", "aac", "-b:a", f"{abr}k", "-movflags", "+faststart", str(out)], check=True)
    print(f"[trailer] discord {out} ({out.stat().st_size / 1e6:.2f} MB at {vbr} kbit/s)")
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--clips", type=Path, default=WT, help="folder holding the recording worktrees")
    ap.add_argument("--only-cut", action="store_true", help="only re-encode the Discord cut from the master")
    args = ap.parse_args()
    (OUT / "work").mkdir(parents=True, exist_ok=True)
    if args.only_cut:
        master = OUT / "GodotUO_trailer_1080p.mp4"
        dur = float(subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(master)],
                                   capture_output=True, text=True).stdout)
        discord_cut(master, dur)
        return
    master, seconds = build(args.clips)
    discord_cut(master, seconds)


if __name__ == "__main__":
    main()
