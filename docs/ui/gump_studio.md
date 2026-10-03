# Gump Studio

Open `launchers/editor/open_project.bat` and choose **UO Gumps**. This workspace
authors separate UI documents; the UO installation and upstream source remain
read-only. Documents normally live in the world project's `gumps/` directory.

## Authoring

- **New Classic / New Modern** starts a window with a heading and reply button.
- Add elements from the palette or browse **UO Assets → Gumps** in the bottom
  shelf. Search by client gump name, common art name, decimal ID or `0x` hex ID;
  press Enter to search. Double-click artwork to insert it, or select it and
  choose **Add to layout** / **Use for selected element**.
- **Browse gump art** and the inspector's **Choose gump art** buttons open a
  searchable thumbnail picker. The workspace has resizable panes, wrapping
  toolbars and scrolling properties/library panels for smaller windows.
- Select layers or click the canvas. Shift-click selects multiple elements.
  Drag to move; drag the bottom-right handle to resize. Snap uses eight pixels.
  Arrows nudge one pixel; Shift-arrows nudge eight. Layers can be locked or hidden.
- Duplicate, delete, reorder, align left/top, undo and redo operate on selection.
  Shortcuts: Ctrl-D, Delete, Ctrl-Z, Ctrl-Y/Ctrl-Shift-Z, Ctrl-S, Ctrl-Shift-S.
- The inspector edits geometry, text, art IDs, page, button/switch/entry IDs,
  radio group, text length, modern colors, font sizes, anchors and binding keys.
- Deselect, or choose **Document properties**, to edit the name and canvas size.
  Zoom ranges from 50% to 200%; the canvas scrolls for larger layouts.
- Page zero is shared. Other pages display only on the chosen page.
  **Interact** enables native controls; page buttons change pages, and reply
  buttons print the collected response below the canvas without sending packets.
- Save creates a `.gump.json` document. Recovery saves on workspace teardown,
  including assembly reload; it is **not** crash-time autosave. Undo history and
  the original save path are not retained across assembly reloads.

Try `godot/GUO/assets/ui/examples/quest.gump.json` for a two-page modern form.

## Loading existing gumps

There are three different sources:

1. **Art:** browse any gump image supplied by the configured client installation.
   An image is one building block, not a complete interactive window.
2. **Shard layout:** paste `{ command arguments }` data and its JSON text array
   into **Import layout**, or open a `.classic.json` export bundle. Page/group,
   gumppic, tiled art, resizepic, text, croppedtext, HTML, button, checkbox, radio,
   textentry and limited textentry commands have editable representations.
   Unknown, malformed, or extended commands are retained as raw commands with
   notices. The original text table is retained so their indices remain valid.
3. **Client C# gumps:** **Load client gump** offers searchable offline previews
   of Status (classic/modern), Paperdoll, Journal, Skills, Options, Chat, Party
   and Profile. Double-click a preview to load its editable control snapshot.
   These use the editor's offline character, so values differ from live play.
   For other gumps or actual game state, open them in the running client and type
   `-gumpcapture` in its chat input. This local command writes snapshots to
   `user://gump_studio` (the absolute directory is printed in the client and
   shown by **Load client gump**). Open the resulting `.gump.json` in the editor.
   Captures also appear directly in the dialog; use **Refresh captures** after
   capturing. **Also show gumps requiring a live capture** lists the remaining
   client types with instructions instead of attempting unsupported constructors.

Captures walk the live hierarchy, so the gump must be open. They flatten visual
coordinates and retain control paths/types and gump serials. Text-entry values
are deliberately omitted. Custom-drawn controls, item pictures and unrecognized
controls retain selectable bounds without painting over captured child art;
their original behavior is not serialized.

To try geometry edits on the original gump, save the document back into
`user://gump_studio` and run `-gumpapply filename.gump.json`. It updates bounds
and visibility of the matching open instance without replacing callbacks or
changing form values. Paths/types/serials are checked before any changes; a
changed hierarchy requires a fresh capture. Added/duplicated controls cannot
be applied this way. Reopen the original gump to discard these transient edits.
Some dynamic controls may recalculate their own bounds on the next update.

## Classic output

**Export → Classic layout bundle** writes `{ "layout": "…", "texts": ["…"] }`.
Pass these to the shard's existing gump API. The export includes pages, radio
groups, button action/target/reply IDs, switches and text entries. Unsupported
modern controls refuse classic export rather than silently disappear.

Classic preview is an authoring approximation, not a pixel-parity renderer:
resizepic uses the nine local art pieces, but native text metrics, checkbox
appearance and HTML presentation differ. Hues are preserved for classic text
export but are not applied in the native preview. Extended gumppic hue commands
remain raw. Plain classic images use their native size on import; resizing their
editor box cannot change the classic protocol's fixed image size. Modern color,
anchor, binding and font properties do not exist in classic output.

## Modern runtime integration

**Export → Godot scene** writes a `.tscn` using `AuthoredGumpView.cs` in this
project. The scene rebuilds its native Godot controls from the embedded document.
It needs this project's script; it is not a standalone asset for other projects.
The document remains the editable source, rather than generated scene children.
Local art resolves from the running client's loaders, with nearest filtering.

`AuthoredGumpView.Reply` emits a `GumpReply`: button ID, selected switch IDs,
and `(ushort entry ID, string text)` entries from **all pages**. Page buttons
only navigate. Radio buttons are exclusive within their group. The host owns
state and decides what actions to invoke; the view sends no packets itself.

Game code in this assembly can use the existing Modern input/lifecycle host:

```csharp
var document = GumpDocument.Parse(File.ReadAllText(path));
var host = ModernAuthoredGump.Show(world, document, reply =>
{
    // sender and gumpId must come from the matching, active server dialog.
    GameActions.ReplyGump(sender, gumpId, reply.ButtonId,
        reply.Switches, reply.Entries);
});
host.View.ApplyBindings(new Dictionary<string, Godot.Variant>
{
    ["player.name"] = world.Player.Name,
    ["player.health"] = 75.0
});
```

Binding keys are plain lookup keys, not expressions or executable scripts.
They update labels, rich text, button captions and progress bars. The host must
refresh them when state changes. Modern anchors support top-left/right,
bottom-left/right and stretch; the host scrolls long content. Layout containers,
arbitrary scene widgets, animation timelines and nested visual grouping are not
implemented yet.

For an in-game preview, save into `user://gump_studio` and run
`-gumpopen filename.gump.json`. It displays replies locally. Desktop input is
captured only while that explicit preview is open. Incoming server gumps and
the automatic Modern registry remain unchanged (ADR-0024).

## Format and verification

Version 1 JSON uses camelCase properties. `GumpDocument` and `GumpElement` in
`src/UI/Authoring/GumpDocument.cs` define the schema and defaults. Element IDs
are unique document identities; they are distinct from reply IDs. Coordinates
are signed pixels, dimensions positive pixels, pages/groups/art IDs unsigned
16-bit ranges, entry IDs unsigned 16-bit, reply/switch IDs nonnegative 32-bit.
Source capture fields are diagnostic identity, not authority to execute code.

Run `python tools/gump_studio_smoke/run.py --reload` for focused headless checks,
or `python tools/editor_smoke/run.py --headless --reload` for the complete editor.
The targeted checks cover import/export preservation, validation, undo/redo,
cross-page replies, radio groups, responsive anchors, display bindings, loading
an exported Godot scene, capture/apply mismatch handling, all nine offline client
previews, art searches, and a canvas drag. With desktop-owner agreement, add
`--windowed` to the focused command to capture the actual workspace at 1600×1000
and 1280×900 and check inspector and asset-shelf visibility.
They do not prove a live shard round trip or visual pixel parity.
