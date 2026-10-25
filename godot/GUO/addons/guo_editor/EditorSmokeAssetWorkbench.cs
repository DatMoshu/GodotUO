#if TOOLS
namespace GUO.Editor;

using System;
using System.Linq;
using System.IO;
using Godot;

public partial class EditorSmoke
{
    private bool _hueRangeScrolled;
    private HueTextDialog _hueTextProof;
    private HueStageDialog _hueStageProof;
    private TextureImportDialog _textureImportProof;
    private TextureStageDialog _textureStageProof;
    private System.Threading.Tasks.Task _textureStageTask;
    private MetadataStageDialog _metadataStageProof;
    private System.Threading.Tasks.Task _metadataStageTask;
    private ClilocStageDialog _clilocStageProof;
    private System.Threading.Tasks.Task _clilocStageTask;
    private SoundStageDialog _soundStageProof;
    private System.Threading.Tasks.Task _soundStageTask;
    private System.Threading.Tasks.Task _hueStageTask;
    private ConfirmationDialog _assetCanceledDialog;
    private Action _assetAfterCancel;
    private string _assetCancelCheck;

    private void CancelAssetProof(ConfirmationDialog dialog, string check, Action resume)
    {
        // A real click queues the dialog for deletion. Opening its replacement
        // in this same frame leaves the old exclusive child registered in Godot.
        dialog.GetCancelButton().EmitSignal(Button.SignalName.Pressed);
        MetadataCheck(check + "_queues_close", dialog.IsQueuedForDeletion());
        _assetCanceledDialog = dialog; _assetCancelCheck = check; _assetAfterCancel = resume;
    }
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
        ItemMetadataNativeReview native = editor.CaptureNativeReview(_data, id);
        editor.NameInput.Text = new string('é', 11); editor.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, editor.NameInput.Text);
        bool pendingRefused = false;
        try { editor.RequireNativeReview(_data, native); } catch (IOException) { pendingRefused = true; }
        MetadataCheck("native_pending_draft_refused", pendingRefused);
        bool invalidRefused = false;
        try { editor.CaptureNativeReview(_data, id); } catch (IOException) { invalidRefused = true; }
        MetadataCheck("native_invalid_draft_refused", invalidRefused); editor.Undo();
        var nativeDialog = MetadataStageDialog.Open(editor, _data, id);
        MetadataCheck("native_dialog_initial_state", !nativeDialog.Prepared && !nativeDialog.Applied && !nativeDialog.Busy && nativeDialog.GetOkButton().Text == "Prepare verified copy");
        nativeDialog.Free();
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
        if (_assetAfterCancel != null)
        {
            bool closed = !IsInstanceValid(_assetCanceledDialog);
            MetadataCheck(_assetCancelCheck + "_dialog_closed", closed);
            Action resume = _assetAfterCancel; _assetAfterCancel = null; _assetCanceledDialog = null;
            if (!closed) { Finish(); return; }
            resume();
            return;
        }
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
        if (_assetsProofStep == 23)
        {
        SaveAssetProof("sound-empty-slot", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
        MetadataCheck("sound_empty_slot_ready", _inspector.Current.Title == "Empty sound slot" && !_inspector.Sound.Visible && _inspector.Current.Actions.Count == 0 && soundsProof.LoopButton.Disabled);
        soundsProof.ViewSlots(0); soundsProof.Search(_auditionProofDocument.Id.ToString());
        var hues = _assets.Panel<HuePanel>(); _assets.ShowPanel(hues); hues.Search("0x0021");
        _inspector.Hues.NameInput.Text = "Project moss"; _inspector.Hues.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, "Project moss");
        _inspector.Hues.ColorInput.Text = "0x83E0"; _inspector.Hues.ColorInput.EmitSignal(LineEdit.SignalName.TextChanged, "0x83E0");
        _inspector.Hues.CommitPending(); GetWindow().Size = AssetProofSize(1366, 900); _assetsProofStep = 24; return;
        }
        if (_assetsProofStep <= 26)
        {
        int hueWidth = new[] { 1366, 1024, 1920 }[_assetsProofStep - 24];
        SaveAssetProof($"hue-working-palette-{hueWidth}", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
        MetadataCheck($"hue_palette_fits_{hueWidth}", _inspector.Hues.Palette.GetChildren().OfType<Control>().All(c => c.GetGlobalRect().End.X <= _inspector.Hues.GetGlobalRect().End.X + 2)
            && _inspector.Hues.SaveButton.GetGlobalRect().End.Y <= EditorInterface.Singleton.GetBaseControl().GetGlobalRect().End.Y + 2
            && !_inspector.Hues.SaveButton.Disabled);
        _assetsProofStep++;
        if (_assetsProofStep == 25) { GetWindow().Size = AssetProofSize(1024, 768); return; }
        if (_assetsProofStep == 26) { GetWindow().Size = AssetProofSize(1920, 1080); return; }
        _inspector.Hues.Undo();
        _inspector.Hues.ChooseRange(0, 31, HueRangeOperation.ShiftRgb, 8, -8, 16); _inspector.Hues.ApplyRange();
        GetWindow().Size = AssetProofSize(1366, 900); return;
        }
        HueEditor hueEditor = _inspector.Hues;
        if (_assetsProofStep <= 29)
        {
        if (!_hueRangeScrolled)
        {
            hueEditor.GetParent().GetParent().GetParent<ScrollContainer>().EnsureControlVisible(hueEditor.Operations);
            _hueRangeScrolled = true; return;
        }
        int rangeWidth = new[] { 1366, 1024, 1920 }[_assetsProofStep - 27];
        SaveAssetProof($"hue-range-operations-{rangeWidth}", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
        MetadataCheck($"hue_range_tools_fit_{rangeWidth}", GodotUi.Walk(hueEditor.Operations).OfType<Control>().Where(c => c.IsVisibleInTree())
            .All(c => c.GetGlobalRect().End.X <= hueEditor.GetGlobalRect().End.X + 2)
            && hueEditor.RangeApply.GetGlobalRect().Position.Y >= hueEditor.GetParent().GetParent().GetParent<Control>().GetGlobalRect().Position.Y - 2
            && !hueEditor.RangeApply.Disabled && !hueEditor.SaveButton.Disabled);
        _assetsProofStep++; _hueRangeScrolled = false;
        if (_assetsProofStep == 28) { GetWindow().Size = AssetProofSize(1024, 768); return; }
        if (_assetsProofStep == 29) { GetWindow().Size = AssetProofSize(1920, 1080); return; }
        hueEditor.Undo();
        hueEditor.OperationsToggle.ButtonPressed = false; hueEditor.GetParent().GetParent().GetParent<ScrollContainer>().ScrollVertical = 0;
        GetWindow().Size = AssetProofSize(1366, 900);
        var entry = new HueTextEntry("Project moss exchange", 0, 31, Enumerable.Range(0, 32).Select(i => (ushort)(0x8000 | (i << 5))).ToArray());
        _hueTextProof = _assets.Panel<HuePanel>().OpenTextReview(hueEditor.Document, HueText.Encode(entry, HueTextFormat.Fiddler1252), "hue-palette-fiddler.txt");
        return;
        }
        if (_assetsProofStep == 30)
        {
            SaveAssetProof("hue-text-import-review", _hueTextProof.GetTexture()?.GetImage());
            MetadataCheck("hue_text_review_ready", _hueTextProof.Plan.CanApply && !_hueTextProof.GetOkButton().Disabled && _hueTextProof.Size.Y <= DisplayServer.ScreenGetUsableRect().Size.Y);
            _hueTextProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _assetsProofStep = 31; return;
        }
        if (_assetsProofStep == 31)
        {
        SaveAssetProof("hue-text-working-document", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
        MetadataCheck("hue_text_working_document_ready", hueEditor.Document.Name == "Project moss exchange" && hueEditor.Document.ColorAt(31) == 0x83E0 && !hueEditor.SaveButton.Disabled);
        hueEditor.Undo();
        hueEditor.Document.ReplacePalette(Enumerable.Range(0, 32).Select(i => (ushort)(0x8000 | (i << 5))).ToArray());
        hueEditor.Document.DraftName = "Native hue export fixture"; hueEditor.Document.CommitDraft();
        _hueStageProof = _assets.Panel<HuePanel>().OpenStageReview(hueEditor.Document);
        _hueStageProof.Destination.Text = Path.Combine(_out, "native-hue-stage");
        _hueStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _hueStageTask = _hueStageProof.Operation; _assetsProofStep = 32; return;
        }
        if (_hueStageTask?.IsCompleted == false) return;
        if (_assetsProofStep <= 34)
        {
            int width = new[] { 1366, 1024, 1920 }[_assetsProofStep - 32];
            MetadataCheck($"hue_native_prepared_{width}", _hueStageProof.Prepared && !_hueStageProof.Applied && !_hueStageProof.GetOkButton().Disabled
                && !Directory.Exists(_hueStageProof.Destination.Text) && _hueStageProof.Size.Y <= GetWindow().Size.Y);
            SaveAssetProof($"hue-native-prepared-{width}", _hueStageProof.GetTexture()?.GetImage());
            if (!_hueStageProof.Prepared) { _failures.Add("Native hue preparation failed: " + _hueStageProof.Status); _hueStageProof.Free(); Finish(); return; }
            _assetsProofStep++;
            if (_assetsProofStep == 33) { GetWindow().Size = AssetProofSize(1024, 768); return; }
            if (_assetsProofStep == 34) { GetWindow().Size = AssetProofSize(1920, 1080); return; }
            _hueStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            var applyReview = _hueStageProof.GetChildren().OfType<ConfirmationDialog>().Last();
            MetadataCheck("hue_native_apply_requires_confirmation", !_hueStageProof.Applied && !Directory.Exists(_hueStageProof.Destination.Text)
                && applyReview.GetOkButton().Text == "Apply to stage");
            applyReview.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _hueStageTask = _hueStageProof.Operation; return;
        }
        if (_assetsProofStep == 35)
        {
            MetadataCheck("hue_native_applied", _hueStageProof.Applied && File.Exists(Path.Combine(_hueStageProof.Destination.Text, "hues.mul")));
            SaveAssetProof("hue-native-applied", _hueStageProof.GetTexture()?.GetImage());
            _hueStageTask = _hueStageProof.RecoverAsync(); _assetsProofStep = 36; return;
        }
        if (_assetsProofStep == 36)
        {
        MetadataCheck("hue_native_recovered_prior_absence", !_hueStageProof.Applied && !Directory.Exists(_hueStageProof.Destination.Text));
        SaveAssetProof("hue-native-recovered", _hueStageProof.GetTexture()?.GetImage());
        _hueStageProof.Free();
        _hueStageProof = _assets.Panel<HuePanel>().OpenStageReview(hueEditor.Document);
        _hueStageProof.Destination.Text = Path.Combine(_out, "canceled-native-hue-stage");
        _hueStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _hueStageTask = _hueStageProof.Operation;
        _hueStageProof.Free(); _assetsProofStep = 37; return;
        }
        if (_assetsProofStep == 37)
        {
            MetadataCheck("hue_native_close_waits_for_owned_job", _hueStageTask.IsCompletedSuccessfully && !Directory.Exists(Path.Combine(_out, "canceled-native-hue-stage")));
            hueEditor.Document.Undo(); hueEditor.Document.Undo(); _assets.Panel<HuePanel>().OnAssetsChanged();
            var textures = _assets.Panel<TexturePanel>(); _assets.ShowPanel(textures); textures.FilterSize(0); textures.Search(textures.SmokeQuery);
            GetWindow().Size = AssetProofSize(1366, 900); _assetsProofStep = 38; return;
        }
        if (_assetsProofStep <= 40)
        {
        int textureWidth = new[] { 1366, 1024, 1920 }[_assetsProofStep - 38];
        MetadataCheck($"texture_browser_fits_{textureWidth}", _inspector.Current.Source == "Textures"
            && _inspector.Texture != null && _inspector.ActionToolbar.GetChildren().OfType<Control>()
                .All(c => c.GetGlobalRect().End.X <= _inspector.ActionToolbar.GetGlobalRect().End.X + 2));
        SaveAssetProof($"texture-browser-{textureWidth}", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
        _assetsProofStep++;
        if (_assetsProofStep == 39) { GetWindow().Size = AssetProofSize(1024, 768); return; }
        if (_assetsProofStep == 40) { GetWindow().Size = AssetProofSize(1920, 1080); return; }
        GetWindow().Size = AssetProofSize(1366, 900);
        var textures = _assets.Panel<TexturePanel>();
        using Image fixture = TextureFixture(128);
        _textureImportProof = textures.OpenImportReview(textures.WorkingDocument(3), fixture.SavePngToBuffer(), "texture-128-fixture.png");
        return;
        }
        TexturePanel texturePanel = _assets.Panel<TexturePanel>(); TextureDocument textureDocument = texturePanel.WorkingDocument(3);
        if (_assetsProofStep <= 43)
        {
            int width = new[] { 1366, 1024, 1920 }[_assetsProofStep - 41];
            MetadataCheck($"texture_review_ready_{width}", !textureDocument.IsDirty && _textureImportProof.Visible
                && _textureImportProof.Size.Y <= GetWindow().Size.Y && !_textureImportProof.GetOkButton().Disabled);
            SaveAssetProof($"texture-replacement-review-{width}", _textureImportProof.GetTexture()?.GetImage());
            _assetsProofStep++;
            if (_assetsProofStep == 42) { GetWindow().Size = AssetProofSize(1024, 768); return; }
            if (_assetsProofStep == 43) { GetWindow().Size = AssetProofSize(1920, 1080); return; }
            CancelAssetProof(_textureImportProof, "texture_review_cancel", () =>
            {
                MetadataCheck("texture_review_cancel_preserves_document", !textureDocument.IsDirty && textureDocument.Size == textureDocument.SourceSize);
                using Image fixture = TextureFixture(128);
                _textureImportProof = texturePanel.OpenImportReview(textureDocument, fixture.SavePngToBuffer(), "texture-128-fixture.png");
                _textureImportProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
                GetWindow().Size = AssetProofSize(1366, 900);
            });
            return;
        }
        if (_assetsProofStep <= 46)
        {
            int width = new[] { 1366, 1024, 1920 }[_assetsProofStep - 44];
            MetadataCheck($"texture_working_copy_fits_{width}", textureDocument.IsDirty && textureDocument.Size == 128
                && !_inspector.Textures.SaveButton.Disabled && _inspector.Textures.SaveButton.GetGlobalRect().End.Y <= EditorInterface.Singleton.GetBaseControl().GetGlobalRect().End.Y + 2);
            SaveAssetProof($"texture-working-copy-{width}", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
            _assetsProofStep++;
            if (_assetsProofStep == 45) { GetWindow().Size = AssetProofSize(1024, 768); return; }
            if (_assetsProofStep == 46) { GetWindow().Size = AssetProofSize(1920, 1080); return; }
            _inspector.Textures.Undo(); MetadataCheck("texture_native_undo_exact_source", textureDocument.Record().AsSpan().SequenceEqual(textureDocument.SourceRecord()));
            textureDocument.Redo(); texturePanel.OnAssetsChanged();
            MetadataCheck("texture_native_save_copy", _inspector.Textures.Save() && !textureDocument.IsDirty && File.Exists(textureDocument.DocumentPath));
            return;
        }
        if (_assetsProofStep == 47)
        {
            SaveAssetProof("texture-saved-copy", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
            GodotUi.Walk(texturePanel).OfType<CheckButton>().Single(c => c.AccessibilityName == "Show source texture").ButtonPressed = true;
            _assetsProofStep = 48; return;
        }
        if (_assetsProofStep == 48)
        {
            MetadataCheck("texture_source_comparison_keeps_document", _inspector.Current.Image.GetWidth() == textureDocument.SourceSize && textureDocument.Size == 128 && !textureDocument.IsDirty);
            SaveAssetProof("texture-source-comparison", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
            GodotUi.Walk(texturePanel).OfType<CheckButton>().Single(c => c.AccessibilityName == "Show source texture").ButtonPressed = false;
            GetWindow().Size = AssetProofSize(1366, 900);
            _textureStageProof = texturePanel.OpenNativeStage(textureDocument); _textureStageProof.Destination.Text = Path.Combine(_out, "native-texture-stage");
            _textureStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _textureStageTask = _textureStageProof.Operation;
            _assetsProofStep = 49; return;
        }
        if (_textureStageTask?.IsCompleted == false) return;
        if (_assetsProofStep <= 51)
        {
            int width = new[] { 1366, 1024, 1920 }[_assetsProofStep - 49];
            MetadataCheck($"texture_native_prepared_{width}", _textureStageProof.Prepared && !_textureStageProof.Applied && !_textureStageProof.GetOkButton().Disabled
                && !Directory.Exists(_textureStageProof.Destination.Text) && _textureStageProof.Size.Y <= GetWindow().Size.Y);
            SaveAssetProof($"texture-native-prepared-{width}", _textureStageProof.GetTexture()?.GetImage());
            if (!_textureStageProof.Prepared) { _failures.Add("Native texture preparation failed: " + _textureStageProof.Status); _textureStageProof.Free(); Finish(); return; }
            _assetsProofStep++;
            if (_assetsProofStep == 50) { GetWindow().Size = AssetProofSize(1024, 768); return; }
            if (_assetsProofStep == 51) { GetWindow().Size = AssetProofSize(1920, 1080); return; }
            _textureStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            var applyReview = _textureStageProof.GetChildren().OfType<ConfirmationDialog>().Last();
            MetadataCheck("texture_native_apply_requires_confirmation", !_textureStageProof.Applied && !Directory.Exists(_textureStageProof.Destination.Text));
            CancelAssetProof(applyReview, "texture_native_cancel", () =>
            {
                MetadataCheck("texture_native_cancel_apply_keeps_stage", !_textureStageProof.Applied && !Directory.Exists(_textureStageProof.Destination.Text));
                _textureStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            });
            return;
        }
        if (_assetsProofStep == 52)
        {
            var applyReview = _textureStageProof.GetChildren().OfType<ConfirmationDialog>().Last();
            SaveAssetProof("texture-native-apply-confirmation", applyReview.GetTexture()?.GetImage());
            applyReview.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _textureStageTask = _textureStageProof.Operation; _assetsProofStep = 53; return;
        }
        if (_assetsProofStep == 53)
        {
            MetadataCheck("texture_native_applied", _textureStageProof.Applied && File.Exists(Path.Combine(_textureStageProof.Destination.Text, "texmaps.mul")));
            SaveAssetProof("texture-native-applied", _textureStageProof.GetTexture()?.GetImage());
            _textureStageTask = _textureStageProof.RecoverAsync(); _assetsProofStep = 54; return;
        }
        if (_assetsProofStep == 54)
        {
            MetadataCheck("texture_native_recovered_prior_absence", !_textureStageProof.Applied && !Directory.Exists(_textureStageProof.Destination.Text));
            SaveAssetProof("texture-native-recovered", _textureStageProof.GetTexture()?.GetImage()); _textureStageProof.Free();
            _textureStageProof = texturePanel.OpenNativeStage(textureDocument); _textureStageProof.Destination.Text = Path.Combine(_out, "canceled-native-texture-stage");
            _textureStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _textureStageTask = _textureStageProof.Operation;
            _textureStageProof.Free(); _assetsProofStep = 55; return;
        }
        if (_assetsProofStep == 55)
        {
            MetadataCheck("texture_native_close_waits_for_owned_job", _textureStageTask.IsCompletedSuccessfully && !Directory.Exists(Path.Combine(_out, "canceled-native-texture-stage")));
            var art = _assets.Panel<ArtPanel>(); _assets.ShowPanel(art); art.SelectKind(false); art.Search("0x001A");
            var editor = _inspector.Metadata;
            editor.NameInput.Text = "Native stone wall"; editor.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, editor.NameInput.Text);
            editor.FlagsInput.Text = "0x8000000004006050"; editor.FlagsInput.EmitSignal(LineEdit.SignalName.TextChanged, editor.FlagsInput.Text);
            editor.HeightInput.Text = "22"; editor.HeightInput.EmitSignal(LineEdit.SignalName.TextChanged, editor.HeightInput.Text); editor.CommitPending();
            GetWindow().Size = AssetProofSize(1366, 900);
            _metadataStageProof = MetadataStageDialog.Open(editor, _data, 26); _metadataStageProof.Destination.Text = Path.Combine(_out, "native-metadata-stage");
            _metadataStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _metadataStageTask = _metadataStageProof.Operation; _assetsProofStep = 56; return;
        }
        if (_metadataStageTask?.IsCompleted == false) return;
        if (_assetsProofStep <= 58)
        {
            int width = new[] { 1366, 1024, 1920 }[_assetsProofStep - 56];
            MetadataCheck($"metadata_native_prepared_{width}", _metadataStageProof.Prepared && !_metadataStageProof.Applied && !_metadataStageProof.GetOkButton().Disabled
                && !Directory.Exists(_metadataStageProof.Destination.Text) && _metadataStageProof.Size.Y <= GetWindow().Size.Y);
            SaveAssetProof($"metadata-native-prepared-{width}", _metadataStageProof.GetTexture()?.GetImage());
            if (!_metadataStageProof.Prepared) { _failures.Add("Native metadata preparation failed: " + _metadataStageProof.Status); _metadataStageProof.Free(); Finish(); return; }
            _assetsProofStep++;
            if (_assetsProofStep == 57) { GetWindow().Size = AssetProofSize(1024, 768); return; }
            if (_assetsProofStep == 58) { GetWindow().Size = AssetProofSize(1920, 1080); return; }
            _metadataStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            var confirm = _metadataStageProof.GetChildren().OfType<ConfirmationDialog>().Last();
            MetadataCheck("metadata_native_apply_requires_confirmation", !_metadataStageProof.Applied && !Directory.Exists(_metadataStageProof.Destination.Text));
            CancelAssetProof(confirm, "metadata_native_cancel", () =>
            {
                MetadataCheck("metadata_native_cancel_apply_keeps_stage", !_metadataStageProof.Applied && !Directory.Exists(_metadataStageProof.Destination.Text));
                _metadataStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            });
            return;
        }
        if (_assetsProofStep == 59)
        {
            var confirm = _metadataStageProof.GetChildren().OfType<ConfirmationDialog>().Last();
            SaveAssetProof("metadata-native-apply-confirmation", confirm.GetTexture()?.GetImage());
            confirm.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _metadataStageTask = _metadataStageProof.Operation; _assetsProofStep = 60; return;
        }
        if (_assetsProofStep == 60)
        {
            MetadataCheck("metadata_native_applied", _metadataStageProof.Applied && File.Exists(Path.Combine(_metadataStageProof.Destination.Text, "tiledata.mul")));
            SaveAssetProof("metadata-native-applied", _metadataStageProof.GetTexture()?.GetImage());
            _metadataStageTask = _metadataStageProof.RecoverAsync(); _assetsProofStep = 61; return;
        }
        if (_assetsProofStep == 61)
        {
            MetadataCheck("metadata_native_recovered_prior_absence", !_metadataStageProof.Applied && !Directory.Exists(_metadataStageProof.Destination.Text));
            SaveAssetProof("metadata-native-recovered", _metadataStageProof.GetTexture()?.GetImage()); _metadataStageProof.Free();
            _metadataStageProof = MetadataStageDialog.Open(_inspector.Metadata, _data, 26); _metadataStageProof.Destination.Text = Path.Combine(_out, "canceled-native-metadata-stage");
            _metadataStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _metadataStageTask = _metadataStageProof.Operation;
            _metadataStageProof.Free(); _assetsProofStep = 62; return;
        }
        if (_assetsProofStep == 62)
        {
            MetadataCheck("metadata_native_close_waits_for_owned_job", _metadataStageTask.IsCompletedSuccessfully && !Directory.Exists(Path.Combine(_out, "canceled-native-metadata-stage")));
            var cliloc = _assets.Panel<ClilocPanel>(); _assets.ShowPanel(cliloc); cliloc.SelectKind(false); cliloc.ChooseLanguage("enu"); cliloc.Search("500207");
            _inspector.Strings.TextInput.Text = "Native Cliloc reviewed text"; _inspector.Strings.TextInput.EmitSignal(TextEdit.SignalName.TextChanged); _inspector.Strings.CommitPending();
            GetWindow().Size = AssetProofSize(1366, 900); _clilocStageProof = cliloc.OpenNativeStage();
            _clilocStageProof.Destination.Text = Path.Combine(_out, "native-cliloc-stage");
            _clilocStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _clilocStageTask = _clilocStageProof.Operation; _assetsProofStep = 63; return;
        }
        if (_clilocStageTask?.IsCompleted == false) return;
        if (_assetsProofStep <= 65)
        {
            int width = new[] { 1366, 1024, 1920 }[_assetsProofStep - 63];
            MetadataCheck($"cliloc_native_prepared_{width}", _clilocStageProof.Prepared && !_clilocStageProof.Applied && !_clilocStageProof.GetOkButton().Disabled
                && !Directory.Exists(_clilocStageProof.Destination.Text) && _clilocStageProof.Size.Y <= GetWindow().Size.Y);
            SaveAssetProof($"cliloc-native-prepared-{width}", _clilocStageProof.GetTexture()?.GetImage());
            if (!_clilocStageProof.Prepared) { _failures.Add("Native Cliloc preparation failed: " + _clilocStageProof.Status); _clilocStageProof.Free(); Finish(); return; }
            _assetsProofStep++;
            if (_assetsProofStep == 64) { GetWindow().Size = AssetProofSize(1024, 768); return; }
            if (_assetsProofStep == 65) { GetWindow().Size = AssetProofSize(1920, 1080); return; }
            GetWindow().Size = AssetProofSize(1024, 768);
            _clilocStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            var confirm = _clilocStageProof.GetChildren().OfType<ConfirmationDialog>().Last();
            MetadataCheck("cliloc_native_confirmation_fits_1024", confirm.Size.X <= GetWindow().Size.X && confirm.Size.Y <= GetWindow().Size.Y);
            MetadataCheck("cliloc_native_apply_requires_confirmation", !_clilocStageProof.Applied && !Directory.Exists(_clilocStageProof.Destination.Text));
            CancelAssetProof(confirm, "cliloc_native_cancel", () =>
            {
                MetadataCheck("cliloc_native_cancel_apply_keeps_stage", !_clilocStageProof.Applied && !Directory.Exists(_clilocStageProof.Destination.Text));
                _clilocStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            });
            return;
        }
        if (_assetsProofStep == 66)
        {
            var confirm = _clilocStageProof.GetChildren().OfType<ConfirmationDialog>().Last();
            SaveAssetProof("cliloc-native-apply-confirmation", confirm.GetTexture()?.GetImage()); confirm.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            _clilocStageTask = _clilocStageProof.Operation; _assetsProofStep = 67; return;
        }
        if (_assetsProofStep == 67)
        {
            MetadataCheck("cliloc_native_applied", _clilocStageProof.Applied && File.Exists(Path.Combine(_clilocStageProof.Destination.Text, "Cliloc.enu")));
            SaveAssetProof("cliloc-native-applied", _clilocStageProof.GetTexture()?.GetImage()); _clilocStageTask = _clilocStageProof.RecoverAsync(); _assetsProofStep = 68; return;
        }
        if (_assetsProofStep == 68)
        {
            MetadataCheck("cliloc_native_recovered_prior_absence", !_clilocStageProof.Applied && !Directory.Exists(_clilocStageProof.Destination.Text));
            SaveAssetProof("cliloc-native-recovered", _clilocStageProof.GetTexture()?.GetImage()); _clilocStageProof.Free();
            _clilocStageProof = _assets.Panel<ClilocPanel>().OpenNativeStage(); _clilocStageProof.Destination.Text = Path.Combine(_out, "canceled-native-cliloc-stage");
            _clilocStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _clilocStageTask = _clilocStageProof.Operation;
            _clilocStageProof.Free(); _assetsProofStep = 69; return;
        }
        if (_assetsProofStep == 69)
        {
            MetadataCheck("cliloc_native_close_waits_for_owned_job", _clilocStageTask.IsCompletedSuccessfully && !Directory.Exists(Path.Combine(_out, "canceled-native-cliloc-stage")));
            var sound = _assets.Panel<SoundPanel>(); _assets.ShowPanel(sound); sound.SelectKind(false); sound.Search("0x0000");
            var document = sound.WorkingDocument(0); document.DraftName = "Native sound review"; document.CommitDraft(); document.ImportWave(SoundWave.Encode(new byte[22050]));
            GetWindow().Size = AssetProofSize(1366, 900); _soundStageProof = sound.OpenNativeStage();
            _soundStageProof.Destination.Text = Path.Combine(_out, "native-sound-stage");
            _soundStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _soundStageTask = _soundStageProof.Operation; _assetsProofStep = 70; return;
        }
        if (_soundStageTask?.IsCompleted == false) return;
        if (_assetsProofStep <= 72)
        {
            int width = new[] { 1366, 1024, 1920 }[_assetsProofStep - 70];
            MetadataCheck($"sound_native_prepared_{width}", _soundStageProof.Prepared && !_soundStageProof.Applied && !_soundStageProof.GetOkButton().Disabled
                && !Directory.Exists(_soundStageProof.Destination.Text) && _soundStageProof.Size.Y <= GetWindow().Size.Y);
            SaveAssetProof($"sound-native-prepared-{width}", _soundStageProof.GetTexture()?.GetImage());
            if (!_soundStageProof.Prepared) { _failures.Add("Native sound preparation failed: " + _soundStageProof.Status); _soundStageProof.Free(); Finish(); return; }
            _assetsProofStep++;
            if (_assetsProofStep == 71) { GetWindow().Size = AssetProofSize(1024, 768); return; }
            if (_assetsProofStep == 72) { GetWindow().Size = AssetProofSize(1920, 1080); return; }
            var sound = _assets.Panel<SoundPanel>();
            GodotUi.Walk(_soundStageProof).OfType<Button>().Single(b => b.TooltipText == "Audition current staged PCM").EmitSignal(Button.SignalName.Pressed);
            MetadataCheck("sound_native_current_stage_audition", sound.ProjectPreviewPlaying && sound.ProjectPreviewSamples == sound.WorkingDocument(0).SourceSamples); sound.Stop();
            GodotUi.Walk(_soundStageProof).OfType<Button>().Single(b => b.TooltipText == "Audition reviewed export PCM").EmitSignal(Button.SignalName.Pressed);
            MetadataCheck("sound_native_export_pcm_audition", sound.ProjectPreviewPlaying && sound.ProjectPreviewSamples == 11025); sound.Stop();
            GetWindow().Size = AssetProofSize(1024, 768); _soundStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            var confirm = _soundStageProof.GetChildren().OfType<ConfirmationDialog>().Last();
            MetadataCheck("sound_native_confirmation_fits_1024", confirm.Size.X <= GetWindow().Size.X && confirm.Size.Y <= GetWindow().Size.Y);
            MetadataCheck("sound_native_apply_requires_confirmation", !_soundStageProof.Applied && !Directory.Exists(_soundStageProof.Destination.Text));
            CancelAssetProof(confirm, "sound_native_cancel", () =>
            {
                MetadataCheck("sound_native_cancel_apply_keeps_stage", !_soundStageProof.Applied && !Directory.Exists(_soundStageProof.Destination.Text));
                _soundStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            });
            return;
        }
        if (_assetsProofStep == 73)
        {
            var confirm = _soundStageProof.GetChildren().OfType<ConfirmationDialog>().Last();
            SaveAssetProof("sound-native-apply-confirmation", confirm.GetTexture()?.GetImage()); confirm.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            _soundStageTask = _soundStageProof.Operation; _assetsProofStep = 74; return;
        }
        if (_assetsProofStep == 74)
        {
            MetadataCheck("sound_native_applied", _soundStageProof.Applied && (File.Exists(Path.Combine(_soundStageProof.Destination.Text, "soundLegacyMUL.uop")) || File.Exists(Path.Combine(_soundStageProof.Destination.Text, "sound.mul"))));
            SaveAssetProof("sound-native-applied", _soundStageProof.GetTexture()?.GetImage()); _soundStageTask = _soundStageProof.RecoverAsync(); _assetsProofStep = 75; return;
        }
        if (_assetsProofStep == 75)
        {
            MetadataCheck("sound_native_recovered_prior_absence", !_soundStageProof.Applied && !Directory.Exists(_soundStageProof.Destination.Text));
            SaveAssetProof("sound-native-recovered", _soundStageProof.GetTexture()?.GetImage()); _soundStageProof.Free();
            _soundStageProof = _assets.Panel<SoundPanel>().OpenNativeStage(); _soundStageProof.Destination.Text = Path.Combine(_out, "canceled-native-sound-stage");
            _soundStageProof.EmitSignal(ConfirmationDialog.SignalName.Confirmed); _soundStageTask = _soundStageProof.Operation;
            _soundStageProof.Free(); _assetsProofStep = 76; return;
        }
        MetadataCheck("sound_native_close_waits_for_owned_job", _soundStageTask.IsCompletedSuccessfully && !Directory.Exists(Path.Combine(_out, "canceled-native-sound-stage")));
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
