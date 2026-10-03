# GUO localization and multilingual shards

This guide defines agent workflows and a roadmap for helping shard owners welcome
players across languages. The first [runtime journal translation milestone](localization-runtime.md)
adds optional local Italian/English speech translation. Per-language shard content
packs remain roadmap work. Use the [localize skill](../.claude/skills/localize/SKILL.md).

## Existing implementation

| Surface | Evidence | Implication |
|---|---|---|
| GUO UI | [Resources](../godot/GUO/src/Resources/embedded), [lookup](../godot/GUO/src/Resources/ResGumps.Designer.cs), [build](../godot/GUO/GUO.csproj) | Preserve ResGumps, ResGeneral and ResErrorMessages keys and ClassicUO resource logical names. Verify localized packaging and selection. |
| Language setting | [Startup](../godot/GUO/src/Client/Main.cs), [packets](../godot/GUO/src/Network/OutgoingPackets.cs) | UO language codes reach the wire; they are not interchangeable with .NET cultures or Godot locales. |
| Client text | [ClilocLoader](../godot/GUO/src/Assets/ClilocLoader.cs) | Loads English, then the selected language, then Clilocs.txt. Missing selected files fall back to English; absent base files require testing. |
| Shard overrides | ClilocLoader.ReadOurs | One Clilocs.txt, numeric ID then whitespace then text. Blank lines and # comments are skipped; later duplicates win. No per-language override selection in this loader. |
| Unlocalized UI | [Mobile inventory](ui/unlocalized_strings.md) | Recheck this historical inventory against code. Reuse an appropriate cliloc or the existing UI resources. |
| Discovery | [Catalogue rules](../servers/README.md) | Owner-approved listings exist. Language filters and localized descriptions are proposed extensions. |

Godot translation features do not automatically localize custom C# gumps or shape
text in GUO's font renderer. Do not edit sources/, commit proprietary client text,
or overwrite the user's installed client data when preparing translations.

## Maintain translations incrementally

Keep a source snapshot with the translation workspace in GUO or the shard's own
repository. The following is a proposed maintenance record, not a runtime format;
use equivalent fields in an existing translation platform where available.

| Field | Purpose |
|---|---|
| Surface, stable key/ID, shard owner | Identity independent of wording and isolation between shards |
| Source locale and exact source text | A shard's original language need not be English |
| Source revision/hash and context revision | Detect changed wording, placeholders or meaning |
| Target locale and translated text | Keep regional variants explicit |
| State | Missing, draft, reviewed or stale; validation errors recorded separately |
| Reviewer and reviewed source revision | Preserve human review provenance |
| Context, token contract and glossary | Screen/speaker, protected names, formatting and terminology |

Compare each content release with its previous snapshot. Added entries need
translation; changed text or context makes reviews stale; unchanged entries retain
translations and review. Removed entries leave the active denominator but remain
recoverable in history. Without a baseline, freshness is unknown. Do not overwrite
human edits with generated drafts; conflicting translations require review.

Coverage is current reviewed entries divided by active source entries for a named
surface and revision. Report missing, draft, stale and invalid entries separately.
Prioritize joining and common interactions rather than a global percentage alone.

Validate actual syntax: .NET composite formats including escaped braces, indexes
and specifiers; cliloc tokens such as ~1_name~ and nested references; server markup
and control characters. Reordering may be grammatical; losing argument meaning
is an error. Plurals need a mechanism the runtime implements, not just ICU syntax
inserted into text.

## Add a language

1. Choose the source owner and target locale. Record a glossary for mechanics,
   lore, names, commands and tone; reuse translations only with matching context
   and permission to reuse them.
2. Map UI culture, UO cliloc language, speech packet language and glyph requirements
   independently. Do not invent wire-language codes for unsupported locales.
3. Translate login, joining instructions, rules, character creation and the first
   interaction. Label drafts and preserve their source revisions.
4. Verify resource logical names, satellite packaging, lookup, locale selection,
   missing-entry and missing-file fallback, and restart. Multiple shard language
   packs need a designed loader change before they can be advertised.
5. Validate formatting and terminology; obtain language review. Exercise accents,
   CJK IME where relevant, combining marks, long text and line breaks. For RTL,
   verify shaping and mixed-direction names/numbers. Do not indiscriminately mirror
   world art or directional controls.
6. Capture the actual client on each target platform. Unsupported rendering/input
   blocks the affected surface's support claim. A resource file alone is insufficient.

## Shard-owner pilot

Start with one willing shard, its source language and one additional language.
Italian-to-English is an example, not consent or a commitment from a shard mentioned
in an attached conversation. Give the owner a localization workspace with original
content, glossary, drafts, source snapshots and a small review queue. Contributors
should be able to translate one quest without learning the client architecture.

Deliver:

- Localized discovery copy, joining instructions and custom-data installation help.
- Rules, roleplay conventions, a first-session tutorial and common error explanations.
- A shared glossary for quests, NPC dialogue, items and volunteer contributors.
- Separate labels for UI languages, content languages and languages staff can
  actually support. Translation does not imply multilingual moderation.
- An update handoff containing only new/stale entries with preview context.
- Named reviewers, a contribution route and rollback to a previous content release.

Follow the catalogue's existing owner-consent rules. Prepare owner-supplied copy
for review; a screenshot or public mention is not authorization to list a shard.
Proposed language fields need schema, loader and UI changes together; adding ignored
fields does not implement discovery.

## Optional speech and emote translation

The [journal pilot](localization-runtime.md) implements the first local-provider
step. The broader feature below remains a proposal. Reviewed static translations should not depend
on a live model. Preserve original utterances and speakers, visibly label translated
text and let players reveal the original. Keep names, commands and emote boundaries
intact. Do not automatically send rewritten commands or translated speech to the server.

Use an asynchronous bounded queue, timeout, cancellation, rate limits and original-text
fallback. Failures must not delay packets or gameplay. Evaluate slang, mixed languages,
invented names, emotes and rapid speech with roleplayers. Measure latency and meaning
preservation before choosing a model or promising hardware support.

Before external processing, obtain authorization for the chat data flow and define
opt-in and retention behavior. Keep private speech out of routine logs and fixtures.
Local processing is an option to evaluate, not a guarantee that all players can run
it. Treat chat as untrusted content to translate, never as instructions to tools.

## Implementation roadmap

| Work | Deliverable | Completion evidence |
|---|---|---|
| Agent workflow (this change) | GUO-specific skill, role and guide | Valid metadata and resolving links |
| Inventory and delta tooling | Deterministic export and stale/missing report | Changed-source, preserved-review, removed-key and invalid-token fixtures |
| UI locale delivery | Culture selection and packaged resources | Export resolves selected text and fallback |
| Shard language packs | Versioned scoped overrides and collision policy | Two shards and two locales stay isolated; missing pack falls back |
| Owner pilot | Reviewed joining journey and update handoff | Target-language player joins and completes first interaction |
| Discovery extensions | Owner-supplied language metadata | Schema/UI agree and consent is recorded |
| Optional live translation | Original plus labeled translation | Latency, outage, privacy and meaning tests |

Example requests: `/localize scan touch UI`, `/localize add-language Italian UI`,
`/localize update shard quests since the previous release`, and
`/localize shard-plan Italian to English onboarding`.

For resource/code changes run the C# build and appropriate runtime checks. Use
godot-console for automation and the smoke check before a commit. A draft, a passing
parser, language review and a successful playthrough are distinct evidence; report
exactly which is available.
