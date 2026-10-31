#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Godot;

public partial class EditorSmoke
{
    private void SoundDocumentCheck(string name, bool ok)
    { _assetReport["sound_document_" + name] = ok; if (!ok) AssetFail("Sound document: " + name); }
    private static bool SoundDocumentRefused(Action action)
    { try { action(); return false; } catch (Exception e) when (e is ArgumentException or IOException) { return true; } }
    private void CheckSoundDocuments()
    {
        string root = Path.Combine(_out, "sound-documents" + Suffix + "-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project"), install = Path.Combine(root, "install"); Directory.CreateDirectory(install);
        byte[] pcm = { 0, 128, 255, 127, 0, 0, 255, 255 }; string name = "original.wav\0trailing header bytes";
        SoundDocument Open(int id = 1) => SoundDocument.Open(project, install, id, () => (pcm, name));
        var document = Open();
        SoundDocumentCheck("baseline_and_pcm_immutable", document.SourceName == "original.wav" && document.Samples == 4 && !document.IsDirty);
        byte[] returned = document.Pcm(); returned[0] ^= 1;
        SoundDocumentCheck("pcm_copy_cannot_modify_source", document.Pcm().AsSpan().SequenceEqual(pcm));
        document.DraftName = "日本語"; document.CommitDraft();
        SoundDocumentCheck("rename_undo_redo", document.CurrentName == "日本語" && document.IsDirty && document.CanUndo);
        document.Undo(); bool undo = document.CurrentName == document.SourceName; document.Redo();
        SoundDocumentCheck("undo_redo_values", undo && document.CurrentName == "日本語");
        document.Revert(); SoundDocumentCheck("revert_is_undoable", document.CurrentName == document.SourceName && document.CanUndo); document.Undo();
        document.DraftName = new string('é', 16); bool boundary = document.CommitDraft(); document.DraftName = new string('é', 17);
        SoundDocumentCheck("32_byte_name_boundary", boundary && !document.CommitDraft()); document.Undo();
        document.DraftName = new string('é', 21);
        SoundDocumentCheck("utf8_limit_and_invalid_draft_retained", !document.CommitDraft() && document.Validation.Length > 0 && document.DraftName.Length == 21);
        document.DraftName = "bad\0name"; SoundDocumentCheck("nul_name_refused", !document.CommitDraft());
        document.DraftName = "named effect"; document.CommitDraft(); document.Save(); var reopened = Open();
        SoundDocumentCheck("save_reload_and_pcm_preserved", !reopened.IsDirty && reopened.CurrentName == "named effect" && reopened.Pcm().AsSpan().SequenceEqual(pcm));
        document.Undo(); SoundDocumentCheck("undo_after_save_dirty", document.IsDirty); document.Redo();
        string savedCopy = File.ReadAllText(reopened.DocumentPath);
        JsonObject bad = reopened.Snapshot(); bad["id"] = 2; File.WriteAllText(reopened.DocumentPath, bad.ToJsonString());
        SoundDocumentCheck("wrong_id_refused", SoundDocumentRefused(() => Open()));
        SoundDocumentCheck("external_saved_copy_refuses_save_and_native_export", SoundDocumentRefused(reopened.Save)
            && SoundDocumentRefused(() => reopened.RequireNativeReview(reopened.NativeReviewToken)));
        File.WriteAllText(reopened.DocumentPath, savedCopy); bad = reopened.Snapshot(); bad["source_sha256"] = "bad"; File.WriteAllText(reopened.DocumentPath, bad.ToJsonString());
        SoundDocumentCheck("wrong_hash_refused", SoundDocumentRefused(() => Open())); File.WriteAllText(reopened.DocumentPath, savedCopy);
        string nativeToken = reopened.NativeReviewToken;
        JsonObject nativeInput = reopened.NativeStageInput(nativeToken, "uop", Path.Combine(root, "captured.pcm"));
        SoundDocumentCheck("native_snapshot_binds_pcm_name_and_source", (string)nativeInput["pcm_sha256"] == reopened.PcmHash
            && (string)nativeInput["source_sha256"] == reopened.SourceHash && (string)nativeInput["name"] == reopened.CurrentName);
        reopened.DraftName = "native review change"; reopened.CommitDraft(); reopened.Undo();
        SoundDocumentCheck("native_undo_invalidates_previous_review", SoundDocumentRefused(() => reopened.RequireNativeReview(nativeToken)));
        CheckSoundDocumentEditor(Open(), Open(2));
        byte[] baseline = (byte[])pcm.Clone(); pcm[0] ^= 1;
        SoundDocumentCheck("source_pcm_change_refuses_save_reopen", SoundDocumentRefused(reopened.Save) && SoundDocumentRefused(() => Open()));
        pcm = baseline; name += "x";
        SoundDocumentCheck("source_name_change_refuses_save_reopen", SoundDocumentRefused(reopened.Save) && SoundDocumentRefused(() => Open())); name = "original.wav\0trailing header bytes";
        SoundDocumentCheck("install_destination_refused", SoundDocumentRefused(() => SoundDocument.Open(install, install, 1, () => (pcm, name))));
        var panel = _assets.Panel<SoundPanel>(); _assets.ShowPanel(panel); panel.SelectKind(false); int id = panel.Search(panel.SmokeQuery) ?? -1;
        string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        var sources = Directory.GetFiles(_data.ClientData).Where(path => Path.GetFileName(path).StartsWith("sound", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), Hash(path)));
        SoundDocument active = panel.WorkingDocument(id);
        SoundDocumentCheck("native_inspector_binding", active != null && _inspector.Sound.Visible && _inspector.Sound.Document == active && _inspector.Sound.Footer.Visible);
        var nativeStage = panel.OpenNativeStage();
        SoundDocumentCheck("native_stage_initial_action", !panel.NativeStageButton.Disabled && nativeStage != null && !nativeStage.Prepared && !nativeStage.Applied && !nativeStage.Busy);
        nativeStage.Free(); active.DraftName = new string('é', 17);
        SoundDocumentCheck("native_pending_invalid_name_refused", SoundDocumentRefused(() => panel.OpenNativeStage())); active.DraftName = active.CurrentName;
        string activeToken = active.NativeReviewToken; panel.SelectKind(true);
        SoundDocumentCheck("native_music_disabled_and_context_refused", panel.NativeStageButton.Disabled && SoundDocumentRefused(() => panel.RequireNativeContext(active)));
        panel.SelectKind(false); panel.Search($"0x{id:X4}");
        _inspector.Sound.NameInput.Text = "saved smoke sound"; _inspector.Sound.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, "saved smoke sound");
        SoundDocumentCheck("active_project_save", _inspector.Sound.Save()); panel.OnAssetsChanged();
        SoundDocumentCheck("working_name_search", panel.Search("saved smoke sound") == id);
        _inspector.Sound.NameInput.Text = "smoke name draft"; _inspector.Sound.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, "smoke name draft"); _inspector.Sound.CommitPending();
        panel.ReopenButton.EmitSignal(Button.SignalName.Pressed); var dialog = panel.GetChildren().OfType<ConfirmationDialog>().Last(); dialog.EmitSignal(ConfirmationDialog.SignalName.Canceled);
        SoundDocumentCheck("reopen_cancel_retains_draft", panel.WorkingDocument(id) == active && active.CurrentName == "smoke name draft");
        panel.ReopenButton.EmitSignal(Button.SignalName.Pressed); dialog = panel.GetChildren().OfType<ConfirmationDialog>().Last(); dialog.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        SoundDocumentCheck("reopen_confirm_discards_unsaved_name", panel.WorkingDocument(id) != active && !panel.WorkingDocument(id).IsDirty
            && panel.WorkingDocument(id).CurrentName == "saved smoke sound");
        active = panel.WorkingDocument(id); active.Revert(); active.Save(); panel.Search(id.ToString());
        SoundDocumentCheck("source_sound_files_unchanged", sources.All(row => row.Value == (new FileInfo(row.Key).Length, File.GetLastWriteTimeUtc(row.Key), Hash(row.Key))));
    }
    private void CheckSoundDocumentEditor(SoundDocument first, SoundDocument second)
    {
        var settings = EditorInterface.Singleton.GetEditorSettings(); Variant previous = settings.GetProjectMetadata("guo_sound", "drafts", "[]");
        settings.SetProjectMetadata("guo_sound", "drafts", "[]");
        var editor = new SoundEditor(); AddChild(editor); SoundEditor recovered = null;
        try
        {
            editor.Bind(first, null); editor.NameInput.Text = new string('é', 21); editor.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, editor.NameInput.Text);
            editor.Bind(second, null); editor.Bind(first, null);
            SoundDocumentCheck("invalid_selection_draft_retained", editor.NameInput.Text.Length == 21 && editor.SaveButton.Disabled && !editor.Save());
            editor.Undo(); editor.NameInput.Text = "recovered name"; editor.NameInput.EmitSignal(LineEdit.SignalName.TextChanged, "recovered name"); editor.CommitPending(); editor.StashDrafts();
            var fresh = SoundDocument.Open(first.Project, first.Install, first.Id, () => (first.Pcm(), "original.wav\0trailing header bytes"));
            recovered = new SoundEditor(); AddChild(recovered); recovered.Bind(fresh, null);
            SoundDocumentCheck("unsaved_reload_recovery", recovered.NameInput.Text == "recovered name" && fresh.IsDirty && !recovered.SaveButton.Disabled);
            SoundDocumentCheck("recovery_save_clears_draft", recovered.Save() && settings.GetProjectMetadata("guo_sound", "drafts", "[]").AsString() == "[]");
            // A malformed recovery must not partially change a document or replace the failed row.
            var snapshot = fresh.Recovery(); snapshot["saved_name"] = "stale saved copy";
            var rows = new JsonArray(snapshot); settings.SetProjectMetadata("guo_sound", "drafts", rows.ToJsonString());
            var refused = SoundDocument.Open(first.Project, first.Install, first.Id, () => (first.Pcm(), "original.wav\0trailing header bytes"));
            recovered.Discard(fresh); settings.SetProjectMetadata("guo_sound", "drafts", rows.ToJsonString()); recovered.Bind(refused, null); recovered.StashDrafts();
            SoundDocumentCheck("failed_recovery_readonly_preserved", refused.RecoveryError != null && !recovered.NameInput.Editable && recovered.SaveButton.Disabled
                && JsonNode.Parse(settings.GetProjectMetadata("guo_sound", "drafts", "[]").AsString()).AsArray().Count == 1 && refused.CurrentName == "recovered name");
            recovered.Discard(refused); SoundDocumentCheck("explicit_discard_clears_failed_recovery", settings.GetProjectMetadata("guo_sound", "drafts", "[]").AsString() == "[]");
            // Restore the fixture's saved name so following model checks use the expected baseline.
            first = SoundDocument.Open(first.Project, first.Install, first.Id, () => (first.Pcm(), "original.wav\0trailing header bytes"));
            first.DraftName = "named effect"; first.CommitDraft(); first.Save();
        }
        finally { recovered?.Free(); editor.Free(); settings.SetProjectMetadata("guo_sound", "drafts", previous); }
    }
}
#endif
