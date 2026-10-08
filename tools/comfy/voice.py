#!/usr/bin/env python3
"""Voice profiles for GUO text-to-speech (TEXT2SPEACHWORKINGFASTQWEN: voice clone from a sample).

A voice is a sample + its auto-transcript. Enroll once (sample copied into
ComfyUI's input dir, transcribed by the workflow's own ASR); speak any text
after that without re-uploading.

    python tools/comfy/voice.py enroll --sample DIABLO.mp3 --voice diablo
    python tools/comfy/voice.py speak --voice diablo --text "hello Britannia" --out build/comfy/voice_hello.mp3
    python tools/comfy/voice.py voices

Profiles live in build/voice_profiles/<voice>.json (gitignored): sample file,
transcript, workflow used. The client reads the same folder (GUO_VOICES).
 PROCUREMENT: samples are the player's own voice recordings.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from run import resolve_url, api_get, api_post  # noqa: E402
from uiconv import convert_file  # noqa: E402

WORKFLOW_NAME = "PERFECT_VOICE_CLONE.json"
DESIGN_WORKFLOW_NAME = "PERFECT_VOICE_DESIGN.json"
DESIGN_PANGRAM = "The quick brown fox jumps over the lazy dog near the Britain bank."
TEXT_NODE = 9
TEXT_WIDGET = 0
AUDIO_NODE = 4
AUDIO_WIDGET = 0


def comfy_source_dir() -> Path:
    for c in (Path(r"D:\ComfyUIPortable\ComfyUI\user\default\workflows"),
              Path(r"C:\ComfyUIY\ComfyUI\user\default\workflows")):
        if (c / WORKFLOW_NAME).is_file():
            return c
    raise FileNotFoundError("TEXT2SPEACHWORKINGFASTQWEN.json not found in known workflow dirs")


def profiles_dir() -> Path:
    d = Path(__file__).resolve().parents[2] / "build" / "voice_profiles"
    d.mkdir(parents=True, exist_ok=True)
    return d


def comfy_input_dir(url: str) -> Path:
    # Same box assumption as everything else here (127.0.0.1:8188).
    for c in (Path(r"D:\ComfyUIPortable\ComfyUI\input"), Path(r"C:\ComfyUIY\ComfyUI\input")):
        if c.is_dir():
            return c
    raise FileNotFoundError("ComfyUI input dir not found")


def queue_and_wait(url: str, workflow: dict, timeout_s: int = 90, poll_s: float = 2.0) -> tuple[str, dict]:
    queued = api_post(url + "/prompt", {"prompt": workflow, "client_id": "guo-voice"})
    pid = queued.get("prompt_id")
    if not pid:
        raise RuntimeError(f"queue refused: {queued}")
    print(f"[voice] queued {pid}")
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        time.sleep(poll_s)
        try:
            hist = api_get(f"{url}/history/{pid}", timeout=30)
        except Exception as ex:
            # Model loads stall the server past short timeouts; keep waiting.
            print(f"[voice] history quiet ({type(ex).__name__}); still waiting...")
            continue
        entry = hist.get(pid, {})
        if entry.get("status", {}).get("completed"):
            if entry["status"].get("status_str") == "error":
                raise RuntimeError(f"execution error: {json.dumps(entry['status'].get('messages'))[:800]}")
            return pid, entry.get("outputs", {})
    raise TimeoutError("no completion in time")


def texts_in(outputs: dict) -> list[str]:
    found = []
    for _nid, node_out in (outputs or {}).items():
        if not isinstance(node_out, dict):
            continue
        for _key, items in node_out.items():
            if not isinstance(items, list):
                continue
            for item in items:
                if isinstance(item, str) and item.strip():
                    found.append(item)
    return found


def audios_in(outputs: dict) -> list[dict]:
    found = []
    for _nid, node_out in (outputs or {}).items():
        if not isinstance(node_out, dict):
            continue
        for _key, items in node_out.items():
            if not isinstance(items, list):
                continue
            for item in items:
                if isinstance(item, dict) and "filename" in item:
                    found.append(item)
    return found


def download(url: str, ref: dict, dest: Path) -> Path:
    import urllib.parse
    qs = urllib.parse.urlencode({"filename": ref["filename"],
                                 "subfolder": ref.get("subfolder", ""),
                                 "type": ref.get("type", "output")})
    with urllib.request.urlopen(f"{url}/view?{qs}", timeout=120) as r:
        dest.write_bytes(r.read())
    print(f"[voice] {ref['filename']} -> {dest} ({dest.stat().st_size} bytes)")
    return dest


def build_speak_prompt(sample_name: str, text: str, seed: int = -1) -> dict:
    """Converted workflow bound to a sample, with a SaveAudio tail so the
    client can fetch a permanent WAV (PreviewAudio only leaves temp files).
    The clone seed varies delivery per NPC (same serial stable); -1 random."""
    import random
    if seed is None or seed < 0:
        seed = random.randint(1, 2 ** 31 - 1)
    wf = convert_file(comfy_source_dir() / WORKFLOW_NAME)
    clone_id = None
    for k, node in wf.items():
        if not isinstance(node, dict):
            continue
        if node.get("class_type") == "LoadAudio":
            node["inputs"]["audio"] = sample_name
        if node.get("class_type") == "Qwen3VoiceClone":
            clone_id = k
            w = node.get("inputs", {})
            for tk in ("text", "prompt"):
                if tk in w and isinstance(w[tk], str):
                    w[tk] = text
            w["seed"] = seed
    if clone_id is not None:
        nxt = str(max([int(k) for k in wf if str(k).isdigit()], default=0) + 1)
        wf[nxt] = {"class_type": "SaveAudio",
                   "inputs": {"audio": [clone_id, 0], "filename_prefix": "GUO_voice"}}
    return wf


def cmd_enroll(args) -> int:
    url = resolve_url(None)
    src = Path(args.sample)
    if not src.is_file():
        # bare name in ComfyUI input dir is fine too
        src = comfy_input_dir(url) / args.sample
    assert src.is_file(), f"no sample: {args.sample}"
    dest_name = f"guo_voice_{args.voice}{src.suffix.lower()}"
    dest = comfy_input_dir(url) / dest_name
    if src.resolve() != dest.resolve():
        shutil.copyfile(src, dest)
        print(f"[voice] sample -> {dest}")
    wf = build_speak_prompt(dest_name, "Voice enrolled and ready.")
    # The client reuses this template (text rebound per line).
    (profiles_dir() / f"{args.voice}.prompt.json").write_text(json.dumps(wf, indent=1), encoding="utf-8")
    _pid, outputs = queue_and_wait(url, wf, timeout_s=args.timeout)
    transcripts = texts_in(outputs)
    print(f"[voice] transcript candidates: {transcripts[:2]}")
    transcript = max(transcripts, key=len) if transcripts else ""
    if not transcript:
        print("[voice] WARNING: no transcript came back; enrollment records an empty ref_text")
    prof = {"format": 1, "voice": args.voice, "sample": dest_name,
            "transcript": transcript, "workflow": WORKFLOW_NAME,
            "note": "player's own voice sample; local use only"}
    out = profiles_dir() / f"{args.voice}.json"
    out.write_text(json.dumps(prof, indent=2), encoding="utf-8")
    print(f"[voice] enrolled '{args.voice}' -> {out}")
    auds = audios_in(outputs)
    if auds:
        download(url, auds[0], Path(args.out or f"build/comfy/voice_{args.voice}_enroll.mp3"))
    return 0


def cmd_speak(args) -> int:
    url = resolve_url(None)
    prof = json.loads((profiles_dir() / f"{args.voice}.json").read_text(encoding="utf-8"))
    wf = build_speak_prompt(prof["sample"], args.text, seed=args.seed)
    _pid, outputs = queue_and_wait(url, wf, timeout_s=args.timeout)
    auds = audios_in(outputs)
    if not auds:
        print("[voice] FAILED: no audio produced")
        return 1
    # Prefer the permanent SaveAudio WAV over preview temp files.
    auds.sort(key=lambda a: (not str(a.get("filename", "")).lower().endswith(".wav"),
                             str(a.get("type", "")) != "output"))
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    download(url, auds[0], out)
    if args.wav:
        wav_voice(out)
    return 0


def wav_voice(src: Path) -> Path:
    """Anything -> RIFF WAV 22050Hz mono 16-bit with exact header (the client gate)."""
    import struct
    import io
    import wave
    ffmpeg = shutil.which("ffmpeg") or r"C:\ffmpeg\bin\ffmpeg.exe"
    out = subprocess.run(
        [ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", str(src),
         "-ar", "22050", "-ac", "1", "-sample_fmt", "s16", "-f", "wav", "pipe:1"],
        capture_output=True, check=True).stdout
    with wave.open(io.BytesIO(out), "rb") as w:
        pcm = w.readframes(w.getnframes())
    dest = src.with_suffix(".wav")
    dest.write_bytes(struct.pack("<4sI4s4sIHHIIHH4sI", b"RIFF", 36 + len(pcm), b"WAVE", b"fmt ", 16,
                                 1, 1, 22050, 22050 * 2, 2, 16, b"data", len(pcm)) + pcm)
    print(f"[voice] {dest} ({len(pcm) // 2} samples)")
    return dest


def ollama_describe(name: str, body: int, kind: str) -> str:
    """One-line voice description from the local LLM (nothing leaves the machine)."""
    import os
    model = os.environ.get("GUO_LLM", "qwen3.5:0.8b")
    host = os.environ.get("OLLAMA_HOST", "http://127.0.0.1:11434").rstrip("/")
    prompt = (f"Describe a voice for an Ultima Online NPC in one short line: age, gender, vocal quality, pace. "
              f"NPC: {name}, a {kind}, body {body}. Reply with ONLY the description, no quotes, under 200 characters.")
    body_json = json.dumps({"model": model, "prompt": prompt, "stream": False,
                             "think": False,
                             "options": {"temperature": 0.3, "num_predict": 60}}).encode()
    req = urllib.request.Request(host + "/api/generate", data=body_json,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            text = json.loads(r.read().decode()).get("response", "")
    except Exception as ex:
        print(f"[voice] LLM unavailable ({type(ex).__name__}); fallback description, design continues")
        return "a warm middle-aged shopkeeper voice"
    line = " ".join(text.strip().split())
    if len(line) > 200:
        line = line[:200]
    print(f"[voice] LLM voice design ({model}): {line}")
    return line or "a warm middle-aged shopkeeper voice"


def free_vram(url: str) -> None:
    """Ask ComfyUI to unload cached models before a heavy job (voice design
    wedges when VRAM is full of stale checkpoints; the server keeps whatever
    is actually running). Never fatal: failure just means less headroom."""
    try:
        req = urllib.request.Request(
            url + "/free", data=json.dumps({"unload_models": True}).encode(),
            headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=60):
            pass
        print("[voice] VRAM caches released")
    except Exception as ex:
        print(f"[voice] VRAM release skipped ({type(ex).__name__})")


def cmd_design(args) -> int:
    url = resolve_url(None)
    free_vram(url)
    voice = args.voice or f"npc_{args.serial}"
    line = args.line or DESIGN_PANGRAM
    # 1. The local LLM designs the voice from who the NPC is.
    description = ollama_describe(args.name, args.body, args.type)
    # 2. The PERFECT design workflow speaks the pangram in that voice, one
    # pass, then a SaveAudio tail takes the sample (the old example graph
    # needed a save/load dance plus a second clone that took 10 minutes).
    from uiconv import convert_file as _convert
    wf = _convert(comfy_source_dir() / DESIGN_WORKFLOW_NAME)
    import random
    import tempfile
    for _k, node in wf.items():
        if isinstance(node, dict) and node.get("class_type") == "Qwen3VoiceDesign":
            node["inputs"]["text"] = line
            node["inputs"]["instruct"] = description
            node["inputs"]["seed"] = random.randint(1, 2 ** 31 - 1)
    nxt = str(max([int(k) for k in wf if str(k).isdigit()], default=0) + 1)
    design_id = next(k for k, n in wf.items()
                     if isinstance(n, dict) and n.get("class_type") == "Qwen3VoiceDesign")
    wf[nxt] = {"class_type": "SaveAudio",
               "inputs": {"audio": [design_id, 0], "filename_prefix": f"GUO_voice_{voice}"}}
    _pid, outputs = queue_and_wait(url, wf, timeout_s=args.timeout)
    auds = [a for a in audios_in(outputs) if str(a.get("filename", "")).lower().endswith((".wav", ".flac", ".mp3"))]
    # Prefer the permanent SaveAudio WAV over preview temp files (same rule as speak).
    auds.sort(key=lambda a: (not str(a.get("filename", "")).lower().endswith(".wav"),
                             str(a.get("type", "")) != "output"))
    if not auds:
        print("[voice] FAILED: design produced no audio")
        return 1
    # 3. That clip becomes the voice sample going forward (proven clone path).
    sample_name = f"guo_voice_{voice}.wav"
    tmp = Path(tempfile.gettempdir()) / f"guo_design_{voice}"
    download(url, auds[0], tmp.with_suffix(".flac"))
    wav_voice(tmp.with_suffix(".flac"))
    shutil.move(str(tmp.with_suffix(".wav")), str(comfy_input_dir(url) / sample_name))
    prof = {"format": 1, "voice": voice, "sample": sample_name, "transcript": line,
            "workflow": WORKFLOW_NAME, "designed": description,
            "note": "LLM-designed NPC voice; sample generated by Qwen3VoiceDesign"}
    (profiles_dir() / f"{voice}.json").write_text(json.dumps(prof, indent=2), encoding="utf-8")
    wf2 = build_speak_prompt(sample_name, "Voice enrolled and ready.")
    (profiles_dir() / f"{voice}.prompt.json").write_text(json.dumps(wf2, indent=1), encoding="utf-8")
    # 4. Map the NPC to its voice so future lines skip all of this.
    vpath = profiles_dir() / "voices.json"
    try:
        vdoc = json.loads(vpath.read_text(encoding="utf-8"))
    except Exception:
        vdoc = {"format": 1, "rules": []}
    vdoc.setdefault("rules", [])
    vdoc["rules"] = [r for r in vdoc["rules"]
                     if not (isinstance(r, dict) and args.serial in (r.get("serials") or []))]
    vdoc["rules"].append({"serials": [args.serial], "voice": voice})
    vpath.write_text(json.dumps(vdoc, indent=2), encoding="utf-8")
    print(f"[voice] designed '{voice}' for serial {args.serial}: {description}")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    sub = ap.add_subparsers(dest="cmd", required=True)
    e = sub.add_parser("enroll", help="register a voice sample (copies + transcribes)")
    e.add_argument("--sample", required=True, help="wav/mp3 file (or name in ComfyUI input/)")
    e.add_argument("--voice", required=True)
    e.add_argument("--out", default=None)
    e.add_argument("--timeout", type=int, default=120)
    s = sub.add_parser("speak", help="speak text in an enrolled voice")
    s.add_argument("--voice", required=True)
    s.add_argument("--text", required=True)
    s.add_argument("--seed", type=int, default=-1, help="clone seed (-1 random; pass a serial hash for stable NPC delivery)")
    s.add_argument("--out", default="build/comfy/voice_out.mp3")
    s.add_argument("--wav", action="store_true", help="also write 22050 mono wav")
    s.add_argument("--timeout", type=int, default=90)
    d = sub.add_parser("design", help="LLM-design a voice for an NPC and enroll it")
    d.add_argument("--serial", type=int, required=True)
    d.add_argument("--name", default="a traveler")
    d.add_argument("--body", type=int, default=400)
    d.add_argument("--type", default="townsperson")
    d.add_argument("--voice", default=None)
    d.add_argument("--line", default=None)
    d.add_argument("--timeout", type=int, default=110, help="hard budget: everything here runs under 120 s")
    v = sub.add_parser("voices", help="list enrolled voices")
    args = ap.parse_args(argv)
    if args.cmd == "enroll":
        return cmd_enroll(args)
    if args.cmd == "speak":
        return cmd_speak(args)
    if args.cmd == "design":
        return cmd_design(args)
    for p in sorted(profiles_dir().glob("*.json")):
        print(p.stem, json.loads(p.read_text()).get("sample"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
