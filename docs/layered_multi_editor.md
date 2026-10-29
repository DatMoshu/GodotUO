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
