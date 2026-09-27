#!/usr/bin/env python3
"""Export a world project (the editor's map and asset edits) as patched files.

A world project (docs/data_formats.md section 9, ADR-0011) holds whole
replaced map blocks as JSON, laid over the read-only install. This tool turns
it into files a server and a client can read, and checks them:

    python tools/world/run.py blocks [--project DIR]
    python tools/world/run.py export [--project DIR] [--out DIR] [--force]
    python tools/world/run.py verify [--project DIR] [--out DIR]

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

Both are generated, never committed: they are derived from the install.

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
from guo import uoart  # noqa: E402
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


def cmd_export(cfg, project: Path, out: Path, force: bool) -> int:
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
    if not blocks and not any(assets.values()):
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

    manifest["assets"] = {
        "land": [f"0x{i:04X}" for i, _ in assets["land"]],
        "statics": [f"0x{i:04X}" for i, _ in assets["statics"]],
        "gumps": [f"0x{i:04X}" for i, _ in assets["gumps"]],
        "hues": [i for i, _ in assets["hues"]],
        "files": files,
    }
    return 0


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


def cmd_verify(cfg, project: Path, out: Path) -> int:
    blocks = project_blocks(project)
    failures = verify_assets(cfg, project, out)
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


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("command", choices=["blocks", "export", "verify"])
    ap.add_argument("--project", type=Path, help="world project folder (default UO_WORLD_PROJECT)")
    ap.add_argument("--out", type=Path, help="export folder (default <project>/export)")
    ap.add_argument("--force", action="store_true", help="export a project made on another install")
    args = ap.parse_args()

    cfg = load_config()
    project = (args.project or cfg.world_project).resolve()
    if not (project / "project.json").exists():
        print(f"[world] not a world project: {project}")
        return 2
    out = (args.out or project / "export").resolve()

    if args.command == "blocks":
        return cmd_blocks(cfg, project)
    if args.command == "export":
        return cmd_export(cfg, project, out, args.force)
    return cmd_verify(cfg, project, out)


if __name__ == "__main__":
    sys.exit(main())
