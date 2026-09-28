"""OLED screensaver loops: original procedural art, CC0 (rendered by run.py --set screensavers).

The screen is mostly true black, and nothing stays lit in one place: each
theme drifts its whole frame on a slow Lissajous path, or keeps every lit
element moving. run.py measures this. `static_max` in the report is the
brightest level any pixel holds for the whole loop. Like the backgrounds,
every theme is a pure function of the loop phase p, so the files loop with
no seam.
"""
import numpy as np
from scipy import ndimage
from scipy.spatial import cKDTree

from run import TAU, Theme, hexrgb, noise, n01, ramp, smoothstep, splat, up, wrap


def splat_ellipse(img, x, y, sa, sb, ang, col, amp):
    """A soft rotated elliptical dot: sa and sb are its two sigmas in pixels."""
    H, W = img.shape[:2]
    r = int(max(sa, sb) * 3 + 1)
    xi, yi = int(np.floor(x)), int(np.floor(y))
    x0, x1 = max(xi - r, 0), min(xi + r + 2, W)
    y0, y1 = max(yi - r, 0), min(yi + r + 2, H)
    if x0 >= x1 or y0 >= y1:
        return
    dx = np.arange(x0, x1)[None, :] - x
    dy = np.arange(y0, y1)[:, None] - y
    c, s = np.cos(ang), np.sin(ang)
    u, v = (dx * c + dy * s) / sa, (-dx * s + dy * c) / sb
    img[y0:y1, x0:x1] += (amp * np.exp(-0.5 * (u * u + v * v)))[..., None] * col


class Screensaver(Theme):
    """A theme whose whole frame drifts on a slow Lissajous path (whole cycles per loop)."""
    seconds = 32
    drift_px = (14, 9)      # amplitude in 720p pixels
    drift_k = (1, 2)

    def offset(self, p):
        ax, ay = self.drift_px
        kx, ky = self.drift_k
        return ax * self.s * np.sin(TAU * kx * p), ay * self.s * np.sin(TAU * ky * p + 0.7)


class MoongateEmbers(Screensaver):
    name, title, seconds = "moongate-embers", "Moongate embers", 36

    def setup(self):
        self.hw, self.hh = self.W // 2, self.H // 2
        self.gx = np.arange(self.hw, dtype=np.float32)[None, :] / self.hh
        self.gy = np.arange(self.hh, dtype=np.float32)[:, None] / self.hh
        self.T1 = n01(noise((256, 128), 3.0, self.seed + 1))
        self.T2 = n01(noise((256, 128), 2.4, self.seed + 2))
        r = self.rng
        n = 150
        self.e = dict(k=r.integers(2, 5, n), ph=r.random(n), th=r.random(n) * TAU,
                      vx=(r.random(n) - 0.5) * 0.05, vy=-(0.05 + 0.1 * r.random(n)),
                      wm=r.integers(1, 3, n), wa=0.004 + 0.01 * r.random(n),
                      sg=(0.7 + 1.0 * r.random(n)) * self.s, b=0.25 + 0.5 * r.random(n),
                      c=hexrgb("#6f9cff") + (hexrgb("#c6a2ff") - hexrgb("#6f9cff")) * r.random(n)[:, None])

    def centre(self, p):
        """The gate wanders the screen on its own Lissajous path (in units of H)."""
        return (0.5 * self.a + 0.36 * self.a * np.sin(TAU * p + 0.3) * 0.72,
                0.5 + 0.2 * np.sin(TAU * 2 * p + 1.1))

    def frame(self, p):
        cx, cy = self.centre(p)
        rx, ry = 0.085, 0.165
        ex, ey = (self.gx - cx) / rx, (self.gy - cy) / ry
        ell = np.sqrt(ex * ex + ey * ey)
        th = (np.arctan2(ey, ex) / TAU) % 1.0
        c1 = [(th + p + ell * 0.16) * 256, (ell * 0.5 - p) * 128]
        c2 = [(th - 2 * p - ell * 0.1) * 256, (ell * 0.8 - 2 * p) * 128]
        s1 = ndimage.map_coordinates(self.T1, c1, order=1, mode="grid-wrap")
        s2 = ndimage.map_coordinates(self.T2, c2, order=1, mode="grid-wrap")
        sw = 0.6 * s1 + 0.4 * s2
        inside = smoothstep(1.02, 0.78, ell)
        rim = np.exp(-((ell - 1) / 0.06) ** 2)
        halo = np.exp(-np.abs(ell - 1) * 4) * 0.08
        inten = (inside * (0.08 + 0.3 * sw ** 1.8) + rim * (0.32 + 0.2 * sw) + halo) * (1 + 0.08 * np.sin(TAU * 3 * p))
        col = ramp(sw, [(0, "#16307a"), (0.5, "#3478c8"), (0.8, "#7070e0"), (1, "#a080e8")])
        img = up(col * (0.7 * inten)[..., None], self.W, self.H)
        # embers: born on the rim where the gate stood at their birth, then rise, sway and fade
        e = self.e
        for i in range(len(e["k"])):
            t = e["k"][i] * p + e["ph"][i]
            age = t - np.floor(t)
            pb = (np.floor(t) - e["ph"][i]) / e["k"][i]
            bx, by = self.centre(pb)
            x = bx + rx * np.cos(e["th"][i]) + e["vx"][i] * age + e["wa"][i] * np.sin(TAU * e["wm"][i] * age * 3)
            y = by + ry * np.sin(e["th"][i]) + e["vy"][i] * age * 2.2
            b = e["b"][i] * np.sin(np.pi * age) ** 1.5
            splat(img, x * self.H, y * self.H, e["sg"][i], e["c"][i], b)
            splat(img, x * self.H, y * self.H, e["sg"][i] * 3.5, e["c"][i], b * 0.05)
        return img


class DriftingReagents(Screensaver):
    name, title, seconds = "drifting-reagents", "Drifting reagents", 32
    store_only = True       # published to the Store by tools/asset_store/seed.py, not built in

    def setup(self):
        r = self.rng
        self.parts = []
        # (kind, count, speed wraps per loop, size, brightness)
        for kind, n, k, sz, b in (("ash", 60, 1, 1.0, 0.2), ("petal", 26, 1, 4.2, 0.42),
                                  ("pearl", 18, 1, 1.5, 0.5), ("petal", 10, 2, 6.5, 0.5), ("ash", 30, 2, 1.8, 0.26)):
            self.parts.append(dict(kind=kind, x0=r.random(n), y0=r.random(n), k=k, kx=r.choice([0, 1], n),
                                   sz=sz * self.s * (0.75 + 0.5 * r.random(n)), b=b * (0.6 + 0.4 * r.random(n)),
                                   wm=r.integers(1, 4, n), wa=0.008 + 0.02 * r.random(n), ph=r.random(n) * TAU,
                                   rk=r.choice([-3, -2, -1, 1, 2, 3], n), tk=r.integers(3, 9, n)))
        self.col = dict(ash=hexrgb("#8a8580"), petal=hexrgb("#c23a4a"), pearl=hexrgb("#e6ecf6"))

    def frame(self, p):
        W, H = self.W, self.H
        img = np.zeros((H, W, 3), np.float32)
        for f in self.parts:
            col = self.col[f["kind"]]
            for i in range(len(f["x0"])):
                y = wrap(f["y0"][i] + f["k"] * p, 1, 0.04)
                x = (f["x0"][i] + f["kx"][i] * p + f["wa"][i] * np.sin(TAU * f["wm"][i] * p + f["ph"][i])) % 1
                X, Y = x * W, y * H
                if f["kind"] == "petal":
                    ang = TAU * f["rk"][i] * p + f["ph"][i]
                    flip = 0.25 + 0.75 * np.abs(np.sin(TAU * f["tk"][i] * p + f["ph"][i]))
                    splat_ellipse(img, X, Y, f["sz"][i], f["sz"][i] * 0.6 * flip, ang, col, f["b"][i])
                elif f["kind"] == "pearl":
                    tw = 0.7 + 0.3 * np.sin(TAU * f["tk"][i] * p + f["ph"][i])
                    splat(img, X, Y, f["sz"][i], col, f["b"][i] * tw)
                    splat(img, X, Y, f["sz"][i] * 4, hexrgb("#8fa8d8"), f["b"][i] * 0.05 * tw)
                else:
                    ang = TAU * f["rk"][i] * p + f["ph"][i]
                    splat_ellipse(img, X, Y, f["sz"][i], f["sz"][i] * 0.6, ang, col, f["b"][i])
        return img


class SpineStarfield(Screensaver):
    name, title, seconds = "spine-starfield", "Stars over the Spine", 40
    drift_px = (18, 8)

    def setup(self):
        W, H, X, Y = self.W, self.H, self.X, self.Y
        r = self.rng
        m = int(28 * self.s)       # margin so the drift never shows an edge
        self.m = m
        Wm, Hm = W + 2 * m, H + 2 * m
        self.Wm, self.Hm = Wm, Hm
        ys = np.arange(Hm, dtype=np.float32)[:, None] / H
        xs = np.arange(Wm, dtype=np.float32)[None, :] / W
        # the Spine: two ranges of peaks, black cut-outs against a faint horizon glow, a moonlit crest
        far = 0.72 - self._peaks(Wm, r, 16, 0.03, 0.11) + 0.015 * self._ridge(Wm, self.seed + 1, 2.1)
        near = 0.84 - self._peaks(Wm, r, 11, 0.05, 0.17) + 0.02 * self._ridge(Wm, self.seed + 2, 2.1)
        mf = np.clip((ys - far[None, :]) * H + 0.5, 0, 1)
        mn = np.clip((ys - near[None, :]) * H + 0.5, 0, 1)
        # moonlight catches a thin line along each crest: the mask minus itself moved down a little
        grain = 0.4 + 0.6 * n01(up(noise((40, 160), 2.0, self.seed + 4), Wm, Hm))
        rim_n = np.clip(mn - ndimage.shift(mn, (2.0 * self.s, 0), order=1, mode="nearest"), 0, 1) * grain
        rim_f = np.clip(mf - ndimage.shift(mf, (1.5 * self.s, 0), order=1, mode="nearest"), 0, 1) * grain * (1 - mn)
        band = n01(up(noise((90, 160), 2.4, self.seed + 3), Wm, Hm))
        mw = np.exp(-(((ys - 0.05 - 0.45 * xs) / 0.16) ** 2)) * smoothstep(0.5, 0.95, band)
        glow = np.exp(-((ys - 0.74) / 0.2) ** 2) * 0.05
        sky = (mw * 0.03)[..., None] * hexrgb("#8a98c8") + glow[..., None] * hexrgb("#34466e")
        self.sky_mask = 1 - mf
        # the glow and the far range's haze breathe once per loop, so no pixel keeps a fixed light
        self.skyglow = sky * (1 - mf)[..., None] + (mf * (1 - mn) * 0.012)[..., None] * hexrgb("#34466e")
        img = (rim_n * 0.085 + rim_f * 0.04)[..., None] * hexrgb("#9aaed0")
        self.bg = img
        n = 420
        self.st = dict(x=r.random(n) * Wm, y=r.random(n) ** 1.2 * 0.82 * Hm, b=0.06 + 0.5 * r.random(n) ** 3,
                       sg=(0.5 + 0.5 * r.random(n)) * self.s, k=r.integers(1, 7, n), ph=r.random(n) * TAU,
                       c=np.stack([hexrgb("#c8d6ff"), hexrgb("#fff2dc"), hexrgb("#ffd6c0")])[r.integers(0, 3, n)])
        # meteors: a few per loop, each a short streak at its own time
        self.met = [(r.random(), 0.1 + 0.7 * r.random(), 0.05 + 0.25 * r.random(), 0.3 + 0.5 * r.random())
                    for _ in range(3)]

    def _peaks(self, n, r, count, hmin, hmax):
        """A range of sharp peaks: the upper envelope of tents with rough flanks (in units of H)."""
        x = np.arange(n) / self.H
        out = np.full(n, 0.01)
        for c in (np.arange(count) + r.random(count)) / count * (n / self.H):
            h = hmin + (hmax - hmin) * r.random() ** 1.5
            sl, sr = 0.35 + 0.9 * r.random(), 0.35 + 0.9 * r.random()      # uneven flanks
            d = x - c
            out = np.maximum(out, h - np.where(d < 0, sl, sr) * np.abs(d) ** (0.8 + 0.4 * r.random()))
        return out

    def _ridge(self, n, seed, beta):
        line = noise((n,), beta, seed)
        return line / np.abs(line).max()

    def frame(self, p):
        W, H, m = self.W, self.H, self.m
        img = self.bg + self.skyglow * (0.55 + 0.45 * np.cos(TAU * p))
        st = self.st
        tw = 0.65 + 0.35 * np.sin(TAU * st["k"] * p + st["ph"])
        for i in range(len(st["x"])):
            yi, xi = min(int(st["y"][i]), self.Hm - 1), min(int(st["x"][i]), self.Wm - 1)
            b = st["b"][i] * tw[i] * self.sky_mask[yi, xi]
            if b > 0.004:
                splat(img, st["x"][i], st["y"][i], st["sg"][i], st["c"][i], b)
        for t0, x0, y0, ang in self.met:
            age = ((p - t0) % 1) * self.seconds
            if age < 0.9:
                L = 160 * self.s
                for j in np.linspace(0, 1, 28):
                    d = (age / 0.9) * L - j * 40 * self.s
                    if d < 0:
                        continue
                    x = x0 * self.Wm + d * np.cos(ang)
                    y = y0 * self.Hm + d * np.sin(ang)
                    splat(img, x, y, 0.8 * self.s, hexrgb("#dfe6ff"), 0.35 * (1 - j) * np.sin(np.pi * age / 0.9))
        ox, oy = self.offset(p)
        out = ndimage.shift(img, (oy, ox, 0), order=1, mode="nearest")
        return out[m:m + H, m:m + W]


class SigilTrace(Screensaver):
    name, title, seconds = "sigil-trace", "Gold sigil trace", 36
    drift_px = (60, 30)

    def setup(self):
        s = self.s
        self.B = B = int(420 * s)           # the sigil's box, rendered alone and then placed
        c = B / 2
        R = 0.4 * B
        pts, sp = [], []
        def seg(path):
            pts.append(np.asarray(path, np.float32))
        th = np.linspace(0, TAU, 900, endpoint=False)
        seg(np.stack([c + R * np.cos(th - np.pi / 2), c + R * np.sin(th - np.pi / 2)], 1))
        # an original rune: a seven-pointed star inscribed in the circle, a smaller ring, a vertical stave
        k = np.arange(8) * 3 % 7
        star = np.stack([c + 0.93 * R * np.cos(TAU * k / 7 - np.pi / 2), c + 0.93 * R * np.sin(TAU * k / 7 - np.pi / 2)], 1)
        seg(np.concatenate([np.linspace(star[i], star[i + 1], 140) for i in range(7)]))
        th2 = np.linspace(0, TAU, 420, endpoint=False)
        seg(np.stack([c + 0.36 * R * np.cos(th2 + np.pi / 2), c + 0.36 * R * np.sin(th2 + np.pi / 2)], 1))
        seg(np.linspace((c, c - 0.36 * R), (c, c - 1.12 * R), 120))
        seg(np.linspace((c, c + 0.36 * R), (c, c + 1.12 * R), 120))
        path = np.concatenate(pts)
        L = np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(path, axis=0), axis=1))])
        # jumps between segments cost no time: the pen lifts
        jumps = np.linalg.norm(np.diff(path, axis=0), axis=1) > 3 * s
        L = np.concatenate([[0], np.cumsum(np.where(jumps, 0, np.linalg.norm(np.diff(path, axis=0), axis=1)))])
        sparam = L / L[-1] * 0.8          # the pen draws for 80% of the loop and rests for 20%
        yy, xx = np.mgrid[0:B, 0:B].astype(np.float32)
        tree = cKDTree(path)
        d, idx = tree.query(np.stack([xx.ravel(), yy.ravel()], 1), k=1)
        self.d = d.reshape(B, B).astype(np.float32)
        self.sp = sparam[idx].reshape(B, B).astype(np.float32)
        self.path, self.pparam = path, sparam
        self.core = np.exp(-(self.d / (1.1 * s)) ** 2)
        self.glow = np.exp(-self.d / (7 * s))
        r = self.rng
        n = 40
        self.mote = dict(x0=r.random(n), y0=r.random(n), k=r.integers(1, 3, n), wm=r.integers(1, 3, n),
                         ph=r.random(n) * TAU, sg=(0.6 + 0.6 * r.random(n)) * s, b=0.05 + 0.1 * r.random(n))
        self.gold = hexrgb("#e0a848")
        self.pale = hexrgb("#ffe0a0")

    def frame(self, p):
        W, H, B, s = self.W, self.H, self.B, self.s
        # age since the pen passed each point, over one loop: fresh ink glows, then fades to a faint ember
        age = (p - self.sp) % 1.0
        # the pen's own sweep: while the pen rests, the whole figure breathes once and dims
        drawn = np.exp(-age / 0.2)
        fade = 0.12 + 0.88 * drawn
        inten = (self.core * 0.55 + self.glow * 0.12) * fade
        box = inten[..., None] * self.gold + (self.core * np.exp(-age / 0.03) * 0.5)[..., None] * self.pale
        if p % 1.0 < 0.8:
            j = np.searchsorted(self.pparam, p % 1.0)
            hx, hy = self.path[min(j, len(self.path) - 1)]
            splat(box, hx, hy, 2.2 * s, self.pale, 0.6)
            splat(box, hx, hy, 9 * s, self.gold, 0.1)
        ox, oy = self.offset(p)
        img = np.zeros((H, W, 3), np.float32)
        cx, cy = W / 2 + ox, H / 2 + oy
        x0, y0 = int(np.floor(cx - B / 2)), int(np.floor(cy - B / 2))
        fx, fy = cx - B / 2 - x0, cy - B / 2 - y0
        box = ndimage.shift(box, (fy, fx, 0), order=1, mode="constant")
        img[y0:y0 + B, x0:x0 + B] = box[:H - y0, :W - x0]
        mo = self.mote
        for i in range(len(mo["x0"])):
            y = wrap(mo["y0"][i] - mo["k"][i] * p, 1, 0.02)
            x = (mo["x0"][i] + 0.01 * np.sin(TAU * mo["wm"][i] * p + mo["ph"][i])) % 1
            splat(img, x * W, y * H, mo["sg"][i], self.gold, mo["b"][i])
        return img


SCREENSAVERS = [MoongateEmbers, DriftingReagents, SpineStarfield, SigilTrace]
