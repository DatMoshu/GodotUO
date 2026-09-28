r"""Bulk unpack and pack of UO art, gumps and animations, with JSON sidecars (Epic H, H2).

    python tools\uopack\run.py unpack --what art|land|gumps|anim|tiledata --ids 0x1B74,50581,400-410
                                      [--from <data dir>] --out <folder>
    python tools\uopack\run.py pack <folder> [--source <data dir>] [--stage <staged set> [--replace]]
                                      [--records <folder>]
    python tools\uopack\run.py from-dreadcrest <candidate folder> --out <folder>
                                      [--item 0xFFF0] [--body 849] [--gump-male 50849] [--gump-female 60849]
    python tools\uopack\run.py selftest

The UOFiddler-style loop: unpack writes one PNG per image and one sidecar .json
per asset; edit the PNGs (or add new ones with their own sidecar); pack reads
the folder back into AssetRecords (tools/guo/uorecord.py) and hands them to
tools/uodata_write, which writes them into a staged data set, never the
install. The folder layout:

    art/static_0x1b74.png + .json       kind static: id, width, height, header, tiledata {...}
    land/land_0x0003.png + .json        kind land: id, tail (UOP padding, hex), tiledata {...}
    gumps/gump_50581.png + .json        kind gump: id, width, height
    anim/body_0581/a00_d0.json          kind anim: body, action, direction, palette[256],
    anim/body_0581/a00_d0_f00.png ...   transparent_index, frames [{png, center_x, center_y, width, height}]
    tiledata/item_0x1b74.json           kind tiledata-item: id, fields (for items without art to unpack)

Colour and transparency follow the client: art and gumps are 15-bit colour, 0 is
transparent and opaque black is stored as the near-black 0x0421 (tools/guo/uoart.py);
an animation frame is palette indices, and a pixel no run covers is transparent.
Animation PNGs are indexed with the group's own palette plus one otherwise unused
index marked transparent, so their bytes survive a round trip exactly.

Byte identity. The client's original encoders leave bytes a PNG cannot carry:
static art rows are padded to 32 bits with leftover memory, and some animation
groups lay their frames out in their own order. So each sidecar records the
SHA-256 of the raw entry it came from and of its decoded pixels. `pack --source`
re-reads that entry: when the PNG's pixels still hash the same and the source
entry is unchanged, its original bytes are used as they are. Anything edited or
new is encoded fresh, and every record pack produces is decoded again and
compared pixel by pixel with its PNGs before anything is written.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import struct
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))

from guo import uoread  # noqa: E402
from guo.uorecord import AssetRecord  # noqa: E402
import uocodecs as C  # noqa: E402

FORMAT = 1


def say(msg: str) -> None:
    print(f"[uopack] {msg}", flush=True)


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def parse_ids(spec: str) -> list[int]:
    out: list[int] = []
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part[1:]:
            a, b = part.split("-", 1) if not part.startswith("-") else (part, part)
            out += range(int(a, 0), int(b, 0) + 1)
        else:
            out.append(int(part, 0))
    return out


def write_json(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=1) + "\n", encoding="utf-8")


# --- pixels <-> PNG --------------------------------------------------------------

def px16_to_png(path: Path, px: list[int], w: int, h: int, land: bool = False) -> None:
    from PIL import Image

    im = Image.new("RGBA", (w, h))
    im.putdata([(0, 0, 0, 0) if (c == 0 and not land) or (land and not uoart_in(i, w)) else C.color_rgba(c)
                for i, c in enumerate(px)])
    path.parent.mkdir(parents=True, exist_ok=True)
    im.save(path)


def uoart_in(i: int, w: int) -> bool:
    return C.uoart.in_diamond(i % w, i // w)


def png_to_px16(path: Path, land: bool = False) -> tuple[int, int, list[int]]:
    return C.uoart.png_pixels(path, land)


def pixel_hash16(w: int, h: int, px: list[int]) -> str:
    return sha(struct.pack("<ii", w, h) + struct.pack(f"<{len(px)}H", *px))


def anim_pixel_hash(group: C.AnimGroup) -> str:
    """What an animation group looks like: palette colour per covered pixel, centres, sizes.
    Palette order does not matter, only the colours a pixel shows."""
    hsh = hashlib.sha256()
    for f in group.frames:
        hsh.update(struct.pack("<hhhh", f.center_x, f.center_y, f.width, f.height))
        hsh.update(struct.pack(f"<{len(f.index)}i", *[-1 if i < 0 else group.palette[i] for i in f.index]))
    return hsh.hexdigest()


def anim_frame_png(path: Path, frame: C.AnimFrame, palette: list[int], clear: int) -> None:
    from PIL import Image

    w, h = max(frame.width, 1), max(frame.height, 1)
    im = Image.new("P", (w, h), clear)
    flat = []
    for c in palette:
        flat += C.color_rgba(c)[:3]
    im.putpalette(flat)
    if frame.width > 0 and frame.height > 0:
        im.putdata([clear if i < 0 else i for i in frame.index])
    path.parent.mkdir(parents=True, exist_ok=True)
    im.save(path, transparency=clear)


# --- unpack ------------------------------------------------------------------------

def unpack(args) -> int:
    data_dir = Path(args.data_from) if args.data_from else _default_data()
    out = Path(args.out)
    ids = parse_ids(args.ids) if args.ids else []
    what = args.what
    n = 0
    tiledata = None
    try:
        tiledata = uoread.TileData(data_dir)
    except (FileNotFoundError, ValueError):
        pass
    if what in ("art", "land"):
        art = uoread.Art(data_dir)
        for i in ids:
            raw = art.get_land(i) if what == "land" else art.get_static(i)
            if not raw:
                continue
            if what == "land":
                px, tail = C.decode_land(raw)
                name = f"land_{i:#06x}"
                px16_to_png(out / "land" / f"{name}.png", px, 44, 44, land=True)
                meta = {"kind": "land", "id": i, "png": f"{name}.png", "tail": tail.hex(),
                        "tiledata": tiledata.land(i) if tiledata else None,
                        "raw_sha256": sha(raw), "pixels_sha256": pixel_hash16(44, 44, px)}
                write_json(out / "land" / f"{name}.json", meta)
            else:
                w, h, px, header = C.decode_static(raw)
                name = f"static_{i:#06x}"
                px16_to_png(out / "art" / f"{name}.png", px, w, h)
                meta = {"kind": "static", "id": i, "png": f"{name}.png", "width": w, "height": h,
                        "header": header, "tiledata": tiledata.static(i) if tiledata else None,
                        "raw_sha256": sha(raw), "pixels_sha256": pixel_hash16(w, h, px)}
                write_json(out / "art" / f"{name}.json", meta)
            n += 1
    elif what == "gumps":
        gumps = uoread.Gumps(data_dir)
        for i in ids:
            e = gumps.get(i)
            if not e:
                continue
            w, h, rows = e
            px = C.decode_gump(rows, w, h)
            name = f"gump_{i}"
            px16_to_png(out / "gumps" / f"{name}.png", px, w, h)
            write_json(out / "gumps" / f"{name}.json",
                       {"kind": "gump", "id": i, "png": f"{name}.png", "width": w, "height": h,
                        "raw_sha256": sha(struct.pack("<II", w, h) + rows),
                        "pixels_sha256": pixel_hash16(w, h, px)})
            n += 1
    elif what == "anim":
        anim = uoread.Anim(data_dir)
        for body in ids:
            for action in range(uoread.anim_actions(body)):
                for direction in range(5):
                    raw = anim.get(body, action, direction)
                    if not raw:
                        continue
                    n += unpack_anim_group(out, body, action, direction, raw)
    elif what == "tiledata":
        if not tiledata:
            raise SystemExit(f"no tiledata.mul in {data_dir}")
        for i in ids:
            t = tiledata.static(i)
            if t is None:
                continue
            write_json(out / "tiledata" / f"item_{i:#06x}.json", {"kind": "tiledata-item", "id": i, "fields": t})
            n += 1
    write_json(out / "uopack.json", {"format": FORMAT, "tool": "tools/uopack"})
    say(f"unpacked {n} {what} asset(s) into {out}")
    return 0


def unpack_anim_group(out: Path, body: int, action: int, direction: int, raw: bytes) -> int:
    group = C.decode_anim(raw)
    used = set()
    for f in group.frames:
        used.update(i for i in f.index if i >= 0)
    free = [i for i in range(256) if i not in used]
    folder = out / "anim" / f"body_{body:04d}"
    stem = f"a{action:02d}_d{direction}"
    frames = []
    for k, f in enumerate(group.frames):
        png = f"{stem}_f{k:02d}.png"
        if free:
            anim_frame_png(folder / png, f, group.palette, free[0])
        else:  # all 256 indices in use: an RGBA PNG, mapped back through the palette on pack
            px = [0 if i < 0 else (group.palette[i] or C.uoart.NEAR_BLACK) for i in f.index]
            px16_to_png(folder / png, px, max(f.width, 1), max(f.height, 1))
        frames.append({"png": png, "center_x": f.center_x, "center_y": f.center_y,
                       "width": f.width, "height": f.height})
    write_json(folder / f"{stem}.json", {
        "kind": "anim", "body": body, "action": action, "direction": direction,
        "palette": group.palette, "transparent_index": free[0] if free else None,
        "frames": frames, "raw_sha256": sha(raw), "pixels_sha256": anim_pixel_hash(group)})
    return 1


# --- pack ------------------------------------------------------------------------------

class Packer:
    def __init__(self, folder: Path, source: Path | None):
        self.folder = folder
        self.source = source
        self.reused = 0
        self.encoded = 0
        self._art = self._gumps = self._anim = None

    def _src(self, kind: str):
        if not self.source:
            return None
        try:
            if kind in ("static", "land"):
                self._art = self._art or uoread.Art(self.source)
                return self._art
            if kind == "gump":
                self._gumps = self._gumps or uoread.Gumps(self.source)
                return self._gumps
            if kind == "anim":
                self._anim = self._anim or uoread.Anim(self.source)
                return self._anim
        except FileNotFoundError:
            return None
        return None

    def _reuse(self, meta: dict, pixels_sha: str, raw_of) -> bytes | None:
        """The source entry's own bytes, when the pixels are unchanged and so is the source."""
        if not meta.get("raw_sha256") or meta.get("pixels_sha256") != pixels_sha:
            return None
        raw = raw_of()
        if raw is not None and sha(raw) == meta["raw_sha256"]:
            self.reused += 1
            return raw
        return None

    def static(self, side: Path, meta: dict) -> list[AssetRecord]:
        w, h, px = png_to_px16(side.with_name(meta["png"]))
        ps = pixel_hash16(w, h, px)
        src = self._src("static")
        data = self._reuse(meta, ps, lambda: src.get_static(meta["id"]) if src else None)
        if data is None:
            data = C.encode_static(px, w, h, int(meta.get("header", 0)))
            self.encoded += 1
        _, _, back, _ = C.decode_static(data)
        if back != px:
            raise ValueError(f"{side.name}: static art does not decode back to its PNG")
        out = [AssetRecord("static", int(meta["id"]), data, {})]
        if meta.get("tiledata_write"):
            out.append(AssetRecord("tiledata-item", int(meta["id"]), b"", dict(meta["tiledata_write"])))
        return out

    def land(self, side: Path, meta: dict) -> list[AssetRecord]:
        w, h, px = png_to_px16(side.with_name(meta["png"]), land=True)
        if (w, h) != (44, 44):
            raise ValueError(f"{side.name}: land art must be 44x44, not {w}x{h}")
        ps = pixel_hash16(44, 44, px)
        src = self._src("land")
        data = self._reuse(meta, ps, lambda: src.get_land(meta["id"]) if src else None)
        if data is None:
            data = C.encode_land(px, bytes.fromhex(meta.get("tail", "")))
            self.encoded += 1
        back, _ = C.decode_land(data)
        if back != px:
            raise ValueError(f"{side.name}: land art does not decode back to its PNG")
        return [AssetRecord("land", int(meta["id"]), data, {})]

    def gump(self, side: Path, meta: dict) -> list[AssetRecord]:
        w, h, px = png_to_px16(side.with_name(meta["png"]))
        ps = pixel_hash16(w, h, px)
        src = self._src("gump")

        def raw_of():
            e = src.get(meta["id"]) if src else None
            return struct.pack("<II", e[0], e[1]) + e[2] if e else None

        data = self._reuse(meta, ps, raw_of)
        if data is None:
            data = struct.pack("<II", w, h) + C.encode_gump(px, w, h)
            self.encoded += 1
        gw, gh = struct.unpack_from("<II", data, 0)
        if (gw, gh) != (w, h) or C.decode_gump(data[8:], w, h) != px:
            raise ValueError(f"{side.name}: gump does not decode back to its PNG")
        return [AssetRecord("gump", int(meta["id"]), data, {"width": w, "height": h})]

    def anim(self, side: Path, meta: dict) -> list[AssetRecord]:
        group = anim_group_from_folder(side, meta)
        ps = anim_pixel_hash(group)
        src = self._src("anim")
        data = self._reuse(meta, ps, lambda: src.get(meta["body"], meta["action"], meta["direction"])
                           if src else None)
        if data is None:
            data = C.encode_anim(group)
            self.encoded += 1
        if anim_pixel_hash(C.decode_anim(data)) != ps:
            raise ValueError(f"{side.name}: animation group does not decode back to its PNGs")
        return [AssetRecord("anim", int(meta["body"]), data,
                            {"action": int(meta["action"]), "direction": int(meta["direction"])})]

    def tiledata(self, side: Path, meta: dict) -> list[AssetRecord]:
        return [AssetRecord("tiledata-item", int(meta["id"]), b"", dict(meta["fields"]))]


def anim_group_from_folder(side: Path, meta: dict) -> C.AnimGroup:
    """Frames from indexed PNGs (the unpacked form) or RGBA PNGs (new or repainted art)."""
    from PIL import Image

    palette = meta.get("palette")
    clear = meta.get("transparent_index")
    images = []
    for fr in meta["frames"]:
        with Image.open(side.with_name(fr["png"])) as im:
            im.load()
            images.append((fr, im.copy()))
    indexed = palette is not None and clear is not None and all(im.mode == "P" for _, im in images)
    if not indexed:
        palette, rgba_frames = build_palette([im.convert("RGBA") for _, im in images], palette)
    frames = []
    for k, (fr, im) in enumerate(images):
        w, h = int(fr["width"]), int(fr["height"])
        if w <= 0 or h <= 0:
            frames.append(C.AnimFrame(int(fr["center_x"]), int(fr["center_y"]), w, h, []))
            continue
        if im.size != (w, h):
            raise ValueError(f"{fr['png']}: {im.size[0]}x{im.size[1]} but the sidecar says {w}x{h}")
        if indexed:
            index = [-1 if i == clear else i for i in pixels(im)]
        else:
            index = rgba_frames[k]
        frames.append(C.AnimFrame(int(fr["center_x"]), int(fr["center_y"]), w, h, index))
    return C.AnimGroup(list(palette), frames)


def pixels(im) -> list:
    """Pillow 12 deprecates getdata() for get_flattened_data(); use whichever there is."""
    flat = getattr(im, "get_flattened_data", None)
    return list(flat() if flat else im.getdata())


def build_palette(images, palette: list[int] | None) -> tuple[list[int], list[list[int]]]:
    """Map RGBA frames onto a 256-colour palette: the sidecar's when every colour is
    in it, otherwise one built from the frames (median cut past 256 colours).
    Alpha below 128 is "not covered"; everything else is opaque, as the client draws it."""
    from PIL import Image

    colours: dict[int, int] = {}
    per_frame = []
    for im in images:
        px = []
        for r, g, b, a in pixels(im):
            if a < 128:
                px.append(-1)
            else:
                c = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)
                colours[c] = colours.get(c, 0) + 1
                px.append(c)
        per_frame.append(px)
    if palette and all(c in palette for c in colours):
        lookup = {}
        for i, c in enumerate(palette):
            lookup.setdefault(c, i)
        return list(palette), [[-1 if c < 0 else lookup[c] for c in px] for px in per_frame]
    if len(colours) <= 256:
        pal = sorted(colours, key=lambda c: -colours[c])
        pal += [0] * (256 - len(pal))
        lookup = {c: i for i, c in enumerate(pal[:len(colours)])}
        return pal, [[-1 if c < 0 else lookup[c] for c in px] for px in per_frame]
    # Too many colours: quantise all frames together to one shared 256-colour palette.
    strip_w = sum(im.size[0] for im in images)
    strip_h = max(im.size[1] for im in images)
    strip = Image.new("RGB", (strip_w, strip_h))
    x = 0
    for im in images:
        strip.paste(im.convert("RGB"), (x, 0))
        x += im.size[0]
    q = strip.quantize(colors=256, method=Image.Quantize.MEDIANCUT)
    raw_pal = q.getpalette()[:768]
    pal = [((raw_pal[i] >> 3) << 10) | ((raw_pal[i + 1] >> 3) << 5) | (raw_pal[i + 2] >> 3) for i in range(0, 768, 3)]
    out = []
    x = 0
    qdata = q.load()
    for im, px in zip(images, per_frame):
        w, h = im.size
        out.append([-1 if px[y * w + xx] < 0 else qdata[x + xx, y] for y in range(h) for xx in range(w)])
        x += w
    return pal, out


def collect(folder: Path, source: Path | None) -> tuple[list[AssetRecord], Packer]:
    packer = Packer(folder, source)
    records: list[AssetRecord] = []
    for side in sorted(folder.rglob("*.json")):
        if side.name == "uopack.json":
            continue
        meta = json.loads(side.read_text(encoding="utf-8"))
        kind = meta.get("kind")
        handler = {"static": packer.static, "land": packer.land, "gump": packer.gump,
                   "anim": packer.anim, "tiledata-item": packer.tiledata}.get(kind)
        if handler is None:
            continue
        records += handler(side, meta)
    return records, packer


def save_records(records: list[AssetRecord], out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    index = []
    for r in records:
        tag = f"{r.kind}_{r.id}" + (f"_a{r.meta['action']:02d}_d{r.meta['direction']}" if r.kind == "anim" else "")
        if r.data:
            (out / f"{tag}.bin").write_bytes(r.data)
        index.append({"kind": r.kind, "id": r.id, "meta": r.meta, "bin": f"{tag}.bin" if r.data else None,
                      "sha256": sha(r.data) if r.data else None})
    write_json(out / "records.json", {"format": FORMAT, "records": index})


def pack(args) -> int:
    folder = Path(args.folder)
    source = Path(args.source) if args.source else None
    records, packer = collect(folder, source)
    kinds = {}
    for r in records:
        kinds[r.kind] = kinds.get(r.kind, 0) + 1
    say(f"{len(records)} record(s) {kinds}: {packer.reused} reused from the source unchanged, "
        f"{packer.encoded} encoded; every one decoded back equal to its PNG(s)")
    if args.records:
        save_records(records, Path(args.records))
        say(f"records written to {args.records}")
    if args.stage:
        sys.path.insert(0, str(HERE.parent / "uodata_write"))
        import uodata as U  # tools/uodata_write

        stage = U.Stage(Path(args.stage), _default_data())
        for line in U.write_records(stage, records, replace=args.replace):
            say("wrote " + line)
        bad = 0
        for r in records:
            back = U.read_back(stage, r)
            if r.kind == "tiledata-item":
                ok = isinstance(back, dict) and all(back.get(k) == v for k, v in r.meta.items() if k in back)
            else:
                ok = back == r.data
            bad += 0 if ok else 1
        changed = stage.check_install_unchanged()
        say(f"staged set {args.stage}: {len(records) - bad}/{len(records)} read back equal; "
            f"install unchanged: {not changed}")
        return 0 if bad == 0 and not changed else 1
    return 0


def roundtrip(args) -> int:
    """unpack -> pack against the same source: every record must equal the source bytes."""
    data_dir = Path(args.data_from) if args.data_from else _default_data()
    records, packer = collect(Path(args.folder), None if args.no_reuse else data_dir)
    art = gumps = anim = None
    same = 0
    for r in records:
        if r.kind == "static":
            art = art or uoread.Art(data_dir)
            src = art.get_static(r.id)
        elif r.kind == "land":
            art = art or uoread.Art(data_dir)
            src = art.get_land(r.id)
        elif r.kind == "gump":
            gumps = gumps or uoread.Gumps(data_dir)
            e = gumps.get(r.id)
            src = struct.pack("<II", e[0], e[1]) + e[2] if e else None
        elif r.kind == "anim":
            anim = anim or uoread.Anim(data_dir)
            src = anim.get(r.id, r.meta["action"], r.meta["direction"])
        else:
            same += 1
            continue
        same += int(src == r.data)
    say(f"round trip: {same}/{len(records)} record(s) byte-identical to the source "
        f"({packer.reused} reused, {packer.encoded} re-encoded)")
    return 0 if same == len(records) else 1


def _default_data() -> Path:
    from guo.config import load_config

    return load_config().client_data


# --- Codex's Dreadcrest candidate -> a uopack folder ------------------------------------

def from_dreadcrest(args) -> int:
    """Turns build/uo_original_expansion/dreadcrest_wearable/ (read only) into a uopack
    folder under new ids: item art + tiledata, the two paperdoll gumps, and the
    equipment animation (one group per action/direction, frames and centres from
    frame-manifest.json)."""
    import shutil

    src = Path(args.candidate)
    out = Path(args.out)
    item, body = int(args.item, 0), int(args.body, 0)
    gm, gf = int(args.gump_male, 0), int(args.gump_female, 0)
    ui = {e["kind"] + str(e["source_reference_id"]): e for e in
          json.loads((src / "item-ui-manifest.json").read_text(encoding="utf-8"))}
    from PIL import Image

    # Item art: the static and its tiledata (flags, layer and the animation id from the source item).
    item_png = src / ui["item7028"]["png"].replace("\\", "/")
    (out / "art").mkdir(parents=True, exist_ok=True)
    shutil.copy2(item_png, out / "art" / f"static_{item:#06x}.png")
    with Image.open(item_png) as im:
        w, h = im.size
    # Tiledata: the source kite shield's (0x1B74) record when an install is at hand,
    # else the values the candidate's README records; renamed, and pointing at the new body.
    tile = {"flags": 0x444002, "weight": 7, "layer": 2, "count": 0, "hue": 0, "light": 0, "height": 1}
    try:
        tile.update(uoread.TileData(_default_data()).static(0x1B74) or {})
    except (FileNotFoundError, ValueError, OSError):
        pass
    tile.update({"name": "dreadcrest shield", "anim": body})
    write_json(out / "art" / f"static_{item:#06x}.json",
               {"kind": "static", "id": item, "png": f"static_{item:#06x}.png", "width": w, "height": h,
                "header": 0, "tiledata_write": tile})
    # Paperdoll gumps, male and female.
    (out / "gumps").mkdir(parents=True, exist_ok=True)
    for key, gid in (("paperdoll50581", gm), ("paperdoll60581", gf)):
        p = src / ui[key]["png"].replace("\\", "/")
        shutil.copy2(p, out / "gumps" / f"gump_{gid}.png")
        with Image.open(p) as im:
            w, h = im.size
        write_json(out / "gumps" / f"gump_{gid}.json",
                   {"kind": "gump", "id": gid, "png": f"gump_{gid}.png", "width": w, "height": h})
    # The equipment animation.
    frames = json.loads((src / "frame-manifest.json").read_text(encoding="utf-8"))
    groups: dict[tuple[int, int], list[dict]] = {}
    for f in frames:
        groups.setdefault((f["action"], f["direction"]), []).append(f)
    folder = out / "anim" / f"body_{body:04d}"
    folder.mkdir(parents=True, exist_ok=True)
    for (action, direction), fl in sorted(groups.items()):
        fl.sort(key=lambda f: f["frame"])
        stem = f"a{action:02d}_d{direction}"
        entries = []
        for f in fl:
            png = f"{stem}_f{f['frame']:02d}.png"
            shutil.copy2(src / f["png"].replace("\\", "/"), folder / png)
            entries.append({"png": png, "center_x": f["center_x"], "center_y": f["center_y"],
                            "width": f["width"], "height": f["height"]})
        write_json(folder / f"{stem}.json", {"kind": "anim", "body": body, "action": action,
                                             "direction": direction, "frames": entries})
    write_json(out / "uopack.json", {"format": FORMAT, "tool": "tools/uopack",
                                     "from": "dreadcrest candidate (read only)"})
    say(f"dreadcrest -> {out}: item {item:#x}, gumps {gm}/{gf}, body {body}, {len(groups)} animation groups")
    return 0


# --- CLI ----------------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    u = sub.add_parser("unpack")
    u.add_argument("--from", dest="data_from", help="a data folder (default UO_CLIENT_DATA; never written)")
    u.add_argument("--what", required=True, choices=["art", "land", "gumps", "anim", "tiledata"])
    u.add_argument("--ids", required=True, help="ids and ranges: 0x1B74,50581,400-410 (anim: body ids)")
    u.add_argument("--out", required=True)
    p = sub.add_parser("pack")
    p.add_argument("folder")
    p.add_argument("--source", help="the data folder the assets were unpacked from (reuse unchanged entries)")
    p.add_argument("--records", help="also write the records (bin + records.json) here")
    p.add_argument("--stage", help="write the records into this staged set (tools/uodata_write)")
    p.add_argument("--replace", action="store_true",
                   help="with --stage: overwrite MUL slots that already hold data (refused otherwise; a UOP name never is)")
    r = sub.add_parser("roundtrip", help="pack a freshly unpacked folder and compare with the source")
    r.add_argument("folder")
    r.add_argument("--from", dest="data_from")
    r.add_argument("--no-reuse", action="store_true",
                   help="encode everything fresh, to measure the encoders against the originals")
    d = sub.add_parser("from-dreadcrest")
    d.add_argument("candidate")
    d.add_argument("--out", required=True)
    d.add_argument("--item", default="0xFFF0")
    d.add_argument("--body", default="849")
    d.add_argument("--gump-male", default="50849")
    d.add_argument("--gump-female", default="60849")
    o = sub.add_parser("from-outfit-lab", help="SpriteMotion's outfit-lab output (read only) as a uopack folder")
    o.add_argument("lab", help="workspace/ultima-online/outfit-lab (manifest.json, atlases/)")
    o.add_argument("--out", required=True)
    o.add_argument("--ids", required=True, help='JSON {item key: {"item": id, "body": body}}')
    o.add_argument("--data", help="the install the originals are read from (default UO_CLIENT_DATA; never written)")
    sub.add_parser("selftest", help="synthetic round trips, no client data (CI)")
    args = ap.parse_args()
    if args.cmd == "unpack":
        return unpack(args)
    if args.cmd == "pack":
        return pack(args)
    if args.cmd == "roundtrip":
        return roundtrip(args)
    if args.cmd == "from-dreadcrest":
        return from_dreadcrest(args)
    if args.cmd == "from-outfit-lab":
        import outfit

        return outfit.from_outfit_lab(args)
    import test_uopack

    return test_uopack.main()


if __name__ == "__main__":
    sys.exit(main())
