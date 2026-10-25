#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

public partial class EditorSmoke
{
    private void CheckItemMetadataDocument()
    {
        byte[] raw = Enumerable.Range(1, 41).Select(value => (byte)value).ToArray();
        Encoding.ASCII.GetBytes("original\0").CopyTo(raw, 21);
        var document = new ItemMetadataDocument(7, raw);
        byte[] baseline = document.PreviewRecord();
        document.Edit(document.Current.Name, 0x8000000000000001, 255);
        byte[] preview = document.PreviewRecord();
        MetadataCheck("unknown_fields_preserved", preview.AsSpan(8, 12).SequenceEqual(raw.AsSpan(8, 12))
            && preview.AsSpan(21, 20).SequenceEqual(raw.AsSpan(21, 20)));
        MetadataCheck("flags_64_bit", document.Current.Flags == 0x8000000000000001
            && ItemMetadataDocument.ParseFlags("0xFFFFFFFFFFFFFFFF") == ulong.MaxValue);
        document.Undo();
        MetadataCheck("undo_exact", document.PreviewRecord().AsSpan().SequenceEqual(baseline));
        document.Redo();
        MetadataCheck("redo", document.Current.Height == 255 && document.IsDirty);
        document.Edit("changed", document.Current.Flags, 6);
        document.Revert();
        MetadataCheck("revert_exact", document.PreviewRecord().AsSpan().SequenceEqual(raw));
        bool badName = false, badHeight = false;
        try { document.Edit(new string('é', 11), 0, 0); } catch (ArgumentException) { badName = true; }
        try { document.Edit("ok", 0, 256); } catch (ArgumentException) { badHeight = true; }
        MetadataCheck("invalid_fields", badName && badHeight);

        string input = Path.Combine(_out, "metadata-fixture" + Suffix);
        Directory.CreateDirectory(input);
        string source = Path.Combine(input, "tiledata.mul");
        const int landBytes = 512 * (4 + 32 * 30);
        byte[] file = new byte[landBytes + 4 + 32 * 41];
        raw.CopyTo(file, landBytes + 4 + 7 * 41);
        File.WriteAllBytes(source, file);
        document.Edit("saved", ulong.MaxValue, 11);
        document.Save(_assetRoot, input, source);
        var reopened = ItemMetadataDocument.Open(_assetRoot, input, 7, ItemMetadataDocument.ReadRecord(source, 7));
        MetadataCheck("save_reload", reopened.Current == document.Current && !reopened.IsDirty && !document.IsDirty);
        MetadataCheck("native_file_unchanged", File.ReadAllBytes(source).AsSpan().SequenceEqual(file));
        string nativeToken = document.ReviewToken;
        JsonNode native = JsonNode.Parse(document.NativeStageInput(_assetRoot, input, source, nativeToken));
        MetadataCheck("native_export_full_record", (string)native["domain"] == "item-metadata"
            && Convert.FromBase64String((string)native["records"][0]["source_record_base64"]).AsSpan().SequenceEqual(raw)
            && Convert.FromBase64String((string)native["records"][0]["record_base64"]).AsSpan().SequenceEqual(document.PreviewRecord()));
        bool Refused(Action action) { try { action(); return false; } catch (IOException) { return true; } }
        document.Edit("native changed", 0, 0);
        MetadataCheck("native_stale_document_refused", Refused(() => document.NativeStageInput(_assetRoot, input, source, nativeToken)));
        document.Undo();
        string savedPath = ItemMetadataDocument.DocumentPath(_assetRoot, input, 7); byte[] savedBytes = File.ReadAllBytes(savedPath);
        File.AppendAllText(savedPath, " ");
        MetadataCheck("native_external_saved_copy_refused", Refused(() => document.NativeStageInput(_assetRoot, input, source, document.ReviewToken)));
        MetadataCheck("external_saved_copy_save_refused", Refused(() => document.Save(_assetRoot, input, source)));
        File.WriteAllBytes(savedPath, savedBytes);
        bool sourceRefusal = false, installRefusal = false, loadRefusal = false;
        file[landBytes + 4 + 7 * 41 + 8] ^= 1;
        File.WriteAllBytes(source, file);
        try { document.Save(_assetRoot, input, source); } catch (IOException) { sourceRefusal = true; }
        MetadataCheck("native_source_change_refused", Refused(() => document.NativeStageInput(_assetRoot, input, source, document.ReviewToken)));
        try { ItemMetadataDocument.Open(_assetRoot, input, 7, ItemMetadataDocument.ReadRecord(source, 7)); }
        catch (IOException) { loadRefusal = true; }
        try { ItemMetadataDocument.DocumentPath(input, input, 7); } catch (IOException) { installRefusal = true; }
        MetadataCheck("source_change_and_install_refused", sourceRefusal && loadRefusal && installRefusal);
        var dialog = new ItemMetadataDialog();
        dialog.Build(_data, reopened, source);
        AddChild(dialog);
        MetadataCheck("dialog_ready", dialog.GetOkButton().Text == "Close");
        reopened.Edit("unsaved", 1, 1);
        dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
        var discard = dialog.GetChildren().OfType<ConfirmationDialog>().FirstOrDefault();
        MetadataCheck("unsaved_close_prompts", discard != null && dialog.Visible);
        discard?.EmitSignal(AcceptDialog.SignalName.Canceled);
        MetadataCheck("cancel_retains_work", reopened.IsDirty && dialog.Visible);
        dialog.Free();
    }

    private void CheckMetadataNativeRecovery()
    {
        var settings = EditorInterface.Singleton.GetEditorSettings();
        Variant previous = settings.GetProjectMetadata("guo_assets", "metadata_drafts", new Godot.Collections.Array());
        var editor = new ItemMetadataEditor(); AddChild(editor);
        try
        {
            int id = 26; string root = _data.Assets.Root;
            var rows = new Godot.Collections.Array<Godot.Collections.Dictionary>
            {
                new() { ["root"] = root, ["id"] = id, ["hash"] = "stale-source", ["name"] = "retained stale name", ["flags"] = "0x0", ["height"] = "1" },
                new() { ["root"] = root, ["id"] = 27, ["hash"] = "other-draft", ["name"] = "keep other draft", ["flags"] = "0x0", ["height"] = "2" },
            };
            settings.SetProjectMetadata("guo_assets", "metadata_drafts", rows); editor.Bind(_data, id);
            bool refused = false;
            try { editor.CaptureNativeReview(_data, id); } catch (IOException) { refused = true; }
            MetadataCheck("native_stale_recovery_refused", refused && editor.SaveButton.Disabled);
            editor.StashDrafts();
            var retained = settings.GetProjectMetadata("guo_assets", "metadata_drafts").AsGodotArray();
            MetadataCheck("native_stale_recovery_retained", retained.Any(v => v.AsGodotDictionary().TryGetValue("native_error", out _)
                && v.AsGodotDictionary()["name"].AsString() == "retained stale name"));
            ItemMetadataDocument before = editor.CurrentDocument;
            editor.Reopen(_data, id); editor.GetChildren().OfType<ConfirmationDialog>().Last().EmitSignal(ConfirmationDialog.SignalName.Canceled);
            MetadataCheck("native_reopen_cancel_preserves_draft", ReferenceEquals(before, editor.CurrentDocument));
            editor.Reopen(_data, id); editor.GetChildren().OfType<ConfirmationDialog>().Last().EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            ItemMetadataNativeReview review = editor.CaptureNativeReview(_data, id);
            MetadataCheck("native_reopen_discards_only_selected_recovery", !ReferenceEquals(before, editor.CurrentDocument)
                && settings.GetProjectMetadata("guo_assets", "metadata_drafts").AsGodotArray().All(v => v.AsGodotDictionary()["id"].AsInt32() == 27));
            bool projectRefused = false;
            try { editor.RequireNativeReview(_data, review with { Root = Path.Combine(root, "other-project") }); } catch (IOException) { projectRefused = true; }
            MetadataCheck("native_project_change_refused", projectRefused);
        }
        finally { editor.Free(); settings.SetProjectMetadata("guo_assets", "metadata_drafts", previous); }
    }

    private void MetadataCheck(string name, bool ok)
    {
        _assetReport["item_metadata_" + name] = ok;
        if (!ok) AssetFail("item metadata: " + name);
    }
}
#endif
