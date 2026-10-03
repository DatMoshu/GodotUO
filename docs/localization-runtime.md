# Runtime journal translation

GUO can translate public speech and emotes in the journal through a **local Ollama**
service. Original text is shown immediately and retained; a labeled machine
translation appears below it when ready. This is a pilot, not native-speaker-reviewed
localization. It does not replace clilocs, quests or UI translations.

## Try it

Install Ollama from its official distribution and pull a model:

```text
ollama pull qwen3:4b
```

With Ollama running locally and the new GUO build connected to a shard, type these
**client commands** in chat (the leading minus sign is important):

```text
-translate
-translate languages
-translate it en
-translate en pt-BR
-translate zh-CN en
-translate en it
-translate status
-translate off
```

The bare command explains the feature. Selecting a direction opts in to sending
subsequent public speech and emotes to local Ollama. Off cancels pending work.
The client consumes these commands; they are not sent as speech to the shard.
Translation is off by default. Settings are saved through GUO's existing settings
lifecycle; check status when returning to the client.

Only ordinary mobile speech, emotes and yells received through the ASCII/Unicode
speech handlers are eligible. Whispers, party/guild/alliance channels, ignored
speakers, item labels, cliloc messages, spells, commands and system messages are
excluded. The original overhead text and all outgoing packets remain unchanged.
The classic journal, resizable journal and companion journal reader display the
translated text; this pilot's screenshot verification covers the desktop journals.

## Configuration

Merge these keys into the existing settings.json in your GUO client home while
the client is closed. Do not replace the rest of that file or commit it. The client
logs its home on startup; launchers normally derive it from the cache directory.

```json
{
  "journal_translation_enabled": false,
  "journal_translation_source": "it",
  "journal_translation_target": "en",
  "journal_translation_endpoint": "http://127.0.0.1:11434/api/chat",
  "journal_translation_model": "qwen3:4b",
  "journal_translation_timeout_seconds": 30,
  "journal_translation_glossary": {
    "Renval": "Renval",
    "Luna Nera": "Black Moon"
  }
}
```

Glossary keys are exact, case-sensitive source terms; values are their approved
target spelling. Longer phrases match first, with word boundaries. The speaker's
name is protected if mentioned in the text. The model receives approved target
terms in context; altered, missing or duplicated protected terms reject the result.
Use the glossary for proper names and fixed terminology, not inflected sentences.
For the reverse direction use the corresponding reverse glossary, e.g. Black Moon
to Luna Nera. Changing direction with the command does not invert a custom glossary.
Reload changed file settings by restarting; the command resets the worker when
changing direction. Glossaries are currently client settings, not downloaded packs.

The endpoint must be an HTTP loopback IP with the exact /api/chat path. Proxy use,
redirects, credentials in URLs and non-loopback endpoints are disabled. This
milestone does not implement a shard-hosted gateway or cloud provider. The supplied
Ollama service/model must itself run locally; GUO cannot control an independently
configured service's internal behavior.

## Runtime behavior

- One background worker; at most 16 outstanding messages including unread results.
  Full queues skip translation and retain the original; there is no retry storm.
- Original messages have monotonic IDs. The journal recycles entry objects, so a
  result is applied only when the original ID still exists. Removed entries cannot
  receive late translations meant for another message.
- Results are applied on the game thread. No Godot objects or world entities cross
  into the translation worker. Turning translation off or clearing the world
  cancels the session and discards its pending results.
- Ignored speakers are checked before submission and again before displaying a
  result. A request already sent before a speaker is ignored cannot be unsent.
- Requests time out after the configured interval, clamped to 1–60 seconds. Model
  loading can make the first request slower. Original speech remains visible during
  loading, timeout or failure. Status reports the last result, not language quality.
- Inputs are limited to 1,024 characters; glossary-expanded inputs to 4,096;
  responses to 32 KiB and translated text to 2,048 characters. Invalid/incomplete
  JSON, provider errors and glossary mismatches fall back to the original.
- No translation disk cache or new chat logging. Existing original-journal logging
  remains controlled by the player's existing preference. The probe uses only
  synthetic sentences and records those explicitly as test evidence.

The provider uses Ollama's [chat API](https://docs.ollama.com/api/chat) and
[structured output](https://docs.ollama.com/capabilities/structured-outputs).
The model has no tools and its output is displayed as text, never executed.
Machine translation can still change nuance, gender or roleplay meaning; players
must be able to read the original. The UI/cliloc language setting is independent
of the explicit source/target pair used here.

## Reproduce verification

Run the deterministic tests (no model, account or network required):

```text
dotnet run --project tools/localization_tests/LocalizationTests.csproj
```

To exercise the real provider with synthetic samples, append -- --live. It defaults
to loopback port 11434; GUO_TRANSLATION_TEST_ENDPOINT overrides that test endpoint.
This is not a native-speaker quality benchmark.

Build GUO, then run the pinned **godot-console** against the local dev shard:

```text
godot-console --path godot/GUO -- --play --translation-probe --cache-dir <absolute-isolated-home>/cache --screenshot-dir <absolute-evidence-folder> --account <dev-test-account> --character Localizer --window-size 1280,800 --no-focus --silent --no-splash
```

Resolve client data/version/shard settings through the normal launcher configuration
or pass the existing --client-data and --client-version flags. Use a dedicated dev
test account; the probe may create its character and sends synthetic public speech.
Never point it at a public shard. An alternate local provider can be configured in
<absolute-isolated-home>/settings.json before running. The probe copies settings
into a scratch profile; it must not use the player's normal home.

It saves Italian-to-English, English-to-Italian, classic-journal and unavailable-
provider screenshots plus evidence.json with provider timings and server-echo
evidence. It also checks command opt-in/off, glossary preservation, original text,
entry recycling and the same session-clear hook used on disconnect. Exit zero
means those checks passed. A separate cancellation test asserts that the provider
receives cancellation; this is not evidence of an actual network-disconnect test.

Before committing, run launchers/dev/smoke.bat. Review the images and the actual
sentences, not just the exit code. Preserve tests and failures separately from
native-speaker approval. Android, web and Steam Deck runtime behavior remains
unverified in this milestone.

## Recorded pilot: 2026-10-02

Windows, Godot 4.7.2 .NET, local ModernUO dev shard, Qwen3:4b on an RTX 4090.
Four synthetic server-echo messages translated in 357, 166, 170 and 157 ms with
the model already loaded. The game advanced 20, 11, 11 and 10 frames respectively
while awaiting results. A separate first model request took about 28 seconds in
the Ollama log, including startup; warm timings are not cold-start guarantees.

The runtime probe passed original retention, both directions, glossary and emote
handling, provider-unavailable fallback, recycled-entry protection and session
clear. The nine deterministic tests and six-step repository smoke check passed.
Screenshots were inspected for the resizable and classic desktop journals. The
generated evidence is in build/localization/screenshots for this worktree; it is
not checked in or bundled with the client.

Examples include “Ci vediamo alla Luna Nera, Renval.” becoming “We'll meet at the
Black Moon, Renval.” and “*Opens the door and greets Renval.*” becoming “*Apre la
porta e saluta Renval.*”. The glossary maps the tavern name in either direction.
The Italian “Incontra me alla Luna Nera” result is understandable but less natural
than “Incontrami alla Luna Nera”; language quality still needs native review.
The client also reported RID/ObjectDB cleanup warnings at exit; the probe and
smoke passed, but no claim is made here that those warnings are resolved.

## Expanded language catalogue

Any two different entries below can be selected. The same qwen3:4b model serves
all entries; no per-language download is needed. `-translate languages` lists them.
Codes ignore case and accept underscores. Regional aliases such as en-US, es-MX
and hi-IN resolve to their base language. pt defaults to pt-BR; zh/zh-CN/zh-SG to
zh-Hans; zh-TW/zh-HK to zh-Hant; fil to tl. Chinese region aliases select writing
systems, not dialects. Unknown or identical resolved languages leave settings intact.

`en` English, `es` Spanish, `pt-BR` Brazilian Portuguese, `pt-PT` European Portuguese, `fr` French, `de` German, `it` Italian, `ru` Russian, `uk` Ukrainian, `pl` Polish, `tr` Turkish, `nl` Dutch, `sv` Swedish, `da` Danish, `nb` Norwegian Bokmal, `fi` Finnish, `cs` Czech, `ro` Romanian, `hu` Hungarian, `el` Greek, `bg` Bulgarian, `sr` Serbian, `hr` Croatian, `sk` Slovak, `zh-Hans` Simplified Chinese, `zh-Hant` Traditional Chinese, `ja` Japanese, `ko` Korean, `hi` Hindi, `bn` Bengali, `ur` Urdu, `pa` Punjabi, `mr` Marathi, `gu` Gujarati, `ta` Tamil, `te` Telugu, `kn` Kannada, `ml` Malayalam, `ne` Nepali, `si` Sinhala, `id` Indonesian, `ms` Malay, `tl` Tagalog, `vi` Vietnamese, `th` Thai, `my` Burmese, `km` Khmer, `ar` Standard Arabic, `fa` Persian, `he` Hebrew, `sw` Swahili, `az` North Azerbaijani, `uz` Northern Uzbek, `kk` Kazakh.

These are provider choices, not fully translated UI or certified display support.
They follow [Qwen's documented coverage](https://qwenlm.github.io/blog/qwen3/).
Arabic, Persian, Urdu and Hebrew need RTL/shaping verification; Indic and Southeast
Asian scripts need glyph and shaping checks. CJK display depends on client fonts.
The earlier screenshots verify only the Italian/English pilot. Native review is
still required. Hausa, Yoruba and Amharic are absent from the model's documented
list and are not promised; those communities need a separately evaluated provider.

To add a language, update `TranslationLanguages.cs`, check provider documentation,
run catalogue tests and a real translation, then verify journal glyphs, wrapping
and input. Keep UI culture, clilocs and wire language separate.

### Expanded-catalogue verification (2026-10-02)

Build and 10 deterministic checks pass, including canonical codes, region aliases,
unknown inputs, same-language rejection and construction for every catalogue entry.
A real local qwen3:4b run translated one synthetic English sentence into each of
53 non-English targets: 51 returned structurally accepted responses; Sinhala (si)
and Burmese (my) raised InvalidDataException and fall back to original text.
Punjabi and Marathi responses showed obvious repetition. Other responses remain
unreviewed: a nonempty response is not a quality pass. These choices are experimental,
especially the noted failures. No expanded-language rendering screenshots were taken.
Raw synthetic results: build/localization/languages-live.log (local, ignored).
Reproduce with GUO_TRANSLATION_TEST_ENDPOINT set to the local /api/chat endpoint:

```text
dotnet run --project tools/localization_tests/LocalizationTests.csproj -- --languages-live
```

The matrix command records every result, including failures, and continues; its
exit status alone is not an all-languages pass. Inspect each JSON status and obtain
native review before promoting a language. The 4B pilot model needs further model
comparison for reliable broad coverage; changing journal_translation_model is
already supported without code changes.
