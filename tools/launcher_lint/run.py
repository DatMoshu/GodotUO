#!/usr/bin/env python3
"""Check every launcher under launchers/ is the .bat/.sh pair DirectorDeck expects.

    python tools/launcher_lint/run.py            check; exit 1 on any problem
    python tools/launcher_lint/run.py --list     the pairs with their descriptions
    python tools/launcher_lint/run.py --json     the same, as JSON (for the deck)

The rule (the shared agent area's rules/launchers_exposed.md):

- every launchers/<group>/<name>.bat has a <name>.sh twin, and the reverse;
- the .bat is CRLF, starts `@echo off`, and its first non-blank line after
  that is `rem <one sentence>.`, optionally followed by `rem args: ...`;
- the .sh is LF, starts `#!/usr/bin/env bash`, its second line is the same
  sentence as `# <sentence>.`, then the same optional `# args: ...`; it runs
  under `set -euo pipefail`, sources _shared/common.sh (or, Windows-only,
  says why and exits 2), and is executable in git (mode 100755);
- neither reads stdin (pause, set /p, choice, read);
- the .bat calls _shared\\common.bat (or config.bat, before the engine exists);
- config.sh defines exactly the keys config.bat does.

launchers/_shared is never a launcher and is not listed.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

_REM_RE = re.compile(r"^rem\s+(?P<text>.*)$", re.IGNORECASE)
_BAT_KEY_RE = re.compile(r'^\s*if\s+not\s+defined\s+\w+\s+set\s+"(?P<key>[A-Za-z_]\w*)=', re.IGNORECASE)
_SH_KEY_RE = re.compile(r'^\s*:\s+"\$\{(?P<key>[A-Za-z_]\w*):=')
_BAT_STDIN_RE = re.compile(r"^\s*(?:@?pause\b|set\s+/p\b|choice\b)|[&|(]\s*(?:pause|choice)\b|\bset\s+/p\b", re.IGNORECASE)
_SH_STDIN_RE = re.compile(r"^\s*read\b|[;&|]\s*read\b")


@dataclass
class Launcher:
    group: str
    name: str
    description: str = ""
    args: str = ""
    windows_only: bool = False
    problems: list[str] = field(default_factory=list)

    @property
    def key(self) -> str:
        return f"{self.group}/{self.name}"


def _lines(raw: bytes) -> list[str]:
    return raw.decode("utf-8", errors="replace").replace("\r\n", "\n").split("\n")


def _sentence_problem(text: str) -> str | None:
    if not text or text.startswith("="):
        return "its first line after @echo off must be 'rem <what it does and what a person sees>.'"
    if not text.endswith("."):
        return "the description must be one sentence ending in a full stop"
    if len(text) > 240:
        return "the description is longer than one sentence (over 240 characters)"
    return None


def read_bat(path: Path, launcher: Launcher) -> None:
    raw = path.read_bytes()
    if re.search(rb"(?<!\r)\n", raw):
        launcher.problems.append(f"{path.name}: has LF-only line endings; .bat files must be CRLF")
    lines = _lines(raw)
    if not lines or lines[0].strip().lower() != "@echo off":
        launcher.problems.append(f"{path.name}: first line must be @echo off")
        return
    rest = [line.strip() for line in lines[1:] if line.strip()]
    match = _REM_RE.match(rest[0]) if rest else None
    text = match.group("text").strip() if match else ""
    problem = _sentence_problem(text) if match else (
        "its first line after @echo off must be 'rem <what it does and what a person sees>.'")
    if problem:
        launcher.problems.append(f"{path.name}: {problem}")
    else:
        launcher.description = text
    if len(rest) > 1:
        second = _REM_RE.match(rest[1])
        if second and second.group("text").lower().startswith("args:"):
            launcher.args = second.group("text")[5:].strip()
    body = [line for line in lines if not _REM_RE.match(line.strip()) and not line.strip().lower().startswith("::")]
    joined = "\n".join(body)
    if "_shared\\common.bat" not in joined and "_shared\\config.bat" not in joined:
        launcher.problems.append(f"{path.name}: never calls _shared\\common.bat")
    for number, line in enumerate(body, 1):
        if _BAT_STDIN_RE.search(line):
            launcher.problems.append(f"{path.name}: reads stdin ({line.strip()}); settings come from config, not prompts")


def read_sh(path: Path, launcher: Launcher, mode: str | None) -> None:
    raw = path.read_bytes()
    if b"\r" in raw:
        launcher.problems.append(f"{path.name}: has CR line endings; .sh files must be LF")
    lines = _lines(raw)
    if not lines or lines[0].strip() != "#!/usr/bin/env bash":
        launcher.problems.append(f"{path.name}: first line must be #!/usr/bin/env bash")
    description = lines[1].strip() if len(lines) > 1 else ""
    if not description.startswith("# "):
        launcher.problems.append(f"{path.name}: second line must be '# <the .bat's description>'")
    elif launcher.description and description[2:].strip() != launcher.description:
        launcher.problems.append(f"{path.name}: description differs from the .bat's rem line")
    elif not launcher.description:
        problem = _sentence_problem(description[2:].strip())
        if problem:
            launcher.problems.append(f"{path.name}: {problem}")
        else:
            launcher.description = description[2:].strip()
    args = ""
    if len(lines) > 2 and lines[2].strip().lower().startswith("# args:"):
        args = lines[2].strip()[7:].strip()
    if args != launcher.args:
        launcher.problems.append(f"{path.name}: '# args:' line differs from the .bat's 'rem args:' line")
    code = [line for line in lines if not line.lstrip().startswith("#")]
    joined = "\n".join(code)
    if not re.search(r"^\s*set -euo pipefail\b", joined, re.MULTILINE):
        launcher.problems.append(f"{path.name}: missing 'set -euo pipefail'")
    sources_common = "_shared/common.sh" in joined
    launcher.windows_only = not sources_common and re.search(r"^\s*exit 2\b", joined, re.MULTILINE) is not None
    if not sources_common and not launcher.windows_only:
        launcher.problems.append(f"{path.name}: never sources _shared/common.sh (a Windows-only launcher says why and exits 2)")
    for line in code:
        if _SH_STDIN_RE.search(line):
            launcher.problems.append(f"{path.name}: reads stdin ({line.strip()})")
    if mode is None:
        launcher.problems.append(f"{path.name}: not in git; add it and run git update-index --chmod=+x")
    elif mode != "100755":
        launcher.problems.append(f"{path.name}: not executable in git (mode {mode}); run git update-index --chmod=+x")


def git_modes(root: Path) -> dict[str, str] | None:
    try:
        out = subprocess.run(["git", "ls-files", "-s", "--", "launchers"], cwd=root,
                             capture_output=True, text=True, check=True).stdout
    except (OSError, subprocess.CalledProcessError):
        return None
    modes = {}
    for line in out.splitlines():
        meta, _, path = line.partition("\t")
        modes[path] = meta.split()[0]
    return modes


def config_problems(root: Path) -> list[str]:
    shared = root / "launchers" / "_shared"
    problems = []
    bat, sh = shared / "config.bat", shared / "config.sh"
    if not bat.is_file():
        return []  # no launchers here at all
    if not sh.is_file():
        return ["launchers/_shared/config.sh is missing (the twin of config.bat)"]
    if not (shared / "config.local.sh.example").is_file():
        problems.append("launchers/_shared/config.local.sh.example is missing")
    if not (shared / "common.sh").is_file():
        problems.append("launchers/_shared/common.sh is missing")
    bat_keys = {m["key"].upper() for line in _lines(bat.read_bytes()) if (m := _BAT_KEY_RE.match(line))}
    sh_keys = {m["key"].upper() for line in _lines(sh.read_bytes()) if (m := _SH_KEY_RE.match(line))} - {"UO_ROOT"}
    for key in sorted(bat_keys - sh_keys):
        problems.append(f"launchers/_shared/config.sh lacks {key} (config.bat has it)")
    for key in sorted(sh_keys - bat_keys):
        problems.append(f"launchers/_shared/config.sh has {key}, which config.bat lacks")
    return problems


def collect(root: Path) -> tuple[list[Launcher], list[str]]:
    base = root / "launchers"
    modes = git_modes(root)
    general = config_problems(root)
    if modes is None:
        general.append("not a git checkout: the executable bit of the .sh files was not checked")
    launchers = []
    names = sorted({(p.parent.name, p.stem) for p in base.glob("*/*") if p.suffix in (".bat", ".sh")
                    and p.parent.name != "_shared"})
    for group, name in names:
        launcher = Launcher(group, name)
        bat, sh = base / group / f"{name}.bat", base / group / f"{name}.sh"
        if bat.is_file():
            read_bat(bat, launcher)
        else:
            launcher.problems.append(f"{name}.bat is missing (every .sh has a .bat twin)")
        if sh.is_file():
            mode = modes.get(f"launchers/{group}/{name}.sh", None) if modes is not None else "100755"
            read_sh(sh, launcher, mode)
        else:
            launcher.problems.append(f"{name}.sh is missing (every .bat has a .sh twin)")
        launchers.append(launcher)
    return launchers, general


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--list", action="store_true", help="print every pair with its description")
    parser.add_argument("--json", action="store_true", help="print every pair as JSON")
    args = parser.parse_args(argv)
    launchers, general = collect(args.root.resolve())
    if args.json:
        print(json.dumps([{"launcher": l.key, "description": l.description, "args": l.args,
                           "windows_only": l.windows_only, "problems": l.problems} for l in launchers], indent=2))
    elif args.list:
        for l in launchers:
            tags = " [Windows only]" if l.windows_only else ""
            extra = f"  (args: {l.args})" if l.args else ""
            print(f"{l.key}{tags}: {l.description or '(no description)'}{extra}")
    failures = [f"launchers/{l.key}: {p}" for l in launchers for p in l.problems]
    failures += [p for p in general if not p.startswith("not a git checkout")]
    for note in general:
        if note.startswith("not a git checkout"):
            print(f"[launcher_lint] note: {note}", file=sys.stderr)
    for failure in failures:
        print(f"[launcher_lint] {failure}", file=sys.stderr)
    if failures:
        print(f"[launcher_lint] FAILED: {len(failures)} problem(s) in {len(launchers)} launchers", file=sys.stderr)
        return 1
    if not (args.list or args.json):
        print(f"[launcher_lint] OK: {len(launchers)} launcher pairs")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
