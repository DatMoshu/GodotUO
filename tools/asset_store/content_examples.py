"""Build original CC0 content-pack starter sources and reproducible store ZIPs."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys
import zlib
import io
import math
import wave
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store.seed import write_pack
from asset_store.pack import verify
from asset_store.run import publish


def png(width, height, color, mask=None):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    rows = b"".join(b"\0" + (bytes(color) * width if mask is None else
        b"".join(bytes(color) if mask[y][x] == "#" else b"\0\0\0\0" for x in range(width))) for y in range(height))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")


def build(output, store=None):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    audio = io.BytesIO()
    with wave.open(audio, "wb") as wav:
        wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(22050)
        wav.writeframes(b"".join(struct.pack("<h", int(2400 * math.sin(i * 2 * math.pi * 440 / 22050))) for i in range(2205)))
    specs = {
        "sample-content-art": ("client", [
            ("stone", "static", "client", "stone.png", png(32, 48, (120, 144, 160, 255))),
            ("ground", "land", "client", "ground.png", png(44, 44, (80, 144, 72, 255))),
            ("slope", "texmap", "client", "slope.png", png(64, 64, (80, 144, 72, 255))),
            ("panel", "gump", "client", "panel.png", png(120, 80, (48, 64, 96, 255))),
            ("palette", "hue", "client", "palette.json", {"colors": [i * 1057 for i in range(32)], "name": "Example grey"}),
            ("english", "translation", "client", "english.json", {"locale": "enu", "strings": {"3100001": "Welcome to the example shard"}}),
            ("chime", "sound", "client", "chime.wav", audio.getvalue()),
            ("ambience", "music", "client", "ambience.wav", audio.getvalue()),
            ("lamp", "light", "client", "lamp.png", png(16, 16, (128, 128, 128, 255))),
            ("platform", "multi", "client", "platform.json", {"items": [{"graphic": 3701, "x": 0, "y": 0, "z": 0, "visible": True}]}),
            ("stone-data", "tiledata", "client", "stone-data.json", {"name": "Example stone", "height": 8, "weight": 1}),
            ("figure", "animation", "client", "figure.json", {"group_type": "Human", "sequences": [
                {"action": 0, "direction": direction, "frames": [{"image": "stone.png", "center_x": 16, "center_y": 0}, {"image": "stone.png", "center_x": 15, "center_y": 1}]}
                for direction in range(5)]}),
            ("paperdoll", "gump", "client", "paperdoll.png", png(32, 48, (120, 144, 160, 255))),
            ("outfit", "wearable", "client", "outfit.json", {"art": "sample-content-art:stone", "animation": "sample-content-art:figure", "paperdoll": "sample-content-art:paperdoll", "layer": 5}),
        ]),
        "sample-content-font": ("client", [
            ("letters", "font", "client", "letters.json", {"glyphs": [
                {"encoding": "ascii", "codepoint": 65, "image": "glyph-a.png"},
                {"encoding": "unicode", "codepoint": 65, "image": "glyph-a.png", "offset_x": 1, "offset_y": 2}]}),
        ]),
        "sample-content-map": ("client", [
            ("courtyard", "map", "client", "courtyard.json", {"blocks": [{"x": 180, "y": 210,
                "land": [{"graphic": 580, "z": 7} for _ in range(64)],
                "statics": [{"graphic": 3701, "x": 3, "y": 4, "z": 7, "hue": 33}]}]}),
        ]),
        "sample-content-server": ("server", [
            ("stone-item", "item", "server", "item.json", {"name": "Example stone", "graphic": "sample-content-art:stone", "movable": True, "weight": 1}),
        ]),
        "sample-content-region": ("server", [
            ("courtyard", "region", "server", "region.json", {"name": "GUO example courtyard", "facet": 0, "priority": 100,
                "areas": [{"x": 1440, "y": 1680, "z": -128, "width": 8, "height": 8, "depth": 256}],
                "music": "sample-content-art:ambience", "enter_message": "Welcome to the authored courtyard.", "exit_message": "Leaving the authored courtyard."}),
        ]),
        "sample-content-loot": ("server", [
            ("stone-cache", "loot", "server", "loot.json", {"entries": [
                {"item": "sample-content-server:stone-item", "chance": 1.0, "min": 1, "max": 3},
                {"item": "sample-content-server:stone-item", "chance": 0.5, "min": 1, "max": 2},
                {"item": "sample-content-server:stone-item", "chance": 0.0, "min": 1, "max": 1}]}),
        ]),
        "sample-content-creature": ("server", [
            ("stone-rat", "creature", "server", "creature.json", {"name": "Example stone rat", "body": 238, "hue": 0, "sound": 204,
                "ai": "animal", "strength": 20, "dexterity": 25, "intelligence": 5, "hits": 18, "damage_min": 1, "damage_max": 3,
                "armor": 5, "fame": 0, "karma": 0, "tactics": 10, "wrestling": 10, "resist": 5, "loot": "sample-content-loot:stone-cache"}),
        ]),
        "sample-content-decoration": ("server", [
            ("courtyard", "decoration", "server", "decoration.json", {"facet": 0, "items": [
                {"id": "stone-west", "graphic": "sample-content-art:stone", "x": 1442, "y": 1682, "z": 7, "hue": 0},
                {"id": "stone-east", "graphic": "sample-content-art:stone", "x": 1445, "y": 1682, "z": 7, "hue": 33}]}),
        ]),
        "sample-content-combined": ("combined", [
            ("stone", "static", "client", "stone.png", png(32, 48, (144, 96, 64, 255))),
            ("stone-item", "item", "server", "item.json", {"name": "Combined example stone", "graphic": "sample-content-combined:stone", "movable": True, "weight": 1}),
        ]),
    }
    specs["sample-content-world"] = ("combined", [
        ("courtyard", "map", "shared", "courtyard.json", {"blocks": [{"x": 180, "y": 210,
            "land": [{"graphic": 3, "z": 7} for _ in range(64)],
            "statics": [{"graphic": 3701, "x": 3, "y": 4, "z": 7, "hue": 33}]}]}),
    ])
    specs["sample-content-collision"] = ("combined", [
        ("barrier", "static", "client", "barrier.png", png(32, 8, (160, 104, 56, 255))),
        ("barrier-data", "tiledata", "shared", "barrier-data.json", {"name": "Example barrier", "flags": 64, "height": 8, "weight": 255}),
        ("courtyard", "map", "shared", "courtyard.json", {"blocks": [{"x": 180, "y": 210,
            "land": [{"graphic": 3, "z": 7} for _ in range(64)],
            "statics": [{"graphic": 3702, "x": 3, "y": 4, "z": 7, "hue": 0}]}]}),
    ])
    result = []
    for pack_id, (target, records) in specs.items():
        payload = {"preview.png": png(128, 96, (64, 96, 128, 255)),
                   "README.txt": b"Original procedural CC0 starter assets. Installation is inert. Runtime consumer support must be checked before activation.\n"}
        components = []
        if pack_id == "sample-content-font":
            payload["glyph-a.png"] = png(5, 7, (248, 248, 248, 255), [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"])
        for local, kind, side, entry, data in records:
            payload[entry] = data if isinstance(data, bytes) else (json.dumps(data, indent=2) + "\n").encode()
            component = dict(id=local, type=kind, target=side, entry=entry)
            if kind == "item": component["references"] = [data["graphic"]]
            if kind == "wearable": component["references"] = [data["art"], data["animation"], data["paperdoll"]]
            if kind == "region" and "music" in data: component["references"] = [data["music"]]
            if kind == "decoration": component["references"] = sorted({item["graphic"] for item in data["items"]})
            if kind == "loot": component["references"] = sorted({item["item"] for item in data["entries"]})
            if kind == "creature" and "loot" in data: component["references"] = [data["loot"]]
            components.append(component)
        deps = {"sample-content-art": "1.0.0"} if pack_id in ("sample-content-server", "sample-content-font", "sample-content-region", "sample-content-decoration") else {"sample-content-font": "1.0.0"} if pack_id in ("sample-content-map", "sample-content-world", "sample-content-collision") else {}
        if pack_id == "sample-content-loot": deps = {"sample-content-server": "1.0.0"}
        if pack_id == "sample-content-creature": deps = {"sample-content-loot": "1.0.0"}
        m = dict(schema="guo/store-pack@2", id=pack_id, version="1.0.0", kind="content",
                 target=target, dependencies=deps, components=components, title=pack_id.replace("-", " ").title(),
                 author="GUO original procedural examples", licence="CC0-1.0", min_profile_version=6,
                 preview="preview.png", files={name: hashlib.sha256(data).hexdigest() for name, data in payload.items()})
        source = output / pack_id
        source.mkdir(exist_ok=True)
        for name, data in payload.items(): (source / name).write_bytes(data)
        (source / "manifest.json").write_text(json.dumps(m, indent=2) + "\n", encoding="utf-8")
        archive = output / (pack_id + ".zip")
        write_pack(archive, m, payload)
        verify(archive)
        if store: publish(archive, store)
        result.append(archive)
    return result


def bundle(archives, destination):
    """Package verified ZIPs, editable declared sources, and current documentation."""
    from asset_store.razor_scripts import make_pack
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    scripts = make_pack(destination.parent / "guo-razor-starters.zip", "2.0.0", True)
    files = {}
    for archive in [*archives, scripts]:
        manifest = verify(archive)
        files[archive.name] = archive.read_bytes()
        with zipfile.ZipFile(archive) as source:
            for name in ["manifest.json", *manifest["files"]]:
                files[manifest["id"] + "/" + name] = source.read(name)
    root = Path(__file__).resolve().parents[2]
    files["CONTRACT.md"] = (root / "docs/asset_pack_ecosystem.md").read_bytes()
    files["TOOLING.md"] = (root / "tools/asset_store/README.md").read_bytes()
    files["GETTING_STARTED.md"] = (
        "# GUO asset-pack starters\n\n"
        "Original starter packs: client art, fonts, client maps, server items, combined items, "
        "combined world maps, shared collision metadata, server regions, decoration sets, loot tables, creatures and managed Razor scripts. ZIPs are ready for publication; folders contain editable declared sources.\n\n"
        "Read CONTRACT.md for supported payloads and limitations, and TOOLING.md for generation and test commands. "
        "Changing sources requires updating manifest hashes and release versions. Published releases are immutable. "
        "Installation is inactive; content needs an explicit deployment lock and scripts need explicit approval. "
        "The combined-world sample must use matching client and server deployments. No proprietary UO data is included.\n"
    ).encode("utf-8")
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED) as output:
        for name, data in sorted(files.items()):
            info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            output.writestr(info, data)
    with zipfile.ZipFile(destination) as output:
        assert output.testzip() is None
    return destination


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=Path("build/content_examples"))
    parser.add_argument("--store", type=Path)
    parser.add_argument("--bundle", type=Path, help="Also create a reproducible source/ZIP/documentation bundle, including managed scripts")
    args = parser.parse_args()
    archives = build(args.out, args.store)
    for path in archives: print(path)
    if args.bundle: print(bundle(archives, args.bundle))
