"""The god view's actions (AD2b, AD2c), checked live on the private instance: admin-check's section 7.

Spawners first, with nobody logged in: two test spawners (three horses, one
healer) are put through the bridge; Respawn replaces the horses with three new
ones and Clear removes them, each seen in the god view's pushes; a spawner
action on a creature or an unknown serial is refused, and so is every action
without the token.

Still with nobody logged in, the Admin tab's hidden presence acts (AD2c):
  Go there    puts its spot in Britain (the reply says hidden, as nobody);
  Bring here  brings a horse to that spot (the god view shows it there);
  paperdoll   answers with the healer's paperdoll: its name and what it wears;
  Follow      moves the spot with the horse: the horse is brought away to a
              spot named in the request, and the healer, brought "here"
              after, arrives beside the horse; Stop counts the moves; a second
              Follow ends by itself when the horse is deleted;
  "as" with "hidden" is refused, and the presence is never in the world (Find
  by its serial finds nothing).

Then a headless client logs in as a game master lane account (staff_client.py)
and, with that character as the admin's own (and the presence asked for by
"hidden" leaves the character where it is):
  Go there    moves it to a spot in Britain (the god view shows it there);
  Bring here  brings a horse to it; the admin's own character and a spawner cannot be brought;
  paperdoll   opens the healer's paperdoll in that client (its objects dump lists the gump);
  Follow      takes it to the horse, brings it back after a Go there away
              (the bridge's own moves), stops on request, and ends by itself when
              the horse is deleted, with a push that says why;
  "as" naming nobody online is refused.
Every action and refusal is in the audit log.
"""

from __future__ import annotations

import json
import socket
import time
import uuid
from pathlib import Path

from staff_client import START, StaffClient

HORSES_AT = (1170, 1660)
HEALER_AT = (1172, 1662)
BRITAIN = (1495, 1628)


class Session:
    """One admin connection that keeps every message: god view pushes update the rows, the rest wait to be read."""

    def __init__(self, port: int, token: str, name: str):
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=10)
        # A plain buffer, not makefile(): a file whose read timed out cannot be read again.
        self.buf = b""
        self.rows: dict[int, dict] = {}
        self.inbox: list[dict] = []
        self.req = 100
        self.send({"op": "hello", "editor": name, "admin_token": token})
        self.hello = self.wait(lambda m: m.get("op") == "hello", 10)

    def send(self, msg: dict) -> None:
        self.sock.sendall((json.dumps(msg) + "\n").encode("utf-8"))

    def _read(self, seconds: float) -> dict | None:
        end = time.monotonic() + max(0.05, seconds)
        while b"\n" not in self.buf:
            left = end - time.monotonic()
            if left <= 0:
                return None
            self.sock.settimeout(left)
            try:
                chunk = self.sock.recv(1 << 20)
            except (socket.timeout, TimeoutError):
                return None
            if not chunk:
                return None
            self.buf += chunk
        line, self.buf = self.buf.split(b"\n", 1)
        msg = json.loads(line.decode("utf-8"))
        if msg.get("op") == "admin_godview" and msg.get("ok"):
            if msg.get("full"):
                self.rows.clear()
            for r in msg.get("upsert") or []:
                self.rows[r["serial"]] = r
            for s in msg.get("removed") or []:
                self.rows.pop(s, None)
        return msg

    def wait(self, want, seconds: float) -> dict | None:
        """The first kept or new message that satisfies want, removed from the inbox; None when the time is up."""
        for m in self.inbox:
            if want(m):
                self.inbox.remove(m)
                return m
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            m = self._read(end - time.monotonic())
            if m is None:
                continue
            if want(m):
                return m
            if m.get("op") != "admin_godview":
                self.inbox.append(m)
        return None

    def until(self, cond, seconds: float) -> bool:
        """Reads pushes until cond() holds over the rows."""
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            if cond():
                return True
            m = self._read(min(0.5, end - time.monotonic()))
            if m is not None and m.get("op") != "admin_godview":
                self.inbox.append(m)
        return cond()

    def ask(self, msg: dict, seconds: float = 15) -> dict | None:
        self.req += 1
        msg = {**msg, "req": self.req}
        self.send(msg)
        return self.wait(lambda m: m.get("op") == msg["op"] and m.get("req") == self.req, seconds)

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


def _dump(watch: Path, name: str, kind: str = "request", body: str = "", seconds: float = 20) -> dict | str | None:
    """Asks the staff client's objects watch for a dump (or another request) and returns its answer."""
    answer = {"request": f"{name}.json", "closegumps": f"{name}.closed"}[kind]
    (watch / answer).unlink(missing_ok=True)
    (watch / f"{name}.{kind}").write_text(body, encoding="utf-8")
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        f = watch / answer
        if f.is_file():
            time.sleep(0.2)
            text = f.read_text(encoding="utf-8")
            return json.loads(text) if kind == "request" else text
        time.sleep(0.25)
    return None


def run(check, cfg, port: int, token: str, shard_port: int, out: Path, with_client: bool = True) -> None:
    # Without the token, no action runs.
    plain = socket.create_connection(("127.0.0.1", port), timeout=10)
    pr = plain.makefile("r", encoding="utf-8", newline="\n")

    def plain_ask(msg):
        plain.sendall((json.dumps(msg) + "\n").encode("utf-8"))
        while True:
            line = pr.readline()
            if not line:
                return None
            m = json.loads(line)
            if m.get("op") == msg["op"]:
                return m

    plain_ask({"op": "hello", "editor": "admin-check plain"})
    refused = [plain_ask({"op": op, "serial": 1, "action": "clear"})
               for op in ("admin_goto", "admin_bring", "admin_paperdoll", "admin_follow", "admin_spawner")]
    check(all(r is not None and r.get("ok") is False and "admin token" in r.get("error", "") for r in refused),
          "no god view action runs without the token (Go there, Bring here, paperdoll, Follow, spawner)")
    plain.close()

    s = Session(port, token, "admin-check actions")
    full = s.ask({"op": "admin_godview", "facet": 0})
    check(full is not None and full.get("ok") is True, "the actions check watches Felucca")
    # Spawners already on those spots (left by an interrupted earlier run) are not this run's.
    stale = {r["serial"] for r in s.rows.values() if r.get("kind") == "spawner" and (r["x"], r["y"]) in (HORSES_AT, HEALER_AT)}
    horses_id, healer_id = str(uuid.uuid4()), str(uuid.uuid4())
    for gid, at, entry, count in ((horses_id, HORSES_AT, "Horse", 3), (healer_id, HEALER_AT, "Healer", 1)):
        s.send({"op": "object", "action": "put", "kind": "spawner", "object": {
            "id": gid, "map": "Felucca", "x": at[0], "y": at[1], "z": 0, "count": count, "home_range": 2,
            "entries": [{"name": entry, "max": count}]}})
    try:
        _spawners_and_character(check, s, cfg, shard_port, out, with_client, stale)
    finally:
        for gid in (horses_id, healer_id):
            s.send({"op": "object", "action": "delete", "kind": "spawner", "id": gid})
        s.until(lambda: not [r for r in s.rows.values() if r.get("kind") == "spawner" and r["serial"] not in stale
                             and (r["x"], r["y"]) in (HORSES_AT, HEALER_AT)], 10)
        s.close()


def _spawners_and_character(check, s: Session, cfg, shard_port: int, out: Path, with_client: bool, stale: set) -> None:
    def spawner_at(at):
        return next((r for r in s.rows.values() if r.get("kind") == "spawner" and (r["x"], r["y"]) == at
                     and r["serial"] not in stale), None)

    def spawned_by(sp):
        return [r for r in s.rows.values() if sp and r.get("spawner") == sp["serial"]]

    s.until(lambda: len(spawned_by(spawner_at(HORSES_AT))) == 3 and len(spawned_by(spawner_at(HEALER_AT))) == 1, 20)
    horses_sp, healer_sp = spawner_at(HORSES_AT), spawner_at(HEALER_AT)
    first = {r["serial"] for r in spawned_by(horses_sp)}
    check(horses_sp and healer_sp and len(first) == 3, f"the test spawners are in the god view with their creatures ({len(first)} horses)")
    if not (horses_sp and healer_sp and first):
        return

    # Spawners, with nobody logged in.
    resp = s.ask({"op": "admin_spawner", "serial": horses_sp["serial"], "action": "respawn"})
    s.until(lambda: len(spawned_by(spawner_at(HORSES_AT))) == 3 and not first & {r["serial"] for r in spawned_by(spawner_at(HORSES_AT))}, 15)
    second = {r["serial"] for r in spawned_by(spawner_at(HORSES_AT))}
    check(resp is not None and resp.get("ok") is True and resp.get("before") == 3 and resp.get("spawned") == 3
          and len(second) == 3 and not first & second,
          f"Respawn removed the 3 horses and spawned 3 new ones; the god view shows the new serials "
          f"(reply {resp and resp.get('before')} -> {resp and resp.get('spawned')})")
    healer = spawned_by(healer_sp)[0]
    bad = s.ask({"op": "admin_spawner", "serial": healer["serial"], "action": "clear"})
    unknown = s.ask({"op": "admin_spawner", "serial": 0x3FFFFFF0, "action": "clear"})
    verb = s.ask({"op": "admin_spawner", "serial": horses_sp["serial"], "action": "delete"})
    check(bad and bad.get("error") == "that is not a spawner" and unknown and unknown.get("error") == "nothing in the world has that serial"
          and verb and verb.get("ok") is False and "respawn or clear" in verb.get("error", ""),
          "a spawner action on a creature, an unknown serial or an unknown verb is refused in plain words")
    _with_presence(check, s, horses_sp, healer_sp, spawner_at, spawned_by)

    if with_client:
        # The presence's checks respawned the horses: these are the ones there now.
        _with_character(check, s, cfg, shard_port, out, horses_sp, healer_sp, {r["serial"] for r in spawned_by(spawner_at(HORSES_AT))})

    clear = s.ask({"op": "admin_spawner", "serial": horses_sp["serial"], "action": "clear"})
    s.until(lambda: not spawned_by(spawner_at(HORSES_AT)), 10)
    check(clear is not None and clear.get("ok") is True and clear.get("spawned") == 0 and not spawned_by(spawner_at(HORSES_AT)),
          f"Clear removed what the spawner spawned ({clear and clear.get('before')} -> {clear and clear.get('spawned')}); "
          f"the god view no longer shows them")

    audit = s.ask({"op": "admin_audit", "count": 60})
    entries = (audit or {}).get("entries") or []
    ops = {(e.get("op"), e.get("ok")) for e in entries}
    want = {("admin_spawner", True), ("admin_spawner", False), ("admin_goto", True), ("admin_goto", False),
            ("admin_bring", True), ("admin_paperdoll", True), ("admin_follow", True)}
    if with_client:
        want |= {("admin_bring", False)}
    check(want <= ops, f"every action and refusal is in the audit log ({len(want & ops)}/{len(want)} kinds)")


def near(row: dict | None, at, cells: int = 2) -> bool:
    return bool(row) and max(abs(row["x"] - at[0]), abs(row["y"] - at[1])) <= cells


def _with_presence(check, s: Session, horses_sp: dict, healer_sp: dict, spawner_at, spawned_by) -> None:
    """The hidden presence (AD2c): every character action with nobody logged in."""
    horse = sorted(r["serial"] for r in spawned_by(spawner_at(HORSES_AT)))[0]
    healer = spawned_by(healer_sp)[0]["serial"]

    both = s.ask({"op": "admin_goto", "as": "Guosweep", "hidden": True, "facet": 0, "x": BRITAIN[0], "y": BRITAIN[1]})
    check(both is not None and both.get("ok") is False and "not both" in both.get("error", ""),
          "naming a character and asking for the hidden presence together is refused")

    go = s.ask({"op": "admin_goto", "facet": 0, "x": BRITAIN[0], "y": BRITAIN[1]})
    check(go is not None and go.get("ok") is True and go.get("hidden") is True and go.get("as") is None
          and (go.get("facet"), go.get("x"), go.get("y")) == (0, *BRITAIN),
          f"with nobody logged in, Go there puts the hidden presence's spot at {BRITAIN} on Felucca "
          f"(reply hidden {go and go.get('hidden')}, at {go and (go.get('x'), go.get('y'))})")

    bring = s.ask({"op": "admin_bring", "serial": horse})
    came = s.until(lambda: near(s.rows.get(horse), BRITAIN, 1), 10)
    check(bring is not None and bring.get("ok") is True and bring.get("hidden") is True and came,
          f"Bring here brought a horse from {HORSES_AT} to the presence's spot; the god view shows it there")

    pd = s.ask({"op": "admin_paperdoll", "serial": healer})
    doll = (pd or {}).get("paperdoll") or {}
    items = doll.get("items") or []
    check(pd is not None and pd.get("ok") is True and doll.get("name") and items
          and all(i.get("layer") and i.get("item_id") for i in items),
          f"the paperdoll, with no client to open it in, answers with the healer's: {doll.get('name')!r}, "
          f"{len(items)} items ({', '.join(i.get('layer', '?') for i in items[:6])})")

    fol = s.ask({"op": "admin_follow", "serial": horse})
    away = s.ask({"op": "admin_bring", "serial": horse, "facet": 0, "x": START[0], "y": START[1]})
    moved = s.until(lambda: near(s.rows.get(horse), START, 1), 10)
    # Follow's 500 ms tick moves the spot to the horse; then "here" is beside it.
    time.sleep(1.5)
    after = s.ask({"op": "admin_bring", "serial": healer})
    beside = s.until(lambda: near(s.rows.get(healer), START, 3), 10)
    stop = s.ask({"op": "admin_follow", "stop": True})
    check(fol is not None and fol.get("ok") is True and fol.get("following") is True and fol.get("hidden") is True
          and away is not None and away.get("ok") is True and moved and after is not None and after.get("ok") is True and beside
          and stop is not None and stop.get("following") is False and (stop.get("moves") or 0) >= 1,
          f"Follow moved the presence's spot with the horse, brought away to {START}: the healer brought 'here' after "
          f"arrived beside it; Stop ended it ({stop and stop.get('moves')} follow moves)")

    fol2 = s.ask({"op": "admin_follow", "serial": horse})
    s.ask({"op": "admin_spawner", "serial": horses_sp["serial"], "action": "clear"})
    ended = s.wait(lambda m: m.get("op") == "admin_follow" and m.get("req") is None and m.get("following") is False, 10)
    check(fol2 is not None and fol2.get("ok") is True and ended is not None and "gone" in (ended.get("reason") or ""),
          f"the presence's Follow ends by itself when the horse is deleted: \"{ended and ended.get('reason')}\"")
    # The horses come back for the character's checks: three new ones, once the old ones' removal has arrived.
    old = {r["serial"] for r in spawned_by(spawner_at(HORSES_AT))} | {horse}
    s.ask({"op": "admin_spawner", "serial": horses_sp["serial"], "action": "respawn"})
    s.until(lambda: len(spawned_by(spawner_at(HORSES_AT))) == 3 and not old & {r["serial"] for r in spawned_by(spawner_at(HORSES_AT))}, 15)

    found = s.ask({"op": "admin_godview_find", "text": "0x3FFFFFFE"})
    check(found is not None and found.get("ok") is True and not found.get("matches")
          and not any(r.get("serial") == 0x3FFFFFFE for r in s.rows.values()),
          "the hidden presence is never in the world: Find by its serial finds nothing, the god view never shows it")


def _with_character(check, s: Session, cfg, shard_port: int, out: Path, horses_sp: dict, healer_sp: dict, horses: set) -> None:
    try:
        client = StaffClient(cfg, shard_port, out)
        client.start()
    except RuntimeError as e:
        check(False, f"a staff character logs in for the actions: {e}")
        return
    me_name = client.character
    watch = out / "staff_client_watch"
    try:
        def me():
            return next((r for r in s.rows.values() if r.get("kind") == "player" and r.get("name") == me_name), None)

        s.until(lambda: me() is not None and abs(me()["x"] - START[0]) <= 3 and abs(me()["y"] - START[1]) <= 3, 20)
        row = me() or {}
        check(row.get("online") is True and row.get("staff") in ("GameMaster", "Seer", "Administrator", "Developer", "Owner"),
              f"the staff character is in the god view, online and staff ({row.get('staff')}) near {START}")
        if not row:
            return

        ghost = s.ask({"op": "admin_goto", "as": "Nobody Online Here", "facet": 0, "x": BRITAIN[0], "y": BRITAIN[1]})
        check(ghost is not None and ghost.get("ok") is False and "is not online" in ghost.get("error", ""),
              "an \"as\" naming nobody online is refused")

        go = s.ask({"op": "admin_goto", "as": me_name, "facet": 0, "x": BRITAIN[0], "y": BRITAIN[1]})
        moved = s.until(lambda: me() and (me()["x"], me()["y"]) == BRITAIN, 10)
        dump = _dump(watch, "goto")
        check(go is not None and go.get("ok") is True and (go.get("x"), go.get("y")) == BRITAIN and moved
              and dump and dump.get("player", [0, 0])[:2] == list(BRITAIN),
              f"Go there moved {me_name} to {BRITAIN} on Felucca: the reply, the god view and the client agree "
              f"(client at {dump and dump.get('player')})")

        # "hidden" asks for the presence even with the character online: the character stays where it is.
        hid = s.ask({"op": "admin_goto", "hidden": True, "facet": 0, "x": START[0], "y": START[1]})
        time.sleep(1)
        check(hid is not None and hid.get("ok") is True and hid.get("hidden") is True and me() and (me()["x"], me()["y"]) == BRITAIN,
              f"with {me_name} online, Go there for the hidden presence moves its spot and leaves {me_name} in Britain")

        horse = sorted(horses)[0]
        bring = s.ask({"op": "admin_bring", "as": me_name, "serial": horse})
        came = s.until(lambda: horse in s.rows and max(abs(s.rows[horse]["x"] - BRITAIN[0]), abs(s.rows[horse]["y"] - BRITAIN[1])) <= 1, 10)
        dump = _dump(watch, "bring")
        near = [m for m in (dump or {}).get("mobiles") or [] if m.get("serial") == horse]
        check(bring is not None and bring.get("ok") is True and came and near,
              f"Bring here brought a horse from {HORSES_AT} to {me_name} in Britain; the client sees it beside the character")
        self_bring = s.ask({"op": "admin_bring", "as": me_name, "serial": row["serial"]})
        sp_bring = s.ask({"op": "admin_bring", "as": me_name, "serial": horses_sp["serial"]})
        check(self_bring and self_bring.get("error") == "that is your own character"
              and sp_bring and sp_bring.get("error") == "only a player or an NPC can be brought",
              "Bring here refuses the admin's own character and a spawner")

        healer = next((r for r in s.rows.values() if r.get("spawner") == healer_sp["serial"]), None)
        # A client drops a paperdoll for a mobile it has never been sent, so go to the healer first.
        if healer:
            s.ask({"op": "admin_goto", "as": me_name, "serial": healer["serial"]})
            time.sleep(2)
        _dump(watch, "pd0", "closegumps")
        pd = s.ask({"op": "admin_paperdoll", "as": me_name, "serial": healer["serial"]}) if healer else None
        gumps = []
        for _ in range(10):
            time.sleep(0.5)
            gumps = ((_dump(watch, "paperdoll") or {}).get("gumps")) or []
            if any(g.get("type") == "PaperDollGump" and g.get("serial") == healer["serial"] for g in gumps):
                break
        check(pd is not None and pd.get("ok") is True
              and any(g.get("type") == "PaperDollGump" and g.get("serial") == healer["serial"] for g in gumps),
              f"Open paperdoll opened the healer's paperdoll in {me_name}'s client (gumps open: "
              f"{[g.get('type') for g in gumps]})")

        # Follow: back to the horses' corner first, so Follow has to take the character to the horse in Britain.
        s.ask({"op": "admin_goto", "as": me_name, "facet": 0, "x": START[0], "y": START[1]})
        fol = s.ask({"op": "admin_follow", "as": me_name, "serial": horse})
        joined = s.until(lambda: me() and horse in s.rows and max(abs(me()["x"] - s.rows[horse]["x"]), abs(me()["y"] - s.rows[horse]["y"])) <= 2, 10)
        check(fol is not None and fol.get("ok") is True and fol.get("following") is True and joined,
              f"Follow took {me_name} from {START} to the horse in Britain")
        s.ask({"op": "admin_goto", "as": me_name, "facet": 0, "x": START[0], "y": START[1]})
        # The god view's row may still show the character beside the horse from before the Go there: give
        # Follow's 500 ms tick time to act before reading where it is, so the moves count proves it did.
        time.sleep(1.5)
        back = s.until(lambda: me() and horse in s.rows and max(abs(me()["x"] - s.rows[horse]["x"]), abs(me()["y"] - s.rows[horse]["y"])) <= 2
                       and abs(me()["x"] - START[0]) > 10, 10)
        stop = s.ask({"op": "admin_follow", "stop": True})
        check(back and stop is not None and stop.get("ok") is True and stop.get("following") is False and (stop.get("moves") or 0) >= 1,
              f"sent away with Go there, Follow brought it back to the horse within a second or so; Stop ended it "
              f"({stop and stop.get('moves')} follow moves)")

        fol2 = s.ask({"op": "admin_follow", "as": me_name, "serial": horse})
        s.ask({"op": "admin_spawner", "serial": horses_sp["serial"], "action": "clear"})
        ended = s.wait(lambda m: m.get("op") == "admin_follow" and m.get("req") is None and m.get("following") is False, 10)
        check(fol2 is not None and fol2.get("ok") is True and ended is not None and "gone" in (ended.get("reason") or ""),
              f"Follow ends by itself when the horse is deleted, and says why: \"{ended and ended.get('reason')}\"")
        # The horses come back for the Clear check that follows.
        s.ask({"op": "admin_spawner", "serial": horses_sp["serial"], "action": "respawn"})
    finally:
        client.stop()
