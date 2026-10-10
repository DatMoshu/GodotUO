"""Load and check a shard host profile (docs/data_formats.md section 35).

The checks are the JSON Schema in schema/profile.schema.json, run by the small
validator below (the subset the schema uses, so the tool needs only the standard
library), plus what a schema cannot say: the files a profile names exist, stay
inside the repo, and nothing in it is a credential or one person's machine path.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCHEMA = HERE / "schema" / "profile.schema.json"
MAX_BYTES = 64 * 1024

PLACEHOLDERS = {"data_dir", "dist", "src"}
_MACHINE = re.compile(r"(?i)(?:\b[a-z]:[\\/]|\\\\|/home/|/users/|/root/)")
_SECRETISH = re.compile(r"(?i)(password|secret|token|apikey|api_key)\s*[:=]")


class ProfileError(Exception):
    def __init__(self, problems: list[str]):
        super().__init__("; ".join(problems))
        self.problems = problems


def _type_ok(value, name: str) -> bool:
    if name == "object":
        return isinstance(value, dict)
    if name == "array":
        return isinstance(value, list)
    if name == "string":
        return isinstance(value, str)
    if name == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    return True


def check(value, schema: dict, root: dict, where: str, out: list[str]) -> None:
    """The subset of JSON Schema profile.schema.json uses."""
    if "$ref" in schema:
        ref = root
        for part in schema["$ref"][2:].split("/"):
            ref = ref[part]
        return check(value, ref, root, where, out)
    if "oneOf" in schema:
        tries = []
        for sub in schema["oneOf"]:
            errs: list[str] = []
            check(value, sub, root, where, errs)
            tries.append(errs)
        if sum(1 for e in tries if not e) != 1:
            # Report the branch whose "kind" matched, else say none did.
            kind = value.get("kind") if isinstance(value, dict) else None
            for sub, errs in zip(schema["oneOf"], tries):
                if kind is not None and sub.get("properties", {}).get("kind", {}).get("const") == kind:
                    out.extend(errs)
                    return
            out.append(f"{where}: matches none of the allowed shapes")
        return
    if "const" in schema and (value != schema["const"] or isinstance(value, bool)):
        out.append(f"{where}: must be {schema['const']!r}")
        return
    if "enum" in schema and value not in schema["enum"]:
        out.append(f"{where}: must be one of {schema['enum']}")
        return
    if "type" in schema and not _type_ok(value, schema["type"]):
        out.append(f"{where}: must be {schema['type']}")
        return
    if isinstance(value, str):
        if len(value) < schema.get("minLength", 0) or len(value) > schema.get("maxLength", 1 << 30):
            out.append(f"{where}: length out of range")
        if "pattern" in schema and not re.search(schema["pattern"], value):
            out.append(f"{where}: {value!r} does not match {schema['pattern']}")
    if isinstance(value, int) and not isinstance(value, bool):
        if value < schema.get("minimum", value) or value > schema.get("maximum", value):
            out.append(f"{where}: {value} out of range")
    if isinstance(value, list):
        if len(value) < schema.get("minItems", 0):
            out.append(f"{where}: needs at least {schema['minItems']} item(s)")
        for i, item in enumerate(value):
            if "items" in schema:
                check(item, schema["items"], root, f"{where}[{i}]", out)
    if isinstance(value, dict):
        props = schema.get("properties", {})
        for key in schema.get("required", []):
            if key not in value:
                out.append(f"{where or 'profile'}: missing required field {key!r}")
        for key, item in value.items():
            if key in props:
                check(item, props[key], root, f"{where}.{key}" if where else key, out)
            elif schema.get("additionalProperties") is False:
                out.append(f"{where or 'profile'}: unknown field {key!r}")


def _strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, list):
        for v in value:
            yield from _strings(v)
    elif isinstance(value, dict):
        for v in value.values():
            yield from _strings(v)


def repo_root(profile_file: Path) -> Path:
    """The git work tree holding the profile; relative paths in it resolve here."""
    for d in [profile_file.parent, *profile_file.parent.parents]:
        if (d / ".git").exists():
            return d
    return profile_file.parent


@dataclass
class Profile:
    data: dict
    file: Path
    root: Path

    def get(self, *keys, default=None):
        cur = self.data
        for k in keys:
            if not isinstance(cur, dict) or k not in cur:
                return default
            cur = cur[k]
        return cur

    @property
    def id(self) -> str:
        return self.data["id"]

    def path(self, rel: str) -> Path:
        return self.root / rel


def load(path: str | Path, root: Path | None = None) -> Profile:
    """`root` overrides the repo root, for a profile kept outside the repo (tests)."""
    file = Path(path).resolve()
    if not file.is_file():
        raise ProfileError([f"{path}: no such file"])
    raw = file.read_bytes()
    if len(raw) > MAX_BYTES:
        raise ProfileError([f"{file.name}: larger than {MAX_BYTES // 1024} KiB"])
    try:
        data = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as e:
        raise ProfileError([f"{file.name}: not UTF-8 JSON ({e})"])
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    problems: list[str] = []
    check(data, schema, schema, "", problems)
    if problems:
        raise ProfileError(problems)

    prof = Profile(data, file, root or repo_root(file))
    problems = semantic(prof)
    if problems:
        raise ProfileError(problems)
    return prof


_SCRIPT_EXT = (".py", ".sh", ".pl", ".rb", ".js", ".ps1")
_ABSOLUTE_PREFIXES = ("{src}/", "{dist}/", "{data_dir}", "/")


def _repo_relative_script(arg: str) -> bool:
    """An exec_start_pre argument that names a file relative to nowhere: it has a slash or a script suffix, and
    does not start with a placeholder or `/`. Options (`--x`), URLs, commands and plain words pass."""
    if not arg or arg.startswith(("-", "$")) or arg.startswith(_ABSOLUTE_PREFIXES):
        return False
    if any(c.isspace() for c in arg) or "://" in arg:
        return False
    return "/" in arg or arg.lower().endswith(_SCRIPT_EXT)


OWNER_PATCH = "0001-headless-owner-account"


def warnings(p: Profile) -> list[str]:
    """Things a profile may do but probably does not mean to; `validate` prints them and still exits 0."""
    out: list[str] = []
    if not any(OWNER_PATCH in Path(rel).name for rel in p.data.get("server", {}).get("patches", [])):
        out.append(f"server.patches has no {OWNER_PATCH}: a headless shard makes no owner account, "
                   "so `plan admin` has nobody to administer with")
    return out


def semantic(p: Profile) -> list[str]:
    out: list[str] = []
    d = p.data
    for s in _strings(d):
        if _MACHINE.search(s):
            out.append(f"{s!r}: a machine path does not belong in a profile")
        if _SECRETISH.search(s):
            out.append(f"{s!r}: looks like a credential; those live in /etc/muo/{d['id']}.env")

    rels: list[tuple[str, str, bool]] = []  # (where, relpath, must be a file)
    for i, rel in enumerate(d.get("server", {}).get("patches", [])):
        rels.append((f"server.patches[{i}]", rel, True))
    for i, a in enumerate(d.get("server", {}).get("assemblies", [])):
        rels.append((f"server.assemblies[{i}].project", a["project"], True))
    if "config_overlay" in d:
        rels.append(("config_overlay", d["config_overlay"], False))
    for i, rel in enumerate(d.get("content", [])):
        rels.append((f"content[{i}]", rel, False))
    if "data_manifest" in d:
        rels.append(("data_manifest.file", d["data_manifest"]["file"], True))
    src = d.get("server", {}).get("source", {})
    if src.get("kind") == "submodule":
        rels.append(("server.source.path", src["path"], False))
    for where, rel, is_file in rels:
        if ".." in Path(rel).parts:
            out.append(f"{where}: {rel!r} leaves the repo")
            continue
        target = p.path(rel)
        if not (target.is_file() if is_file else target.is_dir()):
            out.append(f"{where}: {rel!r} not found under the repo root")

    for i, rel in enumerate(d.get("backup", {}).get("include", [])):
        if ".." in Path(rel).parts:
            out.append(f"backup.include[{i}]: {rel!r} leaves the server folder")

    for i, arg in enumerate(d.get("service", {}).get("exec_start_pre", [])):
        for name in re.findall(r"\{([A-Za-z_]+)\}", arg):
            if name not in PLACEHOLDERS:
                allowed = ", ".join("{" + n + "}" for n in sorted(PLACEHOLDERS))
                out.append(f"service.exec_start_pre[{i}]: unknown placeholder {{{name}}} (allowed: {allowed})")
        if _repo_relative_script(arg):
            out.append(f"service.exec_start_pre[{i}]: {arg!r} looks like a repo-relative script path, but the unit "
                       f"runs in {{dist}}, not the checkout; write it as '{{src}}/{arg.lstrip('./')}'")
    build = d.get("server", {}).get("build")
    if build:
        for name in re.findall(r"\{([A-Za-z_]+)\}", build["output"]):
            if name != "pin":
                out.append(f"server.build.output: unknown placeholder {{{name}}}")
    return out
