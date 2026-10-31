# Scripts, language and journal translation

These features solve different problems. Embedded scripts automate a bounded
set of client actions; interface resources/clilocs display localized text;
journal translation optionally adds a machine translation to eligible speech.
None establishes full Razor compatibility or reviewed localization quality.

## Embedded scripts

Open the panel in game with **Ctrl+Shift+R** or **`-scripts`**. Touch users
can use Options > Macros > Scripts or assign Scripts/Stop script to the
command bar. Save creates a per-character `scripts/script-NAME.razor` file;
names use letters, digits, `_` and `-`. Run validates the complete source
before executing a snapshot. No script starts on login or load.

Try a local-only example:

```text
sysmsg 'Starting'
pause 1000
sysmsg 'Finished'
```

Stop with the panel's **Stop**, **Ctrl+Shift+F12** or **`-stopscript`**.
Closing the panel does not stop a script. Unsaved edits last only until logout.
Cancellation prevents future commands; it cannot unsend packets or dismiss a
target cursor already raised by the server.

| Limit | Current preview |
|---|---|
| Concurrent embedded scripts | One |
| Source size | 65,536 characters |
| Delay/timeout arguments | 0–3,600,000 ms |
| Scheduler | At most one instruction per game update, minimum 25 ms gap |
| Syntax | Limited Razor CE command subset; not Razor Enhanced Python |
| Unsupported examples | Recording, agents, `@` prefixes, unlisted commands and exact upstream timing |

[Embedded scripting](../embedded-scripting.md) contains the supported command,
expression and quoting tables. Unknown commands fail validation before actions.
Runtime world-state errors stop with a line number. External assistant plugins
are independent; Stop here does not stop an external assistant. The test plugin
ABI proof is not proof of compatibility with every real assistant.

## Interface and shard text

[Localization](../localization.md) distinguishes .NET resource cultures,
UO packet language codes, selected cliloc files and shard-owned text.
The loader falls back through its existing files; it does not automatically
translate arbitrary server content. Per-language shard packs and language
quality review remain separate work. Do not replace cliloc IDs, placeholders
or proper names with guessed translations.

## Optional journal translation

The [runtime translation pilot](../localization-runtime.md) sends eligible
public speech/emotes/yells to a separately installed **local Ollama** service.
It is off by default. Original text appears immediately and remains visible;
a labeled machine translation can appear below it. It does not replace UI,
clilocs, quests, overhead text or outgoing packets.

Client commands include:

```text
-translate languages
-translate it en
-translate status
-translate off
```

The leading minus is required; these are consumed by the client. Choosing a
direction opts in to subsequent eligible text. The service/model must already
be available. This guide does not install or start inference services.

Whispers, party/guild/alliance channels, ignored speakers, item labels, spells,
commands, cliloc and system messages are excluded. Turning the feature off
cancels pending work. A request already submitted cannot be unsent.

| Bound | Behaviour |
|---|---|
| Endpoint | HTTP loopback IP, exact `/api/chat`; no redirects, proxy, URL credentials or remote provider |
| Outstanding work | At most 16 including unread results; full queue keeps original text |
| Timeout | Configured seconds clamped to 1–60 |
| Text | Input 1,024 chars; expanded glossary input 4,096; translated text 2,048 |
| Response body | 32 KiB maximum |
| Failure | Original retained on timeout, provider/JSON error or glossary mismatch |

Configure its settings while the client is closed, merging keys into the
existing `settings.json`. Preserve the rest of the file. Direction changes
do not invert a custom glossary. No translation disk cache is created; original
journal logging is still controlled by the player's existing preference.
Status describes transport/results, not translation quality.
