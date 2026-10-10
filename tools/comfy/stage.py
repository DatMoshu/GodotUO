#!/usr/bin/env python3
"""Stage ComfyUI generations for the shard + client: store pack (audio) + splat LODs.

Audio goes the designed route: a guo/store-pack@2 with sound/music components
(WAV 22050Hz mono 16-bit, the only format SoundsLoader/StoreRuntimeContent
accept), verified by tools/asset_store. Splats have no UO archive slot, so
they stage as GUO-side files with a manifest the client reads (GUO_SPLAT_STAGE).

Provenance is recorded per artifact (tool/workflow/seed/prompt, no client
input), and the pack manifest carries the content-policy declaration.

    python tools/comfy/stage.py --pack guo-comfy-gen --out build/staged \\
        --sfx 2100=build/comfy/sfx/node57_audio.mp3 \\
        --music 101=build/comfy/music/node19_audio.mp3 \\
        --splat armoire=build/comfy/multi_lod \\
        --ffmpeg C:/ffmpeg/bin/ffmpeg.exe
    python tools/asset_store/run.py check build/staged/guo-comfy-gen.zip
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import shutil
import struct
import subprocess
import sys
import wave
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store.seed import write_pack  # noqa: E402
from asset_store.pack import verify  # noqa: E402

RATE, CHANNELS, WIDTH = 22050, 1, 2
LICENCE_DEFAULT = "CC0-1.0"


def to_wav(src: Path, ffmpeg: str) -> bytes:
    """Anything ffmpeg reads -> RIFF WAV 22050Hz mono 16-bit (the client gate).

    Rewrites the header: ffmpeg's piped output leaves 0xFFFFFFFF sizes and a
    LIST chunk, both of which StoreRuntimeContent.DecodeWave refuses. The
    rewrite is fmt + data only, with exact lengths.
    """
    out = subprocess.run(
        [ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", str(src),
         "-ar", str(RATE), "-ac", str(CHANNELS), "-sample_fmt", "s16", "-f", "wav", "pipe:1"],
        capture_output=True, check=True)
    with wave.open(io.BytesIO(out.stdout), "rb") as w:
        assert (w.getnchannels(), w.getsampwidth(), w.getframerate()) == (1, 2, RATE), \
            f"ffmpeg did not deliver 22050 mono 16-bit for {src}"
        pcm = w.readframes(w.getnframes())
    body = struct.pack("<4sI4s4sIHHIIHH4sI", b"RIFF", 36 + len(pcm), b"WAVE", b"fmt ", 16,
                       1, CHANNELS, RATE, RATE * CHANNELS * WIDTH, CHANNELS * WIDTH, 8 * WIDTH,
                       b"data", len(pcm)) + pcm
    return body


def check_wav(data: bytes, tag: str) -> int:
    """The same gate the client applies (SoundsLoader.ReadWave / DecodeWave)."""
    with wave.open(io.BytesIO(data), "rb") as w:
        assert w.getnchannels() == 1 and w.getsampwidth() == 2 and w.getframerate() == RATE, \
            f"{tag}: {w.getframerate()}Hz {w.getnchannels()}ch {w.getsampwidth() * 8}-bit"
        frames = w.readframes(w.getnframes())
    assert len(frames) > 0, f"{tag}: empty"
    return len(frames) // 2


def _preview_png(width: int = 64, height: int = 40, color=(72, 96, 128, 255)) -> bytes:
    import zlib
    rows = b"".join(b"\0" + bytes(color) * width for _ in range(height))

    def chunk(kind: bytes, data: bytes) -> bytes:
        import binascii
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", binascii.crc32(kind + data))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


def parse_cli_spec(specs: list[str]) -> list[tuple[int, Path]]:
    out = []
    for s in specs or []:
        id_s, path = s.split("=", 1)
        out.append((int(id_s, 0), Path(path)))
    return out


def build(args) -> Path:
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    ffmpeg = args.ffmpeg or shutil.which("ffmpeg") or r"C:\ffmpeg\bin\ffmpeg.exe"

    payload: dict[str, bytes] = {}
    components = []
    provenance = []
    sounds = {}
    statics = {}

    for art_id, src in parse_cli_spec(args.static):
        from PIL import Image as _Image  # noqa
        with _Image.open(src) as im:
            w, h = im.size
            png_buf = io.BytesIO()
            im.convert("RGBA").save(png_buf, format="PNG")
            png = png_buf.getvalue()
        entry = f"{src.stem}.png"
        if entry in payload:
            entry = f"{art_id}_{src.stem}.png"
        payload[entry] = png
        local = Path(entry).stem.replace("_", "-")
        components.append(dict(id=local, type="static", target="client", entry=entry))
        statics[local] = art_id
        provenance.append(dict(artifact=entry, static_id=art_id,
                               source=str(src), size=[w, h],
                               tool="comfyui", workflow="img2img",
                               licence=args.licence,
                               note="generated img2img from the client's own pixels; derived work, local-only"))
        print(f"[stage] {src.name} -> {entry} ({w}x{h} RGBA static 0x{art_id:04X})")

    for sound_id, src in parse_cli_spec(args.sfx) + parse_cli_spec(args.music):
        is_music = any(s.startswith(f"{sound_id}=") for s in (args.music or []))
        wav = to_wav(src, ffmpeg)
        samples = check_wav(wav, src.name)
        entry = f"{src.stem}.wav"
        if entry in payload:
            entry = f"{sound_id}_{src.stem}.wav"
        payload[entry] = wav
        local = Path(entry).stem.replace("_", "-")
        components.append(dict(id=local, type="music" if is_music else "sound",
                               target="client", entry=entry))
        sounds[local] = sound_id
        provenance.append(dict(artifact=entry, sound_id=sound_id,
                               source=str(src), samples=samples,
                               tool="comfyui", workflow=args.workflow or "FASTAUDIOTGEN/WORKINGAUDIO",
                               licence=args.licence,
                               note="generated with Stable Audio 3 Medium from a text prompt; no audio input"))
        print(f"[stage] {src.name} -> {entry} ({samples} samples @22050 mono)")

    # --- splats: LOD PLYs + manifest (GUO-side, no archive slot) ---
    places = {}
    for spec in args.place or []:
        name, at = spec.split("=", 1)
        coords = at.split(",")
        facet, x, y, z = (int(c) for c in coords[:4])
        places[name] = dict(facet=facet, x=x, y=y, z=z)
        if len(coords) > 4:
            places[name]["yaw"] = float(coords[4])
    footprints = {}
    scales = {}
    for spec in args.footprint or []:
        name, draft = spec.split("=", 1)
        draft_path = Path(draft)
        assert draft_path.suffixes[-2:] == [".multi", ".json"], f"footprint draft must be <name>.multi.json: {draft}"
        meta_path = draft_path.with_name(draft_path.name.replace(".multi.json", ".footprint.json"))
        assert meta_path.is_file(), f"footprint meta missing: {meta_path} (run splatlod.py footprint first)"
        dest_dir = out / "footprints"
        dest_dir.mkdir(exist_ok=True)
        for cand in (draft_path, meta_path):
            (dest_dir / cand.name).write_bytes(cand.read_bytes())
        footprints[name] = f"footprints/{draft_path.name}"
        # Screen px per model unit (reference default 22: a 2-unit model
        # covers one 44px tile): footprint tiles * 11, so the model covers
        # its footprint on the ground.
        try:
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
            size = meta.get("size") or [4, 4]
            scale = max(size[0], size[1]) * 11.0
        except Exception:
            scale = None
        if scale:
            scales[name] = scale
        print(f"[stage] footprint {name}: {footprints[name]}" + (f" (scale {scale})" if scale else ""))
    splats = {}
    for spec in args.splat or []:
        name, folder = spec.split("=", 1)
        files = sorted(Path(folder).glob("*_lod*.ply"))
        assert files, f"no LOD PLYs in {folder} (run splatlod.py lod first)"
        dest_dir = out / "splats"
        dest_dir.mkdir(exist_ok=True)
        entries = []
        for f in files:
            dest = dest_dir / f.name
            dest.write_bytes(f.read_bytes())
            entries.append(f"splats/{f.name}")
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        from splatlod import stats as splat_stats
        full = splat_stats(files[0])
        entry = dict(lods=entries, count=full["count"],
                     bounds_min=full["min"], bounds_max=full["max"],
                     tool="comfyui", workflow=args.multi_workflow or "MultisMaker1",
                     licence=args.licence,
                     note="gaussian splat from a text prompt via flux edit + TripoSplat; no client art input")
        if name in places:
            entry["placement"] = places[name]
        if name in footprints:
            entry["footprint"] = footprints[name]
        if name in scales:
            entry["scale"] = scales[name]
        splats[name] = entry
        print(f"[stage] splat {name}: {len(entries)} LODs, {full['count']} gaussians"
              + (f", placed {places[name]}" if name in places else ", unplaced"))

    (out / "splats.json").write_text(json.dumps(
        dict(format="guo/comfy-splats@1", splats=splats), indent=2) + "\n", encoding="utf-8")

    manifest = dict(
        schema="guo/store-pack@2", id=args.pack, version="1.0.0", kind="content",
        target="client", dependencies={}, components=components,
        title=args.title or args.pack, author=args.author, licence=args.licence,
        min_profile_version=6, preview="preview.png",
        files={name: hashlib.sha256(data).hexdigest() for name, data in payload.items()})
    payload["preview.png"] = _preview_png()
    manifest["files"] = {name: hashlib.sha256(data).hexdigest() for name, data in payload.items()}
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (out / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")

    archive = out / f"{args.pack}.zip"
    write_pack(archive, manifest, payload)
    report = verify(archive)
    print(f"[stage] pack {archive} ({archive.stat().st_size} bytes): verify {report.get('ok', report)}")
    music_locals = {Path(p).stem.replace("_", "-") for _, p in parse_cli_spec(args.music or [])}
    bindings = {f"{args.pack}:{local}": dict(type="music" if local in music_locals else "sound", id=sound_id)
                for local, sound_id in sounds.items()}
    bindings.update({f"{args.pack}:{local}": dict(type="static", id=art_id)
                     for local, art_id in statics.items()})
    (out / "bindings.json").write_text(json.dumps(bindings, indent=2) + "\n", encoding="utf-8")
    return archive


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--pack", default="guo-comfy-gen")
    ap.add_argument("--out", default="build/staged/guo-comfy-gen")
    ap.add_argument("--sfx", action="append", default=[], metavar="ID=mp3",
                    help="SFX clip (repeatable), e.g. --sfx 2100=build/comfy/sfx/node57_audio.mp3")
    ap.add_argument("--music", action="append", default=[], metavar="ID=mp3")
    ap.add_argument("--static", action="append", default=[], metavar="ID=png",
                    help="static art component (repeatable), e.g. --static 0xF000=variant.png")
    ap.add_argument("--splat", action="append", default=[], metavar="NAME=lod-dir")
    ap.add_argument("--footprint", action="append", default=[], metavar="NAME=draft.multi.json")
    ap.add_argument("--place", action="append", default=[], metavar="NAME=facet,x,y,z[,yaw]",
                    help="map placement for the splat visual, e.g. --place armoire=0,1434,1699,0 (yaw default 180)")
    ap.add_argument("--ffmpeg", default=None)
    ap.add_argument("--workflow", default="FASTAUDIOTGEN.json")
    ap.add_argument("--multi-workflow", default="MultisMaker1.json")
    ap.add_argument("--author", default="GUO ComfyUI proofs (owner-generated)")
    ap.add_argument("--licence", default=LICENCE_DEFAULT)
    ap.add_argument("--title", default="GUO ComfyUI generations")
    args = ap.parse_args(argv)
    if not args.sfx and not args.music and not args.splat and not args.static:
        ap.error("nothing to stage: pass --sfx, --music, --splat or --static")
    build(args)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
