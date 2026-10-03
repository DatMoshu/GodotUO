#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The Multi Editor smoke stage, phase 2: the generator panel's preview and Apply, the placement tools, rotate and
/// mirror, copy and paste, stamps, the legacy formats, the unsaved prompt. It runs after the phase 1 checks of
/// <see cref="EditorSmoke.RunMultiEditAsync"/> and shares its report.
/// </summary>
public partial class EditorSmoke
{
    private async Task<bool> WaitFor(Func<bool> done, double seconds)
    {
        double t = 0;
        while (!done() && t < seconds)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            t += GetProcessDeltaTime();
        }

        return done();
    }

    private string Describe(ValidationResult r) =>
        string.Join("; ", r.Findings.Where(f => f.Severity == FindingSeverity.Error).Take(4).Select(f => $"{f.Kind} {f.Message}"));

    private async Task RunMultiEditPhase2Async(MultiEditView view, string root, ushort floorId)
    {
        MultiCanvas canvas = view.Canvas;
        MultiDocument doc = view.Doc;
        GeneratePanel panel = view.GeneratePanel;

        // --- the Generate panel is mounted and keeps one process --------------------------------------------------
        MeCheck("generate_tab_mounted", view.TabNames.Contains("Generate") && panel != null);
        bool styles = await WaitFor(() => panel.StylesLoaded.IsCompleted, 30);
        MeCheck("generate_styles_listed", styles && panel.StyleCount > 0, view.GenClient?.Why ?? "no styles");
        if (!styles || panel.StyleCount == 0)
        {
            return;
        }

        _meReport["generate_styles"] = panel.StyleCount;
        view.NewMulti();
        view.ShowGenerateTab();
        panel.SelectGenerator("house");
        int steps = doc.HistoryCount;
        GenResult house = await panel.NowAsync();
        MeCheck("generate_house_ok", house is { Ok: true } && house.Components.Count > 20, house?.Error);
        MeCheck("generate_preview_is_ghost", canvas.GhostCount == house.Components.Count && view.GeneratorGhostShown && doc.Parts.Count == 0 && doc.HistoryCount == steps,
            $"ghost {canvas.GhostCount} doc {doc.Parts.Count}");
        int? pid = view.GenClient.ProcessId;
        GenResult again = await panel.NowAsync();
        MeCheck("generate_one_process", pid != null && view.GenClient.ProcessId == pid && again.Ok);
        _meReport["generate_house_components"] = house.Components.Count;
        _meReport["generate_ms"] = Math.Round(house.Ms, 1);

        // Apply replaces in one undo step; undo takes it back; redo again.
        bool applied = await view.ApplyGeneratedAsync(true);
        MeCheck("generate_apply_replace_one_step", applied && doc.Parts.Count == house.Components.Count && doc.HistoryCount == steps + 1 && canvas.GhostCount == 0,
            $"{doc.Parts.Count} history {doc.HistoryCount}");
        ValidationResult v = view.ValidateNow();
        _meReport["generate_house_findings"] = string.Join(",", v.Findings.Select(f => f.Kind).Distinct().OrderBy(k => k));
        MeCheck("generate_house_validator_no_errors", !v.HasErrors, Describe(v));
        doc.Undo();
        MeCheck("generate_apply_undo", doc.Parts.Count == 0);
        doc.Redo();
        MeCheck("generate_apply_redo", doc.Parts.Count == house.Components.Count);

        // A change of a control regenerates into the preview by itself (debounced), and Apply (add) keeps what is there.
        int before = doc.Parts.Count;
        panel.SelectGenerator("house");
        panel.SelectStyle(panel.Styles[^1].Key);
        bool ghosted = await WaitFor(() => canvas.GhostCount > 0, 10);
        MeCheck("generate_control_change_regenerates", ghosted);
        bool added = await view.ApplyGeneratedAsync(false);
        MeCheck("generate_apply_add", added && doc.Parts.Count > before && doc.HistoryNames[^1].StartsWith("add house"), doc.HistoryNames[^1]);
        doc.Undo();
        MeCheck("generate_add_undo", doc.Parts.Count == before);
        view.NewMulti();
        await MeToolsAsync(view, floorId);
        view.NewMulti();
        await MeClipboardAsync(view, root);
        view.NewMulti();
    }

    private async Task MeClipboardAsync(MultiEditView view, string root)
    {
        MultiCanvas canvas = view.Canvas;
        MultiDocument doc = view.Doc;
        GeneratePanel panel = view.GeneratePanel;
        view.StampsDir = Path.Combine(root, "stamps");
        view.ShowGenerateTab();
        panel.SelectGenerator("house");
        // The house generator is deterministic (same seed, same bytes), so this is the same building every run.
        await view.ApplyGeneratedAsync(true);
        int total = doc.Parts.Count;

        // Erase a whole roof and a whole stair.
        MultiPart roof = doc.Parts.FirstOrDefault(p => view.GroupOf(p.Uid).Count > 1 && p.Z >= 27);
        bool haveRoof = roof.Uid != 0;
        MeCheck("group_finds_a_roof_or_stair", haveRoof);
        if (haveRoof)
        {
            int n = view.GroupOf(roof.Uid).Count;
            MeCheck("erase_group_one_step", view.EraseGroup(roof.Uid) && doc.Parts.Count == total - n && doc.HistoryNames[^1].StartsWith("erase "), $"{n} group, {doc.Parts.Count} of {total}");
            doc.Undo();
            MeCheck("erase_group_undo", doc.Parts.Count == total);
            _meReport["erase_group_size"] = n;
        }

        MultiPart stair = doc.Parts.FirstOrDefault(p => view.GroupOf(p.Uid).Count > 1 && view.GroupOf(p.Uid).Count != (haveRoof ? view.GroupOf(roof.Uid).Count : -1));
        if (stair.Uid != 0)
        {
            int n = view.GroupOf(stair.Uid).Count;
            MeCheck("erase_second_group", view.EraseGroup(stair.Uid) && doc.Parts.Count == total - n);
            doc.Undo();
        }

        // Copy, paste with a ghost, cut.
        doc.Selection.Clear();
        foreach (MultiPart p in doc.Parts.Take(10))
        {
            doc.Selection.Add(p.Uid);
        }

        MeCheck("copy_selection", view.CopySelection() && view.ClipboardCount == 10);
        int steps = doc.HistoryCount, cursor = doc.Cursor;
        MeCheck("paste_starts_ghost", view.PasteClipboard() && canvas.GhostFollowsMouse && canvas.GhostCount == 10 && doc.HistoryCount == steps);
        canvas.PlaceGhostAt(40, 40);
        var added = doc.Parts.Skip(total).ToList();
        MeCheck("paste_places_one_step", added.Count == 10 && doc.Cursor == cursor + 1 && !canvas.GhostFollowsMouse && canvas.GhostCount == 0 && doc.HistoryNames[^1] == "paste 10",
            $"added {added.Count} history {doc.HistoryCount} vs {steps + 1}, follow {canvas.GhostFollowsMouse}, ghost {canvas.GhostCount}, last {doc.HistoryNames[^1]}");
        MeCheck("paste_lands_at_pointer", added.Count > 0 && Math.Abs(added.Average(p => p.X) - 40) <= 6 && Math.Abs(added.Average(p => p.Y) - 40) <= 6);
        doc.Undo();
        MeCheck("paste_undo", doc.Parts.Count == total);
        doc.Redo();
        doc.Selection.Clear();
        foreach (MultiPart p in doc.Parts.Skip(total))
        {
            doc.Selection.Add(p.Uid);
        }

        MeCheck("cut_removes_and_copies", view.CutSelection() && doc.Parts.Count == total && view.ClipboardCount == 10);
        view.PasteClipboard();
        canvas._GuiInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        MeCheck("paste_escape_cancels", !canvas.GhostFollowsMouse && canvas.GhostCount == 0 && doc.Parts.Count == total);

        // A stamp round trip: save the selection, place it elsewhere, the same pieces and hues.
        MultiPart first = doc.Parts[0];
        doc.SetHue(new[] { first.Uid }, 9);
        doc.Selection.Clear();
        foreach (MultiPart p in doc.Parts.Take(8))
        {
            doc.Selection.Add(p.Uid);
        }

        var saved = Sig(doc).Where(t => doc.Parts.Take(8).Any(p => p.Id == t.Item1 && p.X == t.Item2 && p.Y == t.Item3 && p.Z == t.Item4)).ToList();
        string path = view.SaveStamp("smoke stamp");
        MeCheck("stamp_saved_under_stamps_dir", path != null && File.Exists(path) && view.Stamps().Contains("smoke_stamp"), path);
        MeCheck("stamp_file_is_description", path != null && MultiStore.FromJson(File.ReadAllText(path)).Parts.Count == 8);
        int before = doc.Parts.Count;
        MeCheck("stamp_place_ghost", view.PlaceStamp("smoke_stamp") && canvas.GhostCount == 8 && canvas.GhostFollowsMouse);
        canvas.PlaceGhostAt(-40, -40);
        var placed = doc.Parts.Skip(before).ToList();
        var origin = doc.Parts.Take(8).Select(p => (p.Id, p.Hue)).OrderBy(t => t).ToList();
        MeCheck("stamp_round_trip", placed.Count == 8 && placed.Select(p => (p.Id, p.Hue)).OrderBy(t => t).SequenceEqual(origin) && placed.Any(p => p.Hue == 9),
            $"{placed.Count} placed");
        MeCheck("stamp_delete", view.DeleteStamp("smoke_stamp") && !view.Stamps().Contains("smoke_stamp"));
        MeCheck("stamps_not_in_install", Path.GetFullPath(view.StampsDir).StartsWith(Path.GetFullPath(_out), StringComparison.OrdinalIgnoreCase));
    }

    private static List<(int, int, int, int, bool, int)> Sig(MultiDocument doc) =>
        doc.Parts.Select(p => ((int)p.Id, (int)p.X, (int)p.Y, (int)p.Z, p.Shown, (int)p.Hue)).OrderBy(t => t).ToList();

    private Vector2 Cell(MultiCanvas canvas, int x, int y) => canvas.ScreenOf(x, y, canvas.EditZ);

    private void ClickCell(MultiCanvas canvas, int x, int y)
    {
        Vector2 at = Cell(canvas, x, y);
        canvas._GuiInput(Move(at));
        canvas._GuiInput(Click(at, true));
        canvas._GuiInput(Click(at, false));
    }

    private async Task MeToolsAsync(MultiEditView view, ushort floorId)
    {
        MultiCanvas canvas = view.Canvas;
        MultiDocument doc = view.Doc;
        GeneratePanel panel = view.GeneratePanel;
        canvas.SetEditZ(7);

        // Wall run: click a path, click its first point to close it; autowall applies as one step.
        view.NewMulti();
        view.SetTool(MultiTool.WallRun);
        MeCheck("wall_tool_selects_autowall", panel.Generator == "autowall");
        foreach ((int x, int y) in new[] { (0, 0), (8, 0), (8, 6), (0, 6), (0, 0) })
        {
            ClickCell(canvas, x, y);
        }

        bool walled = await WaitFor(() => doc.Parts.Count > 0, 10);
        MeCheck("wall_tool_builds_walls", walled && doc.HistoryNames[^1].StartsWith("add autowall"), doc.HistoryNames[^1]);
        ValidationResult v = view.ValidateNow();
        MeCheck("wall_tool_validator_no_errors", !v.HasErrors, Describe(v));
        MeCheck("wall_tool_generator_problems_none", panel.Last?.Problems.Count == 0, string.Join(";", panel.Last?.Problems ?? new List<string>()));
        _meReport["wall_components"] = doc.Parts.Count;
        MeCheck("wall_tool_state_cleared", canvas.WallPath.Count == 0 && canvas.GhostCount == 0);

        // Roof: drag a box over the walls.
        int wallParts = doc.Parts.Count;
        view.SetTool(MultiTool.Roof);
        Vector2 a = Cell(canvas, 0, 0), b = Cell(canvas, 8, 6);
        canvas._GuiInput(Move(a));
        canvas._GuiInput(Click(a, true));
        canvas._GuiInput(Move(b));
        canvas._GuiInput(Click(b, false));
        MeCheck("roof_tool_box", canvas.RoofBox is { X0: 0, Y0: 0, X1: 8, Y1: 6 });
        bool apply = await view.ApplyGeneratedAsync(false);
        MeCheck("roof_tool_applies", apply && doc.Parts.Count > wallParts && doc.Parts.Skip(wallParts).All(p => p.Z >= 27), $"{doc.Parts.Count - wallParts} new");
        v = view.ValidateNow();
        MeCheck("roof_tool_validator_no_errors", !v.HasErrors, Describe(v));
        _meReport["roof_components"] = doc.Parts.Count - wallParts;

        // Stairs: a floor upstairs, drag the direction, the hole is cut in the same step.
        view.NewMulti();
        var cells = Enumerable.Range(0, 7).SelectMany(x => Enumerable.Range(0, 11).Select(y => (x, y)));
        doc.PlaceMany("floor above", floorId, cells, 27);
        int floorCount = doc.Parts.Count, steps = doc.HistoryCount;
        view.SetTool(MultiTool.Stairs);
        a = Cell(canvas, 2, 1);
        b = Cell(canvas, 2, 4);
        canvas._GuiInput(Move(a));
        canvas._GuiInput(Click(a, true));
        canvas._GuiInput(Move(b));
        canvas._GuiInput(Click(b, false));
        MeCheck("stairs_tool_flight", canvas.Flight is { X: 2, Y: 1, Rise: "S" });
        apply = await view.ApplyGeneratedAsync(false);
        var holes = ((JsonArray)panel.Last.Raw["holes"]).Select(h => ((int)h[0], (int)h[1])).ToHashSet();
        bool open = !doc.Parts.Any(p => p.Id == floorId && p.Z == 27 && holes.Contains((p.X, p.Y)));
        MeCheck("stairs_tool_cuts_hole_one_step", apply && open && doc.HistoryCount == steps + 1 && doc.Parts.Count == floorCount - holes.Count + panel.Last.Components.Count,
            $"{doc.Parts.Count} holes {holes.Count} history {doc.HistoryCount - steps}");
        v = view.ValidateNow();
        MeCheck("stairs_tool_validator_no_errors", !v.HasErrors, Describe(v));
        doc.Undo();
        MeCheck("stairs_tool_undo_closes_hole", doc.Parts.Count == floorCount);
        doc.Redo();

        // Rotate x4 and mirror x2 return to the original (stairs and straight walls; corner and hip pieces are
        // not an involution in tools/multi's remap table: reported in the notes).
        List<(int, int, int, int, bool, int)> original = Sig(doc);
        for (int i = 0; i < 4; i++)
        {
            await view.TransformAsync("rotate", 1);
            if (i == 0)
            {
                MeCheck("rotate_changes_layout", !Sig(doc).SequenceEqual(original));
            }
        }

        MeCheck("rotate_x4_returns_original", Sig(doc).SequenceEqual(original), $"{doc.Parts.Count} vs {original.Count}");
        await view.TransformAsync("mirror", axis: "x");
        await view.TransformAsync("mirror", axis: "x");
        MeCheck("mirror_x2_returns_original", Sig(doc).SequenceEqual(original));
        await view.TransformAsync("mirror", axis: "y");
        await view.TransformAsync("mirror", axis: "y");
        MeCheck("mirror_y2_returns_original", Sig(doc).SequenceEqual(original));
        // On a selection: the stairs only, four turns, the floor stays.
        doc.Selection.Clear();
        foreach (MultiPart p in doc.Parts.Where(p => p.Z < 27))
        {
            doc.Selection.Add(p.Uid);
        }

        int stairParts = doc.Selection.Count;
        for (int i = 0; i < 4; i++)
        {
            await view.TransformAsync("rotate", 1);
        }

        MeCheck("rotate_selection_x4_returns_original", doc.Selection.Count == stairParts && Sig(doc).SequenceEqual(original), $"{Sig(doc).Count} vs {original.Count}");
        doc.Selection.Clear();
        int rotHistory = doc.HistoryCount;
        await view.TransformAsync("rotate", 1);
        MeCheck("rotate_is_one_undo_step", doc.HistoryCount == rotHistory + 1);
        doc.Undo();
        MeCheck("rotate_undo", Sig(doc).SequenceEqual(original));
    }
}
#endif
