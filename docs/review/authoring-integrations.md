# Pixelorama and UO Anims integration review plan

Status: proposed implementation sequence, based on local source inspection on
2026-11-10. This plan does not change runtime behavior or supersede accepted ADRs.
Use the [section map](sections.md) and [readiness contract](contract.md).

## What is present

| Surface | Observed implementation | Gap to the requested experience |
|---|---|---|
| Pixelorama main tab | [Plugin](../../godot/GUO/addons/guo_editor_pixelorama/GuoPixeloramaPlugin.cs) reports a main screen named Pixelorama; enabled in project defaults | Persistent main-strip space; proposed on-demand menu has not been implemented |
| Pixelorama host | [PixeloramaView](../../godot/GUO/addons/guo_editor_pixelorama/PixeloramaView.cs) uses Windows native-window hosting and an extension preparation subprocess | Missing executable returns a fetch command through status reporting; no complete first-use setup workflow in that view |
| Pixelorama exchange | [Tool documentation](../../tools/pixelorama/README.md) describes pinned fetch, extension, sheet/VD exchange and save checks | Documentation explicitly says embedded painting, undo and keyboard input still need an artist pass; prior scripted saves do not cover that |
| Fit Lab | [SpriteMotionDock](../../godot/GUO/addons/guo_editor/Docks/SpriteMotionDock.cs) hosts the shared web lab; `GuoEditorPlugin.Build` adds the dock | It is a host, not a complete native port or complete UO animation workflow |
| Fit Lab installer | [run.py](../../tools/spritemotion/run.py), `install_cef`, requires the hash-matching `runtime/gdcef.dll` before installation | That locally built library is ignored and not supplied by the public repo; clean-clone setup stops without a separate build step |
| Fit Lab content | [ADR-0033](../architecture/ADR-0033-spritemotion-fit-lab.md) and [README](../../tools/spritemotion/README.md) require a prepared model/catalog | Blender setup, permitted models, raw-asset preparation, staging and in-game equip proof remain incomplete |
| Existing animation editing | [Animation workspace](../../godot/GUO/addons/guo_editor/Animation/AnimationWorkspace.cs), AnimationDocument, AnimationPanel and AnimationOverlay | Reuse/integration must be assessed before adding another editor or document format |

These are concrete review inputs, not a fresh runtime certification. No external
Sprite Lab source was inspected for this first inventory; its full capability set
must be established before estimating or claiming a full port.

## Pixelorama: GUO > Extra Tabs > Pixelorama

Use the upstream spelling **Pixelorama** in labels, search and documentation.
Proposed navigation is a GUO menu with an Extra Tabs submenu that opens/focuses the
Pixelorama workspace on demand. Evaluate the menu's integration with the pinned
Godot editor API before implementation; do not depend on brittle private UI-node
positions. Keep this separate from a cosmetic string shortening.

Retain “Edit in Pixelorama” at the selected asset. Both routes should use the same
session owner and the same PNG/sidecar exchange. Opening a menu entry twice focuses
the existing workspace; opening a different asset must not silently replace an
unsaved document. Persist layout using stable identifiers, and migrate the old
main-screen selection. Detach, editor reload and plugin disable must preserve work.

Out-of-box means the first useful workflow needs no maintainer directory or manually
prepared installation. A valid prerequisite is the user's own UO data. Optional
software acquisition must show what will be installed, size/location, progress,
cancel/retry and a readable failure state. Verify pinned downloads; preserve existing
Pixelorama preferences/extensions. An already installed tool should open directly.
Offline users need an actionable retry/local-install route. Do not start downloads
or a native child window just because the editor started or the menu was built.

Acceptance on each supported host mode:

1. Fresh configuration -> acquire/detect tool -> open a synthetic fixture or permitted
   local asset -> edit pixels and use undo/redo and keyboard shortcuts.
2. Save back -> validate dimensions/transparency/metadata -> import overlay -> observe
   the result in GUO -> close and reopen the same document.
3. Exercise missing executable, failed/cancelled setup, invalid save, multiple assets,
   DPI/resize, tab switch, detach, reload and editor close with unsaved work.
4. Confirm hidden/closed cost and exported-client exclusion. Test standalone fallback
   separately; Windows embedding evidence does not establish Linux/macOS support.

Do not expose the new route as ready until these pass. A menu under Extra Tabs does
not make an incomplete workflow acceptable on main.

## UO Anims: one workspace, staged integration

**UO Anims** is the proposed short main-workspace name. Suggested internal tasks:
Browse/Frames, Sprite, Fit, Preview and Stage. These labels are provisional until
tested against real artist tasks; unavailable modes should not appear as working
buttons in normal main workflows.

First inventory the actual Sprite Lab and Fit Lab source at a pinned, publicly
obtainable revision. Record each control, input/output format, saved setting,
algorithm, undo behavior, preview, render job and failure path. Map each capability
to existing GUO code, reusable portable logic, an external worker or a missing port.
Include licensing/provenance and a permitted synthetic fixture. Do not substitute
“a web page loads” for parity with either lab.

The target is one coherent user workflow. Whether every control must be native Godot
is a separate architectural choice: accepted ADR-0033 currently chooses a shared
hosted UI. A native rewrite must explicitly supersede that decision with its cost,
parity matrix and maintenance plan. Meanwhile, dependency/bootstrap and artifact
contracts can be reviewed without prejudging the UI backend.

| Slice | Scope | Acceptance before normal exposure |
|---|---|---|
| A: capability and dependency inventory | Pin both labs; map existing GUO animation operations; document worker/browser/model requirements | Every required capability has an owner and test; no private fixture is the only proof |
| B: public bootstrap | Resolve browser patch distribution or reproducible build, Python isolation, Blender doctor, catalog/model import and paths | Clean-machine setup and cancel/retry/offline recovery; core editor works without optional dependencies |
| C: workspace and documents | Shared selection, body/action/direction/frame metadata, origin, dirty state and reopen; manual Browse/Frames first | No duplicated document state; save/undo/reload parity; main-strip layout remains usable |
| D: Sprite and Fit workflows | Bring over each inventoried operation, fitting transforms, equipment layers, job state and previews | Matching source-lab results using the same fixture; cancellation/reconnect; no loss of saved fits |
| E: finished artifact handoff | Validate sheet/frame counts, centers, directions, palette/alpha, IDs and provenance; use overlay/uopack contracts | Render -> review -> explicit stage -> readback -> in-game playback/equip result; source install untouched |
| F: promotion | Representative performance, visual/input pass, docs and exports | Scoped READY verdict; editor/browser/worker dependencies absent from exported clients |

Each slice should be a reviewable change, not a single full-lab transplant PR. Existing
local-checkout overrides can remain optional developer conveniences. They must not
be the default route or a prerequisite for normal users or coding agents. Use GUO
product labels in the workflow while retaining third-party licenses and provenance.

Review shared-service ownership explicitly: closing a GUO view must not kill another
session's render job; cancelling a job requires an identified owner. Save completion
does not automatically install packs, mutate a shard or publish content. AI generation
uses `AiFeatures`; manual fitting and animation editing stay available with AI off.

## Immediate review priorities

1. R21/R09: remove private onboarding prerequisites and inventory all normally exposed
   unfinished surfaces, including the current Fit Lab registration.
2. R17: prove Pixelorama first-use and actual artist interaction before changing its
   navigation. The proposed menu and setup UX can then land as one bounded feature.
3. R18: resolve the browser-binary and model/catalog setup gaps, then choose the UO
   Anims backend through a documented ADR update if required.

No runtime containment, menu move, native lab port or new CI gate is implemented by
this plan. Those are follow-up implementation tasks with the acceptance criteria above.
