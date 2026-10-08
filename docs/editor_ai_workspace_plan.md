# GUO AI workspace and editor MCP

Status: proposed interface; initial editor MCP implemented in this checkout.
Date: 2026-10-03. Owner request: manage Codex, Claude Code and Gemini conversations inside GUO, with a left icon rail, session sidebar and chat; expose Godot and GUO editor capabilities to those agents.

## Product outcome

AI becomes a main-screen workspace alongside World, Assets and Multis. A person can start a conversation, pick its provider, return to supported existing conversations, send follow-ups, review changes, and have an agent use the actual editor. A small dock remains available when World or Multis is the active screen. The workspace and dock use the same conversation/controller state.

The visual concept and interactive wireframes accompany this plan. They illustrate the intended interface, not live account entitlements or completed UI. The concept generator supplied illustrative model labels and a shard port; those are not configuration defaults. Implementation must use discovered models and the configured private shard, never the shared shard on port 2593.

## What exists now

| Area | Current implementation | Gap |
|---|---|---|
| AI UI | AiDock with Chat, Agents, Queue, Sessions and Services tabs | No main-screen session workspace |
| ACP | CLI process, initialize, session/new, prompt, streamed updates, permissions, cancel | No GUO UI for authenticate, load/resume, session config/model selection |
| Sessions | Own running AgentSession objects; local Claude/Codex/Cursor history scanner | Discovered history is not a controllable process; no Gemini history scanner |
| Messaging | Direct prompt to a selected GUO-created agent; queue posts for external watchers | Queue does not inject a prompt into an arbitrary live CLI |
| Model picker | HTTP provider model lists; disabled for ACP | Read adapter configuration, expose only advertised choices |
| Usage | Service tests and some endpoint credit/status checks | No normalized account quota reporting across ACP adapters |
| Composer | Chat uses wrapping TextEdit; Agents and Queue retain LineEdit | Windowed clipboard/IME certification, other composers, persistent session drafts |
| F3 | Live Godot menus/settings plus GUO, world, assets, AI, store and logs providers | Action callbacks lack typed results, edit effects and explicit arguments |
| Editor MCP | New authenticated loopback endpoint, stdio bridge, shared AI tools, ACP server injection | See coverage and remaining domain tools below |
| Runtime MCP | Existing tools/guo_mcp and GUO.Automation.McpHost | Keep separate: runtime client state is not editor state |

## Sign in and connect

GUO must not collect provider passwords, copy OAuth tokens, or read provider credential files. Installation and first sign-in happen in each vendor CLI. The UI shows detection, sign-in instructions, and connection diagnostics. An installed executable is not proof of authentication; a successful session/inference is the meaningful check.

Claude Code: install the vendor CLI using its supported Windows installer if `claude` is missing. Install the ACP adapter with `npm install -g @agentclientprotocol/claude-agent-acp`. Start `claude` and use `/login` to select the subscribed Claude account. In GUO, AI > Agents > Claude Code > Start. The CLI's own login is used by the adapter. Confirm the subscription authentication method in the CLI rather than setting an API key. Windows requires the vendor's supported shell prerequisites.

Gemini/Google correction (2026-10-03): Google ended Gemini CLI access for individual Free, AI Pro and Ultra accounts on June 18, 2026. A successful browser OAuth followed by `UNSUPPORTED_CLIENT` is a service eligibility rejection; upgrading the old CLI does not restore consumer subscription access. Gemini CLI remains supported for applicable enterprise and paid API-key paths. The existing GUO Gemini preset launches the legacy client and is not a working consumer-subscription path.

For individual accounts, install Antigravity CLI using Google's Windows instructions (`irm https://antigravity.google/cli/install.ps1 | iex`), reopen PowerShell and run `agy` to complete its supported browser sign-in. This establishes CLI authentication; it does not turn the legacy GUO preset into Antigravity. Google documents Antigravity as an external agent in Zed's registry. The official ACP registry currently lists Google Antigravity 1.2.1 with Windows x86_64/arm64 distributions (`agy_acp_server.exe`). GUO must add and certify that ACP server separately, checking authentication/configuration, negotiated capabilities and editor MCP support. Do not substitute `agy --experimental-acp` without documented support. API-key billing is a separate authentication route from consumer subscriptions.

Codex: retain ChatGPT sign-in through `codex login` and `codex-acp`; account limits follow that login. GUO starting an adapter is independent of the Codex desktop app's open chat.

## Interface and behavior

The entire AI workspace is optional. Editor Settings > GUO > AI > Enabled
(`guo/ai/enabled`) is the saved editor-wide switch, enabled by default to
preserve existing behavior. Turning it off immediately removes the AI dock
and F3 AI commands, stops owned agents/requests and editor MCP, prevents
session discovery and queue operations, and removes image-generation
controls from Art. Manual art exchange, assets, World, Multis and other
authoring tools remain available. Re-enabling restores controls without
resuming prompts or starting inference. The preference persists across
restarts and assembly reloads.

Implementation requirement for this plan and every future AI feature:
use `AiFeatures.Enabled` for registration/visibility, `RequireEnabled` at
execution boundaries, and `AiFeatures.Link` for cancellable work. Workers
use the gate snapshot, never Godot settings APIs. No separate AI integration
may bypass the switch. Disabled-mode smoke skips AI inference stubs while
retaining manual editor coverage; toggle checks prove work is blocked and
normal authoring remains reachable.

Desktop anatomy: 48 px icon rail; resizable conversation sidebar (default 240 px, minimum 180); flexible transcript/composer; optional 280 px context inspector. Keep Godot's theme, fonts and existing GUO branding. Rail icons: Conversations, Agents/accounts, Connections, Activity, Settings. Tooltips and accessible labels explain icon-only actions. No quota gauges unless they convey provider-reported values.

Conversation sidebar: New conversation; provider filter; search; pinned conversations; project groups; active and historical rows. Each row shows title, provider, activity time and lifecycle in text as well as color. Pin/rename/archive are local metadata changes; archiving does not delete vendor history. Clicking a session does not send a message or restart its agent.

New conversation: provider/adapter, working directory, supported model and mode, editor connection. Default to this checkout. Start the session first; inference begins only after Send. Workspace reuse is explicit; avoid spawning a new CLI for every follow-up. Retain a process per conversation initially for isolation; multiplex only adapters that negotiate independent sessions correctly.

Transcript: user/assistant text, visible plan steps, expandable tool calls, diffs, errors and permissions. Render Markdown with escaped markup. Never execute links or text as commands. Preserve message boundaries and distinguish assistant prose from tool output. Show changed files and editor operations with Open in editor, Open in Multis, preview and undo where supported. A tool dispatch acknowledgement is not proof that a build, save or deploy completed.

Composer: TextEdit with wrapping, 3-line minimum and bounded auto-expansion. Ctrl+V preserves CRLF/newlines; Enter sends; Shift+Enter inserts a newline; IME composition prevents premature send. A configurable Ctrl+Enter send mode is optional. Per-session drafts survive navigation and restart. Context chips: @editor, @selection, @scene, @multi, @file. Attach only explicitly chosen content. Proprietary client imagery keeps existing destination consent. Busy sessions show Stop and an explicit queued-follow-up control; a queued message is delivered to that exact session after its turn ends.

GUO-EDT-02 implementation slice (2026-10-04): the existing Chat prompt now uses
a wrapping TextEdit with a 72 px minimum height. Enter/keypad Enter and the
Send button share the existing send route; Shift+Enter inserts a newline.
Composing Enter is left to Godot's IME handling, and key repeats/releases do
not send. LF/CRLF prompt content normalizes to LF while retaining blank lines
and indentation. Busy, empty and AI-disabled sends preserve the draft; incoming
replies do not replace it. Endpoint/URL inputs and Agents/Queue composers remain
unchanged. Auto-expansion, persistent per-session drafts, queued follow-ups and
the main AI workspace remain proposed. Headless smoke checks exercise composer
signals, captured loopback payloads and a simulated composition gate; native
clipboard/IME behavior still needs an agreed windowed focus check. Evidence and
final acceptance are recorded in
`docs/coordination/director/editor-multiline-handoff.md`.

Optional inspector: current scene/selection/multi, editor connection status, proposed/applied edits, model and permission policy, context-window usage, account quota when known. Each usage figure carries source, scope, timestamp and reset time. Session token usage and account remaining quota are separate concepts.

Activity: all GUO-controlled conversations with running/awaiting approval/failed/complete status. Clicking an entry focuses its conversation. No automated prompts or cross-session forwarding without a person's action or an explicitly authorized workflow.

Compact layout: collapse the sidebar to a drawer when chat width would drop below 420 px; hide the optional inspector first. Floating/dock layouts reuse the same views. Keyboard focus returns to the selected conversation's draft after navigation. State indicators use labels, never color alone.

## Session ownership and persistence

| Session category | Allowed controls | Restrictions |
|---|---|---|
| GUO-created ACP session | Send, cancel, close, supported model/mode changes | Route by provider + connection ID + opaque session ID |
| Resumable provider history | View; load/resume when negotiated; then send | Verify `loadSession` or session resume capability; no fabricated session IDs |
| External currently running CLI | View discovered metadata; supported attach or vendor-native handoff only | A JSONL transcript is not a messaging channel; never write prompts into it |
| Queue watcher | Explicit Send to watcher, delivery acknowledgement | Clearly label queued / claimed / completed; no claim of direct ACP delivery |
| Unsupported Cursor/history-only record | View/open in owning tool; start a new conversation from an explicit summary | Do not imply arbitrary Cursor sessions speak ACP |

ConversationRecord stores local ID, provider/adapter ID, project root, opaque remote session ID, ownership, lifecycle, title, pinned/archived flags, last activity, chosen config values, draft and editor connection ID. Persist under the existing per-user GUO configuration directory, not a tracked file. Bounded structured event logs store message, plan, tool, permission and edit events. Credentials remain outside this store. Each event has sequence, timestamp, turn ID and source session.

On restart: list stored GUO conversations immediately, probe adapters, negotiate load/resume before enabling Send. If unsupported, retain history and offer New conversation with context rather than silently restarting. On hot reload: stop transport/controllers, decline pending approvals, retain local metadata, reconstruct views; do not leave old assembly tasks or delegates alive.

Sending to another session always requires an explicit destination chosen by the person; preserve the selected session when the list reorders. Busy-session input is queued per conversation with editable/removable drafts. Reconnection uses an explicit pending/uncertain state; never replay a possibly completed edit automatically.

## Adapter contract

One provider integration implements detection, process launch, negotiated capability snapshot, session lifecycle, prompt streaming, cancellation, permissions, configuration and optional usage. Discovery never starts an inference.

Capture the initialize response and session configuration instead of discarding them. Prefer ACP `configOptions` and `session/set_config_option`; support legacy model/mode methods only where advertised. Process subsequent config updates. Model and effort labels come from adapter data; no universal hardcoded model list. If absent, show Adapter default and explain how to change the model in the owning CLI.

Support `session/load` only with `loadSession`; support resume/list/close extensions only when advertised. Replayed messages are history, not new prompts. Adapter-specific authentication can call ACP authenticate where supported; otherwise open the documented vendor login flow. The first version can continue requiring CLI login before Start.

Normalize usage as `{availability, source, scope, observedAt, windows, tokenUsage, credits}`. Missing/null is Unknown, never zero. Read only documented vendor endpoints or adapter notifications. Gemini's `/stats model` and Claude's `/usage` are useful CLI fallbacks; do not scrape credential files to imitate them. Respect refresh limits; cache with timestamps. MCP and ACP do not guarantee a universal remaining-subscription-quota endpoint.

## Shared capability architecture

```text
GUO UI / F3             AI chat tools              ACP agent
        \                    |                       |
         shared editor capability host <--- MCP stdio bridge
                         |
       main-thread dispatch + approval + typed results
                         |
 Godot APIs / existing GUO documents, overlays, generators, validators
```

F3 is the live discovery catalog, not an unrestricted command executor. Reuse its providers and stable keys for menus, settings, asset IDs and places. Expose query, provider/index readiness, paginated catalog and exact-key resolution, including lookup-generated coordinate results. Refresh dynamic menus before resolving a key. Cached catalogs are hints only; confirm availability at execution.

Promote important actions to descriptors with stable ID, input/result schema, domain, effects (read/navigation/edit/stage/export/deploy), preconditions, availability, undo support and asynchronous job completion. F3, buttons, HTTP chat function tools and MCP should call these same operations. Add descriptors incrementally beside existing SearchEntry callbacks rather than rewriting all providers at once.

Mutations use the existing GUO confirmation gate and Godot undo or GUO overlay/history mechanism. The current generic F3 invocation deliberately asks approval for every callback because effects are not yet classified. Request arguments are shown; callbacks are resolved again after approval. Model-supplied approval flags cannot grant permission.

## Editor MCP implemented in this checkout

Endpoint: opt-in loopback TCP with a single-line 32..256 character environment token, bridged to MCP newline-delimited stdio. Up to four connections; bounded request lines, authentication deadline, operation deadline, cancellation checks and deterministic listener shutdown. The addon remains `#if TOOLS` and disappears from exported assemblies. It does not change the runtime MCP endpoint.

Configuration: `GUO_EDITOR_MCP_PORT` and optional `GUO_EDITOR_MCP_PYTHON` use the shared config resolution. `GUO_EDITOR_MCP_TOKEN` is environment-only and is not logged or included in Config. The adapter receives a stdio server entry in `session/new` when port/token/Python/script are configured. This offers a connection; it is not proof that a real adapter loaded or used the server.

| Tools | Initial coverage |
|---|---|
| search, inspect_asset, jump_world | Existing AI operations reused |
| editor_state, editor_catalog, editor_search | Open scenes/selection, readiness, structured shared F3 catalog/search |
| editor_invoke | Approved exact-key callback dispatch; optional query rehydrates dynamic keys |
| scene_tree, scene_node, scene_select | Edited scene only; bounded tree and explicit property inspection |
| scene_set_property | Approved scalar/vector/color edits recorded in Godot undo history |
| scene_open | Approved existing res:// scene opening |
| project_files, project_setting | Imported resource paths/types and explicit non-sensitive settings |
| multi_document, multi_validate | Active document/component pages, real existing validator |
| multi_open | Approved generated JSON under build/multi; traversal/linked paths/oversized files refused; unsaved document protected |
| multi_history, multi_write_stage | Approved undo/redo and existing staged write/read-back, browser refresh |

No generic `eval`, arbitrary object method call, scene script replacement, credential reader, or client-install writer is introduced. Account sessions are managed through ACP, not exposed as a tool that another agent can silently message. The endpoint's existing search/asset/navigation tools can expose local metadata; images are not returned by the initial MCP server.

## Remaining coverage to reach the complete workbench

| Domain | Next typed operations | Required proof |
|---|---|---|
| Godot scenes | Create/remove/reparent/instance nodes, scene save, batch property edits, signals, resources | Ownership persisted; one undo per operation; open/save/read-back; headless and windowed test |
| Godot scripts/build | Resource/code navigation, diagnostics, build jobs and test results | Exact path/line; completion status; errors and cancellation |
| Editor UI | Active screen, docks/dialog state, approved navigation, captures | No secret/AI transcript dump; windowed pixel evidence |
| World | Camera/selection, land/statics queries, stamp/erase/hue/z/regions/spawns, history and project export | Existing overlay/undo only; no install writes; real renderer evidence |
| Assets | Typed metadata/query by kind, previews with destination consent, staged import/export and provenance | Read-back parity, no client-data redistribution |
| Multis | Generators, transforms, selection edits, file import/export, proof jobs, private-shard deploy | Validator, stage equality, doorway/stairs walk-through; explicit deploy confirmation |
| Gumps | Document/tree inspection, layout edits, preview and export | Existing document history and runtime preview |
| Maps | Generator schemas, seeded jobs, reports and open output | Determinism, validator and world preview |
| Shards | Profiles/status/logs, approved private start/stop/place operations | Configured private instance identity; no shared-port login |
| Store/art | Catalog inspection, workflow/job status, approved publish/export/import | Existing service credentials remain sealed; publication remains explicit |

Each phase must register supported operations and report unsupported ones honestly. A menu entry may offer access to a UI while its typed automation remains unfinished. "All things Godot and GUO" is the coverage target, not a claim that the initial tools implement every subsystem.

## Delivery sequence and acceptance

1. **MCP foundation (this change):** shared catalog, initial typed tools, ACP server injection, stdio transport and protocol smoke. Pass bad-auth refusal, initialize/list/call, main-thread execution, write refusal/approval, malformed-message recovery, cancellation before queued mutation, listener rebind, path guard and reload.
2. **Conversation core:** ConversationStore, event stream and controller abstraction; multiline composer used by both dock and main workspace. Prove a pasted multiline prompt reaches fake ACP unchanged; per-session drafts, selected-target routing and queued follow-up ordering survive navigation.
3. **ACP lifecycle/config:** capability-driven auth/load/resume/config; persist remote IDs and replay transcript events. Prove unsupported controls are disabled, two independent fake sessions do not cross messages, restart/resume and cancellation/permission races work.
4. **Main AI workspace:** icon rail, projects/sessions, transcript, account/connection/activity screens and optional inspector. Match wireframe, preserve native Godot theme, keyboard focus and narrow dock behavior. Capture screenshots at representative widths.
5. **Real adapter certification:** one explicit user-requested conversation through Codex, Claude and Gemini; record CLI/adapter versions and capability snapshot; verify model changes, restart/resume, permissions and editor MCP. Never spend plan quota for automatic discovery tests.
6. **Full workbench coverage:** add domain descriptors/operations in the table above, beginning with the forest-cottage build → multi_open → validate → stage → private proof path. Test writes through native undo/overlays; register long jobs with status/cancel/results. Expand coverage systematically without bypassing subsystem invariants.
7. **Usage and polish:** add documented usage sources where available; timestamps, unknown states, rate limits; activity badges, search/pins/archive, accessibility and error recovery.

Existing editor smoke remains the regression suite; add meaningful protocol/domain checks there. Run headless reload after addon changes, ExportRelease isolation checks, and smoke.bat before a commit. UI screenshots require a windowed run; the user has requested visuals of the proposed UI, not an automatic paid provider inference.

## Source references

- [Claude Code setup](https://code.claude.com/docs/en/setup) and [authentication](https://code.claude.com/docs/en/authentication)
- [Claude ACP adapter](https://github.com/agentclientprotocol/claude-agent-acp)
- [Official Antigravity ACP registry](https://github.com/agentclientprotocol/registry/blob/main/antigravity-acp/agent.json)
- [Google migration announcement](https://developers.googleblog.com/an-important-update-transitioning-gemini-cli-to-antigravity-cli/), [Antigravity install/auth](https://antigravity.google/docs/cli/install), [Antigravity external agent](https://antigravity.google/docs/ide/extensions/zed/)
- [Gemini authentication](https://geminicli.com/docs/get-started/authentication/), [CLI reference](https://geminicli.com/docs/cli/cli-reference/), [quota](https://geminicli.com/docs/resources/quota-and-pricing/)
- [ACP session setup](https://agentclientprotocol.com/protocol/v1/session-setup) and [session config options](https://agentclientprotocol.com/protocol/v1/session-config-options)
- [MCP tools](https://modelcontextprotocol.io/specification/2025-06-18/server/tools)
- [Godot EditorUndoRedoManager](https://docs.godotengine.org/en/stable/classes/class_editorundoredomanager.html)

Repository contracts: ADR-0010 addon lifecycle, ADR-0011 world overlay, ADR-0022 staged data, ADR-0028 AI hub, ADR-0031 multi editor; docs/editor_plan.md and docs/data_formats.md.
