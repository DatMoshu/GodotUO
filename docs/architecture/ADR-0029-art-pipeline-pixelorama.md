# ADR-0029: The art pipeline: a Pixelorama fork, image services, provenance

## Status

Accepted — 2026-10-02 (the owner chose Pixelorama).

## Date

2026-10-02

## Decision Makers

The owner, relayed by the director (GUO-Director).

## Summary

GUO's pixel editor is a **fork of Pixelorama**, kept under its MIT licence in
its own repository. It shares GUO's engine, Godot 4.7, so it can open as a
tab in the GUO editor as well as run on its own. UO-specific tools are added
to the fork, as a Pixelorama extension where the extension API allows and in
the fork itself where it does not. For photo-style edits, **Pinta** (MIT) runs
beside it as an external tool.

The editor also gets windows for **ComfyUI**, **Retro Diffusion** and other
image services. Everything they make enters the asset overlay (ADR-0020) with
a provenance record.

## Context

- The owner wants a free and open-source editor with full pixel-art abilities
  that keeps GUO's licensing permissive.
- Aseprite is under an EULA, and LibreSprite, Krita and GIMP are GPL.
- Pixelorama (MIT, Godot 4.7) has layers, clipping masks, non-destructive
  effects, a frame timeline with onion skinning and tags, palettes, tile mode,
  and an extension API that reads and writes cels and palettes and adds menus.
- Graphite (Apache-2.0) is the closest to a Photoshop-like editor, but its
  raster editing is not yet on by default. It is tracked for later.

## Decision

1. **The fork** lives in its own repository (MIT; upstream's copyright and
   licence kept). In GUO, `tools/pixelorama/README.md` records its version
   and origin. A build is fetched or built into a gitignored folder, the same
   way as `tools/godot`.
2. **The UO tools** added to the fork:
   - UO hue palettes, read from the user's own `hues.mul` at run time and
     never shipped.
   - Templates for the land diamond (44×44), statics with their foot line,
     gumps, and animation frames with their centre point.
   - A live hue preview.
   - Checks against UO sizes and transparency.
   - "Save back to GUO".
3. **Integration with the GUO editor:**
   - "Edit in Pixelorama" on any asset writes a PNG and a sidecar JSON to a
     watched folder and opens the editor.
   - On save, GUO validates the image and imports it into the asset overlay
     (ADR-0020).
   - Hosting Pixelorama as a tab inside the GUO editor is a spike. It
     depends on how much of its autoloads and project settings an addon can
     carry. A separate window is the fallback, and works first.
4. **Image services** share one interface:
   - ComfyUI: API-format workflows from `/prompt`, with progress over its
     websocket.
   - Retro Diffusion through its HTTP API.
   - Others as they come.

   Keys are stored as ADR-0028 decides: the operating system's store, never
   a project file.
5. **Provenance:** every imported image records its tool, model, workflow,
   seed and inputs. When an input was client art, the image is marked
   **derived from client art**, so the store's content policy can refuse it
   for publication. Such images stay local.
6. **A shared UO post-process** step: palette and 16-bit reduction, the
   transparency key, trimming to UO sizes, and the isometric masks.

## Consequences

- GUO stays BSD-2-Clause with no copyleft dependency. The fork stays MIT.
- Rebasing the fork on upstream Pixelorama releases is ongoing work.
  Keeping the UO tools in an extension wherever possible keeps it small.

## Validation

To do:
- A static round trip: GUO to Pixelorama, a scripted save, then back into
  the overlay and drawn in the World tab.
- One ComfyUI run against the local server with a small workflow, imported
  with its provenance.
