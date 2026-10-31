#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

public partial class EditorSmoke
{
    private void HueTextCheck(string name, bool ok)
    { _assetReport["hue_text_" + name] = ok; if (!ok) AssetFail("Hue text: " + name); }
    private void CheckHueText()
    {
        ushort[] colors = Enumerable.Range(0, 32).Select(i => (ushort)(i * 2114)).ToArray(); colors[0] = 0; colors[31] = 65535;
        var entry = new HueTextEntry("café", 0xCAFE, 0xFFFF, colors);
        byte[] legacy = HueText.Encode(entry, HueTextFormat.Fiddler1252);
        string expected = "caf\u00e9\r\n51966\r\n65535\r\n" + string.Concat(colors.Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n"));
        byte[] expectedBytes = expected.Select(c => checked((byte)c)).ToArray();
        HueTextCheck("exact_fiddler_1252_35_line_bytes", legacy.SequenceEqual(expectedBytes));
        var decoded = HueText.Decode(legacy, HueTextFormat.Fiddler1252);
        HueTextCheck("all_16_bits_and_bounds_roundtrip", decoded.Name == "café" && decoded.Start == 0xCAFE && decoded.End == 65535 && decoded.Colors.SequenceEqual(colors));
        var unicode = entry with { Name = "日本語 palette 🟩" }; byte[] utf8 = HueText.Encode(unicode, HueTextFormat.Utf8);
        HueTextCheck("explicit_utf8_preserves_unicode", HueText.Decode(utf8, HueTextFormat.Utf8).Name == unicode.Name && !utf8.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        HueTextCheck("unrepresentable_1252_refused", HueRefused(() => HueText.Encode(unicode, HueTextFormat.Fiddler1252)));
        byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8).ToArray();
        HueTextCheck("bom_requires_utf8_choice", HueText.Decode(bom, HueTextFormat.Utf8).Name == unicode.Name && HueRefused(() => HueText.Decode(bom, HueTextFormat.Fiddler1252)));
        HueTextCheck("invalid_utf8_utf16_and_oversize_refused", HueRefused(() => HueText.Decode(new byte[] { 0xC3, 0x28 }, HueTextFormat.Utf8))
            && HueRefused(() => HueText.Decode(new byte[] { 0xFF, 0xFE, 0, 0 }, HueTextFormat.Fiddler1252)) && HueRefused(() => HueText.Decode(new byte[HueText.MaxBytes + 1], HueTextFormat.Utf8)));
        string body = Encoding.UTF8.GetString(HueText.Encode(entry with { Name = "name" }, HueTextFormat.Utf8));
        byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
        HueTextCheck("missing_extra_and_invalid_decimal_refused", HueRefused(() => HueText.Decode(Text(body[..body.LastIndexOf("65535", StringComparison.Ordinal)]), HueTextFormat.Utf8))
            && HueRefused(() => HueText.Decode(Text(body + "extra\n"), HueTextFormat.Utf8)) && HueRefused(() => HueText.Decode(Text(body.Replace("51966", "65536")), HueTextFormat.Utf8))
            && HueRefused(() => HueText.Decode(Text(body.Replace("51966", "-1")), HueTextFormat.Utf8)) && HueRefused(() => HueText.Decode(Text(body.Replace("51966", "0xCAFE")), HueTextFormat.Utf8)));
        HueTextCheck("line_endings_and_decimal_whitespace", HueText.Decode(Text(body.Replace("\r\n", "\n").Replace("51966", " +51966 ")), HueTextFormat.Utf8).Start == 0xCAFE
            && HueText.Decode(Text(body.Replace("\r\n", "\r")), HueTextFormat.Utf8).Colors.SequenceEqual(colors));
        HueTextCheck("nul_name_and_wrong_palette_size_refused", HueRefused(() => HueText.Encode(entry with { Name = "bad\0name" }, HueTextFormat.Utf8))
            && HueRefused(() => HueText.Encode(entry with { Colors = new ushort[31] }, HueTextFormat.Utf8)));
        HueTextCheck("name_utf8_limit", HueText.Encode(entry with { Name = new string('é', 2048) }, HueTextFormat.Utf8).Length < HueText.MaxBytes
            && HueRefused(() => HueText.Encode(entry with { Name = new string('é', 2049) }, HueTextFormat.Utf8)));
        byte[] names = HueText.EncodeNames(new[] { (33, "café"), (1, "日本語") });
        HueTextCheck("names_zero_based_hex_utf8_crlf", Encoding.UTF8.GetString(names) == "0x0 日本語\r\n0x20 café\r\n");
        HueTextCheck("names_duplicate_and_invalid_ids_refused", HueRefused(() => HueText.EncodeNames(new[] { (1, "a"), (1, "b") }))
            && HueRefused(() => HueText.EncodeNames(new[] { (0, "bad") })));

        string root = Path.Combine(_out, "hue-text" + Suffix + "-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project"), install = Path.Combine(root, "install"); Directory.CreateDirectory(install);
        byte[] source = new byte[88]; Encoding.ASCII.GetBytes("source").CopyTo(source, 68);
        HueDocument Open() => HueDocument.Open(project, install, 1, () => source);
        var document = Open(); byte[] captured = (byte[])legacy.Clone(); var plan = new HueTextReviewPlan(document, captured, HueTextFormat.Fiddler1252);
        captured[0] ^= 1; var candidate = plan.Candidate; candidate.Colors[31] = 0; plan.Apply();
        HueTextCheck("captured_candidate_is_immutable_and_undoable", document.Name == "café" && document.ColorAt(31) == 65535 && document.Start == 0xCAFE && document.IsDirty);
        document.Undo(); HueTextCheck("one_undo_restores_entire_record", document.PreviewRecord().SequenceEqual(source)); document.Redo(); document.Save();
        HueTextCheck("import_save_reload", Open().Colors().SequenceEqual(colors) && Open().Name == "café");
        plan = new(document, legacy, HueTextFormat.Fiddler1252); document.DraftName = new string('a', 21) + "first"; document.CommitDraft();
        HueTextCheck("working_name_change_refuses_stale_review", !plan.CanApply && HueRefused(plan.Apply));
        plan = new(document, legacy, HueTextFormat.Fiddler1252); document.DraftName = new string('a', 21) + "second"; document.CommitDraft();
        HueTextCheck("full_name_beyond_native_prefix_guarded", !plan.CanApply && HueRefused(plan.Apply)); document.Undo(); document.Undo();
        document.DraftColor = "invalid"; plan = new(document, legacy, HueTextFormat.Fiddler1252);
        HueTextCheck("invalid_pending_fields_refuse_import", !plan.CanApply && HueRefused(plan.Apply) && document.DraftColor == "invalid"); document.Undo();
        plan = new(document, legacy, HueTextFormat.Fiddler1252); source[0] = 1;
        HueTextCheck("source_change_refuses_import", !plan.CanApply && HueRefused(plan.Apply)); source[0] = 0;
        string destination = Path.Combine(root, "palette.txt"); HueText.Export(document, destination, HueTextFormat.Fiddler1252);
        HueTextCheck("export_exact_committed_values", HueText.Read(destination).SequenceEqual(legacy));
        document.DraftName = unicode.Name; document.CommitDraft();
        HueTextCheck("failed_encoding_preserves_destination", HueRefused(() => HueText.Export(document, destination, HueTextFormat.Fiddler1252)) && File.ReadAllBytes(destination).SequenceEqual(legacy));
        HueText.Export(document, destination, HueTextFormat.Utf8); HueTextCheck("utf8_export_preserves_working_name", HueText.Decode(File.ReadAllBytes(destination), HueTextFormat.Utf8).Name == unicode.Name);
        HueTextCheck("install_export_refused", HueRefused(() => HueText.Export(document, Path.Combine(install, "palette.txt"), HueTextFormat.Utf8)));
        var stale = new HueTextReviewPlan(document, legacy, HueTextFormat.Fiddler1252); File.AppendAllText(document.Path, " ");
        HueTextCheck("external_saved_copy_change_refuses_import_export", !stale.CanApply && HueRefused(stale.Apply) && HueRefused(() => HueText.Export(document, destination, HueTextFormat.Utf8)));

        var panel = _assets.Panel<HuePanel>(); _assets.ShowPanel(panel); int id = panel.Search("0x0021") ?? -1; var active = panel.WorkingDocument(id);
        byte[] before = active.PreviewRecord(); string beforeName = active.Name;
        var review = panel.OpenTextReview(active, legacy, "palette-fixture.txt");
        HueTextCheck("native_review_ready", review.Plan.CanApply && !review.GetOkButton().Disabled && review.EncodingChoice.ItemCount == 2);
        review.EmitSignal(ConfirmationDialog.SignalName.Canceled);
        HueTextCheck("native_cancel_preserves_document", active.PreviewRecord().SequenceEqual(before) && active.Name == beforeName);
        review = panel.OpenTextReview(active, legacy, "palette-fixture.txt"); review.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        HueTextCheck("native_confirm_edits_working_copy_only", active.Name == "café" && active.IsDirty && active.ColorAt(31) == 65535); active.Undo(); panel.OnAssetsChanged();
        HueTextCheck("native_import_one_undo", active.PreviewRecord().SequenceEqual(before) && active.Name == beforeName && !active.IsDirty);
        string list = Path.Combine(root, "names.txt"); panel.ExportNames(list); string line = File.ReadLines(list).First(row => row.StartsWith($"0x{id - 1:X} ", StringComparison.Ordinal));
        HueTextCheck("native_names_export_uses_zero_based_ids", line == $"0x{id - 1:X} {active.Name}");
    }
}
#endif
