#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

public partial class EditorSmoke
{
    private async Task VerifyTerrainPalette(WorldProject project)
    {
        var data = new WorldData(_world.Host);
        int x = OverlayBx * 8 + 2, y = OverlayBy * 8 + 5;
        ushort tile = data.LandId(x, y) == 3 ? (ushort)22 : (ushort)3;
        sbyte z = data.LandZ(x, y);
        string path = System.IO.Path.Combine(_out, "terrain-palette.xml");
        ushort firstTile = tile == 3 ? (ushort)22 : (ushort)3;
        System.IO.File.WriteAllText(path, $"<Terrains><Terrain Name='Bank' TileID='{firstTile}'/><Terrain Name='Trial shore' ID='91' TileID='{tile}'/></Terrains>");
        var open = _world.FindChildren("*", "Button", true, false).OfType<Button>()
            .Single(b => b.Text == "Terrain palette…");
        EditorFileDialog OpenPicker()
        {
            open.EmitSignal(BaseButton.SignalName.Pressed);
            return _world.GetChildren().OfType<EditorFileDialog>().Single(d => d.Visible && !d.IsQueuedForDeletion());
        }
        void ChooseFile(string file)
        {
            var picker = OpenPicker();
            picker.CurrentPath = file;
            picker.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            if (!picker.IsQueuedForDeletion()) throw new InvalidOperationException("File picker did not accept the file");
        }
        var canceled = OpenPicker();
        canceled.GetCancelButton().EmitSignal(BaseButton.SignalName.Pressed);
        if (!canceled.IsQueuedForDeletion()) throw new InvalidOperationException("Cancel left the palette picker open");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        ChooseFile(path);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var swatches = (OptionButton)_world.FindChild("TerrainSwatches", true, false);
        if (swatches.ItemCount != 3) throw new InvalidOperationException(_world.BrushStatus);
        string invalid = System.IO.Path.Combine(_out, "altitude-table.xml");
        System.IO.File.WriteAllText(invalid, "<Altitudes/>");
        ChooseFile(invalid);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!_world.BrushStatus.Contains("Terrains root") || swatches.ItemCount != 3)
            throw new InvalidOperationException("Invalid XML did not leave the imported swatches available");
        void ChooseSwatch(int index)
        {
            swatches.ShowPopup();
            var popup = swatches.GetPopup();
            // A temporary accelerator drives Godot's native menu activation in headless runs.
            popup.SetItemAccelerator(index, Key.F9);
            using var shortcut = new InputEventKey { Keycode = Key.F9, Pressed = true };
            bool activated = popup.ActivateItemByEvent(shortcut);
            popup.SetItemAccelerator(index, Key.None);
            if (!activated || swatches.Selected != index) throw new InvalidOperationException("Popup did not activate the requested swatch");
        }
        uint previous = _data.CurrentArt;
        bool locked = _world.TerrainLocked;
        _world.TerrainLocked = false;
        int undo = _world.Editor.UndoCount;
        string before = project.BlockText(0, OverlayBx, OverlayBy);
        try
        {
            _world.QuickRecipeForSmoke("Scatter");
            _world.BrushSizeInput.Value = 11;
            var density = _world.BrushDensityInput;
            var spacing = _world.BrushSpacingInput;
            bool GeometryPreserved() => _world.BrushSizeInput.Value == 11 && density.Value == 35 && spacing.Value == 2;
            ChooseSwatch(1);
            if (_data.CurrentArt != firstTile || !GeometryPreserved())
                throw new InvalidOperationException("First palette swatch changed the selected brush size or tile");
            ChooseSwatch(2);
            if (_data.CurrentArt != tile || !GeometryPreserved())
                throw new InvalidOperationException("Second palette swatch changed the selected brush size or tile");
            _overlayReport["palette_size_after_two_swatches"] = _world.BrushSizeInput.Value;
            _overlayReport["palette_density_after_two_swatches"] = density.Value;
            _overlayReport["palette_spacing_after_two_swatches"] = spacing.Value;
            _overlayReport["palette_picker_cancel_reopen_and_invalid_recovery"] = true;
            _overlayReport["palette_selected_ids"] = new[] { firstTile, tile };
            density.Value = 100;
            spacing.Value = 1;
            _world.PlaceForSmoke(new[] { (x, y) });
            if (_world.Editor.UndoCount != undo + 1) throw new InvalidOperationException(_world.BrushStatus);
            using var reopened = WorldProject.OpenOrCreate(project.Root, "", "");
            var block = reopened.Blocks(0).Single(b => b.Bx == OverlayBx && b.By == OverlayBy);
            if (block.LandId[42] != tile || block.LandZ[42] != z)
                throw new InvalidOperationException("Reopened terrain ID, orientation or height differs");
            _overlayReport["palette_tile"] = tile;
            _overlayReport["palette_z"] = z;
        }
        finally
        {
            if (_world.Editor.UndoCount == undo + 1) _world.Editor.Undo();
            _data.CurrentArt = previous;
            _world.TerrainLocked = locked;
        }
        if (project.BlockText(0, OverlayBx, OverlayBy) != before)
            throw new InvalidOperationException("Undo failed to restore the original palette stroke block");
        _overlayReport["palette_undo_restored"] = true;
        await VerifyCoast(project, ChooseFile, ChooseSwatch);
    }

    private void VerifyBrushWorkspace()
    {
        WorldProject project = _world.Host.Project;
        WorldEditor editor = _world.Editor;
        var data = new WorldData(_world.Host);
        int x = (EditX & ~7) + 7, y = EditY;
        var a = (x >> 3, y >> 3); var b = ((x + 1) >> 3, y >> 3);
        string beforeA = project.BlockText(0, a.Item1, a.Item2), beforeB = project.BlockText(0, b.Item1, b.Item2);
        var brush = new WorldBrush { Density = 100, Spacing = 1, KeepStatics = false, AvoidWater = false, FixedHeight = true, Height = 110 };
        int undo = editor.UndoCount;
        bool changed = brush.Apply(editor, data, 0, new[] { (x, y), (x + 1, y), (x, y) }, 0x0E3D);
        Expect(changed && editor.UndoCount == undo + 1, "brush_cross_block_one_undo");
        Expect(editor.StaticsAt(0, x, y).Count(s => s.Id == 0x0E3D && s.Z == 110) == 1
            && editor.StaticsAt(0, x + 1, y).Count(s => s.Id == 0x0E3D && s.Z == 110) == 1, "brush_deduplicates_cells");
        string afterA = project.BlockText(0, a.Item1, a.Item2), afterB = project.BlockText(0, b.Item1, b.Item2);
        editor.Undo();
        Expect(project.BlockText(0, a.Item1, a.Item2) == beforeA && project.BlockText(0, b.Item1, b.Item2) == beforeB, "brush_undo_restores_both_blocks");
        editor.Redo();
        Expect(project.BlockText(0, a.Item1, a.Item2) == afterA && project.BlockText(0, b.Item1, b.Item2) == afterB, "brush_redo_restores_both_blocks");
        data.Invalidate(); brush.KeepStatics = true;
        Expect(!brush.Apply(editor, data, 0, new[] { (x, y) }, 0x0E3E) && editor.UndoCount == undo + 1, "brush_keep_statics_noop");
        editor.Undo(); data.Invalidate();

        bool threw = false;
        try
        {
            editor.EditBatch(0, new Dictionary<(int, int), Action<WorldBlock>>
            {
                [a] = block => block.LandZ[0] = 112,
                [b] = _ => throw new InvalidOperationException("injected brush failure"),
            }, "rollback test");
        }
        catch (InvalidOperationException) { threw = true; }
        Expect(threw && project.BlockText(0, a.Item1, a.Item2) == beforeA && project.BlockText(0, b.Item1, b.Item2) == beforeB, "brush_failed_batch_rolls_back");
        brush.Density = 0; brush.Operation = "Paint";
        Expect(!brush.Apply(editor, data, 0, new[] { (x, y) }, 0x0E3D), "brush_zero_density_noop");
        brush.Density = 100; brush.Land = true; brush.Operation = "Flatten"; brush.Height = 127;
        Expect(brush.Apply(editor, data, 0, new[] { (x, y), (x + 1, y) }, 0), "brush_flatten_cross_block");
        data.Invalidate();
        Expect(data.LandZ(x, y) == 127 && data.LandZ(x + 1, y) == 127, "brush_flatten_exact_height");
        editor.Undo(); data.Invalidate();
        brush.Variants = "0x0E3D:3, 0x0E3E:1";
        var choices = Enumerable.Range(0, 100).Select(i => brush.Choose(x + i, y, 0)).ToArray();
        Expect(choices.SequenceEqual(Enumerable.Range(0, 100).Select(i => brush.Choose(x + i, y, 0)))
            && choices.Distinct().Count() == 2, "brush_scatter_repeatable_with_variation");
        brush.Size = 7; brush.Square = true;
        Expect(brush.Footprint(100, 100).Count() == 49, "brush_square_footprint");
        brush.Square = false;
        Expect(brush.Footprint(100, 100).Count() < 49 && brush.Footprint(100, 100).Contains((100, 100)), "brush_round_footprint");
        _world.Tool = WorldTool.Brush;
        Expect(_world.Tool == WorldTool.Brush, "brush_icon_tool_selectable");
        _world.Tool = WorldTool.Select;
        _world.TerrainLocked = true;
        _world.ApplyBrushForSmoke(new WorldBrush { Land = true, Operation = "Flatten", Height = 42 }, new[] { (x, y) });
        Expect(_world.BrushStatus == "Terrain is locked" && project.BlockText(0, a.Item1, a.Item2) == beforeA, "brush_terrain_lock_blocks_edit");
        _world.TerrainLocked = false;
        brush.Operation = "Paint"; brush.Land = true; brush.Density = 100; brush.Variants = "";
        brush.Edges = "2=0x00A9,8=0x00AA";
        bool firstEdge = brush.Apply(editor, data, 0, new[] { (x, y) }, 0x00A8);
        data.Invalidate();
        Expect(firstEdge && brush.Plan(data, new[] { (x + 1, y) }, 0x00A8).Cells.Any(c => (c.X, c.Y) == (x, y) && c.Repair), "brush_edges_reconnect_existing_stroke");
        if (firstEdge) editor.Undo();
        Expect(project.BlockText(0, a.Item1, a.Item2) == beforeA && project.BlockText(0, b.Item1, b.Item2) == beforeB, "brush_test_restores_project");
        VerifyScatterThenSingle(editor, data, x, y);
        VerifyWorkspaceInteractions();
    }

    // ED2: Scatter is not sticky. After Scatter, one click on Single places exactly one static on one cell,
    // and the visible label names the active tool and recipe.
    private void VerifyScatterThenSingle(WorldEditor editor, WorldData data, int x, int y)
    {
        uint before = _data.CurrentArt;
        const ushort tree = 0x0E3D;
        if (!_data.HasArt(EditorData.LandCount + tree)) return;
        _world.QuickRecipeForSmoke("Scatter");
        Expect(_world.Tool == WorldTool.Brush && _world.ToolLabel == "Brush · Scatter 7×7 35%", "scatter_label_names_recipe");
        _world.QuickRecipeForSmoke("Single");
        _data.CurrentArt = EditorData.LandCount + tree;
        Expect(_world.Tool == WorldTool.Brush && _world.ToolLabel == "Brush · Single 1×1 100%", "single_label_names_recipe");
        _world.SetKeepStaticsForSmoke(false);
        int cx = x + 3, cy = y + 3, undo = editor.UndoCount;
        int near(int dx, int dy) => editor.StaticsAt(0, cx + dx, cy + dy).Count(s => s.Id == tree);
        int[] was = { near(0, 0), near(1, 0), near(-1, 0), near(0, 1), near(0, -1), near(1, 1), near(-1, -1) };
        _world.PlaceForSmoke(new[] { (cx, cy) });
        data.Invalidate();
        Expect(editor.UndoCount == undo + 1 && near(0, 0) == was[0] + 1, "single_places_one_static");
        Expect(near(1, 0) == was[1] && near(-1, 0) == was[2] && near(0, 1) == was[3] && near(0, -1) == was[4]
            && near(1, 1) == was[5] && near(-1, -1) == was[6], "single_touches_only_one_cell");
        if (editor.UndoCount == undo + 1) editor.Undo();
        data.Invalidate();
        _world.SetKeepStaticsForSmoke(true);
        _data.CurrentArt = before;
    }

    private void VerifyWorkspaceInteractions()
    {
        Expect(EditorInterface.Singleton.IsDistractionFreeModeEnabled() && _world.InspectorHost.GetChildCount() > 0, "world_uses_left_inspector_without_native_docks");
        var size = _world.BrushSizeInput;
        var label = (Control)size.GetParent().GetChild(0);
        double original = size.Value;
        size.Value = 7;
        label.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        label.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { ButtonMask = MouseButtonMask.Left, Relative = new Vector2(12, 0) });
        Expect(size.Value == 11, "drag_label_adjusts_brush_size");
        label.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { ButtonMask = MouseButtonMask.Left, ShiftPressed = true, Relative = new Vector2(-12, 0) });
        Expect(size.Value == 10, "shift_drag_label_adjusts_finely");
        label.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { ButtonMask = MouseButtonMask.Left, Relative = new Vector2(9999, 0) });
        Expect(size.Value == 31, "drag_label_clamps_to_tool_range");
        label.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        size.Value = original;

        var art = _world.BrushLibrary;
        var box = art.SearchInput;
        uint before = _data.CurrentArt;
        int shared = Enumerable.Range(1, 1024).First(i => _data.HasArt((uint)i) && _data.HasArt(EditorData.LandCount + (uint)i));
        box.GrabFocus(); box.Text = $"0x{shared:X4}"; art.Suggestions.RefreshSuggestions();
        Expect(art.Suggestions.Visible && art.Suggestions.Results.Any(e => e.Kind == "Land") && art.Suggestions.Results.Any(e => e.Kind == "Static"), "autocomplete_combines_land_and_statics");
        Expect(_data.CurrentArt == before, "autocomplete_typing_does_not_change_brush");
        box.EmitSignal(Control.SignalName.GuiInput, new InputEventKey { Keycode = Key.Down, Pressed = true });
        box.EmitSignal(Control.SignalName.GuiInput, new InputEventKey { Keycode = Key.Enter, Pressed = true });
        Expect(!art.Suggestions.Visible && _data.CurrentArt == (uint)shared, "autocomplete_keyboard_selects_land_target");
        box.Text = "bakcpack"; art.Suggestions.RefreshSuggestions();
        Expect(art.Suggestions.Results.Any(e => e.Title.Contains("backpack", StringComparison.OrdinalIgnoreCase)), "autocomplete_reuses_f3_typo_matching");
        box.EmitSignal(Control.SignalName.GuiInput, new InputEventKey { Keycode = Key.Escape, Pressed = true });
        Expect(!art.Suggestions.Visible, "autocomplete_escape_dismisses");
        box.ReleaseFocus(); _data.CurrentArt = before;
        art.SelectKind(before < EditorData.LandCount); art.Search("");
    }
}
#endif
