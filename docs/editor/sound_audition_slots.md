# Sound audition and empty slots

F14f browsing/audition, L3 manual editor tools, compiled only with `TOOLS`.
The Effects browser offers occupied, all and empty slot views, library-wide
counts, a next-empty action and loop audition. No insertion, removal or native
sound archive writer is included.

## Occupancy and navigation

The existing GUO Effects browser covers **0x0000–0x0FFF**, 4,096 IDs. Occupied
means the active loader returned nonempty PCM at that ID during the library
scan. This includes loader aliases and configured loose-file sources. Empty
means no playable effect in that active library; it is not a promise that an
underlying native archive record is available for insertion. Project edits to
occupied effects do not change the counts. Search and sorting do not change
the library-wide occupied/empty totals.

The all-slots view retains every ID, and the empty view filters out occupied
ones. Empty rows have a read-only inspector, no sound document, and no play,
WAV import/export or rename actions. Next empty selects the next empty numeric
ID after the current selection, clears the search and uses ID order. It enables
the all-slots view when necessary and never wraps at the end of the range.

Standard-file export scopes contain occupied effects only. The review displays
occupied/total counts for browser and selected scopes, and explains that empty
slots are excluded. An empty-only browser/selection has no exportable plan and
cannot fall back silently to exporting the entire library.

## Loop audition

The theme-native loop icon and the inspector/context action loop the current
working PCM, including unsaved project PCM. Click Loop again, Stop, or Escape
to end it. The visible status names the ID and source/project PCM. Selecting
another effect, leaving Sounds, switching to Music, changing the working
document or tearing down/reloading the addon stops the audition. Music retains
its existing configured playback behavior and has no effect-loop control.

The editor uses its owned Godot WAV stream with forward looping from sample
zero through the complete sample count, mono 16-bit at 22,050 Hz. It creates no
background thread and changes no runtime sound/player types. Ordinary source
playback still uses the client's audio; ordinary project preview remains a
single shot. Streams and players are released on addon shutdown.

## Reference and verification

UoFiddlerPixel commit `10f69b6f199f049335e89f7832a7723b274198d5`,
`SoundsControl`'s loop, free-slot and count operations, is the reference.
GUO retains its existing 4,096-ID browser range and computes its full complement
rather than copying Pixel's inconsistent hardcoded 0xFFF/0xFFE count constants.
No claim is made for native insertion/removal or physical archive-slot parity.

`python tools/editor_smoke/run.py --reload` checks the occupied/empty partition,
ID navigation, empty inspectors, export scope refusals, exact source/project PCM,
loop bounds, stop paths, ordinary single-shot behavior and player disposal.
The authorized windowed workbench proof captures looping after multiple short
cycles at 1,920, 1,366 and 1,024 widths and the empty-slot inspector.
[Scenario recipe](../../tools/scenarios/fiddler-f14f/README.md).
