"""Hosting a signed catalogue as a static site (ADR-0026): a catalogue repository of your own, and the
publisher that signs an index and pushes it to a GitHub Pages branch. Standard library plus the git CLI.

The site a push writes is the branch's whole tree (docs/data_formats.md section 12, "A hosted catalogue"):

    index.json, index.json.sig   the signed index; the sequence rises past the one already on the branch
    previews/<id>/<version>/...  one preview per listed pack version
    packs/<id>/<version>.zip     packs hosted on the site itself (--pack); listings name their own URLs
    index.html, .nojekyll        the catalogue's web page; Pages serves files as they are
    .gitattributes               `* -text`: git never rewrites line endings, so the signed bytes are the served bytes

`push` never force-pushes. If the branch moved after it was read, the push is refused and nothing changes.
"""
from __future__ import annotations

import shutil
import subprocess
import tempfile
from pathlib import Path

from asset_store import catalogue, ed25519
from asset_store.pack import identifier, require, sha256, verify

HERE = Path(__file__).resolve().parent
TEMPLATE = HERE / "catalogue_repo"
OFFICIAL_ID, OFFICIAL_TITLE = "guo-official", "GUO packs"

README = """# {title}

A catalogue of packs for [GodotUO](https://github.com/DatMoshu/GodotUO), signed with its own key. Players add
its address in **Options, Video, Store, Catalogues**; the client asks them to approve the key's fingerprint once.

- **Catalogue id:** `{id}`
- **Signing key fingerprint:** {fingerprint}

## Listings

One JSON file per pack version, at `packs/<id>/<version>.json` (see `packs/README.md`). A pull request that adds
one is checked by `.github/workflows/check.yml`; a merge to `main` signs and deploys the catalogue
(`.github/workflows/publish.yml`, which needs the `CATALOGUE_SIGNING_KEY` secret and Pages set to GitHub Actions).

To publish from your own computer instead, keep the key there and run:

    python tools/asset_store/run.py push-catalogue --listing packs --remote <this repository's git URL>

GodotUO's `docs/store/hosting.md` covers both ways.
"""


def git(*args, cwd: Path | None = None, check: bool = True) -> subprocess.CompletedProcess:
    out = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, timeout=300)
    if check and out.returncode:
        # The remote is never echoed: a URL can carry a token.
        raise ValueError(f"git {args[0]} failed: {(out.stderr or out.stdout).strip().splitlines()[-1:] or ['no output']}")
    return out


def init_repo(target: Path, catalogue_id: str, title: str, public: bytes | None = None) -> list[Path]:
    """Writes a catalogue repository of your own into an empty folder: README, listing folder, licence, the
    pull-request check and the publish workflow, with this catalogue's id and title. No key is written."""
    identifier(catalogue_id)
    require(catalogue_id != OFFICIAL_ID, f"{OFFICIAL_ID} is the official catalogue's id; choose your own")
    require(isinstance(title, str) and 0 < len(title) <= 200 and not any(ord(c) < 32 for c in title), "catalogue title required")
    target = Path(target)
    require(not target.exists() or not any(target.iterdir()), f"{target} is not empty")
    fingerprint = f"`{ed25519.fingerprint(public)}`" if public else "(set after `run.py keygen`)"
    files = {
        "README.md": README.format(title=title, id=catalogue_id, fingerprint=fingerprint),
        "packs/README.md": (TEMPLATE / "packs/README.md").read_text(encoding="utf-8"),
        "LICENSE": (TEMPLATE / "LICENSE").read_text(encoding="utf-8"),
        ".gitignore": "*.key\nsite/\n",
        ".github/workflows/check.yml": (TEMPLATE / ".github/workflows/check.yml").read_text(encoding="utf-8"),
    }
    publish = (TEMPLATE / ".github/workflows/publish.yml").read_text(encoding="utf-8")
    for old, new in ((f"UO_STORE_CATALOGUE_ID: {OFFICIAL_ID}", f"UO_STORE_CATALOGUE_ID: {catalogue_id}"),
                     (f"UO_STORE_CATALOGUE_TITLE: {OFFICIAL_TITLE}", f"UO_STORE_CATALOGUE_TITLE: '{title.replace(chr(39), chr(39) * 2)}'")):
        require(old in publish, "the publish workflow template changed; update hosting.init_repo")
        publish = publish.replace(old, new)
    files[".github/workflows/publish.yml"] = publish
    written = []
    for relative, text in files.items():
        path = target / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="\n")
        written.append(path)
    return written


def _checkout(remote: str, branch: str, work: Path) -> bool:
    """The branch's current tree in work, or an empty orphan branch there. True when the branch existed."""
    heads = git("ls-remote", "--heads", remote, branch).stdout.strip()
    if heads:
        git("clone", "--quiet", "--config", "core.autocrlf=false", "--depth", "1", "--single-branch", "--branch", branch, remote, str(work))
        return True
    git("init", "--quiet", str(work))
    git("config", "core.autocrlf", "false", cwd=work)
    git("checkout", "--quiet", "--orphan", branch, cwd=work)
    git("remote", "add", "origin", remote, cwd=work)
    return False


def _place_pack(zip_path: Path, site: Path) -> dict:
    m = verify(zip_path)
    target = site / "packs" / m["id"] / f'{m["version"]}.zip'
    if target.exists():
        require(sha256(target) == sha256(zip_path), f'{m["id"]} {m["version"]} is already published with different bytes')
    else:
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(zip_path, target)
    return m


def build_site(site: Path, secret: bytes, meta: dict, *, listing: Path | None = None, packs: list[Path] = (),
               cache: Path | None = None, mirrors: list[str] | None = None, expires_days: int | None = None,
               base_url: str | None = None, require_https: bool = True) -> dict:
    """Signs the site in place: packs placed under packs/, listings fetched and verified, one index over both."""
    require(listing or packs or (site / "packs").is_dir(), "nothing to list: give --listing, --pack, or both")
    for zip_path in packs:
        _place_pack(Path(zip_path), site)
    entries = catalogue.entries_from_store(site, base_url) if (site / "packs").is_dir() else []
    if listing:
        hosted = {(e["manifest"]["id"], e["manifest"]["version"]) for e in entries}
        with tempfile.TemporaryDirectory(prefix="guo-catalogue-cache-") as scratch:
            listed = catalogue.entries_from_listing(Path(listing), Path(cache or scratch), site, require_https, mirrors)
        clash = hosted & {(e["manifest"]["id"], e["manifest"]["version"]) for e in listed}
        require(not clash, f"listed and also hosted on the site: {sorted(clash)}")
        entries += listed
    index = catalogue.build(site, entries, meta, secret, expires_days)
    shutil.copyfile(HERE / "index.html", site / "index.html")
    (site / ".nojekyll").write_bytes(b"")
    (site / ".gitattributes").write_bytes(b"* -text\n")
    return index


def push(remote: str, secret: bytes, meta: dict, *, branch: str = "gh-pages", dry_run: bool = False,
         keep: Path | None = None, **site_args) -> dict:
    """Reads the Pages branch, signs a new index over it, commits, and pushes (fast-forward only).

    Returns {"index", "existed", "pushed", "work"}; "work" is kept only for dry_run with keep."""
    require(remote and not remote.startswith("-"), "a git remote is required")
    with tempfile.TemporaryDirectory(prefix="guo-catalogue-push-") as scratch:
        work = Path(keep) if keep else Path(scratch) / "site"
        if keep:
            require(not work.exists() or not any(work.iterdir()), f"{work} is not empty")
        existed = _checkout(remote, branch, work)
        index = build_site(work, secret, meta, **site_args)
        # A new index always has a new sequence, so there is always something to commit.
        git("add", "--all", cwd=work)
        message = f'catalogue {index["catalogue"]["id"]}: sequence {index["sequence"]}, {len(index["packs"])} pack(s)'
        git("commit", "--quiet", "-m", message, cwd=work)
        pushed = False
        if not dry_run:
            out = git("push", "--quiet", "origin", f"HEAD:refs/heads/{branch}", cwd=work, check=False)
            if out.returncode:
                text = (out.stderr or out.stdout).lower()
                require("rejected" not in text and "non-fast-forward" not in text and "fetch first" not in text,
                        f"the {branch} branch moved since it was read; nothing was pushed. Run push-catalogue again.")
                raise ValueError(f"git push failed: {(out.stderr or out.stdout).strip().splitlines()[-1:]}")
            pushed = True
        return {"index": index, "existed": existed, "pushed": pushed, "work": str(work) if keep else None}


def signer_from_env(key_path: Path | None, catalogue_id: str, title: str, homepage: str = "") -> tuple[bytes, dict]:
    require(key_path, "set UO_STORE_SIGNING_KEY to the catalogue's secret key file (run.py keygen)")
    meta = {"id": catalogue_id, "title": title}
    if homepage:
        meta["homepage"] = homepage
    return catalogue.read_secret(key_path), meta
