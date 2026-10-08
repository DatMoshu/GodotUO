# The extracted art set

An **extracted art set** is a copy of your own Ultima Online install's art, laid out as large PNG pages that GUO can
read a page at a time. It is optional, it is off until you ask for it, and the client works exactly as before without
it. The format is `docs/data_formats.md` section 36; the reasons are in `docs/architecture/ADR-0034-extracted-art-sets.md`.

**It is your data and it stays on your machine.** The set is made from files you installed yourself. It is written
only to a folder on your computer, it is not part of the repository, it is not put in any export or bundle, and it
must not be uploaded or shared. Delete the folder and it is gone; nothing else depends on it.

## What goes in it

| Class | Contents |
|---|---|
| `land` | the 44 x 44 land tiles |
| `static` | item and static art |
| `gump` | gump art |
| `texmap` | the textures that stretch over land |
| `light` | light masks |
| `anim` | animation frames, stored by the block the client's animation loader reads |

Hues, maps, tiledata, multis, sounds and clilocs are **not** in the set; the client still reads them from the install.
The art files stay needed too: the set is a faster path to the same pixels, and anything it cannot answer (an id it does
not hold, a damaged page, a changed install) is read from the original files, with one warning in the log.

## Make it

Three ways, all running the same tool (`tools/art_extract/run.py`: export, then verify):

- **Launcher:** `launchers\pipeline\05_extract_art.bat`. Add `--what art,gumps` to limit the classes.
- **Editor:** UO Assets dock, the **Art set** tab. Tick the classes, press **Export + verify**; the tool's lines
  appear as it goes (one per class). **Verify only** re-checks an existing set; **Open folder** shows it in Explorer.
  No AI feature is involved.
- **Command line:** `python tools\art_extract\run.py export` then `python tools\art_extract\run.py verify`.

Export removes the previous set in the folder first, so an old set is never mixed with a new one. `verify` reads the
install again and compares every image with its page; it must report a full match.

### Where it goes

`UO_ART_EXTRACT_DIR`. Unset, it is the `art_extract` folder under your workspace folder (`UO_WORKSPACE_DIR`,
`%LOCALAPPDATA%\GUO` by default). Set it in `launchers\_shared\config.local.bat` or the environment, as every other
setting (environment, then `config.local.bat`, then `config.bat`). `python tools\art_extract\run.py where` prints the
folder in use. The tool refuses to write anywhere else.

## Turn it on

Start the client with `--art-set`, or set `UO_ART_SET=1`: `launchers\game\play.bat --art-set`. `--no-art-set` forces it
off. At start the client compares the set's record of your install (file names and sizes) with the install; if they
match it logs `[GUO] art set mounted` and reads art from the pages, and if they do not (you patched or reinstalled the
client) it logs one warning naming the reason and reads the original files. Run the export again after an update.

`UO_ART_SET_CACHE_MB` (default 256, at least 16) caps how much decoded page data the client keeps in memory.

A shard's own override files in the client folder (`Art/Statics`, `Art/Land`, `Gumps`) still win over the set, and a
store pack still wins over both.

## Turn it off and delete it

Start without `--art-set` (it is off by default). To remove the files, delete the folder `python tools\art_extract\run.py
where` prints. Nothing else needs cleaning up.

## Numbers

Measured on the development install, one machine, one run each; yours will differ with the disk and the install.

| | |
|---|---|
| Images in the set | 52,034 (4,244 land, 38,286 static, 5,378 gump, 4,071 texmap, 55 light) |
| Animation blocks | 161,394 (1,136,902 frames) |
| Size on disk | 1,360 MB in 1,051 pages: 142 MB for the art classes, 1,218 MB for the animations |
| Export, everything | 793 s (the animations take 557 s of it); `verify` 712 s, every id and block matching |
| Client start, set on | mounting takes about 0.1 s (108 to 152 ms over several runs); the start is about 0.1 s slower |
| Memory, normal play | no growth over a 10-minute run with animations (+2.5 MB) |
| Memory, worst case | reading all 8.8k static ids in a row: 1.3 GB working set with the set (256 MB page cache), 0.56 GB without; 9.2 s against 4.0 s |

The worst case is a stress sweep of art ids in order, not play. The set does not make the first sight of an image
faster than decoding it; what it gives is a plain, checked, inspectable copy of the art, and the first step towards
running without the original art files, which is not yet a promise (story AX5 measures what still reads them).

## Is it the same picture?

Yes, to the pixel: `verify` proves the pages hold what the exporter decodes, and `ArtSetParityProbe` runs every id (and
every animation block the client can reach) through the client's own loaders with the set on and off, which found no
difference. Scenario runs of the login screen and the paperdoll with the set on produce the same frames as with it off. A walking character in Britain looks the same with the set on and off, pose for pose; because animation follows the clock, two runs never show the same instant, so that comparison is by eye and by "no more different than two runs with the set off", while the block-by-block probe is the exact one.
