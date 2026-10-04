#!/usr/bin/env python3
"""The agent queue: a switchboard-compatible bus for running agent sessions.

One SQLite file per user (setting UO_AGENT_QUEUE). A watching session runs
`tail --as NAME` under its Monitor tool; each request addressed to it comes out
as one JSON line, is marked taken atomically, and the session answers with
`reply`. See tools/agent_queue/README.md and docs/data_formats.md section 21.

Usage:
    python tools/agent_queue/run.py post --to NAME --from NAME TEXT [--attach PATH]
    python tools/agent_queue/run.py tail --as NAME [--once] [--include-broadcast]
    python tools/agent_queue/run.py reply ID TEXT [--from NAME] [--attach PATH]
    python tools/agent_queue/run.py show ID
    python tools/agent_queue/run.py list [--status S] [--to NAME] [--json]
    python tools/agent_queue/run.py cancel ID
    python tools/agent_queue/run.py watch-replies ID | --from NAME

Or through the launcher:  launchers\\dev\\agent_queue.bat <same arguments>

Exit codes: 0 ok, 1 refused (unknown id, wrong state), 2 bad input, 3 timeout.
Standard output carries data only (ids, JSON lines); messages go to stderr, so
a Monitor watching `tail` wakes only for addressed messages.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sqlite3
import shutil
import sys
import time
from datetime import datetime, timezone, timedelta
from pathlib import Path

MAX_TEXT = 8000          # characters per request or reply
MAX_ATTACHMENTS = 16
MAX_PATH = 1024
BROADCAST = "*"
STATUSES = ("new", "taken", "answered", "cancelled")
NAME_RE = re.compile(r"^[A-Za-z0-9_.-]{1,40}$")

from store import SCHEMA, KINDS, PROVENANCE, OWNER_PROVENANCE, migrate, message, seen

# Never store secrets: refuse the shapes that are unmistakably credentials.
SECRET_PATTERNS = [
    re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
    re.compile(r"\bsk-[A-Za-z0-9_-]{20,}"),
    re.compile(r"\bgh[pousr]_[A-Za-z0-9]{30,}"),
    re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    re.compile(r"\bxox[abprs]-[A-Za-z0-9-]{10,}"),
    re.compile(r"(?i)\bauthorization:\s*bearer\s+\S{16,}"),
    re.compile(r"(?i)\b(?:password|passwd|secret|api[_-]?key|token)\s*[:=]\s*\S{8,}"),
]
SECRET_FILE_RE = re.compile(r"(?i)(^\.env(\..*)?$|\.pem$|\.key$|\.pfx$|^id_(rsa|ed25519|ecdsa)$|^credentials(\.json)?$)")


class Refused(Exception):
    """A request the tool will not carry out; `code` is the exit status."""

    def __init__(self, message: str, code: int = 1):
        super().__init__(message)
        self.code = code


def now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


# --- location -------------------------------------------------------------

def default_db_path() -> Path:
    if sys.platform == "win32":
        base = os.environ.get("APPDATA") or str(Path.home() / "AppData" / "Roaming")
        return Path(base) / "GUO" / "agent_queue.db"
    base = os.environ.get("XDG_CONFIG_HOME") or str(Path.home() / ".config")
    return Path(base) / "guo" / "agent_queue.db"


def resolve_db_path(explicit: str | None) -> Path:
    """--db, then UO_AGENT_QUEUE (environment, config.local.bat, config.bat via
    tools/guo), then the per-user default."""
    if explicit:
        return Path(os.path.expandvars(explicit))
    env = os.environ.get("UO_AGENT_QUEUE")
    if env and "%" not in os.path.expandvars(env):
        return Path(os.path.expandvars(env))
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
        from guo.config import load_config
        found = load_config().agent_queue
        if found:
            return Path(found)
    except Exception:
        pass
    return default_db_path()


def connect(path: Path) -> sqlite3.Connection:
    created = not path.exists()
    path.parent.mkdir(parents=True, exist_ok=True)
    # isolation_level=None: autocommit, with explicit BEGIN IMMEDIATE where a
    # read-then-write must be one step.
    db = sqlite3.connect(str(path), timeout=30, isolation_level=None)
    db.row_factory = sqlite3.Row
    db.execute("PRAGMA busy_timeout=30000")
    db.execute("PRAGMA journal_mode=WAL")
    db.execute("PRAGMA synchronous=NORMAL")
    db.execute("PRAGMA foreign_keys=ON")
    try:
        migrate(db)
    except BaseException:
        db.close()
        raise
    if created and os.name == "posix":
        try:
            os.chmod(path, 0o600)
        except OSError:
            pass
    return db


# --- validation -----------------------------------------------------------

def check_name(name: str, what: str, allow_broadcast: bool = False) -> str:
    if allow_broadcast and name == BROADCAST:
        return name
    if not NAME_RE.match(name or ""):
        raise Refused(f"{what} '{name}' is not a valid agent name (1-40 of letters, digits, _ . -)", 2)
    return name


def check_text(text: str) -> str:
    if not text or not text.strip():
        raise Refused("text is empty", 2)
    if len(text) > MAX_TEXT:
        raise Refused(f"text is {len(text)} characters; the limit is {MAX_TEXT}. Put the long part in a file and --attach its path", 2)
    for pattern in SECRET_PATTERNS:
        if pattern.search(text):
            raise Refused("text looks like it holds a secret (key, token or password); the queue never stores those. Name where it lives instead", 2)
    return text


def check_attachments(paths: list[str] | None) -> list[str]:
    out: list[str] = []
    for raw in paths or []:
        if not raw or len(raw) > MAX_PATH:
            raise Refused(f"attachment path is empty or longer than {MAX_PATH} characters", 2)
        if re.match(r"^[A-Za-z][A-Za-z0-9+.-]+://", raw):
            raise Refused(f"attachment '{raw}' is a URL; attachments are local paths only", 2)
        if SECRET_FILE_RE.search(Path(raw).name):
            raise Refused(f"attachment '{Path(raw).name}' looks like a key or credentials file; the queue does not carry those", 2)
        # Absolute so the reader does not need the poster's working folder.
        # The file is never opened or copied here.
        out.append(str(Path(raw).expanduser().absolute()))
    if len(out) > MAX_ATTACHMENTS:
        raise Refused(f"{len(out)} attachments; the limit is {MAX_ATTACHMENTS}", 2)
    return out


def read_text(arg: str) -> str:
    return sys.stdin.read() if arg == "-" else arg


# --- rows -> JSON ---------------------------------------------------------

def emit(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def get_request(db: sqlite3.Connection, request_id: int) -> sqlite3.Row:
    row = db.execute("SELECT * FROM messages WHERE id=?", (request_id,)).fetchone()
    if row is None:
        raise Refused(f"no request {request_id}")
    return row


# --- commands -------------------------------------------------------------

def insert_message(db, *, to, sender, text, kind="request", ref=None, provenance="agent", url=None, key=None, attachments=None):
    if kind == "approval" and provenance not in OWNER_PROVENANCE:
        raise Refused("an approval requires an owner provenance")
    cur = db.execute("INSERT OR IGNORE INTO messages(to_addr,from_addr,kind,text,ref_id,provenance,source_url,source_key,created) VALUES(?,?,?,?,?,?,?,?,?)",
                     (to, sender, kind, text, ref, provenance, url, key, now()))
    if not cur.rowcount:
        return None
    if attachments:
        db.execute("INSERT INTO message_attachments(message_id,attachments) VALUES(?,?)", (cur.lastrowid, json.dumps(attachments)))
    return cur.lastrowid


def cmd_post(db, args):
    to = check_name(args.to, "--to", allow_broadcast=True)
    sender = check_name(args.sender, "--from")
    text = check_text(read_text(args.text))
    attachments = check_attachments(args.attach)
    db.execute("BEGIN IMMEDIATE")
    try:
        mid = insert_message(db, to=to, sender=sender, text=text, kind=args.kind, ref=args.ref,
                             provenance=args.provenance, url=args.url, key=args.key, attachments=attachments)
        db.execute("COMMIT")
    except BaseException:
        db.execute("ROLLBACK")
        raise
    print(mid)
    return 0


def claim(db, name, broadcast, replay):
    where = "(to_addr=? OR to_addr='*')" if broadcast else "to_addr=?"
    db.execute("BEGIN IMMEDIATE")
    try:
        rows = db.execute(f"SELECT * FROM messages WHERE status='new' AND {where} ORDER BY id", (name,)).fetchall()
        stamp = now()
        ids = [r["id"] for r in rows]
        for mid in ids:
            db.execute("UPDATE messages SET status='taken',taken_by=?,taken_at=? WHERE id=? AND status='new'", (name,stamp,mid))
        if replay:
            ids += [r[0] for r in db.execute("SELECT id FROM messages WHERE status='taken' AND taken_by=? AND id NOT IN (SELECT ref_id FROM messages WHERE kind='reply' AND ref_id IS NOT NULL)", (name,))]
        seen(db, name, stamp)
        db.execute("COMMIT")
    except BaseException:
        db.execute("ROLLBACK")
        raise
    return [get_request(db, mid) for mid in sorted(set(ids))]


def unclaim(db, name, request_id):
    db.execute("UPDATE messages SET status='new',taken_by=NULL,taken_at=NULL WHERE id=? AND status='taken' AND taken_by=?", (request_id,name))


def cmd_take(db, args):
    name = check_name(args.agent, "--as")
    for r in claim(db, name, args.include_broadcast, False):
        emit(message(db, r))
    return 0


def cmd_tail(db, args) -> int:
    name = check_name(args.agent, "--as")
    deadline = time.monotonic() + args.timeout if args.timeout else None
    replay = args.replay_taken
    try:
        while True:
            rows = claim(db, name, args.include_broadcast, replay)
            replay = False
            for index, row in enumerate(rows):
                try:
                    emit(message(db, row))
                except (BrokenPipeError, OSError):
                    # The reader is gone: give back what was claimed but not shown.
                    for unseen in rows[index:]:
                        unclaim(db, name, unseen["id"])
                    return 0
            if rows and args.once:
                return 0
            if deadline and time.monotonic() >= deadline:
                print(f"tail: no request for {name} within {args.timeout:g}s", file=sys.stderr)
                return 3
            time.sleep(args.interval)
    except KeyboardInterrupt:
        return 0


def cmd_reply(db, args):
    text = check_text(read_text(args.text))
    attachments = check_attachments(args.attach)
    db.execute("BEGIN IMMEDIATE")
    try:
        request = get_request(db, args.id)
        if request["status"] == "cancelled":
            raise Refused(f"request {args.id} was cancelled; not replying")
        sender = check_name(args.sender or request["taken_by"] or request["to_addr"], "--from")
        mid = insert_message(db, to=request["from_addr"], sender=sender, text=text, kind="reply", ref=args.id, attachments=attachments)
        db.execute("UPDATE messages SET status='answered',answered_at=? WHERE id=?", (now(),args.id))
        seen(db, sender, now())
        db.execute("COMMIT")
    except BaseException:
        db.execute("ROLLBACK")
        raise
    print(mid)
    return 0


def cmd_show(db, args):
    out = message(db, get_request(db,args.id))
    out["replies"] = [message(db,r) for r in db.execute("SELECT * FROM messages WHERE kind='reply' AND ref_id=? ORDER BY id", (args.id,))]
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0


def cmd_list(db, args):
    sql, params = "SELECT * FROM messages WHERE 1=1", []
    for col, val in (("status",args.status),("to_addr",args.to),("from_addr",args.sender),("kind",args.kind)):
        if val:
            sql += f" AND {col}=?"
            params.append(val)
    sql += " ORDER BY id DESC LIMIT ?"
    params.append(args.limit)
    for row in reversed(db.execute(sql,params).fetchall()):
        emit(message(db,row))
    return 0


def cmd_cancel(db, args):
    cur = db.execute("UPDATE messages SET status='cancelled' WHERE id=? AND status IN ('new','taken')", (args.id,))
    if cur.rowcount == 1:
        return 0
    request = get_request(db,args.id)
    raise Refused(f"request {args.id} is already {request['status']}")


def cmd_replies(db, args):
    get_request(db,args.id)
    deadline = time.monotonic() + (args.wait or 0)
    while True:
        rows = db.execute("SELECT * FROM messages WHERE ref_id=? ORDER BY id", (args.id,)).fetchall()
        if rows:
            for r in rows:
                emit(message(db,r))
            return 0
        if time.monotonic() >= deadline:
            return 3
        time.sleep(min(0.5,max(0,deadline-time.monotonic())))


def stale(stamp, minutes=15):
    return stamp is None or datetime.fromisoformat(stamp.replace("Z","+00:00")) < datetime.now(timezone.utc)-timedelta(minutes=minutes)


def cmd_status(db,args):
    out = {"db": str(resolve_db_path(args.db)), "addresses": [], "waiting": [], "leases": []}
    for r in db.execute("SELECT * FROM addresses ORDER BY project,address"):
        out["addresses"].append(dict(r,stale=stale(r["last_seen"])))
    for r in db.execute("SELECT to_addr,COUNT(*) n,MIN(created) oldest FROM messages WHERE status='new' GROUP BY to_addr"):
        out["waiting"].append(dict(r,stale=stale(r["oldest"])))
    for r in db.execute("SELECT * FROM leases ORDER BY resource"):
        out["leases"].append(dict(r,expired=stale(r["until"],0)))
    print(json.dumps(out,ensure_ascii=False,indent=2))
    return 0


def cmd_register(db,args):
    check_name(args.address,"address")
    db.execute("BEGIN IMMEDIATE")
    try:
        if args.integrator:
            other=db.execute("SELECT address FROM addresses WHERE project=? AND integrator=1 AND address<>?",(args.project,args.address)).fetchone()
            if other:
                raise Refused(f"{args.project} already has an integrator ({other[0]})")
        db.execute("INSERT INTO addresses(address,project,tool,role,integrator,registered) VALUES(?,?,?,?,?,?) ON CONFLICT(address) DO UPDATE SET project=excluded.project,tool=excluded.tool,role=excluded.role,integrator=excluded.integrator",
                   (args.address,args.project,args.tool,args.role,int(args.integrator),now()))
        db.execute("COMMIT")
    except BaseException:
        db.execute("ROLLBACK")
        raise
    print(f"registered {args.address}")
    return 0


def cmd_lease(db,args):
    if args.action == "list":
        for r in db.execute("SELECT * FROM leases ORDER BY resource"):
            emit(dict(r))
        return 0
    if not args.resource or not args.holder:
        raise Refused("lease take/release need RESOURCE and --holder",2)
    check_name(args.holder,"--holder")
    if args.action == "release":
        n=db.execute("DELETE FROM leases WHERE resource=? AND holder=?",(args.resource,args.holder)).rowcount
        if not n:
            raise Refused(f"not held by {args.holder}")
        print("released")
        return 0
    if args.minutes <= 0:
        raise Refused("--minutes must be positive",2)
    if args.min_free_gb and args.resource.lower().startswith(("build:","disk:")):
        drive=args.resource.split(":",1)[1].strip("\\/")
        if len(drive)!=1 or not drive.isalpha():
            raise Refused("disk lease needs a drive letter",2)
        free=shutil.disk_usage(f"{drive}:\\").free/2**30
        if free < args.min_free_gb:
            raise Refused(f"free space {free:.0f} GB is below the {args.min_free_gb:g} GB floor")
    db.execute("BEGIN IMMEDIATE")
    try:
        held=db.execute("SELECT * FROM leases WHERE resource=?",(args.resource,)).fetchone()
        if held and held["holder"]!=args.holder and not stale(held["until"],0):
            raise Refused(f"{args.resource} is held by {held['holder']} until {held['until']}")
        until=(datetime.now(timezone.utc)+timedelta(minutes=args.minutes)).isoformat(timespec="seconds")
        db.execute("INSERT OR REPLACE INTO leases VALUES(?,?,?,?,?)",(args.resource,args.holder,args.purpose,now(),until))
        db.execute("COMMIT")
    except BaseException:
        db.execute("ROLLBACK")
        raise
    print(f"leased {args.resource} to {args.holder} until {until}")
    return 0


def cmd_watch_replies(db, args) -> int:
    if (args.id is None) == (args.sender is None):
        raise Refused("give a request ID or --from NAME, not both and not neither", 2)
    if args.id is not None:
        get_request(db, args.id)
    since = args.since_id
    if since is None:
        # By request: everything so far. By agent: only what arrives from now on.
        since = 0 if args.id is not None else (db.execute("SELECT COALESCE(MAX(id),0) FROM messages WHERE kind='reply'").fetchone()[0])
    deadline = time.monotonic() + args.timeout if args.timeout else None
    try:
        while True:
            if args.id is not None:
                rows = db.execute("SELECT * FROM messages WHERE kind='reply' AND ref_id=? AND id>? ORDER BY id", (args.id, since)).fetchall()
            else:
                rows = db.execute("SELECT * FROM messages WHERE kind='reply' AND from_addr=? AND id>? ORDER BY id", (args.sender, since)).fetchall()
            for row in rows:
                try:
                    emit(message(db,row))
                except (BrokenPipeError, OSError):
                    return 0
                since = row["id"]
            if rows and args.once:
                return 0
            if args.id is not None and not rows and get_request(db, args.id)["status"] == "cancelled":
                print(f"watch-replies: request {args.id} was cancelled", file=sys.stderr)
                return 1
            if deadline and time.monotonic() >= deadline:
                print("watch-replies: timed out", file=sys.stderr)
                return 3
            time.sleep(args.interval)
    except KeyboardInterrupt:
        return 0


# --- command line ---------------------------------------------------------

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="The agent request queue (see tools/agent_queue/README.md).")
    parser.add_argument("--db", help="queue file (default: UO_AGENT_QUEUE, else the per-user config folder)")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("post", help="put a request to an agent; prints its id")
    p.add_argument("--to", required=True, help="agent name, or * for whichever watcher takes it first")
    p.add_argument("--from", dest="sender", required=True, help="who is asking (the chat window's name)")
    p.add_argument("text", help="the request; - reads standard input")
    p.add_argument("--attach", action="append", metavar="PATH", help="a local file path to point at (repeatable); never copied")
    p.add_argument("--kind", choices=KINDS, default="request")
    p.add_argument("--ref", type=int)
    p.add_argument("--provenance", choices=PROVENANCE, default="agent")
    p.add_argument("--url")
    p.add_argument("--key", help="unique source key; a duplicate does not post twice")
    p.set_defaults(fn=cmd_post)

    p = sub.add_parser("tail", help="print each new message for an address, marking it taken")
    p.add_argument("--as", dest="agent", required=True, help="this session's agent name")
    p.add_argument("--once", action="store_true", help="block until at least one request arrives, print, exit")
    p.add_argument("--include-broadcast", action="store_true", help="also take requests addressed to *")
    p.add_argument("--timeout", type=float, default=0, help="seconds to wait before giving up with exit 3 (0 = never)")
    p.add_argument("--interval", "--poll", type=float, default=0.5, help="poll interval in seconds")
    p.add_argument("--replay-taken", action="store_true", help="first re-print requests this name took and never answered")
    p.set_defaults(fn=cmd_tail)

    p = sub.add_parser("reply", help="answer a request")
    p.add_argument("id", type=int)
    p.add_argument("text", help="the reply; - reads standard input")
    p.add_argument("--from", dest="sender", help="who is answering (default: the agent that took it)")
    p.add_argument("--attach", action="append", metavar="PATH")
    p.set_defaults(fn=cmd_reply)

    p = sub.add_parser("show", help="print a request and its replies")
    p.add_argument("id", type=int)
    p.set_defaults(fn=cmd_show)

    p = sub.add_parser("list", help="list messages, newest last")
    p.add_argument("--status", choices=STATUSES)
    p.add_argument("--to")
    p.add_argument("--from", dest="sender")
    p.add_argument("--kind", choices=KINDS)
    p.add_argument("--limit", type=int, default=50)
    p.add_argument("--json", action="store_true", help="one JSON line per request")
    p.set_defaults(fn=cmd_list)

    p = sub.add_parser("cancel", help="withdraw a request that is not yet answered")
    p.add_argument("id", type=int)
    p.set_defaults(fn=cmd_cancel)

    p = sub.add_parser("watch-replies", help="stream replies as JSON lines (the chat side)")
    p.add_argument("id", type=int, nargs="?", help="a request id")
    p.add_argument("--from", dest="sender", help="every reply from this agent, from now on")
    p.add_argument("--since-id", type=int, help="only replies with a larger id")
    p.add_argument("--once", action="store_true")
    p.add_argument("--timeout", type=float, default=0)
    p.add_argument("--interval", type=float, default=0.5)
    p.set_defaults(fn=cmd_watch_replies)
    p = sub.add_parser("take", help="take waiting messages and exit immediately")
    p.add_argument("--as", dest="agent", required=True)
    p.add_argument("--include-broadcast", action="store_true")
    p.set_defaults(fn=cmd_take)
    p = sub.add_parser("replies", help="read replies, optionally waiting for one")
    p.add_argument("id", type=int)
    p.add_argument("--wait", type=float, default=0)
    p.set_defaults(fn=cmd_replies)
    sub.add_parser("status").set_defaults(fn=cmd_status)
    p = sub.add_parser("register")
    p.add_argument("address")
    p.add_argument("--project", required=True)
    p.add_argument("--tool", required=True, choices=("claude","codex","dot","human","script"))
    p.add_argument("--role", default="director")
    p.add_argument("--integrator", action="store_true")
    p.set_defaults(fn=cmd_register)
    p = sub.add_parser("lease")
    p.add_argument("action", choices=("take","release","list"))
    p.add_argument("resource", nargs="?")
    p.add_argument("--holder")
    p.add_argument("--purpose", default="")
    p.add_argument("--minutes", type=int, default=60)
    p.add_argument("--min-free-gb", type=float)
    p.set_defaults(fn=cmd_lease)
    return parser


def main(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", newline="\n")
        except (AttributeError, ValueError):
            pass
    args = build_parser().parse_args(argv)
    try:
        db = connect(resolve_db_path(args.db))
        try:
            return args.fn(db, args)
        finally:
            db.close()
    except Refused as error:
        print(f"agent_queue: {error}", file=sys.stderr)
        return error.code
    except sqlite3.OperationalError as error:
        print(f"agent_queue: the queue file is busy or unreadable: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
