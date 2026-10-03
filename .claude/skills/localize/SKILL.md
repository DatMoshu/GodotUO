---
name: localize
description: "Maintain GUO UI and shard translations, add languages, identify stale translations, prepare translator handoffs, and verify localization in the client."
argument-hint: "[scan|extract|update|add-language|validate|status|brief|qa|shard-plan] [locale or scope]"
user-invocable: true
agent: localization-lead
allowed-tools: Read, Glob, Grep, Write, Edit, Bash
---

# GUO localization

Read [the localization guide](../../../docs/localization.md) and the
[localization role](../../agents/localization-lead.md). Follow AGENTS.md and
applicable path rules. Infer the mode from natural language; with no actionable
request, report the current inventory and next gap rather than failing for a
missing subcommand.

## Establish scope

Identify source owner, source language, target locale and surface: GUO UI, client
clilocs, shard content, onboarding material or live speech. A shard's source
language need not be English. Inspect the runtime lookup before choosing a format.
For runtime speech, use `TranslationLanguages.cs` as the shared provider catalogue;
track model response, native review and font/shaping evidence separately. Read the [implemented pilot and verification steps](../../../docs/localization-runtime.md).
Preserve existing keys, numeric IDs, resource names and placeholder dialects.
Do not migrate the port wholesale to Godot Tr(), JSON or ICU during translation.

## Modes

- **scan/status:** Read resources, call sites and the mobile literal backlog.
  Report source, current reviewed, draft, missing, stale, invalid and unknown
  counts by surface and locale. State denominator and exclusions. These modes
  are read-only; file presence does not establish runtime support.
- **extract:** Produce a scoped inventory or translator export in the existing
  format. Include stable identity, exact source, source revision, context,
  placeholder meanings and screenshot reference when available. Exclude logs,
  commands and protocol tokens from player-facing translation candidates.
- **update:** Compare with the previous source snapshot. Queue new, changed or
  context-changed entries; retain unaffected translations and reviewer metadata.
  Changed source invalidates old review. Preserve removed entries in history.
  Without a baseline, report freshness as unknown. Never overwrite human edits
  with machine drafts during an automated merge.
- **add-language:** Identify the UI culture, cliloc language, wire language and
  font mapping separately. Prepare catalogs and drafts within scope. Verify
  packaging, selection and fallback before claiming the language works. Record
  unsupported runtime paths instead of inventing support.
- **validate:** Check duplicate keys/IDs, parsing, encoding, empty values, stale
  entries, escaping, markup and glossary terms. Preserve .NET format indexes
  and specifiers, cliloc token indexes and nested references. Permit grammatical
  reordering while preserving argument meaning. Character counts do not prove
  text fit, glyph coverage or shaping.
- **brief:** Prepare source/target locales, glossary, protected names/commands,
  speaker/tone, screen context, revision and reviewer expectations. Machine
  translations are drafts. Write requested local artifacts; sending them to
  people or services requires authorization.
- **qa:** Exercise selection and fallback in the actual client. Capture login,
  character creation, options, journal, representative gumps and touch UI when
  in scope. Check non-ASCII input, wrapping, glyphs, mixed-direction text, plural
  cases and reconnect behavior. Record build, platform, locale and evidence.
  Use NOT TESTED if execution is unavailable; a plan is not a passing test.
- **shard-plan:** Prepare a language launch plan using the guide: owners, glossary,
  joining journey, review queue, support expectations and acceptance criteria.
  Separate available features from proposed runtime changes.

For RTL, voice or source-freeze requests, apply only the relevant workflow to
actual project content. A freeze is an optional release snapshot, not a prerequisite
for continuous translation maintenance.

## Deliver

Complete authorized local edits without redundant confirmation. Report changed
artifacts, checks, stale/missing content and runtime limitations. Build for resource
or code changes; use godot-console for runtime checks and run the repository smoke
check before committing. Do not claim native-speaker review, successful rendering,
publication or owner consent without evidence.
