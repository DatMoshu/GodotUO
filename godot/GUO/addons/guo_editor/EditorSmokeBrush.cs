#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;

public partial class EditorSmoke
{
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
        Expect(firstEdge && brush.PlanCells(data, new[] { (x + 1, y) }, 0x00A8).Contains((x, y)), "brush_edges_reconnect_existing_stroke");
        if (firstEdge) editor.Undo();
        Expect(project.BlockText(0, a.Item1, a.Item2) == beforeA && project.BlockText(0, b.Item1, b.Item2) == beforeB, "brush_test_restores_project");
    }
}
#endif
