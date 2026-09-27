"""Render GUO's built-in background loops: original procedural art, CC0.

Every theme is a pure function of the loop phase p in [0, 1): noise volumes
are periodic in time, particles move a whole number of screen wraps per loop,
and every oscillator runs a whole number of cycles. Frame N therefore equals
frame 0 exactly and the player can loop the file with no seam and no fade.

    python tools/bg_videos/run.py                    all ten -> godot/GUO/assets/backgrounds
    python tools/bg_videos/run.py --only ember-drift
    python tools/bg_videos/run.py --size 1920x1080 --out D:/masters   (masters stay outside the repo)
    python tools/bg_videos/run.py --set screensavers   the OLED loops -> godot/GUO/assets/screensavers

Output per theme: <name>.ogv (Ogg Theora, no audio) and <name>.png (the
exact first frame, the low-power still), plus backgrounds.json listing them.
A contact sheet and a seam report go to build/bg_videos/.
"""
import argparse
import json
import shutil
import subprocess
import sys
import time
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage
from scipy.spatial import cKDTree
from scipy.special import erf

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUT = ROOT / "godot" / "GUO" / "assets" / "backgrounds"
REPORT_DIR = ROOT / "build" / "bg_videos"
FPS = 24
TAU = 2 * np.pi


# ---------------------------------------------------------------- helpers

def hexrgb(h):
    return np.array([int(h[i:i + 2], 16) / 255 for i in (1, 3, 5)], np.float32)


def ramp(v, stops):
    """Map v through colour stops [(pos, '#rrggbb'), ...] -> (..., 3)."""
    pos = np.array([p for p, _ in stops], np.float32)
    cols = np.stack([hexrgb(c) for _, c in stops])
    out = np.empty(np.shape(v) + (3,), np.float32)
    for c in range(3):
        out[..., c] = np.interp(v, pos, cols[:, c])
    return out


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def n01(x):
    """Standard-normal field -> roughly uniform [0, 1]."""
    return 0.5 * (1 + erf(x / np.sqrt(2)))


def noise(shape, beta, seed, tc=None, small=None):
    """Gaussian noise with power spectrum 1/k^beta, periodic along every axis.

    With tc, axis 0 is time in keyframes over one loop and its spectrum is
    cut off near tc cycles per loop, so the field evolves slowly and loops.
    small (pixels) rolls off detail finer than that. Returns mean 0, std 1."""
    rng = np.random.default_rng(seed)
    nd = len(shape)
    F = np.fft.rfftn(rng.standard_normal(shape))
    k2 = 0.0
    kt = None
    for i, n in enumerate(shape):
        f = np.fft.rfftfreq(n) if i == nd - 1 else np.fft.fftfreq(n)
        f = f.reshape([-1 if j == i else 1 for j in range(nd)])
        if tc is not None and i == 0:
            kt = f * n
        else:
            k2 = k2 + f * f
    k = np.sqrt(k2)
    with np.errstate(divide="ignore"):
        amp = np.where(k > 0, k, np.inf) ** (-beta / 2)
    if small:
        amp = amp * np.exp(-(k * small) ** 2)
    if kt is not None:
        amp = amp * np.exp(-(kt / tc) ** 2)
    out = np.fft.irfftn(F * amp, s=shape)
    out = (out - out.mean()) / out.std()
    return out.astype(np.float32)


def at(vol, p):
    """Sample a time-periodic volume at loop phase p (Catmull-Rom, wraps)."""
    K = vol.shape[0]
    x = (p % 1.0) * K
    i = int(np.floor(x))
    t = x - i
    p0, p1, p2, p3 = (vol[(i + d) % K] for d in (-1, 0, 1, 2))
    return 0.5 * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t
                  + (3 * p1 - p0 - 3 * p2 + p3) * t ** 3)


def drift(field, dy, dx):
    """Shift a periodic tile, wrapping (sub-pixel)."""
    return ndimage.shift(field, (dy, dx), order=3, mode="grid-wrap")


def up(f, W, H):
    """Bicubic upscale of a float field (or an (h, w, 3) image) to W x H."""
    if f.ndim == 3:
        return np.stack([up(f[..., c], W, H) for c in range(f.shape[2])], -1)
    img = Image.fromarray(np.ascontiguousarray(f, np.float32), "F")
    return np.asarray(img.resize((W, H), Image.BICUBIC))


def splat(img, x, y, sig, col, amp):
    """Add a soft gaussian dot (sub-pixel placed) to an (H, W, 3) image."""
    H, W = img.shape[:2]
    r = int(sig * 3 + 1)
    xi, yi = int(np.floor(x)), int(np.floor(y))
    x0, x1 = max(xi - r, 0), min(xi + r + 2, W)
    y0, y1 = max(yi - r, 0), min(yi + r + 2, H)
    if x0 >= x1 or y0 >= y1:
        return
    gx = np.exp(-((np.arange(x0, x1) - x) ** 2) / (2 * sig * sig))
    gy = np.exp(-((np.arange(y0, y1) - y) ** 2) / (2 * sig * sig))
    img[y0:y1, x0:x1] += (amp * gy[:, None] * gx[None, :])[..., None] * col


def wrap(v, span, margin):
    """Loop coordinate v (whole units = one trip) over [-margin, span + margin)."""
    return (v % 1.0) * (span + 2 * margin) - margin


def periodic_signal(rng, kmin, kmax, count):
    """A random wobble made of whole-cycle sines; returns f(p) in about [-1, 1]."""
    ks = rng.integers(kmin, kmax + 1, count)
    ph = rng.random(count) * TAU
    a = 1 / np.sqrt(ks)
    a = a / a.sum()
    return lambda p: float(np.sum(a * np.sin(TAU * ks * p + ph)))


# ---------------------------------------------------------------- themes

class Theme:
    name = title = ""
    seconds = 24

    def __init__(self, W, H, seed):
        self.W, self.H = W, H
        self.a = W / H
        self.s = H / 720
        self.seed = seed
        self.rng = np.random.default_rng(seed)
        self.X = np.linspace(0, 1, W, dtype=np.float32)[None, :]
        self.Y = np.linspace(0, 1, H, dtype=np.float32)[:, None]
        self.setup()

    def setup(self):
        pass

    def frame(self, p):
        raise NotImplementedError

    # shared pieces
    def stars(self, n, ymax, bright=(0.12, 0.7)):
        r = self.rng
        self._st = dict(
            x=r.random(n) * self.W, y=r.random(n) ** 1.3 * ymax * self.H,
            b=bright[0] + (bright[1] - bright[0]) * r.random(n) ** 3,
            sg=(0.55 + 0.6 * r.random(n)) * self.s,
            k=r.integers(1, 9, n), ph=r.random(n) * TAU,
            c=np.stack([hexrgb("#c8d6ff"), hexrgb("#fff2dc")])[r.integers(0, 2, n)])

    def draw_stars(self, img, p, mask=None):
        st = self._st
        tw = 0.72 + 0.28 * np.sin(TAU * st["k"] * p + st["ph"])
        for i in range(len(st["x"])):
            b = st["b"][i] * tw[i]
            if mask is not None:
                b *= mask[min(int(st["y"][i]), self.H - 1), min(int(st["x"][i]), self.W - 1)]
            splat(img, st["x"][i], st["y"][i], st["sg"][i], st["c"][i], b)

    def ridge(self, base, amp, seed, beta=2.2):
        line = noise((self.W,), beta, seed)
        return base + amp * line / np.abs(line).max()


class MoongateShimmer(Theme):
    name, title, seconds = "moongate-shimmer", "Moongate shimmer", 24

    def setup(self):
        W, H, a = self.W, self.H, self.a
        d = np.sqrt(((self.X - 0.5) * a) ** 2 + (self.Y - 0.52) ** 2)
        self.bg = ramp(np.clip(d / 0.9, 0, 1), [(0, "#0b1230"), (0.5, "#060a1c"), (1, "#020309")])
        self.hw, self.hh = W // 2, H // 2
        hx = np.linspace(0, 1, self.hw, dtype=np.float32)[None, :]
        hy = np.linspace(0, 1, self.hh, dtype=np.float32)[:, None]
        ex, ey = (hx - 0.5) * a / 0.17, (hy - 0.52) / 0.33
        self.ell = np.sqrt(ex * ex + ey * ey)
        self.th = (np.arctan2(ey, ex) / TAU) % 1.0
        self.T1 = n01(noise((256, 128), 3.0, self.seed + 1))
        self.T2 = n01(noise((256, 128), 2.4, self.seed + 2))
        r = self.rng
        n = 70
        self.m = dict(t0=r.random(n), dirn=r.choice([-1, 1], n), rr=1.08 + 0.6 * r.random(n),
                      b=0.08 + 0.2 * r.random(n), sg=(0.8 + 1.0 * r.random(n)) * self.s,
                      k=r.integers(2, 7, n), ph=r.random(n) * TAU)

    def frame(self, p):
        ell, th = self.ell, self.th
        c1 = [(th + p + ell * 0.16) * 256, (ell * 0.5 - p) * 128]
        c2 = [(th - 2 * p - ell * 0.1) * 256, (ell * 0.8 - 2 * p) * 128]
        s1 = ndimage.map_coordinates(self.T1, c1, order=1, mode="grid-wrap")
        s2 = ndimage.map_coordinates(self.T2, c2, order=1, mode="grid-wrap")
        sw = 0.6 * s1 + 0.4 * s2
        inside = smoothstep(1.02, 0.78, ell)
        rim = np.exp(-((ell - 1) / 0.05) ** 2)
        halo = np.exp(-np.abs(ell - 1) * 5) * 0.14
        pulse = 1 + 0.06 * np.sin(TAU * 2 * p)
        inten = (inside * (0.18 + 0.42 * sw ** 1.6) + rim * (0.4 + 0.2 * sw) + halo) * pulse
        col = ramp(sw, [(0, "#1a3584"), (0.5, "#3a86d0"), (0.8, "#7e8cf0"), (1, "#b08cf2")])
        glow = up(col * (0.78 * inten)[..., None], self.W, self.H)
        img = self.bg + glow
        m = self.m
        ang = TAU * (m["t0"] + m["dirn"] * p)
        cx, cy = 0.5 * self.W, 0.52 * self.H
        tw = 0.6 + 0.4 * np.sin(TAU * m["k"] * p + m["ph"])
        blue = hexrgb("#9fc4ff")
        for i in range(len(ang)):
            x = cx + np.cos(ang[i]) * m["rr"][i] * 0.17 * self.H
            y = cy + np.sin(ang[i]) * m["rr"][i] * 0.33 * self.H
            splat(img, x, y, m["sg"][i], blue, m["b"][i] * tw[i])
        return img


class CandleParchment(Theme):
    name, title, seconds = "candle-parchment", "Candlelit parchment", 24

    def setup(self):
        W, H, a = self.W, self.H, self.a
        base = n01(up(noise((180, 320), 3.2, self.seed + 1), W, H))
        fine = n01(up(noise((360, 640), 2.0, self.seed + 2), W, H))
        fib = n01(up(noise((360, 80), 1.6, self.seed + 3), W, H))
        stain = smoothstep(0.72, 0.93, n01(up(noise((90, 160), 3.4, self.seed + 4), W, H)))
        tone = 0.55 * base + 0.2 * fine + 0.25 * fib
        parch = ramp(tone, [(0, "#2a1c0e"), (0.5, "#56401f"), (1, "#7a5b34")])
        parch *= (1 - 0.35 * stain)[..., None]
        d = np.sqrt(((self.X - 0.5) * a) ** 2 + (self.Y - 0.5) ** 2)
        parch *= (1 - 0.55 * smoothstep(0.45, 1.0, d))[..., None]
        self.parch = parch
        r = self.rng
        self.fl = periodic_signal(r, 12, 70, 14)
        self.fx = periodic_signal(r, 3, 12, 5)
        self.fy = periodic_signal(r, 3, 12, 5)
        n = 45
        self.dust = dict(x0=r.random(n), y0=r.random(n), k=r.integers(1, 3, n),
                         wm=r.integers(1, 4, n), wa=0.01 + 0.03 * r.random(n), ph=r.random(n) * TAU,
                         sg=(0.7 + 0.9 * r.random(n)) * self.s, b=0.1 + 0.25 * r.random(n))
        self.warm = np.array([1.0, 0.8, 0.58], np.float32)

    def light_at(self, x, y, p):
        lx = 0.22 + 0.006 * self.fx(p)
        ly = 0.88 + 0.006 * self.fy(p)
        d = np.sqrt(((x - lx) * self.a) ** 2 + (y - ly) ** 2)
        return (1 + (d / 0.55) ** 2) ** -1.3 * (1 + 0.06 * self.fl(p))

    def frame(self, p):
        L = self.light_at(self.X, self.Y, p)
        img = self.parch * (0.2 + 1.05 * L)[..., None] * self.warm
        dd = self.dust
        for i in range(len(dd["x0"])):
            y = wrap(dd["y0"][i] - dd["k"][i] * p, 1, 0.02)
            x = (dd["x0"][i] + dd["wa"][i] * np.sin(TAU * dd["wm"][i] * p + dd["ph"][i])) % 1
            b = dd["b"][i] * float(self.light_at(x, y, p))
            splat(img, x * self.W, y * self.H, dd["sg"][i], hexrgb("#ffd9a0"), b)
        return img


class StarlitSea(Theme):
    name, title, seconds = "starlit-sea", "Starlit sea", 24
    hz = 0.58

    def setup(self):
        W, H, a, hz = self.W, self.H, self.a, self.hz
        X, Y = self.X, self.Y
        sky = ramp(np.clip(Y / hz, 0, 1), [(0, "#01030b"), (0.7, "#061126"), (1, "#112441")])
        glow = np.exp(-(((X - 0.68) * a) ** 2 / 0.09 + (Y - hz) ** 2 / 0.025))
        sky = sky + glow[..., None] * 0.13 * hexrgb("#7d97c0")
        band = n01(up(noise((90, 160), 2.6, self.seed + 1), W, H))
        bd = np.exp(-(((Y - 0.1 - 0.35 * X) / 0.12) ** 2))
        sky = sky + (bd * smoothstep(0.4, 0.9, band) * 0.06)[..., None] * hexrgb("#9aa8d0")
        self.sky = sky
        self.stars(520, hz - 0.03)
        self.vol = noise((32, 128, 256), 2.6, self.seed + 2, tc=3)
        hw, hh = W // 2, H // 2
        self.hw, self.hh = hw, hh
        self.r0 = int(np.ceil(hz * hh))
        hx = np.linspace(0, 1, hw, dtype=np.float32)[None, :]
        hy = np.linspace(0, 1, hh, dtype=np.float32)[self.r0:, None]
        dy = hy - hz + 0.004
        dist = 1 / dy
        self.u = np.broadcast_to((hx - 0.5) * a * dist * 8.0, (len(hy), hw))
        self.v = np.broadcast_to(dist * 16.0, (len(hy), hw))
        self.fade = np.broadcast_to(np.clip(1 - dist / 90, 0, 1) ** 1.5, (len(hy), hw))
        base = ramp(np.clip((hy - hz) / (1 - hz), 0, 1), [(0, "#0c1a2e"), (0.35, "#061020"), (1, "#02060d")])
        self.seabase = np.broadcast_to(base, (len(hy), hw, 3))
        spread = 0.1 + 0.7 * (hy - hz)
        self.glint = np.exp(-(((hx - 0.68) * a / spread) ** 2)) * 0.3 + 0.012

    def frame(self, p):
        f = at(self.vol, p)
        n = ndimage.map_coordinates(f, [self.v - p * 128, self.u], order=1, mode="grid-wrap")
        n = n01(n * self.fade)
        sea = self.seabase + ((n - 0.5) * 0.035)[..., None]
        sea = sea + (smoothstep(0.76, 0.95, n) * self.glint)[..., None] * hexrgb("#a6bddc")
        half = np.zeros((self.hh, self.hw, 3), np.float32)
        half[self.r0:] = sea
        seaf = up(half, self.W, self.H)
        m = (self.Y >= self.hz).astype(np.float32)[..., None]
        m = np.clip((self.Y - self.hz) * self.H + 0.5, 0, 1)[..., None]
        img = self.sky * (1 - m) + seaf * m
        self.draw_stars(img, p)
        return img


class DriftingFog(Theme):
    name, title, seconds = "drifting-fog", "Drifting fog", 30

    def setup(self):
        W, H = self.W, self.H
        X, Y = self.X, self.Y
        img = ramp(Y[:, 0], [(0, "#0a0e0f"), (0.55, "#18201f"), (1, "#080a0a")])
        img = np.broadcast_to(img[:, None, :], (H, W, 3)).copy()
        for base, amp, sd, col in ((0.6, 0.06, 1, "#131a19"), (0.7, 0.08, 2, "#0c1110"),
                                   (0.84, 0.05, 3, "#060808")):
            line = self.ridge(base, amp, self.seed + sd)[None, :]
            m = np.clip((Y - line) * H + 0.5, 0, 1)[..., None]
            img = img * (1 - m) + hexrgb(col) * m
        self.bg = img
        self.A = noise((40, 90, 160), 3.2, self.seed + 4, tc=2)
        self.B = noise((40, 90, 160), 3.0, self.seed + 5, tc=2)
        self.C = noise((40, 60, 106), 3.4, self.seed + 6, tc=2)
        self.wa = (smoothstep(0.3, 0.55, Y) * smoothstep(0.85, 0.6, Y))
        self.wb = smoothstep(0.45, 0.9, Y)
        self.wc = 0.5 + 0.5 * smoothstep(0.2, 1.0, Y)
        self.fog = hexrgb("#76857f")

    def frame(self, p):
        W, H = self.W, self.H
        fa = n01(up(at(self.A, p), W, H))
        fb = n01(up(drift(at(self.B, p), 0, (p % 1) * 160), W, H))
        fc = n01(up(drift(at(self.C, p), 0, (p % 1) * 106), W, H))
        img = self.bg
        for f, w, k in ((fa, self.wa, 0.4), (fb, self.wb, 0.35), (fc, self.wc, 0.22)):
            d = (smoothstep(0.35, 0.95, f) * w * k)[..., None]
            img = img * (1 - d) + self.fog * 0.42 * d
        return img


class EmberDrift(Theme):
    name, title, seconds = "ember-drift", "Ember drift", 24

    def setup(self):
        W, H, X, Y = self.W, self.H, self.X, self.Y
        g = np.exp(-(1 - Y) * 3.8) * (1 - 0.35 * ((X - 0.5) * 2) ** 2)
        self.glowmap = g[..., None] * hexrgb("#8a2a0a")
        self.bg = np.broadcast_to(hexrgb("#040102"), (H, W, 3))
        self.smoke = noise((32, 90, 160), 3.0, self.seed + 1, tc=2)
        self.sw = (0.3 + 0.7 * Y)[..., None]
        r = self.rng
        n = 260
        depth = r.random(n)
        self.e = dict(x0=r.random(n), y0=r.random(n), k=np.where(depth > 0.55, 2, 1) + (depth > 0.9),
                      wm=r.integers(1, 4, n), wa=0.005 + 0.025 * r.random(n), ph=r.random(n) * TAU,
                      q=r.integers(25, 70, n), fph=r.random(n) * TAU,
                      sg=(0.6 + 1.3 * depth) * self.s, b=0.2 + 0.55 * depth,
                      c=hexrgb("#ff8a30") * (1 - r.random(n)[:, None] * 0.0) +
                      (hexrgb("#ffd070") - hexrgb("#ff8a30")) * r.random(n)[:, None])
        self.breathe = periodic_signal(r, 2, 6, 3)

    def frame(self, p):
        W, H = self.W, self.H
        sm = n01(up(drift(at(self.smoke, p), -(p % 1) * 90, 0), W, H))
        img = self.bg + self.glowmap * (0.32 * (1 + 0.1 * self.breathe(p)))
        img = img + (smoothstep(0.5, 0.95, sm)[..., None] * 0.1) * hexrgb("#4a3026") * self.sw
        e = self.e
        for i in range(len(e["x0"])):
            y = wrap(e["y0"][i] - e["k"][i] * p, 1, 0.02)
            x = (e["x0"][i] + e["wa"][i] * np.sin(TAU * e["wm"][i] * p + e["ph"][i])) % 1
            fl = 0.65 + 0.35 * np.sin(TAU * e["q"][i] * p + e["fph"][i])
            b = e["b"][i] * fl * (0.15 + 0.85 * np.clip(y, 0, 1))
            splat(img, x * W, y * H, e["sg"][i], e["c"][i], b)
            splat(img, x * W, y * H, e["sg"][i] * 3.5, e["c"][i], b * 0.06)
        return img


class RainOnStone(Theme):
    name, title, seconds = "rain-on-stone", "Rain on stone", 24

    def setup(self):
        W, H, a, s = self.W, self.H, self.a, self.s
        r = self.rng
        gx, gy = 16, 9
        pts = []
        for j in range(-1, gy + 1):
            for i in range(-1, gx + 1):
                pts.append(((i + 0.2 + 0.6 * r.random()) / gx * a, (j + 0.2 + 0.6 * r.random()) / gy))
        pts = np.array(pts)
        yy, xx = np.mgrid[0:H, 0:W]
        q = np.stack([(xx.ravel() / W) * a, yy.ravel() / H], 1)
        dist, idx = cKDTree(pts).query(q, k=2)
        edge = (dist[:, 1] - dist[:, 0]).reshape(H, W)
        cell = idx[:, 0].reshape(H, W)
        tone = (0.55 + 0.45 * r.random(len(pts)))[cell]
        tex = n01(up(noise((180, 320), 2.2, self.seed + 1), W, H))
        bevel = smoothstep(0.004, 0.03, edge)
        stone = ramp(np.clip(tone * (0.7 + 0.3 * tex), 0, 1), [(0, "#07090c"), (0.6, "#171c22"), (1, "#28303a")])
        stone = stone * (0.45 + 0.55 * bevel)[..., None] * 0.8
        sheen = np.exp(-((self.X - 0.55) * a) ** 2 / 0.35 - (self.Y - 0.3) ** 2 / 0.12)
        stone = stone + (sheen * bevel * (0.4 + 0.6 * tex) * 0.09)[..., None] * hexrgb("#8fa6c2")
        self.bg = stone
        n = 260
        self.rain = dict(x0=r.random(n) * W, y0=r.random(n), k=r.integers(20, 33, n),
                         ln=(18 + 18 * r.random(n)) * s, b=0.06 + 0.07 * r.random(n),
                         sg=(0.55 + 0.35 * r.random(n)) * s)
        self.slant = -0.13
        m = 90
        self.rip = []
        for j in range(m):
            pj = int(r.integers(8, 17))
            self.rip.append((pj, r.random(), r.random((pj, 2))))
        self.rcol = hexrgb("#a7bbd0")

    def frame(self, p):
        W, H, s = self.W, self.H, self.s
        img = self.bg.copy()
        for pj, sj, pos in self.rip:
            t = p * pj + sj
            c = int(np.floor(t)) % pj
            age = (t - np.floor(t)) * self.seconds / pj
            if age > 0.9:
                continue
            rad = (2 + age * 30) * s
            amp = 0.2 * (1 - age / 0.9) ** 2
            cx, cy = pos[c, 0] * W, pos[c, 1] * H
            R = int(rad + 6 * s)
            x0, x1 = max(int(cx) - R, 0), min(int(cx) + R + 1, W)
            y0, y1 = max(int(cy - R * 0.55), 0), min(int(cy + R * 0.55) + 1, H)
            if x0 >= x1 or y0 >= y1:
                continue
            xs = np.arange(x0, x1)[None, :] - cx
            ys = (np.arange(y0, y1)[:, None] - cy) / 0.55
            d = np.sqrt(xs * xs + ys * ys)
            ring = np.exp(-((d - rad) / (1.3 * s)) ** 2) - 0.6 * np.exp(-((d - rad + 3 * s) / (1.6 * s)) ** 2)
            img[y0:y1, x0:x1] += (amp * ring)[..., None] * self.rcol
        rn = self.rain
        span = H * 1.1
        for i in range(len(rn["x0"])):
            yh = wrap(rn["y0"][i] + rn["k"][i] * p, span, 0) - 0.05 * H
            for t in np.linspace(0, 1, max(int(rn["ln"][i] / rn["sg"][i]), 6)):
                y = yh - t * rn["ln"][i]
                x = (rn["x0"][i] + self.slant * y) % W
                splat(img, x, y, rn["sg"][i], self.rcol, rn["b"][i] * (1 - 0.6 * t))
        return img


class AuroraNight(Theme):
    name, title, seconds = "aurora-night", "Aurora night", 30

    def setup(self):
        W, H, X, Y = self.W, self.H, self.X, self.Y
        sky = ramp(Y[:, 0], [(0, "#010208"), (0.5, "#040a16"), (0.72, "#0a1a22"), (1, "#0a1a22")])
        self.sky = np.broadcast_to(sky[:, None, :], (H, W, 3)).copy()
        self.stars(380, 0.8, (0.08, 0.5))
        self.ridges = []
        for base, amp, sd, col in ((0.7, 0.09, 1, "#050a12"), (0.83, 0.06, 2, "#010204")):
            line = self.ridge(base, amp, self.seed + sd)[None, :]
            self.ridges.append((np.clip((Y - line) * H + 0.5, 0, 1)[..., None], hexrgb(col)))
        self.mask_sky = 1 - self.ridges[0][0][..., 0]
        self.C1 = noise((48, 512), 2.4, self.seed + 3, tc=2)
        self.C2 = noise((48, 512), 2.4, self.seed + 4, tc=2)
        self.S = noise((48, 1024), 1.0, self.seed + 5, tc=3)
        self.E = noise((48, 256), 2.6, self.seed + 6, tc=2)
        self.xs = np.linspace(0, 1, W, endpoint=False)

    def row(self, vol, p, shift=0.0):
        f = at(vol, p)
        n = len(f)
        return np.interp((self.xs + shift) % 1 * n, np.arange(n + 1), np.append(f, f[0]))

    def curtain(self, base, amp, c, s, env, k):
        yb = (base + amp * c)[None, :]
        dy = yb - self.Y
        shape = np.where(dy > 0, np.exp(-dy / 0.2), np.exp(-(dy / 0.03) ** 2))
        inten = shape * (0.35 + 0.65 * s[None, :]) * env[None, :] * k
        mix = smoothstep(0.02, 0.26, dy)[..., None]
        col = hexrgb("#35d88a") * (1 - mix) + hexrgb("#8446c8") * mix
        return inten[..., None] * col

    def frame(self, p):
        s = n01(self.row(self.S, p, p % 1))
        e = smoothstep(0.25, 0.8, n01(self.row(self.E, p)))
        img = self.sky.copy()
        self.draw_stars(img, p, mask=self.mask_sky)
        img += self.curtain(0.42, 0.06, self.row(self.C1, p), s, e, 0.36)
        s2 = n01(self.row(self.S, p, 0.37 - 2 * (p % 1)))
        img += self.curtain(0.3, 0.08, self.row(self.C2, p), s2, 1 - e * 0.7, 0.2)
        for m, col in self.ridges:
            img = img * (1 - m) + col * m
        return img


class SnowfallPines(Theme):
    name, title, seconds = "snowfall-pines", "Snowfall in the pines", 30

    def setup(self):
        W, H, X, Y, s = self.W, self.H, self.X, self.Y, self.s
        r = self.rng
        sky = ramp(Y[:, 0], [(0, "#02040b"), (0.6, "#0b1527"), (1, "#111d33")])
        img = np.broadcast_to(sky[:, None, :], (H, W, 3)).copy()
        glow = np.exp(-(((X - 0.78) * self.a) ** 2 / 0.12 + (Y - 0.05) ** 2 / 0.08))
        img += (glow * 0.06)[..., None] * hexrgb("#8ea2c8")
        yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
        layers = ((0.62, 0.02, 70, (0.07, 0.13), "#0c1526", "#131d30"),
                  (0.73, 0.03, 40, (0.12, 0.2), "#070e1b", "#18223a"),
                  (0.86, 0.03, 20, (0.2, 0.34), "#03060d", "#1b2538"))
        for li, (base, amp, count, hr, col, snow) in enumerate(layers):
            line = self.ridge(base, amp, self.seed + 10 + li)
            ground = np.clip((yy - line[None, :] * H) + 0.5, 0, 1)
            gcol = hexrgb(snow) * (1 - smoothstep(0, 0.12, Y - line[None, :]))[..., None] * 0.35 + hexrgb(col) * 0.8
            img = img * (1 - ground[..., None]) + gcol * ground[..., None]
            mask = np.zeros((H, W), np.float32)
            for cx in np.sort(r.random(count)) * W:
                h = (hr[0] + (hr[1] - hr[0]) * r.random()) * H
                by = line[min(int(cx), W - 1)] * H + 2
                ty = by - h
                w = 0.3 * h
                x0, x1 = max(int(cx - w) - 1, 0), min(int(cx + w) + 2, W)
                y0, y1 = max(int(ty) - 1, 0), min(int(by) + 1, H)
                if x0 >= x1 or y0 >= y1:
                    continue
                ly = (yy[y0:y1, x0:x1] - ty) / h
                tier = (ly * 4) % 1
                half = w * ly * (0.62 + 0.38 * tier)
                dx = np.abs(xx[y0:y1, x0:x1] - cx)
                m = np.clip(half - dx + 0.5, 0, 1) * (ly >= 0)
                mask[y0:y1, x0:x1] = np.maximum(mask[y0:y1, x0:x1], m)
            img = img * (1 - mask[..., None]) + hexrgb(col) * mask[..., None]
        self.bg = img
        self.flakes = []
        for n, k, sg, b, kx in ((200, 1, 0.8, 0.22, 0), (120, 2, 1.3, 0.3, 0), (55, 3, 2.3, 0.3, 1)):
            self.flakes.append(dict(x0=r.random(n), y0=r.random(n), k=k, kx=kx, sg=sg * s,
                                    b=b * (0.6 + 0.4 * r.random(n)), wm=r.integers(1, 4, n),
                                    wa=0.004 + 0.012 * r.random(n), ph=r.random(n) * TAU))
        self.fcol = hexrgb("#c9d5ea")

    def frame(self, p):
        W, H = self.W, self.H
        img = self.bg.copy()
        for f in self.flakes:
            for i in range(len(f["x0"])):
                y = wrap(f["y0"][i] + f["k"] * p, 1, 0.02)
                x = (f["x0"][i] + f["kx"] * p + f["wa"][i] * np.sin(TAU * f["wm"][i] * p + f["ph"][i])) % 1
                splat(img, x * W, y * H, f["sg"], self.fcol, f["b"][i])
        return img


class SunkenCaustics(Theme):
    name, title, seconds = "sunken-caustics", "Sunken caustics", 24

    def setup(self):
        W, H, X, Y, a = self.W, self.H, self.X, self.Y, self.a
        r = self.rng
        water = ramp(Y[:, 0], [(0, "#0b2c3c"), (0.5, "#072030"), (1, "#03111a")])
        img = np.broadcast_to(water[:, None, :], (H, W, 3)).copy()
        line = self.ridge(0.66, 0.035, self.seed + 1)[None, :]
        self.floor = smoothstep(0, 0.04, Y - line)
        sand = n01(up(noise((180, 320), 2.4, self.seed + 2), W, H))
        scol = ramp(sand, [(0, "#0e1b1c"), (1, "#20302e")]) * (1 - 0.4 * smoothstep(0.66, 1, Y))[..., None]
        self.bg = img * (1 - self.floor[..., None]) + scol * self.floor[..., None]
        # caustics: the edges of a Voronoi net whose seeds circle in whole cycles
        gx, gy = 22, 22
        jj, ii = np.mgrid[0:gy, 0:gx]
        self.cs = np.stack([(ii.ravel() + 0.2 + 0.6 * r.random(gx * gy)) / gx * a,
                            (jj.ravel() + 0.2 + 0.6 * r.random(gx * gy)) / gy * 2.4], 1)
        self.cr = 0.3 * 2.4 / gy
        self.ck = r.choice([-2, -1, 1, 2], gx * gy)
        self.cph = r.random(gx * gy) * TAU
        self.cq = np.stack([(np.mgrid[0:180, 0:320][1].ravel() / 320) * a,
                            np.mgrid[0:180, 0:320][0].ravel() / 180 * 2.4], 1)
        self.V1 = noise((32, 90, 160), 3.2, self.seed + 3, tc=2)
        self.V2 = noise((32, 90, 160), 3.2, self.seed + 4, tc=2)
        self.cw = 0.2 * self.floor * (0.5 + 0.5 * Y)
        self.beams = [(0.15 + 0.7 * r.random(), 0.015 + 0.03 * r.random(), r.random() * TAU) for _ in range(5)]
        n = 30
        self.bub = dict(x0=r.random(n), y0=r.random(n), k=r.integers(2, 4, n), wm=r.integers(2, 6, n),
                        ph=r.random(n) * TAU, sg=(0.9 + 1.2 * r.random(n)) * self.s, b=0.06 + 0.1 * r.random(n))
        n = 140
        self.snow = dict(x0=r.random(n), y0=r.random(n), wm=r.integers(1, 3, n), ph=r.random(n) * TAU,
                         sg=(0.5 + 0.6 * r.random(n)) * self.s, b=0.03 + 0.06 * r.random(n))

    def frame(self, p):
        W, H = self.W, self.H
        ang = TAU * self.ck * p + self.cph
        seeds = self.cs + self.cr * np.stack([np.cos(ang), np.sin(ang)], 1)
        w1 = up(at(self.V1, p), 320, 180).ravel()
        w2 = up(at(self.V2, p), 320, 180).ravel()
        q = self.cq + 0.02 * np.stack([w1, w2], 1)
        dist, _ = cKDTree(seeds).query(q, k=2)
        edge = (dist[:, 1] - dist[:, 0]).reshape(180, 320)
        caus = np.clip(up(np.exp(-(edge / 0.012) ** 1.5) * (0.35 + 0.65 * n01(w1.reshape(180, 320))), W, H), 0, 1)
        img = self.bg + (caus * self.cw)[..., None] * hexrgb("#78d4cc")
        for x0, w, ph in self.beams:
            xb = x0 + 0.03 * np.sin(TAU * p + ph)
            u = self.X - xb - 0.22 * self.Y
            k = np.exp(-(u / w) ** 2) * (1 - self.Y) ** 1.6 * 0.065 * (0.8 + 0.2 * np.sin(TAU * 2 * p + ph))
            img += k[..., None] * hexrgb("#8fd0e0")
        c = hexrgb("#bfe8f0")
        b = self.bub
        for i in range(len(b["x0"])):
            y = wrap(b["y0"][i] - b["k"][i] * p, 1, 0.02)
            x = (b["x0"][i] + 0.008 * np.sin(TAU * b["wm"][i] * p + b["ph"][i])) % 1
            splat(img, x * W, y * H, b["sg"][i], c, b["b"][i])
        sn = self.snow
        for i in range(len(sn["x0"])):
            y = wrap(sn["y0"][i] + p, 1, 0.02)
            x = (sn["x0"][i] + 0.01 * np.sin(TAU * sn["wm"][i] * p + sn["ph"][i])) % 1
            splat(img, x * W, y * H, sn["sg"][i], c, sn["b"][i])
        return img


class TwinMoons(Theme):
    name, title, seconds = "twin-moons", "Twin moons", 30

    def setup(self):
        W, H, X, Y = self.W, self.H, self.X, self.Y
        sky = ramp(Y[:, 0], [(0, "#010209"), (0.7, "#08101f"), (1, "#0c1528")])
        img = np.broadcast_to(sky[:, None, :], (H, W, 3)).copy()
        self.stars(300, 0.9, (0.06, 0.45))
        yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
        mott = n01(up(noise((90, 160), 2.2, self.seed + 1), W, H))
        self.light = np.zeros((H, W, 3), np.float32)
        for cx, cy, R, col, pk in ((0.7, 0.24, 0.055, "#b4bece", 0.62), (0.29, 0.34, 0.04, "#c47c60", 0.5)):
            cx, cy, R = cx * W, cy * H, R * H
            d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
            limb = np.sqrt(np.clip(1 - (d / R) ** 2, 0, 1))
            disc = np.clip(R - d + 0.5, 0, 1) * (0.72 + 0.28 * limb) * (0.85 + 0.15 * mott)
            halo = np.exp(-np.maximum(d - R, 0) / (2.2 * R)) * 0.12 * (d > R - 1)
            c = hexrgb(col)
            img = img + ((disc * pk + halo)[..., None] * c)
            self.light += (np.exp(-d / (7 * R)) * 0.55)[..., None] * c
        self.sky = img
        self.V1 = noise((40, 90, 160), 3.6, self.seed + 2, tc=2)
        self.V2 = noise((40, 180, 320), 2.4, self.seed + 3, tc=2)
        self.cbase = hexrgb("#121a29")

    def frame(self, p):
        W, H = self.W, self.H
        img = self.sky.copy()
        self.draw_stars(img, p)
        f = 0.9 * drift(at(self.V1, p), 0, (p % 1) * 160) + 0.2 * up(drift(at(self.V2, p), 0, (p % 1) * 320), 160, 90)
        n = n01(up(f, W, H) / 0.9)
        dens = smoothstep(0.42, 0.85, n)[..., None]
        thick = smoothstep(0.5, 0.97, n)[..., None]
        ccol = self.cbase + self.light * (1 - thick) * 1.1
        return img * (1 - 0.93 * dens) + ccol * dens


THEMES = [MoongateShimmer, CandleParchment, StarlitSea, DriftingFog, EmberDrift,
          RainOnStone, AuroraNight, SnowfallPines, SunkenCaustics, TwinMoons]


def theme_set(name):
    """(themes, default output folder, manifest file, report folder) for one --set."""
    if name == "builtin":
        return THEMES, DEFAULT_OUT, "backgrounds.json", REPORT_DIR
    if name == "screensavers":
        from screensavers import SCREENSAVERS
        return SCREENSAVERS, ROOT / "godot" / "GUO" / "assets" / "screensavers", "screensavers.json", REPORT_DIR / "screensavers"
    if name == "store":
        # Store-only loops: packed by tools/asset_store/seed.py, not shipped in the client.
        from store_loops import STORE_LOOPS
        return STORE_LOOPS, REPORT_DIR / "store", "store_loops.json", REPORT_DIR / "store_report"
    raise SystemExit(f"unknown set {name}")


# ---------------------------------------------------------------- render

def to8(img, dither):
    return np.clip(np.rint(img * 255 + dither), 0, 255).astype(np.uint8)


def render(cls, W, H, out, q, seed, report_dir=REPORT_DIR):
    t0 = time.time()
    th = cls(W, H, seed)
    N = th.seconds * FPS
    dither = (np.random.default_rng(seed + 99).random((H, W, 1))
              - np.random.default_rng(seed + 98).random((H, W, 1))).astype(np.float32)
    ogv = out / f"{th.name}.ogv"
    cmd = [shutil.which("ffmpeg"), "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
           "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-", "-an", "-c:v", "libtheora",
           "-q:v", str(q), "-g", str(FPS * 4), "-pix_fmt", "yuv420p", str(ogv)]
    ff = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    thumbs = []
    first = None
    lit = None          # per pixel, the dimmest it gets over the loop: what an OLED would burn in
    luma = 0.0
    for i in range(N):
        fr = to8(th.frame(i / N), dither)
        lit = fr.max(2) if lit is None else np.minimum(lit, fr.max(2))
        luma += float(fr.mean()) / N
        if i == 0:
            first = fr
            Image.fromarray(fr).save(out / f"{th.name}.png", optimize=True)
        if i % (N // 4) == 0:
            thumbs.append(Image.fromarray(fr).resize((320, 180), Image.BILINEAR))
        ff.stdin.write(fr.tobytes())
    ff.stdin.close()
    if ff.wait() != 0:
        raise RuntimeError(f"ffmpeg failed for {th.name}")
    seam = int(np.abs(to8(th.frame(1.0), dither).astype(int) - first.astype(int)).max())
    step = float(np.abs(to8(th.frame(1 / N), dither).astype(int) - first.astype(int)).mean())
    sheet = Image.new("RGB", (320 * 4, 180))
    for j, t in enumerate(thumbs[:4]):
        sheet.paste(t, (320 * j, 0))
    report_dir.mkdir(parents=True, exist_ok=True)
    sheet.save(report_dir / f"sheet_{th.name}.png")
    return dict(name=th.name, title=th.title, seconds=th.seconds, frames=N,
                bytes=ogv.stat().st_size, mbit=ogv.stat().st_size * 8 / th.seconds / 1e6,
                seam_max=seam, step_mean=round(step, 3), mean_level=round(luma, 2),
                static_max=int(lit.max()), secs=round(time.time() - t0, 1))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--only", nargs="*", help="theme names to render (default: all)")
    ap.add_argument("--size", default="1280x720")
    ap.add_argument("--set", default="builtin", choices=["builtin", "screensavers", "store"])
    ap.add_argument("--out", type=Path, help="output folder (default: the set's own)")
    ap.add_argument("--q", type=int, default=8, help="Theora quality 0-10")
    ap.add_argument("--jobs", type=int, default=5)
    ap.add_argument("--seed", type=int, default=2026)
    args = ap.parse_args()
    if not shutil.which("ffmpeg"):
        sys.exit("ffmpeg (with libtheora) is not on PATH")
    W, H = map(int, args.size.lower().split("x"))
    themes, default_out, manifest_name, report_dir = theme_set(args.set)
    args.out = args.out or default_out
    args.out.mkdir(parents=True, exist_ok=True)
    todo = [c for c in themes if not args.only or c.name in args.only]
    with ProcessPoolExecutor(args.jobs) as ex:
        futs = [ex.submit(render, c, W, H, args.out, args.q, args.seed + 1000 * i, report_dir)
                for i, c in enumerate(themes) if c in todo]
        stats = [f.result() for f in futs]
    manifest = [dict(name=c.name, title=c.title, video=f"{c.name}.ogv", still=f"{c.name}.png",
                     **({"store_only": True} if getattr(c, "store_only", False) else {}))
                for c in themes if (args.out / f"{c.name}.ogv").exists()]
    (args.out / manifest_name).write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    total = sum((args.out / f"{m['name']}.ogv").stat().st_size for m in manifest)
    for s in stats:
        print(f"{s['name']:18} {s['seconds']:3}s {s['bytes'] / 1e6:5.2f} MB {s['mbit']:4.2f} Mbit/s "
              f"seam {s['seam_max']} step {s['step_mean']} mean {s['mean_level']} static {s['static_max']} ({s['secs']} s)")
    print(f"total {total / 1e6:.2f} MB over {len(manifest)} videos -> {args.out}")
    (report_dir / "report.json").write_text(json.dumps(stats, indent=2), encoding="utf-8")
    sheets = [report_dir / f"sheet_{c.name}.png" for c in themes if (report_dir / f"sheet_{c.name}.png").exists()]
    if sheets:
        big = Image.new("RGB", (1280, 180 * len(sheets)))
        for i, sp in enumerate(sheets):
            big.paste(Image.open(sp), (0, 180 * i))
        big.save(report_dir / "contact_sheet.png")


if __name__ == "__main__":
    main()
