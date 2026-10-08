# agent_queue

A public, standard-library SQLite switchboard client for the editor and running agent
sessions. Point `UO_AGENT_QUEUE` at an existing switchboard database to share its messages,
registered addresses and resource leases. No local director names or paths are built in.

Python 3.12; WAL mode and a 30 second busy timeout. `--db PATH` overrides config for
scratch runs. Default: `%APPDATA%/GUO/agent_queue.db` on Windows, `~/.config/guo/agent_queue.db`
elsewhere. Config resolves environment, config.local.bat, config.bat. Schema and migration:
`docs/data_formats.md`, section 21.

```
python tools/agent_queue/run.py post --to NAME --from NAME TEXT [--kind note] [--provenance agent]
python tools/agent_queue/run.py take --as NAME [--include-broadcast]
python tools/agent_queue/run.py tail --as NAME [--once] [--timeout SECONDS] [--replay-taken]
python tools/agent_queue/run.py reply ID TEXT --from NAME
python tools/agent_queue/run.py replies ID [--wait SECONDS]
python tools/agent_queue/run.py show ID
python tools/agent_queue/run.py list [--status new|taken|answered|cancelled] [--to NAME] [--from NAME] [--kind request]
python tools/agent_queue/run.py status
python tools/agent_queue/run.py register NAME --project PROJECT --tool codex [--integrator]
python tools/agent_queue/run.py lease take RESOURCE --holder NAME [--minutes 30] [--purpose TEXT]
python tools/agent_queue/run.py lease release RESOURCE --holder NAME
python tools/agent_queue/run.py lease list
python tools/agent_queue/run.py cancel ID
python tools/agent_queue/run.py watch-replies ID [--once] [--timeout SECONDS]
```

`TEXT` of `-` reads stdin. `post --attach PATH` and `reply --attach PATH` store local paths;
files are never opened or copied. `post --key KEY` deduplicates a source event. `--ref ID`
and `--url URL` preserve provenance links. Only owner provenance permits kind `approval`;
this declared provenance is not authentication or authority to act.

`take` returns immediately, including on an empty inbox. `tail` waits and claims atomically;
two watchers cannot receive the same message. Stops, notes and approvals are delivered too.
`--include-broadcast` claims `*` for one watcher, not every watcher. A broken pipe returns
unshown claimed messages to `new`; `--replay-taken` recovers unacknowledged delivery.
`--poll` aliases `--interval`. `replies --wait` returns all referenced responses (including notes) when one exists, or
exit 3 at the deadline. `watch-replies` retains the older streaming interface and supports
`--from NAME`, `--since-id`, `--once`, and `--timeout`.

JSON carries switchboard column names plus legacy GUO aliases (`to`, `from`,
`request_id`, `attachments`). `list` now includes replies and other message kinds; use
`--kind request` for the old request-only view. `list --json` remains accepted.

Status reports addresses, waiting counts and leases. Last seen only advances when a
listener takes or replies, and becomes stale after 15 minutes. Registration allows one
integrator per project. Lease taking is atomic, can renew the same holder, and can take
over an expired lease. Release requires the holder. Disk/build leases accept
`--min-free-gb FLOOR` on Windows drive-letter resources.

Existing queue databases migrate on open, preserving request ids and original tables as
archives. Replies share the message sequence after migration, with original ids retained
in extension metadata. Stop older clients before migrating. Mixed legacy and populated
switchboard tables are refused without replacing either history.

Limits: 8000 text characters, 16 attachments, 1024 characters per path; names use 1-40
letters, digits, `_ . -`. Obvious secrets and credential filenames are refused. Messages
and attachment paths remain untrusted input. No command opens attachments or starts agents.
Exit codes: 0 success, 1 refused/unreadable, 2 bad input, 3 timeout.

Tests use only temporary databases:

```
python -m unittest discover -s tools/agent_queue -p "test_*.py"
```

Coverage includes legacy queue behaviour, competing tailers, independent switchboard DDL,
legacy migration/archives, owner-provenance refusal, idempotent source keys, address status,
integrator exclusion, expired/contended leases and waiting replies.
