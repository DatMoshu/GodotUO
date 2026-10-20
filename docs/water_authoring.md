# Water authoring

Open **UO World → Water** in the left settings tabs, beside Tools and Advanced.
The workspace is a manual editor feature (L3, `#if TOOLS`), available with AI
disabled. It authors the existing `guo/water@1` Store component from
[data formats, section 44](data_formats.md).

## Start with a preset

Choose a starter and press **Load**. UO+, Pixel, Cel and Realistic demonstrate
the models; Calm harbor, Windy coast, Dark swamp, Gentle river and Stormy sea
are editable starting points. These names describe their look. Gentle river
does not add directional current, and Stormy sea does not change the weather.
Loading a preset is reversible with **Undo**. **Reset preset** returns to the
last loaded or saved state. **Duplicate** starts a separate named draft.

The synthetic preview uses the client's actual water shader over original
generated texels. It needs neither an install nor a shard. Choose open water,
a low beach or a high cliff, then daylight, dusk or night. **Compare** shows
original synthetic texels on the left and the style on the right. **Classic**
temporarily bypasses styling, and **Freeze** holds water time and the wave
field. **Test splash**, **Moving hull** and **Clear waves** exercise the ripple
simulation. The moving hull is a synthetic disturbance, not a live boat test.

## Tune and see the result

The controls edit name, model, deep-water tint, speed, inverse wave scale,
wave strength, glints, palette levels, pixel size, shore foam and width,
ripple strength and wave retention. Labels can be scrubbed like other numeric
controls in the workbench. All values use the runtime validator's bounds.
Higher wave retention makes disturbances last longer. Zero glint suppresses
highlights. UO+ preserves native texels and uses shore foam as a subtle lap;
its model does not use a replacement tint or glints. Pixel and Cel retain
their characteristic bands even when palette levels is zero.
The model description explains these differences in the panel. Controls unused
by the selected model are dimmed and read-only; switching models preserves
their values. Realistic does not use palette levels, while UO+ also leaves
tint, glints and wave scale unused. Cel uses shore foam as an on/off surf switch.

Use **Keep reference** before tuning to retain a snapshot of the current draft.
It enables **Compare**: the left half shows the reference, and the right half
shows your current draft. Both halves use the same scene, lighting, clock and
splash/hull inputs; each retains its own ripple strength and wave retention.
Keeping or clearing a reference restarts both ripple fields. Later preset loads,
edits and undo do not change the reference. **Clear reference** returns Compare
to its original Classic-versus-draft view. **Classic** temporarily bypasses both
styles. This reference is a temporary comparison, not another saved draft;
save or export the draft separately to keep its settings between editor sessions.
Only the synthetic preview is split; the World preview always uses the draft.

Enable **Preview in World** to use the draft on the World view's wet tiles.
It applies only to that embedded world instance. Classic comparison temporarily
draws its original tiles. Disabling preview restores the editor's previous
water selection. No player profile, map, client install or shard setting is
saved by previewing. This preview does not change the World view's lighting;
the daylight/dusk/night selector belongs to the synthetic test surface.

## Save and share

**Save draft** stores validated JSON under `<UO_WORKSPACE_DIR>/editor/water/drafts/`.
The existing per-user workspace setting and platform default apply (ADR-0032),
so saved presets are shared across checkouts and survive build-output cleanup.
Subsequent saves update that working copy atomically. **Refresh** discovers
saved drafts. **Import JSON** reads an existing style as a new draft, with
malformed or oversized files reported in the panel. A draft is local working
data; it is not automatically installed or activated in a client.
Before saving, the editor compares the current file with the version you loaded
or last saved. If another session changed or removed it, the save is refused
and your edits stay in the panel. Load the disk version to review it, or use
**Duplicate** to keep both versions. Earlier workbench drafts under
`<UO_BUILD_DIR>/water/drafts/` can be brought into the library with **Import JSON**.

To export, provide a lowercase pack ID, three-part version, author and licence.
**Export water pack** writes a v2 Store ZIP with one `water` component named
`sea`, `water.json`, and an original palette swatch. Attributed licences include
`LICENSE.txt`. No shader code, native art or client data is included. The
existing Store installation/deployment workflow activates a pack; exporting
does not publish, install, download or activate anything. Destinations inside
the client install or Godot project are refused.

## Verification

After building the Debug assembly, the standalone contract probe works headless:

```bat
godot-console --headless --path godot/GUO res://addons/guo_editor/Water/WaterWorkbenchProbe.tscn
```

Without `--headless`, the same probe renders native controls and comparison
captures with each renderer. `GUO_WATER_WORKBENCH_OUT` chooses an evidence run
folder; otherwise artifacts go under `<UO_BUILD_DIR>/water/probe/`.
The smoke launchers run the headless contracts. The editor smoke additionally
checks World preview scoping and restoration, before and after assembly reload.

Live-shard wading/boat behavior, local water zones and shard-selected styles
are separate work; synthetic hull captures do not prove those behaviors.
