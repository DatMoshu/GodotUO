"""Pre-push docs/privacy checks on isolated committed snapshots, not dirty files."""
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


def clean_environment():
    # Hooks inherit repository-local Git variables. Never let those redirect a
    # temporary checkout operation back into the user's real index/worktree.
    return {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}


def git(root, *args):
    return subprocess.run(["git", "-C", str(root), *args], env=clean_environment(), check=True, capture_output=True, text=True).stdout.strip()


# Same pattern as the CI guard "No personal paths outside docs". Matched against every
# commit that the push would publish, binaries included: an intermediate commit on
# GitHub is as public as the tip.
MACHINE_PATH = r"[A-Za-z]:[\\/](Users|_uo|Games)[\\/]"
PROGRAM_SUFFIXES = (".dll", ".exe", ".so", ".dylib", ".pdb", ".lib", ".node", ".pyd")


def pushed_commits(root, local, remote):
    """Commits the push publishes: remote..local, or everything not already on a remote."""
    if set(remote) == {"0"}:
        out = git(root, "rev-list", local, "--not", "--remotes")
    else:
        out = git(root, "rev-list", f"{remote}..{local}")
    return out.split()


def check_commit(root, commit, deny):
    """Problems in one committed tree, read from the object store (no checkout)."""
    problems = []
    grep = subprocess.run(["git", "-C", str(root), "grep", "-c", "-E", MACHINE_PATH, commit, "--", ".", ":!docs"],
                          env=clean_environment(), capture_output=True, text=True)
    if grep.returncode == 0:
        problems.append("machine path: " + grep.stdout.strip().splitlines()[0].split(":", 1)[1])
    for value in deny:
        hit = subprocess.run(["git", "-C", str(root), "grep", "-c", "-i", "-F", "-e", value, commit, "--", ".", ":!tools/privacy_scan"],
                             env=clean_environment(), capture_output=True, text=True)
        if hit.returncode == 0:
            problems.append("local deny list: " + hit.stdout.strip().splitlines()[0].split(":", 1)[1])
            break
    added = git(root, "diff-tree", "--no-commit-id", "--name-only", "-r", "--diff-filter=AM", "--root", commit)
    for name in added.splitlines():
        if name.lower().endswith(PROGRAM_SUFFIXES):
            problems.append("compiled program committed: " + name)
    return problems


def check(root, updates):
    root = Path(root).resolve()
    tips = set()
    ranges = []
    for line in updates.splitlines():
        fields = line.split()
        if len(fields) != 4 or not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", fields[1]):
            raise ValueError("Malformed pre-push ref update")
        if set(fields[1]) != {"0"}:  # A deletion has no new content to check.
            tips.add(git(root, "rev-parse", "--verify", fields[1] + "^{commit}"))
            ranges.append((fields[1], fields[3]))
    if not tips:
        return 0
    deny_file = root / "tools/privacy_scan/deny.local.txt"
    deny = [l.strip() for l in deny_file.read_text(encoding="utf-8").splitlines()
            if l.strip() and not l.startswith("#")] if deny_file.is_file() else []
    seen = set()
    for local, remote in ranges:
        for commit in pushed_commits(root, local, remote):
            if commit in seen:
                continue
            seen.add(commit)
            problems = check_commit(root, commit, deny)
            if problems:
                print(f"[pre-push] blocked: commit {commit[:12]} would publish private material:")
                for p in problems:
                    print("  " + p)
                print("[pre-push] rewrite that commit (rebase or amend) before pushing; the tip being clean is not enough.")
                return 1
    print(f"[pre-push] {len(seen)} new commit(s) scanned for machine paths and compiled programs")
    if not tips:
        return 0
    scratch = root / "build" / "git_hooks"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="check-", dir=scratch) as temporary:
        temporary = Path(temporary).resolve()
        if not temporary.is_relative_to(scratch.resolve()):
            raise ValueError("Temporary checkout escaped its workspace")
        checkout = temporary / "checkout"
        empty_hooks = temporary / "empty-hooks"
        empty_hooks.mkdir()
        # Shared objects are read-only here; all checkout/index changes are private.
        git(root, "-c", f"core.hooksPath={empty_hooks}", "clone", "--quiet", "--shared", "--no-checkout", str(root), str(checkout))
        environment = clean_environment()
        for tip in sorted(tips):
            git(checkout, "-c", f"core.hooksPath={empty_hooks}", "checkout", "--quiet", "--detach", "--force", tip)
            private_deny = root / "tools/privacy_scan/deny.local.txt"
            if private_deny.is_file():
                destination = checkout / "tools/privacy_scan/deny.local.txt"
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(private_deny, destination)
            print(f"[pre-push] checking committed tip {tip[:12]}", flush=True)
            for script in ("tools/docs_lint/run.py", "tools/privacy_scan/run.py"):
                if not (checkout / script).is_file():
                    raise ValueError(f"Pushed commit lacks required checker: {script}")
                result = subprocess.run([sys.executable, str(checkout / script)], cwd=checkout, env=environment)
                if result.returncode:
                    print(f"[pre-push] blocked: {script} failed for {tip[:12]}")
                    return 1
    print("[pre-push] docs and privacy checks passed")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(check(git(Path.cwd(), "rev-parse", "--show-toplevel"), sys.stdin.read()))
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        print("[pre-push] failed:", exc, file=sys.stderr)
        raise SystemExit(1)
