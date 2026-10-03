# ai_hub

Test support and setup notes for the editor's **AI** dock (ADR-0028, `docs/editor_plan.md`,
code in `godot/GUO/addons/guo_editor/AI/`).

* `fake_acp_agent.py` is a tiny ACP agent that spends nothing and logs into nothing. The editor
  smoke (`python tools/editor_smoke/run.py`) starts it to prove the ACP client: initialize,
  session/new, a prompt whose chunks stream in, a permission round trip, cancel.
  Flags: `--banner` (a non-JSON line first), `--silent` (never answers), `--no-session-new`.
* `test_fake_acp_agent.py` checks its framing: `python tools/ai_hub/test_fake_acp_agent.py`.

GUO stores no tokens. Each agent CLI signs in with its own account and keeps its own credentials;
the editor never reads them. Keys for OpenAI-compatible endpoints are sealed by the operating
system's store (DPAPI on Windows) in `%APPDATA%/GUO/ai_endpoints.json` (data_formats section 22).

The dock finds each agent on PATH. For one that is missing it shows the commands below. GUO does not
install anything for you.

## ChatGPT plan, through OpenCode

```
npm i -g opencode-ai
opencode auth login        # choose OpenAI, then ChatGPT Plus/Pro; a browser opens
```

Or start `opencode` and type `/connect`. Then, in the editor, AI dock > Agents > OpenCode > Start
(it runs `opencode acp`). `opencode auth list` shows what is connected.

## ChatGPT plan, through Codex

```
npm i -g @agentclientprotocol/codex-acp
```

(The adapter was `@zed-industries/codex-acp` until July 2026; that repository is archived.) The
adapter bundles the Codex binary and offers ChatGPT browser sign-in as one of its auth methods
when it starts. Alternatively sign in with the Codex CLI itself:

```
npm i -g @openai/codex
codex login
```

The Agents tab starts `codex-acp`; set `CODEX_PATH` to use a Codex binary of your own.

## Claude Code

```
npm i -g @agentclientprotocol/claude-agent-acp
claude                      # then /login, once
```

(Older name: `@zed-industries/claude-code-acp`, binary `claude-code-acp`.) The preset starts
`claude-agent-acp`. If you installed the older package, use the Custom command `claude-code-acp`.

## Gemini CLI

```
npm i -g @google/gemini-cli
gemini                      # sign in with Google, once
```

The preset passes `--experimental-acp` (Gemini CLI 0.11). Newer releases name the flag `--acp`;
use the Custom command `gemini --acp` for those. Checked here with 0.11.3: `initialize` answers with
the sign-in methods, and `session/new` answers "Authentication required" until the CLI is signed
in (a Workspace account also needs `GOOGLE_CLOUD_PROJECT`).

## Ollama

Nothing to set up if Ollama runs on 127.0.0.1:11434 (or `OLLAMA_HOST`). The Chat tab lists
`/api/tags`. Reasoning models such as qwen3 answer directly unless "Think" is ticked.

## Notes on the sources

The install and sign-in commands were taken from the vendors' pages on 2026-10-02 (agentclientprotocol.com,
opencode.ai/docs/acp, the codex-acp and claude-agent-acp repositories). The `opencode-ai` package name
and `opencode auth login` were not confirmed on a fetched page; if either has moved, `opencode` itself
prints its install and login help.
