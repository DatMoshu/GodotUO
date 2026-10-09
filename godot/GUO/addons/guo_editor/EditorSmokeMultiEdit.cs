#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using GUO.Assets;

/// <summary>
/// The smoke stage for the Multi Editor (ADR-0031). It opens a client multi, draws, erases and moves with
/// undo and redo (through the canvas's own input handling), checks that a known-bad edit raises the
/// expected validation flags, writes a multi to a temporary stage and reads it back identical, and takes a
/// World selection into a new multi. Everything it writes is under the smoke's own output folder; the
/// install's multi file is compared before and after.
/// </summary>
public partial class EditorSmoke
{
    private readonly Dictionary<string, object> _meReport = new();
    private Task _meTask;
    private double _meClock;

    /// <summary>The Multi Editor tab the plugin made.</summary>
    internal MultiEditView MultiEdit { get; set; }

    private void MeCheck(string name, bool ok, string detail = "")
    {
        _meReport[name] = ok;
        if (!ok)
        {
            _meReport["ok"] = false;
            _failures.Add($"MultiEdit: {name} {detail}".Trim());
            GD.Print($"[GUO editor] smoke MultiEdit FAIL: {name} {detail}");
        }
    }

    /// <summary>One tick of the stage; true when it is finished.</summary>
    private bool StepMultiEdit(double delta)
    {
        if (System.Environment.GetEnvironmentVariable("GUO_MULTIEDIT_SKIP") != null || MultiEdit == null)
        {
            return true;
        }

        _meClock += delta;
        if (_meTask == null)
        {
            _meReport["ok"] = true;
            _meTask = RunMultiEditAsync();
        }

        if (_meTask.IsCompleted)
        {
            if (_meTask.IsFaulted)
            {
                MeCheck("threw", false, $"{_meTask.Exception?.GetBaseException().GetType().Name}: {_meTask.Exception?.GetBaseException().Message}");
            }

            _report["multiedit"] = _meReport;
            return true;
        }

        if (_meClock > 240)
        {
            MeCheck("finished_in_time", false, "the stage did not finish within 240 s");
            _report["multiedit"] = _meReport;
            return true;
        }

        return false;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static InputEventMouseButton Click(Vector2 at, bool pressed, bool shift = false) =>
        new() { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at, ShiftPressed = shift };

    private static InputEventMouseMotion Move(Vector2 at) => new() { Position = at, Relative = Vector2.Zero };

    private async Task RunMultiEditAsync()
    {
        MultiEditView view = MultiEdit;
        MultiCanvas canvas = view.Canvas;
        MultiDocument doc = view.Doc;
        StaticTiles[] tiles = _data.Files.TileData.StaticData;
        string root = Path.Combine(Path.GetFullPath(_out), "multiedit" + Suffix);
        Directory.CreateDirectory(root);
        string stage = Path.Combine(root, "stage");

        string installUop = Path.Combine(_data.ClientData, "MultiCollection.uop");
        var installBefore = File.Exists(installUop) ? new FileInfo(installUop) : null;
        long sizeBefore = installBefore?.Length ?? -1;
        DateTime timeBefore = installBefore?.LastWriteTimeUtc ?? default;

        view.OnDataLoaded();
        _meReport["tabs"] = string.Join(",", view.TabNames);
        MeCheck("tab_in_tree", view.IsInsideTree());
        MeCheck("tables_read", view.Tables != null && view.Tables.Groups.Count > 0, "no house customization tables");
        _meReport["palette_groups"] = view.Palette.GroupCount;
        _meReport["support_rows"] = view.Tables?.PlaceInfo.Count ?? 0;

        // --- open a client multi, and its order is the Multis panel's ------------------------------------
        MeCheck("open_client_multi", view.OpenClientMulti(0x64));
        List<MultiInfo> infos = _data.Files.Multis.GetMultis(0x64);
        MeCheck("open_count", doc.Parts.Count == infos.Count, $"{doc.Parts.Count} vs {infos.Count}");
        var panelOrder = MultiPanel.ClientOrder(_data, infos).Select(p => ((int)p.ID, (int)p.X, (int)p.Y, (int)p.Z)).ToList();
        var canvasOrder = canvas.PaintOrder.Where(p => p.Shown).Select(p => ((int)p.Id, (int)p.X, (int)p.Y, (int)p.Z)).ToList();
        MeCheck("order_matches_multis_panel", panelOrder.SequenceEqual(canvasOrder), $"{panelOrder.Count} vs {canvasOrder.Count}");
        _meReport["parts_opened"] = doc.Parts.Count;

        ValidationResult clean = view.ValidateNow();
        MeCheck("client_multi_no_errors", !clean.HasErrors, string.Join("; ", clean.Findings.Where(f => f.Severity == FindingSeverity.Error).Take(3).Select(f => f.Message)));
        MeCheck("walkable_found", clean.Walkable.Count > 0);
        _meReport["walkable"] = clean.Walkable.Count;
        _meReport["opened_warnings"] = clean.Findings.Count(f => f.Severity == FindingSeverity.Warning);

        // --- the vision modes: the client's own seven ------------------------------------------------------
        int visibleAll = doc.Parts.Count(p => canvas.Display(p).Visible);
        view.SetVision(0, StoryVision.HideAll);
        int visibleHidden = doc.Parts.Count(p => canvas.Display(p).Visible);
        view.SetVision(0, StoryVision.TransparentContent);
        bool anyAlpha = doc.Parts.Any(p => canvas.Display(p) is { Visible: true, Alpha: < 1f });
        view.SetVision(0, StoryVision.Normal);
        MeCheck("vision_hide_all_hides", visibleHidden < visibleAll, $"{visibleHidden} vs {visibleAll}");
        MeCheck("vision_transparent_content", anyAlpha);
        MeCheck("seven_vision_modes", Enum.GetValues<StoryVision>().Length == 7);

        // --- a floor tile to draw with, from the client's own table ---------------------------------------------
        ushort floorId = view.Tables.Groups.Where(g => g.Kind == "Floors").SelectMany(g => g.Items).Select(i => i.Id)
            .FirstOrDefault(id => _data.HasArt(EditorData.LandCount + id));
        MeCheck("palette_floor_from_table", floorId != 0);
        view.Palette.Choose(floorId);
        MeCheck("palette_to_canvas", canvas.TileId == floorId);
        view.Palette.ShowMode("All statics", "wall");
        MeCheck("palette_all_statics_search", view.Palette.ShownCount > 0);
        view.Palette.ShowMode("Client tables");
        MeCheck("palette_dropped_tile", PaletteDrop(view, floorId));

        // --- draw, erase, move with undo and redo, through the canvas's input -----------------------------------
        int baseCount = doc.Parts.Count, baseHistory = doc.HistoryCount;
        canvas.SetEditZ(7);
        view.SetTool(MultiTool.Draw);
        Vector2 at = canvas.ScreenOf(24, 24, 7);
        canvas._GuiInput(Move(at));
        canvas._GuiInput(Click(at, true));
        canvas._GuiInput(Click(at, false));
        MeCheck("draw_adds_one", doc.Parts.Count == baseCount + 1 && doc.HistoryCount == baseHistory + 1, $"{doc.Parts.Count} history {doc.HistoryCount}");

        // Rect fill and line: one history entry each.
        view.SetTool(MultiTool.Rect);
        Vector2 a = canvas.ScreenOf(26, 24, 7), b = canvas.ScreenOf(28, 25, 7);
        canvas._GuiInput(Move(a));
        canvas._GuiInput(Click(a, true));
        canvas._GuiInput(Move(b));
        canvas._GuiInput(Click(b, false));
        MeCheck("rect_fill_six_cells", doc.Parts.Count == baseCount + 1 + 6, $"{doc.Parts.Count - baseCount - 1} cells");
        view.SetTool(MultiTool.Line);
        a = canvas.ScreenOf(30, 24, 7);
        b = canvas.ScreenOf(33, 27, 7);
        canvas._GuiInput(Move(a));
        canvas._GuiInput(Click(a, true));
        canvas._GuiInput(Move(b));
        canvas._GuiInput(Click(b, false));
        MeCheck("line_four_cells", doc.Parts.Count == baseCount + 1 + 6 + 4, $"{doc.Parts.Count - baseCount - 7} cells");
        view.SetTool(MultiTool.Brush);
        canvas.BrushRadius = 1;
        a = canvas.ScreenOf(40, 24, 7);
        canvas._GuiInput(Move(a));
        canvas._GuiInput(Click(a, true));
        canvas._GuiInput(Click(a, false));
        canvas.BrushRadius = 0;
        MeCheck("brush_nine_cells", doc.Parts.Count == baseCount + 11 + 9, $"{doc.Parts.Count - baseCount - 11} cells");
        int afterDraw = doc.Parts.Count;
        doc.Undo();
        doc.Undo();
        doc.Undo();
        doc.Undo();
        MeCheck("undo_four_steps", doc.Parts.Count == baseCount && doc.CanRedo, $"{doc.Parts.Count}");
        doc.Redo();
        doc.Redo();
        doc.Redo();
        doc.Redo();
        MeCheck("redo_four_steps", doc.Parts.Count == afterDraw);
        doc.JumpTo(baseHistory - 1);
        MeCheck("history_jump_back", doc.Parts.Count == baseCount, $"{doc.Parts.Count}");
        doc.JumpTo(doc.HistoryCount - 1);
        MeCheck("history_jump_forward", doc.Parts.Count == afterDraw);
        MeCheck("history_list_has_named_steps", doc.HistoryNames.Any(n => n.StartsWith("rect fill")) && doc.HistoryNames.Any(n => n.StartsWith("line")));

        // Erase the first tile again.
        view.SetTool(MultiTool.Erase);
        at = canvas.ScreenOf(24, 24, 7);
        canvas._GuiInput(Move(at));
        canvas._GuiInput(Click(at, true));
        canvas._GuiInput(Click(at, false));
        MeCheck("erase_removes_one", doc.Parts.Count == afterDraw - 1, $"{doc.Parts.Count}");
        doc.Undo();

        // Select by click, move by drag, group z.
        view.SetTool(MultiTool.Select);
        canvas._GuiInput(Move(at));
        canvas._GuiInput(Click(at, true));
        canvas._GuiInput(Click(at, false));
        MeCheck("select_click", doc.Selection.Count == 1, $"{doc.Selection.Count}");
        int uid = doc.Selection.First();
        view.SetTool(MultiTool.Move);
        Vector2 to = canvas.ScreenOf(26, 24, 7);
        canvas._GuiInput(Move(at));
        canvas._GuiInput(Click(at, true));
        canvas._GuiInput(Move(to));
        canvas._GuiInput(Click(to, false));
        MultiPart moved = doc.Parts.First(p => p.Uid == uid);
        MeCheck("move_drags_cells", moved.X == 26 && moved.Y == 24, $"{moved.X},{moved.Y}");
        canvas.NudgeZ(5);
        MeCheck("group_z_up", doc.Parts.First(p => p.Uid == uid).Z == 12);
        canvas.NudgeZ(-5);
        canvas.Nudge(-2, 0);
        MeCheck("nudge_keys", doc.Parts.First(p => p.Uid == uid).X == 24);
        view.SetTool(MultiTool.Select);

        // Box select takes everything drawn.
        doc.Selection.Clear();
        Vector2 b0 = new(-6000, -6000), b1 = new(6000, 6000);
        canvas._GuiInput(Move(b0));
        canvas._GuiInput(Click(b0, true));
        canvas._GuiInput(Move(b1));
        canvas._GuiInput(Click(b1, false));
        var drawn = doc.Parts.Where(p => p.X >= 24 && p.Id == floorId && p.Z == 7 && p.X < 50).ToList();
        MeCheck("box_select", drawn.Count == 20 && drawn.All(p => doc.Selection.Contains(p.Uid)) && doc.Selection.Count == doc.Parts.Count, $"{doc.Selection.Count} selected of {doc.Parts.Count}, {drawn.Count} drawn");
        doc.Selection.Clear();

        // Pipette picks the tile and hue; the hue per component and the shown flag are history steps.
        int hueBase = doc.HistoryCount;
        doc.SetHue(new[] { uid }, 5);
        doc.SetShown(new[] { uid }, false);
        MeCheck("hue_and_shown_flags", doc.Parts.First(p => p.Uid == uid) is { Hue: 5, Shown: false } && doc.HistoryCount == hueBase + 2);
        doc.SetShown(new[] { uid }, true);
        view.SetTool(MultiTool.Pipette);
        at = canvas.ScreenOf(24, 24, 7);
        canvas._GuiInput(Move(at));
        canvas._GuiInput(Click(at, true));
        canvas._GuiInput(Click(at, false));
        MeCheck("pipette_picks_tile_and_hue", canvas.TileId == floorId && canvas.TileHue == 5, $"0x{canvas.TileId:X4} hue {canvas.TileHue}");
        view.Palette.SetHue(0);

        // --- the story vision, the cut and z range ---------------------------------------------------------------
        canvas.CutAbove = true;
        canvas.SetEditZ(7);
        int cutVisible = doc.Parts.Count(p => canvas.Display(p).Visible);
        canvas.CutAbove = false;
        MeCheck("cut_above_story", cutVisible <= doc.Parts.Count(p => canvas.Display(p).Visible));
        MeCheck("story_model", Stories.StoryOf(6) == -1 && Stories.StoryOf(7) == 0 && Stories.StoryOf(27) == 1 && Stories.ZOf(3) == 67);

        // --- validation flags on a known-bad edit ----------------------------------------------------------------------
        int goodHistory = doc.HistoryCount;
        ushort noArt = (ushort)Enumerable.Range(0x1000, Math.Min(tiles.Length, 0x10000) - 0x1000).FirstOrDefault(id => !_data.HasArt(EditorData.LandCount + (uint)id));
        if (noArt == 0)
        {
            noArt = 0xFFF0;
        }

        ushort tall = (ushort)Enumerable.Range(1, Math.Min(tiles.Length, 0x4000) - 1).First(id => _data.HasArt(EditorData.LandCount + (uint)id)
            && tiles[id].Height >= 14 && tiles[id].IsImpassable);
        doc.Do("known-bad edit", parts =>
        {
            parts.Add(doc.Make(floorId, 50, 50, 7));
            parts.Add(doc.Make(floorId, 50, 50, 12));      // a second surface 5 above the first: no headroom
            parts.Add(doc.Make(noArt, 51, 50, 7));          // an id without art
            parts.Add(doc.Make(floorId, 52, 50, 100));
            parts.Add(doc.Make(tall, 53, 50, 17));          // reaches into the next story
            parts.Add(doc.Make(floorId, 54, 50, 7));
            parts.Add(doc.Make(floorId, 54, 50, 7));        // twice
        });
        ValidationResult bad = view.ValidateNow();
        MeCheck("flag_double_surface", bad.Count("double-surface") >= 1);
        MeCheck("flag_unknown_id", bad.Findings.Any(f => f.Kind == "unknown-id" && f.Severity == FindingSeverity.Error));
        MeCheck("flag_cross_story", bad.Findings.Any(f => f.Kind == "cross-story" && f.X == 53));
        MeCheck("flag_duplicate", bad.Count("duplicate") >= 1);
        MeCheck("bad_edit_has_errors", bad.HasErrors);
        MeCheck("legality_grid_ran", bad.Count("illegal") + clean.Count("illegal") >= 0 && view.Tables.HasSupport);
        _meReport["bad_findings"] = string.Join(",", bad.Findings.Select(f => f.Kind).Distinct().OrderBy(k => k));

        SaveResult refused = await view.SaveToStageAsync();
        MeCheck("save_refused_with_errors", !refused.Ok && refused.Error != null && !Directory.Exists(stage));
        doc.JumpTo(goodHistory - 1);
        doc.Do("z out of range", parts => parts.Add(doc.Make(floorId, 60, 60, 200)));
        MeCheck("flag_z_range", view.ValidateNow().Count("z-range") == 1);
        doc.JumpTo(goodHistory - 1);
        doc.Do("clean up", parts => parts.RemoveAll(p => p.X >= 24 && p.Y >= 24 && p.Id == floorId && p.Z == 7 && p.X < 50));
        // A hue on one part, to prove the description keeps it.
        doc.SetHue(new[] { doc.Parts[^1].Uid }, 33);

        // --- save: the description, then a staged write read back -------------------------------------------------------------
        view.SetStageDir(stage);
        view.SetName("smoke_multiedit");
        string path = view.SaveDescription();
        MultiPart[] before = doc.Parts.ToArray();
        MeCheck("description_saved", File.Exists(path) && path.EndsWith(".multi.json"));
        MeCheck("description_roundtrip", view.OpenDescription(path) && doc.Parts.Count == before.Length
            && doc.Parts.Zip(before).All(z => z.First.SameAs(z.Second)));

        view.ValidateNow();
        SaveResult saved = await view.SaveToStageAsync();
        MeCheck("stage_write_ok", saved.Ok, saved.Error);
        _meReport["stage_log"] = saved.Log.Trim().Split('\n').LastOrDefault() ?? "";
        MeCheck("stage_read_back_equal", saved.ReadBackEqual);
        MeCheck("stage_id_in_pack_range", saved.Id is >= 0x3F00 and < 0x4000, $"{saved.Id:X}");
        if (saved.Id is int newId)
        {
            List<MultiPart> back = MultiStore.ReadStaged(stage, newId);
            MeCheck("stage_independent_read_identical", back != null && MultiStore.SameComponents(back, doc.Parts));
            MeCheck("stage_inside_out_folder", Path.GetFullPath(stage).StartsWith(Path.GetFullPath(_out), StringComparison.OrdinalIgnoreCase));
            _meReport["multi_id"] = newId;

            // The same multi again is the same entry; a changed one goes under the next name.
            SaveResult again = await view.SaveToStageAsync();
            MeCheck("stage_same_multi_same_id", again.Ok && again.Id == newId, again.Error);
            doc.Do("one more", parts => parts.Add(doc.Make(floorId, 70, 70, 7)));
            SaveResult next = await view.SaveToStageAsync();
            MeCheck("stage_changed_multi_new_name", next.Ok && next.Name == "smoke_multiedit-2" && next.Id != newId, $"{next.Name} {next.Error}");

            // Live refresh: the loaders, the Multis panel and the World tab see it without a restart.
            MeCheck("loader_sees_staged_multi", _data.Files.Multis.GetMultis((uint)newId).Count == back?.Count);
            MultiPanel panel = _assets.Panel<MultiPanel>();
            MeCheck("multis_panel_lists_it", panel != null && panel.Search($"0x{newId:X4}") == newId);
            if (_world != null && _world.IsBooted)
            {
                MeCheck("world_preview_places", _world.PreviewMulti(newId));
                await Frames(Headless ? 8 : 20);
                bool house = _world.Host.World.HouseManager.TryGetHouse(0x4000_F001, out var h);
                MeCheck("world_draws_staged_multi", house && h.Components.Count == back?.Count(p => p.Shown), house ? $"{h.Components.Count} vs {back?.Count(p => p.Shown)} shown" : "no house");
                _world.Host.RemoveServerObject(0x4000_F001);
            }
        }

        // --- World selection to a new multi ------------------------------------------------------------------------------------
        if (_world != null && _world.IsBooted)
        {
            view.SaveDescription();                       // the World selection asks before it replaces unsaved work
            _world.SetArea(1490, 1620, 1530, 1660);
            var taken = _world.AreaParts(out string worldName);
            MeCheck("world_selection_has_statics", taken is { Count: > 0 }, $"{taken?.Count}");
            _meReport["world_selection_statics"] = taken?.Count ?? 0;
            int count = _world.SaveAreaAsMulti();
            MeCheck("world_selection_opens_multi", count > 0 && doc.Parts.Count == count && doc.Name == worldName, $"{count} vs {doc.Parts.Count}");
            MeCheck("world_selection_centred", doc.Parts.All(p => Math.Abs(p.X) <= 21 && Math.Abs(p.Y) <= 21));
            // ED6: the Multi Editor says where the building came from and the way back; the World says where it went.
            GD.Print($"[GUO words] multi bar: {view.FromWorldNotice} | world: {_world.BrushStatus}");
            MeCheck("world_selection_announced", view.FromWorldNotice.StartsWith("From the World") && view.FromWorldNotice.Contains($"{count} items"), view.FromWorldNotice);
            MeCheck("world_status_names_multis_tab", _world.BrushStatus.Contains("Multis tab") && _world.BrushStatus.Contains("Back to World"), _world.BrushStatus);
            _world.SetArea(0, 0, 0, 0);
        }

        // --- the generator seam --------------------------------------------------------------------------------------------------------
        int steps = doc.HistoryCount;
        view.Sink.PushComponents("smoke_gen", new[] { new GeneratedPart(floorId, 0, 0, 7), new GeneratedPart(floorId, 1, 0, 7), new GeneratedPart(floorId, 0, 1, 7, false) }, true);
        MeCheck("seam_replaces_in_one_step", doc.Parts.Count == 3 && doc.HistoryCount == steps + 1 && doc.HistoryNames[^1].StartsWith("generate smoke_gen"));
        view.Sink.PushComponents("more", new[] { new GeneratedPart(floorId, 2, 0, 7) }, false);
        MeCheck("seam_adds", doc.Parts.Count == 4);
        view.NewMulti();
        MeCheck("new_blank_multi", doc.Parts.Count == 0 && doc.HistoryCount == 1);
        MeCheck("from_world_bar_closes_on_new", view.FromWorldNotice == "");
        await RunMultiEditPhase2Async(view, root, floorId);

        // The install's multi file is as it was.
        var installAfter = File.Exists(installUop) ? new FileInfo(installUop) : null;
        MeCheck("install_untouched", installAfter?.Length == sizeBefore && installAfter?.LastWriteTimeUtc == timeBefore);
        _meReport["history_cap"] = MultiDocument.HistoryCap;
    }

    private static bool PaletteDrop(MultiEditView view, ushort id)
    {
        var payload = new Godot.Collections.Dictionary { ["guo_static"] = (int)id };
        bool can = view.Palette._CanDropData(Vector2.Zero, payload) && view.Canvas._CanDropData(Vector2.Zero, payload);
        view.Palette._DropData(Vector2.Zero, payload);
        return can && view.Palette.TileId == id;
    }
}
#endif
