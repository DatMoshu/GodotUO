"""A hosted catalogue end to end, on this computer only: sign and push to a Pages branch, serve it, and let the
client's own C# approve, list and install from it as a federated catalogue. Exit 0 PASS, 1 FAIL.

A bare git repository stands in for GitHub; a loopback HTTP server stands in for Pages and for a pack author's
own host. Nothing leaves this computer. `--serve` keeps the site up afterwards for the client scenario
(tools/scenarios/client/store_hosted_catalogue.scenario.json) and prints the --var it needs.
"""
from __future__ import annotations

import argparse
import functools
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store import catalogue, ed25519, hosting
from asset_store.run import Handler
from http.server import ThreadingHTTPServer

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "tools/asset_store/headless/StoreSmoke.csproj"
HEADLESS = ROOT / "tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll"
META = {"id": "proof-catalogue", "title": "Hosted catalogue proof"}


def pack(folder: Path, pack_id: str, ver: str, note_text: str = "") -> Path:
    # A 1x1 PNG; the bytes differ per pack so every ZIP has its own hash.
    image = bytes.fromhex("89504e470d0a1a0a0000000d4948445200000001000000010806000000"
                          "1f15c4890000000d4944415478da63f8ffff3f0005fe02fe0dbd5d9a0000000049454e44ae426082")
    note = (note_text or f"{pack_id} {ver}").encode()
    manifest = dict(schema="guo/store-pack@1", id=pack_id, version=ver, kind="background", title=f"Proof {pack_id}",
                    author="GUO proof", licence="CC0-1.0", min_profile_version=6, preview="still.png",
                    files={"still.png": hashlib.sha256(image).hexdigest(), "note.txt": hashlib.sha256(note).hexdigest()})
    path = folder / f"{pack_id}-{ver}.zip"
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("manifest.json", json.dumps(manifest))
        z.writestr("still.png", image)
        z.writestr("note.txt", note)
    return path


def writable(function, path, _exc) -> None:
    # git marks its object files read-only; Windows will not delete them until they are writable again.
    os.chmod(path, 0o700)
    function(path)


class Quiet(Handler):
    def log_message(self, *args):
        pass


def serve(folder: Path) -> ThreadingHTTPServer:
    httpd = ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Quiet, directory=str(folder)))
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    return httpd


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, help="write proof.json and the pushed site's file list here (evidence)")
    parser.add_argument("--serve", action="store_true", help="keep the hosted site up afterwards, until Ctrl+C")
    parser.add_argument("--dotnet", default=shutil.which("dotnet") or "dotnet")
    args = parser.parse_args()
    steps: list[dict] = []
    servers: list[ThreadingHTTPServer] = []

    def record(name: str, ok: bool, detail: str) -> None:
        steps.append({"step": name, "ok": ok, "detail": detail})
        print(f"[hosted catalogue] {'PASS' if ok else 'FAIL'} {name}: {detail}", flush=True)
        if not ok:
            raise ValueError(f"{name}: {detail}")

    # A commit needs a name; the proof's own, so it never borrows the user's git identity.
    for key, value in (("GIT_AUTHOR_NAME", "GUO catalogue proof"), ("GIT_AUTHOR_EMAIL", "proof@example.com"),
                       ("GIT_COMMITTER_NAME", "GUO catalogue proof"), ("GIT_COMMITTER_EMAIL", "proof@example.com")):
        os.environ[key] = value
    temporary = tempfile.TemporaryDirectory(prefix="guo-hosted-proof-", ignore_cleanup_errors=True)
    root = Path(temporary.name)
    result = 1
    try:
        built = subprocess.run([args.dotnet, "build", str(PROJECT), "-v", "q", "-nologo"], capture_output=True, text=True, timeout=600)
        record("headless client builds", built.returncode == 0, "StoreSmoke (the client's Store sources)" if not built.returncode else built.stdout[-800:])

        key = root / "catalogue.key"
        public = catalogue.keygen(key)
        secret = catalogue.read_secret(key)
        remote = root / "remote.git"
        hosting.git("init", "--quiet", "--bare", str(remote))
        repo = root / "catalogue-repo"
        hosting.init_repo(repo, META["id"], META["title"], public)
        record("init-catalogue", (repo / ".github/workflows/publish.yml").is_file()
               and f"UO_STORE_CATALOGUE_ID: {META['id']}" in (repo / ".github/workflows/publish.yml").read_text(),
               f"repository template written, key {ed25519.fingerprint(public)}")

        # A pack author's own host, named by a listing; and a pack hosted on the catalogue's site itself.
        author = root / "author-host"
        author.mkdir()
        listed_zip = pack(author, "proof-listed", "1.0.0")
        author_url = f"http://127.0.0.1:{(httpd := serve(author)).server_port}"
        servers.append(httpd)
        listing = repo / "packs/proof-listed"
        listing.mkdir(parents=True)
        (listing / "1.0.0.json").write_text(json.dumps({"urls": [f"{author_url}/{listed_zip.name}"],
            "sha256": hashlib.sha256(listed_zip.read_bytes()).hexdigest(), "size": listed_zip.stat().st_size,
            "provenance": "generated by the proof"}))
        packs_dir = root / "zips"
        packs_dir.mkdir()

        first = hosting.push(str(remote), secret, META, listing=repo / "packs", packs=[pack(packs_dir, "proof-hosted", "1.0.0")],
                             require_https=False, expires_days=30)
        record("first push", first["pushed"] and not first["existed"] and len(first["index"]["packs"]) == 2,
               f"new gh-pages branch, sequence {first['index']['sequence']}, 2 packs")
        second = hosting.push(str(remote), secret, META, listing=repo / "packs", packs=[pack(packs_dir, "proof-hosted", "1.1.0")],
                              require_https=False, expires_days=30)
        versions = sorted(f'{p["manifest"]["id"]} {p["manifest"]["version"]}' for p in second["index"]["packs"])
        record("second push", second["existed"] and second["index"]["sequence"] > first["index"]["sequence"] and len(versions) == 3,
               f"sequence {second['index']['sequence']} > {first['index']['sequence']}; still lists {', '.join(versions)}")
        commits = hosting.git("--git-dir", str(remote), "rev-list", "--count", "gh-pages").stdout.strip()
        record("history kept", commits == "2", f"gh-pages has {commits} commits, none rewritten")

        try:
            hosting.push(str(remote), secret, META, packs=[pack(root, "proof-hosted", "1.0.0", "changed bytes")], require_https=False)
            record("immutable versions", False, "a changed proof-hosted 1.0.0 was accepted")
        except ValueError as refused:
            record("immutable versions", "different bytes" in str(refused), str(refused))

        pages = root / "pages"
        hosting.git("clone", "--quiet", "--branch", "gh-pages", str(remote), str(pages))  # as a Pages checkout would, autocrlf and all
        shutil.rmtree(pages / ".git", onexc=writable)
        site = serve(pages)
        servers.append(site)
        url = f"http://127.0.0.1:{site.server_port}/"
        raw = (pages / "index.json").read_bytes()
        index = catalogue.check(raw, (pages / "index.json.sig").read_text(encoding="ascii"), public)
        record("index verifies", index["sequence"] == second["index"]["sequence"], f"sequence {index['sequence']} signed by {ed25519.fingerprint(public)}")

        installed = root / "client-store"

        def client(step: str, *extra: str) -> str:
            out = subprocess.run([args.dotnet, str(HEADLESS), "catalogue", step, url, str(installed), *extra],
                                 capture_output=True, text=True, timeout=180)
            text = (out.stdout + out.stderr).strip()
            record(f"client {step}{' ' + extra[0] if extra else ''}", out.returncode == 0, text.splitlines()[-1] if text else "no output")
            return text

        client("expect-approval")
        client("approve")
        listed = client("list")
        record("federated listing", "3 pack(s)" in listed and "signed True" in listed, "both hosts reachable from one signed index")
        client("install", "proof-listed")
        record("listed pack installed", (installed / "proof-listed/1.0.0/note.txt").is_file(), "fetched from the author's host")
        result = 0
        if args.serve:
            print(f"[hosted catalogue] serving {url} ; run the client scenario with --var catalogue={url}", flush=True)
            try:
                threading.Event().wait()
            except KeyboardInterrupt:
                pass
    except (OSError, ValueError, subprocess.TimeoutExpired) as exc:
        if not steps or steps[-1]["ok"]:
            steps.append({"step": "error", "ok": False, "detail": str(exc)})
            print(f"[hosted catalogue] FAIL: {exc}", file=sys.stderr)
    finally:
        if args.out:
            args.out.mkdir(parents=True, exist_ok=True)
            (args.out / "proof.json").write_text(json.dumps({"result": "PASS" if result == 0 else "FAIL", "steps": steps}, indent=2) + "\n")
        for httpd in servers:
            httpd.shutdown()
            httpd.server_close()
        temporary.cleanup()
    print(f"[hosted catalogue] {'PASS' if result == 0 else 'FAIL'}: {sum(s['ok'] for s in steps)}/{len(steps)} steps", flush=True)
    return result


if __name__ == "__main__":
    raise SystemExit(main())
