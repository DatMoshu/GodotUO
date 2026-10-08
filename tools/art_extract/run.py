r"""Extract the client's art into atlas pages the runtime can load (ADR-0034, data_formats section 36).

    python tools\art_extract\run.py export [--what art,land,gumps,texmaps,lights,anim] [--out DIR] [--from DATA]
    python tools\art_extract\run.py verify [--set DIR] [--what ...] [--from DATA]
    python tools\art_extract\run.py where

`export` reads the install with GUO's own decoders (tools/guo/uoread.py and uoart.py, the ones tools/uopack and
tools/world use) and writes 2048 RGBA8 PNG pages plus one index.json per class and a set.json into
UO_ART_EXTRACT_DIR. `--out` may only name that folder, a folder inside it, or one inside the repository's build/.
`verify` decodes every id again from the install and compares it with the page rectangle; the set is accepted
only at 100%. The set is derived from your own install: it stays on your machine and is never committed.

`pack-container` is for a shard owner's custom art: the same pieces sealed with AES-256-GCM into one `<shard>.guoart`,
written straight from memory (no PNG reaches the disk). It keeps the art from being copied off a disk by someone without
the shard's key; it cannot stop a screenshot. See docs/data_formats.md section 36.
"""

from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import art_container as container  # noqa: E402
import art_export as ex  # noqa: E402
from art_sources import CLASSES  # noqa: E402

# The names people type; the set's own class names are the content seam's keys.
WHAT = {"art": ("land", "static"), "land": ("land",), "static": ("static",), "gumps": ("gump",), "gump": ("gump",),
        "texmaps": ("texmap",), "texmap": ("texmap",), "lights": ("light",), "light": ("light",),
        "anim": ("anim",), "animations": ("anim",)}


def parse_what(text: str | None) -> tuple[str, ...]:
    if not text:
        return CLASSES
    out = []
    for word in text.split(","):
        word = word.strip().lower()
        if word not in WHAT:
            raise ex.ArtExtractError(f"--what: {word!r} is not one of art, land, static, gumps, texmaps, lights, anim")
        out += [c for c in WHAT[word] if c not in out]
    return tuple(c for c in CLASSES if c in out)


def _data(args, cfg) -> Path:
    data = Path(args.data) if args.data else Path(cfg["client_data"])
    if not data.is_dir():
        raise ex.ArtExtractError(f"client data folder {data} not found (UO_CLIENT_DATA, or --from)")
    return data


def cmd_export(args) -> int:
    cfg = ex.settings()
    data = _data(args, cfg)
    out = ex.check_out(Path(args.out) if args.out else cfg["art_dir"], cfg["art_dir"], cfg["root"])
    t0 = time.time()
    print(f"exporting {', '.join(parse_what(args.what))} from the install to {out}")
    doc = ex.export(data, out, parse_what(args.what), client_version=args.client_version or cfg["client_version"])
    total = sum(c["bytes"] for c in doc["classes"].values())
    print(f"done: {sum(c['count'] for c in doc['classes'].values())} images, "
          f"{sum(c['skipped'] for c in doc['classes'].values())} skipped, {total / 1e6:.1f} MB of pages, "
          f"{time.time() - t0:.1f} s, set_id {doc['fingerprint']['set_id'][:16]}")
    return 0


def cmd_verify(args) -> int:
    cfg = ex.settings()
    data = _data(args, cfg)
    folder = Path(args.set) if args.set else cfg["art_dir"]
    t0 = time.time()
    print(f"verifying {folder} against the install")
    result = ex.verify(data, folder, parse_what(args.what) if args.what else None)
    print(f"{'OK: every id matches' if result['ok'] else 'FAILED'} ({time.time() - t0:.1f} s)")
    return 0 if result["ok"] else 1


def cmd_where(args) -> int:
    cfg = ex.settings()
    print(f"UO_ART_EXTRACT_DIR = {cfg['art_dir']}")
    return 0


def cmd_keygen(args) -> int:
    shard = container.check_shard_id(args.shard)
    try:
        container.write_key_file(Path(args.key_file), container.new_key())
    except FileExistsError:
        raise ex.ArtExtractError(f"{args.key_file} exists; a shard key is never overwritten") from None
    key_id = container.key_id_of(container.read_key_file(Path(args.key_file)))
    print(f"wrote a new key for shard {shard} (key id {key_id}) to {args.key_file}; keep it out of the repository, "
          f"give it to players only inside the shard's signed pack")
    return 0


def cmd_pack_container(args) -> int:
    cfg = ex.settings()
    data = _data(args, cfg)
    shard = container.check_shard_id(args.shard)
    key = container.read_key_file(Path(args.key_file))
    default = Path(cfg["art_dir"]) / "containers" / f"{shard}.guoart"
    out = ex.check_out(Path(args.out) if args.out else default, cfg["art_dir"], cfg["root"])
    t0 = time.time()
    what = parse_what(args.what)
    print(f"sealing {', '.join(what)} for shard {shard} (key id {container.key_id_of(key)}) into {out}")
    sink = ex.ContainerSink(out, shard, key)
    doc = ex.export(data, out, what, client_version=args.client_version or cfg["client_version"], sink=sink)
    print(f"done: {sum(c['count'] for c in doc['classes'].values())} images, {out.stat().st_size / 1e6:.1f} MB container, "
          f"{time.time() - t0:.1f} s, set_id {doc['fingerprint']['set_id'][:16]}")
    return 0


def cmd_info(args) -> int:
    """The header of a container, read without a key (it holds no secret)."""
    import struct
    raw = Path(args.file).read_bytes()[:1 << 16]
    if raw[:len(container.MAGIC)] != container.MAGIC:
        raise ex.ArtExtractError(f"{args.file} is not an art container")
    (n,) = struct.unpack_from("<I", raw, len(container.MAGIC))
    print(raw[len(container.MAGIC) + 4:len(container.MAGIC) + 4 + n].decode("utf-8"))
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="art_extract", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="command", required=True)
    e = sub.add_parser("export", help="write the set")
    e.add_argument("--what", help="comma list of art, land, static, gumps, texmaps, lights, anim (default: all)")
    e.add_argument("--out", help="a folder inside UO_ART_EXTRACT_DIR or the repository's build/")
    e.add_argument("--from", dest="data", help="client data folder (default UO_CLIENT_DATA)")
    e.add_argument("--client-version", dest="client_version", help="recorded in set.json (default UO_CLIENT_VERSION)")
    e.set_defaults(fn=cmd_export)
    v = sub.add_parser("verify", help="compare the set with the install, id by id")
    v.add_argument("--set", help="the set folder (default UO_ART_EXTRACT_DIR)")
    v.add_argument("--what", help="limit to these classes")
    v.add_argument("--from", dest="data", help="client data folder (default UO_CLIENT_DATA)")
    v.set_defaults(fn=cmd_verify)
    w = sub.add_parser("where", help="print the resolved UO_ART_EXTRACT_DIR")
    w.set_defaults(fn=cmd_where)
    k = sub.add_parser("keygen", help="write a new 256-bit shard key to a file (hex, owner-only where the OS allows)")
    k.add_argument("--shard", required=True)
    k.add_argument("--key-file", dest="key_file", required=True)
    k.set_defaults(fn=cmd_keygen)
    c = sub.add_parser("pack-container", help="seal the art into one encrypted <shard>.guoart")
    c.add_argument("--shard", required=True)
    c.add_argument("--key-file", dest="key_file", required=True)
    c.add_argument("--what", help="comma list of art, land, static, gumps, texmaps, lights, anim (default: all)")
    c.add_argument("--out", help="the container file (default UO_ART_EXTRACT_DIR/containers/<shard>.guoart)")
    c.add_argument("--from", dest="data", help="client data folder (default UO_CLIENT_DATA)")
    c.add_argument("--client-version", dest="client_version")
    c.set_defaults(fn=cmd_pack_container)
    i = sub.add_parser("info", help="print a container's header (no key needed)")
    i.add_argument("file")
    i.set_defaults(fn=cmd_info)
    args = ap.parse_args(argv)
    try:
        return args.fn(args)
    except (ex.ArtExtractError, container.ContainerError) as err:
        print(f"art_extract: {err}", file=sys.stderr)
        return 2
    except FileNotFoundError as err:
        print(f"art_extract: {err}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
