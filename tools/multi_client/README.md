# tools/multi_client

Runs four scripted clients at once on one shard, each in its own quarter of the
screen, and writes one summary: `session` (the ordinary playtest, with its own
trade partner), `effects` (frame cost of a crowd of blended effects),
`highlight` (the mesh highlight check) and `sweep` (a Britain street photo).
The session lane plays the owner account; the others use
`UO_SHARD_GM_ACCOUNTS`, which the shard creates as game masters. Needs a
shard. Sound is off unless `--sound` is passed.

## Run

```
launchers\dev\multi_client.bat                 the default 2x2
launchers\dev\multi_client.bat --only session,sweep
launchers\dev\multi_client.bat --list
```

## Tests

No automated tests: the run itself is the check, and its exit code says whether it passed. Exit 0 only when every lane passed.
