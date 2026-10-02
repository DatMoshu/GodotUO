"""Signed catalogue indexes, guo/store-index@2 (ADR-0026). Standard library only.

A catalogue is index.json plus index.json.sig, an Ed25519 signature over the
index's exact bytes. Each entry pins a pack ZIP's SHA-256 and size and lists
the URLs it can be fetched from, on any host, tried in order.

Two ways to build one:

- From a store folder (`packs/<id>/<version>.zip`, as `run.py publish` lays
  out): every pack is local, and its URL is relative to the index, or under
  `--base-url`. Optional `listing/<id>/<version>.json` files add a
  provenance line and mirror URLs.
- From a listing folder (the catalogue repository's `packs/<id>/<version>.json`
  entries, each naming its own URLs, hash and size): each ZIP is downloaded
  once into a cache and verified before it is listed.
"""
from __future__ import annotations

import datetime as dt
import json
import os
import shutil
import tempfile
import urllib.request
import zipfile
from pathlib import Path
from urllib.parse import urlsplit

from asset_store import ed25519
from asset_store.pack import identifier, require, sha256, verify, version

INDEX_SCHEMA = "guo/store-index@2"
SECRET_PREFIX = "guo-catalogue-secret "
LISTING_FIELDS = {"urls", "sha256", "size", "provenance"}
MAX_PROVENANCE = 500


# ---------------------------------------------------------------- keys


def keygen(path: Path) -> bytes:
    """Writes a new secret key file and returns the public key. Refuses to overwrite."""
    path = Path(path)
    secret = os.urandom(32)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="ascii") as stream:
        stream.write(SECRET_PREFIX + ed25519.encode(secret) + "\n")
    return ed25519.public_key(secret)


def read_secret(path: Path) -> bytes:
    text = Path(path).read_text(encoding="ascii").strip()
    require(text.startswith(SECRET_PREFIX), f"{path} is not a GUO catalogue secret key")
    return ed25519.decode(text[len(SECRET_PREFIX):], 32)


# ---------------------------------------------------------------- urls


def check_url(url: str) -> str:
    """HTTPS anywhere; plain HTTP only on loopback or a private LAN address; a relative path as is."""
    require(isinstance(url, str) and 0 < len(url) <= 2048 and not any(ord(c) < 33 for c in url), "invalid URL")
    parts = urlsplit(url)
    if not parts.scheme:
        require(not url.startswith("/") and ".." not in url.split("/") and "\\" not in url, "a relative URL must stay under the index")
        return url
    require(parts.scheme in ("https", "http") and parts.hostname and not parts.username and not parts.password
            and not parts.fragment, f"URL must be http(s) with a host and no credentials: {url}")
    if parts.scheme == "http":
        require(local_host(parts.hostname), f"plain HTTP is only allowed on loopback or a LAN address: {url}")
    return url


def local_host(host: str) -> bool:
    import ipaddress
    if host == "localhost" or host.endswith(".localhost") or host.endswith(".lan") or host.endswith(".local"):
        return True
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        return False
    return address.is_loopback or address.is_private or address.is_link_local


# ---------------------------------------------------------------- entries


def read_listing(path: Path) -> dict:
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    require(isinstance(raw, dict) and not set(raw) - LISTING_FIELDS, f"{path}: unknown listing field")
    urls = raw.get("urls", [])
    require(isinstance(urls, list) and len(urls) <= 16, f"{path}: urls must be a list of at most 16")
    for url in urls:
        check_url(url)
    provenance = raw.get("provenance", "")
    require(isinstance(provenance, str) and len(provenance) <= MAX_PROVENANCE
            and not any(ord(c) < 32 for c in provenance), f"{path}: invalid provenance")
    return raw


def entry(manifest: dict, zip_path: Path, urls: list[str], preview_url: str, provenance: str) -> dict:
    require(urls, f'{manifest["id"]} {manifest["version"]}: no URL to fetch it from')
    return {
        "manifest": manifest,
        "sha256": sha256(zip_path),
        "size": zip_path.stat().st_size,
        "urls": [check_url(u) for u in urls],
        "preview_url": check_url(preview_url),
        "provenance": provenance,
    }


def extract_preview(zip_path: Path, manifest: dict, site: Path) -> str:
    relative = f'previews/{manifest["id"]}/{manifest["version"]}/{manifest["preview"]}'
    target = (site / relative).resolve()
    require(site.resolve() in target.parents, "preview escapes the catalogue")
    target.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(zip_path) as archive, archive.open(manifest["preview"]) as src, target.open("wb") as dst:
        shutil.copyfileobj(src, dst)
    return relative


def entries_from_store(root: Path, base_url: str | None) -> list[dict]:
    root = Path(root).resolve()
    out = []
    for path in sorted((root / "packs").glob("*/*.zip")):
        require(not path.is_symlink() and root in path.resolve().parents, "pack escapes store")
        m = verify(path)
        relative = path.relative_to(root).as_posix()
        require(relative == f'packs/{m["id"]}/{m["version"]}.zip', "published path disagrees with manifest")
        listing_file = root / "listing" / m["id"] / f'{m["version"]}.json'
        listing = read_listing(listing_file) if listing_file.is_file() else {}
        own = base_url.rstrip("/") + "/" + relative if base_url else relative
        preview = extract_preview(path, m, root)
        out.append(entry(m, path, [own, *listing.get("urls", [])], preview, listing.get("provenance", "")))
    return out


def fetch(url: str, target: Path, size: int) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": "GUO-catalogue/1"})
    with urllib.request.urlopen(request, timeout=60) as response, target.open("wb") as dst:
        received = 0
        while chunk := response.read(1 << 20):
            received += len(chunk)
            require(received <= size, f"{url}: larger than its listing says")
            dst.write(chunk)


def entries_from_listing(listing_dir: Path, cache: Path, site: Path, require_https: bool = True,
                         mirrors: list[str] | None = None) -> list[dict]:
    """The catalogue repository's packs/<id>/<version>.json entries; each ZIP is fetched once and verified.

    Each base URL in mirrors (a host that runs `run.py mirror` against this catalogue) is listed first,
    as <base>packs/<id>/<version>.zip. Until a mirror has synced, clients fall through to the next URL."""
    out = []
    cache.mkdir(parents=True, exist_ok=True)
    for path in sorted(Path(listing_dir).glob("*/*.json")):
        pack_id, pack_version = path.parent.name, path.stem
        identifier(pack_id)
        version(pack_version)
        listing = read_listing(path)
        for key in ("urls", "sha256", "size"):
            require(key in listing, f"{path}: missing {key}")
        require(isinstance(listing["size"], int) and 0 < listing["size"] <= 512 * 1024 * 1024, f"{path}: invalid size")
        require(isinstance(listing["sha256"], str) and len(listing["sha256"]) == 64, f"{path}: invalid sha256")
        require(not require_https or all(urlsplit(u).scheme == "https" for u in listing["urls"]), f"{path}: public listings use HTTPS URLs")
        cached = cache / f'{listing["sha256"]}.zip'
        if not cached.is_file() or sha256(cached) != listing["sha256"]:
            errors = []
            for url in listing["urls"]:
                partial = cached.with_suffix(".part")
                try:
                    fetch(url, partial, listing["size"])
                    if sha256(partial) == listing["sha256"]:
                        os.replace(partial, cached)
                        break
                    errors.append(f"{url}: hash mismatch")
                except (OSError, ValueError) as ex:
                    errors.append(f"{url}: {ex}")
                finally:
                    partial.unlink(missing_ok=True)
            require(cached.is_file(), f"{path}: no URL served the listed bytes ({'; '.join(errors)})")
        m = verify(cached)
        require((m["id"], m["version"]) == (pack_id, pack_version), f"{path}: the ZIP is {m['id']} {m['version']}")
        require(cached.stat().st_size == listing["size"], f"{path}: size disagrees")
        preview = extract_preview(cached, m, site)
        hosted = [base.rstrip("/") + f"/packs/{pack_id}/{pack_version}.zip" for base in mirrors or []]
        out.append(entry(m, cached, [*hosted, *[u for u in listing["urls"] if u not in hosted]], preview, listing.get("provenance", "")))
    return out


def fetch_index(base_url: str) -> tuple[bytes, str]:
    base = base_url.rstrip("/") + "/"
    with urllib.request.urlopen(urllib.request.Request(base + "index.json", headers={"User-Agent": "GUO-catalogue/1"}), timeout=60) as r:
        raw = r.read(8 * 1024 * 1024 + 1)
    require(len(raw) <= 8 * 1024 * 1024, "index too large")
    with urllib.request.urlopen(urllib.request.Request(base + "index.json.sig", headers={"User-Agent": "GUO-catalogue/1"}), timeout=60) as r:
        signature = r.read(1024).decode("ascii")
    return raw, signature


def mirror(base_url: str, public: bytes, out: Path) -> dict:
    """Copies a signed catalogue into out, laid out as packs/<id>/<version>.zip and previews/..., every ZIP
    checked against the signed hash before it is kept. The index and its signature are copied byte for byte
    last, so a client reading the mirror never sees an index whose packs are not there yet."""
    out = Path(out)
    raw, signature = fetch_index(base_url)
    seen = previous_sequence(out, json.loads(raw).get("catalogue", {}).get("id", ""))
    index = check(raw, signature, public, seen_sequence=seen)
    base = base_url.rstrip("/") + "/"
    for item in index["packs"]:
        m = item["manifest"]
        identifier(m["id"])
        version(m["version"])
        target = out / "packs" / m["id"] / f'{m["version"]}.zip'
        if not (target.is_file() and target.stat().st_size == item["size"] and sha256(target) == item["sha256"]):
            target.parent.mkdir(parents=True, exist_ok=True)
            partial = target.with_suffix(".part")
            errors = []
            for url in item["urls"]:
                url = check_url(url if urlsplit(url).scheme else base + url)
                try:
                    fetch(url, partial, item["size"])
                    if partial.stat().st_size == item["size"] and sha256(partial) == item["sha256"]:
                        os.replace(partial, target)
                        break
                    errors.append(f"{url}: hash mismatch")
                except (OSError, ValueError) as ex:
                    errors.append(f"{url}: {ex}")
                finally:
                    partial.unlink(missing_ok=True)
            require(target.is_file(), f'{m["id"]} {m["version"]}: no URL served the signed bytes ({"; ".join(errors)})')
        verify(target)
        extract_preview(target, m, out)
    for name, data in (("index.json.sig", signature.encode("ascii")), ("index.json", raw)):
        fd, temporary = tempfile.mkstemp(dir=out, suffix=".tmp")
        try:
            with os.fdopen(fd, "wb") as stream:
                stream.write(data)
            os.replace(temporary, out / name)
        finally:
            Path(temporary).unlink(missing_ok=True)
    return index


# ---------------------------------------------------------------- index


def now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc).replace(microsecond=0)


def stamp(value: dt.datetime) -> str:
    return value.strftime("%Y-%m-%dT%H:%M:%SZ")


def previous_sequence(site: Path, catalogue_id: str) -> int:
    path = Path(site) / "index.json"
    try:
        old = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return 0
    if old.get("schema") != INDEX_SCHEMA or old.get("catalogue", {}).get("id") != catalogue_id:
        return 0
    return int(old.get("sequence", 0))


def build(site: Path, entries: list[dict], catalogue: dict, secret: bytes, expires_days: int | None = None) -> dict:
    """Writes index.json and index.json.sig into site, with a sequence higher than any before it."""
    site = Path(site)
    identifier(catalogue.get("id"))
    require(isinstance(catalogue.get("title"), str) and 0 < len(catalogue["title"]) <= 200, "catalogue title required")
    if catalogue.get("homepage"):
        check_url(catalogue["homepage"])
    keys = [(e["manifest"]["id"], e["manifest"]["version"]) for e in entries]
    require(len(keys) == len(set(keys)), "a pack version is listed twice")
    entries = sorted(entries, key=lambda e: (e["manifest"]["id"], version(e["manifest"]["version"])))
    issued = now()
    index = {
        "schema": INDEX_SCHEMA,
        "catalogue": {k: catalogue[k] for k in ("id", "title", "homepage") if catalogue.get(k)},
        "key": ed25519.encode(ed25519.public_key(secret)),
        # Seconds since 1970 keep the sequence rising on a fresh CI checkout that has no old
        # index; the previous index's number keeps it rising when two builds share a second.
        "sequence": max(previous_sequence(site, catalogue["id"]) + 1, int(issued.timestamp())),
        "issued": stamp(issued),
        "packs": entries,
    }
    if expires_days:
        index["expires"] = stamp(issued + dt.timedelta(days=expires_days))
    raw = (json.dumps(index, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    signature = ed25519.encode(ed25519.sign(secret, raw)) + "\n"
    site.mkdir(parents=True, exist_ok=True)
    # Each file is replaced atomically. A reader between the two replacements sees a mismatched
    # pair, refuses it as a bad signature, and fetches again.
    for name, data in (("index.json.sig", signature.encode("ascii")), ("index.json", raw)):
        fd, temporary = tempfile.mkstemp(dir=site, suffix=".tmp")
        try:
            with os.fdopen(fd, "wb") as stream:
                stream.write(data)
            os.replace(temporary, site / name)
        finally:
            Path(temporary).unlink(missing_ok=True)
    return index


def check(raw: bytes, signature_text: str, public: bytes | None, seen_sequence: int = 0, at: dt.datetime | None = None) -> dict:
    """What the client does with a fetched index, for tests and `run.py check-index`."""
    signature = ed25519.decode(signature_text, 64)
    index = json.loads(raw.decode("utf-8"))
    require(index.get("schema") == INDEX_SCHEMA, "not a guo/store-index@2 index")
    listed = ed25519.decode(index.get("key", ""), 32)
    if public is not None:
        require(listed == public, "the index names a different key than the one trusted for this catalogue")
    require(ed25519.verify(listed, raw, signature), "the index signature does not verify")
    require(type(index.get("sequence")) is int and index["sequence"] >= seen_sequence, "the index is older than one already seen (rollback)")
    if "expires" in index:
        expires = dt.datetime.strptime(index["expires"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=dt.timezone.utc)
        require((at or now()) <= expires, "the index has expired")
    return index
