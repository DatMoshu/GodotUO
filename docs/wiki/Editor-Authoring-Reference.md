# Editor authoring reference

Manual asset authoring is L3 editor code and remains available with AI off.
Each guide below has its own scope. Working documents, native stages, external
text exchanges and independent read-back proofs are distinct operations.
Use [Assets and Fiddler](Assets-and-Fiddler.md) for the end-to-end workflow;
use the exact format guide before exchanging files with another tool.

| Detailed guide | Evidence boundary |
|---|---|
| [Assets workbench](../editor/assets_workbench.md) | Scoped authoring guide; see its validation and limitations |
| [Batch static PNG replacement](../editor/batch_static_replacement.md) | Scoped authoring guide; see its validation and limitations |
| [Cliloc CSV exchange](../editor/cliloc_csv_exchange.md) | Scoped authoring guide; see its validation and limitations |
| [Custom Cliloc sources](../editor/cliloc_custom_sources.md) | Scoped authoring guide; see its validation and limitations |
| [Localized-string working documents](../editor/cliloc_documents.md) | Scoped authoring guide; see its validation and limitations |
| [Verified native Cliloc staging](../editor/cliloc_native_staging.md) | Scoped authoring guide; see its validation and limitations |
| [Synchronize Cliloc names from tiledata](../editor/cliloc_tiledata_sync.md) | Scoped authoring guide; see its validation and limitations |
| [UOFiddler parity: a workbench for UO modders](../editor/fiddler_parity.md) | Planning / coverage matrix; read status cells |
| [Hue palette working documents](../editor/hue_documents.md) | Scoped authoring guide; see its validation and limitations |
| [Verified native hue export](../editor/hue_native_staging.md) | Scoped authoring guide; see its validation and limitations |
| [Item metadata working documents (F06a)](../editor/item_metadata.md) | Scoped authoring guide; see its validation and limitations |
| [Verified native item metadata staging](../editor/item_metadata_native_staging.md) | Scoped authoring guide; see its validation and limitations |
| [Reviewed Fiddler CSV multi exchange](../editor/multi_fiddler_csv.md) | Scoped authoring guide; see its validation and limitations |
| [Fiddler TXT multi interchange](../editor/multi_fiddler_txt.md) | Scoped authoring guide; see its validation and limitations |
| [Fiddler UOA text interchange](../editor/multi_fiddler_uoa.md) | Scoped authoring guide; see its validation and limitations |
| [Reviewed Fiddler UOX3 export](../editor/multi_fiddler_uox3.md) | Scoped authoring guide; see its validation and limitations |
| [Reviewed Fiddler WSC exchange](../editor/multi_fiddler_wsc.md) | Scoped authoring guide; see its validation and limitations |
| [Sound audition and empty slots](../editor/sound_audition_slots.md) | Scoped authoring guide; see its validation and limitations |
| [Sound naming working documents](../editor/sound_documents.md) | Scoped authoring guide; see its validation and limitations |
| [Sound lists and batch WAV export](../editor/sound_list_export.md) | Scoped authoring guide; see its validation and limitations |
| [Verified native sound staging](../editor/sound_native_staging.md) | Scoped authoring guide; see its validation and limitations |
| [Sound-effect WAV export](../editor/sound_wav_export.md) | Scoped authoring guide; see its validation and limitations |
| [Sound-effect WAV import and project PCM](../editor/sound_wav_import.md) | Scoped authoring guide; see its validation and limitations |
| [Verified native texture staging](../editor/texture_native_staging.md) | Scoped authoring guide; see its validation and limitations |
| [Texture workbench](../editor/texture_workbench.md) | Scoped authoring guide; see its validation and limitations |

## Common distinctions that prevent data loss

- **Save copy** saves a working project/document, not a native client archive.
- **Prepare** captures and validates a candidate; it does not apply it.
- **Apply** requires the reviewed inputs, document revision and destination
  still to match. Changes or links require a fresh review.
- **Recover** uses the retained prior stage state. It is not a repair tool
  for arbitrary overwritten installations.
- **Source** remains read-only; a disposable stage contains local derived data
  and must not be uploaded publicly.

### Multi formats

Fiddler TXT reads bare item IDs as hexadecimal; GUO legacy TXT uses decimal
unless prefixed. Choose explicitly. TXT records are `ID X Y Z Flags`, preserve
order/duplicates/raw flags in the working document, and carry no hue/level.
The reviewed path accepts 1–65,535 records and at most 4 MiB. Its review shows
the first 200 rows but confirmation applies all captured records. Nonzero hues
need explicit loss confirmation for TXT export. Source:
[Fiddler TXT contract](../editor/multi_fiddler_txt.md).

Fiddler CSV, UOA text, UOX3 and WSC each have separate numeric, field and
loss rules. File-extension detection retains legacy behaviour; it does not
automatically select every newer Fiddler profile. No single format's proof
certifies the others.

### Native replacements

Hue staging replaces existing records, not group allocation/growth. Its
reviewed manifest accepts 1–256 existing IDs and at most 256 KiB. Complete
native names/padding, group headers, other records and length are checked by
the independent reader. See [Hue staging](../editor/hue_native_staging.md).

Sound working documents cover Effects, not Music. Auditioning an empty slot
does not implement insertion/removal. WAV import/export, working names and
occupied-slot native replacement have different guides. Texture and item
metadata replacement likewise require their typed native-stage paths.
See the corresponding guides above before choosing a destination.
