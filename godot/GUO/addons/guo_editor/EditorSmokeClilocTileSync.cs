#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Godot;

public partial class EditorSmoke
{
    private void TileSyncCheck(string name, bool ok) => ClilocCheck("tile_sync_" + name, ok);
    private static byte[] TileSyncFixture(bool modern, int count, IReadOnlyDictionary<int, byte[]> names)
    {
        int land = 512 * (4 + 32 * (modern ? 30 : 26)), record = modern ? 41 : 37, block = 4 + 32 * record;
        byte[] bytes = new byte[land + (count / 32) * block];
        foreach (var row in names) row.Value.AsSpan().CopyTo(bytes.AsSpan(land + (row.Key / 32) * block + 4 + (row.Key % 32) * record + record - 20, row.Value.Length));
        return bytes;
    }
    private void CheckClilocTileSync()
    {
        string root = Path.Combine(_out, "tile-sync" + Suffix + "-" + Guid.NewGuid().ToString("N")), project = Path.Combine(root, "project"), install = Path.Combine(root, "install");
        Directory.CreateDirectory(install); string tiledata = Path.Combine(install, "tiledata.mul"), source = Path.Combine(install, "Cliloc.test");
        var names = new Dictionary<int, byte[]> { [0] = Encoding.ASCII.GetBytes("same"), [1] = Encoding.ASCII.GetBytes("renamed"),
            [2] = Encoding.ASCII.GetBytes(" \t"), [4] = Encoding.ASCII.GetBytes("new"), [5] = Encoding.ASCII.GetBytes("kept\0ignored"),
            [6] = Encoding.ASCII.GetBytes("abcdefghijklmnopqrst"), [7] = new byte[] { 99, 97, 102, 0xE9 } };
        byte[] raw = TileSyncFixture(true, 32, names); File.WriteAllBytes(tiledata, raw);
        byte[] cliloc = ClilocFixture((1020000, 9, "same"), (1020001, 0xE3, "old ~1_NAME~"), (1020002, 7, "remove me"), (1020010, 5, ""), (77, 1, "unmapped")); File.WriteAllBytes(source, cliloc);
        DateTime tileStamp = File.GetLastWriteTimeUtc(tiledata), clilocStamp = File.GetLastWriteTimeUtc(source);
        TileNameSnapshot Capture(IReadOnlyList<ItemNameDraft> drafts = null) => TileNameSnapshot.Capture(tiledata, true, project, install, drafts ?? Array.Empty<ItemNameDraft>());
        ClilocDocument Open() => ClilocDocument.Open(project, install, "enu", source);
        var doc = Open(); var snapshot = Capture(); ClilocTileSyncPlan Plan() => new(doc, Capture(), () => Capture());
        var plan = Plan();
        TileSyncCheck("all_mapping_boundaries", new[] { 0, 0x3FFF, 0x4000, 0x7FFF, 0x8000, 0xFFFF }.Select(ClilocTileSyncPlan.ClilocId)
            .SequenceEqual(new[] { 1020000, 1036383, 1095256, 1111639, 1116792, 1149559 }) && ClilocRefused(() => ClilocTileSyncPlan.ClilocId(-1)) && ClilocRefused(() => ClilocTileSyncPlan.ClilocId(0x10000)));
        TileSyncCheck("ascii_first_nul_full_width", snapshot.Names[5] == "kept" && snapshot.Names[6] == "abcdefghijklmnopqrst" && snapshot.Names[7] == "caf?");
        TileSyncCheck("exact_add_update_remove_unchanged_plan", plan.Changes.Count == 7 && plan.Changes.Count(row => row.Kind == "Add") == 4
            && plan.Changes.Count(row => row.Kind == "Remove") == 2 && plan.Changes.Single(row => row.Kind == "Update").Warning.Length > 0
            && plan.Changes.All(row => row.Id != 77 && row.Id != 1020000));
        plan.Apply(new[] { 1020001, 1020004 });
        TileSyncCheck("selected_only_flags_retained_new_zero", doc.Get(1020001) == new ClilocDocument.Entry(0xE3, "renamed") && doc.Get(1020004)?.Flag == 0
            && doc.Get(1020002).Text == "remove me" && doc.Get(1020005) == null && doc.Get(77).Text == "unmapped");
        doc.Undo(); TileSyncCheck("whole_selection_single_undo", doc.Get(1020001).Text == "old ~1_NAME~" && doc.Get(1020004) == null && !doc.IsDirty);
        doc.Redo(); doc.Save(); TileSyncCheck("saved_patch_reopens_without_source_write", Open().Get(1020004).Text == "new" && File.ReadAllBytes(tiledata).AsSpan().SequenceEqual(raw)
            && File.ReadAllBytes(source).AsSpan().SequenceEqual(cliloc) && File.GetLastWriteTimeUtc(tiledata) == tileStamp && File.GetLastWriteTimeUtc(source) == clilocStamp);
        Plan().Apply(new[] { 1020002, 1020010 }); TileSyncCheck("whitespace_removes_even_empty_existing_text", doc.Get(1020002) == null && doc.Get(1020010) == null);
        doc.Undo(); TileSyncCheck("remove_undo_restores_empty_and_unknown_flag", doc.Get(1020002)?.Flag == 7 && doc.Get(1020010)?.Text == "");
        plan = Plan(); TileSyncCheck("empty_foreign_selection_atomic", ClilocRefused(() => plan.Apply(Array.Empty<int>())) && ClilocRefused(() => plan.Apply(new[] { 77 })) && doc.Get(77).Text == "unmapped");
        plan = Plan(); doc.Edit(77, "intervening"); doc.Undo(); TileSyncCheck("edit_undo_invalidates_revision", ClilocRefused(() => plan.Apply(new[] { 1020005 })));
        plan = Plan(); byte[] altered = (byte[])raw.Clone(); altered[0] = 1; File.WriteAllBytes(tiledata, altered);
        TileSyncCheck("tiledata_change_invalidates_review", ClilocRefused(() => plan.Apply(new[] { 1020005 }))); File.WriteAllBytes(tiledata, raw);
        plan = Plan(); File.WriteAllBytes(source, ClilocFixture((1020001, 1, "external change")));
        TileSyncCheck("cliloc_change_invalidates_review", ClilocRefused(() => plan.Apply(new[] { 1020005 }))); File.WriteAllBytes(source, cliloc);
        byte[] record = ItemMetadataDocument.ReadRecord(tiledata, 1); var metadata = new ItemMetadataDocument(1, record);
        metadata.Edit("saved name", metadata.Current.Flags, metadata.Current.Height); metadata.Save(project, install, tiledata);
        var saved = Capture(); TileSyncCheck("saved_project_names_used", saved.Names[1] == "saved name" && saved.Origins[1] == "Saved project name");
        var flagsOnly = new ItemMetadataDocument(7, ItemMetadataDocument.ReadRecord(tiledata, 7));
        flagsOnly.Edit(flagsOnly.Current.Name, ulong.MaxValue, 1); flagsOnly.Save(project, install, tiledata);
        TileSyncCheck("flags_only_patch_keeps_source_ascii_name", Capture().Names[7] == "caf?");
        var draft = new ItemNameDraft(1, metadata.SourceHash, "日本語", "0x0", "0");
        TileSyncCheck("unsaved_project_name_overrides_saved", Capture(new[] { draft }).Names[1] == "日本語");
        TileSyncCheck("draft_revert_overrides_saved_name", Capture(new[] { draft with { Name = "renamed" } }).Names[1] == "renamed");
        ItemNameDraft live = draft;
        var pending = new ClilocTileSyncPlan(doc, Capture(new[] { live }), () => Capture(new[] { live })); live = live with { Name = "later pending" };
        TileSyncCheck("raw_metadata_name_change_invalidates_review", ClilocRefused(() => pending.Apply(new[] { 1020001 })));
        TileSyncCheck("blank_project_name_proposes_remove", new ClilocTileSyncPlan(doc, Capture(new[] { draft with { Name = " " } }), () => Capture()).Changes.Single(row => row.Id == 1020001).After == null);
        TileSyncCheck("invalid_pending_fields_and_foreign_baseline_refused", ClilocRefused(() => Capture(new[] { draft with { Name = new string('x', 21) } }))
            && ClilocRefused(() => Capture(new[] { draft with { Flags = "invalid" } })) && ClilocRefused(() => Capture(new[] { draft with { Height = "256" } }))
            && ClilocRefused(() => Capture(new[] { draft with { SourceHash = "wrong" } })) && ClilocRefused(() => Capture(new[] { draft, draft })));
        plan = new(doc, Capture(), () => Capture()); metadata.Edit("later name", 0, 0); metadata.Save(project, install, tiledata);
        TileSyncCheck("saved_metadata_change_invalidates_review", ClilocRefused(() => plan.Apply(new[] { 1020001 })));
        string manifest = ItemMetadataDocument.DocumentPath(project, install, 1); string json = File.ReadAllText(manifest); File.WriteAllText(manifest, "{}");
        TileSyncCheck("corrupt_project_patch_refuses_whole_preview", ClilocRefused(() => Capture())); File.WriteAllText(manifest, json);
        string legacy = Path.Combine(root, "legacy.mul"); File.WriteAllBytes(legacy, TileSyncFixture(false, 32, names));
        TileSyncCheck("legacy_layout_same_name_mapping", TileNameSnapshot.Capture(legacy, false, Path.Combine(root, "legacy-project"), install, Array.Empty<ItemNameDraft>()).Names.SequenceEqual(snapshot.Names));
        File.WriteAllBytes(legacy, raw[..^1]); TileSyncCheck("partial_and_wrong_layout_refused", ClilocRefused(() => TileNameSnapshot.Capture(legacy, false, project, install, Array.Empty<ItemNameDraft>()))
            && ClilocRefused(() => TileNameSnapshot.Capture(tiledata, false, project, install, Array.Empty<ItemNameDraft>())));
        File.WriteAllBytes(legacy, TileSyncFixture(true, 65568, new Dictionary<int, byte[]>()));
        TileSyncCheck("oversize_item_idspace_refused", ClilocRefused(() => TileNameSnapshot.Capture(legacy, true, project, install, Array.Empty<ItemNameDraft>())));
        string large = Path.Combine(root, "large.mul"); File.WriteAllBytes(large, TileSyncFixture(true, 10016, Enumerable.Range(0, 10016).ToDictionary(id => id, id => Encoding.ASCII.GetBytes("item" + id))));
        var many = TileNameSnapshot.Capture(large, true, Path.Combine(root, "large-project"), install, Array.Empty<ItemNameDraft>());
        string empty = Path.Combine(root, "Cliloc.empty"); File.WriteAllBytes(empty, ClilocFixture()); var bounded = ClilocDocument.Open(Path.Combine(root, "bounded"), install, "custom1", empty);
        var oversized = new ClilocTileSyncPlan(bounded, many, () => many);
        TileSyncCheck("selection_budget_atomic_not_truncated", ClilocRefused(() => oversized.Apply(oversized.Changes.Select(row => row.Id))) && bounded.ChangeCount == 0 && !bounded.CanUndo);
        CheckTileSyncRecoveryDrafts(project, draft, tiledata, install);
        CheckTileSyncPanel(root, source, snapshot, many);
        var actual = TileNameSnapshot.Capture(_data.Files.GetUOFilePath("tiledata.mul"), true, Path.Combine(root, "actual"), _data.ClientData, Array.Empty<ItemNameDraft>());
        TileSyncCheck("installed_source_ascii_name_and_record_count", actual.Names.Count == _data.Files.TileData.StaticData.Length && actual.Names[0x001A] == "stone wall");
        _assetReport["cliloc_tile_sync_installed_items"] = actual.Names.Count;
    }
    private void CheckTileSyncRecoveryDrafts(string project, ItemNameDraft draft, string tiledata, string install)
    {
        var settings = EditorInterface.Singleton.GetEditorSettings(); Variant prior = settings.GetProjectMetadata("guo_assets", "metadata_drafts", new Godot.Collections.Array());
        var editor = new ItemMetadataEditor(); AddChild(editor);
        try
        {
            var rows = new Godot.Collections.Array<Godot.Collections.Dictionary>();
            rows.Add(new() { ["root"] = project, ["id"] = draft.Id, ["hash"] = draft.SourceHash, ["name"] = draft.Name, ["flags"] = draft.Flags, ["height"] = draft.Height });
            settings.SetProjectMetadata("guo_assets", "metadata_drafts", rows);
            TileSyncCheck("unvisited_recovered_metadata_name_included", TileNameSnapshot.Capture(tiledata, true, project, install, editor.NameDrafts(project)).Names[1] == draft.Name);
            rows.Add(rows[0].Duplicate()); settings.SetProjectMetadata("guo_assets", "metadata_drafts", rows);
            TileSyncCheck("duplicate_recovery_drafts_refused", ClilocRefused(() => editor.NameDrafts(project)));
            settings.SetProjectMetadata("guo_assets", "metadata_drafts", new Godot.Collections.Array()); editor.Bind(_data, 0x001A);
            editor.NameInput.Text = "live sync name"; editor.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, "live sync name");
            var captured = editor.NameDrafts(_data.Assets.Root);
            TileSyncCheck("active_raw_metadata_name_captured_without_save", captured.Single(row => row.Id == 0x001A).Name == "live sync name" && editor.CurrentDocument.Current.Name != "live sync name");
        }
        finally { editor.Free(); settings.SetProjectMetadata("guo_assets", "metadata_drafts", prior); }
    }
    private void CheckTileSyncPanel(string root, string source, TileNameSnapshot snapshot, TileNameSnapshot many)
    {
        var panel = _assets.Panel<ClilocPanel>(); _assets.ShowPanel(panel);
        ClilocDocument doc = panel.OpenCustomFile("custom1", source, remember: false); var plan = new ClilocTileSyncPlan(doc, snapshot, () => snapshot);
        var dialog = ClilocTileSyncDialog.Open(panel, doc, plan);
        TileSyncCheck("native_review_owned_no_default_selection", dialog.GetParent() == panel && dialog.SelectionCount == 0 && dialog.GetOkButton().Disabled && dialog.Rows.GetRoot().GetChildCount() == 7);
        var theme = EditorInterface.Singleton.GetEditorTheme();
        TileSyncCheck("native_selection_icons_exist", theme.HasIcon("GuiChecked", "EditorIcons") && theme.HasIcon("GuiUnchecked", "EditorIcons"));
        dialog.ChooseFilter(2); dialog.SelectMatching(); TileSyncCheck("filter_selects_updates_only", dialog.SelectionCount == 1);
        dialog.ChooseFilter(1); dialog.SelectMatching(); TileSyncCheck("selection_retained_across_filters", dialog.SelectionCount == 5);
        dialog.ClearSelection(); dialog.SetSelected(1020001, true); dialog.SetSelected(1020002, true);
        dialog.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        TileSyncCheck("review_applies_selected_update_and_removal", doc.Get(1020001).Text == "renamed" && doc.Get(1020002) == null && doc.Get(1020004) == null);
        _inspector.Strings.Undo(); TileSyncCheck("inspector_undo_reverts_entire_sync", doc.Get(1020001).Text == "old ~1_NAME~" && doc.Get(1020002)?.Flag == 7);
        var paged = ClilocTileSyncDialog.Open(panel, doc, new ClilocTileSyncPlan(doc, many, () => many));
        paged.SelectMatching(); paged.NextPage();
        TileSyncCheck("all_matching_selection_spans_pages", paged.SelectionCount == many.Names.Count && paged.Rows.GetRoot().GetChildCount() == 250
            && (int)paged.Rows.GetRoot().GetFirstChild().GetMetadata(0) == 1020250);
        paged.ChooseFilter(0, "0x0000");
        TileSyncCheck("filter_keeps_hidden_selection", paged.SelectionCount == many.Names.Count && paged.Rows.GetRoot().GetChildCount() == 1);
        paged.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        TileSyncCheck("oversized_review_stays_open_no_partial_apply", paged.Status.Visible && doc.Get(1020001).Text == "old ~1_NAME~" && doc.Get(1020004) == null);
        paged.ClearSelection(); TileSyncCheck("clear_removes_hidden_selection", paged.SelectionCount == 0 && paged.GetOkButton().Disabled); paged.Free();
        var stale = ClilocTileSyncDialog.Open(panel, doc, new ClilocTileSyncPlan(doc, snapshot, () => snapshot)); stale.SelectMatching();
        _inspector.Strings.TextInput.Text = new string('x', 32768); _inspector.Strings.TextInput.EmitSignal(TextEdit.SignalName.TextChanged);
        stale.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        TileSyncCheck("new_invalid_cliloc_draft_blocks_apply", stale.Status.Visible && doc.Get(1020001).Text == "old ~1_NAME~"); _inspector.Strings.Undo(); stale.Free();
        var capture = panel.CaptureItemNames; panel.CaptureItemNames = () => Array.Empty<ItemNameDraft>();
        string priorProject = _data.Assets.Root;
        try
        {
            // The earlier metadata smoke deliberately leaves a foreign-source record
            // in its export fixture. Sync must refuse it, then work in a clean project.
            TileSyncCheck("toolbar_refuses_foreign_source_project_metadata", ClilocRefused(() => panel.OpenTileSync()));
            _data.OpenAssets(Path.Combine(root, "toolbar-project")); panel.OpenCustomFile("custom1", source, remember: false);
            var actual = panel.OpenTileSync(); TileSyncCheck("toolbar_opens_installed_sync_review", actual != null && actual.GetParent() == panel); actual.Free();
        }
        finally { panel.CaptureItemNames = capture; _data.OpenAssets(priorProject); panel.OpenCustomFile("custom1", source, remember: false); }
        TileSyncCheck("sync_icon_named_tooltipped", panel.SyncButton.Icon != null && panel.SyncButton.Text.Length == 0 && panel.SyncButton.AccessibilityName.Length > 0 && panel.SyncButton.TooltipText.Length > 0);
        panel.SelectKind(true); TileSyncCheck("speech_sync_disabled", panel.SyncButton.Disabled); panel.SelectKind(false);
        _inspector.Strings.Discard(doc); panel.ChooseLanguage("enu"); panel.Search("500207");
    }
}
#endif
