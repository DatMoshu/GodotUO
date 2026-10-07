# muo_shard

Install and run a ModernUO shard on a Linux host under systemd, from a **profile**
([`docs/data_formats.md`](../../docs/data_formats.md) section 35). It never changes a host itself:
each verb **prints a bash script** that a person reads and then runs on the host.

```
python tools/muo_shard/run.py validate --profile tools/muo_shard/profiles/guo-dev.profile.json
python tools/muo_shard/run.py plan bootstrap --profile P > bootstrap.sh
python tools/muo_shard/run.py plan deploy    --profile P > deploy.sh   [--pin <40-hex>]
python tools/muo_shard/run.py plan status    --profile P > status.sh
ssh HOST 'sudo bash -s' < bootstrap.sh
```

M1 ships `validate` and `plan bootstrap|deploy|status`. `admin`, `backup`, `restore`, `reset` and `secrets` are M2.

| File | What it is |
|---|---|
| `run.py` | the command line |
| `shardprofile.py` | loads a profile, checks it against the schema, then the rules a schema cannot state |
| `plans.py` | one function per verb, each returning a script |
| `schema/profile.schema.json` | the profile's JSON Schema (draft 2020-12); unknown fields are refused |
| `profiles/guo-dev.profile.json` | GUO's dev shard |
| `test_muo_shard.py`, `tests/` | pytest; `tests/golden/` holds the expected scripts, `tests/fixtures/` a profile that uses every field |

## What the scripts do

- **bootstrap** (root): installs only the packages that are missing (`dotnet-sdk-<channel>`, git, zstd,
  libdeflate-dev, libargon2-1, python3, iproute2, and the profile's `host_packages`), creates the service user (no login shell),
  `/srv/muo/<id>/{src,dist,uodata}`, the backup root, and an empty `/etc/muo/<id>.env` (0600, never overwritten).
  A second run changes nothing.
- **deploy** (root): reads `/etc/muo/<id>.env`, stops the unit, checks out the pinned commit (patches off before the
  pin moves, on after), builds (`publish.sh release linux <arch>`, or the profile's `server.build`), copies the
  result into `dist/`, builds `server.assemblies`, writes `Configuration/` (the template in
  `tools/modernuo/config/modernuo.template.json`, then the profile's `config_overlay` over it, `{{KEY}}` filled from the
  env file on the host only, then `dataDirectories` and `client_version`), writes `dist/muo-run.sh` (maps
  `MUO_ADMIN_USER/PASSWORD` to the names `tools/modernuo/patches/0001` reads), the `data_manifest` check, runs
  the `content` folders, and installs, enables and restarts `muo-<id>.service`. `Saves/` and `Logs/` are never touched.
  The patches, template, overlay and manifest are embedded in the script, so the host needs no copy of the repo.
- **status**: read-only. `systemctl status` and whether the port is listening (exit 1 if not).

Things to know:

- The overlay folder may hold `modernuo.template.json`; it is the tool's own template and is never copied over the shard.
- `content` folders each hold `deploy.args` (one `tools/shard_content/run.py deploy` argument per line). That tool
  needs the repo, so the host env file also needs `MUO_REPO`, a checkout of the profile's repo. Profiles without
  `content` (GUO's) need nothing.
- The pin is the profile's `server.source.ref` (or the submodule's gitlink). GUO's equals `UO_SHARD_REF` in
  `launchers/_shared/config.bat`; a test fails when they drift, and `plan deploy` says so on stderr.
- Ubuntu's archive SDK can trail the version the pin's `global.json` names (26.04 had 10.0.112 against 10.0.201), so
  deploy rewrites that one line to the installed SDK and marks the file `skip-worktree`; the next deploy restores it
  before it moves the pin.
- The unit's working directory is `dist/`; write repo-relative commands in `exec_start_pre` as `{src}/...`.
- The backup timer (`backup.on_calendar`) is installed by M2, with the verbs it runs.
- Open question for the owner, not a tool setting: the shared template has `accountHandler.enableAutoAccountCreation`
  on. A public port wants it off; put that in the profile's overlay when decided.

## Container dry run

Proves a fresh `bootstrap` + `deploy` listens on 2593, with nothing on a real host. Needs Docker, 10 GB free, and
the `build:D` switchboard lease. The UO install is mounted **read-only** from `UO_CLIENT_DATA`; it is never copied
into an image or a repo.

```
python tools/muo_shard/run.py plan bootstrap --profile tools/muo_shard/profiles/guo-dev.profile.json > build/muo/bootstrap.sh
python tools/muo_shard/run.py plan deploy    --profile tools/muo_shard/profiles/guo-dev.profile.json > build/muo/deploy.sh
docker run -d --name muo-dry -v "%UO_CLIENT_DATA%:/mnt/uodata:ro" -v "%CD%\build\muo:/plans:ro" ubuntu:26.04 sleep infinity
docker exec muo-dry bash /plans/bootstrap.sh
docker exec muo-dry bash -c "printf 'MUO_CLIENT_DATA=/mnt/uodata\nMUO_ADMIN_USER=dryadmin\nMUO_ADMIN_PASSWORD=dry-run-only\n' > /etc/muo/guo-dev.env"
docker exec muo-dry bash /plans/deploy.sh
docker exec -d muo-dry bash -c "set -a; . /etc/muo/guo-dev.env; set +a; runuser -u muo-guo-dev -- /srv/muo/guo-dev/dist/muo-run.sh"
docker exec muo-dry bash -c "sleep 20; ss -ltn"            # expect 0.0.0.0:2593 LISTEN
docker exec muo-dry bash /plans/bootstrap.sh               # again: prints only its last line
docker rm -f muo-dry
```

The container has no systemd, so `deploy` ends by printing how to start the shard by hand, as above. The env file in
the dry run holds a made-up password; nothing in it is kept.

## Tests

```
python -m pytest tools/muo_shard -q
MUO_UPDATE_GOLDEN=1 python -m pytest tools/muo_shard -q     # after a deliberate change to a script
```

Goldens are bytes with LF endings; `tools/muo_shard/.gitattributes` keeps them so on Windows.
