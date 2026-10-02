# Pack content policy

This policy applies to the official GUO catalogue (`GodotUO-packs`) and is the
recommended default for any other catalogue. ADR-0026 section 6 is the
decision behind it.

## What a pack may contain

- **Original work.** Art, sound, music, maps, scripts and text that you made,
  or that you have the right to publish under the licence you declare.
- **Openly licensed work by others.** It must be under a licence on the
  allowlist (`CC0-1.0`, `CC-BY-4.0`, `CC-BY-SA-4.0`, `MIT`, `BSD-2-Clause`,
  `BSD-3-Clause`, `Apache-2.0`), credited in `LICENSE.txt`.
- **Data that refers to the client's content by number.** A map block that
  places static 0x0001, a loot table naming an item graphic, or a tiledata row
  for an ID is fine. The numbers are not the art.

## What a pack may not contain

- **Anything from the Ultima Online client's files.** That covers art, gumps,
  animations, sounds, music, maps, cliloc text, tiledata tables and fonts,
  whether the file is renamed, converted to PNG or WAV, or repacked.
- **Work derived from those files.** Recolours, edits, paint-overs, upscales,
  re-renders of the client's art, and AI image-to-image or style transfer
  from it are all derived work. A pack that redraws a client sprite from
  scratch in its own style is original. A pack that starts from the client's
  pixels is not.
- **Other games' assets,** ripped or "for fan use", unless their licence is
  on the allowlist.
- **Executable code** of any kind outside the script formats the format allows
  (`.razor` command scripts, run only by GUO's own interpreter under the
  player's approval). No DLLs, no server code, no native binaries.
- **Malicious or abusive content,** and anything that targets a real person.

## What a submission declares

Each catalogue entry states the pack's `licence` and a `provenance` line: how
the content was made ("drawn in Aseprite", "modelled in Blender and rendered",
"generated with <tool> from a text prompt, no image input"). A reviewer may ask
for source files.

## Scripts and shards

Many shards forbid automation. A script pack is reviewed for what it does,
and its approved abilities are shown to the player before it runs. Shard
rules still apply to the player. A shard can say in its content descriptor
that it does not allow script packs, and GUO honours that (ADR-0026 section 4).

## Takedown

Report a pack that breaks this policy, or infringes your rights, through the
catalogue repository's issue form or the contact in its README. The pack is
delisted while it is reviewed. Delisting removes it from the index, and every
mirror the catalogue controls deletes the file. A client that already has the
pack keeps it until the player removes it. Repeat infringers lose their
listing rights.

## Names

The store and its packs are "GUO packs". Nothing in a pack or its listing may
claim to be official Ultima Online content, or use Ultima Online logos.
