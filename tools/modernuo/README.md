# ModernUO — the local dev shard

The client needs a server to talk to. This is it: a local
[ModernUO](https://github.com/modernuo/ModernUO) shard, running against the
same UO install the client reads, listening on `127.0.0.1:2593` as **GUO Dev**.

It is a development dependency, not part of the port. Nothing in
`godot/GUO/` knows it exists; the client connects to `UO_SHARD_HOST` /
`UO_SHARD_PORT` and does not care what answers.

| | |
|---|---|
| **Upstream** | <https://github.com/modernuo/ModernUO.git> |
| **Licence** | GPL-2.0 (RunUO lineage). Not vendored, not redistributed. |
| **Pinned at** | `24bcfee55` — `0.15.6.178-12-g24bcfee55`, fetched 2026-09-21 |
| **Runtime** | .NET 10 SDK (ModernUO's own requirement, not the client's) |
| **Checkout** | `tools/modernuo/src/` — **gitignored**, ~1 GB with the build |

## Use it

```
launchers\shard\fetch.bat     clone + apply the patches   (once)
launchers\shard\build.bat     publish release win x64     (once, ~2 min)
launchers\shard\run.bat       run it                      (Ctrl-C to stop)
```

Then, in another terminal, `launchers\game\play.bat`.

Auto account creation is on, so the first login with any name and password
makes that account. The input probe
(`launchers\dev\screenshot.bat --play --input-probe --shot-after N`) uses
`guoprobe` / `guoprobe`.

## What is tracked here, and why

The checkout is gitignored. What is committed is everything needed to
reproduce it:

```
patches/    the diffs applied on top of upstream, numbered, in order
config/     the server configuration, as templates
configure.py  fills the templates in from config.bat
```

### `patches/0001-headless-skip-owner-account-prompt.patch`

`AccountPrompt.Initialize()` insists on an owner account at first boot and
asks for it at the console. `Core.Headless` is true whenever stdin is
redirected, which is every scripted run, and the prompt throws there instead
of asking. The patch skips the prompt when headless: auto account creation is
on, so the first client login makes the account and no owner is needed to
boot.

Keep this list append-only and numbered. A patch that upstream adopts should
be deleted, not silently dropped from the set.

### `config/`

`modernuo.template.json` is the full server configuration with three values
left as placeholders — `@UO_CLIENT_DATA@`, `@UO_SHARD_NAME@`,
`@UO_SHARD_PORT@` — and `expansion.json` pins the expansion to **Endless
Journey** (Id 11), which is what a 7.0.x client expects.

`configure.py` writes both into `src/Distribution/Configuration/` on first run,
resolving the placeholders through `tools/guo/config.py` — the same
environment → `config.bat` → shared-config order everything else here uses.

It writes only files that do not exist. Edit the generated file to change a
setting on this machine; edit the template to change it for everyone, and say
so in the commit.

Without this, ModernUO asks for its data directory and expansion at a console
prompt on first boot, and refuses to prompt when stdin is redirected:

```
HeadlessConsoleInputException: Interactive console input required but the
server is headless (stdin is not a TTY)
```

## Gotchas

**Clone it fully.** ModernUO versions itself with Nerdbank.GitVersioning,
which walks the history. A shallow clone fails the build with *"Shallow clone
lacks the objects required to calculate version height"*; `git fetch
--unshallow` fixes an existing one.

**The world is saved.** `src/Distribution/Saves/` and `Backups/` hold the
shard's state, including the characters the probe makes. Delete `Saves/` for a
clean world; that is also how to get rid of a character whose name the probe
has taken.
