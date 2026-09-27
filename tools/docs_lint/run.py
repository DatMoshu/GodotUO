"""Check local Markdown links and GitHub-style heading anchors (no network)."""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import html
from pathlib import Path
import re
import sys
import unicodedata
from urllib.parse import unquote, urlsplit


def without_fences(text):
    output, fence = [], None
    for line in text.splitlines(keepends=True):
        match = re.match(r"^ {0,3}(`{3,}|~{3,})", line)
        if match:
            marker = match[1]
            if fence is None:
                fence = marker
            elif marker[0] == fence[0] and len(marker) >= len(fence):
                fence = None
            output.append("\n")
        else:
            output.append(line if fence is None else "\n")
    return "".join(output)


def slug(title):
    title = re.sub(r"!?\[([^]]+)\]\([^)]*\)", r"\1", title)
    title = html.unescape(re.sub(r"<[^>]+>", "", title))
    title = title.replace("`", "").replace("*", "").strip().lower()
    return "".join(c for c in title if c in "-_ " or unicodedata.category(c)[0] not in "PS").replace(" ", "-")


def anchors(text):
    text = without_fences(text)
    text = re.sub(r"<!--.*?-->", lambda m: "\n" * m[0].count("\n"), text, flags=re.S)
    result, counts = set(), {}
    lines = text.splitlines()
    for i, line in enumerate(lines):
        heading = re.match(r"^ {0,3}#{1,6}\s+(.+?)\s*#*\s*$", line)
        title = heading[1] if heading else None
        if (i and re.fullmatch(r" {0,3}(?:=+|-+)\s*", line)
                and lines[i - 1].strip()
                and not re.match(r"^(?: {4}|\t| {0,3}(?:#{1,6}\s|>|[-+*]\s|\d+[.)]\s))", lines[i - 1])):
            title = lines[i - 1].strip()
        if title is not None:
            base = slug(title)
            number = counts.get(base, 0)
            identifier = base if number == 0 else f"{base}-{number}"
            while identifier in result:
                number += 1
                identifier = f"{base}-{number}"
            counts[base] = number + 1
            result.add(identifier)
    result.update(html.unescape(m[1]) for m in re.finditer(r'\b(?:id|name)=["\']([^"\']+)["\']', text))
    return result


def normalized(label):
    return " ".join(label.lower().split())


def links(text):
    text = without_fences(text)
    # Inline code and HTML comments are examples, not links rendered by Markdown.
    text = re.sub(r"<!--.*?-->", lambda m: "\n" * m[0].count("\n"), text, flags=re.S)
    text = re.sub(r"(`+).*?\1", lambda m: " " * len(m[0]), text)
    definitions = {}
    for match in re.finditer(r"^ {0,3}\[([^]]+)\]:\s*(<[^>]+>|\S+)", text, re.M):
        definitions[normalized(match[1])] = match[2].strip("<>")
        yield text.count("\n", 0, match.start()) + 1, match[2].strip("<>"), None
    for match in re.finditer(r"!?\[[^]\n]*\]\(\s*(<[^>]+>|(?:[^()\s]|\([^()]*\))+)(?:\s+[\"'][^\n]*?[\"'])?\s*\)", text):
        yield text.count("\n", 0, match.start()) + 1, match[1].strip("<>"), None
    for match in re.finditer(r"!?\[([^]\n]+)\]\[([^]\n]*)\]", text):
        key = normalized(match[2] or match[1])
        yield text.count("\n", 0, match.start()) + 1, definitions.get(key), None if key in definitions else key
    for match in re.finditer(r"\b(?:href|src)=[\"']([^\"']+)[\"']", text):
        yield text.count("\n", 0, match.start()) + 1, match[1], None


@dataclass(frozen=True)
class Issue:
    path: Path
    line: int
    message: str


def check(root, paths):
    root = Path(root).resolve()
    issues, cached = [], {}
    for path in paths:
        text = path.read_text(encoding="utf-8-sig")
        for line, target, missing_ref in links(text):
            if missing_ref:
                issues.append(Issue(path, line, f"undefined reference [{missing_ref}]"))
                continue
            target = html.unescape(target)
            parts = urlsplit(target)
            if parts.scheme or parts.netloc or target.startswith("//"):
                continue
            relative = unquote(parts.path)
            destination = (root / relative.lstrip("/") if relative.startswith("/") else path.parent / relative).resolve() if relative else path.resolve()
            if not destination.is_relative_to(root):
                issues.append(Issue(path, line, f"link leaves repository: {target}"))
            elif not destination.exists():
                issues.append(Issue(path, line, f"missing file: {target}"))
            elif parts.fragment and destination.suffix.lower() == ".md":
                if destination not in cached:
                    cached[destination] = anchors(destination.read_text(encoding="utf-8-sig"))
                if unquote(parts.fragment) not in cached[destination]:
                    issues.append(Issue(path, line, f"missing anchor: {target}"))
    return issues


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("paths", nargs="*", help="Markdown files or folders relative to the repository")
    args = parser.parse_args()
    root = args.root.resolve()
    paths = set()
    for name in args.paths or ["README.md", "CONTRIBUTING.md", "SECURITY.md", "docs"]:
        path = root / name
        if path.is_dir():
            paths.update(path.rglob("*.md"))
        elif path.is_file():
            paths.add(path)
        elif args.paths:
            print(f"docs_lint: missing input {name}")
            return 1
    issues = check(root, sorted(paths))
    for issue in issues:
        print(f"{issue.path.relative_to(root)}:{issue.line}: {issue.message}")
    print(f"docs_lint: {len(paths)} documents, {len(issues)} issues")
    return int(bool(issues))


if __name__ == "__main__":
    sys.exit(main())
