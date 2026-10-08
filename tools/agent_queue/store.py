"""Switchboard storage; all callers use the same messages and lease rows.

The public schema follows the locally authored switchboard. Attachments are a
GUO extension in a separate table, so an unmodified switchboard can share it.
"""
import json
import sqlite3

KINDS = ("request", "reply", "approval", "stop", "note")
OWNER_PROVENANCE = ("owner-discord", "owner-reaction", "owner-terminal", "owner-dot-chat")
PROVENANCE = OWNER_PROVENANCE + ("relayed", "agent")
SCHEMA = """
CREATE TABLE IF NOT EXISTS messages(
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 to_addr TEXT NOT NULL, from_addr TEXT NOT NULL, kind TEXT NOT NULL,
 text TEXT NOT NULL, ref_id INTEGER, provenance TEXT NOT NULL,
 source_url TEXT, source_key TEXT UNIQUE,
 status TEXT NOT NULL DEFAULT 'new', created TEXT NOT NULL,
 taken_by TEXT, taken_at TEXT, answered_at TEXT);
CREATE INDEX IF NOT EXISTS messages_inbox ON messages(to_addr, status);
CREATE TABLE IF NOT EXISTS addresses(
 address TEXT PRIMARY KEY, project TEXT, tool TEXT, role TEXT,
 integrator INTEGER NOT NULL DEFAULT 0, registered TEXT, last_seen TEXT);
CREATE TABLE IF NOT EXISTS leases(
 resource TEXT PRIMARY KEY, holder TEXT NOT NULL, purpose TEXT,
 taken TEXT NOT NULL, until TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS message_attachments(
 message_id INTEGER PRIMARY KEY REFERENCES messages(id),
 attachments TEXT NOT NULL DEFAULT '[]', legacy_reply_id INTEGER);
"""


def migrate(db):
    """Copy a legacy queue atomically, keeping original tables as an archive.

    Request ids survive. Reply ids get the shared message sequence; their old ids
    remain in message_attachments and replies_legacy. Refuse an ambiguous mixed
    database rather than replacing existing switchboard messages.
    """
    db.executescript(SCHEMA)
    db.execute("BEGIN IMMEDIATE")
    try:
        tables = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if "requests" in tables:
            if db.execute("SELECT 1 FROM messages LIMIT 1").fetchone():
                raise sqlite3.OperationalError("legacy and switchboard messages coexist; migration needs a separate database")
            db.execute("""INSERT INTO messages(id,to_addr,from_addr,kind,text,provenance,status,created,taken_by,taken_at)
                SELECT id,to_agent,from_agent,'request',text,'agent',status,created,taken_by,taken_at FROM requests""")
            db.execute("INSERT INTO message_attachments(message_id,attachments) SELECT id,attachments FROM requests")
            if "replies" in tables:
                for r in db.execute("SELECT * FROM replies ORDER BY id").fetchall():
                    parent = db.execute("SELECT from_addr FROM messages WHERE id=?", (r["request_id"],)).fetchone()
                    if parent is None:
                        raise sqlite3.OperationalError("legacy reply has no parent request")
                    cur = db.execute("""INSERT INTO messages(to_addr,from_addr,kind,text,ref_id,provenance,created)
                        VALUES(?,?,'reply',?,?,'agent',?)""",
                        (parent[0], r["from_agent"], r["text"], r["request_id"], r["created"]))
                    db.execute("INSERT INTO message_attachments VALUES(?,?,?)", (cur.lastrowid, r["attachments"], r["id"]))
                    db.execute("UPDATE messages SET answered_at=? WHERE id=?", (r["created"], r["request_id"]))
                db.execute("ALTER TABLE replies RENAME TO replies_legacy")
            db.execute("ALTER TABLE requests RENAME TO requests_legacy")
        db.execute("COMMIT")
    except BaseException:
        db.execute("ROLLBACK")
        raise


def message(db, row):
    """Canonical switchboard keys plus the existing GUO JSON aliases."""
    out = dict(row)
    a = db.execute("SELECT attachments FROM message_attachments WHERE message_id=?", (row["id"],)).fetchone()
    out.update(to=row["to_addr"], **{"from": row["from_addr"]},
               attachments=json.loads(a[0]) if a else [])
    if row["kind"] == "reply":
        out["request_id"] = row["ref_id"]
    return out


def seen(db, address, stamp):
    db.execute("INSERT INTO addresses(address,registered,last_seen) VALUES(?,?,?) "
               "ON CONFLICT(address) DO UPDATE SET last_seen=excluded.last_seen", (address, stamp, stamp))
