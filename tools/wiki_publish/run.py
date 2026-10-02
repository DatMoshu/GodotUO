"""Turn docs/wiki into the pages of the repository's GitHub Wiki.

docs/wiki is the source: reviewed and merged like any other change. GitHub's
wiki is a separate git repository with its own link rules, so this writes a
copy for it:

- `Page.md` and `Page.md#part` links become `Page` and `Page#part` (wiki slugs);
- links that leave docs/wiki (`../x.md`, `../../tools/y/README.md`) become
  absolute links to the file on GitHub, and images become raw links;
- README.md (about the folder, not a wiki page) is left out.

    python tools/wiki_publish/run.py --out DIR [--repo OWNER/NAME] [--ref main]
    python tools/wiki_publish/run.py --check          every relative link resolves; writes nothing

The `wiki` workflow runs it on every push to main that touches docs/wiki.
"""
import argparse
import os
import posixpath
import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WIKI = ROOT / "docs" / "wiki"
LINK = re.compile(r"(!?)\[([^\]]*)\]\(([^)\s]+)\)")
IMAGE_EXT = (".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg")


def pages():
    return sorted(p for p in WIKI.glob("*.md") if p.name != "README.md")


def convert(text, repo, ref, problems=None, page=""):
    def swap(m):
        bang, label, target = m.groups()
        if re.match(r"^[a-z][a-z0-9+.-]*:", target, re.I) or target.startswith("#"):
            return m.group(0)
        path, _, anchor = target.partition("#")
        anchor = f"#{anchor}" if anchor else ""
        if "/" not in path and path.endswith(".md"):
            if problems is not None and not (WIKI / path).is_file():
                problems.append(f"{page}: no wiki page {path}")
            return f"{bang}[{label}]({path[:-3]}{anchor})"
        resolved = posixpath.normpath(posixpath.join("docs/wiki", path))
        if problems is not None and (resolved.startswith("..") or not (ROOT / resolved).exists()):
            problems.append(f"{page}: {target} does not exist")
        kind = "raw" if bang or resolved.lower().endswith(IMAGE_EXT) else ("tree" if (ROOT / resolved).is_dir() else "blob")
        return f"{bang}[{label}](https://github.com/{repo}/{kind}/{ref}/{resolved}{anchor})"
    return LINK.sub(swap, text)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", type=Path, help="the wiki checkout (or any folder) to write the pages into")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "DatMoshu/GodotUO"))
    parser.add_argument("--ref", default="main")
    parser.add_argument("--check", action="store_true", help="check every relative link; write nothing")
    args = parser.parse_args()

    if args.check:
        problems = []
        for page in pages():
            convert(page.read_text(encoding="utf-8"), args.repo, args.ref, problems, page.name)
        for problem in problems:
            print(f"[wiki_publish] {problem}")
        print(f"[wiki_publish] {len(pages())} pages, {len(problems)} broken links")
        return 1 if problems else 0

    if not args.out:
        parser.error("--out or --check is required")
    args.out.mkdir(parents=True, exist_ok=True)
    keep = {p.name for p in pages()}
    for old in args.out.glob("*.md"):
        if old.name not in keep:
            old.unlink()  # a page removed from docs/wiki leaves the wiki too
    for page in pages():
        (args.out / page.name).write_text(convert(page.read_text(encoding="utf-8"), args.repo, args.ref),
                                          encoding="utf-8", newline="\n")
    print(f"[wiki_publish] wrote {len(keep)} pages to {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
