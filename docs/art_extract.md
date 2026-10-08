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

## Can the art files go? (AX5 measurement)

Measured once, on the development install, with the full set mounted (`--art-set`) and the install's art files pointed
at nothing through the files-override map (`--files-override`, entries for the art, gump, texmap, light and animation
files). Report only; no loader was changed. Hiding one class at a time:

| Hidden | Result | Where it stops |
|---|---|---|
| `artLegacyMUL.uop` | client does not start | `ArtLoader.Load` leaves `_file` null and calls `_file.FillEntries()` (`src/Assets/ArtLoader.cs:62`) |
| `gumpartLegacyMUL.uop` (and `.mul`/`gumpidx.mul`) | client does not start | `GumpsLoader.Load` opens `gumpart.mul` (`src/Assets/GumpsLoader.cs:62`, DirectoryNotFound) |
| `texmaps.mul`, `texidx.mul` | client does not start | `FileSystemHelper.EnsureFileExists` in `TexmapsLoader.Load` (`src/Assets/TexmapsLoader.cs:26`) |
| `light.mul`, `lightidx.mul` | client does not start | `EnsureFileExists` in `LightsLoader.Load` (`src/Assets/LightsLoader.cs:25`) |
| `AnimationFrame1-6.uop` | **works**: login scenario passes, world draws, no errors | the loader skips a missing UOP (`File.Exists` guard, `AnimationsLoader.cs:73`); bodies then resolve through the mul index |
| `anim*.mul`, `anim*.idx` | starts, then the world does not draw | `AnimationsLoader.GetIndices` reads `_files[fileIndex].IdxFile` (`src/Assets/AnimationsLoader.cs:335`) for every mobile each frame; the exception aborts `World.Update`, so the in-world still is one grey colour (the scenario still passes: its checks are scene and position) |

Why the art loaders still need their files even though the set answers the pixels: the entry tables (`Arts.File`,
`Gumps.File`, `Texmaps.File`) are the client's record of which ids exist and how big they are, and they are read
without going through the content seam in `UOFileManager` (verdata patching and art.def, lines 211-224 and 407-414),
`Land.cs:97`, `ItemView.cs:479,489`, `StaticView.cs:114`, `View.cs:116,199,232,240`, `MultiView.cs:131`,
`ChunkMesh.cs:533`, `LightningEffectView.cs:29`, `AnimatedStaticsManager.cs:75` and `StoreRuntimeContent.cs:220`.

So today only the `AnimationFrame*.uop` files can be absent. Hiding the rest needs the index tables served from the set
(the set's `index.json` already holds the ids) or a small stand-in for `UOFile`; both touch verbatim-tier loaders, which
is for guo-director to decide. The animation mul indexes are the cheapest of the rest (one lookup). Not measured: that the
mobiles drawn with the UOP files hidden are pixel-identical (AX3's block parity is the proof), and the mul data files
alone (`anim*.mul` without `.idx`).

## For shard owners: an encrypted container (AX6)

This part is for a shard that ships its own custom art; a player's own extracted set above stays plain. `art_extract
pack-container` seals a shard's art into one `<shard id>.guoart` that the client opens only if the player's profile holds
that shard's key (delivered inside the shard's signed pack). It keeps the art from being copied off a disk by someone
without the key. It does **not** stop anyone from capturing what the game draws, and a player with the key can read the
art, so treat it as a lock on the door, not a vault. Without the key, or with a wrong one, the client reads the original
files and logs one warning that names the shard. Details: `docs/data_formats.md` section 36.
