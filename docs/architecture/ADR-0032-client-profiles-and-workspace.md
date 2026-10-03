# ADR-0032: Client profiles and the per-user workspace

**Status:** Accepted
**Date:** 2026-10-03
**Decision makers:** the owner (asked to manage multiple clients as well as multiple servers, switch between
them, and organise the server and client folders), the GUO director

## Summary

A **client profile** names everything needed to run one kind of client: the program, the base UO data it
reads, the overlay a shard layers over that data, the client version and encryption, plugins and
arguments. Server profiles point at a default client instead of carrying private client paths. Both live in a
per-user **workspace** that every checkout and worktree shares. The editor run bar and the game's
pregame Servers tab read the same registry (`src/Workspace`, with no Godot or editor dependency).

## Context

- `ServerProfile` embedded `ClientProject` and `ClientData`, so two servers that use the same client
  repeated its paths, and a shard that wants its own ClassicUO fork or an OSI client could not be
  expressed.
- The pregame Servers tab kept a private `data_folder` per server entry (ADR-0021's custom slot).
- Profiles, server installs and run state lived under one checkout's `build/`, so a worktree started
  from empty.
- Client data cannot be swapped in memory: the loaders open files once at startup. A different client
  needs a restart (ADR-0021 already says so).

## Decision

1. **Kinds.** `guo-project` (a Godot project folder; an empty `program` means the project hosting the
   registry, today's behaviour), `guo-build` (an exported GUO executable) and `external` (a shard's own
   ClassicUO fork, an OSI client or a launcher). GUO only *starts* an `external` client, with its
   arguments and working directory, and injects nothing: no environment, no settings, no plugins.
   `external` and `guo-build` are desktop only; Android and the web build ignore them (the registry
   returns no client files for such a profile).
2. **The workspace.** `UO_WORKSPACE_DIR`, resolved env var, then `config.local.bat`, then `config.bat`,
   then a default in code: `%LOCALAPPDATA%\GUO` on Windows, `$XDG_DATA_HOME/guo` or
   `~/.local/share/guo` elsewhere; `user://workspace` on Android and web. The layout is in
   `docs/data_formats.md` section 30. `config.bat` sets no default (no machine path).
3. **UO installs are never copied or written.** A client profile's `base_data` points at the install in
   place. A shard's custom files are an overlay folder with a `guo_data.json` (ADR-0021's custom slot, the
   same verified runtime overlay mechanism the store uses, ADR-0020). GUO reads it, never writes into the
   install. The workspace holds the overlay folder next to the profile (`clients/<id>/overlay/`) unless the
   profile names another.
4. **Shared registry.** `GUO.Workspace.ClientRegistry` owns `clients.json` and the per-client
   `client.json`. The editor run bar and the Manage window use it under `#if TOOLS`; the pregame's
   `ServerBook` entries keep a `client_id`, and ask the registry for the files, so "needs its own client
   files" means "uses client X". The restart rule is unchanged (`ShardSession`; Play restarts GUO with
   the profile's files). `ServerEntry.DataFolder` stays as a computed property so callers and the probe
   keep working; a legacy `data_folder` in `servers.json` is read once and migrated.
5. **Server profiles** gain `DefaultClient` and an optional `ExpectedClientVersion`, and lose
   `ClientProject` and `ClientData`. Validation stays strict: absolute paths, count and length limits,
   and unknown fields are refused.
6. **Migration, once and automatic.** If `build/editor_servers/profiles.json` exists, each profile's
   `ClientProject` and `ClientData` pair becomes a client profile (identical pairs share one), the
   servers merge into `profiles/servers.json` by id, and the old file is renamed `profiles.json.migrated`.
   Existing server installs are not moved. New installs are suggested under `servers/<id>/`.
7. **Runs are keyed by server and client**: `runs/<server-id>/<client-id>/slot-1..4/` hold the client's
   settings, account cache (`cache/`), `process.json` and `client.log`. Two clients on one server, or one
   client on two servers, never share accounts or settings.
8. **Warnings, not blocks.** Choosing a client whose version differs from the server's
   `ExpectedClientVersion`, or whose recorded base-data fingerprint no longer matches the folder, shows a
   warning in the run bar. It does not stop the launch.
9. **Process safety.** An `external` or `guo-build` client is tracked like a managed server: PID, start
   time and executable, stopped only when all three match. The tracker is shared
   (`ManagedServerProcess.Start(ProcessStartInfo, state)`).

## Consequences

- Servers and clients are organised separately. Picking a server picks its default client and the person
  may override it.
- Several worktrees see the same profiles; a server removed in one disappears in all.
- The registry code is plain .NET, so `tools/server_manager/tests` compiles it directly.
- An `external` program runs with the person's own rights. The Manage window says what will run.

## Alternatives rejected

- *Keep client paths in the server profile.* No reuse, no external clients, no organisation.
- *Switch client data in process.* The loaders cannot be reopened (ADR-0021).
- *One workspace per checkout.* Each worktree would need setup; the owner asked for shared.
