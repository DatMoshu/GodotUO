#!/usr/bin/env python3
"""The agent request queue: a chat window puts requests to running AI agent
sessions and gets replies back.

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
a Monitor watching `tail` wakes only for real requests.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sqlite3
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

MAX_TEXT = 8000          # characters per request or reply
MAX_ATTACHMENTS = 16
MAX_PATH = 1024
BROADCAST = "*"
STATUSES = ("new", "taken", "answered", "cancelled")
NAME_RE = re.compile(r"^[A-Za-z0-9_.-]{1,40}$")

SCHEMA = """
CREATE TABLE IF NOT EXISTS requests (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    to_agent    TEXT NOT NULL,
    from_agent  TEXT NOT NULL,
    text        TEXT NOT NULL,
    attachments TEXT NOT NULL DEFAULT '[]',
    status      TEXT NOT NULL DEFAULT 'new'
                CHECK (status IN ('new','taken','answered','cancelled')),
    created     TEXT NOT NULL,
    taken_by    TEXT,
    taken_at    TEXT
);
CREATE TABLE IF NOT EXISTS replies (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id  INTEGER NOT NULL REFERENCES requests(id),
    from_agent  TEXT NOT NULL,
    text        TEXT NOT NULL,
    attachments TEXT NOT NULL DEFAULT '[]',
    created     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS requests_status ON requests(status, to_agent, id);
CREATE INDEX IF NOT EXISTS replies_request ON replies(request_id, id);
"""

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
    db.executescript(SCHEMA)
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

def request_json(row: sqlite3.Row) -> dict:
    return {
        "id": row["id"], "to": row["to_agent"], "from": row["from_agent"],
        "text": row["text"], "attachments": json.loads(row["attachments"]),
        "status": row["status"], "created": row["created"],
        "taken_by": row["taken_by"], "taken_at": row["taken_at"],
    }


def reply_json(row: sqlite3.Row) -> dict:
    return {
        "id": row["id"], "request_id": row["request_id"], "from": row["from_agent"],
        "text": row["text"], "attachments": json.loads(row["attachments"]),
        "created": row["created"],
    }


def emit(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def get_request(db: sqlite3.Connection, request_id: int) -> sqlite3.Row:
    row = db.execute("SELECT * FROM requests WHERE id=?", (request_id,)).fetchone()
    if row is None:
        raise Refused(f"no request {request_id}")
    return row


# --- commands -------------------------------------------------------------

def cmd_post(db, args) -> int:
    to = check_name(args.to, "--to", allow_broadcast=True)
    sender = check_name(args.sender, "--from")
    text = check_text(read_text(args.text))
    attachments = check_attachments(args.attach)
    cur = db.execute(
        "INSERT INTO requests(to_agent, from_agent, text, attachments, status, created) VALUES (?,?,?,?, 'new', ?)",
        (to, sender, text, json.dumps(attachments), now()))
    print(cur.lastrowid)
    return 0


def claim(db: sqlite3.Connection, name: str, broadcast: bool, replay: bool) -> list[sqlite3.Row]:
    """Take every waiting request for `name` in one write transaction. The
    UPDATE re-checks status='new', so two watchers can never both take one."""
    where = "(to_agent=? OR to_agent='*')" if broadcast else "to_agent=?"
    db.execute("BEGIN IMMEDIATE")
    try:
        rows = db.execute(f"SELECT * FROM requests WHERE status='new' AND {where} ORDER BY id", (name,)).fetchall()
        taken = []
        stamp = now()
        for row in rows:
            cur = db.execute(
                "UPDATE requests SET status='taken', taken_by=?, taken_at=? WHERE id=? AND status='new'",
                (name, stamp, row["id"]))
            if cur.rowcount == 1:
                taken.append(row)
        if replay:
            # Requests this name took earlier and never answered: for a session
            # that died after taking one and before showing it.
            again = db.execute(
                "SELECT * FROM requests WHERE status='taken' AND taken_by=? AND id NOT IN (SELECT request_id FROM replies) ORDER BY id",
                (name,)).fetchall()
            seen = {r["id"] for r in taken}
            taken = [r for r in again if r["id"] not in seen] + taken
            taken.sort(key=lambda r: r["id"])
        db.execute("COMMIT")
    except BaseException:
        db.execute("ROLLBACK")
        raise
    # Fresh rows so status/taken_by show the claim.
    return [get_request(db, r["id"]) for r in taken]


def unclaim(db: sqlite3.Connection, name: str, request_id: int) -> None:
    db.execute("UPDATE requests SET status='new', taken_by=NULL, taken_at=NULL WHERE id=? AND status='taken' AND taken_by=?",
               (request_id, name))


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
                    emit(request_json(row))
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


def cmd_reply(db, args) -> int:
    text = check_text(read_text(args.text))
    attachments = check_attachments(args.attach)
    db.execute("BEGIN IMMEDIATE")
    try:
        request = get_request(db, args.id)
        if request["status"] == "cancelled":
            raise Refused(f"request {args.id} was cancelled; not replying")
        sender = check_name(args.sender or request["taken_by"] or request["to_agent"], "--from")
        if sender == BROADCAST:
            raise Refused("name yourself with --from; the request went to everyone", 2)
        cur = db.execute(
            "INSERT INTO replies(request_id, from_agent, text, attachments, created) VALUES (?,?,?,?,?)",
            (args.id, sender, text, json.dumps(attachments), now()))
        db.execute("UPDATE requests SET status='answered' WHERE id=?", (args.id,))
        db.execute("COMMIT")
    except BaseException:
        db.execute("ROLLBACK")
        raise
    print(cur.lastrowid)
    return 0


def cmd_show(db, args) -> int:
    request = request_json(get_request(db, args.id))
    request["replies"] = [reply_json(r) for r in db.execute(
        "SELECT * FROM replies WHERE request_id=? ORDER BY id", (args.id,))]
    print(json.dumps(request, ensure_ascii=False, indent=2))
    return 0


def cmd_list(db, args) -> int:
    sql, params = "SELECT * FROM requests WHERE 1=1", []
    if args.status:
        sql += " AND status=?"
        params.append(args.status)
    if args.to:
        sql += " AND to_agent=?"
        params.append(args.to)
    sql += " ORDER BY id DESC LIMIT ?"
    params.append(args.limit)
    for row in reversed(db.execute(sql, params).fetchall()):
        if args.json:
            emit(request_json(row))
        else:
            text = row["text"].replace("\n", " ")
            print(f"{row['id']:>5}  {row['status']:<9} {row['from_agent']} -> {row['to_agent']}  {text[:70]}")
    return 0


def cmd_cancel(db, args) -> int:
    cur = db.execute("UPDATE requests SET status='cancelled' WHERE id=? AND status IN ('new','taken')", (args.id,))
    if cur.rowcount == 1:
        return 0
    request = get_request(db, args.id)
    raise Refused(f"request {args.id} is already {request['status']}")


def cmd_watch_replies(db, args) -> int:
    if (args.id is None) == (args.sender is None):
        raise Refused("give a request ID or --from NAME, not both and not neither", 2)
    if args.id is not None:
        get_request(db, args.id)
    since = args.since_id
    if since is None:
        # By request: everything so far. By agent: only what arrives from now on.
        since = 0 if args.id is not None else (db.execute("SELECT COALESCE(MAX(id),0) FROM replies").fetchone()[0])
    deadline = time.monotonic() + args.timeout if args.timeout else None
    try:
        while True:
            if args.id is not None:
                rows = db.execute("SELECT * FROM replies WHERE request_id=? AND id>? ORDER BY id", (args.id, since)).fetchall()
            else:
                rows = db.execute("SELECT * FROM replies WHERE from_agent=? AND id>? ORDER BY id", (args.sender, since)).fetchall()
            for row in rows:
                try:
                    emit(reply_json(row))
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
    p.set_defaults(fn=cmd_post)

    p = sub.add_parser("tail", help="print each new request for an agent as a JSON line, marking it taken")
    p.add_argument("--as", dest="agent", required=True, help="this session's agent name")
    p.add_argument("--once", action="store_true", help="block until at least one request arrives, print, exit")
    p.add_argument("--include-broadcast", action="store_true", help="also take requests addressed to *")
    p.add_argument("--timeout", type=float, default=0, help="seconds to wait before giving up with exit 3 (0 = never)")
    p.add_argument("--interval", type=float, default=0.5, help="poll interval in seconds")
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

    p = sub.add_parser("list", help="list requests, newest last")
    p.add_argument("--status", choices=STATUSES)
    p.add_argument("--to")
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
