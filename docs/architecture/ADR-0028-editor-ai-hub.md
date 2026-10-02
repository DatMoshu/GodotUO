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

To do:
- Queue tests (post, tail, reply, offset resume, two watchers racing).
- ACP framing against a fake agent.
- A smoke check against a stub Ollama server.
- One real round trip with a Claude session watching the queue.
