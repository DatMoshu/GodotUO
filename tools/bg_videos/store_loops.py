"""Store background loops: six more original procedural scenes, CC0 (rendered by run.py --set store).

These are not built into the client. tools/asset_store/seed.py packs each one
as a `background` pack for the Asset Store. Like the built-in backgrounds,
every theme is a pure function of the loop phase p. Particles wrap a whole
number of times, oscillators run whole cycles, and noise evolves on a
periodic time axis, so the files loop with no seam. The scenes are calm and
lean dark, so the login and character screens stay readable over them.
"""
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage
from scipy.spatial import cKDTree

from run import TAU, Theme, at, hexrgb, noise, n01, periodic_signal, ramp, smoothstep, splat, up, wrap


# ---------------------------------------------------------------- helpers

def poly_mask(W, H, polys, ss=4):
    """Anti-aliased coverage (H, W) of polygons [[(x, y), ...], ...] in pixels."""
    im = Image.new("L", (W * ss, H * ss), 0)
    d = ImageDraw.Draw(im)
    for pts in polys:
        d.polygon([(x * ss, y * ss) for x, y in pts], fill=255)
    return np.asarray(im.resize((W, H), Image.BOX), np.float32) / 255


def seg(img, x0, y0, x1, y1, w, col, amp, tail=0.0):
    """Add a soft line segment. w is its sigma in pixels. With tail > 0 it
    fades from full at (x1, y1) to 1 - tail at (x0, y0), like a streak.
    img is (H, W, 3), or (H, W) with col ignored."""
    H, W = img.shape[:2]
    r = w * 3 + 1
    xa, xb = int(max(min(x0, x1) - r, 0)), int(min(max(x0, x1) + r + 1, W))
    ya, yb = int(max(min(y0, y1) - r, 0)), int(min(max(y0, y1) + r + 1, H))
    if xa >= xb or ya >= yb:
        return
    px = np.arange(xa, xb, dtype=np.float32)[None, :]
    py = np.arange(ya, yb, dtype=np.float32)[:, None]
    dx, dy = x1 - x0, y1 - y0
    t = np.clip(((px - x0) * dx + (py - y0) * dy) / (dx * dx + dy * dy + 1e-9), 0, 1)
    d2 = (px - x0 - t * dx) ** 2 + (py - y0 - t * dy) ** 2
    v = amp * np.exp(-d2 / (2 * w * w)) * (1 - tail * (1 - t))
    if img.ndim == 2:
        img[ya:yb, xa:xb] += v
    else:
        img[ya:yb, xa:xb] += v[..., None] * col


def pool(X, Y, cx, cy, r, squash=1.0):
    """A soft light falloff 1 / (1 + d^2 / r^2), flattened vertically by squash."""
    return 1 / (1 + ((X - cx) ** 2 + ((Y - cy) * squash) ** 2) / (r * r))


def over(img, mask, col):
    """Composite a flat colour (or an (H, W, 3) image) over img by coverage mask."""
    m = mask[..., None]
    return img * (1 - m) + m * col


def vignette(X, Y, a, k=0.35):
    return (1 - k * smoothstep(0.35, 1.05, np.sqrt(((X - 0.5) * a) ** 2 + (Y - 0.5) ** 2) / 0.9))[..., None]


def roll_rows(img, shift):
    """Shift each row of img horizontally by shift[y] pixels (sub-pixel, wrapping)."""
    H, W = img.shape[:2]
    xs = (np.arange(W, dtype=np.float32)[None, :] - shift[:, None]) % W
    x0 = np.floor(xs).astype(np.int32)
    t = (xs - x0)[..., None]
    x0 %= W     # float rounding can land xs on W itself
    rows = np.arange(H)[:, None]
    return img[rows, x0] * (1 - t) + img[rows, (x0 + 1) % W] * t


# ---------------------------------------------------------------- themes

class BritainLanterns(Theme):
    """A town square at night: timbered houses, a castle keep beyond, and
    four lanterns on posts that sway a little and flicker, their light
    pooling on the plaster and the cobbles. Moths circle them."""
    name, title, seconds = "britain-lanterns", "Britain lanterns", 28

    def setup(self):
        W, H, s, r = self.W, self.H, self.s, self.rng
        X = np.arange(W, dtype=np.float32)[None, :]
        Y = np.arange(H, dtype=np.float32)[:, None]
        self.px, self.py = X, Y
        self.sky = np.broadcast_to(ramp(np.clip(self.Y / 0.6, 0, 1), [(0, "#04060d"), (0.65, "#0b0f22"), (1, "#1a1a2e")]), (H, W, 3)).copy()
        glow = np.exp(-((self.X - 0.3) * self.a) ** 2 / 0.4 - (self.Y - 0.55) ** 2 / 0.02)
        self.sky += (0.05 * glow)[..., None] * hexrgb("#6a5a7a")
        self.stars(160, 0.42, (0.08, 0.5))

        # far: a keep with battlements and a row of roofs
        far = []
        kx0, kx1, ktop = 0.07 * W, 0.19 * W, 0.24 * H
        far.append([(kx0, H), (kx0, ktop), (kx1, ktop), (kx1, H)])
        n = 7
        for i in range(n):
            bx = kx0 + (kx1 - kx0) * i / (n - 1) - 6 * s
            far.append([(bx, ktop), (bx, ktop - 12 * s), (bx + 12 * s, ktop - 12 * s), (bx + 12 * s, ktop)])
        tx = 0.215 * W
        far.append([(tx - 16 * s, H), (tx - 16 * s, 0.3 * H), (tx, 0.21 * H), (tx + 16 * s, 0.3 * H), (tx + 16 * s, H)])
        self.far_windows = [(kx0 + 30 * s, 0.33 * H), (kx1 - 44 * s, 0.4 * H), (tx - 3 * s, 0.34 * H)]
        x = 0.24 * W
        while x < W + 40 * s:
            w = (60 + 80 * r.random()) * s
            wall = H * (0.41 + 0.05 * r.random())
            peak = wall - (26 + 34 * r.random()) * s
            far.append([(x, H), (x, wall), (x + w / 2, peak), (x + w, wall), (x + w, H)])
            if r.random() < 0.6:
                cx = x + w * (0.2 + 0.5 * r.random())
                far.append([(cx, wall), (cx, peak + 4 * s), (cx + 9 * s, peak + 4 * s), (cx + 9 * s, wall)])
            if r.random() < 0.5:
                self.far_windows.append((x + w * (0.3 + 0.4 * r.random()), wall + 12 * s))
            x += w * (0.8 + 0.25 * r.random())
        self.far = poly_mask(W, H, far)

        # near: gabled timber houses across the square
        self.base = 0.8 * H
        near, beams, windows, doors = [], [], [], []
        x = -40 * s
        while x < W + 40 * s:
            w = (170 + 90 * r.random()) * s
            wall = H * (0.53 + 0.05 * r.random())
            peak = wall - (45 + 30 * r.random()) * s
            near.append([(x, self.base), (x, wall), (x + w / 2, peak), (x + w, wall), (x + w, self.base)])
            bw = 7 * s
            for fx in (0.02, 0.5, 0.98):
                cx = x + w * fx
                beams.append([(cx - bw / 2, wall), (cx + bw / 2, wall), (cx + bw / 2, self.base), (cx - bw / 2, self.base)])
            for fy in (0.0, 0.5):
                y = wall + (self.base - wall) * fy
                beams.append([(x, y - bw / 2), (x + w, y - bw / 2), (x + w, y + bw / 2), (x, y + bw / 2)])
            beams.append([(x + w * 0.02, wall), (x + w * 0.5, peak + 3 * s), (x + w * 0.5, peak + 3 * s + bw), (x + w * 0.02, wall + bw)])
            beams.append([(x + w * 0.98, wall), (x + w * 0.5, peak + 3 * s), (x + w * 0.5, peak + 3 * s + bw), (x + w * 0.98, wall + bw)])
            mid = wall + (self.base - wall) * 0.5
            for fx in (0.22, 0.72):
                if r.random() < 0.8:
                    windows.append((x + w * fx, wall + (mid - wall) * 0.5, r.random() < 0.75))
            if r.random() < 0.7:
                windows.append((x + w * 0.5, (wall + peak) / 2 + 8 * s, r.random() < 0.5))
            dx = x + w * (0.38 + 0.2 * r.random())
            doors.append([(dx, self.base), (dx, mid + 18 * s), (dx + 30 * s, mid + 18 * s), (dx + 30 * s, self.base)])
            x += w
        self.near = poly_mask(W, H, near)
        self.beams = poly_mask(W, H, beams) * self.near
        self.doors = poly_mask(W, H, doors) * self.near
        plaster = n01(up(noise((90, 160), 2.2, self.seed + 1), W, H))
        alb = ramp(0.5 + 0.5 * plaster, [(0, "#4a4038"), (1, "#8a7862")])
        alb = alb * (1 - 0.8 * self.beams)[..., None] + (0.8 * self.beams)[..., None] * hexrgb("#24180f")
        alb = alb * (1 - 0.85 * self.doors)[..., None]
        self.alb_near = alb

        # the cobbles: voronoi cells, flattened toward the far edge
        pts = []
        y = self.base
        while y < H + 30 * s:
            h = (8 + 26 * (y - self.base) / (H - self.base)) * s
            for i in range(int(W / (h * 1.6)) + 2):
                pts.append(((i + 0.2 + 0.6 * r.random()) * h * 1.6, y + h * (0.2 + 0.6 * r.random())))
            y += h
        pts = np.array(pts)
        gy, gx = np.mgrid[int(self.base):H, 0:W]
        dist, _ = cKDTree(pts).query(np.stack([gx.ravel(), gy.ravel()], 1), k=2)
        edge = (dist[:, 1] - dist[:, 0]).reshape(gx.shape)
        cob = np.zeros((H, W), np.float32)
        cob[int(self.base):] = smoothstep(1.0 * s, 6 * s, edge) ** 0.7 * (0.7 + 0.3 * n01(noise(gx.shape, 1.2, self.seed + 2)))
        self.alb_ground = cob[..., None] * hexrgb("#5a524a")
        self.ground = (Y >= self.base).astype(np.float32) * np.ones((1, W), np.float32)

        # windows: warm glass with a cross mullion
        self.win = []
        ww, wh = 22 * s, 30 * s
        for cx, cy, lit in windows:
            if lit:
                self.win.append((cx, cy, periodic_signal(r, 1, 6, 3), 0.45 + 0.35 * r.random()))
        self.winmask = poly_mask(W, H, [[(cx - ww / 2, cy - wh / 2), (cx + ww / 2, cy - wh / 2), (cx + ww / 2, cy + wh / 2), (cx - ww / 2, cy + wh / 2)] for cx, cy, lit in windows if lit])
        mull = poly_mask(W, H, [[(cx - 1.5 * s, cy - wh / 2), (cx + 1.5 * s, cy - wh / 2), (cx + 1.5 * s, cy + wh / 2), (cx - 1.5 * s, cy + wh / 2)] for cx, cy, _ in windows]
                         + [[(cx - ww / 2, cy - 1.5 * s), (cx + ww / 2, cy - 1.5 * s), (cx + ww / 2, cy + 1.5 * s), (cx - ww / 2, cy + 1.5 * s)] for cx, cy, _ in windows])
        self.winmask = self.winmask * (1 - mull)
        self.win_id = np.zeros((H, W), np.int32) - 1
        for i, (cx, cy, _, _) in enumerate(self.win):
            self.win_id[int(cy - wh / 2 - 2):int(cy + wh / 2 + 2), int(cx - ww / 2 - 2):int(cx + ww / 2 + 2)] = i

        # lantern posts: in front of the houses, standing on the cobbles
        self.lan = []
        posts = []
        for i, fx in enumerate((0.13, 0.38, 0.62, 0.87)):
            lx = fx * W
            foot = 0.93 * H
            top = 0.55 * H
            side = 1 if i % 2 == 0 else -1
            posts.append([(lx - 4 * s, foot), (lx - 4 * s, top), (lx + 4 * s, top), (lx + 4 * s, foot)])
            posts.append([(lx - 8 * s, foot), (lx - 8 * s, foot - 14 * s), (lx + 8 * s, foot - 14 * s), (lx + 8 * s, foot)])
            ax = lx + side * 34 * s
            posts.append([(lx, top - 2 * s), (ax, top - 2 * s), (ax, top + 3 * s), (lx, top + 3 * s)])
            posts.append([(lx, top + 22 * s), (lx + side * 20 * s, top + 1 * s), (lx + side * 23 * s, top + 3 * s), (lx + side * 3 * s, top + 25 * s)])
            hang = 26 * s
            ly = top + hang + 12 * s
            self.lan.append(dict(ax=ax, ay=top, hang=hang, lx=ax, ly=ly, foot=foot,
                                 sway=periodic_signal(r, 1, 3, 2), flick=periodic_signal(r, 9, 40, 6),
                                 moths=[(periodic_signal(r, 2, 5, 2), periodic_signal(r, 2, 5, 2), 10 + 22 * r.random()) for _ in range(3)]))
        self.posts = poly_mask(W, H, posts)
        self.L_face = [pool(X, Y, l["lx"], l["ly"], 100 * s) ** 2 for l in self.lan]
        self.L_ground = [pool(X, Y, l["lx"], l["foot"] - 10 * s, 120 * s, 2.6) ** 2 for l in self.lan]
        self.warm = hexrgb("#ffae5c")
        self.fog = noise((24, 40, 128), 2.4, self.seed + 5, tc=3)

    def frame(self, p):
        W, H, s = self.W, self.H, self.s
        img = self.sky.copy()
        self.draw_stars(img, p, 1 - self.far)
        img = over(img, self.far, hexrgb("#0a0b15"))
        for i, (wx, wy) in enumerate(self.far_windows):
            splat(img, wx, wy, 1.6 * s, hexrgb("#ffb866"), 0.45 + 0.1 * np.sin(TAU * (i % 3 + 1) * p + i))
        fog = n01(up(at(self.fog, p), W, H))
        band = np.exp(-((self.Y - 0.6) / 0.09) ** 2)
        img += (fog * band * 0.07)[..., None] * hexrgb("#8a90b0")
        f = [1 + 0.1 * l["flick"](p) for l in self.lan]
        lf = sum(fi * L for fi, L in zip(f, self.L_face))
        lg = sum(fi * L for fi, L in zip(f, self.L_ground))
        light = (0.015 + 1.4 * lf)[..., None] * self.warm + 0.03 * hexrgb("#6070a0")
        house = self.alb_near * light
        win = np.zeros((H, W), np.float32)
        for i, (_, _, sig, b) in enumerate(self.win):
            win[self.win_id == i] = b * (1 + 0.15 * sig(p))
        house += (self.winmask * win)[..., None] * hexrgb("#ffb65e")
        img = over(img, self.near, house)
        ground = self.alb_ground * ((0.012 + 1.0 * lg)[..., None] * self.warm + 0.02)
        img = img * (1 - self.ground[..., None]) + ground * self.ground[..., None]
        for (cx, cy, sig, b) in self.win:
            splat(img, cx, cy, 16 * s, hexrgb("#ff9a40"), 0.05 * b)
        img = over(img, self.posts, hexrgb("#07060a"))
        for l, fi in zip(self.lan, f):
            ang = 0.06 * l["sway"](p)
            lx = l["ax"] + l["hang"] * np.sin(ang)
            ly = l["ay"] + l["hang"] * np.cos(ang)
            seg(img, l["ax"], l["ay"] + 2 * s, lx, ly, 0.9 * s, np.zeros(3), 0.0)
            box = np.zeros((H, W), np.float32)
            seg(box, lx, ly, lx, ly + 22 * s, 7 * s, None, 1.0)
            img *= 1 - 0.8 * np.clip(box, 0, 1)[..., None]
            splat(img, lx, ly + 12 * s, 6 * s, hexrgb("#ffd290"), 0.9 * fi)
            splat(img, lx, ly + 12 * s, 2.5 * s, hexrgb("#fff0c8"), 0.6 * fi)
            splat(img, lx, ly + 12 * s, 28 * s, self.warm, 0.16 * fi)
            splat(img, lx, ly + 12 * s, 90 * s, self.warm, 0.035 * fi)
            for mx, my, rad in l["moths"]:
                splat(img, lx + rad * s * mx(p) * 1.6, ly + 10 * s + rad * s * my(p), 1.1 * s, hexrgb("#e8d8b0"), 0.35)
        return img * vignette(self.X, self.Y, self.a, 0.3)


class DungeonTorchlight(Theme):
    """A sandstone wall in a dungeon, an arched doorway into the dark, and a
    torch in a sconce on either side. The flames are turbulent noise; their
    light flickers over the bricks and sparks rise from them."""
    name, title, seconds = "dungeon-torchlight", "Dungeon torchlight", 24

    def setup(self):
        W, H, s, r = self.W, self.H, self.s, self.rng
        yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
        self.floor_y = 0.86 * H
        bh, bw = 42 * s, 100 * s
        row = np.floor(yy / bh)
        off = r.random(int(H / bh) + 3)[row.astype(int)] * bw
        u = xx + off
        col = np.floor(u / bw)
        fx, fy = u - col * bw, yy - row * bh
        edge = np.minimum(np.minimum(fx, bw - fx), np.minimum(fy, bh - fy))
        tone = (0.55 + 0.45 * r.random(4096))[((row * 97 + col * 31) % 4096).astype(int)]
        grain = n01(up(noise((180, 320), 1.4, self.seed + 3), W, H))
        stain = n01(up(noise((45, 80), 2.6, self.seed + 4), W, H))
        bevel = smoothstep(1.0 * s, 8 * s, edge)
        # the doorway: a round-headed arch, black inside
        cx, aw = 0.5 * W, 0.075 * W
        spring = 0.5 * H
        inside = ((np.abs(xx - cx) < aw) & (yy > spring)) | (((xx - cx) ** 2 + (yy - spring) ** 2) < aw * aw)
        inside &= yy < self.floor_y
        dist_in = ndimage.distance_transform_edt(~inside)
        ring = (dist_in > 0) & (dist_in < 22 * s)
        height = bevel * (0.85 + 0.15 * grain)
        # voussoirs: the stones of the arch, cut radially
        ang = np.arctan2(yy - spring, xx - cx)
        vid = np.floor(ang / (np.pi / 11))
        vfrac = ang / (np.pi / 11) - vid
        vedge = np.minimum(np.minimum(vfrac, 1 - vfrac) * aw * np.pi / 11, np.minimum(dist_in, 22 * s - dist_in))
        arch = ring & (yy < spring)
        height = np.where(arch, smoothstep(1.0 * s, 7 * s, vedge) * (0.85 + 0.15 * grain), height)
        alb = tone * (0.7 + 0.3 * grain) * (0.55 + 0.45 * stain) * (0.2 + 0.8 * np.where(arch, smoothstep(1.0 * s, 7 * s, vedge), bevel))
        alb = np.where(arch, alb * 1.1, alb)
        # the floor: flagstones in rows, flattened
        fl = yy >= self.floor_y
        frow = np.floor(np.log1p(np.maximum(yy - self.floor_y, 0) / (6 * s)) * 3.2)
        fcol = np.floor((xx - cx) / ((50 + 30 * frow) * s) + r.random(64)[np.clip(frow, 0, 63).astype(int)])
        ftone = (0.55 + 0.35 * r.random(4096))[((frow * 53 + fcol * 17) % 4096).astype(int)]
        fr = np.log1p(np.maximum(yy - self.floor_y, 0) / (6 * s)) * 3.2 - frow
        fedge = np.minimum(fr, 1 - fr)
        alb = np.where(fl, ftone * (0.7 + 0.3 * grain) * (0.35 + 0.65 * smoothstep(0.02, 0.12, fedge)) * 0.8, alb)
        height = np.where(fl, 0.5, height)
        gy, gx = np.gradient(height * 6 * s)
        nrm = np.stack([-gx, -gy, np.full_like(gx, 3 * s)], -1)
        nrm /= np.linalg.norm(nrm, axis=-1, keepdims=True)
        self.alb = alb[..., None] * hexrgb("#b39c84")
        self.inside = inside.astype(np.float32)
        self.inside = ndimage.gaussian_filter(self.inside, 1.0 * s)
        depth = np.clip((yy - spring + aw) / (self.floor_y - spring + aw), 0, 1)
        self.void = (0.012 + 0.03 * depth ** 2)[..., None] * hexrgb("#4a6a9a")

        # two torches in sconces
        self.torch = []
        sconce = []
        for tx in (0.3 * W, 0.7 * W):
            ty = 0.45 * H
            sconce.append([(tx - 14 * s, ty), (tx + 14 * s, ty), (tx + 7 * s, ty + 22 * s), (tx - 7 * s, ty + 22 * s)])
            sconce.append([(tx - 3 * s, ty + 20 * s), (tx + 3 * s, ty + 20 * s), (tx + 3 * s, ty + 64 * s), (tx - 3 * s, ty + 64 * s)])
            sconce.append([(tx - 16 * s, ty + 58 * s), (tx + 16 * s, ty + 58 * s), (tx + 16 * s, ty + 68 * s), (tx - 16 * s, ty + 68 * s)])
            fy0 = ty - 34 * s
            d = np.stack([tx - xx, fy0 - yy, np.full_like(xx, 90 * s)], -1)
            dist = np.linalg.norm(d, axis=-1)
            lam = np.clip((nrm * d).sum(-1) / dist, 0, 1)
            falloff = 1 / (1 + ((xx - tx) ** 2 + (yy - fy0) ** 2) / (250 * s) ** 2)
            self.torch.append(dict(x=tx, y=ty, L=(lam * falloff).astype(np.float32),
                                   f1=periodic_signal(r, 8, 40, 7), f2=periodic_signal(r, 2, 6, 3),
                                   sway=periodic_signal(r, 3, 9, 3),
                                   vol=noise((32, 48, 24), 1.8, int(r.integers(1 << 30)), tc=10),
                                   tile=noise((48, 24), 1.6, int(r.integers(1 << 30))),
                                   sp=dict(k=r.integers(2, 6, 36), ph=r.random(36), dx=(r.random(36) - 0.5) * 60 * s,
                                           h=(120 + 180 * r.random(36)) * s, wk=r.integers(1, 4, 36), b=0.25 + 0.5 * r.random(36))))
        self.sconce = poly_mask(W, H, sconce)
        self.fw, self.fh = int(76 * s), int(150 * s)
        u = np.linspace(-1, 1, self.fw, dtype=np.float32)[None, :]
        v = np.linspace(1, 0, self.fh, dtype=np.float32)[:, None]
        self.fu, self.fv = u, v

    def flame(self, t, p):
        u, v = self.fu, self.fv
        turb = up(at(t["vol"], p), self.fw, self.fh)
        scroll = ndimage.shift(t["tile"], (-p * 48 * 14, 0), order=1, mode="grid-wrap")
        turb = 0.6 * turb + 0.6 * up(scroll, self.fw, self.fh)
        uu = u - 0.25 * v * v * t["sway"](p) - 0.12 * v * turb
        width = 0.55 * (1 - v) ** 0.7 * smoothstep(-0.05, 0.25, v) + 0.02
        shape = 1 - np.abs(uu) / width
        heat = np.clip(shape * 1.1 + 0.35 * turb * v - 0.9 * v * v, 0, 1)
        return ramp(heat, [(0, "#000000"), (0.2, "#3a0800"), (0.45, "#c43c08"), (0.7, "#ff9a30"), (0.9, "#ffdc90"), (1, "#fff6dc")])

    def frame(self, p):
        W, H, s = self.W, self.H, self.s
        light = np.zeros((H, W, 1), np.float32) + 0.025
        fl = []
        for t in self.torch:
            f = 1 + 0.12 * t["f1"](p) + 0.08 * t["f2"](p)
            fl.append(f)
            light = light + (1.6 * f * t["L"])[..., None]
        img = self.alb * light * hexrgb("#ffb070")
        img = img * (1 - self.inside[..., None]) + self.void * self.inside[..., None]
        img = over(img, self.sconce, hexrgb("#0d0907"))
        for t, f in zip(self.torch, fl):
            fc = self.flame(t, p) * f
            x0 = int(t["x"] - self.fw / 2)
            y0 = int(t["y"] + 6 * s - self.fh)
            img[y0:y0 + self.fh, x0:x0 + self.fw] += fc
            splat(img, t["x"], t["y"] - 30 * s, 30 * s, hexrgb("#ff8a30"), 0.16 * f)
            splat(img, t["x"], t["y"] - 30 * s, 110 * s, hexrgb("#ff7a20"), 0.03 * f)
            sp = t["sp"]
            for i in range(len(sp["k"])):
                age = (sp["k"][i] * p + sp["ph"][i]) % 1.0
                x = t["x"] + sp["dx"][i] * age + 6 * s * np.sin(TAU * (sp["wk"][i] * p + sp["ph"][i]) * 3)
                y = t["y"] - 40 * s - sp["h"][i] * age
                splat(img, x, y, 1.0 * s, hexrgb("#ffb060"), sp["b"][i] * (1 - age) ** 2 * f)
        return img * vignette(self.X, self.Y, self.a, 0.45)


class ShipAtDusk(Theme):
    """A tall ship at anchor against a sunset: streaked clouds lit from
    below, a sea that glitters in the sun's path, gulls crossing, and a
    lantern at the ship's stern."""
    name, title, seconds = "ship-at-dusk", "Ship at dusk", 30

    def setup(self):
        W, H, s, r = self.W, self.H, self.s, self.rng
        self.hz = hz = 0.62 * H
        self.sun = (0.64 * W, hz + 6 * s)
        Y = self.Y
        stops = [(0, "#120c26"), (0.35, "#3a2248"), (0.62, "#9a4a48"), (0.85, "#e07a44"), (1, "#f4b060")]
        self.sky = np.broadcast_to(ramp(np.clip(Y * H / hz, 0, 1), stops), (H, W, 3)).copy()
        xs = np.arange(W, dtype=np.float32)[None, :]
        ys = np.arange(H, dtype=np.float32)[:, None]
        d = np.sqrt((xs - self.sun[0]) ** 2 + (ys - self.sun[1]) ** 2)
        self.sky += (0.5 * np.exp(-d / (90 * s)) + 0.2 * np.exp(-d / (320 * s)))[..., None] * hexrgb("#ffb070")
        self.sky += (smoothstep(46 * s, 40 * s, d) * 1.0)[..., None] * hexrgb("#ffe2b0")
        self.cloud = noise((24, 90, 96), 2.6, self.seed + 1, tc=3)
        self.cloud_col = ramp(np.clip(Y * H / hz, 0, 1), [(0, "#2a1a3a"), (0.5, "#6a3a58"), (0.8, "#d8704a"), (1, "#ffb070")])
        # the sea: a periodic wave field sampled in perspective
        self.wave = noise((256, 256), 3.2, self.seed + 2)
        self.wave_s = ndimage.gaussian_filter(self.wave, 3, mode="wrap")
        self.glint = noise((256, 256), 1.2, self.seed + 3)
        rows = np.arange(int(hz), H, dtype=np.float32)[:, None] - hz + 1.5
        self.z = 60 * s / rows
        self.su = (xs - W / 2) / rows * 8 / s
        self.sea_rows = H - int(hz)
        mirror = np.clip(hz - (np.arange(int(hz), H) - hz) * 0.6, 0, H - 1).astype(int)
        self.refl = self.sky[mirror] * 0.55
        dsun = xs - self.sun[0]
        spread = 30 * s + 0.9 * (rows - 1.5)
        self.path = np.exp(-(dsun / spread) ** 2)
        self.near_w = smoothstep(0, 60 * s, rows)
        # the ship, drawn once and rolled each frame
        L = 200 * s
        self.L = L
        sw, sh = int(1.9 * L), int(1.2 * L)
        self.sox, self.soy = 0.9 * L, 0.95 * L
        ox, oy = self.sox, self.soy
        P = lambda pts: [(ox + x * L, oy + y * L) for x, y in pts]
        polys = [P([(-0.52, -0.2), (-0.3, -0.12), (0.38, -0.1), (0.52, -0.2), (0.44, -0.04), (0.3, 0.03), (-0.4, 0.03), (-0.5, -0.06)]),
                 P([(-0.54, -0.28), (-0.3, -0.26), (-0.3, -0.11), (-0.52, -0.12)])]
        for mx, top in ((-0.08, -0.86), (0.2, -0.72), (-0.36, -0.55)):
            polys.append(P([(mx - 0.008, -0.1), (mx - 0.008, top), (mx + 0.008, top), (mx + 0.008, -0.1)]))

        def sail(cx, y0, y1, w0, w1, bulge):
            pts = [(cx - w1 / 2, y1), (cx + w1 / 2, y1)]
            for t in np.linspace(0, 1, 8):
                pts.append((cx + (w1 + (w0 - w1) * t) / 2 + bulge * np.sin(np.pi * t), y1 + (y0 - y1) * t))
            pts += [(cx - w0 / 2, y0)]
            for t in np.linspace(1, 0, 8):
                pts.append((cx - (w1 + (w0 - w1) * t) / 2 - bulge * 0.3 * np.sin(np.pi * t), y1 + (y0 - y1) * t))
            return P(pts)
        polys += [sail(-0.08, -0.22, -0.46, 0.4, 0.34, 0.02), sail(-0.08, -0.5, -0.68, 0.32, 0.24, 0.015),
                  sail(-0.08, -0.71, -0.82, 0.2, 0.14, 0.01), sail(0.2, -0.22, -0.42, 0.32, 0.26, 0.02),
                  sail(0.2, -0.46, -0.64, 0.24, 0.18, 0.015), sail(-0.36, -0.24, -0.5, 0.18, 0.14, 0.01),
                  P([(0.44, -0.17), (0.74, -0.33), (0.745, -0.32), (0.45, -0.15)]),
                  P([(0.72, -0.31), (0.21, -0.66), (0.26, -0.2)]),
                  P([(-0.08, -0.86), (0.02, -0.84), (-0.08, -0.82)])]
        im = Image.new("L", (sw * 4, sh * 4), 0)
        dr = ImageDraw.Draw(im)
        for pts in polys:
            dr.polygon([(x * 4, y * 4) for x, y in pts], fill=255)
        for a, b in (((-0.08, -0.86), (-0.5, -0.27)), ((-0.08, -0.86), (0.2, -0.72)), ((0.2, -0.72), (0.72, -0.31)), ((-0.36, -0.55), (-0.52, -0.27))):
            (x0, y0), (x1, y1) = P([a, b])
            dr.line([(x0 * 4, y0 * 4), (x1 * 4, y1 * 4)], fill=200, width=max(int(3 * s), 2))
        self.ship = np.asarray(im.resize((sw, sh), Image.BOX), np.float32) / 255
        self.ship_at = (0.34 * W, hz + 34 * s)
        self.lantern = (-0.5 * L, -0.3 * L)
        self.bob = periodic_signal(r, 2, 4, 2)
        self.roll = periodic_signal(r, 1, 3, 2)
        self.gulls = [dict(ph=r.random(), y=(0.18 + 0.2 * r.random()) * H, k=int(r.integers(30, 45)),
                           wob=periodic_signal(r, 1, 3, 2), sz=(0.7 + 0.5 * r.random()) * s, dir=1 if i % 2 == 0 else -1) for i in range(3)]

    def frame(self, p):
        W, H, s, hz = self.W, self.H, self.s, int(self.hz)
        img = self.sky.copy()
        c = n01(up(at(self.cloud, p), W, H))
        dens = smoothstep(0.52, 0.85, c) * smoothstep(0.02, 0.2, self.Y) * smoothstep(0.62, 0.45, self.Y)
        img = img * (1 - 0.85 * dens[..., None]) + (0.85 * dens)[..., None] * self.cloud_col
        # the sea
        v = self.z * 30 + p * 256 * 2
        u = self.su
        sh = (self.sea_rows, W)
        coords = [np.broadcast_to(v, sh), np.broadcast_to(u, sh)]
        wv = ndimage.map_coordinates(self.wave, coords, order=1, mode="grid-wrap")
        ws = ndimage.map_coordinates(self.wave_s, coords, order=1, mode="grid-wrap")
        wv = ws + (wv - ws) * self.near_w
        # the glints scroll a whole number of tiles per loop, like the waves
        g = ndimage.map_coordinates(self.glint, [np.broadcast_to(self.z * 102 - p * 256 * 3, sh), np.broadcast_to(u * 2.6, sh)], order=1, mode="grid-wrap")
        sea = self.refl * (0.6 + 0.15 * wv[..., None]) + hexrgb("#0a0716") * 0.5
        sea += (self.path * (0.25 + 0.1 * wv))[..., None] * hexrgb("#ff9a58") * 0.6
        sea += (self.path * smoothstep(1.4, 2.6, g + 0.3 * wv) * (0.25 + 0.35 * self.near_w))[..., None] * hexrgb("#ffd89a")
        img[hz:] = sea
        # the ship, its reflection and its lantern
        ang = 1.2 * self.roll(p)
        dy = 3 * s * self.bob(p)
        sim = Image.fromarray(self.ship, "F").rotate(ang, resample=Image.BICUBIC, center=(self.sox, self.soy))
        m = np.asarray(sim, np.float32)
        sx, sy = self.ship_at
        x0, y0 = int(round(sx - self.sox)), int(round(sy - self.soy + dy))
        mh, mw = m.shape
        mask = np.zeros((H, W), np.float32)
        ya, yb = max(y0, 0), min(y0 + mh, H)
        xa, xb = max(x0, 0), min(x0 + mw, W)
        mask[ya:yb, xa:xb] = m[ya - y0:yb - y0, xa - x0:xb - x0]
        wl = int(sy + dy + 2 * s)
        refl = np.zeros_like(mask)
        n = min(H - wl, wl)
        rows = np.arange(n)
        src = wl - 1 - (rows / 0.7).astype(int)
        okr = src >= 0
        refl[wl:wl + n][okr] = mask[src[okr]] * np.exp(-rows[okr] / (70 * s))[:, None]
        refl = roll_rows(refl[..., None], 2.5 * s * np.sin(np.arange(H) * 0.35 + TAU * 12 * p))[..., 0]
        mask[wl:] = 0
        img *= 1 - 0.55 * np.clip(refl, 0, 1)[..., None]
        rim = np.clip(mask - ndimage.shift(mask, (0, -2 * s), order=1), 0, 1)
        img = over(img, mask, hexrgb("#0c0714"))
        img += (rim * 0.35)[..., None] * hexrgb("#ff9a58")
        a = np.deg2rad(-ang)
        lx0, ly0 = self.lantern
        lx = sx + lx0 * np.cos(a) - ly0 * np.sin(a)
        ly = sy + dy + lx0 * np.sin(a) + ly0 * np.cos(a)
        flick = 1 + 0.1 * np.sin(TAU * 17 * p) * np.sin(TAU * 7 * p)
        splat(img, lx, ly, 1.8 * s, hexrgb("#ffd890"), 0.9 * flick)
        splat(img, lx, ly, 10 * s, hexrgb("#ffa050"), 0.12 * flick)
        splat(img, lx, 2 * (sy + dy) - ly + 10 * s, 3 * s, hexrgb("#ffa050"), 0.12 * flick)
        # gulls: each crosses once per loop
        gm = np.zeros((H, W), np.float32)
        for gl in self.gulls:
            x = wrap(gl["ph"] + gl["dir"] * p, W, 40 * s)
            y = gl["y"] + 14 * s * gl["wob"](p)
            fl = np.sin(TAU * gl["k"] * p)
            z = gl["sz"]
            for sd in (-1, 1):
                ex, ey = x + sd * 7 * z, y - (3 + 3 * fl) * z
                seg(gm, x, y, ex, ey, 0.9 * z, None, 1.0)
                seg(gm, ex, ey, x + sd * 14 * z, y - (1 + 6 * fl) * z, 0.8 * z, None, 1.0)
        img = over(img, np.clip(gm, 0, 1) * 0.85, hexrgb("#1a0e1c"))
        return img * vignette(self.X, self.Y, self.a, 0.3)


class ForestFireflies(Theme):
    """A deep forest at night: three layers of trunks fading into mist, a
    moon glow high in the canopy, and fireflies wandering and blinking, some
    between the trees and some close."""
    name, title, seconds = "forest-fireflies", "Forest fireflies", 28

    def setup(self):
        W, H, s, r = self.W, self.H, self.s, self.rng
        X, Y = self.X, self.Y
        self.bg = np.broadcast_to(ramp(Y, [(0, "#081a22"), (0.45, "#0e2a30"), (0.75, "#0c2226"), (1, "#061014")]), (H, W, 3)).copy()
        mg = np.exp(-(((X - 0.74) * self.a) ** 2 + (Y - 0.1) ** 2) / 0.05)
        self.bg += (0.22 * mg)[..., None] * hexrgb("#8fc0c0")
        # light shafts slanting down from the moon
        ang = np.deg2rad(62)
        t = (X * self.a) * np.cos(ang) + Y * np.sin(ang)
        rays = n01(up(noise((4, 256), 1.8, self.seed + 9)[:1], W, 1))
        q = np.clip(((X * self.a - 0.74 * self.a) * np.sin(ang) - (Y - 0.1) * np.cos(ang)) * 0.5 + 0.5, 0, 1)
        self.rays = (smoothstep(0.55, 0.9, np.interp(q, np.linspace(0, 1, W), rays[0])) * np.exp(-((t - 0.9) / 0.5) ** 2) * smoothstep(0.1, 0.35, Y)).astype(np.float32)
        xs = np.arange(W, dtype=np.float32)[None, :]
        ys = np.arange(H, dtype=np.float32)[:, None]
        self.layers = []
        spec = [(26, (8, 18), 0.66, "#10282c", 0.35, 0.5), (14, (20, 38), 0.78, "#07161a", 0.25, 0.3), (6, (50, 95), 0.95, "#020707", 0.18, 0.2)]
        for li, (n, (w0, w1), gnd, colr, can, _) in enumerate(spec):
            m = np.zeros((H, W), np.float32)
            xs0 = np.sort(r.random(n)) * W + (r.random(n) - 0.5) * W / n
            for i, xc in enumerate(xs0):
                w = (w0 + (w1 - w0) * r.random()) * s
                wob = noise((H // 8,), 2.4, int(r.integers(1 << 30)))
                xc_y = xc + 8 * s * np.interp(np.arange(H), np.linspace(0, H, len(wob)), wob / np.abs(wob).max())
                half = w / 2 * (1 + 0.35 * ys / H)
                m = np.maximum(m, smoothstep(half[:, 0][:, None] + 1, half[:, 0][:, None] - 1, np.abs(xs - xc_y[:, None])))
            fol = n01(up(noise((90, 160), 2.3 - 0.2 * li, int(r.integers(1 << 30))), W, H))
            m = np.maximum(m, smoothstep(0.02, -0.02, Y * H / H - (can + 0.45 * fol - 0.3)) * smoothstep(0.5, 0.56, fol + 0.1 * (1 - Y)))
            line = self.ridge(gnd * H, 22 * s * (1 + li), int(r.integers(1 << 30)), beta=1.4)
            m = np.maximum(m, smoothstep(line[None, :] - 1, line[None, :] + 1, ys))
            self.layers.append((np.clip(m, 0, 1), hexrgb(colr)))
        self.fog = noise((24, 45, 80), 2.4, self.seed + 5, tc=3)
        n = 90
        self.ff = dict(x=r.random(n) * W, y=(0.35 + 0.6 * r.random(n)) * H,
                       ax=(20 + 60 * r.random(n)) * s, ay=(10 + 30 * r.random(n)) * s,
                       kx=r.integers(1, 3, n), ky=r.integers(1, 4, n), px=r.random(n) * TAU, py=r.random(n) * TAU,
                       kb=r.integers(4, 11, n), pb=r.random(n), near=r.random(n) < 0.22,
                       c=hexrgb("#b8ff5c") + (hexrgb("#fff2a0") - hexrgb("#b8ff5c")) * r.random(n)[:, None])

    def fireflies(self, img, p, near):
        f, s = self.ff, self.s
        for i in np.nonzero(f["near"] == near)[0]:
            x = f["x"][i] + f["ax"][i] * np.sin(TAU * f["kx"][i] * p + f["px"][i])
            y = f["y"][i] + f["ay"][i] * np.sin(TAU * f["ky"][i] * p + f["py"][i])
            b = (f["kb"][i] * p + f["pb"][i]) % 1.0
            glow = 0.08 + np.exp(-((b - 0.5) / 0.07) ** 2)
            z = 1.8 if near else 1.0
            splat(img, x, y, 1.4 * z * s, f["c"][i], 1.1 * glow)
            splat(img, x, y, 10 * z * s, f["c"][i], 0.16 * glow)

    def frame(self, p):
        W, H = self.W, self.H
        img = self.bg + (self.rays * (0.05 + 0.02 * np.sin(TAU * p)))[..., None] * hexrgb("#a8d8d0")
        fog = n01(up(at(self.fog, p), W, H))
        for li, (m, c) in enumerate(self.layers):
            if li == 2:
                self.fireflies(img, p, False)
            img = over(img, m, c)
            if li < 2:
                band = smoothstep(0.45, 0.8, self.Y) * (0.12 - 0.05 * li)
                img += (fog * band)[..., None] * hexrgb("#5a8a8a")
        self.fireflies(img, p, True)
        return img * vignette(self.X, self.Y, self.a, 0.35)


class CompassionSands(Theme):
    """Desert dunes at dusk: sharp crests layered into the haze, sand
    streaming off them in the wind, a low saltating sheet over the near
    slope, and grains hopping past."""
    name, title, seconds = "compassion-sands", "Compassion sands", 30

    def setup(self):
        W, H, s, r = self.W, self.H, self.s, self.rng
        X, Y = self.X, self.Y
        ys = np.arange(H, dtype=np.float32)[:, None]
        sky = ramp(np.clip(Y / 0.5, 0, 1), [(0, "#1a1430"), (0.45, "#4a2e48"), (0.8, "#b0605a"), (1, "#e89a66")])
        self.sky = np.broadcast_to(sky, (H, W, 3)).copy()
        self.sky += (0.25 * np.exp(-((X - 0.3) * self.a) ** 2 / 0.3 - (Y - 0.5) ** 2 / 0.01))[..., None] * hexrgb("#ffb070")
        self.stars(60, 0.25, (0.06, 0.35))
        base = [0.5, 0.57, 0.67, 0.8]
        amp = [14, 26, 46, 72]
        lit = ["#b07262", "#c07a5c", "#cf8a5c", "#dc9a62"]
        shade = ["#8a5460", "#7a4652", "#65363e", "#4a2830"]
        body = ["#9c6260", "#9a5c56", "#8a4e46", "#7a4238"]
        self.dunes = []
        self.crests = []
        rip = n01(up(noise((60, 20), 2.0, self.seed + 7), W, H))
        for j in range(4):
            n = noise((W,), 4.2 - 0.3 * j, self.seed + 10 + j)
            n = n / np.abs(n).max()
            crest = base[j] * H - amp[j] * s * (1 - np.abs(n)) ** 1.5
            slope = ndimage.gaussian_filter1d(np.gradient(crest), 14, mode="wrap")
            d = ys - crest[None, :]
            # the boundary between the lit and the shaded face slants down the slope
            face1 = smoothstep(-0.04, 0.04, slope)
            xi = np.arange(W, dtype=np.float32)
            face = np.stack([np.interp(xi + 0.8 * np.maximum(d[y], 0), xi, face1, period=W) for y in range(H)])
            m = smoothstep(-0.8, 0.8, d)
            near = np.exp(-np.maximum(d, 0) / (1.2 * amp[j] * s))
            c_face = hexrgb(lit[j]) * (1 - face[..., None]) + hexrgb(shade[j]) * face[..., None]
            col = c_face * near[..., None] + hexrgb(body[j]) * (1 - near[..., None])
            if j >= 2:
                wv = 0.5 + 0.5 * np.sin(TAU * (d / (7 * s) + 4 * rip))
                col = col * (0.94 + 0.08 * wv[..., None])
            haze = [0.45, 0.3, 0.15, 0.0][j]
            col = col * (1 - haze) + self.sky[int(base[0] * H) - 2][None, :, :] * haze
            self.dunes.append((m, col))
            self.crests.append(crest)
        self.plume_tex = noise((360, 80), 1.8, self.seed + 30)
        self.sheet_tex = noise((240, 64), 1.6, self.seed + 31)
        n = 260
        self.gr = dict(x=r.random(n), y=(0.72 + 0.28 * r.random(n)) * H, k=r.integers(3, 7, n),
                       kh=r.integers(20, 40, n), ph=r.random(n), b=0.12 + 0.2 * r.random(n))

    def frame(self, p):
        W, H, s = self.W, self.H, self.s
        img = self.sky.copy()
        self.draw_stars(img, p, smoothstep(0.3, 0.05, self.Y[:, 0])[:, None] * np.ones((1, W)))
        plume = n01(up(ndimage.shift(self.plume_tex, (0, p * 80 * 3), order=1, mode="grid-wrap"), W, H))
        sheet = n01(up(ndimage.shift(self.sheet_tex, (0, p * 64 * 5), order=1, mode="grid-wrap"), W, H))
        ys = np.arange(H, dtype=np.float32)[:, None]
        for j, (m, col) in enumerate(self.dunes):
            img = over(img, m, col)
            if j >= 1:
                dist = self.crests[j][None, :] - ys
                pl = np.exp(-np.maximum(dist, 0) / ((6 + 5 * j) * s)) * smoothstep(-4 * s, 2 * s, dist)
                img += (pl * smoothstep(0.45, 0.85, plume) * (0.09 + 0.05 * j))[..., None] * hexrgb("#f0b080")
        low = smoothstep(0.7, 1.0, self.Y)
        img += (low * smoothstep(0.4, 0.9, sheet) * 0.07)[..., None] * hexrgb("#f4c090")
        g = self.gr
        for i in range(len(g["k"])):
            x = wrap(g["x"][i] + g["k"][i] * p, W, 6)
            y = g["y"][i] - abs(np.sin(TAU * (g["kh"][i] * p + g["ph"][i]))) * 7 * s
            splat(img, x, y, 0.8 * s, hexrgb("#ffdcaa"), g["b"][i])
        return img * vignette(self.X, self.Y, self.a, 0.3)


class MoonglowRain(Theme):
    """Rain over a town of mages' towers at night: spires against moonlit
    cloud, windows lit blue and amber, a glowing orb on the high tower, and
    the whole of it reflected, rippling, in the flooded street."""
    name, title, seconds = "moonglow-rain", "Moonglow rain", 24

    def setup(self):
        W, H, s, r = self.W, self.H, self.s, self.rng
        X, Y = self.X, self.Y
        self.hz = hz = int(0.74 * H)
        self.sky = np.broadcast_to(ramp(np.clip(Y / 0.74, 0, 1), [(0, "#05071a"), (0.6, "#10183a"), (1, "#1c2450")]), (H, W, 3)).copy()
        self.moon = (0.76 * W, 0.2 * H)
        xs = np.arange(W, dtype=np.float32)[None, :]
        ys = np.arange(H, dtype=np.float32)[:, None]
        d = np.sqrt((xs - self.moon[0]) ** 2 + (ys - self.moon[1]) ** 2)
        self.moon_disc = smoothstep(36 * s, 33 * s, d)
        self.moon_glow = np.exp(-d / (160 * s))
        self.cloud = noise((24, 60, 160), 2.5, self.seed + 1, tc=3)
        # the skyline: towers with spires, a domed hall, small houses
        polys, wins = [], []

        def tower(cx, w, top, cone):
            polys.append([(cx - w / 2, hz), (cx - w / 2, top), (cx, top - cone), (cx + w / 2, top), (cx + w / 2, hz)])
            polys.append([(cx - w / 2 - 4 * s, top + 2 * s), (cx + w / 2 + 4 * s, top + 2 * s), (cx + w / 2 + 4 * s, top + 8 * s), (cx - w / 2 - 4 * s, top + 8 * s)])
            y = top + 26 * s
            while y < hz - 30 * s:
                if r.random() < 0.55:
                    wins.append((cx + (r.random() - 0.5) * w * 0.4, y))
                y += (30 + 20 * r.random()) * s
        x = -20 * s
        while x < W + 20 * s:
            w = (50 + 70 * r.random()) * s
            wall = hz - (40 + 50 * r.random()) * s
            polys.append([(x, hz), (x, wall), (x + w / 2, wall - 28 * s), (x + w, wall), (x + w, hz)])
            if r.random() < 0.6:
                wins.append((x + w * (0.3 + 0.4 * r.random()), wall + 18 * s))
            x += w * 0.9
        for cx, w, top, cone in ((0.12, 34, 0.42, 70), (0.27, 28, 0.5, 60), (0.36, 40, 0.38, 80),
                                 (0.63, 30, 0.46, 64), (0.86, 36, 0.4, 76), (0.95, 26, 0.52, 50)):
            tower(cx * W, w * s, top * H, cone * s)
        cx, w = 0.5 * W, 50 * s
        self.orb = (cx, 0.3 * H - 100 * s)
        tower(cx, w, 0.3 * H, 90 * s)
        dx, dw = 0.74 * W, 110 * s
        dome = [(dx - dw / 2, hz), (dx - dw / 2, 0.58 * H)]
        for t in np.linspace(np.pi, 0, 24):
            dome.append((dx + dw / 2 * np.cos(t), 0.58 * H - dw * 0.55 * np.sin(t)))
        dome += [(dx + dw / 2, hz)]
        polys.append(dome)
        polys.append([(dx - 3 * s, 0.58 * H - dw * 0.55), (dx, 0.58 * H - dw * 0.55 - 30 * s), (dx + 3 * s, 0.58 * H - dw * 0.55)])
        for i in range(4):
            wins.append((dx - dw * 0.3 + i * dw * 0.2, 0.64 * H))
        self.town = poly_mask(W, H, polys)
        self.rim = np.clip(self.town - ndimage.shift(self.town, (2 * s, -2 * s), order=1), 0, 1) * np.exp(-np.abs(xs - self.moon[0]) / (500 * s))
        self.wins = [(x, y, r.random() < 0.55, periodic_signal(r, 1, 5, 3), 0.5 + 0.4 * r.random()) for x, y in wins]
        self.pulse = periodic_signal(r, 1, 3, 2)
        # rain: a far and a near layer of streaks
        self.rain = []
        for n, ln, w, b, (k0, k1) in ((240, 14, 0.5, 0.05, (26, 40)), (90, 34, 0.8, 0.09, (18, 28))):
            self.rain.append(dict(x=r.random(n) * W, y=r.random(n), k=r.integers(k0, k1, n), ln=ln * s, w=w * s, b=b * (0.6 + 0.8 * r.random(n))))
        self.slant = -0.12
        self.rip = [(int(r.integers(10, 20)), r.random(), r.random((20, 2))) for _ in range(70)]

    def frame(self, p):
        W, H, s, hz = self.W, self.H, self.s, self.hz
        c = n01(up(at(self.cloud, p), W, H))
        dens = smoothstep(0.35, 0.8, c)
        img = self.sky * (1 - 0.4 * dens[..., None])
        img += (dens * (0.12 + 0.9 * self.moon_glow))[..., None] * hexrgb("#6a78b0") * 0.5
        mv = 1 - 0.8 * dens[int(self.moon[1]), int(self.moon[0])]
        img += (0.85 * self.moon_disc * mv + self.moon_glow * 0.25 * mv)[..., None] * hexrgb("#dfe6ff")
        img = over(img, self.town, hexrgb("#060818"))
        img += (self.rim * 0.25)[..., None] * hexrgb("#8090d0")
        for x, y, blue, sig, b in self.wins:
            col = hexrgb("#9fb4ff") if blue else hexrgb("#ffc27a")
            bb = b * (1 + (0.05 if blue else 0.18) * sig(p))
            splat(img, x, y, 2.4 * s, col, bb)
            splat(img, x, y, 10 * s, col, 0.05 * bb)
        pu = 1 + 0.25 * self.pulse(p)
        splat(img, self.orb[0], self.orb[1], 4 * s, hexrgb("#d6e4ff"), 0.9 * pu)
        splat(img, self.orb[0], self.orb[1], 26 * s, hexrgb("#7f9cff"), 0.2 * pu)
        splat(img, self.orb[0], self.orb[1], 90 * s, hexrgb("#5a70d0"), 0.05 * pu)
        # the flooded street: the town mirrored, rippling
        n = H - hz
        src = np.clip(hz - 1 - (np.arange(n) * 1.1).astype(int), 0, H - 1)
        refl = img[src]
        rows = np.arange(n, dtype=np.float32)
        refl = roll_rows(refl, (1.5 + rows / n * 4) * s * np.sin(rows * 0.11 / s + TAU * 6 * p))
        refl = ndimage.gaussian_filter(refl, (1.5 * s, 0.6 * s, 0))
        img[hz:] = refl * 0.45 * (1 - 0.4 * rows / n)[:, None, None] + hexrgb("#04050c")
        for pj, sj, pos in self.rip:
            t = p * pj + sj
            k = int(np.floor(t)) % pj
            age = (t - np.floor(t)) * self.seconds / pj
            if age > 0.8:
                continue
            cx, cy = pos[k, 0] * W, hz + 6 * s + pos[k, 1] * (n - 10 * s)
            rad = (2 + age * 26) * s * (0.5 + (cy - hz) / n)
            R = int(rad + 5 * s)
            x0, x1 = max(int(cx) - R, 0), min(int(cx) + R + 1, W)
            y0, y1 = max(int(cy - R * 0.3), hz), min(int(cy + R * 0.3) + 1, H)
            if x0 >= x1 or y0 >= y1:
                continue
            dd = np.sqrt((np.arange(x0, x1)[None, :] - cx) ** 2 + ((np.arange(y0, y1)[:, None] - cy) / 0.3) ** 2)
            ring = np.exp(-((dd - rad) / (1.2 * s)) ** 2)
            img[y0:y1, x0:x1] += (0.12 * (1 - age / 0.8) ** 2 * ring)[..., None] * hexrgb("#9fb0e0")
        for L in self.rain:
            for i in range(len(L["k"])):
                yh = (L["y"][i] + L["k"][i] * p) % 1.0 * (H + L["ln"]) - 0.5 * L["ln"]
                xh = (L["x"][i] + self.slant * yh) % W
                seg(img, xh - self.slant * L["ln"], yh - L["ln"], xh, yh, L["w"], hexrgb("#a8b8e0"), L["b"][i], tail=0.9)
        return img * vignette(self.X, self.Y, self.a, 0.3)


STORE_LOOPS = [BritainLanterns, DungeonTorchlight, ShipAtDusk, ForestFireflies, CompassionSands, MoonglowRain]
