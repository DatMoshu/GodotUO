"""GUO asset CDN: serve, publish, verify, index. No third-party dependencies."""
from __future__ import annotations

import argparse
import functools
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import zipfile
from urllib.parse import urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import load_config
from asset_store.pack import INDEX_SCHEMA, require, sha256, verify, version
from asset_store import catalogue, ed25519


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(dir=path.parent, suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(value, stream, indent=2, ensure_ascii=False)
            stream.write("\n")
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def make_index(root, signer=None):
    """The store folder's index: a signed guo/store-index@2 when signer is (secret, catalogue, base_url),
    otherwise the unsigned v1 index a local store has always had."""
    root = Path(root).resolve()
    if signer:
        secret, meta, base_url = signer
        entries = catalogue.entries_from_store(root, base_url or None)
        catalogue.build(root, entries, meta, secret)
        shutil.copyfile(Path(__file__).with_name("index.html"), root / "index.html")
        return entries
    (root / "index.json.sig").unlink(missing_ok=True)
    packs = []
    for path in sorted((root / "packs").glob("*/*.zip")):
        require(not path.is_symlink() and root in path.resolve().parents, "pack escapes store")
        m = verify(path)
        require(path.relative_to(root).as_posix() == f'packs/{m["id"]}/{m["version"]}.zip', "published path disagrees with manifest")
        preview = f'previews/{m["id"]}/{m["version"]}/{m["preview"]}'
        target = root / preview
        require(root in target.resolve().parents, "preview escapes store")
        target.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(path) as archive:
            with archive.open(m["preview"]) as src, target.open("wb") as dst:
                shutil.copyfileobj(src, dst)
        packs.append(dict(m, url=path.relative_to(root).as_posix(), sha256=sha256(path), size=path.stat().st_size, preview_url=preview))
    packs.sort(key=lambda p: (p["id"], version(p["version"])))
    atomic_json(root / "index.json", {"schema": INDEX_SCHEMA, "packs": packs})
    shutil.copyfile(Path(__file__).with_name("index.html"), root / "index.html")
    return packs


def publish(path, root, signer=None):
    # Stage first: validation and publication operate on exactly the same bytes.
    root = Path(root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(dir=root, suffix=".tmp")
    os.close(fd)
    staged = Path(name)
    try:
        require(Path(path).stat().st_size <= 512 * 1024 * 1024, "ZIP too large")
        shutil.copyfile(path, staged)
        m = verify(staged)
        target = root / "packs" / m["id"] / (m["version"] + ".zip")
        require(root in target.resolve().parents, "destination escapes store")
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            require(sha256(target) == sha256(staged), "id/version already published with different bytes")
        else:
            # Exclusive create keeps an existing release immutable across publishers.
            os.link(staged, target)  # Atomic publication, never expose a partial ZIP.
        make_index(root, signer)
        return m
    finally:
        staged.unlink(missing_ok=True)


class Handler(SimpleHTTPRequestHandler):
    """Static files with single RFC byte ranges; no listings or symlink escape."""
    def send_head(self):
        self.byte_range = None
        path = Path(self.translate_path(self.path))
        root = Path(self.directory).resolve()
        if path.is_dir():
            path = path / "index.html"
        if root not in path.resolve().parents or not path.is_file() or path.suffix == ".tmp":
            self.send_error(404)
            return None
        stream = path.open("rb")
        size = os.fstat(stream.fileno()).st_size
        start, end = 0, size - 1
        requested = self.headers.get("Range")
        if requested:
            match = re.fullmatch(r"bytes=(\d*)-(\d*)", requested.strip())
            if not match or not any(match.groups()):
                stream.close()
                self.send_error(400, "Only one byte range is supported")
                return None
            first, last = match.groups()
            if first:
                start = int(first)
                end = min(int(last), size - 1) if last else size - 1
            else:
                start = max(0, size - int(last))
            if start >= size or end < start or (not first and int(last) == 0):
                stream.close()
                self.send_response(416)
                self.send_header("Content-Range", f"bytes */{size}")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return None
            self.byte_range = (start, end)
        self.send_response(206 if requested else 200)
        self.send_header("Content-Type", self.guess_type(str(path)))
        self.send_header("Content-Length", str(end - start + 1))
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Last-Modified", self.date_time_string(os.fstat(stream.fileno()).st_mtime))
        if requested:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        stream.seek(start)
        return stream

    def copyfile(self, source, outputfile):
        remaining = self.byte_range[1] - self.byte_range[0] + 1 if self.byte_range else None
        while remaining is None or remaining > 0:
            block = source.read(min(64 * 1024, remaining) if remaining is not None else 64 * 1024)
            if not block:
                break
            outputfile.write(block)
            if remaining is not None:
                remaining -= len(block)


def server(root, host="127.0.0.1", port=18865):
    return ThreadingHTTPServer((host, port), functools.partial(Handler, directory=str(Path(root).resolve())))


def main():
    config = load_config()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--store-dir", type=Path, default=config.store_dir)
    sub = parser.add_subparsers(dest="command", required=True)
    s = sub.add_parser("serve")
    s.add_argument("--host", default="127.0.0.1")
    s.add_argument("--port", type=int, default=urlsplit(config.store_url).port or 18865)
    sub.add_parser("index")
    for verb in ("verify", "publish"):
        sub.add_parser(verb).add_argument("pack", type=Path)
    k = sub.add_parser("keygen", help="write a new catalogue signing key; prints the public key")
    k.add_argument("key", type=Path)
    b = sub.add_parser("build-catalogue", help="a signed index from a catalogue repository's listing folder")
    b.add_argument("--listing", type=Path, required=True)
    b.add_argument("--site", type=Path, required=True)
    b.add_argument("--cache", type=Path, required=True)
    b.add_argument("--expires-days", type=int)
    b.add_argument("--mirror", action="append", default=[], help="a mirror's base URL, listed first for every pack")
    b.add_argument("--check", action="store_true", help="verify every listing and its ZIP; sign and write nothing (pull requests)")
    m = sub.add_parser("mirror", help="copy a signed catalogue into --store-dir, verifying every ZIP")
    m.add_argument("--from", dest="source", required=True, help="the catalogue's base URL")
    m.add_argument("--key", required=True, help="the catalogue's public key, ed25519:...")
    c = sub.add_parser("check-index", help="verify a store folder's signed index")
    c.add_argument("--key", help="the trusted public key (ed25519:...); default: the key the index names")
    args = parser.parse_args()
    signer = None
    if config.store_signing_key and args.command not in ("keygen", "check-index", "mirror"):
        signer = (catalogue.read_secret(config.store_signing_key),
                  {"id": config.store_catalogue_id, "title": config.store_catalogue_title}, config.store_base_url)
    try:
        if args.command == "keygen":
            public = catalogue.keygen(args.key)
            print(f"Secret key written to {args.key}. Keep it out of the repository and back it up offline.")
            print(f"Public key : {ed25519.encode(public)}")
            print(f"Fingerprint: {ed25519.fingerprint(public)}")
            return 0
        if args.command == "build-catalogue":
            if args.check:
                entries = catalogue.entries_from_listing(args.listing, args.cache, Path(tempfile.mkdtemp()), mirrors=args.mirror)
                print(f"Checked {len(entries)} pack(s): every listing names a ZIP that verifies")
                return 0
            require(signer, "set UO_STORE_SIGNING_KEY to sign the catalogue")
            entries = catalogue.entries_from_listing(args.listing, args.cache, args.site, mirrors=args.mirror)
            index = catalogue.build(args.site, entries, signer[1], signer[0], args.expires_days)
            print(f"Catalogue {index['catalogue']['id']} sequence {index['sequence']}: {len(entries)} pack(s)")
            return 0
        if args.command == "mirror":
            index = catalogue.mirror(args.source, ed25519.decode(args.key, 32), args.store_dir)
            print(f"Mirrored {index['catalogue']['id']} sequence {index['sequence']}: {len(index['packs'])} pack(s) into {args.store_dir}")
            return 0
        if args.command == "check-index":
            root = Path(args.store_dir)
            trusted = ed25519.decode(args.key, 32) if args.key else None
            index = catalogue.check((root / "index.json").read_bytes(), (root / "index.json.sig").read_text(encoding="ascii"), trusted)
            public = ed25519.decode(index["key"], 32)
            print(f"OK: {index['catalogue']['id']} sequence {index['sequence']}, {len(index['packs'])} pack(s), key {ed25519.fingerprint(public)}")
            return 0
        if args.command == "verify":
            m = verify(args.pack)
            print(f'Verified {m["id"]} {m["version"]}')
        elif args.command == "publish":
            m = publish(args.pack, args.store_dir, signer)
            print(f'Published {m["id"]} {m["version"]}')
        elif args.command == "index":
            print(f"Indexed {len(make_index(args.store_dir, signer))} packs{' (signed)' if signer else ''}")
        else:
            make_index(args.store_dir, signer)
            try:
                httpd = server(args.store_dir, args.host, args.port)
            except OSError as exc:
                if args.port == 0 or exc.errno not in {13, 48, 98, 10013, 10048} and getattr(exc, "winerror", None) not in {10013, 10048}:
                    raise
                print(f"Port {args.port} unavailable; using an ephemeral port. Set UO_STORE_URL to the listening URL below.", flush=True)
                httpd = server(args.store_dir, args.host, 0)
            with httpd:
                print(f"Store listening on http://{args.host}:{httpd.server_port}", flush=True)
                httpd.serve_forever()
        return 0
    except (ValueError, OSError, zipfile.BadZipFile, KeyError, TypeError) as exc:
        print(f"Store error: {exc}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
