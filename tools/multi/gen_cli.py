"""The generators as JSON operations: one request in, one result out (run.py autowall, roof, stairs, house,
rotate, mirror, import, export, styles; and `serve`, which answers one JSON request per line so the editor
keeps one process and regenerates on every slider change).

A request is the generator's parameter dict; `style` names a style (tools/multi/styles/*.json, then the
user's own build/multi/styles/*.json). The answer carries `components`, `notes`, `problems`, `ms`, or `error`.
Nothing here reads the client install except `preview`; mined ids live in the user's build folder.
"""
from __future__ import annotations

import base64
import json
import sys
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import formats  # noqa: E402
import kit  # noqa: E402
import orient  # noqa: E402
import styles as S  # noqa: E402
from generate import DescriptionError  # noqa: E402
from multifile import Component  # noqa: E402

OPS = ("styles", "house", "autowall", "roof", "stairs", "rotate", "mirror", "import", "export", "validate-style")
REVIEWED_TEXT = ("txt-fiddler", "uoa-fiddler", "wsc-fiddler", "csv-fiddler")


class Context:
    """Styles and the remap table, loaded once and reused across requests."""

    def __init__(self, build: Path | None = None, style_files: list[Path] | None = None, user: bool = True):
        self.build = Path(build) if build else None
        self.style_files = style_files or []
        self.user = user
        self._styles: dict | None = None
        self._cat: S.StyleCatalogue | None = None
        self._table: dict | None = None

    @property
    def styles(self) -> dict:
        if self._styles is None:
            self._styles = S.load_styles(self.style_files, self.build, self.user)
        return self._styles

    def catalogue(self) -> S.StyleCatalogue:
        if self._cat is None:
            self._cat = S.StyleCatalogue(self.styles)
        return self._cat

    def table(self) -> dict:
        if self._table is None:
            cached = self.build / "multi" / "orient" / "table.json" if self.build else None
            table = orient.table_from_styles(self.styles)
            if cached and cached.exists():
                extra = json.loads(cached.read_text(encoding="utf-8"))
                for k in ("wall",):
                    table[k] = {**extra.get(k, {}), **table[k]}
                for base in table["map"]:
                    table["map"][base] = {**extra.get("map", {}).get(base, {}), **table["map"][base]}
                table["door_bases"] = sorted(set(table["door_bases"]) | set(extra.get("door_bases", [])))
            self._table = table
        return self._table


def comps_in(rows: list) -> list[Component]:
    return [Component(int(r[0], 0) if isinstance(r[0], str) else int(r[0]), int(r[1]), int(r[2]), int(r[3]),
                      not (len(r) > 4 and r[4] in (0, False))) for r in rows]


def op_styles(ctx: Context, p: dict) -> dict:
    out = []
    for key, st in sorted(ctx.styles.items()):
        out.append({"key": key, "name": st.get("name", key), "material": st.get("material", ""), "hues": st.get("hues", []),
                    "z": {**S.DEFAULT_Z, **st.get("z", {})}, "notes": S.check_style(key, st),
                    "roofs": [k for k in ("gable", "hip") if (st.get("roof") or {}).get(k)] +
                             (["flat"] if (st.get("roof") or {}).get("flat") or st.get("floors") else []),
                    "stairs": [k for k in ("straight", "turned", "ladder") if (st.get("stairs") or {}).get(k)],
                    "windows": len(st.get("windows") or []), "floors": len(st.get("floors") or [])})
    return {"styles": out}


def op_validate_style(ctx: Context, p: dict) -> dict:
    st = ctx.styles[p["style"]]
    return {"notes": S.check_style(p["style"], st)}


def op_rotate(ctx: Context, p: dict) -> dict:
    t0 = time.perf_counter()
    comps = comps_in(p["components"])
    turns = int(p.get("turns", 1)) % 4
    op = ("rot90", "rot180", "rot270")[turns - 1] if turns else None
    table = ctx.table()
    if op and p.get("preserve_entries"): assert_entry_order(comps, op, table)
    out = orient.transform(comps, op, table, p.get("recentre", False), p.get("keep_centre", False)) if op else comps
    return {"components": kit.comps_out(out), "notes": [] if table["wall"] else ["no remap table: ids unchanged"],
            "ms": kit.ms_since(t0)}


def op_mirror(ctx: Context, p: dict) -> dict:
    t0 = time.perf_counter()
    axis = p.get("axis", "x")
    comps = comps_in(p["components"])
    if p.get("preserve_entries"): assert_entry_order(comps, "mirror_x" if axis == "x" else "mirror_y", ctx.table())
    out = orient.transform(comps, "mirror_x" if axis == "x" else "mirror_y", ctx.table(), p.get("recentre", False), p.get("keep_centre", False))
    return {"components": kit.comps_out(out), "notes": [], "ms": kit.ms_since(t0)}


def assert_entry_order(comps: list[Component], op: str, table: dict) -> None:
    """Opaque per-entry metadata needs a proven one-to-one transform, not equal counts alone."""
    expected = list(comps)
    for base in orient.base_ops(op):
        next_parts = []
        for component in expected:
            pieces = orient.step(base, component, table)
            if len(pieces) != 1:
                raise formats.FormatError('Transform would split imported TXT entries; raw flags need a one-to-one transform')
            next_parts.extend(pieces)
        expected = next_parts
    if expected != orient.transform(comps, op, table):
        raise formats.FormatError('Transform would reorder or join imported TXT entries; raw flags need a one-to-one transform')


def op_import(ctx: Context, p: dict) -> dict:
    t0 = time.perf_counter()
    if "path" in p:
        if p.get("format") in REVIEWED_TEXT and Path(p["path"]).stat().st_size > 4 * 1024 * 1024:
            raise formats.FormatError("Fiddler text exceeds 4 MiB")
        data = Path(p["path"]).read_bytes()
        name = Path(p["path"]).name
    else:
        data = base64.b64decode(p["data"]) if p.get("base64") else p["data"].encode("utf-8")
        name = p.get("name", "")
    fmt = p.get("format") or formats.detect(name, data[:512])
    tiles = formats.read(fmt, data)
    if fmt == "wsc-fiddler" and "dynamic_ids" in p:
        dynamic_ids = p["dynamic_ids"]
        if not isinstance(dynamic_ids,list) or len(dynamic_ids)>65535 or any(type(i) is not int or not 0<=i<=65535 for i in dynamic_ids):
            raise formats.FormatError("WSC dynamic_ids must be a bounded list of uint16 decimal IDs")
        tiles = formats.read_fiddler_wsc(data.decode("utf-8-sig","replace"),set(dynamic_ids))
    if p.get("recentre", fmt not in REVIEWED_TEXT):
        tiles = formats.recentre(tiles)
    if fmt in REVIEWED_TEXT:
        (formats.write_fiddler_csv if fmt=="csv-fiddler" else formats.write_fiddler_txt)(tiles)  # validates centered int16 bounds too
    out = {"components": kit.comps_out(formats.comps_of(tiles)), "format": fmt, "notes": [], "ms": kit.ms_since(t0)}
    if any(t.hue for t in tiles):
        out["hues"] = [t.hue for t in tiles]
    if fmt in REVIEWED_TEXT: out["text_flags"] = [t.text_flags for t in tiles]
    if fmt=="csv-fiddler": out["text_extra"]=[t.text_extra for t in tiles]
    return out


def op_export(ctx: Context, p: dict) -> dict:
    t0 = time.perf_counter()
    fmt = p["format"]
    hues = p.get("hues") or []
    flags = p.get("text_flags") or []
    extra = p.get("text_extra") or []
    if fmt in REVIEWED_TEXT:
        rows = p["components"]
        if not isinstance(rows, list) or not 1 <= len(rows) <= 65535:
            raise formats.FormatError("Fiddler text requires 1 to 65535 components")
        if any(not isinstance(row, list) or len(row) not in (4, 5) or any(type(value) is not int for value in row[:4])
               or (len(row) == 5 and row[4] not in (0, 1)) for row in rows):
            raise formats.FormatError("Fiddler text components require integer ID/X/Y/Z and optional 0/1 visibility; use text_flags for raw flags")
        if "text_flags" in p and (not isinstance(p["text_flags"], list) or len(p["text_flags"]) != len(rows)):
            raise formats.FormatError("Fiddler text text_flags must match the component count")
        if "text_extra" in p and (not isinstance(p["text_extra"],list) or len(p["text_extra"])!=len(rows)
                or any(type(value) is not int or not -(2**31)<=value<2**31 for value in p["text_extra"])):
            raise formats.FormatError("Fiddler text_extra must be an int32 list matching components")
    tiles = [formats.Tile(c.item, c.x, c.y, c.z, c.visible, hues[i] if i < len(hues) else 0,
                         text_flags=flags[i] if i < len(flags) else None, text_extra=extra[i] if i<len(extra) else None)
             for i, c in enumerate(comps_in(p["components"]))]
    data = formats.write(fmt, tiles, p.get("name", "multi"))
    out = {"format": fmt, "bytes": len(data), "ms": kit.ms_since(t0)}
    if "path" in p:
        target = Path(p["path"])
        if fmt in REVIEWED_TEXT:
            from guo import load_config
            cfg = load_config()
            roots = [Path(value).resolve() for value in (cfg.client_data, cfg.client_data_env, cfg.client_data_setting) if str(value).strip() not in ('', '.')]
            def guard():
                if not roots: raise formats.FormatError('Configure UO_CLIENT_DATA before writing; the read-only installation boundary must be known')
                if target.is_symlink() or any(target.resolve().is_relative_to(root) for root in roots):
                    raise formats.FormatError('Fiddler text output cannot write into the read-only installation or through a file link')
            guard(); target.parent.mkdir(parents=True, exist_ok=True)
            temporary = target.with_name(target.name + '.tmp-' + uuid.uuid4().hex)
            try:
                temporary.write_bytes(data); guard(); temporary.replace(target)
            finally: temporary.unlink(missing_ok=True)
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        out["path"] = p["path"]
    elif fmt == "uoab":
        out["data"] = base64.b64encode(data).decode("ascii")
        out["base64"] = True
    else:
        out["data"] = data.decode("utf-8")
    return out


def run_op(ctx: Context, op: str, p: dict) -> dict:
    """One request. Errors a user can fix come back as {"error": ...}, not exceptions."""
    try:
        if op in ("house", "autowall", "roof", "stairs"):
            p = dict(p)
            if "style" not in p:
                p["style"] = sorted(ctx.styles)[0]
            if p["style"] not in ctx.styles:
                raise DescriptionError(f"style '{p['style']}' is not known (have {sorted(ctx.styles)})")
            return getattr(kit, op)(ctx.catalogue(), p)
        fn = {"styles": op_styles, "rotate": op_rotate, "mirror": op_mirror, "import": op_import,
              "export": op_export, "validate-style": op_validate_style}.get(op)
        if fn is None:
            return {"error": f"unknown op '{op}' (have {', '.join(OPS)})"}
        return fn(ctx, p)
    except (DescriptionError, formats.FormatError, KeyError, ValueError, OSError) as e:
        return {"error": f"{type(e).__name__}: {e}"}


def serve(ctx: Context, stdin=None, stdout=None) -> int:
    """Line protocol: `{"op": "house", ...params}` per line in, one JSON line out. A blank line or
    {"op": "quit"} ends it."""
    stdin, stdout = stdin or sys.stdin, stdout or sys.stdout
    for line in stdin:
        line = line.strip()
        if not line:
            break
        try:
            req = json.loads(line)
        except json.JSONDecodeError as e:
            stdout.write(json.dumps({"error": f"bad JSON: {e}"}) + "\n")
            stdout.flush()
            continue
        op = req.pop("op", "")
        if op == "quit":
            break
        res = run_op(ctx, op, req)
        if "id" in req:
            res["id"] = req["id"]
        stdout.write(json.dumps(res, separators=(",", ":")) + "\n")
        stdout.flush()
    return 0
