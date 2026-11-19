"""Disposable map payloads and durable recovery receipts for one smoke cycle.

No private service is required. The .org-cache.json receipt also implements the
optional org.storage.cache.v1 producer contract, without importing Org.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import tempfile
import time

RESERVE_BYTES = 20 * 1024**3


def plain(path: Path) -> Path:
    path = Path(os.path.abspath(path))
    for ancestor in (path, *path.parents):
        try:
            info = ancestor.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError(f"staging refuses a link/reparse point: {ancestor}")
    return path


def digest(path: Path, algorithm="sha256") -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def write_json(path: Path, value) -> None:
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def peak_bytes(project: Path, client: Path) -> int:
    from guo.uomap import MAP_BLOCK, STATIC_SIZE, facet_files, open_facet

    project = plain(project)
    total = 0
    for folder in (project / "blocks").iterdir():
        plain(folder)
        if not folder.is_dir():
            continue
        facet = int(folder.name)
        sources = facet_files(client, facet)
        total += sum(p.stat().st_size for p in sources)
        if sources[0].suffix.lower() == ".uop":
            with open_facet(client, facet) as mapped:
                total += mapped.width_blocks * mapped.height_blocks * MAP_BLOCK
        for block in folder.glob("*.json"):
            plain(block)
            total += (len(json.loads(block.read_text(encoding="utf-8"))["statics"]) + 1) * STATIC_SIZE
    # Two exports, one recovery replay, and one transient client copy. Charging
    # a whole fourth export also covers its unused UOP plus metadata headroom.
    return 4 * total + 64 * 1024**2


def snapshot(project: Path, destination: Path) -> None:
    if (project / "assets").exists():
        raise ValueError("UltimaLive smoke expects a map-only overlay; assets stay in the asset smoke")
    destination.mkdir()
    sources = [project / "project.json"]
    for folder, directories, names in os.walk(plain(project / "blocks"), followlinks=False):
        for name in directories:
            plain(Path(folder) / name)
        sources.extend(Path(folder) / name for name in names if name.endswith(".json"))
    for source in sources:
        source = plain(source)
        target = destination / source.relative_to(plain(project))
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        if digest(source) != digest(target):
            raise ValueError("overlay snapshot readback failed")


def create_stage(root: Path, required: int, protected: tuple[Path, ...]) -> tuple[Path, dict]:
    root = plain(root)
    parts = [part.lower() for part in root.parts]
    if "_disposable" not in parts or parts[parts.index("_disposable") + 1:][:1] != ["guo"]:
        raise ValueError("--staging-root must be under an _disposable/guo directory")
    for path in protected:
        other = path.resolve()
        if root.is_relative_to(other) or other.is_relative_to(root):
            raise ValueError("staging must be separate from evidence and the client install")
    existing = root
    while not existing.exists():
        existing = existing.parent
    free = shutil.disk_usage(existing).free
    if free < RESERVE_BYTES + required:
        raise ValueError(f"staging capacity refused: need {required} incremental bytes plus {RESERVE_BYTES} reserve; free {free}")
    root.mkdir(parents=True, exist_ok=True)
    # Recheck immediately before allocating payload, including new ancestors.
    plain(root)
    if shutil.disk_usage(root).free < RESERVE_BYTES + required:
        raise ValueError("staging capacity changed before allocation")
    stage = Path(tempfile.mkdtemp(prefix="ultimalive-", dir=root))
    return stage, dict(required_bytes=required, reserve_bytes=RESERVE_BYTES, free_bytes=free)


def exclusive(path: Path) -> None:
    if os.name != "nt":
        return  # POSIX producer custody: all owned synchronous children returned.
    import ctypes
    from ctypes import wintypes

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                  ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.CreateFileW(str(path), 0x80000000, 0, None, 3, 0, None)
    if handle == wintypes.HANDLE(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    kernel.CloseHandle(handle)


def release(stage: Path) -> dict:
    """Freeze and unlink only this completed producer's known outputs, never inputs."""
    stage = plain(stage)
    files = []
    for folder, directories, names in os.walk(stage, followlinks=False):
        for name in directories:
            plain(Path(folder) / name)
        for name in names:
            path = plain(Path(folder) / name)
            if path == stage / ".org-cache.json":
                continue
            if not (re.fullmatch(r"(?:map\d+(?:LegacyMUL)?\.uop|map\d+\.mul|staidx\d+\.mul|statics\d+\.mul)", name)
                    or name in ("export.json", "files_override.txt")):
                raise ValueError(f"unexpected staging file retained: {path}")
            info = path.stat()
            if info.st_nlink != 1:
                raise ValueError(f"hardlinked staging file retained: {path}")
            exclusive(path)
            files.append((path, info.st_size, info.st_mtime_ns, info.st_ino, digest(path)))
    for path, size, modified, inode, checksum in files:
        plain(path)
        info = path.stat()
        if (info.st_size, info.st_mtime_ns, info.st_ino, digest(path)) != (size, modified, inode, checksum):
            raise ValueError("staging changed before release")
        exclusive(path)
    before = shutil.disk_usage(stage).free
    for path, *_ in files:
        plain(path).unlink()
    return dict(deleted_files=len(files), deleted_logical_bytes=sum(row[1] for row in files),
                free_before=before, free_after=shutil.disk_usage(stage).free)


def export_files(export: Path) -> dict:
    manifest = json.loads((export / "export.json").read_text(encoding="utf-8"))
    files = {name: ref for facet in manifest["facets"].values() for name, ref in facet["files"].items()}
    for name, ref in files.items():
        if Path(name).name != name:
            raise ValueError("invalid export manifest filename")
        path = plain(export / name)
        if path.stat().st_size != ref["bytes"] or digest(path, "sha1") != ref["sha1"]:
            raise ValueError(f"export manifest readback failed: {name}")
    return files


def run_checks(out: Path, tool, staging_root: Path | None = None) -> list[str]:
    from guo import load_config

    project = out / "world_project"
    if not (project / "blocks/0/187_203.json").is_file():
        return ["the overlay stage left no world project block to export"]
    receipt = {"completed": False, "released": False}
    stage = None
    evidence = None
    try:
        cfg = load_config()
        required = peak_bytes(project, cfg.client_data)
        parent = out / "ultimalive"
        parent.mkdir(exist_ok=True)
        evidence = Path(tempfile.mkdtemp(prefix="cycle-", dir=parent))
        stage, receipt["capacity"] = create_stage(
            staging_root or Path(tempfile.gettempdir()) / "_disposable/guo/editor-smoke",
            required, (out, cfg.client_data, cfg.root))
        receipt.update(stage=str(stage), evidence=str(evidence))
        tools = Path(__file__).resolve().parents[1]
        receipt["producer_sha256"] = {str(p.relative_to(tools)): digest(p) for p in
                                      (Path(__file__), tools / "world/run.py", tools / "guo/uomap.py")}
        enrollment = dict(schema="org.storage.cache.v1", producer="guo-editor-smoke",
                          recipe=f"tools/editor_smoke/run.py; exact producer hashes, overlay snapshots, install fingerprint and export manifests at {evidence}",
                          completed=False, regenerable=True, pins=[str(evidence)])
        write_json(stage / ".org-cache.json", enrollment)
        first, second = evidence / "inputs-first", evidence / "inputs-second"
        snapshot(project, first)
        snapshot(project, second)
        block = second / "blocks/0/187_203.json"
        data = json.loads(block.read_text(encoding="utf-8"))
        data["statics"].append({"id": "0x0CE3", "x": 7, "y": 7, "z": 0, "hue": "0x0000"})
        write_json(block, data)
        exp1, exp2, replay = stage / "ul_export1", stage / "ul_export2", stage / "retrieval"
        root = stage / "ultimalive_root"
        common = ["--ultimalive-root", str(root), "--ultimalive-shard", "GUO-Smoke"]

        def call(*args):
            code, text = tool(*args)
            with (evidence / "commands.log").open("a", encoding="utf-8") as log:
                log.write(f"{args!r}\nexit={code}\n{text}\n")
            if code:
                raise ValueError(f"world {args[0]} failed (exit {code}); see {evidence / 'commands.log'}")
            return text

        call("export", "--project", str(first), "--out", str(exp1), *common)
        export_files(exp1)
        shutil.copyfile(exp1 / "export.json", evidence / "first-export.json")
        shard = root / "GUO-Smoke"
        shard.mkdir(parents=True)
        for name in ("map0.mul", "staidx0.mul", "statics0.mul"):
            shutil.copyfile(exp1 / name, shard / name)
        text = call("ultimalive", "--project", str(first), "--out", str(exp1), *common)
        fresh = "already has this export" in text and "WARNING" not in text
        text = call("export", "--project", str(second), "--out", str(exp2), *common)
        warned = "WARNING" in text and "lacks 1 of this export's 1 block(s)" in text and "--clear-ultimalive" in text
        expected = export_files(exp2)
        shutil.copyfile(exp2 / "export.json", evidence / "second-export.json")
        text = call("ultimalive", "--project", str(second), "--out", str(exp2), *common, "--clear-ultimalive")
        cleared = "removed" in text and not (shard / "map0.mul").exists()
        # Recovery consumes only the saved overlay plus the user's original
        # install. Compare exact payload bytes, then read back independently.
        call("export", "--project", str(second), "--out", str(replay), *common)
        retrieved = export_files(replay) == expected
        call("verify", "--project", str(second), "--out", str(replay))
        receipt["checks"] = dict(fresh=fresh, warned=warned, cleared=cleared, retrieved=retrieved)
        if not all(receipt["checks"].values()):
            raise ValueError(f"UltimaLive checks failed: {receipt['checks']}")
        # All world commands are synchronous and have exited. Failure or an
        # interruption before here retains the stage pinned for diagnosis.
        enrollment.update(completed=True, completed_at=time.time(), pins=[])
        write_json(stage / ".org-cache.json", enrollment)
        receipt["release"] = release(stage)
        receipt.update(completed=True, released=True)
        print("[editor_smoke]   ok   UltLive fresh/stale/clear, saved-overlay retrieval and payload release")
        return []
    except (OSError, ValueError, KeyError) as error:
        receipt["error"] = str(error)
        return [f"UltimaLive staging: {error}"]
    finally:
        if evidence is not None:
            write_json(evidence / "receipt.json", receipt)
