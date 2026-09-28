"""from-outfit-lab: SpriteMotion's outfit-lab output (read only) as a uopack folder.

The outfit lab (SpriteMotion, games/ultima-online/outfit-lab) fits
generated textures onto original UO equipment silhouettes for body 400 and
writes one atlas per action and stored direction: atlases/aNN_dD.png, 256 px
per frame across, rows 0 body, 1 mask, then an original/new pair per manifest
item in order. D is the lab's facing 3..7 = the anim file's stored direction
0..4 (FACING in its build.py); the client mirrors the other three.

For each wearable item this writes, under the ids it is given:
  anim/body_NNNN/aAA_dD.json + frames   the "new" row, cropped to its alpha, with
                                        centre_x = 128 - left, centre_y = 192 - bottom
                                        (the lab pastes a frame at (128 - cx, 192 - h - cy))
  art/static_0xNNNN.png + .json         the item art (see below) and its tiledata:
                                        the original item's, renamed, anim = the new body
  gumps/gump_NNNNN.png + .json          the male paperdoll gump (50000 + body)

Art and gump: for clothing, the original static and the original paperdoll gump
(50000 + original anim) recoloured by a colour map learned from the atlases
themselves -- every pixel where the original and the new row overlap pairs an
original colour with its new one, so the map carries the lab's restyle to the
art the lab did not make. The lightsaber is not a recolour of the broadsword:
its static is the lab's generated lightsaber.png scaled to the broadsword's
height, and its gump is the broadsword gump through the colour map (flagged).
Backpack and familiar are viewer overlays in the lab, not wearable animations,
and the staff is an alternative weapon: all three are skipped.
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

# The lab's manifest order; wearable ones only, with the new names.
WEARABLE = {
    "sword": "astral lightsaber", "robe": "astral robe", "hair": "astral hair", "shirt": "astral shirt",
    "pants": "astral pants", "shoes": "astral shoes", "gloves": "astral gloves",
}
SKIPPED = {"staff": "an alternative weapon", "backpack": "a viewer overlay, not a wearable animation",
           "familiar": "a viewer overlay (a follower), not a wearable animation"}
FRAME = 256
ORIGIN = (128, 192)


def say(msg: str) -> None:
    print(f"[uopack] {msg}", flush=True)


def to15(rgb) -> int:
    r, g, b = (int(v) for v in rgb[:3])
    return ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)


def from15(c: int) -> tuple[int, int, int]:
    return ((c >> 10) & 31) * 255 // 31, ((c >> 5) & 31) * 255 // 31, (c & 31) * 255 // 31


class ColourMap:
    """Original 15-bit colour -> the mean new colour at the same pixels, over the atlases."""

    def __init__(self):
        import numpy as np

        self.np = np
        self.sum = {}
        self.count = {}

    def learn(self, orig, new) -> None:
        np = self.np
        m = (orig[:, :, 3] >= 128) & (new[:, :, 3] >= 128)
        if not m.any():
            return
        o = orig[m][:, :3].astype(np.int64) >> 3
        keys = (o[:, 0] << 10) | (o[:, 1] << 5) | o[:, 2]
        n = new[m][:, :3].astype(np.int64)
        uniq, inv = np.unique(keys, return_inverse=True)
        sums = np.zeros((len(uniq), 3), np.int64)
        np.add.at(sums, inv, n)
        counts = np.bincount(inv)
        for k, s, c in zip(uniq.tolist(), sums, counts.tolist()):
            if k in self.sum:
                self.sum[k] = self.sum[k] + s
                self.count[k] += c
            else:
                self.sum[k] = s.copy()
                self.count[k] = c

    def finish(self) -> None:
        np = self.np
        self.keys = np.array(sorted(self.sum), np.int64)
        self.rgb = np.array([from15(k) for k in self.keys.tolist()], np.float64)
        self.out = np.array([self.sum[k] / self.count[k] for k in self.keys.tolist()], np.float64)

    def apply(self, im):
        """Recolours an RGBA image: each colour to the new colour of the nearest learned one."""
        np = self.np
        a = np.array(im.convert("RGBA"))
        m = a[:, :, 3] >= 128
        px = a[m][:, :3].astype(np.float64)
        if len(px) and len(self.keys):
            uniq, inv = np.unique(px, axis=0, return_inverse=True)
            d = ((uniq[:, None, :] - self.rgb[None, :, :]) ** 2).sum(-1)
            mapped = self.out[d.argmin(1)]
            a[m, :3] = np.clip(mapped[inv.reshape(-1)], 0, 255).astype(np.uint8)
        from PIL import Image

        return Image.fromarray(a)


def unpack_originals(items: dict, out: Path, data: str | None) -> Path:
    """The original statics and paperdoll gumps, through uopack unpack (reads the install)."""
    orig = out.parent / f"{out.name}_originals"
    common = [sys.executable, str(HERE / "run.py"), "unpack"] + (["--from", data] if data else [])
    statics = ",".join(str(i["graphic"]) for i in items.values())
    gumps = ",".join(str(50000 + i["animId"]) for i in items.values())
    for what, ids in (("art", statics), ("gumps", gumps)):
        r = subprocess.run(common + ["--what", what, "--ids", ids, "--out", str(orig)], capture_output=True, text=True)
        if r.returncode != 0:
            raise SystemExit(f"unpack {what} failed: {r.stdout[-400:]}{r.stderr[-400:]}")
    return orig


def from_outfit_lab(args) -> int:
    import numpy as np
    from PIL import Image

    from guo import uoread

    lab = Path(args.lab)
    out = Path(args.out)
    ids = json.loads(Path(args.ids).read_text(encoding="utf-8"))
    manifest = json.loads((lab / "manifest.json").read_text(encoding="utf-8"))
    if manifest.get("body") != 400 or manifest.get("canvas") != FRAME or list(manifest.get("origin", [])) != list(ORIGIN):
        raise SystemExit("unexpected outfit-lab manifest (body 400, canvas 256, origin 128,192 expected)")
    order = [i["key"] for i in manifest["items"]]
    items = {i["key"]: i for i in manifest["items"] if i["key"] in WEARABLE}
    missing = [k for k in items if k not in ids]
    if missing:
        raise SystemExit(f"--ids has no ids for {missing}")
    for k, why in SKIPPED.items():
        if k in order:
            say(f"skipped {k}: {why}")

    maps = {k: ColourMap() for k in items}
    groups = 0
    frames_written = 0
    for action in manifest["actions"]:
        a = action["index"]
        for facing, view in action["views"].items():
            direction = int(facing) - 3
            atlas = np.array(Image.open(lab / view["atlas"]).convert("RGBA"))
            n = view["count"]
            for k, item in items.items():
                j = order.index(k)
                body = ids[k]["body"]
                folder = out / "anim" / f"body_{body:04d}"
                folder.mkdir(parents=True, exist_ok=True)
                stem = f"a{a:02d}_d{direction}"
                entries = []
                for f in range(n):
                    orig = atlas[256 * (2 + 2 * j):256 * (3 + 2 * j), 256 * f:256 * (f + 1)]
                    new = atlas[256 * (3 + 2 * j):256 * (4 + 2 * j), 256 * f:256 * (f + 1)]
                    maps[k].learn(orig, new)
                    ys, xs = np.nonzero(new[:, :, 3] >= 128)
                    if len(xs) == 0:
                        entries.append({"png": f"{stem}_f{f:02d}.png", "center_x": 0, "center_y": 0, "width": 0, "height": 0})
                        Image.new("RGBA", (1, 1)).save(folder / f"{stem}_f{f:02d}.png")
                        continue
                    x0, x1, y0, y1 = int(xs.min()), int(xs.max()) + 1, int(ys.min()), int(ys.max()) + 1
                    Image.fromarray(new[y0:y1, x0:x1]).save(folder / f"{stem}_f{f:02d}.png")
                    entries.append({"png": f"{stem}_f{f:02d}.png", "center_x": ORIGIN[0] - x0, "center_y": ORIGIN[1] - y1,
                                    "width": x1 - x0, "height": y1 - y0})
                    frames_written += 1
                (folder / f"{stem}.json").write_text(json.dumps(
                    {"kind": "anim", "body": body, "action": a, "direction": direction, "frames": entries}, indent=1),
                    encoding="utf-8")
                groups += 1
        if a % 5 == 0:
            say(f"action {a}: done")
    for m in maps.values():
        m.finish()

    orig = unpack_originals(items, out, args.data)
    tiles = uoread.TileData(Path(args.data) if args.data else _default_data())
    (out / "art").mkdir(parents=True, exist_ok=True)
    (out / "gumps").mkdir(parents=True, exist_ok=True)
    notes = {}
    for k, item in items.items():
        new_item, body = ids[k]["item"], ids[k]["body"]
        # Static art.
        src_static = orig / "art" / f"static_{item['graphic']:#06x}.png"
        with Image.open(src_static) as im:
            original_static = im.convert("RGBA")
        if k == "sword":
            # The generated saber lies flat, hilt left; the broadsword's art runs
            # hilt bottom-left to tip top-right. Turn it to that diagonal and fit
            # it in the broadsword's box.
            art = Image.open(lab / "lightsaber.png").convert("RGBA")
            art = art.crop(art.getbbox()).rotate(45, resample=Image.BICUBIC, expand=True)
            art = art.crop(art.getbbox())
            bx = original_static.getbbox()
            scale = min((bx[2] - bx[0]) / art.width, (bx[3] - bx[1]) / art.height)
            art = art.resize((max(1, round(art.width * scale)), max(1, round(art.height * scale))), Image.LANCZOS)
            notes[k] = ("static: the lab's generated lightsaber.png turned to the broadsword's diagonal and fitted to its box; "
                        "gump: the broadsword gump through the colour map")
        else:
            art = maps[k].apply(original_static)
            notes[k] = "static and gump: the originals recoloured by the atlas colour map"
        a = np.array(art)
        a[:, :, 3] = np.where(a[:, :, 3] >= 128, 255, 0)
        art = Image.fromarray(a)
        art.save(out / "art" / f"static_{new_item:#06x}.png")
        tile = dict(tiles.static(item["graphic"]) or {})
        tile.update({"name": WEARABLE[k], "anim": body})
        (out / "art" / f"static_{new_item:#06x}.json").write_text(json.dumps(
            {"kind": "static", "id": new_item, "png": f"static_{new_item:#06x}.png", "width": art.width,
             "height": art.height, "header": 0, "tiledata_write": tile}, indent=1), encoding="utf-8")
        # Male paperdoll gump.
        gid = 50000 + body
        src_gump = orig / "gumps" / f"gump_{50000 + item['animId']}.png"
        with Image.open(src_gump) as im:
            gump = maps[k].apply(im.convert("RGBA"))
        g = np.array(gump)
        g[:, :, 3] = np.where(g[:, :, 3] >= 128, 255, 0)
        gump = Image.fromarray(g)
        gump.save(out / "gumps" / f"gump_{gid}.png")
        (out / "gumps" / f"gump_{gid}.json").write_text(json.dumps(
            {"kind": "gump", "id": gid, "png": f"gump_{gid}.png", "width": gump.width, "height": gump.height}, indent=1),
            encoding="utf-8")

    (out / "uopack.json").write_text(json.dumps({
        "format": "guo/uopack@1", "tool": "tools/uopack from-outfit-lab",
        "from": "SpriteMotion outfit-lab (read only)", "title": manifest.get("title"),
        "items": {k: {**ids[k], "was": {"graphic": items[k]["graphic"], "anim": items[k]["animId"], "layer": items[k]["layer"]},
                      "name": WEARABLE[k], "notes": notes[k]} for k in items},
        "skipped": SKIPPED}, indent=2), encoding="utf-8")
    say(f"outfit lab -> {out}: {len(items)} items, {groups} animation groups, {frames_written} frames")
    return 0


def _default_data() -> Path:
    sys.path.insert(0, str(HERE.parent))
    from guo import load_config

    return load_config().client_data
