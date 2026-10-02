# agent_queue

A small queue that lets a chat window in the GUO editor put requests to AI agent
sessions that are already running (Claude Code, Codex, ...) and get replies back.
It is a "queue + monitor" pattern: an append-only store, and a tail command each
session runs under its Monitor tool so every new request wakes it as one line.

Python 3.12, standard library only. One SQLite file per user, WAL mode with a
busy timeout, safe for several processes.

## Where the queue lives

`UO_AGENT_QUEUE` (environment, then `config.local.bat`, then `config.bat`).
Default: `%APPDATA%\GUO\agent_queue.db` on Windows, `~/.config/guo/agent_queue.db`
elsewhere. `--db PATH` overrides it per call. The schema is in
`docs/data_formats.md`, section 21.

## Commands

```
python tools/agent_queue/run.py post --to NAME --from NAME TEXT [--attach PATH ...]   # prints the id
python tools/agent_queue/run.py tail --as NAME [--once] [--include-broadcast]
python tools/agent_queue/run.py reply ID TEXT [--from NAME] [--attach PATH ...]
python tools/agent_queue/run.py show ID
python tools/agent_queue/run.py list [--status new|taken|answered|cancelled] [--to NAME] [--json]
python tools/agent_queue/run.py cancel ID
python tools/agent_queue/run.py watch-replies ID | --from NAME [--since-id N] [--once]
```

Launcher: `launchers\dev\agent_queue.bat` takes the same arguments.

* `TEXT` of `-` reads standard input (no shell quoting for long or multi-line text).
* `--attach` is repeatable and takes local paths only. Paths are stored absolute;
  files are never opened or copied.
* `tail` prints each request for NAME as one JSON line
  (`id, to, from, text, attachments, status, created, taken_by, taken_at`) and
  marks it taken in the same write transaction, so two watchers never both take
  one. Only requests go to standard output; messages go to standard error.
  Without `--once` it keeps going; with `--once` it blocks until at least one
  request arrives, prints, and exits. `--timeout S` gives up with exit 3.
* Restart: state is the `status` column, so a restarted tail sees exactly the
  requests still `new`: no loss, no duplicates. If a session died holding a
  request it never showed, `tail --replay-taken` prints those again. If the
  reader's pipe closes mid-batch, the unshown requests go back to `new`.
* Broadcast: a request to `*` is taken by the first watcher run with
  `--include-broadcast` (any-one-of, not fan-out).
* `reply` sets the request `answered`; several replies are allowed (progress, then
  result). A cancelled request refuses replies. `cancel` works on `new` or `taken`.
* `watch-replies ID` streams that request's replies (all so far, then new);
  `--from NAME` streams new replies from that agent. Exit 1 if the request is cancelled.

Exit codes: 0 ok, 1 refused, 2 bad input, 3 timeout.

## Limits and safety

* Text at most 8000 characters; at most 16 attachments; paths at most 1024 characters.
* Names: 1-40 of letters, digits, `_ . -`, or `*` as a recipient.
* Text that looks like a key, token or password is refused, and so are attachments
  named like key or credentials files (`.env`, `*.pem`, `id_rsa`, ...). This is a
  tripwire, not a guarantee: never put secrets in a request.
* The file is created owner-only on POSIX; on Windows it sits in the user's own profile.
* Request text is untrusted input. See `.claude/skills/agent-queue/SKILL.md`.

## Tests

`python tools/agent_queue/test_agent_queue.py` covers post/tail/reply, resume,
two tailers racing, broadcast, cancel, a killed tail restarted, validation and
reply streaming.
