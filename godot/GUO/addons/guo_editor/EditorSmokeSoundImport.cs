#if TOOLS
namespace GUO.Editor;

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

public partial class EditorSmoke
{
    private void SoundImportCheck(string name, bool ok)
    { _assetReport["sound_import_" + name] = ok; if (!ok) AssetFail("Sound import: " + name); }

    private byte[] IndependentInputWave(string root, byte[] pcm)
    {
        Directory.CreateDirectory(root); string raw = Path.Combine(root, "input.pcm"), wave = Path.Combine(root, "input.wav"); File.WriteAllBytes(raw, pcm);
        var start = new ProcessStartInfo(EditorData.Setting("PYTHON", "python"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-c", "import pathlib,sys,wave; f=wave.open(sys.argv[2],'wb'); f.setparams((1,2,22050,0,'NONE','not compressed')); f.writeframes(pathlib.Path(sys.argv[1]).read_bytes()); f.close()", raw, wave }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start);
        if (!child.WaitForExit(10_000)) { child.Kill(entireProcessTree: true); child.WaitForExit(); throw new IOException("Independent WAV fixture timed out."); }
        if (child.ExitCode != 0) throw new IOException("Independent WAV fixture failed: " + child.StandardError.ReadToEnd());
        return File.ReadAllBytes(wave);
    }

    private static byte[] WaveWithChunk(byte[] wave, int position, string tag, byte[] payload)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write(wave.AsSpan(0, position)); writer.Write(Encoding.ASCII.GetBytes(tag)); writer.Write(payload.Length); writer.Write(payload);
        if (payload.Length % 2 != 0) writer.Write((byte)0);
        writer.Write(wave.AsSpan(position)); byte[] result = output.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8); return result;
    }

    private void CheckSoundImport()
    {
        string root = Path.Combine(_out, "sound-import" + Suffix + "-" + Guid.NewGuid().ToString("N"));
        byte[] source = { 0, 128, 255, 127, 0, 0, 255, 255 };
        byte[] incoming = Enumerable.Range(0, 4410).Select(i => (byte)(i * 17)).ToArray();
        byte[] wave = IndependentInputWave(root, incoming);
        SoundImportCheck("independent_signed_pcm_exact", SoundWave.Decode(wave).AsSpan().SequenceEqual(incoming));
        byte[] padded = WaveWithChunk(wave, 12, "JUNK", new byte[] { 1, 2, 3 });
        SoundImportCheck("odd_unknown_chunk_padding", SoundWave.Decode(padded).AsSpan().SequenceEqual(incoming));
        byte[] Change(int offset, byte value) { byte[] copy = (byte[])wave.Clone(); copy[offset] = value; return copy; }
        bool Refused(byte[] bytes) => SoundDocumentRefused(() => SoundWave.Decode(bytes));
        SoundImportCheck("container_and_format_refusals", Refused(null) && Refused(new byte[43]) && Refused(Change(0, 0))
            && Refused(Change(8, 0)) && Refused(Change(4, 0)) && Refused(Change(20, 3)) && Refused(Change(22, 2))
            && Refused(Change(24, 0)) && Refused(Change(28, 0)) && Refused(Change(32, 4)) && Refused(Change(34, 8)));
        byte[] truncated = wave[..^1]; BinaryPrimitives.WriteInt32LittleEndian(truncated.AsSpan(4), truncated.Length - 8);
        byte[] huge = Change(40, 255); BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(40), uint.MaxValue);
        byte[] odd = (byte[])wave.Clone(); BinaryPrimitives.WriteInt32LittleEndian(odd.AsSpan(40), incoming.Length - 1);
        SoundImportCheck("truncated_odd_and_oversized_chunks", Refused(truncated) && Refused(huge) && Refused(odd));
        SoundImportCheck("duplicate_and_order_refusals", Refused(WaveWithChunk(wave, 12, "data", source))
            && Refused(WaveWithChunk(wave, 36, "fmt ", wave.AsSpan(20, 16).ToArray()))
            && Refused(WaveWithChunk(wave, wave.Length, "data", source)));
        byte[] empty = wave[..44]; BinaryPrimitives.WriteInt32LittleEndian(empty.AsSpan(4), 36); BinaryPrimitives.WriteInt32LittleEndian(empty.AsSpan(40), 0);
        SoundImportCheck("empty_data_refused", Refused(empty));
        string sparse = Path.Combine(root, "oversize.wav"); using (var file = File.Create(sparse)) file.SetLength(SoundWave.MaximumWaveBytes + 1L);
        SoundImportCheck("bounded_file_read", SoundDocumentRefused(() => SoundWave.ReadFile(sparse)) && SoundWave.ReadFile(Path.Combine(root, "input.wav")).AsSpan().SequenceEqual(wave));
        File.Delete(sparse);

        string project = Path.Combine(root, "project"), install = Path.Combine(root, "install"); Directory.CreateDirectory(install);
        SoundDocument Open(int id = 1) => SoundDocument.Open(project, install, id, () => (source, "original"));
        var document = Open(); document.DraftName = "retained name";
        SoundImportCheck("invalid_input_no_partial_mutation", SoundDocumentRefused(() => document.ImportWave(odd))
            && document.DraftName == "retained name" && document.CurrentName == "original" && !document.CanUndo && !document.HasPcmChange);
        document.ImportWave(padded);
        SoundImportCheck("import_retains_name_and_pcm", document.CurrentName == "retained name" && document.HasPcmChange && document.Samples == incoming.Length / 2
            && document.SourceSamples == source.Length / 2 && document.Pcm().AsSpan().SequenceEqual(incoming));
        document.Undo(); bool undone = !document.HasPcmChange && document.CurrentName == "retained name"; document.Redo();
        SoundImportCheck("import_undo_redo", undone && document.HasPcmChange && document.Pcm().AsSpan().SequenceEqual(incoming));
        byte[] returned = document.Pcm(); returned[0] ^= 1;
        SoundImportCheck("imported_pcm_immutable", document.Pcm().AsSpan().SequenceEqual(incoming));
        document.Revert(); bool reverted = document.CurrentName == "original" && !document.HasPcmChange; document.Undo();
        SoundImportCheck("revert_pcm_is_undoable", reverted && document.HasPcmChange && document.CurrentName == "retained name");
        document.DraftName = new string('x', 33); string priorHash = document.PcmHash;
        SoundImportCheck("invalid_name_blocks_import", SoundDocumentRefused(() => document.ImportWave(wave)) && document.PcmHash == priorHash);
        document.DraftName = document.CurrentName; document.Save(); var reopened = Open();
        string blob = reopened.BlobPath(reopened.PcmHash); string manifest = File.ReadAllText(reopened.DocumentPath);
        SoundImportCheck("schema2_blob_save_reopen", !reopened.IsDirty && reopened.HasPcmChange && reopened.Pcm().AsSpan().SequenceEqual(incoming)
            && (int)reopened.Snapshot()["schema"] == 2 && File.Exists(blob) && new FileInfo(reopened.DocumentPath).Length < 1024);
        string exported = Path.Combine(root, "working-export.wav"); reopened.ExportWave(exported);
        SoundImportCheck("working_export_independent_exact", IndependentWave(exported, Path.Combine(root, "input.pcm")));
        File.Move(blob, blob + ".held");
        SoundImportCheck("missing_blob_refused", SoundDocumentRefused(() => Open())); File.Move(blob + ".held", blob);
        byte[] blobBytes = File.ReadAllBytes(blob), corrupt = (byte[])blobBytes.Clone(); corrupt[^1] ^= 1; File.WriteAllBytes(blob, corrupt);
        SoundImportCheck("corrupt_blob_refuses_open_and_save", SoundDocumentRefused(() => Open()) && SoundDocumentRefused(reopened.Save)); File.WriteAllBytes(blob, blobBytes);
        JsonObject bad = reopened.Snapshot(); bad["pcm_sha256"] = "../../escape"; File.WriteAllText(reopened.DocumentPath, bad.ToJsonString());
        SoundImportCheck("unsafe_pcm_reference_refused", SoundDocumentRefused(() => Open()));
        bad = reopened.Snapshot(); bad.Remove("pcm_sha256"); File.WriteAllText(reopened.DocumentPath, bad.ToJsonString());
        SoundImportCheck("missing_pcm_reference_refused", SoundDocumentRefused(() => Open())); File.WriteAllText(reopened.DocumentPath, manifest);

        var recovered = Open(2); recovered.ImportWave(wave); JsonObject recovery = recovered.Recovery(); var fresh = Open(2); fresh.Restore(recovery);
        SoundImportCheck("pcm_draft_recovery", fresh.HasPcmChange && fresh.IsDirty && fresh.Pcm().AsSpan().SequenceEqual(incoming) && recovery.ToJsonString().Length < 1024);
        recovery["saved_pcm_sha256"] = "wrong"; var refused = Open(2);
        SoundImportCheck("stale_saved_pcm_recovery_refused", SoundDocumentRefused(() => refused.Restore(recovery)) && !refused.HasPcmChange && !refused.CanUndo);
        recovery = recovered.Recovery(); File.Move(blob, blob + ".held");
        SoundImportCheck("missing_recovery_pcm_no_mutation", SoundDocumentRefused(() => refused.Restore(recovery)) && !refused.HasPcmChange); File.Move(blob + ".held", blob);
        JsonObject legacy = Open(3).Snapshot(); legacy["schema"] = 1; legacy["kind"] = "guo/sound-name"; legacy["name"] = "legacy name"; legacy.Remove("pcm_sha256");
        string legacyPath = Open(3).DocumentPath; File.WriteAllText(legacyPath, legacy.ToJsonString()); var migrated = Open(3); migrated.Save();
        SoundImportCheck("legacy_name_document_migration", migrated.CurrentName == "legacy name" && !migrated.HasPcmChange && (int)JsonNode.Parse(File.ReadAllText(legacyPath))["schema"] == 2);
        var oldDraft = new JsonObject { ["key"] = Open(4).RecoveryKey, ["saved_name"] = "original", ["raw_name"] = "legacy draft", ["snapshot"] = legacy.DeepClone() };
        oldDraft["snapshot"]["id"] = 4; oldDraft["snapshot"]["name"] = "legacy draft"; var legacyRecovery = Open(4); legacyRecovery.Restore(oldDraft);
        SoundImportCheck("legacy_name_recovery_migration", legacyRecovery.CurrentName == "legacy draft" && !legacyRecovery.HasPcmChange);
        CheckSoundPcmRecoveryEditor(Open(2), recovered.Recovery(), blob);
        source[0] ^= 1; byte[] priorExport = File.ReadAllBytes(exported);
        SoundImportCheck("changed_source_blocks_import_and_export", SoundDocumentRefused(() => reopened.ImportWave(wave)) && SoundDocumentRefused(() => reopened.ExportWave(exported))
            && File.ReadAllBytes(exported).AsSpan().SequenceEqual(priorExport)); source[0] ^= 1;
        CheckSoundImportPanel(root, wave, incoming);
    }

    private void CheckSoundPcmRecoveryEditor(SoundDocument document, JsonObject recovery, string blob)
    {
        var settings = EditorInterface.Singleton.GetEditorSettings(); Variant previous = settings.GetProjectMetadata("guo_sound", "drafts", "[]");
        byte[] originalBlob = File.ReadAllBytes(blob); var editor = new SoundEditor(); AddChild(editor); SoundEditor failed = null;
        try
        {
            string rows = new JsonArray(recovery).ToJsonString(); settings.SetProjectMetadata("guo_sound", "drafts", rows); editor.Bind(document, null);
            SoundImportCheck("pcm_editor_recovery_binding", document.HasPcmChange && !editor.SaveButton.Disabled);
            File.WriteAllBytes(blob, new byte[44]); editor.StashDrafts();
            SoundImportCheck("failed_blob_stash_retains_prior_row", settings.GetProjectMetadata("guo_sound", "drafts", "[]").AsString() == rows);
            var fresh = SoundDocument.Open(document.Project, document.Install, document.Id, () => (document.SourcePcm(), "original"));
            failed = new SoundEditor(); AddChild(failed); failed.Bind(fresh, null); string beforeName = fresh.CurrentName; string beforeHash = fresh.PcmHash;
            fresh.DraftName = "retained readonly draft"; fresh.Undo(); fresh.Redo(); fresh.Revert(); failed.StashDrafts();
            SoundImportCheck("failed_pcm_recovery_readonly", fresh.RecoveryError != null && !failed.NameInput.Editable && failed.SaveButton.Disabled
                && fresh.CurrentName == beforeName && fresh.PcmHash == beforeHash && fresh.DraftName == "retained readonly draft"
                && settings.GetProjectMetadata("guo_sound", "drafts", "[]").AsString() == rows);
            failed.Discard(fresh); SoundImportCheck("failed_pcm_recovery_explicit_discard", settings.GetProjectMetadata("guo_sound", "drafts", "[]").AsString() == "[]");
        }
        finally { File.WriteAllBytes(blob, originalBlob); failed?.Free(); editor.Free(); settings.SetProjectMetadata("guo_sound", "drafts", previous); }
    }

    private void CheckSoundImportPanel(string root, byte[] wave, byte[] incoming)
    {
        var panel = _assets.Panel<SoundPanel>(); _assets.ShowPanel(panel); panel.SelectKind(false); int id = panel.Search(panel.SmokeQuery) ?? -1;
        var document = panel.WorkingDocument(id); byte[] original = document.Pcm(); string originalName = document.CurrentName;
        string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
        var stamps = Directory.GetFiles(_data.ClientData).Where(path => Path.GetFileName(path).StartsWith("sound", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), Hash(path)));
        SoundImportCheck("typed_import_action", _inspector.Current.Actions.Exists(a => a.Id == "import-wav"));
        var picker = panel.OpenWavImport(id);
        SoundImportCheck("native_import_picker", picker.GetParent() == panel && picker.FileMode == EditorFileDialog.FileModeEnum.OpenFile && picker.Filters.Single().Contains("*.wav"));
        picker.EmitSignal(EditorFileDialog.SignalName.Canceled);
        SoundImportCheck("picker_cancel_no_mutation", document.Pcm().AsSpan().SequenceEqual(original));
        string path = Path.Combine(root, "review.wav"); File.WriteAllBytes(path, wave);
        var review = panel.OpenImportReview(id, path); review.EmitSignal(ConfirmationDialog.SignalName.Canceled);
        SoundImportCheck("review_cancel_no_mutation", document.Pcm().AsSpan().SequenceEqual(original) && document.CurrentName == originalName);
        review = panel.OpenImportReview(id, path); document.DraftName = "changed during review"; review.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        SoundImportCheck("changed_document_review_refused", document.Pcm().AsSpan().SequenceEqual(original)
            && GodotUi.Walk(review).OfType<Label>().Any(label => label.Visible && label.Text.Contains("changed while")));
        review.EmitSignal(ConfirmationDialog.SignalName.Canceled); document.DraftName = document.CurrentName;
        picker = panel.OpenWavImport(id); picker.EmitSignal(EditorFileDialog.SignalName.FileSelected, path);
        review = panel.GetChildren().OfType<SoundImportDialog>().Last(); File.WriteAllBytes(path, new byte[44]); review.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        SoundImportCheck("confirmed_captured_candidate", document.Pcm().AsSpan().SequenceEqual(incoming) && document.CurrentName == originalName
            && _inspector.Current.Origin.Contains("Project PCM") && _inspector.Current.Fields.Any(field => field.Item1 == "Samples" && field.Item2 == document.Samples.ToString()));
        SoundImportCheck("project_preview_play_stop", panel.Play(id, false) && panel.ProjectPreviewPlaying && panel.ProjectPreviewSamples == incoming.Length / 2);
        panel.Stop(); SoundImportCheck("project_preview_stops", !panel.ProjectPreviewPlaying);
        SoundImportCheck("project_pcm_ui_save", _inspector.Sound.Save() && !document.IsDirty);
        string output = Path.Combine(root, "panel-export.wav"); panel.ExportWav(id, output);
        SoundImportCheck("panel_export_project_pcm", IndependentWave(output, Path.Combine(root, "input.pcm")));
        panel.ReopenButton.EmitSignal(Button.SignalName.Pressed); var reopen = panel.GetChildren().OfType<ConfirmationDialog>().Last(); reopen.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        SoundImportCheck("panel_pcm_reopen", panel.WorkingDocument(id) != document && panel.WorkingDocument(id).Pcm().AsSpan().SequenceEqual(incoming));
        document = panel.WorkingDocument(id); string manifest = File.ReadAllText(document.DocumentPath); File.WriteAllText(document.DocumentPath, "{}");
        panel.ReopenButton.EmitSignal(Button.SignalName.Pressed); reopen = panel.GetChildren().OfType<ConfirmationDialog>().Last(); reopen.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        byte[] exported = File.ReadAllBytes(output);
        SoundImportCheck("invalid_project_refuses_export_fallback", panel.WorkingDocument(id) == null && SoundDocumentRefused(() => panel.ExportWav(id, output))
            && File.ReadAllBytes(output).AsSpan().SequenceEqual(exported));
        File.WriteAllText(document.DocumentPath, manifest); panel.ReopenButton.EmitSignal(Button.SignalName.Pressed);
        reopen = panel.GetChildren().OfType<ConfirmationDialog>().Last(); reopen.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        picker = panel.OpenWavImport(id); picker.EmitSignal(EditorFileDialog.SignalName.FileSelected, path);
        SoundImportCheck("invalid_picker_owned_error", panel.GetChildren().OfType<AcceptDialog>().Any(dialog => dialog.Title == "Import sound WAV"));
        document = panel.WorkingDocument(id); document.Revert(); document.Save(); panel.OnAssetsChanged(); panel.Shutdown();
        SoundImportCheck("shutdown_releases_project_audio", !panel.ProjectPreviewPlaying && panel.ProjectPreviewSamples == 0);
        SoundImportCheck("native_sound_files_unchanged", stamps.All(row => row.Value == (new FileInfo(row.Key).Length, File.GetLastWriteTimeUtc(row.Key), Hash(row.Key))));
    }
}
#endif
