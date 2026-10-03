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
    out = orient.transform(comps, op, table, p.get("recentre", False), p.get("keep_centre", False)) if op else comps
    return {"components": kit.comps_out(out), "notes": [] if table["wall"] else ["no remap table: ids unchanged"],
            "ms": kit.ms_since(t0)}


def op_mirror(ctx: Context, p: dict) -> dict:
    t0 = time.perf_counter()
    axis = p.get("axis", "x")
    comps = comps_in(p["components"])
    out = orient.transform(comps, "mirror_x" if axis == "x" else "mirror_y", ctx.table(), p.get("recentre", False), p.get("keep_centre", False))
    return {"components": kit.comps_out(out), "notes": [], "ms": kit.ms_since(t0)}


def op_import(ctx: Context, p: dict) -> dict:
    t0 = time.perf_counter()
    if "path" in p:
        data = Path(p["path"]).read_bytes()
        name = Path(p["path"]).name
    else:
        data = base64.b64decode(p["data"]) if p.get("base64") else p["data"].encode("utf-8")
        name = p.get("name", "")
    fmt = p.get("format") or formats.detect(name, data[:512])
    tiles = formats.read(fmt, data)
    if p.get("recentre", True):
        tiles = formats.recentre(tiles)
    out = {"components": kit.comps_out(formats.comps_of(tiles)), "format": fmt, "notes": [], "ms": kit.ms_since(t0)}
    if any(t.hue for t in tiles):
        out["hues"] = [t.hue for t in tiles]
    return out


def op_export(ctx: Context, p: dict) -> dict:
    t0 = time.perf_counter()
    fmt = p["format"]
    hues = p.get("hues") or []
    tiles = [formats.Tile(c.item, c.x, c.y, c.z, c.visible, hues[i] if i < len(hues) else 0)
             for i, c in enumerate(comps_in(p["components"]))]
    data = formats.write(fmt, tiles, p.get("name", "multi"))
    out = {"format": fmt, "bytes": len(data), "ms": kit.ms_since(t0)}
    if "path" in p:
        Path(p["path"]).parent.mkdir(parents=True, exist_ok=True)
        Path(p["path"]).write_bytes(data)
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
    except (DescriptionError, formats.FormatError, KeyError, ValueError) as e:
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
