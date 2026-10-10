# World Editing

How to change the map in the GUO editor: find a spot, select what is there,
place and move things, and take it back if you change your mind. This page is
for a shard owner opening the editor for the first time. For how the editor
is set up (profiles, the world project, the live tier), see [Editor](Editor.md).

Nothing you do here touches your UO install. Every edit goes into a **world
project**, a folder of its own that the editor lays over the install (see
[Where your edits go](#where-your-edits-go)).

## Open the World tab

1. Open the project with `launchers\editor\open_project.bat`.
2. Click **World** in the row of main screens at the top of the editor, beside
   2D, 3D and Script. The first time, the status line says *starting the
   world...* while the game's own renderer loads.
3. Go somewhere: pick the map (`map0` to `map5`) in the top bar, type
   `x,y` in the box beside it and press **Go**. You can also click a spot on
   the minimap in the corner of the view, or use **Show in UO World** in the
   Maps panel of UO Assets.

[screenshot 1: the World tab, with the tool rail on the left, the Tools and UO
Inspector panels, the brush library and the map]

The tab has four parts:

- **The tool rail**: a column of icon buttons on the far left. The pressed
  one is what a left click on the map does.
- **The left panel**: the **Tools** and **Advanced** tabs on top, and the
  **UO Inspector** and **Nearby tiles** tabs below them.
- **The brush library**: art to place, with a search box, beside the map.
- **The map**, with the status line under it. The status line tells you what
  the last click did, so read it whenever a click seems to do nothing.

## Moving around

| Do this | To |
|---|---|
| Arrow keys (Shift: 8 cells at a time) | Step the view one cell |
| Right-drag or middle-drag | Pan |
| Hold Space and left-drag | Pan |
| Mouse wheel | Zoom in and out |
| Tab, or the **Focus** button | Hide the side panels (and the Godot docks) for more map; press again to bring the side panels back |

Keyboard shortcuts work while the map has focus. Click the map once
first if a key does nothing.

## The tools

| Tool | Key | A left click on the map... |
|---|---|---|
| Select | V | Shows what is under the pointer in the UO Inspector. Changes nothing. |
| Brush | B | Paints with the brush recipe while you drag; the edit applies when you let go (Esc cancels). |
| Stamp | S | Places the current static on the cell, on top of what you clicked. |
| Erase | E | Removes the static you clicked. |
| Raise | R | Raises the ground at the cell by 1 (Shift-click: 5). |
| Lower | F | Lowers the ground by 1 (Shift-click: 5). |
| Hue | H | Gives the static you clicked the current hue. |
| PlaceItem | | Places the current static as a **shard object** (see below). |
| PlaceSpawner | | Places a spawner for the creature named in the **spawns** box. |
| MoveObject | | First click picks up one of the project's objects, second click puts it down. |
| DeleteObject | | Deletes one of the project's objects. |
| Measure | M | First click starts, second click gives the distance in tiles. |
| Area | G | Two clicks set the corners of a rectangle; **Area to multi** (under Advanced > World & overlays) takes its items into the Multi Editor (see [From an area to a building](#from-an-area-to-a-building)). |

The rail also has **Route** (the walking path between two clicks) and
**Pin**. Hover over a rail button to see its name and key. The four
object tools, Route and Pin have no key; click their buttons.

Two more keys: **[** and **]** shrink and grow the brush, and **Q** opens
your quick favourites.

## Selecting: what is under the pointer

With **Select** (V), a left click on the map fills the **UO Inspector** tab on
the left. It shows what you clicked (land or a static), its id and name, its
map, position and height (z), its block and cell, and its hue if it has one.

[screenshot 2: a static selected, its details in the UO Inspector]

Three things trip people up:

- **Nothing is highlighted on the map.** The result shows only in the
  UO Inspector and the status line. If the left panel is hidden, press Tab or
  **Focus** to bring it back. If the UO Inspector tab is hidden, click its
  tab under Tools.
- **Click on the drawn pixels.** The editor picks the way the game does, by
  the pixels of the art under the pointer. Clicking the empty space around a
  lamp post or inside a tree's gaps picks whatever is drawn behind it, often
  the ground.
- **A cell can hold a stack.** A table with a candle on it, or a wall on a
  floor, is several things on one cell. Clicking picks the top one you can
  see.

To reach the things lower in a stack:

- **Alt + mouse wheel** steps through everything on the cell, highest first,
  and shows each one in the UO Inspector as you go.
- The **Nearby tiles** tab (beside UO Inspector) shows the ground of the 3 by
  3 cells around the pointer and, next to it, the full stack of the centre
  cell. Click a row to inspect it; double-click a row to make it the current
  art. Click one of the 3 by 3 cells to look at that cell instead. Tick **Pin** to keep the panel on one cell while you move
  the mouse.
- **Shift-click** with Select starts again from the top of the stack.

[screenshot 3: the Nearby tiles tab with a stack listed]

Selecting a piece of land or a static also makes it the **current art**, so
you can select something already in the world and then Stamp more of it.

## Choosing what to place

The **current art** is what Stamp, PlaceItem and the brush put down. Its
picture and name show at the top of the Tools tab. To set it:

- click a static in the **brush library** (type a name like `torch` in its
  search box), or in **UO Assets > Art**;
- type a name or an id (`0x0A0F`) in the field at the top of the Tools tab;
- select a static on the map with Select;
- **Alt-click** a static on the map with any tool (the **Pick** button in the
  Tools tab does the same). This takes its hue as well.

Stamp and PlaceItem need a **static**, not a land tile. If you picked land, the
status line says so.

[screenshot 4: the brush library with a search, and the current art at the top
of the Tools tab]

## Brushes, and getting back to one item

The brush library (left of the map) has four one-click recipes. Each sets the
brush size, density and target in one go:

| Recipe | What it does |
|---|---|
| **Single** | One item per click: a 1-tile, 100% brush with the selected static. |
| **Scatter** | Many items over a 7-tile area at 35% density, spaced 2 tiles apart. |
| **Terrain** | Paints land over a 5-tile area. |
| **Sculpt** | Raises land over a 5-tile area. |

A recipe stays in force until you pick another. If you used Scatter and want
one item again, click **Single**. Choosing art in the library does not change
the recipe, so a recipe and the art are two separate choices.

The map always tells you what a click will do: the line at the top left of the
map names the tool and, for the brush, the recipe, for example
`Brush · Single 1×1 100%` or `Brush · Scatter 7×7 35%`. While you hover, it also
shows how many cells the stroke will change. If a click changes nothing and
a rule turned the cells away, the status line names that rule and what to
change. With **Keep existing statics** on (Advanced tab, Variation & rules), a
cell that already holds an item is left alone; the status line then says
*Nothing placed: that cell already has an item. Keep existing statics is on;
untick it to place on top*.

Rows in the Variation list, the saved presets, the favourites and the Nearby
tiles stack show the name first and the number second, for example
*tree · 0x0CCA*.

## Stamp or PlaceItem?

There are two kinds of thing you can put in the world, and they go to
different places:

- **Stamp** adds a **map static**: part of the map itself, like the walls
  and trees in the client's files. It ends up in the exported map files, so
  every player sees it, and a player cannot pick it up or move it.
- **PlaceItem** adds a **shard object**: an item kept in the project's list
  of things for the shard to create, as a server-side item. It is drawn with
  its own art and hue. Use
  this for things the shard owns, like decoration that may be moved or
  removed later.
- **PlaceSpawner** puts a spawner on the cell. Type the creature or vendor
  class name the shard knows (`Horse`, `Tanner`, ...) in the **spawns** box
  first; it is under **Advanced > World & overlays**. The spawner shows as a
  hued spawner item.

**MoveObject** and **DeleteObject** work only on shard objects (what PlaceItem
and PlaceSpawner made). To move a map static, Erase it and Stamp it again where
you want it.

## Hue and height (Z)

- **Hue**: set it in the **Hue** field of the Tools tab (a name or an id,
  `0` for none). Stamp, PlaceItem and the brush use it, and the **Hue** tool
  applies it to a static already in the world.
- **Height**: Stamp and PlaceItem put the new thing **on top of** what you
  clicked: on the ground, or on the top of the static you clicked. Click a
  table to stamp something on the table.
- To stamp or paint at an exact height, tick **Fixed Z** in the Tools tab and
  set **Z / offset**. (PlaceItem always goes on top of what you clicked.)
- To change one static's height or hue afterwards: click its row in the
  **Nearby tiles** stack. The status line names it and the button:
  *Chosen: crate · 0x0E3C at Z 0. Type Z / ground offset and Hue, then press
  Set Z / hue*. Set **Z / ground offset** and **Hue**, and press **Set Z /
  hue**. Set Z / hue changes items only; for the ground's height use the
  Flatten brush.
- **Z min** and **Z max** hide everything outside a height range. This helps
  when you work on an upper floor.

## Seeing the map differently: the View menu

**View** (Advanced > World & overlays) recolours the map to answer one
question, without changing it. A box at the top left of the map names the
view, says in one sentence what the colours mean, and lists each colour.
Hover over a view in the menu to read that sentence before you choose it.

| View | What the colours mean |
|---|---|
| Height | How high the ground is: blue is low, red and white are high. |
| Walkability | Where a player can stand, and where they are stopped. |
| Reachability | Click a cell: where a player can walk to from it. |
| Types | What each cell holds: wall, floor, roof, water and so on. |
| IDs | Each kind of item gets its own colour, so the map looks like a patchwork. That is normal, not damage. |
| Land mesh | The ground's shape; yellow tiles are stretched over a slope. |
| Problems | Only mistakes: holes in floors, two items in one spot at one height (they flicker), items on water. |
| Project diff | The parts of the map this project changed; the rest is your own install. |

Choose **Off** to see the plain map again.

## From an area to a building

With the **Area** tool, click two corners, then press **Area to multi**
(Advanced > World & overlays). The items inside open in the **Multis** tab as
a new building. A bar across the top of that tab says *From the World: N items
opened here as a new building*; its **Back to World** button returns to the
World tab, and **×** hides the bar. The World's status line says where the
items went.

## Undo and redo

**Ctrl+Z** undoes, **Ctrl+Y** (or Ctrl+Shift+Z) redoes, and the **Undo** and
**Redo** buttons in the top bar do the same. The status line counts what is
left to undo and redo, and the last 200 map edits can be undone. One brush
stroke is one undo step.

Shard objects (PlaceItem, PlaceSpawner, MoveObject, DeleteObject) are **not** in
undo. Take one back with DeleteObject or MoveObject.

## Where your edits go

Every edit is saved the moment you make it. There is no Save button and
nothing to lose if the editor closes.

- Edits go into the **world project**: the folder named by the
  `UO_WORLD_PROJECT` setting, or `build\world\default` in your GUO folder if
  that setting is empty.
- Map edits are stored as whole 8 by 8 cell blocks, one small file per block
  you changed, under the project's `blocks` folder. Shard objects go in its
  `shard\objects.json`.
- **Your UO install is never written to.** The editor only reads it and
  draws the project over it.
- **Reload project** (top bar) reads the project from disk again, for example
  after you copied blocks in from someone else.
- To give the map edits to a server or a client, export them:
  `launchers\pipeline\04_world_export.bat` writes patched map files beside
  the install, never into it (see [Editor](Editor.md#exporting-it)).

[screenshot 5: the status line after an edit, showing the undo and redo count]

## When a click does nothing

Read the status line under the map first; it says why. The usual answers:

| Status line | Meaning |
|---|---|
| *nothing under the pointer* | The pointer was not on drawn pixels. Click on the art itself. |
| *Erase takes an item; that is the ground* (or *Hue takes...*) | You clicked the ground. Use Alt + wheel or Nearby tiles to reach the item. |
| *Stamp needs an item* | The current art is a land tile. Pick an item. |
| *Nothing placed: that cell already has an item...* | **Keep existing statics** is on. Untick it to place on top. |
| *Nothing placed: the brush is over water...* | **Avoid water** is on. Untick it to paint there. |
| *Nothing placed: Density and Spacing left these cells empty...* | Raise **Density**, or move the brush. |
| *Set Z / hue: first click an item's row in Nearby tiles* | Choose the row first, then press Set Z / hue. |
| *Move object: click one of the project's objects first* | Move and Delete work only on shard objects, not on map statics. |
| *Place spawner needs a creature name in the spawns box* | Type a creature name under Advanced > World & overlays. |
| *Terrain is locked* | Untick **Lock terrain** in the Tools tab. |
| A key does nothing | Click the map once so it has focus. |

With **Select**, a click only fills the UO Inspector and never changes the map.
To change something, pick a tool from the rail first.
