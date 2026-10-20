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
        SaveAssetProof("cliloc-japanese-source", EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage());
        MetadataCheck("cliloc_japanese_ready", _inspector.Strings.Document.Language == "jpn" && _inspector.Strings.TextInput.Text.Length > 0);
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
