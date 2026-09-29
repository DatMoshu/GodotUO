#!/usr/bin/env python3
"""Export a world project (the editor's map and asset edits) as patched files.

A world project (docs/data_formats.md section 9, ADR-0011) holds whole
replaced map blocks as JSON, laid over the read-only install. This tool turns
it into files a server and a client can read, and checks them:

    python tools/world/run.py blocks [--project DIR]
    python tools/world/run.py export [--project DIR] [--out DIR] [--force]
    python tools/world/run.py verify [--project DIR] [--out DIR]
    python tools/world/run.py pack   [--project DIR] [--out FILE.zip]
    python tools/world/run.py apply-commands [--project DIR] --host H --port P [--dry-run]
    python tools/world/run.py ultimalive [--project DIR] [--out DIR] [--clear-ultimalive]

Or through the launcher:

    launchers\\pipeline\\04_world_export.bat

export copies, per facet the project touches, the install's land file
(map<N>LegacyMUL.uop or map<N>.mul), staidx<N>.mul and statics<N>.mul into
the export folder (default <project>/export) and patches the copies: each
replaced block's 64 land cells are written in place, and its statics are
appended to the statics copy with its index entry pointed at them. Blocks the
project does not replace are byte for byte the install's. It also writes:

  files_override.txt  for the client: upstream's UOFilesOverrideMap format
                      (settings.json "files_override", or -filesoverride)
  export.json         what was exported, from which project, onto which install

A project's assets/ (ADR-0020: replaced land art, static art and gumps as PNG,
hues as JSON) export as a patch set:

  verdata.mul         the replaced art and gumps in upstream's own patch
                      format, merged with the install's verdata.mul if it has
                      one (ours win on the same id). Every client applies a
                      non-empty verdata.mul, whatever its version.
  hues.mul            a copy of the install's with the replaced hues written in
  texmaps.mul         copies of the install's with each replaced texmap (the
  texidx.mul          texture sloped land draws with) appended and its index
                      entry pointed at it; the client ignores verdata texmaps

All are generated, never committed: they are derived from the install.

A server reads the export by listing the folder FIRST in its data directories
(ModernUO: dataDirectories in modernuo.json), ahead of the install.

It never writes into UO_CLIENT_DATA: an --out inside it is refused, and the
install is only ever opened read only. It refuses a project made on a
different install (its base fingerprint) unless --force.

verify reads the export back with an independent reader (tools/guo/uomap.py)
and checks every replaced block equals the project's and every other block
equals the install's.

Exit codes: 0 ok, 1 a check failed or the export was refused, 2 bad input.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import struct
import sys
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from guo import uoart, worldobjects  # noqa: E402
from backends import modernuo, servuo  # noqa: E402

BACKENDS = {modernuo.NAME: modernuo, servuo.NAME: servuo}
from guo.uomap import IDX_SIZE, MAP_BLOCK, STATIC_SIZE, Block, facet_files, install_fingerprint, open_facet  # noqa: E402


# --- the project ----------------------------------------------------------

def read_block(path: Path) -> tuple[int, int, int, Block]:
    j = json.loads(path.read_text(encoding="utf-8"))
    b = Block()
    for y, row in enumerate(j["land"]):
        for x, cell in enumerate(row.split()):
            tid, z = cell.split(":")
            b.land_id[y * 8 + x] = int(tid, 16)
            b.land_z[y * 8 + x] = int(z)
    for s in j["statics"]:
        b.statics.append((int(s["id"], 16), int(s["x"]), int(s["y"]), int(s["z"]), int(s["hue"], 16)))
    bx, by = j["block"]
    return int(j["facet"]), int(bx), int(by), b


def project_blocks(project: Path) -> dict[int, dict[tuple[int, int], Block]]:
    out: dict[int, dict[tuple[int, int], Block]] = {}
    for f in sorted((project / "blocks").glob("*/*.json")):
        facet, bx, by, b = read_block(f)
        out.setdefault(facet, {})[(bx, by)] = b
    return out


def inside(path: Path, parent: Path) -> bool:
    try:
        path.resolve().relative_to(parent.resolve())
        return True
    except ValueError:
        return False


def sha1(path: Path) -> str:
    h = hashlib.sha1()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# --- commands ---------------------------------------------------------------

def cmd_blocks(cfg, project: Path) -> int:
    blocks = project_blocks(project)
    if not blocks:
        print(f"[world] {project}: no blocks")
        return 0
    for facet, bs in sorted(blocks.items()):
        for (bx, by), b in sorted(bs.items()):
            print(f"[world] map{facet} block {bx},{by} (cells {bx*8},{by*8}..{bx*8+7},{by*8+7}): {len(b.statics)} statics")
    return 0


def ultimalive_root(given: Path | None) -> Path:
    import os
    return given or Path(os.environ.get("ProgramData", r"C:\\ProgramData"))


def check_ultimalive(out: Path, facets: dict, shard: str, root: Path, clear: bool) -> int:
    """A client that has played on an UltimaLive shard keeps its own copy of each map
    (<ProgramData>/<shard name>/map<N>.mul) and, once made, never refreshes it from the
    install or an export: it would go on showing the old map. Compare that copy with the
    exported map on the blocks the project replaces; report a stale one with the exact fix, or remove it
    when asked (--clear-ultimalive). Returns how many stale copies remain."""
    folder = root / shard
    stale = 0
    for facet, bs in sorted(facets.items()):
        if not (folder / f"map{facet}.mul").is_file():
            continue
        # The project's replaced blocks, land and statics, in the copy against the export.
        with open_facet(folder, facet) as copy, open_facet(out, facet) as exp:
            differ = 0
            for (bx, by) in bs:
                a, b = copy.read(bx, by), exp.read(bx, by)
                if a.land_id != b.land_id or a.land_z != b.land_z or Counter(a.statics) != Counter(b.statics):
                    differ += 1
        copy_path = folder / f"map{facet}.mul"
        if differ == 0:
            print(f"[world] UltimaLive copy {copy_path} already has this export's {len(bs)} block(s)")
            continue
        if clear:
            for name in (f"map{facet}.mul", f"staidx{facet}.mul", f"statics{facet}.mul"):
                (folder / name).unlink(missing_ok=True)
            print(f"[world] UltimaLive copy of map{facet} was stale ({differ} of {len(bs)} block(s)); removed. "
                  f"The client makes a fresh one at its next login to {shard}.")
            continue
        stale += 1
        print(f"[world] WARNING: the client's UltimaLive copy {copy_path} lacks {differ} of this export's {len(bs)} block(s).")
        print(f"[world]   A client that already played on '{shard}' keeps showing the OLD map until it is removed.")
        print(f"[world]   Fix: close the client, then run this export again with --clear-ultimalive")
        print(f"[world]        (or delete {folder}); the client copies the new map at its next login.")
    return stale


def cmd_export(cfg, project: Path, out: Path, force: bool, ul_shard: str = "GUO-Editor-Private",
               ul_root: Path | None = None, ul_clear: bool = False) -> int:
    data = cfg.client_data
    if inside(out, data):
        print(f"[world] REFUSED: {out} is inside UO_CLIENT_DATA ({data}). The install is never written.")
        return 1

    meta = json.loads((project / "project.json").read_text(encoding="utf-8"))
    base = meta.get("base", {}).get("fingerprint")
    now = install_fingerprint(data)
    if base != now and not force:
        print(f"[world] REFUSED: the project was made on another install (fingerprint {base}, this one {now}). --force to export anyway.")
        return 1

    blocks = project_blocks(project)
    assets = uoart.project_assets(project)
    try:
        objects = worldobjects.load(project)
    except (ValueError, KeyError) as ex:
        print(f"[world] REFUSED: shard/objects.json is not valid: {ex}")
        return 1
    if not blocks and not any(assets.values()) and not objects:
        print(f"[world] {project}: nothing to export")
        return 1

    out.mkdir(parents=True, exist_ok=True)
    override_lines = ["# GUO world export: UOFilesOverrideMap entries (file=path). See tools/world/run.py."]
    manifest = {
        "format": 1,
        "project": str(project),
        "project_name": meta.get("name"),
        "base_fingerprint": now,
        "created": datetime.now(timezone.utc).isoformat(),
        "facets": {},
    }

    for facet, bs in sorted(blocks.items()):
        sources = facet_files(data, facet)
        targets = [out / p.name for p in sources]
        for src, dst in zip(sources, targets):
            shutil.copyfile(src, dst)

        # Offsets come from the install (identical layout to the copies).
        with open_facet(data, facet) as fac:
            land, idx, statics = targets
            with land.open("r+b") as lf, idx.open("r+b") as xf, statics.open("r+b") as sf:
                sf.seek(0, 2)
                for (bx, by), b in sorted(bs.items()):
                    n = fac.number(bx, by)
                    lf.seek(fac.map_offset(n) + 4)  # keep the block header
                    lf.write(b"".join(struct.pack("<Hb", b.land_id[i], b.land_z[i]) for i in range(64)))

                    xf.seek(n * IDX_SIZE)
                    _, _, extra = struct.unpack("<iii", xf.read(IDX_SIZE))
                    xf.seek(n * IDX_SIZE)
                    if b.statics:
                        at = sf.tell()
                        sf.write(b"".join(struct.pack("<HBBbH", *s) for s in b.statics))
                        xf.write(struct.pack("<iii", at, len(b.statics) * STATIC_SIZE, extra))
                    else:
                        xf.write(struct.pack("<iii", -1, 0, extra))

        for src, dst in zip(sources, targets):
            override_lines.append(f"{src.name.lower()}={dst}")

        # UltimaLive (src/Game/UltimaLive.cs) copies map<N>.mul, never the UOP:
        # its UOP conversion is commented out upstream, so a UOP-only install
        # gives it nothing. Write the patched land as a MUL too (the same
        # 196-byte blocks, in block order) and point map<N>.mul at it.
        if land.suffix.lower() == ".uop":
            mul = out / f"map{facet}.mul"
            with open_facet(out, facet) as exp, mul.open("wb") as mf:
                for n in range(exp.width_blocks * exp.height_blocks):
                    at = exp.map_offset(n)
                    mf.write(exp.map[at:at + MAP_BLOCK])
            override_lines.append(f"map{facet}.mul={mul}")
            targets.append(mul)
        manifest["facets"][str(facet)] = {
            "blocks": [[bx, by] for (bx, by) in sorted(bs)],
            "files": {dst.name: {"sha1": sha1(dst), "bytes": dst.stat().st_size} for dst in targets},
        }
        print(f"[world] map{facet}: {len(bs)} block(s) -> {', '.join(t.name for t in targets)}")

    if any(assets.values()):
        rc = export_assets(data, assets, out, override_lines, manifest)
        if rc:
            return rc

    if objects:
        backend = BACKENDS[cfg_backend()]
        written = backend.export(objects, project.name, out)
        manifest["objects"] = {"backend": backend.NAME, "spawners": len(objects.spawners), "items": len(objects.items),
                               "files": {p.relative_to(out).as_posix(): sha1(p) for p in written}}
        print(f"[world] objects: {len(objects.spawners)} spawner(s), {len(objects.items)} item(s) -> {backend.NAME} files in {out / 'shard'}")

    if blocks:
        check_ultimalive(out, blocks, ul_shard, ultimalive_root(ul_root), ul_clear)

    (out / "files_override.txt").write_text("\n".join(override_lines) + "\n", encoding="utf-8")
    (out / "export.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"[world] export: {out}")
    return 0


def export_assets(data: Path, assets: dict, out: Path, override_lines: list[str], manifest: dict) -> int:
    patches: list[uoart.Patch] = []
    for kind in ("land", "statics", "gumps"):
        for id_, png in assets[kind]:
            w, h, px = uoart.png_pixels(png, land=kind == "land")
            if kind == "land":
                if (w, h) != (44, 44) or id_ >= uoart.LAND_COUNT:
                    print(f"[world] REFUSED: {png} is not a 44x44 land tile with an id below 0x4000")
                    return 1
                patches.append(uoart.Patch(uoart.VERDATA_ART, id_, uoart.encode_land(px)))
            elif kind == "statics":
                patches.append(uoart.Patch(uoart.VERDATA_ART, uoart.LAND_COUNT + id_, uoart.encode_static(px, w, h)))
            else:
                patches.append(uoart.Patch(uoart.VERDATA_GUMP, id_, uoart.encode_gump(px, w, h), (w << 16) | h))

    files = {}
    if patches:
        ours = {(p.file_id, p.block) for p in patches}
        base = data / "verdata.mul"
        kept = [p for p in uoart.read_verdata(base) if (p.file_id, p.block) not in ours] if base.is_file() else []
        verdata = out / "verdata.mul"
        uoart.write_verdata(verdata, kept + patches)
        override_lines.append(f"verdata.mul={verdata}")
        files[verdata.name] = {"sha1": sha1(verdata), "bytes": verdata.stat().st_size}
        print(f"[world] assets: {len(patches)} art/gump patch(es), {len(kept)} kept from the install -> verdata.mul")

    if assets["hues"]:
        hues = out / "hues.mul"
        shutil.copyfile(data / "hues.mul", hues)
        size = hues.stat().st_size
        with hues.open("r+b") as f:
            for _, path in assets["hues"]:
                e = uoart.read_hue(path)
                at = uoart.hue_offset(e.hue)
                if e.hue < 1 or at + uoart.HUE_BLOCK > size:
                    print(f"[world] REFUSED: hue {e.hue} is outside hues.mul")
                    return 1
                f.seek(at)
                f.write(e.pack())
        override_lines.append(f"hues.mul={hues}")
        files[hues.name] = {"sha1": sha1(hues), "bytes": hues.stat().st_size}
        print(f"[world] assets: {len(assets['hues'])} hue(s) -> hues.mul")

    if assets["texmaps"]:
        rc = export_texmaps(data, assets["texmaps"], out, override_lines, files)
        if rc:
            return rc

    manifest["assets"] = {
        "land": [f"0x{i:04X}" for i, _ in assets["land"]],
        "texmaps": [f"0x{i:04X}" for i, _ in assets["texmaps"]],
        "statics": [f"0x{i:04X}" for i, _ in assets["statics"]],
        "gumps": [f"0x{i:04X}" for i, _ in assets["gumps"]],
        "hues": [i for i, _ in assets["hues"]],
        "files": files,
    }
    return 0


def texterr_redirected(data: Path) -> set[int]:
    """Texmap indices TexTerr.def points at another entry (TexmapsLoader.Load): a replacement there never shows."""
    path = data / "TexTerr.def"
    out = set()
    if path.is_file():
        for line in path.read_text(encoding="latin-1").splitlines():
            line = line.split("#", 1)[0].strip()
            if "{" in line:
                try:
                    out.add(int(line.split()[0]))
                except ValueError:
                    pass
    return out


def export_texmaps(data: Path, texmaps: list, out: Path, override_lines: list[str], files: dict) -> int:
    idx_src, mul_src = data / "texidx.mul", data / "texmaps.mul"
    count = idx_src.stat().st_size // uoart.TEXIDX_RECORD
    redirected = texterr_redirected(data)
    encoded = []
    for id_, png in texmaps:
        size, px = uoart.texmap_pixels(png)
        if not size or id_ >= count:
            print(f"[world] REFUSED: {png} is not a 64x64 or 128x128 texmap with an index below {count}")
            return 1
        if id_ in redirected:
            print(f"[world] WARNING: TexTerr.def points texmap {id_} at another entry; the client will not show 0x{id_:04X}.png")
        encoded.append((id_, uoart.encode_texmap(px)))
    idx, mul = out / "texidx.mul", out / "texmaps.mul"
    shutil.copyfile(idx_src, idx)
    shutil.copyfile(mul_src, mul)
    with idx.open("r+b") as xf, mul.open("r+b") as mf:
        mf.seek(0, 2)
        for id_, raw in encoded:
            at = mf.tell()
            mf.write(raw)
            xf.seek(id_ * uoart.TEXIDX_RECORD)
            _, _, extra = struct.unpack("<iii", xf.read(uoart.TEXIDX_RECORD))
            xf.seek(id_ * uoart.TEXIDX_RECORD)
            xf.write(struct.pack("<iii", at, len(raw), extra))
    for f in (mul, idx):
        override_lines.append(f"{f.name}={f}")
        files[f.name] = {"sha1": sha1(f), "bytes": f.stat().st_size}
    print(f"[world] assets: {len(encoded)} texmap(s) -> texmaps.mul, texidx.mul")
    return 0


def verify_texmaps(cfg, texmaps: list, out: Path) -> int:
    """Each replaced entry decodes to its PNG; every other index entry, and the install's bytes, are untouched."""
    data = cfg.client_data
    a_idx, b_idx = (out / "texidx.mul").read_bytes(), (data / "texidx.mul").read_bytes()
    a_mul, b_mul = (out / "texmaps.mul").read_bytes(), (data / "texmaps.mul").read_bytes()
    failures = 0
    ours = {}
    for id_, png in texmaps:
        size, want = uoart.texmap_pixels(png)
        at, length, _ = struct.unpack_from("<iii", a_idx, id_ * uoart.TEXIDX_RECORD)
        got_size, have = uoart.decode_texmap(a_mul[at:at + length]) if at >= 0 and length > 0 else (0, [])
        if (got_size, have) != (size, want):
            failures += 1
            print(f"[world] FAIL texmap 0x{id_:04X}: does not decode to its PNG")
        ours[id_] = True
    other = sum(1 for n in range(len(b_idx) // uoart.TEXIDX_RECORD) if n not in ours and
                a_idx[n * uoart.TEXIDX_RECORD:(n + 1) * uoart.TEXIDX_RECORD] != b_idx[n * uoart.TEXIDX_RECORD:(n + 1) * uoart.TEXIDX_RECORD])
    if len(a_idx) != len(b_idx) or other or a_mul[:len(b_mul)] != b_mul:
        failures += 1
        print(f"[world] FAIL texmaps: {other} other index entr(ies) changed, or the install's texture data was altered")
    if not failures:
        print(f"[world] assets: {len(ours)} texmap(s) decode to the project's PNGs; every other texmap is the install's")
    return failures


def verify_assets(cfg, project: Path, out: Path) -> int:
    """Decode the patch set with the loaders' layouts and compare it with the project's files."""
    assets = uoart.project_assets(project)
    failures = 0
    if assets["land"] or assets["statics"] or assets["gumps"]:
        verdata = out / "verdata.mul"
        if not verdata.is_file():
            print(f"[world] FAIL assets: {verdata} is missing")
            return 1
        got = {(p.file_id, p.block): p for p in uoart.read_verdata(verdata)}
        ours = set()
        for kind in ("land", "statics", "gumps"):
            for id_, png in assets[kind]:
                w, h, want = uoart.png_pixels(png, land=kind == "land")
                if kind == "gumps":
                    key = (uoart.VERDATA_GUMP, id_)
                else:
                    key = (uoart.VERDATA_ART, id_ if kind == "land" else uoart.LAND_COUNT + id_)
                ours.add(key)
                p = got.get(key)
                have = None
                if p is not None:
                    if kind == "land":
                        have = uoart.decode_land(p.data)
                    elif kind == "statics":
                        gw, gh, px = uoart.decode_static(p.data)
                        have = px if (gw, gh) == (w, h) else None
                    elif (p.extra >> 16, p.extra & 0xFFFF) == (w, h):
                        have = uoart.decode_gump(p.data, w, h)
                if have != want:
                    failures += 1
                    why = "no patch" if p is None else "size differs" if have is None else \
                        f"{sum(a != b for a, b in zip(have, want))} pixels differ"
                    print(f"[world] FAIL {kind} 0x{id_:04X}: {why}")
        base = cfg.client_data / "verdata.mul"
        kept = 0
        if base.is_file():
            for p in uoart.read_verdata(base):
                if (p.file_id, p.block) in ours:
                    continue
                q = got.get((p.file_id, p.block))
                if q is None or q.data != p.data or q.extra != p.extra:
                    failures += 1
                    print(f"[world] FAIL the install's verdata patch {p.file_id}/{p.block} was not kept")
                kept += 1
        if failures == 0:
            print(f"[world] assets: {len(ours)} art/gump patch(es) decode to the project's PNGs exactly"
                  + (f"; the install's {kept} other patch(es) kept" if kept else ""))

    if assets["texmaps"]:
        failures += verify_texmaps(cfg, assets["texmaps"], out)

    if assets["hues"]:
        mine = {e.hue: e for e in (uoart.read_hue(p) for _, p in assets["hues"])}
        a = (out / "hues.mul").read_bytes()
        b = (cfg.client_data / "hues.mul").read_bytes()
        bad = other = 0
        for hue in range(1, len(a) // uoart.HUE_GROUP * 8 + 1):
            at = uoart.hue_offset(hue)
            block = a[at:at + uoart.HUE_BLOCK]
            if hue in mine:
                bad += block != mine[hue].pack()
            elif block != b[at:at + uoart.HUE_BLOCK]:
                other += 1
        headers = all(a[g * uoart.HUE_GROUP:g * uoart.HUE_GROUP + 4] == b[g * uoart.HUE_GROUP:g * uoart.HUE_GROUP + 4]
                      for g in range(len(a) // uoart.HUE_GROUP))
        if len(a) != len(b) or bad or other or not headers:
            failures += 1
            print(f"[world] FAIL hues.mul: {bad} project hue(s) wrong, {other} other hue(s) changed, "
                  f"sizes {len(a)}/{len(b)}, headers {'same' if headers else 'differ'}")
        else:
            print(f"[world] assets: {len(mine)} hue(s) match the project; every other hue matches the install")
    return failures


def cfg_backend() -> str:
    import os
    name = (os.environ.get("UO_SHARD_BACKEND") or "modernuo").lower()
    if name not in BACKENDS:
        raise SystemExit(f"[world] UO_SHARD_BACKEND={name}: no such backend (have {', '.join(BACKENDS)})")
    return name


def verify_objects(project: Path, out: Path) -> int:
    """Reads the backend's files back the way the server parses them and compares them with the model."""
    objects = worldobjects.load(project)
    if not objects:
        return 0
    backend = BACKENDS[cfg_backend()]
    spawners, items = backend.read_back(out, project.name)
    failures = 0
    got = {r.get("guid") or r.get("UniqueId"): r for r in spawners}
    for s in objects.spawners:
        r = got.pop(s.id, None)
        want = backend.spawner_record(s)
        if r != want:
            failures += 1
            print(f"[world] FAIL spawner {s.id}: {'missing' if r is None else 'differs from the model'}")
    for guid in got:
        failures += 1
        print(f"[world] FAIL spawner {guid} is exported but not in the model")
    want_items = sorted((i.map, i.type, i.item_id, {**i.props, **({"Hue": f"0x{i.hue:X}"} if i.hue else {})}, i.x, i.y, i.z)
                        for i in objects.items)
    have_items = sorted(items)
    if [(*w[:3], sorted(w[3].items()), *w[4:]) for w in want_items] != [(*h[:3], sorted(h[3].items()), *h[4:]) for h in have_items]:
        failures += 1
        print(f"[world] FAIL decoration: {len(have_items)} item(s) read back, model has {len(want_items)}, or they differ")
    manifest = json.loads((out / "shard" / "guo_objects.json").read_text(encoding="utf-8"))
    if {m["id"] for m in manifest["spawners"] + manifest["items"]} != {o.id for o in objects.spawners + objects.items}:
        failures += 1
        print("[world] FAIL guo_objects.json does not list exactly the model's objects")
    if failures == 0:
        print(f"[world] objects: {len(objects.spawners)} spawner(s) and {len(objects.items)} item(s) read back from the "
              f"{backend.NAME} files equal the model; the manifest lists all of them")
    return failures


def cmd_verify(cfg, project: Path, out: Path) -> int:
    blocks = project_blocks(project)
    failures = verify_assets(cfg, project, out) + verify_objects(project, out)
    for facet, bs in sorted(blocks.items()):
        with open_facet(out, facet) as exp, open_facet(cfg.client_data, facet) as inst:
            # Replaced blocks equal the project's.
            for (bx, by), want in sorted(bs.items()):
                got = exp.read(bx, by)
                same = (got.land_id == want.land_id and got.land_z == want.land_z
                        and Counter(got.statics) == Counter(want.statics))
                if not same:
                    failures += 1
                    print(f"[world] FAIL map{facet} block {bx},{by}: export differs from the project")

            # Every other block equals the install's: land bytes and statics bytes.
            replaced = {exp.number(bx, by) for (bx, by) in bs}
            land_diff = static_diff = 0
            for n in range(exp.width_blocks * exp.height_blocks):
                if n in replaced:
                    continue
                a, b = exp.map_offset(n), inst.map_offset(n)
                if exp.map[a:a + MAP_BLOCK] != inst.map[b:b + MAP_BLOCK]:
                    land_diff += 1
                ea, el = exp.statics_span(n)
                ia, il = inst.statics_span(n)
                if exp.sta[ea:ea + el] != inst.sta[ia:ia + il]:
                    static_diff += 1
            total = exp.width_blocks * exp.height_blocks - len(replaced)
            if land_diff or static_diff:
                failures += 1
                print(f"[world] FAIL map{facet}: {land_diff} land and {static_diff} statics blocks differ outside the project")
            else:
                print(f"[world] map{facet}: {len(bs)} replaced block(s) match the project; the other {total} match the install")

    print("[world] verify OK" if failures == 0 else f"[world] verify FAILED ({failures})")
    return 0 if failures == 0 else 1


PACK_README = """This is a GUO world pack: map and art edits made in the GUO editor.

It holds only the edits: changed 8x8 map blocks as JSON (blocks/), and
replaced art, gumps and hues as PNG and JSON (assets/). It holds NO Ultima
Online client data, so it can be sent to anyone. To use it you need your own
UO install and a GUO checkout.

Made from project "{name}" on {created}.
Base install: client {version}, fingerprint {fingerprint}.
Contents: {summary}.

To apply it (shard owner, or each player):

  1. Unzip this folder anywhere outside your UO install.
  2. python tools\\world\\run.py export --project <this folder> --out <export folder>
     python tools\\world\\run.py verify --project <this folder> --out <export folder>
     If export refuses because the pack was made on another install, the
     map files differ in size. Check you have the same client version, then
     add --force.
  3. Shard: list <export folder> FIRST in the shard's data directories
     (ModernUO: dataDirectories in modernuo.json) and restart it.
  4. Client: point settings.json "files_override" at
     <export folder>\\files_override.txt.

The export folder is made from your install. Do not send it on; send this
pack instead. See docs/wiki/Manage-Your-Shard-From-The-Editor.md.
"""


def cmd_pack(cfg, project: Path, out: Path) -> int:
    """Zip a project's edits (and nothing derived from the install) for sending to a shard owner."""
    import zipfile

    if inside(out, cfg.client_data):
        print(f"[world] REFUSED: {out} is inside UO_CLIENT_DATA ({cfg.client_data}). The install is never written.")
        return 1

    meta = json.loads((project / "project.json").read_text(encoding="utf-8"))
    blocks = sorted((project / "blocks").glob("*/*.json"))
    assets = uoart.project_assets(project)
    if assets["texmaps"]:
        print(f"[world] NOTE: {len(assets['texmaps'])} texmap(s) are export-only; store packs do not carry them yet")

    # Check every file parses and fits before anything is written, so a pack
    # that leaves this machine always exports.
    for f in blocks:
        try:
            read_block(f)
        except (ValueError, KeyError, IndexError, json.JSONDecodeError) as ex:
            print(f"[world] REFUSED: {f} is not a valid block file: {ex}")
            return 1
    for kind in ("land", "statics", "gumps"):
        for id_, png in assets[kind]:
            w, h, _ = uoart.png_pixels(png, land=kind == "land")
            limit = (44, 44) if kind == "land" else (1024, 1024) if kind == "statics" else (2048, 2048)
            if (kind == "land" and (w, h) != limit) or w > limit[0] or h > limit[1]:
                print(f"[world] REFUSED: {png} is {w}x{h}; {kind} must be {'exactly 44x44' if kind == 'land' else f'at most {limit[0]}x{limit[1]}'}")
                return 1
    for _, path in assets["hues"]:
        try:
            if len(uoart.read_hue(path).colors) != 32:
                raise ValueError("not 32 colours")
        except (ValueError, KeyError, json.JSONDecodeError) as ex:
            print(f"[world] REFUSED: {path} is not a valid hue file: {ex}")
            return 1

    files = [project / "project.json", *blocks]
    try:
        objects = worldobjects.load(project)
    except (ValueError, KeyError) as ex:
        print(f"[world] REFUSED: shard/objects.json is not valid: {ex}")
        return 1
    if objects:
        files.append(worldobjects.objects_path(project))
    for kind in ("land", "statics", "gumps", "hues"):
        files += [p for _, p in assets[kind]]
    if len(files) == 1:
        print(f"[world] {project}: nothing to pack")
        return 1

    counts = [f"{len(blocks)} map block(s)"] + ([f"{len(objects)} world object(s)"] if objects else []) + [f"{len(assets[k])} {k}" for k in ("land", "statics", "gumps", "hues") if assets[k]]
    note = PACK_README.format(
        name=meta.get("name"), created=datetime.now(timezone.utc).date().isoformat(),
        version=meta.get("base", {}).get("client_version"), fingerprint=meta.get("base", {}).get("fingerprint"),
        summary=", ".join(counts),
    )
    out.parent.mkdir(parents=True, exist_ok=True)
    root = project.name
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for f in files:
            z.write(f, f"{root}/{f.relative_to(project).as_posix()}")
        z.writestr(f"{root}/README.txt", note.replace("\n", "\r\n"))
    print(f"[world] pack: {', '.join(counts)} -> {out} ({out.stat().st_size} bytes)")
    return 0


def cmd_apply_commands(cfg, project: Path, host: str, port: int, dry_run: bool) -> int:
    """World objects onto a shard WITHOUT GUO's bridge: a GM client types the server's own commands.

    Only for ModernUO. The project keeps a record per shard of what this
    placed (shard/applied/<host>_<port>.json); removals use only that
    record's tags, so nothing the fallback did not place can be removed.
    """
    import os
    import subprocess
    from guo.process import no_activate

    try:
        objects = worldobjects.load(project)
    except (ValueError, KeyError) as ex:
        print(f"[world] REFUSED: shard/objects.json is not valid: {ex}")
        return 1
    rec_path = project / "shard" / "applied" / f"{host.replace(':', '_')}_{port}.json"
    record = json.loads(rec_path.read_text(encoding="utf-8")) if rec_path.is_file() else {"spawners": {}, "items": {}}
    commands, new_record, notes = modernuo.plan_commands(objects, record)
    for n in notes:
        print(f"[world] note: {n}")
    if not commands:
        print(f"[world] {host}:{port} already matches the project: nothing to type")
        return 0
    for c in commands:
        print(f"[world]   {c}")
    if dry_run:
        return 0

    home = cfg.build / "world_commands" / "client_home"
    shutil.rmtree(home, ignore_errors=True)
    (home / "cache").mkdir(parents=True)
    (home / "profiles").mkdir()
    (home / "settings.json").write_text(json.dumps({"profilespath": str(home / "profiles")}), encoding="utf-8")
    cmd = [str(cfg.godot_console_exe), "--headless", "--path", str(cfg.godot_project), "--", "--play"]
    for c in commands:
        cmd += ["--shard-command", c]
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": host, "UO_SHARD_PORT": str(port)}
    log = cfg.build / "world_commands" / "client.log"
    # Headless, the client captures no frame; it ends once every command has been answered.
    with log.open("w", encoding="utf-8", errors="replace") as f:
        proc = subprocess.Popen(cmd, stdout=f, stderr=subprocess.STDOUT, env=env, **no_activate())
        done = False
        for _ in range(1200):
            text = log.read_text(encoding="utf-8", errors="replace")
            if "shard commands done" in text or "shard commands FAILED" in text or proc.poll() is not None:
                done = "shard commands done" in text
                break
            if text.count("[GUO] shard command: [") >= len(commands):
                # The last command is printed before it is typed: wait for the
                # shard's answer to it (or 30 s) before ending the client.
                import time as _t
                for _ in range(60):
                    tail = log.read_text(encoding="utf-8", errors="replace").rsplit("[GUO] shard command: [", 1)[-1]
                    if "[GUO] shard says:" in tail:
                        break
                    _t.sleep(0.5)
                _t.sleep(2)
                done = True
                break
            import time as _t
            _t.sleep(0.5)
        if proc.poll() is None:
            proc.kill()
    if not done:
        print(f"[world] FAILED: the GM client did not get through the commands; see {log}")
        return 1
    if "Awaiting confirmation" in log.read_text(encoding="utf-8", errors="replace"):
        # A remove matched more than one object and the shard asked a GM to
        # confirm; nothing was removed. Tags are unique per placement, so this
        # means the shard holds objects this record does not describe.
        print(f"[world] FAILED: the shard asked to confirm a removal (more than one match); nothing removed. See {log}")
        return 1
    rec_path.parent.mkdir(parents=True, exist_ok=True)
    rec_path.write_text(json.dumps(new_record, indent=2) + "\n", encoding="utf-8")
    print(f"[world] {len(commands)} command(s) typed on {host}:{port}; record: {rec_path}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("command", choices=["blocks", "export", "verify", "pack", "apply-commands", "ultimalive"])
    ap.add_argument("--host", help="shard host (apply-commands)")
    ap.add_argument("--port", type=int, help="shard port (apply-commands)")
    ap.add_argument("--dry-run", action="store_true", help="print the commands only (apply-commands)")
    ap.add_argument("--project", type=Path, help="world project folder (default UO_WORLD_PROJECT)")
    ap.add_argument("--out", type=Path, help="export folder (default <project>/export)")
    ap.add_argument("--force", action="store_true", help="export a project made on another install")
    ap.add_argument("--ultimalive-shard", default=None,
                    help="UltimaLive shard name whose client map copies to check (export; default GUO_BRIDGE_SHARD or GUO-Editor-Private)")
    ap.add_argument("--ultimalive-root", type=Path, help="folder holding the copies (export; default %%ProgramData%%)")
    ap.add_argument("--clear-ultimalive", action="store_true",
                    help="remove a stale client UltimaLive map copy instead of only reporting it (export)")
    args = ap.parse_args()

    cfg = load_config()
    project = (args.project or cfg.world_project).resolve()
    if not (project / "project.json").exists():
        print(f"[world] not a world project: {project}")
        return 2
    if args.command == "ultimalive":
        # Only the check: is a client's UltimaLive map copy older than this export?
        import os
        shard = args.ultimalive_shard or os.environ.get("GUO_BRIDGE_SHARD") or "GUO-Editor-Private"
        stale = check_ultimalive((args.out or project / "export").resolve(), project_blocks(project), shard,
                                 ultimalive_root(args.ultimalive_root), args.clear_ultimalive)
        return 1 if stale else 0
    if args.command == "apply-commands":
        if not args.host or not args.port:
            print("[world] apply-commands needs --host and --port")
            return 2
        return cmd_apply_commands(cfg, project, args.host, args.port, args.dry_run)
    if args.command == "pack":
        return cmd_pack(cfg, project, (args.out or cfg.build / "world_pack" / f"{project.name}.zip").resolve())
    out = (args.out or project / "export").resolve()

    if args.command == "blocks":
        return cmd_blocks(cfg, project)
    if args.command == "export":
        import os
        shard = args.ultimalive_shard or os.environ.get("GUO_BRIDGE_SHARD") or "GUO-Editor-Private"
        return cmd_export(cfg, project, out, args.force, shard, args.ultimalive_root, args.clear_ultimalive)
    return cmd_verify(cfg, project, out)


if __name__ == "__main__":
    sys.exit(main())
