# Synchronize Cliloc names from tiledata

F16a/b, L3 editor authoring. The Cliloc page's sync icon previews item-name
differences for the selected installed language or custom source document.
It supports complete legacy 37-byte and modern 41-byte item tables, up to
65,536 items. No native tiledata or Cliloc file is written.

The mapping follows Fiddler 4.24 commit
`610c930833dac8d158f08a0af36da43dd90d08b4`,
`ClilocControl.BuildTileDataSyncPlan` / `GetCliLocBaseId`:

| Item ID | Cliloc ID |
|---|---|
| `0x0000`–`0x3FFF` | item ID + 1,020,000 |
| `0x4000`–`0x7FFF` | item ID + 1,078,872 |
| `0x8000`–`0xFFFF` | item ID + 1,084,024 |

Native names stop at their first NUL within the 20-byte field and decode as
ASCII, matching Fiddler's name helper. Unedited names with bytes above ASCII
therefore use `?` replacements; this operation does not silently reinterpret
them as UTF-8. Explicit GUO project name edits retain their authored Unicode.

The snapshot combines exact source bytes with validated saved modern item
metadata documents and current/recovered metadata drafts. An active draft takes
precedence over its saved document, including reverting a saved name to source.
Unvisited recovered drafts are included. Invalid fields, duplicate recovery rows,
corrupt saved documents or a foreign source baseline refuse the preview rather
than silently falling back. Metadata field capture does not save or commit it.
Project metadata still requires modern tiledata; source-only legacy sync works.

## Review and apply

Each row identifies the item, mapped Cliloc ID, Add/Update/Remove action, current
text and proposed item name. Equal text produces no row. Missing strings with
nonblank item names propose Add. Blank or whitespace-only names propose Remove
when a working string exists, including an existing empty string. Unmapped
strings and unselected rows stay unchanged.

Check the displayed target language before applying: item names can replace
translated text and remove placeholder/reference tokens. Token differences are
shown in the comparison. This is deliberately chosen synchronization, not an
automatic translation or runtime language-registration operation.

No rows are preselected. Filter by action or item hex ID, Cliloc ID or text.
The list displays 250 rows per page, with every change available through paging.
Select matching includes all filtered pages; Clear selection clears hidden rows
too. Selection remains across filters/pages, with selected add/update/removal
totals always visible.

Apply selected edits the project working document in one undo step. Source flags
are preserved; new records use flag zero. This follows GUO's working-document
policy rather than Fiddler's Modified-flag rewrite. Save copy persists the normal
source-bound Cliloc JSON; Undo after save becomes dirty again. Removal uses the
existing project tombstone contract, never an empty-text CSV workaround.

The complete selected patch must fit 10,000 changes and 2 MiB of authored text,
with each edited string bounded to 32,767 UTF-8 bytes. An oversized selection
fails atomically and remains open for a smaller selection; it is never truncated.
Review can show more rows than the selected patch budget.

Applying rechecks the Cliloc source/revision and captures tiledata/project
metadata again. Changed source bytes, saved metadata, raw metadata drafts,
intervening Cliloc edits (even edit then undo), invalid Cliloc drafts or failed
recovery refuse the stale review. Close and reopen it to rebuild the plan.

## Verification

`python tools/editor_smoke/run.py --reload` exercises the mapping boundaries,
both layouts, name decoding, add/update/remove/unchanged rows, selected-only
application, source flags, single-step undo, save/reopen, source-byte preservation,
stale guards, project/recovered metadata, invalid data and patch limits.
The authorized windowed workbench proof captures a synthetic sync review and
the resulting inline working copy. [Scenario recipe](../../tools/scenarios/fiddler-f16ab/README.md).

Reverse synchronization, variant-specific default-Cliloc fields, tiledata-to-
animdata navigation and native binary staging remain separate catalogue work.
