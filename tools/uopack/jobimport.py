"""from-job: a SpriteMotion transfer artifact (read only) as a uopack folder.

A transfer artifact is one folder: transfer.json plus the cropped frame PNGs it
lists (SpriteMotion docs/transfer-artifact.md, kind spritemotion.transfer-artifact,
schema_version 1). GUO reads it by that documented contract with its own checks
here; no SpriteMotion code is vendored or imported.

    python tools\\uopack\\run.py from-job <artifact folder> --out <new folder> --body 849
                                 [--item 0xFFF1] [--allow-preview]

Writes, like from-outfit-lab:
  anim/body_NNNN/aAA_dD.json + frames   one group per (action, stored direction 0-4); RGBA
                                        frames, centre_x = 128 - left, centre_y = 192 - bottom
  art/static_0xNNNN.png + .json         with --item and equipment.item_art: the item art and
                                        equipment.tiledata (plus anim = --body) as tiledata_write
  tiledata/item_0xNNNN.json             with --item, tiledata but no item art
  uopack.json                           identity, provenance and acceptance as declared

Colour: frames stay RGBA as exported (an artifact is unquantized). `pack` maps a
group onto one palette, and quantizes one with more than 256 colours to one shared
15-bit palette (run.py build_palette); there is no second quantizer here. Alpha
below 128 is "not covered" there; premultiplied colour is un-premultiplied here.
"""
from __future__ import annotations

import hashlib
import json
import re
import struct
from pathlib import Path

KIND = "spritemotion.transfer-artifact"
VERSIONS = (1,)
CANVAS = (256, 256)
ANCHOR = (128, 192)
MIRROR_MAP = {"5": 3, "6": 2, "7": 1}
STORED = (0, 1, 2, 3, 4)
TILEDATA_KEYS = ("flags", "weight", "layer", "count", "hue", "light", "height", "name")
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


class Refused(Exception):
    """The artifact breaks the contract or GUO's import rules; the message says how, in plain words."""


def say(msg: str) -> None:
    print(f"[uopack] {msg}", flush=True)


def actions_for(body: int) -> int:
    """Actions an anim.idx body slot holds (data_formats section 14: 110, 65 or 175 entries / 5 directions)."""
    return 22 if body < 200 else 13 if body < 400 else 35


def safe_file(root: Path, rel, what: str) -> Path:
    if not isinstance(rel, str) or not rel:
        raise Refused(f"{what}: no file path")
    if "\\" in rel or rel.startswith("/") or re.match(r"^[A-Za-z]:", rel):
        raise Refused(f"{what}: '{rel}' is not a relative forward-slash path")
    if any(part in ("", ".", "..") for part in rel.split("/")):
        raise Refused(f"{what}: '{rel}' has an empty, '.' or '..' segment")
    p = (root / rel).resolve()
    if not p.is_relative_to(root):
        raise Refused(f"{what}: '{rel}' leads outside the artifact")
    if not p.is_file():
        raise Refused(f"{what}: '{rel}' is listed but not in the artifact")
    return p


def checked_bytes(root: Path, ref: dict, what: str) -> tuple[Path, bytes]:
    p = safe_file(root, ref.get("path") if "path" in ref else ref.get("png"), what)
    data = p.read_bytes()
    if hashlib.sha256(data).hexdigest() != str(ref.get("sha256", "")).lower():
        raise Refused(f"{what}: {p.name} does not match its sha256 (changed after export?)")
    return p, data


def png_size(data: bytes, what: str) -> tuple[int, int]:
    if data[:8] != PNG_MAGIC or data[12:16] != b"IHDR":
        raise Refused(f"{what}: not a PNG")
    return struct.unpack(">II", data[16:24])


def read_manifest(root: Path) -> dict:
    p = root / "transfer.json"
    if not p.is_file():
        raise Refused(f"{root}: no transfer.json, so not a SpriteMotion transfer artifact")
    try:
        m = json.loads(p.read_text(encoding="utf-8"))
    except (ValueError, UnicodeDecodeError) as e:
        raise Refused(f"transfer.json is not valid JSON ({e})") from None
    if not isinstance(m, dict) or m.get("schema") != KIND:
        raise Refused(f"transfer.json is not a SpriteMotion transfer artifact "
                      f"(schema is {m.get('schema')!r}, expected '{KIND}')" if isinstance(m, dict)
                      else "transfer.json is not an object")
    v = m.get("schema_version")
    if v not in VERSIONS:
        raise Refused(f"this artifact is schema version {v!r}; GUO reads version "
                      f"{', '.join(map(str, VERSIONS))} only. Export it with a SpriteMotion that writes "
                      f"version 1, or update tools/uopack/jobimport.py for the new version first")
    return m


def check_pixels(m: dict) -> dict:
    px = m.get("pixels") or {}
    canvas, anchor = px.get("canvas") or {}, px.get("anchor") or {}
    if (canvas.get("width"), canvas.get("height")) != CANVAS:
        raise Refused(f"canvas is {canvas.get('width')}x{canvas.get('height')}; the contract fixes 256x256")
    if (anchor.get("x"), anchor.get("y")) != ANCHOR:
        raise Refused(f"anchor is ({anchor.get('x')}, {anchor.get('y')}); the contract fixes (128, 192)")
    if px.get("alpha") not in ("straight", "premultiplied", "binary"):
        raise Refused(f"pixels.alpha {px.get('alpha')!r} is not straight, premultiplied or binary")
    q = px.get("quantization") or {}
    if q.get("policy") not in ("none", "per-animation-group"):
        raise Refused(f"pixels.quantization.policy {q.get('policy')!r} is not none or per-animation-group")
    return px


def check_animation(m: dict, body: int, allow_preview: bool) -> dict[tuple[int, int], dict[int, dict]]:
    """Every action's frames by (action, stored direction) -> index, complete and in range."""
    anim = m.get("animation") or {}
    if {str(k): v for k, v in (anim.get("mirror_map") or {}).items()} != MIRROR_MAP:
        raise Refused(f"animation.mirror_map is {anim.get('mirror_map')}; the contract fixes {MIRROR_MAP}")
    coverage = anim.get("coverage")
    if coverage not in ("preview", "current-action", "full"):
        raise Refused(f"animation.coverage {coverage!r} is not preview, current-action or full")
    if coverage == "preview" and not allow_preview:
        raise Refused("this is a preview export (coverage: preview); GUO imports finished exports only. "
                      "Re-export the job in full mode, or pass --allow-preview to import it for a test")
    actions = anim.get("actions") or []
    if not actions:
        raise Refused("animation.actions is empty: nothing to import")
    limit = actions_for(body)
    want: dict[int, int] = {}
    for a in actions:
        n, count, dirs = a.get("action"), a.get("frame_count"), a.get("directions")
        if not isinstance(n, int) or not 0 <= n < limit:
            raise Refused(f"action {n!r}: body {body} holds actions 0-{limit - 1}")
        if n in want:
            raise Refused(f"action {n} is listed twice")
        if not isinstance(count, int) or count < 1:
            raise Refused(f"action {n}: frame_count {count!r} is not a positive number")
        if sorted(dirs or []) != list(STORED):
            raise Refused(f"action {n}: stored directions {dirs}; GUO needs every one of 0-4 "
                          f"(5-7 are mirrored by the client)")
        want[n] = count
    groups: dict[tuple[int, int], dict[int, dict]] = {}
    for f in m.get("frames") or []:
        key, i = (f.get("action"), f.get("direction")), f.get("index")
        where = f"frame a{key[0]} d{key[1]} #{i}"
        if key[0] not in want or key[1] not in STORED:
            raise Refused(f"{where}: not an action and stored direction the manifest lists")
        if not isinstance(i, int) or not 0 <= i < want[key[0]]:
            raise Refused(f"{where}: index outside 0-{want[key[0]] - 1}")
        if i in groups.setdefault(key, {}):
            raise Refused(f"{where}: listed twice")
        groups[key][i] = f
    for n, count in want.items():
        for d in STORED:
            missing = [i for i in range(count) if i not in groups.get((n, d), {})]
            if missing:
                raise Refused(f"action {n} direction {d}: frames {missing} are missing")
    return groups


def frame_image(root: Path, f: dict, alpha: str):
    """The checked frame as an RGBA Pillow image and its centre, or None for an empty frame."""
    from PIL import Image
    import io

    where = f"frame a{f['action']} d{f['direction']} #{f['index']}"
    if f.get("empty"):
        if any(k in f for k in ("png", "sha256", "crop", "centre")):
            raise Refused(f"{where}: an empty frame carries no png, sha256, crop or centre")
        return None
    c = f.get("crop") or {}
    left, top, right, bottom = (c.get(k) for k in ("left", "top", "right", "bottom"))
    if not all(isinstance(v, int) for v in (left, top, right, bottom)) or not (
            0 <= left < right <= CANVAS[0] and 0 <= top < bottom <= CANVAS[1]):
        raise Refused(f"{where}: crop {c} is not a box on the 256x256 canvas")
    centre = (ANCHOR[0] - left, ANCHOR[1] - bottom)
    stated = f.get("centre")
    if stated is not None and (stated.get("x"), stated.get("y")) != centre:
        raise Refused(f"{where}: centre ({stated.get('x')}, {stated.get('y')}) does not match its crop {centre}")
    _, data = checked_bytes(root, f, where)
    if png_size(data, where) != (right - left, bottom - top):
        raise Refused(f"{where}: the PNG is {png_size(data, where)} but its crop is {right - left}x{bottom - top}")
    with Image.open(io.BytesIO(data)) as im:
        im = im.convert("RGBA")
    if alpha == "premultiplied":
        from run import pixels
        px = [(min(255, r * 255 // a), min(255, g * 255 // a), min(255, b * 255 // a), a) if a else (0, 0, 0, 0)
              for r, g, b, a in pixels(im)]
        im.putdata(px)
    return im, centre


def write_json(path: Path, obj) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=1) + "\n", encoding="utf-8")


def from_job(args) -> int:
    try:
        return _from_job(args)
    except Refused as e:
        say(f"refused: {e}")
        return 2


def _from_job(args) -> int:
    from PIL import Image

    root = Path(args.artifact).resolve()
    out = Path(args.out).resolve()
    body = int(args.body, 0)
    item = int(args.item, 0) if args.item else None
    if not root.is_dir():
        raise Refused(f"{args.artifact} is not a folder")
    if out == root or out.is_relative_to(root):
        raise Refused("--out is inside the artifact; the artifact is only read")
    if out.exists() and any(out.iterdir()):
        raise Refused(f"--out {args.out} is not empty; give a new or empty folder")
    if not 0 <= body < 0x10000:
        raise Refused(f"--body {body} is not an animation body id")

    m = read_manifest(root)
    px = check_pixels(m)
    groups = check_animation(m, body, args.allow_preview)
    equip = m.get("equipment") or {}
    tile = dict(equip.get("tiledata") or {})
    if set(tile) - set(TILEDATA_KEYS):
        raise Refused(f"equipment.tiledata has keys GUO does not write: {sorted(set(tile) - set(TILEDATA_KEYS))}")
    if "layer" in equip and "layer" in tile and equip["layer"] != tile["layer"]:
        raise Refused(f"equipment.layer {equip['layer']} and equipment.tiledata.layer {tile['layer']} differ")
    if "layer" in equip:
        tile.setdefault("layer", equip["layer"])

    # Read and check every frame before writing anything.
    images = {key: {i: frame_image(root, f, px["alpha"]) for i, f in sorted(fr.items())}
              for key, fr in sorted(groups.items())}
    art = None
    if equip.get("item_art"):
        p, data = checked_bytes(root, equip["item_art"], "equipment.item_art")
        png_size(data, "equipment.item_art")
        with Image.open(p) as im:
            art = im.convert("RGBA")

    folder = out / "anim" / f"body_{body:04d}"
    folder.mkdir(parents=True, exist_ok=True)
    frames_written = 0
    for (action, direction), fr in images.items():
        stem = f"a{action:02d}_d{direction}"
        entries = []
        for i, got in fr.items():
            png = f"{stem}_f{i:02d}.png"
            if got is None:
                Image.new("RGBA", (1, 1)).save(folder / png)
                entries.append({"png": png, "center_x": 0, "center_y": 0, "width": 0, "height": 0})
                continue
            im, (cx, cy) = got
            im.save(folder / png)
            entries.append({"png": png, "center_x": cx, "center_y": cy, "width": im.width, "height": im.height})
            frames_written += 1
        write_json(folder / f"{stem}.json", {"kind": "anim", "body": body, "action": action,
                                             "direction": direction, "frames": entries})

    wrote_item = None
    if item is not None and art is not None:
        a = art.split()[3].point(lambda v: 255 if v >= 128 else 0)
        art.putalpha(a)
        (out / "art").mkdir(exist_ok=True)
        art.save(out / "art" / f"static_{item:#06x}.png")
        write_json(out / "art" / f"static_{item:#06x}.json",
                   {"kind": "static", "id": item, "png": f"static_{item:#06x}.png", "width": art.width,
                    "height": art.height, "header": 0, "tiledata_write": {**tile, "anim": body}})
        wrote_item = "art + tiledata"
    elif item is not None:
        write_json(out / "tiledata" / f"item_{item:#06x}.json",
                   {"kind": "tiledata-item", "id": item, "fields": {**tile, "anim": body}})
        wrote_item = "tiledata only (the artifact has no item art)"
    elif art is not None or equip.get("tiledata"):
        say("the artifact has item art or tiledata; pass --item to import them too")

    write_json(out / "uopack.json", {
        "format": "guo/uopack@1", "tool": "tools/uopack from-job",
        "from": f"SpriteMotion transfer artifact v{m['schema_version']} (read only)",
        "manifest_sha256": hashlib.sha256((root / "transfer.json").read_bytes()).hexdigest(),
        "identity": m.get("identity"), "coverage": m["animation"]["coverage"],
        "pixels": {"alpha": px["alpha"], "quantization": px["quantization"]},
        "body": body, "item": item,
        "paperdoll": equip.get("paperdoll"),
        "acceptance": m.get("acceptance"), "provenance": m.get("provenance")})
    say(f"transfer artifact -> {out}: body {body}, {len(images)} animation groups, {frames_written} frames"
        + (f", item {item:#x} {wrote_item}" if wrote_item else "")
        + ("; PREVIEW coverage, imported for a test only" if m["animation"]["coverage"] == "preview" else ""))
    return 0
