#if TOOLS
namespace GUO.Editor;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>ED3b: the brush, its settings, variations, rules and presets, on camera in plain words.</summary>
public partial class EditorTour
{
    // --- Brush recipes: Single, Scatter, Sculpt, Terrain ------------------------------------------------------

    private async Task WeBrush()
    {
        if (!await NeedWe())
        {
            return;
        }

        int cx = EditX - 12, cy = EditY - 12;
        await OpenWe(cx, cy);
        _world.SetKeepStaticsForSmoke(false);
        _overlay.ClearMarks();
        Say("The Brush paints many things at once. It has four ready-made recipes above the item library. Choose a tree first: type \"tree\".");
        ushort tree = await ChooseArt("tree") ?? Tree;
        _overlay.ClearMarks();

        Say("Single: one item per click, like Stamp. The label at the top left of the map always names the active brush.");
        await Press("Single");
        await AimCell(cx, cy);
        await Frames(10);
        MarkControl(_world.TourPreviewLabel, "active brush");
        await Shot(2.5);
        int undo = _world.Editor.UndoCount;
        await Click();
        await Frames(15);
        Check(StaticCount(cx - 1, cy - 1, cx + 1, cy + 1, tree) == 1 && _world.Editor.UndoCount == undo + 1, "Single placed exactly one tree, one undo step");
        Check(_world.ToolLabel == "Brush · Single 1×1 100%", $"the label reads Brush · Single 1×1 100% ({_world.ToolLabel})");
        await Shot();
        _overlay.ClearMarks();

        Say("Scatter: a round brush 7 tiles wide that fills about a third of its tiles. Hold the button and drag: the faint trees show where they will go.");
        await Press("Scatter");
        // On screen a step in x or y moves 22 px down (at this zoom 44): keep x + y small so the stroke stays above the caption.
        var stroke = Line(cx + 5, cy - 5, cx + 5, cy - 1);
        _overlay.ClearMarks();
        await Stroke(stroke, async () => await Shot());
        int placed = StaticCount(cx + 1, cy - 9, cx + 9, cy + 3, tree);
        Check(placed >= 4, $"Scatter placed {placed} trees from one stroke");
        Check(_world.Editor.UndoCount == undo + 2, "the whole stroke is one undo step");
        Say("Let go and they are planted. The whole stroke is one step for Undo.");
        await Shot();
        _overlay.ClearMarks();
        await Press("Undo");
        await Frames(15);
        _overlay.ClearMarks();
        _world.ForcedMouse = null;

        Say("Sculpt shapes the ground with a brush 5 tiles wide. Strength says how many steps each pass lifts: set it to 4, then click.");
        await Press("Sculpt");
        int sx = cx - 3, sy = cy + 4;
        int zs = LandZ(sx, sy);
        await SetNumber("Strength", 4);
        await Shot(2.5);
        await Stroke(new List<(int, int)> { (sx, sy) });
        int zs2 = LandZ(sx, sy);
        Check(zs2 == zs + 4, $"Sculpt at strength 4 lifted the ground {zs2 - zs} steps");
        _overlay.SetDetail($"Ground height in the middle: {zs} → {zs2}");
        Say("A hill, four steps up in the middle.");
        await Shot();
        _overlay.SetDetail(null);
        _overlay.ClearMarks();
        await SetNumber("Strength", 1);
        _overlay.ClearMarks();
        _world.ForcedMouse = null;

        Say("Terrain paints ground tiles instead of items. Press Terrain: the library now lists ground. Type \"sand\".");
        await Press("Terrain");
        _overlay.ClearMarks();
        ushort sand = await ChooseArt("sand") ?? Grass;
        _overlay.ClearMarks();
        Say("Drag across the grass: a strip of sand.");
        int before = SandCells(cx - 7, cy - 7, cx + 2, cy - 1, sand);
        await Stroke(Line(cx - 4, cy - 4, cx, cy - 4));
        int after = SandCells(cx - 7, cy - 7, cx + 2, cy - 1, sand);
        Check(after > before, $"the Terrain stroke painted ground ({before} to {after} tiles of it)");
        await Shot();
        _overlay.ClearMarks();
        _world.SetKeepStaticsForSmoke(true);
        _world.ForcedMouse = null;
    }

    /// <summary>How many cells in the rectangle have this land tile, as the project now has them.</summary>
    private int SandCells(int x0, int y0, int x1, int y1, ushort land)
    {
        int n = 0;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                WorldBlock b = WeBlock(x, y);
                ushort id = b != null ? b.LandId[(y & 7) * 8 + (x & 7)] : (ushort)_world.Modes.Data.LandId(x, y);
                n += id == land ? 1 : 0;
            }
        }

        return n;
    }

    // --- Brush settings: Size, Density, Spacing, Square, the [ ] keys, Seed ------------------------------------------

    private async Task WeSettings()
    {
        if (!await NeedWe())
        {
            return;
        }

        int cx = EditX + 6, cy = EditY - 10;
        await OpenWe(cx, cy);
        _world.SetKeepStaticsForSmoke(false);
        UseArt(Tree);
        await Rail(WorldTool.Brush);
        await Press("Scatter");
        _overlay.ClearMarks();

        Say("The brush's settings are on the Tools tab. Size is how wide it is, Density how many of its tiles get an item, Spacing the smallest gap between items. Size 3, density 100, spacing 1, and tick Square:");
        await SetNumber("Size (tiles)", 3);
        await SetNumber("Density (%)", 100);
        await SetNumber("Spacing (tiles)", 1);
        await SetCheck("Square footprint", true);
        Check(_world.TourRecipe.Size == 3 && _world.TourRecipe.Density == 100 && _world.TourRecipe.Square, "the brush took size 3, density 100, square");
        await Shot();
        _overlay.ClearMarks();

        Say("One click fills a block of 3 by 3 tiles: nine trees.");
        await AimCell(cx, cy);
        await Frames(10);
        await Shot(2.5);
        await Click();
        await Frames(20);
        int block = StaticCount(cx - 2, cy - 2, cx + 2, cy + 2, Tree);
        Check(block == 9, $"a square 3x3 at 100% placed {block} trees (9 expected)");
        await Shot();
        _world.Editor.Undo();
        await Frames(10);
        _overlay.ClearMarks();

        Say("Without Square the brush is round. The ] key makes it bigger, [ makes it smaller. Two presses of ] make it 5 wide.");
        await SetCheck("Square footprint", false);
        Aim(Centre);
        await Frames(4);
        await KeyPress(Key.Bracketright);
        await KeyPress(Key.Bracketright);
        await Frames(6);
        Check(_world.TourRecipe.Size == 5, $"] twice made the brush 5 wide ({_world.TourRecipe.Size})");
        MarkControl(_world.TourNumber("Size (tiles)"), "Size");
        await Shot();
        _overlay.ClearMarks();

        Say("Seed decides which tiles Scatter picks. It is on the Advanced tab.");
        await SetNumber("Density (%)", 35);
        await SetNumber("Size (tiles)", 7);
        _overlay.ClearMarks();
        var stroke = Line(cx - 2, cy - 4, cx - 2, cy + 4);
        await Stroke(stroke);
        var seedA = StaticCells(cx - 8, cy - 10, cx + 4, cy + 10, Tree);
        await Shot(2.5);
        _world.Editor.Undo();
        await Frames(15);
        await SetNumber("Seed", 7);
        await Shot(2.5);
        _overlay.ClearMarks();
        Say("The same stroke with seed 7 puts the trees in other places. The same seed always gives the same result.");
        await Stroke(stroke);
        var seedB = StaticCells(cx - 8, cy - 10, cx + 4, cy + 10, Tree);
        Check(!seedA.SetEquals(seedB), $"seed 7 scattered differently from seed 1 ({seedA.Count} and {seedB.Count} trees)");
        await Shot();
        _world.Editor.Undo();
        await Frames(15);
        _overlay.ClearMarks();
        await SetNumber("Seed", 1);
        _overlay.ClearMarks();
        _world.SetKeepStaticsForSmoke(true);
        _world.ForcedMouse = null;
    }

    // --- Variations: several items in one stroke ------------------------------------------------------------------------

    private async Task WeVariations()
    {
        if (!await NeedWe())
        {
            return;
        }

        int cx = EditX - 2, cy = EditY - 14;
        await OpenWe(cx, cy);
        _world.SetKeepStaticsForSmoke(false);
        Say("Variations mix several items in one stroke. First a Scatter brush 5 tiles wide, density 60.");
        await Rail(WorldTool.Brush);
        await Press("Scatter");
        await SetNumber("Size (tiles)", 5);
        await SetNumber("Density (%)", 60);
        await Shot();
        _overlay.ClearMarks();

        Say("Choose a tree and press + Variation; then choose a rock and press + Variation again.");
        ushort tree = await ChooseArt("tree") ?? Tree;
        _overlay.ClearMarks();
        await Press("+ Variation");
        await Shot(2.5);
        _overlay.ClearMarks();
        ushort rock = await ChooseArt("rock") ?? Anvil;
        _overlay.ClearMarks();
        await Press("+ Variation");
        Check(_world.TourVariants.Contains(','), $"two variations are in the brush ({_world.TourVariants})");
        await Shot();
        _overlay.ClearMarks();

        Say("Both are listed under Variation & rules on the Advanced tab, each with its share of the stroke.");
        await ShowControl(_world.TourVariantRows);
        MarkControl(_world.TourVariantRows, "variations");
        await Shot();
        _overlay.ClearMarks();

        Say("One stroke now plants trees and rocks together.");
        await Stroke(Line(cx - 3, cy - 3, cx + 3, cy));
        int trees = StaticCount(cx - 8, cy - 8, cx + 8, cy + 5, tree), rocks = StaticCount(cx - 8, cy - 8, cx + 8, cy + 5, rock);
        Check(rocks > 0 && trees > 0, $"the stroke mixed {trees} trees and {rocks} rocks");
        await Shot();
        _world.Editor.Undo();
        await Frames(10);
        _world.TourWeights.Text = "";
        _world.TourWeights.EmitSignal(LineEdit.SignalName.TextChanged, "");
        _world.TourWeights.EmitSignal(LineEdit.SignalName.TextSubmitted, "");
        _overlay.ClearMarks();
        _world.SetKeepStaticsForSmoke(true);
        _world.ForcedMouse = null;
    }

    // --- Rules: Avoid water, Allowed land IDs, Keep existing statics, Maximum slope, edge rules ---------------------

    private async Task ScatterBlock(int size)
    {
        await Press("Scatter");
        await SetNumber("Size (tiles)", size);
        await SetNumber("Density (%)", 100);
        await SetNumber("Spacing (tiles)", 1);
        await SetCheck("Square footprint", true);
        UseArt(Tree);
        _overlay.ClearMarks();
    }

    private async Task WeRules()
    {
        if (!await NeedWe())
        {
            return;
        }

        // Everything sits within a few tiles of the view's centre (ox, oy): x + y small keeps it above the caption.
        int ox = EditX + 16, oy = EditY - 8;
        await OpenWe(ox, oy);
        await Rail(WorldTool.Brush);
        _world.SetKeepStaticsForSmoke(false);
        _overlay.ClearMarks();

        Say("Brush rules, on the Advanced tab, keep a brush out of places it should not go. To try them, first a strip of water, painted with Terrain: type the water tile's number, 0x00A8.");
        await Press("Terrain");
        await SetNumber("Size (tiles)", 3);
        await SetNumber("Density (%)", 100);
        await SetCheck("Square footprint", true);
        _overlay.ClearMarks();
        await ChooseArt("0x00A8");
        _overlay.ClearMarks();
        await Stroke(Line(ox - 4, oy, ox + 4, oy));
        await Shot();
        _overlay.ClearMarks();

        await ScatterBlock(3);
        var across = Line(ox, oy - 4, ox, oy + 4);
        Say("Avoid water off: a stroke of trees across the strip puts trees in the water too.");
        await SetCheck("Avoid water", false);
        await Shot(2.5);
        _overlay.ClearMarks();
        await Stroke(across);
        int inWater = StaticCount(ox - 1, oy - 1, ox + 1, oy + 1, Tree);
        await Shot();
        _world.Editor.Undo();
        await Frames(15);
        _overlay.ClearMarks();

        Say("Avoid water on (it is on unless you untick it): the same stroke skips every water tile.");
        await SetCheck("Avoid water", true);
        await Shot(2.5);
        _overlay.ClearMarks();
        await Stroke(across);
        int dry = StaticCount(ox - 1, oy - 1, ox + 1, oy + 1, Tree);
        Check(inWater > dry, $"Avoid water skipped tiles: {dry} trees against {inWater} without it");
        await Shot();
        _world.Editor.Undo();
        await Frames(15);
        _overlay.ClearMarks();

        Say("Allowed land IDs lets the brush paint only on the ground tiles you list (blank: anywhere). Grass only, 0x0003, with Avoid water off: still nothing in the water.");
        await SetCheck("Avoid water", false);
        await SetText(_world.TourAllowed, "Allowed land IDs", "0x0003");
        await Shot(2.5);
        _overlay.ClearMarks();
        await Stroke(across);
        int grassOnly = StaticCount(ox - 1, oy - 1, ox + 1, oy + 1, Tree);
        Check(grassOnly < inWater, $"Allowed land IDs kept the trees off the water ({grassOnly} against {inWater})");
        await Shot();
        _world.Editor.Undo();
        await Frames(15);
        _overlay.ClearMarks();
        await SetText(_world.TourAllowed, "Allowed land IDs", "");
        await SetCheck("Avoid water", true);
        _overlay.ClearMarks();

        Say("Keep existing statics (on unless you untick it) leaves a tile alone if something already stands on it. A lone tree first, with Single:");
        await SetCheck("Keep existing statics", false);
        _overlay.ClearMarks();
        await Press("Single");
        await ClickCell(ox + 5, oy - 5);
        await Frames(10);
        await Shot();
        _overlay.ClearMarks();
        await ScatterBlock(3);
        Say("Then a 3 by 3 block of trees right over it, with Keep existing statics ticked: the lone tree stays one tree, and the tiles around it fill.");
        await SetCheck("Keep existing statics", true);
        await Shot(2.5);
        _overlay.ClearMarks();
        int treesBefore = StaticCount(ox + 3, oy - 7, ox + 7, oy - 3, Tree);
        await Stroke(new List<(int, int)> { (ox + 5, oy - 5) });
        int treesKeep = StaticCount(ox + 3, oy - 7, ox + 7, oy - 3, Tree);
        Check(StaticCount(ox + 5, oy - 5, ox + 5, oy - 5, Tree) == 1 && treesKeep >= treesBefore + 3, $"Keep existing statics: the taken tile kept one tree, the free tiles around it filled ({treesBefore} to {treesKeep})");
        await Shot();
        _world.Editor.Undo();
        await Frames(10);
        _overlay.ClearMarks();

        Say("Maximum slope skips ground steeper than the limit. A hill first, with Sculpt at strength 6:");
        await SetCheck("Keep existing statics", false);
        await Press("Sculpt");
        await SetNumber("Strength", 6);
        await SetNumber("Size (tiles)", 3);
        await SetNumber("Density (%)", 100);
        _overlay.ClearMarks();
        await Stroke(new List<(int, int)> { (ox - 4, oy + 4) });
        await Shot();
        _overlay.ClearMarks();
        await ScatterBlock(5);
        await SetNumber("Maximum slope", 127);
        _overlay.ClearMarks();
        await Stroke(new List<(int, int)> { (ox - 4, oy + 4) });
        int allSlopes = StaticCount(ox - 7, oy + 1, ox - 1, oy + 7, Tree);
        _world.Editor.Undo();
        await Frames(10);
        Say("Then trees over the hill with Maximum slope 0: only the flat tiles get a tree, the slopes stay bare.");
        await SetNumber("Maximum slope", 0);
        await Shot(2.5);
        _overlay.ClearMarks();
        await Stroke(new List<(int, int)> { (ox - 4, oy + 4) });
        int flatOnly = StaticCount(ox - 7, oy + 1, ox - 1, oy + 7, Tree);
        Check(flatOnly < allSlopes, $"Maximum slope 0 skipped the sloping tiles ({flatOnly} against {allSlopes})");
        await Shot();
        _world.Editor.Undo();
        await Frames(10);
        _overlay.ClearMarks();
        await SetNumber("Maximum slope", 127);
        await SetNumber("Strength", 1);
        _overlay.ClearMarks();

        Say("Terrain edge rules choose the tile used where a painted patch meets other ground, so a pond gets a shore. Each rule is a side number and a tile: east is 2, west is 8.");
        await Press("Terrain");
        await SetNumber("Size (tiles)", 3);
        await SetNumber("Density (%)", 100);
        await SetCheck("Square footprint", true);
        await SetText(_world.TourEdges, "Terrain edge rules", "2=0x00A9,8=0x00AA");
        await Shot(2.5);
        _overlay.ClearMarks();
        await ChooseArt("0x00A8");
        _overlay.ClearMarks();
        int u = _world.Editor.UndoCount;
        await Stroke(Line(ox - 6, oy - 2, ox - 2, oy - 2));
        Check(_world.Editor.UndoCount == u + 1, "a stroke with edge rules is one undo step");
        Say("The water patch gets its edge tiles on the east and west sides.");
        await Shot();
        _overlay.ClearMarks();
        await SetText(_world.TourEdges, "Terrain edge rules", "");
        _overlay.ClearMarks();
        _world.SetKeepStaticsForSmoke(true);
        _world.ForcedMouse = null;
    }

    // --- Saved presets and favourites -------------------------------------------------------------------------------------

    private async Task WePresets()
    {
        if (!await NeedWe())
        {
            return;
        }

        await OpenWe(EditX, EditY);
        Say("Saved presets keep a whole brush: its size, density, rules, variations, item and colour. First a brush to keep: Scatter, size 9, density 20.");
        await Rail(WorldTool.Brush);
        await Press("Scatter");
        await SetNumber("Size (tiles)", 9);
        await SetNumber("Density (%)", 20);
        await Shot();
        _overlay.ClearMarks();

        Say("Open Saved presets under the library, give the brush a name and press Save preset.");
        Button section = _world.TourButton("Saved presets");
        if (section != null && !_world.TourPresetName.IsVisibleInTree())
        {
            MarkControl(section, "Saved presets");
            PointAt(section);
            await Shot(2.5);
            section.ButtonPressed = true;
            await Frames(6);
            _overlay.ClearMarks();
        }

        await SetText(_world.TourPresetName, "name", "wide and thin");
        int before = _world.TourPresets.ItemCount;
        await Press("Save preset");
        await Frames(4);
        Check(_world.TourPresets.ItemCount == before + 1, $"Save preset added 'wide and thin' to the list ({_world.TourPresets.ItemCount})");
        await Reveal(_world.TourPresets);
        MarkControl(_world.TourPresets, "saved");
        await Shot();
        _overlay.ClearMarks();

        Say("Change the brush (size 2, density 90), then pick the preset and press Load: size 9 and density 20 come back.");
        await SetNumber("Size (tiles)", 2);
        await SetNumber("Density (%)", 90);
        await Shot(2.5);
        _overlay.ClearMarks();
        _world.TourPresets.Select(_world.TourPresets.ItemCount - 1);
        await Press("Load");
        await Frames(4);
        Check(_world.TourRecipe.Size == 9 && _world.TourRecipe.Density == 20, "Load brought back size 9 and density 20");
        MarkControl(_world.TourNumber("Size (tiles)"), "Size");
        MarkControl(_world.TourNumber("Density (%)"), "Density");
        await Shot();
        _overlay.ClearMarks();
        section?.SetPressedNoSignal(false);
        if (section != null)
        {
            section.ButtonPressed = false;
        }

        Say("Favourites: + Favorite keeps the chosen item as a one-click button under the library. A tree, then a crate:");
        UseArt(Tree);
        int f0 = _world.TourFavoriteCount;
        await Press("+ Favorite");
        _overlay.ClearMarks();
        UseArt(_stampArt);
        await Press("+ Favorite");
        await Frames(4);
        Check(_world.TourFavoriteCount == f0 + 2, $"two favourites were kept ({_world.TourFavoriteCount})");
        await Shot();
        _overlay.ClearMarks();

        Say("The Q key opens the same favourites over the map, where you are working. Q again closes them.");
        Aim(Centre);
        await Frames(4);
        _overlay.ClearMarks();
        await KeyPress(Key.Q);
        await Frames(6);
        Check(_world.TourQuickFavorites.Visible, "Q showed the favourites over the map");
        MarkControl(_world.TourQuickFavorites, "favourites");
        await Shot();
        _overlay.ClearMarks();
        await KeyPress(Key.Q);
        Check(!_world.TourQuickFavorites.Visible, "Q again closed them");
        _world.ForcedMouse = null;
    }
}
#endif
