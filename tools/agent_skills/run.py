"""Link .agents/skills to .claude/skills so Codex finds the project's skills.

Claude Code, GitHub Copilot and Cursor read .claude/skills/ natively; Codex reads
only .agents/skills/. The link is local and gitignored, so the skills have one
copy in git. On Windows it is a directory junction (no admin rights needed),
elsewhere a relative symlink.

    python tools/agent_skills/run.py           # create or repair the link
    python tools/agent_skills/run.py --check   # validate the skills and the link, change nothing
    python tools/agent_skills/run.py --remove  # remove the link only
"""
import argparse
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / ".claude" / "skills"
LINK = ROOT / ".agents" / "skills"
NAME = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")


def is_link(path):
    return path.is_symlink() or (os.name == "nt" and path.is_dir() and path.is_junction())


def points_at_source(path):
    try:
        return is_link(path) and path.resolve() == SOURCE.resolve()
    except OSError:
        return False


def validate():
    """Every skill folder has a SKILL.md whose front matter names it and describes it (Agent Skills format)."""
    problems = []
    for folder in sorted(p for p in SOURCE.iterdir() if p.is_dir()):
        skill = folder / "SKILL.md"
        if not skill.is_file():
            problems.append(f"{folder.name}: no SKILL.md")
            continue
        match = re.match(r"---\r?\n(.*?)\r?\n---", skill.read_text(encoding="utf-8"), re.S)
        if not match:
            problems.append(f"{folder.name}: no front matter")
            continue
        name = re.search(r"^name:\s*['\"]?([^'\"\r\n]+)", match.group(1), re.M)
        if not name or name.group(1).strip() != folder.name:
            problems.append(f"{folder.name}: front matter name does not match the folder")
        elif not NAME.match(folder.name) or len(folder.name) > 64:
            problems.append(f"{folder.name}: name must be lowercase letters, digits and single hyphens, at most 64")
        if not re.search(r"^description:\s*\S", match.group(1), re.M):
            problems.append(f"{folder.name}: no description")
    return problems


def make_link():
    LINK.parent.mkdir(exist_ok=True)
    if os.name == "nt":
        subprocess.run(["cmd", "/c", "mklink", "/J", str(LINK), str(SOURCE)], check=True, capture_output=True)
    else:
        LINK.symlink_to(os.path.relpath(SOURCE, LINK.parent), target_is_directory=True)


def remove_link():
    if os.name == "nt" and LINK.is_junction():
        os.rmdir(LINK)  # removes the junction only, never its target
    else:
        LINK.unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true", help="validate the skills and report the link; change nothing")
    parser.add_argument("--remove", action="store_true", help="remove the link only")
    args = parser.parse_args()

    problems = validate()
    for problem in problems:
        print(f"[agent_skills] {problem}")
    count = sum(1 for p in SOURCE.iterdir() if p.is_dir())

    if args.remove:
        if is_link(LINK):
            remove_link()
            print("[agent_skills] link removed")
        return 1 if problems else 0
    if args.check:
        state = "linked" if points_at_source(LINK) else "not linked (Codex will not see the skills; run without --check)"
        print(f"[agent_skills] {count} skills, {len(problems)} problems; .agents/skills {state}")
        return 1 if problems else 0

    if points_at_source(LINK):
        print(f"[agent_skills] .agents/skills already links to .claude/skills ({count} skills)")
    elif LINK.exists() or is_link(LINK):
        if not is_link(LINK):
            print("[agent_skills] .agents/skills is a real folder; move its skills into .claude/skills, then rerun")
            return 1
        remove_link()
        make_link()
        print(f"[agent_skills] repaired .agents/skills -> .claude/skills ({count} skills)")
    else:
        make_link()
        print(f"[agent_skills] linked .agents/skills -> .claude/skills ({count} skills)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
