#if TOOLS
namespace GUO.Editor;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

public partial class EditorSmoke
{
    private void HueCheck(string name, bool ok)
    { _assetReport["hue_document_" + name] = ok; if (!ok) AssetFail("Hue document: " + name); }
    private static bool HueRefused(Action action)
    { try { action(); return false; } catch (Exception e) when (e is ArgumentException or IOException) { return true; } }
    private void CheckHueDocuments()
    {
        string root = Path.Combine(_out, "hue-documents" + Suffix + "-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project"), install = Path.Combine(root, "install"); Directory.CreateDirectory(install);
        byte[] source = Enumerable.Range(0, 88).Select(i => (byte)(i * 7)).ToArray();
        Array.Clear(source, 68, 20); Encoding.ASCII.GetBytes("original").CopyTo(source, 68); source[87] = 0xE3;
        byte[] baseline = (byte[])source.Clone();
        HueDocument Open(int id = 1) => HueDocument.Open(project, install, id, () => source);
        var document = Open();
        HueCheck("exact_88_byte_baseline", document.PreviewRecord().AsSpan().SequenceEqual(source) && !document.IsDirty);
        ushort old = document.ColorAt(0); ushort[] returned = document.Colors(); returned[0] ^= 1;
        HueCheck("palette_copy_is_immutable", document.ColorAt(0) == old);
        document.DraftName = "Project moss"; document.DraftColor = "0x83E0"; document.DraftEnd = "0xFFFF";
        HueCheck("atomic_valid_fields", document.CommitDraft() && document.ColorAt(0) == 0x83E0 && document.End == 65535 && document.Name == "Project moss");
        document.Undo(); HueCheck("single_undo_restores_raw_record", document.PreviewRecord().AsSpan().SequenceEqual(source)); document.Redo();
        document.DraftColor = "0x10000"; document.DraftName = "invalid pending";
        HueCheck("invalid_color_retains_all_raw_fields", !document.CommitDraft() && !document.SelectColor(1) && document.Index == 0 && document.Name == "Project moss" && document.DraftName == "invalid pending");
        var rawRecovery = document.Recovery(); var recovered = Open(); recovered.Restore(rawRecovery);
        HueCheck("invalid_raw_draft_recovers", recovered.Pending && recovered.DraftColor == "0x10000" && recovered.Name == "Project moss");
        document.Undo(); HueCheck("undo_discards_invalid_fields_first", !document.Pending && document.Name == "Project moss");
        document.Save(); var reopened = Open();
        HueCheck("save_reload_full_16_bits_and_bounds", !reopened.IsDirty && !reopened.CanUndo && !reopened.CanRedo && reopened.ColorAt(0) == 0x83E0 && reopened.End == 65535 && reopened.Name == "Project moss");
        document.Undo(); HueCheck("undo_after_save_is_dirty", document.IsDirty); document.Redo();
        var json = JsonNode.Parse(File.ReadAllText(document.Path)).AsObject(); json["future_metadata"] = "preserve me";
        json.Remove("source_sha256"); json.Remove("source_record_base64"); File.WriteAllText(document.Path, json.ToJsonString());
        HueCheck("changed_saved_copy_refuses_overwrite", HueRefused(document.Save));
        var legacy = Open(); legacy.DraftName = "legacy renamed"; legacy.CommitDraft(); legacy.Save();
        HueCheck("legacy_overlay_upgrade_preserves_unknown_keys", (string)JsonNode.Parse(File.ReadAllText(legacy.Path))["future_metadata"] == "preserve me" && Open().Name == "legacy renamed");
        HueCheck("stale_recovery_refused_atomically", HueRefused(() => legacy.Restore(rawRecovery)) && legacy.Name == "legacy renamed");
        var colors = Enumerable.Repeat((ushort)0xFC00, 32).ToArray(); legacy.ReplacePalette(colors); colors[0] = 0;
        HueCheck("palette_import_copies_input", legacy.ColorAt(0) == 0xFC00); legacy.Undo();
        HueCheck("palette_import_single_undo", legacy.ColorAt(0) == 0x83E0);
        legacy.DraftName = new string('é', 2048); bool fits = legacy.CommitDraft(); legacy.DraftName += "é";
        HueCheck("utf8_project_name_limit", fits && !legacy.CommitDraft() && legacy.NameWarning.Length > 0); legacy.Undo();
        legacy.DraftName = "bad\0name"; HueCheck("nul_name_refused", !legacy.CommitDraft()); legacy.Undo();
        source[0] ^= 1; HueCheck("loaded_base_change_refuses_save", HueRefused(legacy.Save)); source = baseline;
        var bound = JsonNode.Parse(File.ReadAllText(legacy.Path)).AsObject(); bound["hue"] = 2; File.WriteAllText(legacy.Path, bound.ToJsonString());
        HueCheck("wrong_saved_id_refused", HueRefused(() => Open()));
        bound["hue"] = 1; bound["source_sha256"] = "bad"; File.WriteAllText(legacy.Path, bound.ToJsonString());
        HueCheck("foreign_source_binding_refused", HueRefused(() => Open()));
        HueCheck("install_destination_refused", HueRefused(() => HueDocument.Open(install, install, 1, () => source)));

        var settings = EditorInterface.Singleton.GetEditorSettings(); Variant previous = settings.GetProjectMetadata("guo_hues", "drafts", "[]");
        settings.SetProjectMetadata("guo_hues", "drafts", "[]");
        var editor = new HueEditor(); AddChild(editor); HueEditor freshEditor = null;
        try
        {
            var first = Open(2); var second = Open(3); editor.Bind(first, null);
            editor.ColorInput.Text = "invalid"; editor.ColorInput.EmitSignal(LineEdit.SignalName.TextChanged, "invalid");
            editor.Bind(second, null); editor.Bind(first, null);
            HueCheck("inspector_retains_invalid_selection_draft", editor.ColorInput.Text == "invalid" && editor.SaveButton.Disabled && !editor.Save());
            editor.StashDrafts(); var fresh = Open(2); freshEditor = new HueEditor(); AddChild(freshEditor); freshEditor.Bind(fresh, null);
            HueCheck("editor_reload_restores_invalid_draft", freshEditor.ColorInput.Text == "invalid" && fresh.Pending && freshEditor.SaveButton.Disabled);
            freshEditor.Undo(); fresh.DraftColor = "0x8000"; fresh.CommitDraft(); freshEditor.Bind(fresh, null);
            freshEditor.Picker.Color = Colors.Red; freshEditor.ApplyPickedColor();
            HueCheck("picker_quantizes_and_preserves_bit15", fresh.ColorAt(0) == 0xFC00 && freshEditor.Palette.GetChildCount() == 32);
            HueCheck("editor_save_clears_recovery", freshEditor.Save() && !fresh.IsDirty);
            editor.Discard(first); freshEditor.Discard(fresh);
        }
        finally { freshEditor?.Free(); editor.Free(); settings.SetProjectMetadata("guo_hues", "drafts", previous); }

        var panel = _assets.Panel<HuePanel>(); _assets.ShowPanel(panel); int id = panel.Search("0x0021") ?? -1;
        // Earlier asset tests deliberately overwrite this palette via the legacy import API.
        // Review that changed saved copy through the same explicit reopen action as the UI.
        panel.ReopenButton.EmitSignal(Button.SignalName.Pressed);
        panel.GetChildren().OfType<ConfirmationDialog>().Last().EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        byte[] native = _data.SourceHueRecord(id); var active = panel.WorkingDocument(id);
        string initialName = active.Name; ushort initialStart = active.Start, initialEnd = active.End; ushort[] initialColors = active.Colors();
        HueCheck("native_inspector_binding", active != null && _inspector.Hues.Visible && _inspector.Hues.Document == active && _inspector.Hues.Footer.Visible);
        string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        var files = Directory.GetFiles(_data.ClientData).Where(p => Path.GetFileName(p).Equals("hues.mul", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Equals("verdata.mul", StringComparison.OrdinalIgnoreCase)).ToDictionary(p => p, Hash);
        string savedHash = File.Exists(active.Path) ? Hash(active.Path) : "missing";
        active.DraftName = "Project moss fixture"; active.DraftColor = "0x83E0"; active.CommitDraft(); panel.OnAssetsChanged();
        HueCheck("unsaved_preview_does_not_mutate_loaded_base", native.AsSpan().SequenceEqual(_data.SourceHueRecord(id)) && savedHash == (File.Exists(active.Path) ? Hash(active.Path) : "missing"));
        HueCheck("working_name_search", panel.Search("Project moss fixture") == id);
        HueCheck("active_project_save", _inspector.Hues.Save()); panel.OnAssetsChanged();
        HueCheck("saved_overlay_retains_original_base", native.AsSpan().SequenceEqual(_data.SourceHueRecord(id)) && active.ColorAt(0) == 0x83E0);
        string png = Path.Combine(root, "palette.png"); using (Image image = Image.CreateEmpty(32, 1, false, Image.Format.Rgba8)) { image.Fill(Colors.Red); image.SavePng(png); }
        panel.ImportPalette(id, png); HueCheck("png_import_is_unsaved_working_edit", active.ColorAt(0) == 0x7C00 && active.IsDirty); active.Undo();
        HueCheck("png_import_undo_restores_raw_high_bit", active.ColorAt(0) == 0x83E0 && !active.IsDirty);
        using (Image image = Image.CreateEmpty(32, 1, false, Image.Format.Rgba8))
        {
            image.Fill(Color.Color8(7, 7, 7, 0));
            image.SetPixel(1, 0, Color.Color8(8, 127, 248));
            image.SetPixel(2, 0, Color.Color8(127, 128, 129));
            image.SetPixel(3, 0, Color.Color8(248, 4, 4)); image.SavePng(png);
        }
        panel.ImportPalette(id, png);
        HueCheck("png_import_legacy_shift_boundaries_alpha_and_zero", active.ColorAt(0) == 0 && active.ColorAt(1) == 0x05FF && active.ColorAt(2) == 0x3E10 && active.ColorAt(3) == 0x7C00);
        active.Undo(); HueCheck("png_boundary_import_one_undo", active.ColorAt(0) == 0x83E0 && !active.IsDirty);
        active.DraftName = "discarded working hue"; active.CommitDraft();
        panel.ReopenButton.EmitSignal(Button.SignalName.Pressed); panel.GetChildren().OfType<ConfirmationDialog>().Last().EmitSignal(ConfirmationDialog.SignalName.Canceled);
        HueCheck("reopen_cancel_retains_working_name", panel.WorkingDocument(id) == active && active.Name == "discarded working hue");
        panel.ReopenButton.EmitSignal(Button.SignalName.Pressed); panel.GetChildren().OfType<ConfirmationDialog>().Last().EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        HueCheck("reopen_confirm_discards_working_name", panel.WorkingDocument(id) != active && panel.WorkingDocument(id).Name == "Project moss fixture" && !panel.WorkingDocument(id).IsDirty);
        active = panel.WorkingDocument(id);
        // Keep the earlier export/control fixture distinct from the install for the
        // harness's client read-back probes after this test completes.
        active.Revert(); active.ReplacePalette(initialColors); active.DraftName = initialName;
        active.DraftStart = $"0x{initialStart:X4}"; active.DraftEnd = $"0x{initialEnd:X4}"; active.CommitDraft(); active.Save(); panel.OnAssetsChanged();
        HueCheck("native_files_unchanged", files.All(row => row.Value == Hash(row.Key)));
    }
}
#endif
