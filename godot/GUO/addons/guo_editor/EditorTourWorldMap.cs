#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using GUO.Game.GameObjects;

/// <summary>ED3b: world objects, the map tools, what the map shows (layers, guides, season, views, map layers), undo.</summary>
public partial class EditorTour
{
    // --- World objects: place an item, a spawner, move, delete -------------------------------------------------

    private async Task WeObjects()
    {
        if (!await NeedWe())
        {
            return;
        }

        int cx = EditX - 14, cy = EditY + 12;
        await OpenWe(cx, cy);
        _overlay.ClearMarks();
        int n0 = _world.Objects.DrawnCount;
        UseArt(Anvil);
        Say("World objects are things the server owns, such as decorations and spawners, kept in the project apart from the map. Place item puts the chosen item, an anvil, as a decoration.");
        await Rail(WorldTool.PlaceItem);
        await AimCell(cx, cy);
        await Shot(2.5);
        await Click();
        await Frames(15);
        Check(_world.Objects.DrawnCount == n0 + 1, "PlaceItem put one anvil");
        await Shot();
        _overlay.ClearMarks();

        Say("Place spawner puts a spawner: a point where the server makes creatures appear. It spawns what the spawns box on the Advanced tab names, a Horse here. It shows as a small marker.");
        await Rail(WorldTool.PlaceSpawner);
        await AimCell(cx + 4, cy);
        await Shot(2.5);
        await Click();
        await Frames(15);
        Check(_world.Objects.DrawnCount == n0 + 2, "PlaceSpawner added a spawner");
        MarkControl(_world.TourStatus, "what was placed");
        await Shot();
        _overlay.ClearMarks();

        Say("Move object: the first click picks an object up, the second puts it down. The anvil goes four tiles to the left.");
        await Rail(WorldTool.MoveObject);
        Item anvil = _world.Objects.DrawnItemWithGraphic(Anvil);
        Check(anvil != null, "the anvil is in the world");
        if (anvil != null)
        {
            await AimAt(WorldAim.ScreenOfObject(_world, _data, anvil), o => o is Item i && i.Graphic == Anvil);
            await Shot(2.5);
            await Click();
            string picked = _world.TourStatus.Text;
            await AimCell(cx - 4, cy);
            await Shot(2.5);
            await Click();
            await Frames(15);
            Item moved = _world.Objects.DrawnItemWithGraphic(Anvil);
            Check(moved != null && moved.X == cx - 4, $"MoveObject put the anvil at x={moved?.X} (expected {cx - 4}); after pick-up: {picked}; after drop: {_world.TourStatus.Text}");
        }

        await Shot();
        _overlay.ClearMarks();

        Say("Delete object removes the object you click.");
        await Rail(WorldTool.DeleteObject);
        Item victim = _world.Objects.DrawnItemWithGraphic(Anvil);
        if (victim != null)
        {
            await AimAt(WorldAim.ScreenOfObject(_world, _data, victim), o => o is Item i && i.Graphic == Anvil);
            await Shot(2.5);
            await Click();
            await Frames(15);
        }

        Check(_world.Objects.DrawnCount == n0 + 1, $"DeleteObject removed the anvil (status: {_world.TourStatus.Text})");
        await Shot();
        _overlay.ClearMarks();
        await Rail(WorldTool.Select);
        _overlay.ClearMarks();
        _world.ForcedMouse = null;
    }

    // --- Measure, Route, Pin, Area to multi -------------------------------------------------------------------------

    private async Task WeMapTools()
    {
        if (!await NeedWe())
        {
            return;
        }

        int cx = 1496, cy = 1628;
        await OpenWe(cx, cy);
        MapLayers layers = _world.Layers;
        layers.Measure.Clear();
        layers.Route.Clear();
        _overlay.ClearMarks();

        Say("Measure (keyboard: M): click two spots. The line under the map gives the distance in tiles.");
        await Rail(WorldTool.Measure);
        await ClickCell(cx - 3, cy - 2);
        await Shot(2.5);
        await ClickCell(cx + 3, cy + 2);
        await Frames(6);
        Check(layers.Measure.Result != null, "Measure has a result for two clicks");
        MarkControl(_world.TourStatus, "distance");
        await Shot();
        _overlay.ClearMarks();

        Say("Route: click a start, then a goal. It draws the way a player could walk there, by the game's own walking rules, and gives its length in steps.");
        await Rail(WorldTool.Route);
        await ClickCell(cx, cy);
        await Shot(2.5);
        await ClickCell(cx + 4, cy);
        await Frames(6);
        Check(layers.Route.Path != null, $"Route found a way of {layers.Route.Length} steps");
        MarkControl(_world.TourStatus, "length");
        await Shot();
        _overlay.ClearMarks();

        Say("Pin drops a bookmark. It is saved with the project and shows on the small map at the top right too.");
        await Rail(WorldTool.Pin);
        int pins = layers.Pins.All.Count;
        await ClickCell(cx - 1, cy + 3);
        await Frames(10);
        Check(layers.Pins.All.Count == pins + 1, "Pin added a bookmark");
        MarkControl(_world.Minimap, "small map");
        await Shot();
        _overlay.ClearMarks();
        foreach (var p in layers.Pins.All.ToList())
        {
            layers.Pins.Remove(p);
        }

        layers.Route.Clear();
        layers.Measure.Clear();
        foreach (string n in _world.LayerNames)
        {
            _world.SetLayer(n, false);
        }

        Say("Area (keyboard: G): click two corners to select a rectangle.");
        await Rail(WorldTool.Area);
        await ClickCell(cx - 4, cy - 4);
        await Shot(2.5);
        await ClickCell(cx + 2, cy + 2);
        await Frames(6);
        Check(_world.Area != null, "two clicks selected an area");
        await Shot();
        _overlay.ClearMarks();

        Say("Area to multi, under World & overlays on the Advanced tab, copies the items in that rectangle into the Multi Editor as a new building.");
        Button a2m = All<Button>(_world).FirstOrDefault(b => b.Text == "Area to multi");
        Check(a2m != null, "the World tab has an Area to multi button");
        if (a2m != null)
        {
            await ShowControl(a2m);
            MarkControl(a2m, "Area to multi");
            PointAt(a2m);
            await Shot(2.5);
            a2m.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(25);
            _overlay.ClearMarks();
            Check(_world.TourStatus.Text.StartsWith("Area to multi"), $"Area to multi said: {_world.TourStatus.Text}");
            Say("The Multi Editor opens with the copied items as a new building, ready to edit and save.");
            _overlay.CaptionArea = null;
            await Shot();

            Say("The World button at the top brings the map back.");
            Button worldTab = All<Button>(EditorInterface.Singleton.GetBaseControl())
                .FirstOrDefault(b => b.Text == GuoEditorPlugin.WorldTabName && b.IsVisibleInTree() && b.ToggleMode);
            if (worldTab != null)
            {
                MarkControl(worldTab, "World");
                PointAt(worldTab);
                await Shot(2.5);
            }

            EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
            await Frames(20);
            _overlay.CaptionArea = MapArea;
            _overlay.ClearMarks();
            Check(_world.IsVisibleInTree(), "the World tab is open again");
            await CloseFolds();
            await Shot();
        }

        await Rail(WorldTool.Select);
        _overlay.ClearMarks();
        _world.ForcedMouse = null;
    }

    // --- What the map shows: the Layers menu, the Guides menu, the season ------------------------------------

    private async Task WeLayers()
    {
        if (!await NeedWe())
        {
            return;
        }

        await OpenWe(1496, 1628);
        _overlay.ClearMarks();
        await ShowControl(_world.LayersMenu);
        Say("The Layers menu on the Tools tab switches parts of the world on and off: ground, items, buildings, roofs and world objects.");
        await OpenMenu(_world.LayersMenu, "Layers");
        PointAt(_world.LayersMenu);
        await Shot();
        CloseMenu(_world.LayersMenu);
        await Toggle("Statics", false);
        await Frames(10);
        Check(!_world.Host.ShowStatics && !_world.MenuItemChecked("Statics"), "Statics off hides the items");
        Say("Statics (the items) unticked: only the bare ground and the buildings' outlines are left.");
        await Shot();
        await Toggle("Statics", true);
        Check(_world.Host.ShowStatics, "Statics back on");
        _overlay.ClearMarks();

        Say("The Guides menu adds drawing aids the game never shows: a grid of tiles and each tile's height as a number.");
        await OpenMenu(_world.GuidesMenu, "Guides");
        PointAt(_world.GuidesMenu);
        await Shot(2.5);
        CloseMenu(_world.GuidesMenu);
        await Toggle("Grid", true);
        await Toggle("Altitude", true);
        await Frames(15);
        Check(_world.Guides.CellsDrawn > 0, $"the guides drew {_world.Guides.CellsDrawn} tiles");
        await Shot();
        await Toggle("Grid", false);
        await Toggle("Altitude", false);
        _overlay.ClearMarks();

        // Seasons change plants, so the step goes where the trees are.
        await OpenWe(1150, 1680);
        Say("Season, under World & overlays on the Advanced tab, switches to the game's own seasonal art. Winter, among the trees:");
        OptionButton season = All<OptionButton>(_world).FirstOrDefault(o => o.TooltipText.StartsWith("Season"));
        Check(season != null, "the World tab has a season box");
        if (season != null)
        {
            await ShowControl(season);
            MarkControl(season, "season");
            PointAt(season);
            await Shot(2.5);
            _world.Season = GUO.Game.Managers.Season.Winter;
            await Frames(25);
            Check(_world.Season == GUO.Game.Managers.Season.Winter, "the season is Winter");
            await Shot();
            _world.Season = GUO.Game.Managers.Season.Summer;
            await Frames(15);
        }

        _overlay.ClearMarks();
        await CloseFolds();
    }

    // --- Views: the View menu's colour modes ------------------------------------------------------------------------

    /// <summary>What each view shows, for someone who owns a shard, not for a programmer.</summary>
    private static readonly Dictionary<string, string> ModeWords = new()
    {
        ["Height"] = "Height colours every spot by how high it is. The legend in the top left corner gives the scale; lines mark every 5 steps of height.",
        ["Walkability"] = "Walkability shows where a player can stand and walk, by the game's own movement rules. The legend names the colours.",
        ["Reachability"] = "Reachability starts from one spot: green is where a player can walk to from there, orange is ground a player could stand on but cannot reach.",
        ["Types"] = "Types gives each kind of thing its own colour: ground, floor, wall, window, door, roof, stairs, plants, water and other items.",
        ["IDs"] = "Item colours give every different item its own colour, so two items that look alike but are not the same stand out. The bright patchwork is what this view looks like.",
        ["Land mesh"] = "Land mesh draws the ground as a wire frame at its corner heights. Yellow marks ground tiles the game stretches.",
        ["Problems"] = "Problems marks likely mistakes: gaps in floors, two items in exactly the same place (they flicker in the game), and items standing in water. This street has none of them, so nothing is coloured.",
        ["Project diff"] = "Project changes shows which 8 by 8 patches this project replaced. Everything else is still the original map.",
    };

    private async Task WeModes()
    {
        if (!await NeedWe())
        {
            return;
        }

        await OpenWe(1496, 1628);
        _world.SetSolid(false);
        _overlay.ClearMarks();
        Say("The View menu, under World & overlays, colours the map to show what it is made of. It only changes what you see here, never the map itself.");
        await ShowControl(_world.ViewMenu);
        await OpenMenu(_world.ViewMenu, "View");
        PointAt(_world.ViewMenu);
        await Shot();
        CloseMenu(_world.ViewMenu);
        _overlay.ClearMarks();

        foreach (string mode in _world.Modes.ModeNames)
        {
            if (mode == "Reachability")
            {
                _world.Modes.SetOrigin(1496, 1628, _world.Modes.Data.LandZ(1496, 1628));
            }

            if (mode == "Project diff")
            {
                // The town has no project changes; the patches this tour edited are by the first crate.
                await OpenWe(EditX, EditY);
            }

            Check(ModeWords.ContainsKey(mode), $"the tour has words for the view '{mode}'");
            Say(ModeWords.TryGetValue(mode, out string words) ? words : mode + ".");
            Check(_world.SetViewMode(mode), $"View: {mode}");
            await Frames(mode is "Walkability" or "Reachability" ? 40 : 20);
            Check(_world.Chip.Visible && _world.Chip.Rows > 0, $"{mode}: the legend shows its colours");
            MarkLegend();
            await Shot();
            _overlay.ClearMarks();
        }

        Say("Solid fill, at the bottom of the View menu, hides the world under the colours; without it the colours are see-through. Walkability, solid:");
        _world.SetViewMode("Walkability");
        _world.SetSolid(true);
        await Frames(20);
        MarkLegend();
        await Shot();
        _world.SetSolid(false);
        _world.SetViewMode("Off");
        _overlay.ClearMarks();
        Say("View: Off returns to the plain map.");
        await Frames(10);
        await Shot();
        await CloseFolds();
    }

    // --- Map layers and the scene pack -------------------------------------------------------------------------------

    private async Task WeMapLayers()
    {
        if (!await NeedWe())
        {
            return;
        }

        MapLayers layers = _world.Layers;
        layers.ShardFolder = EditorSmoke.FixtureShardFolder();
        await OpenWe(1496, 1628);
        _overlay.ClearMarks();
        Say("Map layers, next to View, draw labels on the map and on the small map, like an online map: places, regions, spawners, houses, pins. The regions here come from a small sample shard.");
        await ShowControl(_world.MapLayersMenu);
        await OpenMenu(_world.MapLayersMenu, "Map layers");
        PointAt(_world.MapLayersMenu);
        await Shot();
        CloseMenu(_world.MapLayersMenu);
        foreach (string n in new[] { "Places", "Regions", "Spawns" })
        {
            Check(_world.SetLayer(n, true), $"layer {n} on");
        }

        await Frames(25);
        _overlay.ClearMarks(keepPointer: true);
        Say("Places, Regions and Spawns on: names and outlines over the town, on both maps.");
        MarkControl(_world.Minimap, "small map");
        await Shot();
        foreach (string n in _world.LayerNames)
        {
            _world.SetLayer(n, false);
        }

        _overlay.ClearMarks();
        Button pack = All<Button>(_world).FirstOrDefault(b => b.Text == "Scene pack");
        Check(pack != null, "the World tab has a Scene pack button");
        if (pack != null)
        {
            Say("Scene pack saves a picture of this view with a list of what is in it, for a helper program to read. It stays on this computer; nothing is sent anywhere.");
            await ShowControl(pack);
            MarkControl(pack, "Scene pack");
            PointAt(pack);
            await Shot(2.5);
            string folder = Path.Combine(EditorData.RepoRoot, "build", "scene_packs");
            int count = Directory.Exists(folder) ? Directory.GetDirectories(folder).Length : 0;
            pack.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(20);
            Check(Directory.Exists(folder) && Directory.GetDirectories(folder).Length > count, "Scene pack wrote a new folder");
            // The line under the map now names a folder: this frame is left out of the overview.
            await Shot();
        }

        _overlay.ClearMarks();
        await CloseFolds();
    }

    // --- Undo and redo across blocks ----------------------------------------------------------------------------------------

    private async Task WeUndo()
    {
        if (!await NeedWe())
        {
            return;
        }

        // x = 7 and 8 sit on either side of a block boundary (8 tiles per block).
        int bx = ((EditX >> 3) + 3) * 8 - 1, cy = EditY + 20;
        await OpenWe(bx, cy);
        await Rail(WorldTool.Brush);
        _world.SetKeepStaticsForSmoke(false);
        await ScatterBlock(5);
        UseArt(_stampArt);
        await Toggle("Blocks", true);
        await Frames(10);
        string a = Slash(_world.Host.Project.BlockPath(0, bx >> 3, cy >> 3)), b = Slash(_world.Host.Project.BlockPath(0, (bx + 1) >> 3, cy >> 3));

        Say("The map is stored in patches of 8 by 8 tiles; the Blocks guide draws their borders. One stroke of crates across a border changes two patches, yet it is one step for Undo.");
        int u = _world.Editor.UndoCount;
        await Stroke(new List<(int, int)> { (bx - 1, cy), (bx + 2, cy) });
        Check(_world.Editor.UndoCount == u + 1 && File.Exists(a) && File.Exists(b), "one undo step, two patches written");
        await Shot();
        _overlay.ClearMarks();

        Say("Undo (or Ctrl+Z) takes the whole stroke back, on both sides of the border.");
        await Press("Undo");
        await Frames(20);
        Check(!File.Exists(a) && !File.Exists(b), "Undo put both patches back");
        await Shot();
        _overlay.ClearMarks();

        Say("Redo (or Ctrl+Y) puts it all back again.");
        await Press("Redo");
        await Frames(20);
        Check(File.Exists(a) && File.Exists(b), "Redo restored both patches");
        await Shot();
        _overlay.ClearMarks();

        // The keyboard does the same: Ctrl+Z, Ctrl+Y (not filmed again).
        _world.TourInput(new InputEventKey { Keycode = Key.Z, Pressed = true, CtrlPressed = true });
        await Frames(20);
        Check(!File.Exists(a), "Ctrl+Z undid it");
        _world.TourInput(new InputEventKey { Keycode = Key.Y, Pressed = true, CtrlPressed = true });
        await Frames(20);
        Check(File.Exists(a), "Ctrl+Y redid it");
        await Toggle("Blocks", false);
        _world.SetKeepStaticsForSmoke(true);
        _world.ForcedMouse = null;
    }

    private void MarkLegend()
    {
        LegendChip chip = _world.Chip;
        MarkRect(chip, () => new Rect2(chip.GlobalPosition, chip.DrawnSize), "legend");
    }

    /// <summary>Closes the Advanced tab's folds the tour opened and goes back to the Tools tab, so later steps find the panel as a person left it.</summary>
    private async Task CloseFolds()
    {
        foreach (string name in new[] { "World & overlays" })
        {
            Button fold = All<Button>(_world).FirstOrDefault(b => b.Text == name && b.ToggleMode);
            if (fold != null && fold.ButtonPressed)
            {
                fold.ButtonPressed = false;
            }
        }

        _world.TourSettingsTab(0);
        await Frames(4);
    }
}
#endif
