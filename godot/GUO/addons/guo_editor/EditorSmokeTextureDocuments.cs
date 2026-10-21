#if TOOLS
namespace GUO.Editor;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;

public partial class EditorSmoke
{
    internal static Image TextureFixture(int size)
    {
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8); image.Fill(new Color(0, 0, 0, 0));
        image.SetPixel(1, 0, Color.Color8(8, 127, 248, 0)); image.SetPixel(2, 0, Color.Color8(127, 128, 129, 128));
        image.SetPixel(3, 0, Colors.Red); return image;
    }
    private void CheckTextureDocuments()
    {
        void Check(string name, bool ok) { _assetReport["texture_document_" + name] = ok; if (!ok) AssetFail("Texture document: " + name); }
        bool Refused(Action action) { try { action(); return false; } catch (Exception e) when (e is IOException or ArgumentException or FormatException) { return true; } }
        string root = Path.Combine(_out, "texture-documents" + Suffix), project = Path.Combine(root, "project"), install = Path.Combine(root, "install");
        byte[] native = new byte[8192]; BinaryPrimitives.WriteUInt16LittleEndian(native, 0x8000); string identity = "fixture-index-v1";
        TextureSource Source() => new(native, identity);
        var doc = TextureDocument.Open(project, install, 3, Source);
        Check("source_full16_retained", !doc.IsDirty && doc.Record().AsSpan().SequenceEqual(native));
        byte[] defensive = doc.Record(); defensive[0] = 255; Check("record_copy_isolated", doc.Record()[0] == 0);
        using Image fixture = TextureFixture(128); byte[] png = fixture.SavePngToBuffer(), changed = TextureDocument.DecodePng(png);
        Check("legacy_quantization_alpha_zero", BinaryPrimitives.ReadUInt16LittleEndian(changed) == 0
            && BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(2)) == 0x05FF
            && BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(4)) == 0x3E10
            && BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(6)) == 0x7C00);
        string token = doc.ReviewToken; doc.Import(changed, token);
        Check("replacement_size_and_dirty", doc.IsDirty && doc.Size == 128 && doc.CanUndo);
        Check("stale_review_refused", Refused(() => doc.Import(native, token)));
        doc.Undo(); Check("undo_exact_source", !doc.IsDirty && doc.Record().AsSpan().SequenceEqual(native));
        doc.Redo(); Check("redo_exact_replacement", doc.Record().AsSpan().SequenceEqual(changed));
        doc.Revert(); Check("revert_is_undoable", doc.Record().AsSpan().SequenceEqual(native) && doc.CanUndo); doc.Undo();
        doc.Save(); var reopened = TextureDocument.Open(project, install, 3, Source);
        Check("save_reload_exact", !reopened.IsDirty && reopened.Record().AsSpan().SequenceEqual(changed));
        using Image small = TextureFixture(64); byte[] smallRecord = TextureDocument.DecodePng(small.SavePngToBuffer());
        doc.Import(smallRecord, doc.ReviewToken); JsonObject recovery = doc.Recovery(); reopened.Restore(recovery);
        Check("draft_recovery", reopened.IsDirty && reopened.Record().AsSpan().SequenceEqual(smallRecord));
        identity = "different-index"; Check("index_mapping_change_refused", Refused(doc.Save)); identity = "fixture-index-v1";
        native[1] ^= 1; Check("native_source_change_refused", Refused(doc.Save)); native[1] ^= 1;
        string saved = File.ReadAllText(doc.DocumentPath); File.AppendAllText(doc.DocumentPath, " ");
        Check("saved_copy_change_refused", Refused(doc.Save)); File.WriteAllText(doc.DocumentPath, saved);
        var wrongRecovery = (JsonObject)recovery.DeepClone(); wrongRecovery["saved_file_sha256"] = "other";
        Check("stale_recovery_refused", Refused(() => reopened.Restore(wrongRecovery)));
        Check("install_project_refused", Refused(() => TextureDocument.Open(install, install, 3, Source)));
        using Image wrongSize = TextureFixture(32); Check("invalid_size_refused", Refused(() => TextureDocument.DecodePng(wrongSize.SavePngToBuffer())));
        Check("truncated_png_refused", Refused(() => TextureDocument.DecodePng(png[..24])));
        doc.RecoveryError = "Invalid recovered draft"; Check("recovery_error_blocks_save", Refused(doc.Save));
        var panel = _assets.Panel<TexturePanel>(); _assets.ShowPanel(panel); panel.Search(panel.SmokeQuery);
        TextureDocument active = panel.WorkingDocument(3); using Image activeFixture = TextureFixture(128);
        TextureImportDialog review = panel.OpenImportReview(active, activeFixture.SavePngToBuffer(), "texture-fixture.png");
        string before = active.RecordHash; Check("review_does_not_mutate", active.RecordHash == before && !active.IsDirty);
        review.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        Check("review_applies_one_undo", active.Size == 128 && active.IsDirty && active.CanUndo);
        panel.OnAssetsChanged(); Check("inspector_document_bound", _inspector.Textures.Document == active && _inspector.Textures.Visible && !_inspector.Textures.SaveButton.Disabled);
        Check("project_tray_tracks_unsaved", panel.Changes().TryGetValue(active.Id, out string changeState) && changeState == "Unsaved texture document");
        _inspector.Textures.Undo(); Check("inspector_undo", !active.IsDirty && active.Size == 64);
        active.Import(TextureDocument.DecodePng(activeFixture.SavePngToBuffer()), active.ReviewToken); panel.OnAssetsChanged();
        _inspector.Textures.StashDrafts(); var recoveredEditor = new TextureEditor(); AddChild(recoveredEditor);
        TextureDocument newDocument = TextureDocument.Open(active.Project, active.Install, active.Id, () => _data.SourceTexture(active.Id)); recoveredEditor.Bind(newDocument, null);
        Check("editor_local_recovery", newDocument.IsDirty && newDocument.Size == 128); recoveredEditor.Discard(newDocument); recoveredEditor.Free();
        active.Undo(); panel.OnAssetsChanged(); _inspector.Textures.StashDrafts();
    }
}
#endif
