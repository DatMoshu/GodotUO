# ADR-0028: The editor's AI hub: chat, agents over ACP, a request queue

## Status

Accepted — 2026-10-02 (the owner approved the plan).

## Date

2026-10-02

## Decision Makers

The owner, relayed by the director (GUO-Director).

## Summary

The GUO editor gets an **AI** dock with three parts:

1. **Chat** with local models (Ollama) and any OpenAI-compatible endpoint.
2. **Agents**: coding agents (OpenCode, Codex, Claude Code, Gemini CLI)
   started from the editor over the **Agent Client Protocol** (ACP).
3. **Queue**: a request queue that agent sessions already running elsewhere
   watch and answer.

GUO never holds an AI subscription's tokens: each agent's own CLI logs in
with the user's own plan.

## Context

- The owner works with several agents at once (Claude Code sessions, Codex,
  Cursor) and wants one place in the editor to talk to them, and to local
  models.
- On this machine, a Discord router already passes requests to running
  sessions: an append-only queue, plus a tail command with a remembered
  offset that a session runs under its Monitor.
- In September 2026 OpenAI launched "Sign in with ChatGPT" plan usage in
  partner tools. OpenCode is one of those tools, and Codex logs in with a
  ChatGPT plan.
- ACP (JSON-RPC over stdio) is spoken by OpenCode, Codex (through an
  adapter), Claude Code (through an adapter) and Gemini CLI.

## Decision

1. **Chat providers** share one streaming interface:
   - Ollama on loopback, which lists its models, and its vision models
     receive the selected asset or a world frame.
   - Any OpenAI-compatible endpoint the user adds.
   - Agents over ACP.

   Editor tools exposed to a model are read-only (search, inspect, jump to)
   unless the user approves each action.
2. **Agents run as their own CLIs over ACP.**
   - GUO starts the CLI, shows its transcript, and turns its permission
     requests into editor dialogs.
   - The CLI's own login pays: a ChatGPT plan through Codex or OpenCode, a
     Claude plan through Claude Code, a Google account through Gemini CLI.
   - GUO stores no tokens for them.
3. **The request queue** is SQLite in the user's configuration folder
   (`UO_AGENT_QUEUE`), driven by `tools/agent_queue/run.py`:
   - `post` adds a request; `tail --as NAME` takes requests atomically and
     resumes after a restart; `reply` answers one.
   - A running session watches the queue under its Monitor and answers
     through `reply`. The chat dock shows replies as they arrive.
   - Request text is input from the owner's own editor. Sessions still
     confirm destructive actions with the owner.
4. **Keys for third-party services** are stored in the operating system's
   store (DPAPI on Windows, as the pre-game accounts already are). They are
   never kept in a project, a setting file or the repository.
5. **The session overview** is read-only at first:
   - It lists local Claude Code and Codex sessions and Cursor projects from
     their own state folders, with their last activity.
   - From there the owner can open a session's transcript, or send a request
     to it through the queue.

## Consequences

- No AI SDK enters the client or the editor build. Providers are plain HTTP
  or stdio JSON-RPC.
- The queue works without the editor too (command line, other tools).
- Each agent CLI's version and installation is the user's to manage. The dock
  detects what is on PATH and says what is missing.

## Validation

Phase 2 (the dock) is built; `python tools/editor_smoke/run.py` has an AI stage that checks, with no
paid service and no real model:

- ACP framing against `tools/ai_hub/fake_acp_agent.py`: initialize, session/new, a prompt whose
  chunks stream in, a permission request answered, a silent agent timing out, its process killed.
- The Ollama provider and an OpenAI-compatible provider against stub HTTP servers (models, streamed
  answer, thinking text, bearer key, idle timeout); an endpoint key stored sealed, not as text.
- Queue post, list, refuse a secret, reply seen, in a temporary database; the same through the Queue tab.
- The dock: detection of the presets, an agent started from the Agents tab, its permission dialog
  answered, a chat turn through it and through stub Ollama, every child process dead after Shutdown.

Also checked by hand: a short chat with the local Ollama (qwen3:8b); a Gemini CLI 0.11.3 ACP handshake
(initialize works, session/new answers "Authentication required", so Gemini needs its own sign-in first).

Added in the follow-up (the same AI stage, 69 checks in all):

- Sessions tab: a fake home folder (Claude jsonl, Codex sessions and index, Cursor project, plus `auth.json`,
  `.credentials.json`, `config.toml` and `.env` holding a marker string). The scanner's open log shows no
  credential file opened, the marker is not on screen, a first user line beyond the 32 KB cap is not read,
  "Open transcript" hands the right file to the viewer hook, "Send to queue" posts to the session's name.
- Services tab: add, test, remove against stubs for OpenAI-compatible, ComfyUI (`/system_stats`), Ollama
  (`/api/tags`) and Retro Diffusion (`/v1/inferences/credits`, key as `X-RD-Token`); keys are not in the files as text;
  a dead server fails its test. Test never generates anything.
- Editor tools: the stub OpenAI-compatible server answers with a streamed function call (`search`), the editor runs it,
  and the stub sees the `tool` message in the next request; `inspect_asset` runs; an unknown tool and a changing tool
  with no approval are refused.
- Vision: the attached picture reaches the stub as an `image_url` data URI (OpenAI-compatible) and as `images[]`
  (Ollama); a remote endpoint with "Allow client art" off refuses and keeps the attachment; local Ollama needs no
  leave; the chat shows a one-line client-art notice.

Not done: attaching the current World frame (the headless editor cannot capture it; only the selected asset
picture is attached), `jump_world` is covered by construction but not exercised by the smoke, the Retro Diffusion
credits endpoint is taken from its docs and was not called live, and the Art/ providers (ComfyUI, Retro Diffusion)
that will read `ServiceBook` are another change.

Still to do:
- Queue tests beyond tools/agent_queue's own (two watchers racing from the dock).
- One real round trip with a Claude session watching the queue.
- A real agent turn through OpenCode or Codex with a ChatGPT sign-in (not run: neither is installed here).
- A real tool-calling model (the stubs only prove the wire format) and a real vision model.

## Addendum: World tools (2026-10-04)

The chat tool host adds `world_state`, `describe_cell` and `walkable` as
read-only calls over the active World facet. Land and statics use the same
`WorldData` and `WorldEditor.StaticsAt` paths as Nearby tiles; walkability
uses `WorldData.WalkAt`, including its `surface` category for floors, bridges
and steps. These calls do not move the camera; use `jump_world` for another facet.

`stamp_static` is registered with `ReadOnly=false`, so the existing approval
dialog asks once per call and no approver means refusal. It writes at land z
through `WorldEditor.Stamp` into an open world project and shares its undo.
The tool refuses invalid cells/ids, absent projects and projects inside the
client installation. Cancelled or timed-out queued calls cannot execute later.
The headless AI smoke checks reads against Britain cells, refusal, approved
stamping and undo, and untouched install timestamps; it uses no real model.
