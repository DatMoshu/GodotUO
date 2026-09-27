"""Rewrite unpublished Git history in a throwaway clone, never in a worktree.

Replacement JSON is a private, external {"old": "new"} map; never commit it.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid


def git(repo, *args, data=None, check=True):
    result = subprocess.run(["git", "-C", str(repo), *args], input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if check and result.returncode:
        # Do not echo command arguments or Git output: either can contain private data.
        raise RuntimeError(f"git {args[0]} failed (exit {result.returncode})")
    return result


class Objects:
    def __init__(self, repo, replacements):
        self.repo, self.replacements = repo, replacements
        self.reader = subprocess.Popen(["git", "-C", str(repo), "cat-file", "--batch"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        self.cache = {}
        self.changed_blobs = 0

    def read(self, oid):
        self.reader.stdin.write(oid.encode() + b"\n"); self.reader.stdin.flush()
        header = self.reader.stdout.readline().split()
        if len(header) != 3:
            raise RuntimeError("Cannot read Git object")
        data = self.reader.stdout.read(int(header[2]))
        if self.reader.stdout.read(1) != b"\n":
            raise RuntimeError("Truncated Git object")
        return header[1].decode(), data

    def write(self, kind, data):
        return git(self.repo, "hash-object", "-w", "-t", kind, "--stdin", data=data).stdout.strip().decode()

    def replace(self, data):
        for old, new in self.replacements:
            data = data.replace(old, new)
        return data

    def tree(self, oid):
        if oid in self.cache:
            return self.cache[oid]
        kind, raw = self.read(oid)
        if kind == "blob":
            changed = self.replace(raw)
            result = self.write(kind, changed) if changed != raw else oid
            self.changed_blobs += result != oid
        elif kind == "tree":
            pos = 0; entries = []
            while pos < len(raw):
                end = raw.index(b"\0", pos)
                header = raw[pos:end]
                child = raw[end + 1:end + 21].hex()
                mode, name = header.split(b" ", 1)
                # Submodule commits refer to another repository: leave them intact.
                updated = child if mode == b"160000" else self.tree(child)
                new_name = self.replace(name)
                if b"/" in new_name or b"\0" in new_name:
                    raise ValueError("Replacement creates an invalid tree name")
                entries.append((mode, new_name, updated))
                pos = end + 21
            # A filename replacement can alter Git's tree ordering.
            entries.sort(key=lambda e: e[1] + (b"/" if e[0] == b"40000" else b""))
            changed = b"".join(mode + b" " + name + b"\0" + bytes.fromhex(child) for mode, name, child in entries)
            result = self.write(kind, changed) if changed != raw else oid
        else:
            raise RuntimeError("Unexpected tree object type")
        self.cache[oid] = result
        return result

    def close(self):
        self.reader.stdin.close(); self.reader.stdout.close(); self.reader.wait(timeout=10)


def rewrite(source, output, refs, replacements, publish=False, expected=None):
    source = Path(source).resolve(); output = Path(output).resolve()
    if output != source / "build" / "scrub":
        raise ValueError("Output must be <source>/build/scrub")
    baseline = git(source, "rev-parse", "refs/remotes/origin/main^{commit}").stdout.strip().decode()
    tips = {ref: git(source, "rev-parse", f"{ref}^{{commit}}").stdout.strip().decode() for ref in refs}
    if publish and (refs != ["main"] or expected != tips["main"]):
        raise ValueError("Publishing requires only main and its exact expected SHA")
    output.mkdir(parents=True, exist_ok=True)
    clone = output / ("clone-" + uuid.uuid4().hex)
    subprocess.run(["git", "clone", "--no-local", "--bare", "--quiet", str(source), str(clone)], check=True)
    objects = Objects(clone, replacements)
    mapping = {}
    try:
        commits = git(clone, "rev-list", "--reverse", "--topo-order", *tips.values(), "--not", baseline).stdout.decode().splitlines()
        for oid in commits:
            kind, raw = objects.read(oid)
            if kind != "commit": raise RuntimeError("Expected commit")
            header, message = raw.split(b"\n\n", 1)
            lines = []; skip = False
            for line in header.splitlines():
                if line.startswith(b" ") and skip: continue
                skip = line.startswith((b"gpgsig ", b"gpgsig-sha256 ", b"mergetag "))
                if skip: continue  # Rewriting invalidates embedded signatures.
                if line.startswith(b"tree "):
                    line = b"tree " + objects.tree(line[5:].decode()).encode()
                elif line.startswith(b"parent "):
                    old = line[7:].decode(); line = b"parent " + mapping.get(old, old).encode()
                else:
                    line = objects.replace(line)
                lines.append(line)
            changed = b"\n".join(lines) + b"\n\n" + objects.replace(message)
            mapping[oid] = objects.write("commit", changed) if changed != raw else oid
        # Validate each rewritten commit's message, identities and full tree, using
        # git grep as a second independent content check (including binary files).
        patterns = output / ("patterns-" + uuid.uuid4().hex + ".tmp")
        patterns.write_bytes(b"\n".join(old for old, _ in replacements) + b"\n")
        try:
            scanned = set()
            for oid in mapping.values():
                _, raw = objects.read(oid)
                if any(old in raw for old, _ in replacements): raise RuntimeError("Commit metadata verification failed")
                tree = raw.splitlines()[0].split()[1].decode()
                if tree in scanned: continue
                scanned.add(tree)
                result = git(clone, "grep", "-a", "-F", "-q", "-f", str(patterns), tree, "--", check=False)
                if result.returncode != 1: raise RuntimeError("Rewritten content verification failed")
            # Verify every reachable tree/blob too, including filenames and symlinks.
            reachable = git(clone, "rev-list", "--objects", *mapping.values(), "--not", baseline).stdout.splitlines() if mapping else []
            for record in reachable:
                oid = record.split(b" ", 1)[0].decode()
                _, raw = objects.read(oid)
                if any(old in raw for old, _ in replacements): raise RuntimeError("Reachable-object verification failed")
        finally:
            patterns.unlink(missing_ok=True)
        for ref, old in tips.items():
            git(clone, "update-ref", "refs/heads/scrub/" + ref, mapping.get(old, old))
        # Published history is an immutable boundary, and source tips cannot drift.
        if git(source, "rev-parse", "refs/remotes/origin/main").stdout.strip().decode() != baseline:
            raise RuntimeError("origin/main changed during scrub")
        for ref, old in tips.items():
            if git(source, "rev-parse", ref).stdout.strip().decode() != old:
                raise RuntimeError("Source ref changed during scrub; rerun")
        map_path = output / ("commit-map.txt" if publish else "dry-run-commit-map.txt")
        map_path.write_text("old new\n" + "".join(f"{old} {new}\n" for old, new in mapping.items()), encoding="ascii")
        summary = dict(clone=clone.name, commits=len(mapping), changed_commits=sum(a != b for a, b in mapping.items()), changed_blobs=objects.changed_blobs, baseline=baseline, tips={ref: mapping.get(oid, oid) for ref, oid in tips.items()}, verified=True, published=publish)
        if publish:
            git(source, "fetch", "--no-tags", str(clone), "refs/heads/scrub/main:refs/scrub/main")
        (output / ("result.json" if publish else "dry-run-result.json")).write_text(json.dumps(summary, indent=2) + "\n")
        print(json.dumps(summary, indent=2))
        return summary
    finally:
        objects.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--replacements", type=Path, required=True)
    parser.add_argument("--publish", action="store_true")
    parser.add_argument("--expected-main")
    parser.add_argument("refs", nargs="+")
    args = parser.parse_args()
    try:
        values = json.loads(args.replacements.read_text(encoding="utf-8-sig"))
        if not isinstance(values, dict) or not values or any(not k or not isinstance(v, str) or "\n" in k or "\0" in k for k, v in values.items()):
            raise ValueError("Invalid replacement map")
        replacements = [(k.encode(), v.encode()) for k, v in values.items()]
        if any(old in new for old, _ in replacements for _, new in replacements):
            raise ValueError("Replacement output contains a forbidden input")
        rewrite(args.source, args.source / "build/scrub", args.refs, replacements, args.publish, args.expected_main)
        return 0
    except (ValueError, OSError, RuntimeError, subprocess.SubprocessError):
        print("Scrub failed; source refs were not rewritten. Inspect the throwaway clone locally.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
