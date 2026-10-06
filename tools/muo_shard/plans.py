"""Generate the shell scripts a person reviews and runs on the shard host.

Nothing here runs anything. Every function returns the text of one bash script
for one verb; run.py prints it. The scripts are idempotent (a second run changes
nothing it already set), hold no secret (those live in /etc/muo/<id>.env on the
host) and no path of the machine that generated them. Everything a script needs
from the repo (patches, configuration, a data manifest) is embedded in it, so
`ssh host 'sudo bash -s' < step.sh` is all the host needs.
"""

from __future__ import annotations

import json
import re
import shlex
import subprocess
from pathlib import Path

from shardprofile import Profile, ProfileError

TOOLS = Path(__file__).resolve().parents[1]
MODERNUO_TEMPLATE = TOOLS / "modernuo" / "config" / "modernuo.template.json"

BASE_PACKAGES = ["git", "zstd", "libdeflate-dev", "libargon2-1", "ca-certificates", "python3", "iproute2"]
DEFAULT_BACKUP_ROOT = "/var/backups/muo"
DEFAULT_KEYS = {"client_data": "MUO_CLIENT_DATA", "admin_user": "MUO_ADMIN_USER", "admin_password": "MUO_ADMIN_PASSWORD"}


# --------------------------------------------------------------- context


class Ctx:
    """The names and paths one profile resolves to, the same in every script."""

    def __init__(self, p: Profile):
        self.p = p
        d = p.data
        self.id = d["id"]
        self.user = p.get("service", "user", default=f"muo-{self.id}")
        self.base = f"/srv/muo/{self.id}"
        self.src = f"{self.base}/src"
        self.dist = f"{self.base}/dist"
        self.env_file = f"/etc/muo/{self.id}.env"
        self.unit = f"muo-{self.id}.service"
        self.port = d["listen"]["port"]
        self.address = p.get("listen", "address", default="0.0.0.0")
        self.dotnet = p.get("server", "dotnet", default="10.0")
        self.backup_root = p.get("backup", "root", default=f"{DEFAULT_BACKUP_ROOT}/{self.id}")
        self.keys = {**DEFAULT_KEYS, **d.get("host_keys", {})}

    def header(self, verb: str, root: bool = True) -> list[str]:
        out = [
            "#!/usr/bin/env bash",
            f"# muo_shard {verb} for profile {self.id}. Generated: review it, then run it on the host as root.",
            "# It is idempotent and holds no secret; those live in " + self.env_file + ".",
            "set -euo pipefail",
            'die() { echo "muo_shard: $*" >&2; exit 1; }',
        ]
        if root:
            out.append('[ "$(id -u)" = 0 ] || die "run this as root"')
        out += [
            f"ID={self.id}",
            f"SVC_USER={shlex.quote(self.user)}",
            f"BASE={self.base}",
            "SRC=$BASE/src",
            "DIST=$BASE/dist",
            f"ENV_FILE={self.env_file}",
            f"UNIT={self.unit}",
            f"PORT={self.port}",
            "",
        ]
        return out


def _finish(lines: list[str]) -> str:
    return "\n".join(lines).rstrip("\n") + "\n"


def _embed(dest: str, text: str, mode: str = "0644", n: list[int] | None = None) -> list[str]:
    """Write `text` to `dest` on the host through a quoted heredoc (no expansion inside)."""
    text = text.replace("\r\n", "\n")
    if not text.endswith("\n"):
        text += "\n"
    tag = "MUO_EOF"
    i = 0
    while re.search(rf"^{tag}_{i}$", text, re.M):
        i += 1
    tag = f"{tag}_{i}"
    return [
        f'install -d "$(dirname {dest})"',
        f"cat > {dest} <<'{tag}'",
        text.rstrip("\n"),
        tag,
        f"chmod {mode} {dest}",
    ]


# --------------------------------------------------------------- bootstrap


def bootstrap(p: Profile) -> str:
    c = Ctx(p)
    pkgs = [f"dotnet-sdk-{c.dotnet}"] + BASE_PACKAGES
    for extra in p.get("host_packages", default=[]):
        if extra not in pkgs:
            pkgs.append(extra)
    out = c.header("bootstrap")
    out += [
        "# packages: only what is missing, so a second run touches nothing",
        f"want={shlex.quote(' '.join(pkgs))}",
        "missing=",
        'for pkg in $want; do dpkg -s "$pkg" >/dev/null 2>&1 || missing="$missing $pkg"; done',
        "if [ -n \"$missing\" ]; then",
        "    export DEBIAN_FRONTEND=noninteractive",
        "    apt-get update -qq",
        "    apt-get install -y --no-install-recommends $missing",
        "fi",
        "",
        "# service user: a system account with no login shell",
        'id -u "$SVC_USER" >/dev/null 2>&1 || useradd --system --user-group --home-dir "$BASE" --shell /usr/sbin/nologin "$SVC_USER"',
        "",
        "# folders",
        'install -d -o "$SVC_USER" -g "$SVC_USER" -m 0750 "$BASE" "$SRC" "$DIST" "$BASE/uodata"',
        f'install -d -o "$SVC_USER" -g "$SVC_USER" -m 0750 {shlex.quote(c.backup_root)}',
        "install -d -m 0755 /etc/muo",
        "",
        "# the host-local values file: created empty and closed, never overwritten",
        '[ -e "$ENV_FILE" ] || install -m 0600 -o root -g root /dev/null "$ENV_FILE"',
        "",
        f"echo \"muo_shard: bootstrap of $ID done. Put {c.keys['client_data']}, {c.keys['admin_user']} and "
        f"{c.keys['admin_password']} in $ENV_FILE (run.py secrets), then deploy.\"",
    ]
    return _finish(out)


# --------------------------------------------------------------- deploy


def _systemd_arg(arg: str, c: Ctx) -> str:
    """One argument of an Exec line: literal text escaped, placeholders expanded."""
    values = {"dist": c.dist, "src": c.src}
    parts = re.split(r"(\{[A-Za-z_]+\})", arg)
    out = []
    for part in parts:
        m = re.fullmatch(r"\{([A-Za-z_]+)\}", part)
        if m and m.group(1) == "data_dir":
            out.append("${" + c.keys["client_data"] + "}")
        elif m and m.group(1) in values:
            out.append(values[m.group(1)].replace("%", "%%"))
        else:
            out.append(part.replace("\\", "\\\\").replace('"', '\\"').replace("$", "$$").replace("%", "%%"))
    return '"' + "".join(out) + '"'


def exec_line(argv: list[str], c: Ctx) -> str:
    return " ".join(_systemd_arg(a, c) for a in argv)


def unit_text(p: Profile) -> str:
    c = Ctx(p)
    pre = []
    if p.get("data_manifest"):
        pre.append(f'ExecStartPre=/usr/bin/python3 "{c.dist}/muo-verify-data.py"')
    custom = p.get("service", "exec_start_pre")
    if custom:
        pre.append("ExecStartPre=" + exec_line(custom, c))
    lines = [
        "[Unit]",
        f"Description=ModernUO shard {p.data['name']} ({c.unit})",
        "After=network-online.target",
        "Wants=network-online.target",
        "",
        "[Service]",
        "Type=simple",
        f"User={c.user}",
        f"Group={c.user}",
        f"WorkingDirectory={c.dist}",
        f"EnvironmentFile={c.env_file}",
        *pre,
        f"ExecStart={c.dist}/muo-run.sh",
        f"Restart={p.get('service', 'restart', default='on-failure')}",
        "RestartSec=5",
        "NoNewPrivileges=true",
        "PrivateTmp=true",
    ]
    mem = p.get("service", "memory_max")
    if mem:
        lines.append(f"MemoryMax={mem}")
    lines += ["", "[Install]", "WantedBy=multi-user.target"]
    return "\n".join(lines) + "\n"


def render_template(p: Profile) -> str:
    """configure.py's template, filled for this profile; the client data path is filled on the host."""
    c = Ctx(p)
    text = MODERNUO_TEMPLATE.read_text(encoding="utf-8").replace("\r\n", "\n")
    text = text.replace("@UO_CLIENT_DATA@", "")
    text = text.replace("@UO_SHARD_NAME@", json.dumps(p.data["name"])[1:-1])
    text = text.replace("0.0.0.0:@UO_SHARD_PORT@", f"{c.address}:{c.port}")
    doc = json.loads(text)
    return json.dumps(doc, indent=2) + "\n"


def overlay_files(p: Profile) -> list[tuple[str, str]]:
    """(relative name, text) for each file of the overlay folder. *.template.json is the tool's own."""
    rel = p.get("config_overlay")
    if not rel:
        return []
    root = p.path(rel)
    files = []
    for f in sorted(x for x in root.rglob("*") if x.is_file()):
        if f.name.endswith(".template.json"):
            continue
        try:
            text = f.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            raise ProfileError([f"config_overlay: {f.name} is not UTF-8 text"])
        files.append((f.relative_to(root).as_posix(), text))
    return files


_VERIFY = '''\
#!/usr/bin/env python3
"""Refuse a client data folder that does not match the manifest (muo_shard data_manifest)."""
import hashlib, json, os, sys

base = os.environ.get("@KEY@")
if not base or not os.path.isdir(base):
    sys.exit("muo_shard: @KEY@ is not set to a folder")
here = os.path.dirname(os.path.abspath(__file__))
manifest = json.load(open(os.path.join(here, "muo-data-manifest.json"), encoding="utf-8"))
mode = "@MODE@"
bad = []
for name, want in sorted(manifest["files"].items()):
    path = os.path.join(base, name)
    if not os.path.isfile(path):
        bad.append(name + ": missing")
    elif mode == "size":
        if os.path.getsize(path) != want["size"]:
            bad.append(name + ": size " + str(os.path.getsize(path)) + ", expected " + str(want["size"]))
    else:
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        if h.hexdigest() != want["sha256"]:
            bad.append(name + ": sha256 differs")
if bad:
    sys.exit("muo_shard: client data does not match the manifest:\\n  " + "\\n  ".join(bad))
'''

_FILL = '''\
import os, sys
root = sys.argv[1]
for rel in sys.argv[2:]:
    path = os.path.join(root, rel)
    text = open(path, encoding="utf-8").read()
    out, rest = [], text
    while "{{" in rest:
        head, _, tail = rest.partition("{{")
        key, sep, rest = tail.partition("}}")
        if not sep or not key.isidentifier() or key not in os.environ:
            sys.exit("muo_shard: " + rel + " uses {{" + key + "}}, which is not set in the host env file")
        out += [head, os.environ[key]]
    out.append(rest)
    with open(path, "w", encoding="utf-8", newline="\\n") as f:
        f.write("".join(out))
'''

_FINISH_CONFIG = '''\
import json, sys
path, data, version = sys.argv[1:4]
doc = json.load(open(path, encoding="utf-8"))
doc["dataDirectories"] = [data]
if version:
    doc.setdefault("settings", {})["clientData.clientVersion"] = version
with open(path, "w", encoding="utf-8", newline="\\n") as f:
    json.dump(doc, f, indent=2)
    f.write("\\n")
'''

_ASSEMBLIES = '''\
import json, os, sys
path, name = sys.argv[1:3]
items = json.load(open(path, encoding="utf-8")) if os.path.exists(path) else []
if name not in items:
    items.append(name)
with open(path, "w", encoding="utf-8", newline="\\n") as f:
    json.dump(items, f, indent=2)
    f.write("\\n")
'''


def _manifest_text(p: Profile) -> str:
    m = p.data["data_manifest"]
    try:
        doc = json.loads(p.path(m["file"]).read_text(encoding="utf-8"))
        files = doc["files"]
        assert isinstance(files, dict) and files
        for name, want in files.items():
            assert not name.startswith("/") and ".." not in Path(name).parts
            if m["verify"] == "size":
                assert isinstance(want["size"], int)
            else:
                assert re.fullmatch(r"[0-9a-f]{64}", want["sha256"])
    except (OSError, ValueError, KeyError, TypeError, AssertionError):
        raise ProfileError([f"data_manifest.file: {m['file']} must be {{\"files\": {{name: {{\"size\": n, \"sha256\": hex}}}}}} "
                            f"with the {m['verify']} of each file"])
    return json.dumps(doc, indent=2, sort_keys=True) + "\n"


def _source(p: Profile, pin_override: str | None) -> tuple[str, str]:
    """(clone url, pinned commit) for the profile's source."""
    src = p.data["server"]["source"]
    if src["kind"] == "git":
        return src["url"], pin_override or src["ref"]
    repo, rel = p.root, src["path"]
    try:
        git = lambda *a: subprocess.run(["git", "-C", str(repo), *a], capture_output=True, text=True, check=True).stdout.strip()
        pin = git("rev-parse", f"HEAD:{rel}")
        url = git("config", "-f", ".gitmodules", "--get", f"submodule.{rel}.url")
        if (repo / rel / ".git").exists():
            dirty = subprocess.run(["git", "-C", str(repo / rel), "status", "--porcelain"], capture_output=True, text=True, check=True).stdout
            if dirty.strip():
                raise ProfileError([f"server.source.path: the submodule {rel} has local changes; commit or discard them first"])
    except subprocess.CalledProcessError as e:
        raise ProfileError([f"server.source.path: cannot read submodule {rel} from the repo ({(e.stderr or '').strip()})"])
    return url, pin_override or pin


def _content_steps(p: Profile, c: Ctx) -> list[str]:
    out = []
    for rel in p.get("content", default=[]):
        args_file = p.path(rel) / "deploy.args"
        if not args_file.is_file():
            raise ProfileError([f"content: {rel}/deploy.args not found (one shard_content deploy argument per line)"])
        args = [a for a in args_file.read_text(encoding="utf-8").replace("\r\n", "\n").split("\n") if a.strip()]
        words = " ".join(shlex.quote(a) for a in args)
        out.append(f"as_user python3 \"$MUO_REPO/tools/shard_content/run.py\" deploy --shard-dir \"$DIST\" {words}")
    return out


def deploy(p: Profile, pin: str | None = None) -> str:
    c = Ctx(p)
    d = p.data
    if pin is not None and not re.fullmatch(r"[0-9a-f]{40}", pin):
        raise ProfileError(["--pin: must be a 40-hex commit"])
    url, sha = _source(p, pin)
    patches = d["server"].get("patches", [])
    build = d["server"].get("build")
    assemblies = d["server"].get("assemblies", [])
    content = bool(d.get("content"))
    overlay = overlay_files(p)
    ck = c.keys

    out = c.header("deploy")
    out += [
        f"PIN={sha}",
        f"CLONE_URL={shlex.quote(url)}",
        "",
        "as_user() { runuser -u \"$SVC_USER\" -- env HOME=\"$BASE\" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \"$@\"; }",
        "have_systemd() { [ -d /run/systemd/system ]; }",
        "",
        "# preflight: bootstrap ran, the host values are there",
        '[ -d "$BASE" ] && [ -f "$ENV_FILE" ] || die "run bootstrap first"',
        "set -a; . \"$ENV_FILE\"; set +a",
        f': "${{{ck["client_data"]}:?{ck["client_data"]} is not set in $ENV_FILE}}"',
        f'[ -d "${ck["client_data"]}" ] || die "{ck["client_data"]} is not a folder on this host"',
    ]
    if content:
        out.append(': "${MUO_REPO:?MUO_REPO is not set in $ENV_FILE (a checkout of the profile repo, for content)}"')
    out += ["", "# stop the shard before its files change", 'if have_systemd && systemctl is-active --quiet "$UNIT"; then systemctl stop "$UNIT"; fi', ""]

    # patches go on the host first, so the checkout step can take them off and put them back
    out.append("# patches (embedded)")
    names = []
    for rel in patches:
        name = Path(rel).name
        names.append(name)
        out += _embed(f"$BASE/patches/{name}", p.path(rel).read_text(encoding="utf-8"))
    out += [
        "",
        "# checkout at the pin; patches off before the pin moves, on after",
        'if [ ! -d "$SRC/.git" ]; then as_user git clone --no-checkout "$CLONE_URL" "$SRC"; fi',
        'as_user git -C "$SRC" cat-file -e "$PIN^{commit}" 2>/dev/null || as_user git -C "$SRC" fetch origin',
        '# global.json is rewritten below for the installed SDK; put it back before anything else looks at the tree',
        'as_user git -C "$SRC" update-index --no-skip-worktree global.json 2>/dev/null || true',
        'as_user git -C "$SRC" checkout -q -- global.json 2>/dev/null || true',
        'head="$(as_user git -C "$SRC" rev-parse -q --verify HEAD 2>/dev/null || true)"',
        'if [ "$head" != "$PIN" ]; then',
    ]
    for name in reversed(names):
        out.append(f'    if as_user git -C "$SRC" apply -R --check "$BASE/patches/{name}" >/dev/null 2>&1; then as_user git -C "$SRC" apply -R "$BASE/patches/{name}"; fi')
    out += ['    as_user git -C "$SRC" checkout -q --detach "$PIN"', "fi"]
    for name in names:
        out += [
            f'if as_user git -C "$SRC" apply --check "$BASE/patches/{name}" >/dev/null 2>&1; then',
            f'    as_user git -C "$SRC" apply "$BASE/patches/{name}"',
            "else",
            f'    echo "muo_shard: {name} already applied, skipping"',
            "fi",
        ]

    out += [
        "",
        "# the archive's SDK can trail the SDK version the pin's global.json names; build with the installed one",
        'if [ -f "$SRC/global.json" ]; then',
        "    sdk=\"$(as_user sh -c 'cd / && dotnet --version')\"",
        '    as_user sed -i -E "s/\\"version\\": *\\"[^\\"]*\\"/\\"version\\": \\"$sdk\\"/" "$SRC/global.json"',
        '    as_user git -C "$SRC" update-index --skip-worktree global.json',
        "fi",
        "",
        "# build",
    ]
    if build:
        argv = " ".join(shlex.quote(a) for a in build["command"])
        out.append(f"as_user sh -c 'cd \"$1\" && shift && exec \"$@\"' sh \"$SRC\" {argv}")
        output = build["output"].replace("{pin}", sha)
        out.append(f"OUT=\"$SRC\"/{shlex.quote(output)}")
    else:
        out += [
            'arch=x64; [ "$(uname -m)" = aarch64 ] && arch=arm64',
            "as_user sh -c 'cd \"$1\" && exec bash ./publish.sh release linux \"$2\"' sh \"$SRC\" \"$arch\"",
            'OUT="$SRC/Distribution"',
        ]
    out += ['[ -x "$OUT/ModernUO" ] || die "the build left no ModernUO in $OUT"', 'as_user cp -a "$OUT/." "$DIST/"']

    if assemblies:
        out += ["", "# extra content assemblies"]
        out += _embed("$BASE/muo-assemblies.py", _ASSEMBLIES)
        out.append('install -d -o "$SVC_USER" -g "$SVC_USER" "$DIST/Assemblies" "$DIST/Data"')
        for a in assemblies:
            proj = shlex.quote(a["project"])
            asm = shlex.quote(a["assembly"])
            out += [
                f"as_user sh -c 'cd \"$1\" && exec dotnet publish \"$2\" -c Release -o \"$3\"' sh \"$SRC\" {proj} \"$BASE/asm\"",
                f'as_user cp -f "$BASE/asm/"{asm} "$DIST/Assemblies/"',
                f'python3 "$BASE/muo-assemblies.py" "$DIST/Data/assemblies.json" {asm}',
            ]

    out += ["", "# configuration: the template, the overlay over it (last wins), then the host values"]
    out += _embed("$DIST/Configuration/modernuo.json", render_template(p))
    for name, text in overlay:
        out += _embed(f"$DIST/Configuration/{name}", text)
    out += _embed("$BASE/muo-fill.py", _FILL)
    out += _embed("$BASE/muo-finish-config.py", _FINISH_CONFIG)
    if overlay:
        names_q = " ".join(shlex.quote(n) for n, _ in overlay)
        out.append(f'python3 "$BASE/muo-fill.py" "$DIST/Configuration" {names_q}')
    version = d.get("client_version", "")
    out.append(f'python3 "$BASE/muo-finish-config.py" "$DIST/Configuration/modernuo.json" "${ck["client_data"]}" {shlex.quote(version)}')

    out += ["", "# start script: the unit's one ExecStart; maps the host keys to the names the shard reads"]
    wrapper = "\n".join([
        "#!/bin/sh",
        "# generated by muo_shard",
        f'if [ -n "${{{ck["admin_user"]}:-}}" ]; then export UO_SHARD_OWNER="${ck["admin_user"]}"; fi',
        f'if [ -n "${{{ck["admin_password"]}:-}}" ]; then export UO_SHARD_OWNER_PASSWORD="${ck["admin_password"]}"; fi',
        'cd "$(dirname "$0")"',
        "exec ./ModernUO",
    ])
    out += _embed("$DIST/muo-run.sh", wrapper, "0755")

    if d.get("data_manifest"):
        out += ["", "# client data manifest, checked now and before every start"]
        verify = _VERIFY.replace("@KEY@", ck["client_data"]).replace("@MODE@", d["data_manifest"]["verify"])
        out += _embed("$DIST/muo-verify-data.py", verify, "0755")
        out += _embed("$DIST/muo-data-manifest.json", _manifest_text(p))
        out.append('python3 "$DIST/muo-verify-data.py"')

    if content:
        out += ["", "# content packs, after the build"]
        out += _content_steps(p, c)

    out += [
        "",
        "# ownership; Saves/ and Logs/ are never touched",
        'chown -R "$SVC_USER:$SVC_USER" "$DIST"',
        "",
        "# the service",
    ]
    out += _embed("$BASE/unit.new", unit_text(p))
    out += [
        'if have_systemd; then',
        '    if ! cmp -s "$BASE/unit.new" "/etc/systemd/system/$UNIT"; then',
        '        install -m 0644 "$BASE/unit.new" "/etc/systemd/system/$UNIT"',
        "        systemctl daemon-reload",
        "    fi",
        '    systemctl enable "$UNIT"',
        '    systemctl restart "$UNIT"',
        "else",
        '    echo "muo_shard: no systemd here (a container?). Start the shard with:"',
        '    echo "  set -a; . $ENV_FILE; set +a; runuser -u $SVC_USER -- $DIST/muo-run.sh"',
        "fi",
        'rm -f "$BASE/unit.new"',
        'echo "muo_shard: deploy of $ID done at ${PIN:0:9}; check with: run.py plan status"',
    ]
    return _finish(out)


# --------------------------------------------------------------- status


def status(p: Profile) -> str:
    c = Ctx(p)
    out = c.header("status", root=False)
    out += [
        "# read-only: changes nothing on the host",
        "if [ -d /run/systemd/system ]; then",
        '    systemctl status "$UNIT" --no-pager || true',
        "else",
        '    echo "no systemd on this host; unit state not available"',
        "fi",
        'if ss -ltn "sport = :$PORT" | grep -q LISTEN; then',
        '    echo "listening: tcp/$PORT"',
        "else",
        '    echo "NOT listening: tcp/$PORT" >&2',
        "    exit 1",
        "fi",
    ]
    return _finish(out)


VERBS = {"bootstrap": bootstrap, "deploy": deploy, "status": status}
