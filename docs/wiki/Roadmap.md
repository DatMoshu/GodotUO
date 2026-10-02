# Roadmap

Where GUO is heading. This is a direction, not a schedule: items move as they
are measured and proven. Decisions that bind are in the ADRs
(`docs/architecture/`); open work is in GitHub Issues.

## Now: the first public preview (v0.1.0)

- Downloads for Windows, Steam Deck and Android from one release workflow.
- The [Known Issues](Known-Issues.md) list kept honest.
- The wiki, the project site, and a welcome in Discussions.

## Next

**Performance, the one parity gap.** Frame times on every platform are measured
with one probe, scene by scene. Draw-call cuts land only at exact parity, and
ADR-0007 (the sorted world in batched meshes) is decided with numbers. A fair
ClassicUO-vs-GUO table, with the same view on the same machine, is published
before any comparison is claimed.

**Mobile.**
- The command bar redesign: one bar with rows that slide in.
- Touch-sized Modern versions of the gumps that are hard to use on a phone
  (ADR-0024).
- Controller polish on handhelds.

**Web.**
- Choosing your UO folder in the browser.
- A smaller download and a faster start.
- A hosted test client, once the engine side allows it (ADR-0008).

**Editor and Store.**
- Theme, sound and preset packs applied, not just installed.
- A hosted Store catalogue on GitHub Pages.
- The RunUO shard backend for the editor.

## Later

- New content, authored the safe way: multis, interiors and art in a staged
  data set, never the install ([Building Multis](Building-Multis.md),
  [Author UO Data](Author-UO-Data.md)).
- Steam Deck Game Mode and more handhelds.
- Upstream ClassicUO changes merged as they land
  ([Parity and Drift](Parity-and-Drift.md)).

## How to help

Pick an issue labelled `good first issue` or `help wanted`, or bring an idea
to the Ideas category in Discussions. See [Contributing](Contributing.md).
