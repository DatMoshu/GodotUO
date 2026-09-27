# ADR-0022: Authoring UO Data Files into a Staged Set, Append-Only, in Reserved Ranges

## Status

Proposed

## Date

2026-09-27

## Last Verified

2026-09-27, on a UOP install (Classic, no `verdata.mul`):

- `python tools\uodata_write\test_uodata.py` passes on synthetic fixtures made
  at test time: a tiny tiledata, an anim.idx/anim.mul for three people bodies,
  and two LegacyMUL UOPs of a few entries. No client data. It checks the free
  scanners, the range policy and its override, the allocator, the chained UOP
  block, the MUL+IDX append, the tiledata in-place write, the read-back and an
  unchanged install. CI runs it.
- `python tools\uodata_write\run.py dreadcrest` writes Codex's Dreadcrest
  shield into `build\uodata\moshu`: item 0xFFF0, animation body 849, paperdoll
  gumps 50849 and 60849. All 179 records read back equal, and the install is
  hash-checked unchanged.
- `python tools\uodata_write\play.py --clip` puts the stage in front of the
  private ModernUO shard and a GUO client. The bridge equips 0xFFF0; the
  client's dump shows it worn on TwoHanded, the paperdoll shows its gump, and
  the character walks with it (H3; the clip is in `build\uodata_play`).
- `tools\uopack` (H2) writes through the same writers and stages the same
  shield, 179/179.

**Not yet verified:** a MUL-only install end to end (the MUL+IDX writer is
covered by the tests and by anim.mul, not by art in a real MUL install), and a
shard other than ModernUO reading the stage.

## Context

The owner wants new content in the game, starting with a new wearable: its
item art, paperdoll gumps, 175 animation frames and a tiledata record. ADR-0020
covers replacing existing assets through a verdata patch set. It does not
cover adding assets at new ids, and a verdata patch cannot add animation. So
the files themselves have to be written.

Three things make that risky:

1. The install is the user's, and proprietary. A broken write must never reach
   it.
2. UO data files are indexed by id. An id the client or a shard already uses
   is overwritten silently, and an id another pack takes later collides.
3. Shards add their own content. A shard maintainer's ranges are not known to
   us.

## Decision

### A staged set, copy-on-write

Every write goes into a stage folder (`tools/uodata_write/uodata.py`, class
`Stage`). The first write to a file copies it from the install and records its
SHA-1 and size in `stage.json`. Later reads prefer the staged copy. A stage
inside the install is refused. `check_install_unchanged()` re-hashes every
copied original; the tools run it after each write.

The stage also holds:

- `files_override.txt`, for the client's `settings.json` `files_override`;
- `slots.json`, the range registry below.

A ModernUO shard lists the stage first in its data directories.

### Append-only writers

Nothing already in a file is changed, except one tiledata record per new item.

| File | Write |
|---|---|
| LegacyMUL UOP (art, gumps) | Data appended at the end, then a **new block** of entries chained after the last. Its next pointer is set; the header's file count is updated. An existing name hash is refused. |
| MUL + IDX (anim, and art or gumps on a MUL install) | Data appended to the MUL, then the IDX entry written. |
| tiledata.mul | The item's 41-byte record written in place. Fields not given keep their value. |
| hues.mul | The hue's record written in place. |

The client walks the UOP block chain and ignores the header count, so the old
entries and offsets stay valid. The writers keep records as given (see
`docs/data_formats.md`):

- a UOP land entry is 2048 bytes, not 2024;
- a static's 4-byte header is not always zero.

### Verification through the readers

Every record is read back through the same tables the client uses and compared
byte for byte. The client's asset probe then decodes the new art and gumps.

### Free slots

Each namespace has a scanner:

- **Item ids:** tiledata name and flags both empty, and no art entry.
- **Gumps:** no entry.
- **People bodies (400+):** all 175 anim.idx entries empty, no
  AnimationFrame*.uop entry, and no mention in Body.def, Bodyconv.def,
  mobtypes.txt, Anim1/2.def or Equipconv.def. A wearable body also needs
  paperdoll gumps 50000 + body and 60000 + body free.

### Ranges: the policy

A pack (a set of related content, such as the owner's `moshu`) reserves a
contiguous range per namespace in `slots.json`. The allocator only hands out
ids that:

- the scanner finds free;
- no other pack holds;
- the policy does not bar.

The policy is `tools/uodata_write/ranges.json`:

```json
{ "never": { "static": [[65535, 65535]] },
  "packs": { "moshu": { "static": [65520, 65534] } } }
```

**Why the top of item space (0xFFF0-0xFFFE) for moshu.**

- An item id is the one id that leaves the stage. The shard saves it in every
  item it creates, so it must be the same in every stage built for that shard.
  A scan-derived id would depend on the install. So the pack's item range is
  fixed in the policy.
- The animation body and gumps are only referenced from the staged tiledata
  (its anim field) and from gump ids derived from the body. They can differ
  between stages without breaking a save, so they are scanned, lowest free
  first.
- Official art grows upward from the bottom, in gaps and after the highest
  used id. Shards that add items fill the same gaps or continue upward from
  the last official id. The top of the 16-bit space is the part neither
  reaches first.
- 0xFFFF itself is never handed out. Upstream uses it as a sentinel: for
  example, `Mobile` compares `GetGraphicForAnimation()` with 0xFFFF, and many
  hue and graphic defaults are 0xFFFF.

**How packs avoid shard-custom ranges.**

- The scanner already skips every id the installed files use, so a shard's
  custom art that is in the user's files is never taken.
- Custom content that is only on the shard's side (a custom tiledata or art
  the user has not installed yet) is invisible to the scanner. That is what
  `never` is for.
- Packs with no fixed range take the highest free run below every reserved
  range. Two packs are never given overlapping ranges in one stage.

**How a shard maintainer overrides it.**

A maintainer writes a file of the same shape and passes it:

- with `--ranges FILE` on `tools/uodata_write/run.py`, or
- with the `UO_DATA_RANGES` environment variable, which every tool reads.

Its `never` lists are added to the default's. Its `packs` entries replace the
default's, namespace by namespace. For example, a shard whose own items live
at 0xF000-0xFFFF moves moshu below them and bars its whole block:

```json
{ "never": { "static": [[61440, 65535]] },
  "packs": { "moshu": { "static": [59392, 59406] } } }
```

A fixed range that is not free (used by the install, held by a pack, or
barred) is refused with the conflicting ids. It is never trimmed.

## Consequences

**Positive**

- The install cannot be damaged by the tools, and a check says so after every
  write.
- New content at new ids, including animation, which ADR-0020 cannot do.
- A pack's item ids are the same on every machine, so shard saves stay valid.
- One writer module is shared by `tools/uodata_write`, `tools/uopack` (H2) and
  the editor's bulk panel.

**Negative**

- A stage duplicates whole files: a staged `anim.mul` or art UOP is the full
  size of the original.
- UOP files grow with each append, and replaced entries are not reclaimed. A
  repack is a separate, later tool.
- The policy only knows the shard ranges someone writes down. A maintainer who
  does not provide a file relies on the scanner seeing their content in the
  user's install.

## Alternatives considered

- **Write into the install.** Rejected: rule 8, and a single bad write loses
  the user's data.
- **Verdata patches (ADR-0020).** They cannot add animation, and adding new
  ids through verdata is patchy across clients.
- **Rewrite the UOP whole, entries sorted.** More compact, but every offset
  changes, and a bug corrupts everything. Appending a chained block is the
  smallest change the client accepts.
- **Scan for the item range too.** It would give different ids on different
  installs and break shard saves.

## Related

- ADR-0019 (the Asset Store) and ADR-0020 (asset overlay).
- `docs/data_formats.md`: the LegacyMUL UOP block chain, anim index
  arithmetic, and tiledata layout.
- `tools/uodata_write`, `tools/uopack`, and `tools/guo/uorecord.py`.
