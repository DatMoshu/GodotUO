# Layered multi editing — first milestone

Layer: L3 editor plus Tools. Manual editing works with AI disabled. Chat/MCP
uses the existing AiFeatures lifetime, tool approval and editor-thread dispatch.
Native multi geometry/export semantics are unchanged. The renderer signal-lifetime
fix below supports editor assembly reload.

The Layers tab supports named layers, active drawing layer, selection, assigning
selected components, visibility, locks, movement and tile replacement. Visibility
is an editing aid: it never changes Shown/native flags or excludes components
from export. UO depth sorting, not panel order, controls drawing.

Geometry and metadata edits are history transactions. Locks protect components
through existing drawing/erasing/moving/generator paths: touching a locked
component refuses the whole edit. Undo restores geometry and metadata.
Active layer and selection are editor context, not undo steps.
Rotation preserves layer membership and refuses ambiguous mappings.

## Saved format
Existing format-1 components JSON remains readable. New documents add:

    "editor_layers": {
      "version": 1,
      "layers": [{"Id":0,"Name":"Foundation","Visible":true,"Locked":false}],
      "membership": [0,0,0]
    }

Membership follows component array order. IDs are document-local unique
nonnegative integers; names are 1–80 characters; maximum 256 layers.
Invalid membership/version refuses opening without replacing the current
document. Legacy files open with one Components layer. Save/recovery writes
a full temporary document and renames it into place. Native export flattens
components; MUL/UOP multi records cannot retain layers or hues.

## Shared manual and AI operations
Read multi_document for Revision, layers, ActiveLayerId and selection_key.
Call multi_layer_preview with expected_revision and action; inspect the affected
count, then call multi_layer_edit with the same arguments. The edit requires
GUO approval. Neither tool starts inference.

Actions: create/rename (name), visible/lock (value), activate/select, assign,
move (dx/dy/dz), replace (tile/replacement). Except create, supply layer_id.
Assign also requires selection_key from multi_document, detecting changes to
manual selection. Stale revisions are rejected; read and preview again.

Manual and AI commands call MultiDocument.EditLayer. Replacement is literal
tile-ID replacement, not semantic material-family retiling. Validate afterwards.
No arbitrary code is accepted.

## Tavern fixture
With configured external UO_BUILD_DIR:

    python tools/multi/run.py mine --no-statics
    python tools/multi_layers/tavern.py

Recipe: tools/multi/examples/layered_tavern.json. This original demonstration
uses locally mined UO families, not a claimed Zomboid reconstruction.
Outputs under UO_BUILD_DIR/multi:
- edit/layered_tavern.multi.json: seven-layer editable document.
- built/layered_tavern: components, functional-door/tour sidecar, provenance,
  roof-on, roof-off and ground-floor previews.

The 12 × 10 tavern has two storeys, upstairs rooms, staircase, slate roof,
furnished hall and fenced garden. Floors use one coherent board tile.
Open the description in Multis; hide or move Roof, lock Foundation, or select
Furnishings. Undo restores the document. Stage/prove through canonical multi
tools on an owned private shard; preserve the built door/tour sidecar.

## Verification
Model tests, with external output paths:

    dotnet run --project tools/multi_layers/MultiLayers.Tests.csproj -p:BaseIntermediateOutputPath=EXTERNAL/obj/ -p:OutputPath=EXTERNAL/bin/

Set GUO_LAYERED_MULTI_FIXTURE to the generated JSON, then run
tools/editor_smoke/run.py --headless with a fresh external --out.
GUO_LAYERED_MULTI_ONLY=1 runs only the focused proof; omit for the full suite.
Use --windowed for an editor capture. Checks exercise the manual button signal,
AI preview/apply/stale refusal, undo, viewport hiding and save/reopen.
Automatic tool approval is confined to that smoke fixture, not a preference.

Hierarchical groups, masks, parametric roofs, database retrieval, source-room
preservation and AI synthesis remain later work. This foundation supplies their
layer identities and editing operations. Custom-house legality warnings and
private-server multi validity are distinct checks.


## Renderer reload lifetime
The windowed proof also boots World before editing the tavern. This exposed a
frame_pre_draw delegate left connected during assembly serialization, followed
by stale callbacks and a shutdown access violation. OrderedWorldCapture now
owns a native object/method signal relay and frees it during disposal, so
teardown can disconnect the exact connection even as managed delegates reload.
This does not change draw ordering or the GPU pass.

## Database-driven building generation

Run `python tools/multi_layers/database.py --help`. Supply `--db` with the
shared building census, `--decor-db` with the separate mined decor database,
`--source` with the local Project Zomboid install, `--source-id` with an exact
census ID, `--name` with a safe filename, and `--out` with a new external folder.
The initial adapter accepts exactly two storeys. It reads current snapshots in
SQLite read-only mode, checks the room table against building analysis and
verifies the source header hash before resolving the layout. Missing mappings
or native movement failures stop generation; nonempty output is refused.

The canonical layout importer, furniture placer, roof/yard builders, validator
and native writer remain the implementation. Output includes an editable
`NAME.multi.json`, native components/sidecar, source layout, previews, room
plans and generation report. The seven layers are Foundation, Yard, Tavern hall,
Upstairs rooms, Stairs, Roof and Furnishings. There is no model/API request in
this deterministic compiler. Open the generated document in Multi Edit for
manual controls or the existing approved AI tool operations.

An optional `generation` object survives document saves and recovery, contributes
to dirty-state tracking and is cleared for a new document. It records snapshots,
source room rectangles, verified source provenance, input/geometry hashes and
explicit theme adaptations. `multi_document` exposes source ID, snapshots and
source rooms to AI tools. Room masks are immutable original-generation evidence,
not constraints that follow later edits. Native MUL/UOP exports carry geometry,
not this metadata; keep the layered JSON as the editable source.

The yard and gable roof are authored adaptations, not claimed reconstructions of
the source parcel/roof. Modern wash fixtures may use recorded native visual
substitutes. Generated furniture is limited to existing complete native templates;
this milestone does not create new artwork or plumbing mechanics.

## Wall-family joins

The layout importer's default stone theme uses the wall-only `rubble_stone`
set in `tools/layout_import/wall_styles.json`. It binds straight runs, windows
and corners to one visually checked native family. The west corner uses its
capped end and the south corner uses the family's two-face piece. Mined neighbour
frequency alone can misclassify end pieces and mix similarly named stone sets.
A material override does not inherit the stone set; another explicit matching
set can be supplied through `wall_style`. These wall-only definitions are kept
out of the complete-house preset picker. Existing saved multis are unchanged
until regenerated. Tests cover the four corners, runs, windows, material override
and height mismatch; visual review remains necessary for new wall sets.
