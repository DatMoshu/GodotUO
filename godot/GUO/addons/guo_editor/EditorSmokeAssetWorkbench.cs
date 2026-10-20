#if TOOLS
namespace GUO.Editor;

using System;
using System.Linq;
using System.IO;
using Godot;

public partial class EditorSmoke
{
    private void CheckAssetWorkbench(ArtPanel art, int id)
    {
        art.SelectKind(false); art.Search($"0x{id:X4}");
        ItemMetadataEditor editor = _inspector.Metadata;
        MetadataCheck("docked_editor_selected", editor.Visible && editor.CurrentDocument?.Id == id);
        var edit = _inspector.Current.Actions.Find(a => a.Id == "edit-image");
        _assetReport["history_icon_names"] = EditorInterface.Singleton.GetEditorTheme().GetIconList("EditorIcons")
            .Where(name => name.Contains("ndo", StringComparison.OrdinalIgnoreCase) || name.Contains("edo", StringComparison.OrdinalIgnoreCase)).ToArray();
        MetadataCheck("image_destinations_grouped", edit?.Children.Count == 3);
        MetadataCheck("toolbar_icons_named", _inspector.ActionToolbar.GetChildren().OfType<Button>().All(b => b.Icon != null && b.Text.Length == 0 && b.AccessibilityName.Length > 0 && b.TooltipText.Length > 0));
        byte[] source = editor.CurrentDocument.PreviewRecord();
        editor.NameInput.Text = new string('é', 11);
        editor.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, editor.NameInput.Text);
        MetadataCheck("docked_invalid_save_disabled", editor.SaveButton.Disabled && !editor.CommitPending());
        art.Search("0x001A"); art.Search($"0x{id:X4}");
        MetadataCheck("invalid_draft_survives_selection", editor.NameInput.Text == new string('é', 11) && editor.SaveButton.Disabled);
        editor.Undo();
        editor.FlagsInput.Text = "0x8000000004006050";
        editor.FlagsInput.EmitSignal(LineEdit.SignalName.TextChanged, editor.FlagsInput.Text);
        editor.CommitPending();
        CheckBox wall = GodotUi.Walk(editor).OfType<CheckBox>().First(box => box.Text == "Wall");
        wall.ButtonPressed = false;
        MetadataCheck("named_flag_preserves_unknown_bits", editor.CurrentDocument.Current.Flags == 0x8000000004006040);
        editor.Undo();
        MetadataCheck("docked_document_undo", editor.CurrentDocument.Current.Flags == 0x8000000004006050);
        editor.Redo();
        MetadataCheck("docked_document_redo", editor.CurrentDocument.Current.Flags == 0x8000000004006040);
        editor.NameInput.Text = "workbench draft";
        editor.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, editor.NameInput.Text);
        art.Search("0x001A"); art.Search($"0x{id:X4}");
        MetadataCheck("valid_draft_survives_selection", editor.NameInput.Text == "workbench draft" && editor.DraftCount >= 2);
        MetadataCheck("docked_save", editor.Save() && !editor.CurrentDocument.IsDirty);
        editor.NameInput.Text = "recovered draft";
        editor.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, editor.NameInput.Text);
        editor.StashDrafts();
        var restored = new ItemMetadataEditor(); AddChild(restored); restored.Bind(_data, id);
        MetadataCheck("editor_local_draft_recovery", restored.NameInput.Text == "recovered draft" && !restored.SaveButton.Disabled);
        restored.Free();
        MetadataCheck("source_compare_is_overlay_free", !SamePixels(_data.SourceArtImage(EditorData.LandCount + (uint)id), _data.ArtImage(EditorData.LandCount + (uint)id)));
        MetadataCheck("comparison_restores_overlay", SamePixels(_data.ArtImage(EditorData.LandCount + (uint)id), _data.Assets.Load(AssetKind.Static, id)));
        editor.Undo();
    }

    private static bool SamePixels(Image a, Image b) => a != null && b != null
        && a.GetWidth() == b.GetWidth() && a.GetHeight() == b.GetHeight() && a.GetData().AsSpan().SequenceEqual(b.GetData());

    private int _assetsProofStep, _assetsProofFrames;
    private StaticStageDialog _stageProof;
    private BatchStaticDialog _batchProof;
    private SoundImportDialog _soundImportProof;
    private SoundExportDialog _soundExportProof;
    private ClilocCsvDialog _clilocCsvProof;
    private ClilocTileSyncDialog _tileSyncProof;
    private SoundDocument _auditionProofDocument;
    private ulong _auditionProofStarted;

    private void StepAssetWorkbenchProof()
    {
        if (!_data.IsLoaded)
        {
            if (_elapsed > TimeoutSeconds) { _failures.Add(_data.Error ?? "Assets proof: client data did not load"); Finish(); }
            return;
        }
        if (_assetsProofStep == 0)
        {
            CheckLoaded();
            _report["assets_workbench"] = _assetReport;
            _data.OpenAssets(Path.Combine(_out, "visual-project"));
            _assets.ShowPanel(_assets.Panel<ArtPanel>());
            _assets.Panel<ArtPanel>().SelectKind(false); _assets.Panel<ArtPanel>().Search("0x001A");
            GetWindow().Mode = Window.ModeEnum.Windowed;
            GetWindow().Size = AssetProofSize(1920, 1080);
            _assetsProofStep = 1; _assetsProofFrames = 0; return;
        }
        if (_assetsProofStep == 5 && _batchProof.IsBusy)
        {
            if (_elapsed > TimeoutSeconds) { _failures.Add("Batch folder scan did not finish"); Finish(); }
            return;
        }
        if (++_assetsProofFrames < 35) return;
        _assetsProofFrames = 0;
        if (_assetsProofStep <= 3)
        {
            int width = new[] { 1920, 1366, 1024 }[_assetsProofStep - 1];
            _assetReport[$"actual_window_{width}"] = GetWindow().Size.ToString();
            Control toolbar = _inspector.ActionToolbar;
            MetadataCheck($"toolbar_fits_{width}", toolbar.GetGlobalRect().End.X <= EditorInterface.Singleton.GetBaseControl().GetGlobalRect().End.X + 2
                && toolbar.GetChildren().OfType<Control>().All(c => c.GetGlobalRect().End.X <= toolbar.GetGlobalRect().End.X + 2));
            SaveAssetProof($"assets-workbench-{width}", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
            _assetsProofStep++;
            if (_assetsProofStep == 2) GetWindow().Size = AssetProofSize(1366, 900);
            else if (_assetsProofStep == 3) GetWindow().Size = AssetProofSize(1024, 768);
            else
            {
                GetWindow().Size = AssetProofSize(1366, 900);
                _data.Assets.Import(AssetKind.Static, 0x001A, FixtureStaticImage());
                _data.ReapplyAssets(AssetKind.Static, 0x001A);
                _stageProof = StaticStageDialog.Open(_data, 0x001A, _data.ArtImage(EditorData.LandCount + 0x001A));
            }
            return;
        }
        if (_assetsProofStep == 4)
        {
        SaveAssetProof("static-export-review", _stageProof.GetTexture()?.GetImage());
        _assetReport["review_size"] = _stageProof.Size.ToString();
        _assetReport["review_layout"] = GodotUi.Walk(_stageProof).OfType<Control>().Where(c => c.GetCombinedMinimumSize().Y > 500)
            .Select(c => new { Path = c.GetPath().ToString(), Size = c.Size.ToString(), Minimum = c.GetCombinedMinimumSize().ToString() }).ToArray();
        MetadataCheck("review_fits_screen", _stageProof.Size.Y <= DisplayServer.ScreenGetUsableRect().Size.Y);
        MetadataCheck("comparison_ready", _stageProof.Comparison.GetChildCount() == 2 && _stageProof.GetOkButton().Text == "Prepare verified copy");
        _stageProof.Free();
        _data.Assets.Revert(AssetKind.Static, 0x001A); _data.ReapplyAssets(AssetKind.Static, 0x001A);
        _batchProof = BatchStaticDialog.Open(_data); _batchProof.ShowScan(BatchPreviewFixture());
        GodotUi.Walk(_batchProof).OfType<Button>().First(button => button.Text == "Scan folder").EmitSignal(Button.SignalName.Pressed);
        _assetsProofStep = 5; return;
        }
        if (_assetsProofStep == 5)
        {
        SaveAssetProof("batch-static-review", _batchProof.GetTexture()?.GetImage());
        MetadataCheck("batch_review_fits_screen", _batchProof.Size.Y <= DisplayServer.ScreenGetUsableRect().Size.Y);
        MetadataCheck("batch_review_ready", _batchProof.ReadyToPrepare && _batchProof.Rows.GetRoot().GetChildCount() == 2);
        _batchProof.Free();
        var cliloc = _assets.Panel<ClilocPanel>(); _assets.ShowPanel(cliloc); cliloc.ChooseLanguage("enu"); cliloc.Search("500207");
        _inspector.Strings.TextInput.Text = "Project backpack label"; _inspector.Strings.TextInput.EmitSignal(TextEdit.SignalName.TextChanged);
        _assetsProofStep = 6; return;
        }
        if (_assetsProofStep == 6)
        {
            SaveAssetProof("cliloc-working-document", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
            MetadataCheck("cliloc_editor_ready", _inspector.Strings.Visible && !_inspector.Strings.SaveButton.Disabled);
            _inspector.Strings.Undo();
            var cliloc = _assets.Panel<ClilocPanel>(); cliloc.ChooseLanguage("jpn"); cliloc.Search("500207");
            _assetsProofStep = 7; return;
        }
        if (_assetsProofStep == 7)
        {
        SaveAssetProof("cliloc-japanese-source", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
        MetadataCheck("cliloc_japanese_ready", _inspector.Strings.Document.Language == "jpn" && _inspector.Strings.TextInput.Text.Length > 0);
        var sounds = _assets.Panel<SoundPanel>(); _assets.ShowPanel(sounds); sounds.SelectKind(false); sounds.Search(sounds.SmokeQuery);
        _assetsProofStep = 8; return;
        }
        if (_assetsProofStep == 8)
        {
            _inspector.Sound.NameInput.Text = "Project forest effect";
            _inspector.Sound.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, "Project forest effect");
            _assetsProofStep = 9; return;
        }
        if (_assetsProofStep == 9)
        {
        SaveAssetProof("sound-wave-export", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
        MetadataCheck("sound_wave_export_ready", _inspector.Current.Actions.Exists(action => action.Id == "export-wav")
            && _inspector.ActionToolbar.GetChildren().OfType<Control>().All(control => control.GetGlobalRect().End.X <= _inspector.ActionToolbar.GetGlobalRect().End.X + 2));
        MetadataCheck("sound_name_document_ready", _inspector.Sound.Visible && !_inspector.Sound.SaveButton.Disabled);
        _soundImportProof = SoundImportDialog.Open(_assets.Panel<SoundPanel>(), _inspector.Sound.Document,
            SoundWave.Encode(new byte[4410]), "synthetic-silence.wav");
        _assetsProofStep = 10; return;
        }
        if (_assetsProofStep == 10)
        {
            SaveAssetProof("sound-wav-import-review", _soundImportProof.GetTexture()?.GetImage());
            MetadataCheck("sound_import_review_fits", _soundImportProof.Size.Y <= DisplayServer.ScreenGetUsableRect().Size.Y
                && _soundImportProof.GetOkButton().Text == "Import into working copy");
            _soundImportProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            _assetsProofStep = 11; return;
        }
        if (_assetsProofStep == 11)
        {
        SaveAssetProof("sound-project-pcm", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
        MetadataCheck("sound_project_pcm_ready", _inspector.Sound.Document.HasPcmChange && _inspector.Sound.Document.Samples == 2205
            && _inspector.Current.Origin.Contains("Project PCM") && !_inspector.Sound.SaveButton.Disabled);
        var sounds = _assets.Panel<SoundPanel>();
        _soundExportProof = sounds.OpenExportDialog(sounds.ResultAssetIds().Take(2).ToArray(), true);
        _assetsProofStep = 12; return;
        }
        if (_assetsProofStep == 12)
        {
            SaveAssetProof("sound-batch-export-review", _soundExportProof.GetTexture()?.GetImage());
            MetadataCheck("sound_batch_review_ready", _soundExportProof.Plan.Rows.Count == 2 && _soundExportProof.Size.Y <= DisplayServer.ScreenGetUsableRect().Size.Y);
            _soundExportProof.Free(); var sounds = _assets.Panel<SoundPanel>();
            _soundExportProof = sounds.OpenExportDialog(sounds.ResultAssetIds().Take(2).ToArray());
            _soundExportProof.Choose(2, SoundExportFormat.FiddlerCsv, true, true);
            _assetsProofStep = 13; return;
        }
        if (_assetsProofStep == 13)
        {
        SaveAssetProof("sound-list-csv-review", _soundExportProof.GetTexture()?.GetImage());
        MetadataCheck("sound_csv_review_ready", _soundExportProof.Plan.Rows.Count == 2 && _soundExportProof.Rows.GetRoot().GetChildCount() == 2);
        _soundExportProof.Free(); _inspector.Sound.Undo(); _inspector.Sound.Undo();
        var cliloc = _assets.Panel<ClilocPanel>(); _assets.ShowPanel(cliloc); cliloc.ChooseLanguage("enu"); cliloc.Search("500207");
        string csv = Path.Combine(_out, "visual-cliloc.csv");
        File.WriteAllBytes(csv, ClilocCsv.Encode(new[] { new ClilocCsvRow(500207, "Project backpack ~1_NAME~", 2),
            new ClilocCsvRow(9999901, "New project label", 1) }, ClilocCsvFormat.Fiddler));
        _clilocCsvProof = cliloc.OpenImportReview(csv, ClilocCsvFormat.Fiddler);
        _assetsProofStep = 14; return;
        }
        if (_assetsProofStep == 14)
        {
            SaveAssetProof("cliloc-csv-import-review", _clilocCsvProof.GetTexture()?.GetImage());
            MetadataCheck("cliloc_csv_review_ready", _clilocCsvProof.Plan.Changes.Count == 2 && _clilocCsvProof.Size.Y <= DisplayServer.ScreenGetUsableRect().Size.Y);
            _clilocCsvProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            _assetsProofStep = 15; return;
        }
        if (_assetsProofStep == 15)
        {
            SaveAssetProof("cliloc-csv-working-document", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
            MetadataCheck("cliloc_csv_working_document_ready", _inspector.Strings.Document.Get(500207).Text == "Project backpack ~1_NAME~" && !_inspector.Strings.SaveButton.Disabled);
            _inspector.Strings.Undo();
            string source = Path.Combine(_out, "Cliloc.guild-quests");
            File.WriteAllBytes(source, ClilocFixture((700001, 0xE3, "Quest ~1_NAME~"), (700002, 7, "日本語")));
            _assets.Panel<ClilocPanel>().OpenCustomFile("custom1", source, remember: false);
            _inspector.Strings.TextInput.Text = "Working quest ~1_NAME~";
            _inspector.Strings.TextInput.EmitSignal(TextEdit.SignalName.TextChanged);
            _assetsProofStep = 16; return;
        }
        if (_assetsProofStep == 16)
        {
            SaveAssetProof("cliloc-custom-source", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
            MetadataCheck("cliloc_custom_source_ready", _inspector.Strings.Document.IsCustom && _inspector.Strings.Id == 700001
                && !_inspector.Strings.SaveButton.Disabled && _inspector.Current.Origin.Contains("Cliloc.guild-quests"));
            string csv = Path.Combine(_out, "visual-custom-cliloc.csv");
            File.WriteAllBytes(csv, ClilocCsv.Encode(new[] { new ClilocCsvRow(700001, "Incoming quest ~1_NAME~", 2) }, ClilocCsvFormat.Fiddler));
            _clilocCsvProof = _assets.Panel<ClilocPanel>().OpenImportReview(csv, ClilocCsvFormat.Fiddler);
            _assetsProofStep = 17; return;
        }
        if (_assetsProofStep == 17)
        {
            SaveAssetProof("cliloc-custom-csv-review", _clilocCsvProof.GetTexture()?.GetImage());
            MetadataCheck("cliloc_custom_csv_review_ready", _clilocCsvProof.Plan.Changes.Count == 1
                && _clilocCsvProof.Size.Y <= DisplayServer.ScreenGetUsableRect().Size.Y);
            _clilocCsvProof.Free();
            var custom = _inspector.Strings.Document; _inspector.Strings.Undo(); _inspector.Strings.Discard(custom);
            string tiledata = Path.Combine(_out, "visual-tiledata.mul"), cliloc = Path.Combine(_out, "Cliloc.sync-preview");
            File.WriteAllBytes(tiledata, TileSyncFixture(true, 32, new System.Collections.Generic.Dictionary<int, byte[]>
                { [1] = System.Text.Encoding.ASCII.GetBytes("stone wall"), [4] = System.Text.Encoding.ASCII.GetBytes("new item name") }));
            File.WriteAllBytes(cliloc, ClilocFixture((1020001, 0xE3, "Old wall ~1_NAME~"), (1020002, 7, "Unused item label")));
            var panel = _assets.Panel<ClilocPanel>(); var document = panel.OpenCustomFile("custom2", cliloc, remember: false);
            TileNameSnapshot Capture() => TileNameSnapshot.Capture(tiledata, true, Path.Combine(_out, "sync-project"), _data.ClientData, Array.Empty<ItemNameDraft>());
            _tileSyncProof = ClilocTileSyncDialog.Open(panel, document, new ClilocTileSyncPlan(document, Capture(), Capture));
            _tileSyncProof.ChooseFilter(2); _tileSyncProof.SelectMatching(); _tileSyncProof.ChooseFilter(1); _tileSyncProof.SelectMatching(); _tileSyncProof.ChooseFilter(0);
            _assetsProofStep = 18; return;
        }
        if (_assetsProofStep == 18)
        {
            SaveAssetProof("cliloc-tiledata-sync-review", _tileSyncProof.GetTexture()?.GetImage());
            MetadataCheck("cliloc_tiledata_sync_review_ready", _tileSyncProof.SelectionCount == 2 && _tileSyncProof.Rows.GetRoot().GetChildCount() == 3
                && _tileSyncProof.Size.Y <= DisplayServer.ScreenGetUsableRect().Size.Y);
            _tileSyncProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _assetsProofStep = 19; return;
        }
        if (_assetsProofStep == 19)
        {
        SaveAssetProof("cliloc-tiledata-sync-working", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
        MetadataCheck("cliloc_tiledata_sync_working_ready", _inspector.Strings.Document.Get(1020001).Text == "stone wall"
            && _inspector.Strings.Document.Get(1020002).Text == "Unused item label" && _inspector.Strings.Document.Get(1020004).Text == "new item name");
        var synchronized = _inspector.Strings.Document; _inspector.Strings.Undo();
        MetadataCheck("cliloc_tiledata_sync_single_undo", synchronized.Get(1020001).Text == "Old wall ~1_NAME~" && synchronized.Get(1020004) == null);
        _inspector.Strings.Discard(synchronized); _assets.Panel<ClilocPanel>().ChooseLanguage("enu");
        var sound = _assets.Panel<SoundPanel>(); _assets.ShowPanel(sound); sound.SelectKind(false); sound.ViewSlots(0); sound.Search(sound.SmokeQuery);
        _auditionProofDocument = _inspector.Sound.Document; _auditionProofDocument.ImportWave(SoundWave.Encode(new byte[4410])); sound.OnAssetsChanged();
        sound.LoopEffect(_auditionProofDocument.Id); _auditionProofStarted = Time.GetTicksMsec();
        GetWindow().Size = AssetProofSize(1366, 900); _assetsProofStep = 20; return;
        }
        var soundsProof = _assets.Panel<SoundPanel>();
        if (_assetsProofStep <= 22)
        {
            int width = new[] { 1366, 1024, 1920 }[_assetsProofStep - 20];
            SaveAssetProof($"sound-loop-audition-{width}", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
            MetadataCheck($"sound_loop_survives_multiple_cycles_{width}", soundsProof.Looping && soundsProof.ProjectPreviewSamples == 2205 && Time.GetTicksMsec() - _auditionProofStarted > 100);
            Control tools = soundsProof.GetChild<Control>(0);
            MetadataCheck($"sound_tools_fit_{width}", tools.GetGlobalRect().End.X <= soundsProof.GetGlobalRect().End.X + 2
                && tools.GetChildren().OfType<Control>().All(c => c.GetGlobalRect().End.X <= tools.GetGlobalRect().End.X + 2));
            _assetsProofStep++;
            if (_assetsProofStep == 21) GetWindow().Size = AssetProofSize(1024, 768);
            else if (_assetsProofStep == 22) GetWindow().Size = AssetProofSize(1920, 1080);
            else
            {
                soundsProof.Stop(); _auditionProofDocument.Revert(); soundsProof.OnAssetsChanged(); soundsProof.ViewSlots(2); soundsProof.Search("");
                soundsProof.Search(soundsProof.ResultAssetIds().First().ToString()); GetWindow().Size = AssetProofSize(1366, 900);
            }
            return;
        }
        SaveAssetProof("sound-empty-slot", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
        MetadataCheck("sound_empty_slot_ready", _inspector.Current.Title == "Empty sound slot" && !_inspector.Sound.Visible && _inspector.Current.Actions.Count == 0 && soundsProof.LoopButton.Disabled);
        soundsProof.ViewSlots(0); soundsProof.Search(_auditionProofDocument.Id.ToString());
        Finish();
    }

    private void SaveAssetProof(string subject, Image image)
    {
        if (Headless || image == null) { _failures.Add("Windowed assets proof requires a rendered viewport"); return; }
        string path = Path.Combine(_out, $"guo_FIDDLER-UX_{subject}_screenshot_{DateTime.UtcNow:yyyyMMdd-HHmm}.png");
        image.SavePng(path); image.Dispose();
        _assetReport[subject] = path;
    }

    private static Vector2I AssetProofSize(int width, int height) => (Vector2I)(new Vector2(width, height) * EditorInterface.Singleton.GetEditorScale());
}
#endif
