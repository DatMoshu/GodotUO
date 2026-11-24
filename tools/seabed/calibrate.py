"""Measure what each floor hue looks like in the client, and fit a model of it.

The stage's floor is a sand tile under a translucent water surface. What the player sees
is not the hue's colour, so the ramp is chosen from photographs, not from the hue table:

    python tools/seabed/run.py generate --calibrate --out DIR    # 64 hues in an 8x8 pattern, no objects
    (photograph the Haven shallows with --layer-shot, same light)
    python tools/seabed/calibrate.py fit PNG --at X Y --player-px PX PY

fit finds where the cells sit in the picture, reads the mean colour of every hue, fits
observed = gain * predicted + offset per channel (predicted: the sand tile through the
hue), and reports how well the model predicts the held-out hues.
"""
from __future__ import annotations

import struct
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "uodata_write"))
sys.path.insert(0, str(HERE.parent))

GRID = 8
STEP = 47


def candidates(ramp):
    """64 hue ids: the ramp's own, then an even spread over hues.mul."""
    out = []
    for h in tuple(dict.fromkeys(ramp)) + tuple(range(3, 3000, STEP)):
        if h not in out:
            out.append(h)
    return out[:GRID * GRID]


def pattern(ramp):
    cand = np.asarray(candidates(ramp))
    return lambda xs, ys: cand[(xs % GRID) + GRID * (ys % GRID)]


def read_hues(path: Path):
    """hues.mul -> {hue id (1-based): 32 RGB triplets (0..255)}."""
    data = Path(path).read_bytes()
    out = {}
    n = 0
    for g in range(len(data) // (4 + 8 * 88)):
        base = g * (4 + 8 * 88) + 4
        for k in range(8):
            n += 1
            cols = struct.unpack_from("<32H", data, base + k * 88)
            out[n] = np.array([[((c >> 10) & 31) * 255 // 31, ((c >> 5) & 31) * 255 // 31, (c & 31) * 255 // 31] for c in cols], float)
    return out


def hued_tile_mean(pixels, hue_colors):
    """Mean colour of a diamond tile run through a hue, ClassicUO style: the pixel's
    grey (mean of its 5-bit channels) indexes the hue's 32 colours."""
    px = np.asarray([p for p in pixels if p & 0x8000 or p], np.int64)
    r = (px >> 10) & 31; g = (px >> 5) & 31; b = px & 31
    grey = np.clip(((r + g + b) // 3), 0, 31)
    return hue_colors[grey].mean(axis=0)


def cell_pixel(cx, cy, px, py, ox, oy, z=-15, ref_z=0):
    """Screen position of the middle of cell (cx, cy) at height z, with the player cell at (px, py) on screen."""
    return ox + (cx - px - (cy - py)) * 22, oy + (cx - px + (cy - py)) * 22 - (z - ref_z) * 4 + 22


def sand_stats(install, hues, ids=(0x9DE9, 0x9DEA, 0x9DEB, 0x9DEC)):
    """({hue: mean colour}, {hue: summed per-channel spread}) of the four sand tiles through each hue."""
    import uodata as U
    from guo import uoart
    uop = Path(install) / "artLegacyMUL.uop"
    tiles = []
    for i in ids:
        w, h, px = uoart.decode_static(U.uop_read(uop, U.ART_PATTERN.format(0x4000 + i)))
        tiles.append([p for p in px if p])
    mean, spread = {}, {}
    for k, c in hues.items():
        mean[k] = np.mean([hued_tile_mean(t, c) for t in tiles], axis=0)
        grey = [np.clip((((np.asarray(t) >> 10) & 31) + ((np.asarray(t) >> 5) & 31) + (np.asarray(t) & 31)) // 3, 0, 31) for t in tiles]
        spread[k] = float(np.concatenate([c[g] for g in grey]).std(axis=0).sum())
    return mean, spread


def sand_mean(install, hues, ids=(0x9DE9, 0x9DEA, 0x9DEB, 0x9DEC)):
    """{hue: mean colour of the four sand tiles through that hue}."""
    return sand_stats(install, hues, ids)[0]


def lattice(im, player_px, top=225, left=40, right=620, search=22, half=(9, 4)):
    """Where the cell centres sit: the offset whose diamonds are most uniform (above the deep water)."""
    best = None
    for ox in range(-search, search + 1, 2):
        for oy in range(-search, search + 1, 2):
            s = []
            for dx in range(-8, 9):
                for dy in range(-8, 9):
                    sx = player_px[0] + ox + (dx - dy) * 22
                    sy = player_px[1] + oy + (dx + dy) * 22 + 40
                    if left <= sx - half[0] and sx + half[0] < right and 30 <= sy - half[1] and sy + half[1] < top:
                        s.append(im[sy - half[1]:sy + half[1], sx - half[0]:sx + half[0]].reshape(-1, 3).std(0).sum())
            if s and (best is None or np.mean(s) < best[0]):
                best = (float(np.mean(s)), ox, oy)
    return best


def measure(png, player=(3505, 2612), px=(325, 228), ramp=None, model=None, half=(9, 4), top=225):
    """Mean on-screen colour of every calibration hue in `png` as {hue: (rgb, n)}, plus the
    lattice offset and the index shift picked by correlating with `model` ({hue: predicted rgb})."""
    from PIL import Image
    im = np.asarray(Image.open(png).convert("RGB"), float)
    cand = candidates(ramp)
    _, ox, oy = lattice(im, px, top=top)
    cells = []
    for dx in range(-8, 9):
        for dy in range(-8, 9):
            sx = px[0] + ox + (dx - dy) * 22
            sy = px[1] + oy + (dx + dy) * 22 + 40
            if 40 <= sx - half[0] and sx + half[0] < 620 and 30 <= sy - half[1] and sy + half[1] < top:
                cells.append((player[0] + dx, player[1] + dy, im[sy - half[1]:sy + half[1], sx - half[0]:sx + half[0]].reshape(-1, 3).mean(0)))
    best = None
    for kx in range(GRID):
        for ky in range(GRID):
            obs = np.array([c[2] for c in cells]); pred = np.array([model[cand[((c[0] + kx) % GRID) + GRID * ((c[1] + ky) % GRID)]] for c in cells])
            score = np.mean([np.corrcoef(obs[:, j], pred[:, j])[0, 1] for j in range(3)])
            if best is None or score > best[0]:
                best = (score, kx, ky)
    score, kx, ky = best
    groups = {}
    for x, y, c in cells:
        groups.setdefault(cand[((x + kx) % GRID) + GRID * ((y + ky) % GRID)], []).append(c)
    return {h: (np.mean(v, axis=0), len(v)) for h, v in groups.items()}, (ox, oy, kx, ky), score


def fit(measured, model):
    """observed = gain * predicted + offset per channel; returns (gain, offset, rms residual)."""
    hs = [h for h in measured if measured[h][1] >= 2]
    P = np.array([model[h] for h in hs]); O = np.array([measured[h][0] for h in hs])
    gain = np.zeros(3); off = np.zeros(3)
    for j in range(3):
        a, b = np.polyfit(P[:, j], O[:, j], 1); gain[j], off[j] = a, b
    res = O - (P * gain + off)
    return gain, off, float(np.sqrt((res ** 2).mean()))


# What the client showed on 2026-11-22 at Haven (see fit): observed = GAIN * predicted + OFFSET
# per channel, residual 1.5 over 63 hues. SHORE is hue 1151 as photographed, DEEP the original opaque sea.
GAIN = np.array([0.365, 0.365, 0.365])
OFFSET = np.array([-0.3, 37.2, 54.7])
SHORE = np.array([42.0, 96.0, 114.0])
DEEP = np.array([0.0, 54.0, 80.0])


def predict(model):
    """{hue: expected on-screen colour} from the hued sand tile."""
    return {h: np.clip(m * GAIN + OFFSET, 0, None) for h, m in model.items()}


def progress(colour):
    """(progress along SHORE->DEEP, distance off that line) of an on-screen colour."""
    v = DEEP - SHORE
    n = float(np.linalg.norm(v))
    p = float((colour - SHORE) @ v / n ** 2)
    return p, float(np.linalg.norm(colour - SHORE - p * v))


def choose_ramp(model, spread=None, bin_width=0.04, tol=4.0, max_spread=30.0):
    """The hues that lie within `tol` colour units of the line from the shore colour to the deep
    colour, one per `bin_width` of progress (the one closest to the line). Returns
    [(hue, progress)] sorted by progress. Neighbours can sit a gap apart where the hue table has
    no colour on the line; plan() dithers between them with probability, so the gap is never a step."""
    pred = predict(model)
    best = {}
    for h, c in pred.items():
        p, off = progress(c)
        if off <= tol and -0.03 <= p <= 1.03 and (spread is None or spread[h] <= max_spread):
            b = int(round(p / bin_width))
            if b not in best or (off, h) < best[b][:2]:
                best[b] = (off, h, p)
    return sorted(((h, p) for off, h, p in best.values()), key=lambda kv: kv[1])


if __name__ == "__main__":
    import argparse
    from guo import load_config
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("cmd", choices=("fit", "ramp"))
    ap.add_argument("png", nargs="?")
    a = ap.parse_args()
    inst = Path(load_config().client_data)
    model, spread = sand_stats(inst, read_hues(inst / "hues.mul"))
    if a.cmd == "ramp":
        for h, p in choose_ramp(model, spread):
            print(h, round(p, 3), predict(model)[h].round(0))
    else:
        import seabed as S
        m, info, score = measure(a.png, ramp=S.HUE_RAMP, model=model)
        g, o, rms = fit(m, model)
        print("lattice/shift", info, "corr", round(score, 4), "gain", g.round(3), "offset", o.round(1), "rms", round(rms, 2))
