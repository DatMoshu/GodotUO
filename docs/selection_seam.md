# Replacement selection seam

Optional splat replacements forward interaction to their live hidden original
at the `SelectedObject.Object` assignment seam. Ordinary objects and null pass
through unchanged. Mouse-down snapshots the already resolved object, so target,
use, drop, drag and ignore paths retain the upstream input code. Neither
GameSceneInputHandler nor TargetManager needs a per-call replacement resolve.

`SplatObject` first uses its live `Victim`, then searches the placement tile for
a hidden original matching `VictimGraphic` (or the existing `_hhhh` name tag),
preferring the same Z. Destroyed originals are never returned. With no match,
the splat passes through. This change adds no render integration, authoring,
model or service. The optional placement binding defaults to zero; existing
placements retain their name-tag lookup.

JarJar's interaction helpers from `a0a850bf` are retained verbatim in a scoped
cherry-pick, separately from the director's placement compatibility field and
selection hook. JarJar's standalone `72264857` boot null guard is also retained:
InputProbe may wait for the game/world before they exist, rather than throwing
during early shard-command startup. Login and roof changes are excluded.

After building, run the headless seam proof:

```text
godot-console --headless --path godot/GUO res://src/Bootstrap/SelectionSeamProbe.tscn
```

It checks native/null identity, forwarding, the drag snapshot and destroyed or
unbound fallback. Before submission, a scripted private-shard run at 2100,1800
with empty splat/override folders, fixed light and zoom 1 must compare its render
dump with the same run from main. Run the full developer smoke as well. Art and
private profiles remain local build evidence.
