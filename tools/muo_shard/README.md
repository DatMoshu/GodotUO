# muo_shard

Install and run a ModernUO shard on a Linux host under systemd, from a **profile**
([`docs/data_formats.md`](../../docs/data_formats.md) section 35). It never changes a host itself:
each verb **prints a bash script** that a person reads and then runs on the host.

```
python tools/muo_shard/run.py validate --profile tools/muo_shard/profiles/guo-dev.profile.json
python tools/muo_shard/run.py plan bootstrap --profile P > bootstrap.sh
python tools/muo_shard/run.py plan deploy    --profile P > deploy.sh   [--pin <40-hex>]
python tools/muo_shard/run.py plan status    --profile P > status.sh
python tools/muo_shard/run.py plan admin     --profile P > admin.sh
python tools/muo_shard/run.py plan backup    --profile P [--name N] > backup.sh
python tools/muo_shard/run.py plan restore NAME --profile P > restore.sh
python tools/muo_shard/run.py plan reset     --profile P > reset.sh
python tools/muo_shard/run.py secrets --host [user@]HOST --profile P
ssh HOST 'sudo bash -s' < bootstrap.sh
```

| File | What it is |
|---|---|
| `run.py` | the command line |
| `shardprofile.py` | loads a profile, checks it against the schema, then the rules a schema cannot state |
| `plans.py` | one function per verb, each returning a script |
| `hostsecrets.py` | `secrets`: prompts here, writes the host's env file over ssh stdin |
| `schema/profile.schema.json` | the profile's JSON Schema (draft 2020-12); unknown fields are refused |
| `profiles/guo-dev.profile.json` | GUO's dev shard |
| `test_muo_shard.py`, `tests/` | pytest; `tests/golden/` holds the expected scripts, `tests/fixtures/` a profile that uses every field |

## What the scripts do

- **bootstrap** (root): installs only the packages that are missing (`dotnet-sdk-<channel>`, git, zstd,
  libdeflate-dev, libargon2-1, python3, iproute2, and the profile's `host_packages`), creates the service user (no login shell),
  `/srv/muo/<id>/{src,dist,uodata}`, the backup root, and an empty `/etc/muo/<id>.env` (0600, never overwritten).
  A second run changes nothing.
- **deploy** (root): reads `/etc/muo/<id>.env`, stops the unit, checks out the pinned commit (patches off before the
  pin moves, on after; a patch counts as already applied only when it reverses cleanly, and a patch that neither
  applies nor reverses stops the deploy with git's error), builds (`publish.sh release linux <arch>`, or the profile's `server.build`), copies the
  result into `dist/`, builds `server.assemblies`, writes `Configuration/` (the template in
  `tools/modernuo/config/modernuo.template.json`, then the profile's `config_overlay` over it, `{{KEY}}` filled from the
  env file on the host only, then `dataDirectories` and `client_version`), writes `dist/muo-run.sh` (maps
  `MUO_ADMIN_USER/PASSWORD` to the names `tools/modernuo/patches/0001` reads), the `data_manifest` check, runs
  the `content` folders, and installs, enables and restarts `muo-<id>.service`. `Saves/` and `Logs/` are never touched.
  The patches, template, overlay and manifest are embedded in the script, so the host needs no copy of the repo.
- **admin** (root): checks the owner values are in the env file, restarts the shard and waits for the port. The shard
  itself creates the owner account (or raises an existing one) from those values at every start; the account is
  written to `Saves/` at the next autosave (every 5 minutes; ModernUO does not save when stopped). Prints no password.
- **backup** (root): installs `/srv/muo/<id>/muo-backup.sh`, installs and enables the profile's backup timer when
  `backup.on_calendar` is set (systemd hosts), then runs the helper once (`--name N` names the snapshot; default the UTC
  time `yyyymmdd_hhmmss`). The helper leaves the shard running (ModernUO does not save when it is stopped, so stopping
  it would only discard what it has not saved): it tars the `backup.include` folders (default `Saves`) to
  `<backup.root>/<name>.tar.zst`, retrying up to three times if ModernUO swapped or wrote the folder meanwhile, then
  prunes to `backup.keep`. Only timestamp-named snapshots are pruned, so a `--name seed` snapshot is never lost. A name
  already taken is refused. A snapshot holds what the shard last saved (autosave runs every 5 minutes), not what is in memory.
- **restore NAME** (root): stops the shard (refuses if a shard that systemd does not own is listening), moves each included
  folder to `<folder>.pre-restore` (one generation, the previous one is removed), extracts the snapshot, hands it to the
  service user and starts the shard if it was running.
- **reset** (root): `restore` of the profile's `seed` snapshot (refused when the profile has none). Make the seed once
  with `plan backup --name <seed>` on a shard in the state you want to return to.
- **secrets** (local, not a plan): asks here for `MUO_CLIENT_DATA`, `MUO_ADMIN_USER` and `MUO_ADMIN_PASSWORD` (the
  password hidden, asked twice) and runs `ssh -- HOST sudo python3 -c <merge> /etc/muo/<id>.env`, the values on ssh's stdin.
  The command line holds no value and no local file is written. A blank answer keeps that key; other keys in the file are
  kept; the file stays root 0600. Needs a terminal and passwordless sudo on the host (stdin is the data). A value may not
  hold `'` or a line break.
- **status**: read-only. `systemctl status` and whether the port is listening (exit 1 if not).

Things to know:

- The overlay folder may hold `modernuo.template.json`; it is the tool's own template and is never copied over the shard.
- `content` folders each hold `deploy.args` (one `tools/shard_content/run.py deploy` argument per line). That tool
  needs the repo, so the host env file also needs `MUO_REPO`, a checkout of the profile's repo. Profiles without
  `content` (GUO's) need nothing.
- The pin is the profile's `server.source.ref` (or the submodule's gitlink). GUO's equals `UO_SHARD_REF` in
  `launchers/_shared/config.bat`; a test fails when they drift, and `plan deploy` says so on stderr.
- Ubuntu's archive SDK can trail the version the pin's `global.json` names (26.04 had 10.0.112 against 10.0.201), so
  deploy rewrites that one line to the installed SDK (and warns on stderr, naming both versions) and marks the file `skip-worktree`; the next deploy restores it
  before it moves the pin.
- The unit's working directory is `dist/`; write repo-relative commands in `exec_start_pre` as `{src}/...`. `validate`
  refuses an argument that looks like a repo-relative script (`tools/x/run.py`, `x.sh`) and names the fix.
- `validate` warns (and still exits 0) when `server.patches` lacks `0001-headless-owner-account`: without it a headless
  shard has no owner, so `plan admin` has nobody to administer with.
- The base packages include `libargon2-dev` (ModernUO's password hashing links libargon2).
- The backup timer runs `muo-backup.sh`; the shard keeps running while it does.
- A public port wants auto account creation off: `profiles/guo-vps.profile.json` does that with its own overlay folder,
  `tools/modernuo/config-vps/` (the template's `modernuo.json` with that one setting changed, the port fixed at 2593 and
  the name `GUO`; a test fails if it drifts from the template in any other way). Same pin and patches as `guo-dev`,
  `MemoryMax=4G`, a daily backup timer. With auto creation off the owner makes every other account in game: the
  `shard.admin_add_account` scenario drives the Admin gump (`--no-record`: the gump's layout changes at the movie's
  1280x720), and `login_probe.py` says whether a login is accepted without a client. The generated `muo-run.sh` always
  unsets `UO_SHARD_GM_ACCOUNTS` (patch 0001's dev-only list of game master accounts), so no env file can turn it on. The ordered host steps live in a local `build/muo/guo-vps/COMMANDS.md`, not in git.

## Container dry run

Proves a fresh `bootstrap` + `deploy` listens on 2593, with nothing on a real host. Needs Docker, 10 GB free, and
the `build:D` switchboard lease. The UO install is mounted **read-only** from `UO_CLIENT_DATA`; it is never copied
into an image or a repo.

```
python tools/muo_shard/run.py plan bootstrap --profile tools/muo_shard/profiles/guo-dev.profile.json > build/muo/bootstrap.sh
python tools/muo_shard/run.py plan deploy    --profile tools/muo_shard/profiles/guo-dev.profile.json > build/muo/deploy.sh
docker run -d --name muo-dry -v "%UO_CLIENT_DATA%:/mnt/uodata:ro" -v "%CD%\build\muo:/plans:ro" --security-opt seccomp=unconfined ubuntu:26.04 sleep infinity
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

### Running the client against the container

Every `docker run` here needs `--security-opt seccomp=unconfined` (ModernUO aborts on io_uring under Docker's default profile). The server list
sends the client to the shard's own address and listening port, so publish the same port (`-p 2593:2593`) and, in the
container's own `Configuration/modernuo.json` (not the profile), set `"serverListing.address": "127.0.0.1"`, then start the
shard. A fresh owner account has no character: make one once with the client (`launchers\game\play.bat --play --account A
--password P --shard-command "[Where"`). Then, with `GUO_SHARD_CONTAINER_HOST=127.0.0.1`, `GUO_SHARD_CONTAINER_PORT=2593`
and `GUO_SCENARIO_ACCOUNT` / `GUO_SCENARIO_PASSWORD` set to the env file's throwaway owner values:

```
python tools/scenario_run/run.py shard.console --shard container --no-register
```

## Tests

```
python -m pytest tools/muo_shard -q
MUO_UPDATE_GOLDEN=1 python -m pytest tools/muo_shard -q     # after a deliberate change to a script
```

Goldens are bytes with LF endings; `tools/muo_shard/.gitattributes` keeps them so on Windows.
