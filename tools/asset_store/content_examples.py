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

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store.seed import write_pack
from asset_store.pack import verify
from asset_store.run import publish


def png(width, height, color):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    rows = b"".join(b"\0" + bytes(color) * width for _ in range(height))
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
        ]),
        "sample-content-server": ("server", [
            ("stone-item", "item", "server", "item.json", {"name": "Example stone", "graphic": "sample-content-art:stone", "movable": True, "weight": 1}),
        ]),
        "sample-content-combined": ("combined", [
            ("stone", "static", "client", "stone.png", png(32, 48, (144, 96, 64, 255))),
            ("stone-item", "item", "server", "item.json", {"name": "Combined example stone", "graphic": "sample-content-combined:stone", "movable": True, "weight": 1}),
        ]),
    }
    result = []
    for pack_id, (target, records) in specs.items():
        payload = {"preview.png": png(128, 96, (64, 96, 128, 255)),
                   "README.txt": b"Original procedural CC0 starter assets. Installation is inert. Runtime consumer support must be checked before activation.\n"}
        components = []
        for local, kind, side, entry, data in records:
            payload[entry] = data if isinstance(data, bytes) else (json.dumps(data, indent=2) + "\n").encode()
            component = dict(id=local, type=kind, target=side, entry=entry)
            if kind == "item": component["references"] = [data["graphic"]]
            components.append(component)
        deps = {"sample-content-art": "1.0.0"} if pack_id == "sample-content-server" else {}
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


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=Path("build/content_examples"))
    parser.add_argument("--store", type=Path)
    args = parser.parse_args()
    for path in build(args.out, args.store): print(path)
