#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

public partial class EditorSmoke
{
    private void CsvCheck(string name, bool ok) => ClilocCheck("csv_" + name, ok);
    private void CheckClilocCsv()
    {
        const ClilocCsvFormat legacy = ClilocCsvFormat.Fiddler, quoted = ClilocCsvFormat.QuotedUtf8;
        byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
        var native = new[] { new ClilocCsvRow(1, "日本語", 0), new ClilocCsvRow(2, "custom", 1), new ClilocCsvRow(3, "modified", 2),
            new ClilocCsvRow(4, "both", 3), new ClilocCsvRow(5, "unknown", 243) };
        byte[] export = ClilocCsv.Encode(native, legacy);
        CsvCheck("fiddler_exact_utf8_named_flags_crlf", export.AsSpan().SequenceEqual(Bytes("Number;Text;Flag\r\n1;日本語;Original\r\n2;custom;Custom\r\n3;modified;Modified\r\n4;both;Custom, Modified\r\n5;unknown;243\r\n")));
        CsvCheck("fiddler_flag_vocabulary_roundtrip", ClilocCsv.Parse(export, legacy).SequenceEqual(native));
        string bomText = "Number;Text;Flag\r\n17;日本語 😀;Modified\r\n";
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true),
            new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
        {
            byte[] bomFile = encoding.GetPreamble().Concat(encoding.GetBytes(bomText)).ToArray();
            using var reference = new StreamReader(new MemoryStream(bomFile));
            CsvCheck("fiddler_bom_" + encoding.WebName, reference.ReadToEnd() == bomText
                && ClilocCsv.Parse(bomFile, legacy).Single() == new ClilocCsvRow(17, "日本語 😀", 2));
        }
        CsvCheck("malformed_bom_encodings_refused", ClilocRefused(() => ClilocCsv.Parse(new byte[] { 0xFF, 0xFE, 0x41 }, legacy))
            && ClilocRefused(() => ClilocCsv.Parse(new byte[] { 0, 0, 0xFE, 0xFF, 0, 0x11, 0, 0 }, legacy)));
        CsvCheck("quoted_dialect_remains_utf8_only", ClilocRefused(() => ClilocCsv.Parse(new byte[] { 0xFF, 0xFE, 0x41, 0 }, quoted)));
        var trimmed = ClilocCsv.Parse(Bytes("\uFEFF# comment;ignored\r\nNumber;Text;Flag\r\n\r\n 17 ; text ; Modified \r\n"), legacy);
        CsvCheck("fiddler_comments_bom_whitespace", trimmed.Single() == new ClilocCsvRow(17, "text", 2));
        CsvCheck("headerless_rows", ClilocCsv.Parse(Bytes("1;text;0"), legacy).Count == 1);
        CsvCheck("invalid_utf8_refused", ClilocRefused(() => ClilocCsv.Parse(new byte[] { 0xff }, legacy)));
        CsvCheck("negative_overflow_ids_refused", ClilocRefused(() => ClilocCsv.Parse(Bytes("-1;x;0"), legacy))
            && ClilocRefused(() => ClilocCsv.Parse(Bytes("2147483648;x;0"), legacy)));
        CsvCheck("duplicate_ids_refused", ClilocRefused(() => ClilocCsv.Parse(Bytes("1;a;0\n1;b;0"), legacy)));
        CsvCheck("bad_flags_refused", ClilocRefused(() => ClilocCsv.Parse(Bytes("1;x;256"), legacy)) && ClilocRefused(() => ClilocCsv.Parse(Bytes("1;x;nope"), legacy)));
        CsvCheck("malformed_empty_nul_refused", ClilocRefused(() => ClilocCsv.Parse(Bytes("Number;Text;Flag\r\n"), legacy))
            && ClilocRefused(() => ClilocCsv.Parse(Bytes("1;a;0;extra"), legacy)) && ClilocRefused(() => ClilocCsv.Parse(Bytes("1;a\0b;0"), legacy)));
        string rich = "  日本語;\"quoted\"\r\nsecond line  ";
        var richRows = new[] { new ClilocCsvRow(9, rich, 0), new ClilocCsvRow(10, "", 2) };
        CsvCheck("quoted_multiline_quotes_spaces_blank_roundtrip", ClilocCsv.Parse(ClilocCsv.Encode(richRows, quoted), quoted).SequenceEqual(richRows));
        CsvCheck("legacy_unsafe_text_refused", ClilocRefused(() => ClilocCsv.Encode(richRows, legacy))
            && ClilocRefused(() => ClilocCsv.Encode(new[] { new ClilocCsvRow(1, " space ", 0) }, legacy)));
        CsvCheck("quoted_malformed_refused", ClilocRefused(() => ClilocCsv.Parse(Bytes("1;\"missing;0"), quoted))
            && ClilocRefused(() => ClilocCsv.Parse(Bytes("1;\"x\"garbage;0"), quoted))
            && ClilocRefused(() => ClilocCsv.Parse(Bytes("1;un\"quoted;0"), quoted)));
        CsvCheck("unsupported_dialect_refused", ClilocRefused(() => ClilocCsv.Parse(export, (ClilocCsvFormat)99)));

        string root = Path.Combine(_out, "cliloc-csv" + Suffix + "-" + Guid.NewGuid().ToString("N")), install = Path.Combine(root, "install"), project = Path.Combine(root, "project");
        Directory.CreateDirectory(install); string source = Path.Combine(install, "Cliloc.enu");
        byte[] original = ClilocFixture((1, 243, "source ~1_NAME~"), (2, 130, "second"), (3, 7, "keep"), (4, 0, new string('x', 40000)));
        File.WriteAllBytes(source, original); DateTime stamp = File.GetLastWriteTimeUtc(source);
        ClilocDocument Open() => ClilocDocument.Open(project, install, "enu", source);
        var doc = Open(); doc.Edit(2, "preexisting");
        CsvCheck("null_text_refused_for_missing_id", ClilocRefused(() => doc.Edit(99, null)) && doc.Get(99) == null);
        var rows = new[] { new ClilocCsvRow(1, "edited ~1_NAME~", 2), new ClilocCsvRow(2, "日本語", 1), new ClilocCsvRow(9, "new", 1) };
        var plan = new ClilocCsvPlan(doc, ClilocCsv.Encode(rows, legacy), legacy);
        CsvCheck("review_does_not_mutate", doc.Get(1).Text == "source ~1_NAME~" && doc.Get(2).Text == "preexisting" && doc.Get(9) == null
            && plan.Records == 3 && plan.Changes.Count == 3 && plan.FlagDifferences == 3);
        plan.Apply();
        CsvCheck("merge_preserves_flags_omissions", doc.Get(1) == new ClilocDocument.Entry(243, "edited ~1_NAME~")
            && doc.Get(2).Flag == 130 && doc.Get(9).Flag == 0 && doc.Get(3).Text == "keep");
        var utf16 = new UnicodeEncoding(false, true, true);
        byte[] bomImport = utf16.GetPreamble().Concat(utf16.GetBytes("Number;Text;Flag\r\n3;UTF-16 日本語;Modified\r\n")).ToArray();
        new ClilocCsvPlan(doc, bomImport, legacy).Apply();
        CsvCheck("bom_import_uses_same_review_and_preserves_flags", doc.Get(3) == new ClilocDocument.Entry(7, "UTF-16 日本語"));
        doc.Undo();
        doc.Undo(); CsvCheck("batch_single_undo_retains_prior_edits", doc.Get(1).Text == "source ~1_NAME~" && doc.Get(2).Text == "preexisting" && doc.Get(9) == null);
        doc.Redo(); CsvCheck("batch_single_redo", doc.Get(1).Text == "edited ~1_NAME~" && doc.Get(9).Text == "new");
        doc.Save(); CsvCheck("batch_persists_project_only", Open().Get(9).Text == "new" && File.ReadAllBytes(source).AsSpan().SequenceEqual(original) && File.GetLastWriteTimeUtc(source) == stamp);
        var unchanged = new ClilocCsvPlan(doc, ClilocCsv.Encode(doc.Ids.Select(id => new ClilocCsvRow(id, doc.Get(id).Text, doc.Get(id).Flag)), legacy), legacy);
        long revision = doc.Revision; unchanged.Apply();
        CsvCheck("unchanged_long_source_no_history", unchanged.Changes.Count == 0 && doc.Revision == revision && doc.Get(4).Text.Length == 40000);
        var invalid = rows.Append(new ClilocCsvRow(11, new string('x', 32768), 0));
        CsvCheck("invalid_late_row_atomic", ClilocRefused(() => new ClilocCsvPlan(doc, ClilocCsv.Encode(invalid, quoted), quoted)) && doc.Revision == revision && doc.Get(11) == null);
        var oversized = Enumerable.Range(100000, 10001).ToDictionary(id => id, _ => "x");
        CsvCheck("change_count_budget_atomic", ClilocRefused(() => doc.ApplyTextBatch(oversized)) && doc.Revision == revision && doc.Get(100000) == null);
        var textBudget = Enumerable.Range(100000, 70).ToDictionary(id => id, _ => new string('x', 32767));
        CsvCheck("text_budget_atomic", ClilocRefused(() => doc.ApplyTextBatch(textBudget)) && doc.Revision == revision && doc.Get(100000) == null);
        var stale = new ClilocCsvPlan(doc, ClilocCsv.Encode(new[] { new ClilocCsvRow(1, "stale", 0) }, legacy), legacy);
        doc.Edit(1, "intervening"); doc.Undo();
        CsvCheck("edit_then_undo_invalidates_review", ClilocRefused(stale.Apply) && doc.Get(1).Text == "edited ~1_NAME~");
        var warning = new ClilocCsvPlan(doc, ClilocCsv.Encode(new[] { new ClilocCsvRow(1, "missing token", 0) }, legacy), legacy);
        CsvCheck("token_warning_reviewed", warning.Changes.Single().Warning.Length > 0);
        doc.Delete(2);
        string csv = Path.Combine(root, "subset.csv");
        ClilocCsv.Export(doc, new[] { 9, 2, 1 }, csv, legacy);
        var subset = ClilocCsv.Parse(File.ReadAllBytes(csv), legacy);
        CsvCheck("export_scope_working_text_removed_omitted", subset.Select(row => row.Id).SequenceEqual(new[] { 9, 1 }) && subset[1].Text == doc.Get(1).Text && subset[1].Flag == 243);
        CsvCheck("document_install_destination_refused", ClilocRefused(() => ClilocCsv.Export(doc, new[] { 1 }, Path.Combine(install, "forbidden.csv"), legacy))
            && !File.Exists(Path.Combine(install, "forbidden.csv")));
        string hardlink = Path.Combine(root, "source-alias.csv");
        var start = new ProcessStartInfo(EditorData.Setting("PYTHON", "python")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-c", "import os,sys; os.link(sys.argv[1],sys.argv[2])", source, hardlink }) start.ArgumentList.Add(argument);
        using (var child = Process.Start(start))
        {
            bool ready = child.WaitForExit(10_000) && child.ExitCode == 0; CsvCheck("hardlink_fixture_ready", ready);
            if (ready)
            {
                ClilocCsv.Export(doc, new[] { 1 }, hardlink, legacy);
                CsvCheck("hardlink_export_preserves_install_backing", File.ReadAllBytes(source).AsSpan().SequenceEqual(original)
                    && ClilocCsv.Parse(File.ReadAllBytes(hardlink), legacy).Single().Id == 1);
            }
        }
        var restore = new ClilocCsvPlan(doc, Bytes("2;restored;Original"), legacy); restore.Apply();
        CsvCheck("removed_id_restores_source_flag", doc.Get(2).Flag == 130 && doc.Get(2).Text == "restored");
        doc.Undo(); CsvCheck("undo_restoration_retains_tombstone", doc.Get(2) == null);
        var empty = new ClilocCsvPlan(doc, Bytes("3;;Original"), legacy); empty.Apply();
        CsvCheck("blank_text_is_not_deletion", doc.Get(3)?.Text == ""); doc.Undo();
        var sourceBound = new ClilocCsvPlan(doc, Bytes("3;changed;Original"), legacy);
        original[0] ^= 1; File.WriteAllBytes(source, original);
        CsvCheck("source_drift_refuses_import_export", ClilocRefused(sourceBound.Apply) && ClilocRefused(() => ClilocCsv.Export(doc, new[] { 1 }, csv, legacy)) && doc.Get(3).Text == "keep");
        CheckClilocCsvUi();
    }
    private void CheckInstalledClilocCsv(ClilocDocument document, string root)
    {
        string path = Path.Combine(root, "installed-" + document.Language + ".csv");
        ClilocCsv.Export(document, document.Ids, path, ClilocCsvFormat.QuotedUtf8);
        var rows = ClilocCsv.Parse(ClilocCsv.ReadFile(path), ClilocCsvFormat.QuotedUtf8);
        CsvCheck("installed_" + document.Language + "_whole_language_roundtrip", rows.Count == document.Ids.Count()
            && rows.All(row => document.Get(row.Id) == new ClilocDocument.Entry(row.Flag, row.Text)));
        var plan = new ClilocCsvPlan(document, ClilocCsv.ReadFile(path), ClilocCsvFormat.QuotedUtf8);
        long revision = document.Revision; plan.Apply();
        CsvCheck("installed_" + document.Language + "_unchanged_import_no_mutation", plan.Changes.Count == 0 && document.Revision == revision && !document.IsDirty);
    }
    private void CheckClilocCsvUi()
    {
        var panel = _assets.Panel<ClilocPanel>(); _assets.ShowPanel(panel); panel.ChooseLanguage("enu"); panel.Search("500207");
        var doc = panel.WorkingDocument; var original = doc.Get(500207);
        string csv = Path.Combine(_out, "review-cliloc" + Suffix + ".csv");
        File.WriteAllBytes(csv, ClilocCsv.Encode(new[] { new ClilocCsvRow(500207, "CSV reviewed backpack", 1), new ClilocCsvRow(9999901, "new CSV string", 1) }, ClilocCsvFormat.Fiddler));
        var review = panel.OpenImportReview(csv, ClilocCsvFormat.Fiddler);
        CsvCheck("review_ui_has_all_changes_and_apply", review.Rows.GetRoot().GetChildCount() == 2 && !review.GetOkButton().Disabled && doc.Get(500207) == original);
        review.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        CsvCheck("review_confirmation_imports_working_copy", doc.Get(500207).Text == "CSV reviewed backpack" && doc.Get(9999901)?.Flag == 0);
        _inspector.Strings.Undo();
        CsvCheck("inspector_undo_restores_entire_import", doc.Get(500207) == original && doc.Get(9999901) == null);
        review = panel.OpenImportReview(csv, ClilocCsvFormat.Fiddler); doc.Edit(500207, "new draft during review");
        review.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        CsvCheck("stale_ui_review_keeps_error_and_draft", review.Status.Visible && review.Status.Text.Contains("changed after review") && doc.Get(500207).Text == "new draft during review" && doc.Get(9999901) == null);
        review.Free(); doc.Undo(); panel.OnAssetsChanged();
        doc.Edit(500207, "scoped CSV output 日本語"); panel.Search("scoped CSV output");
        var chooser = ClilocCsvDialog.Choose(panel, false); chooser.ScopeOption.Selected = 1;
        chooser.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        var picker = chooser.GetChildren().OfType<EditorFileDialog>().Last();
        CsvCheck("native_save_picker_owned_by_chooser", picker.FileMode == EditorFileDialog.FileModeEnum.SaveFile && picker.Filters.Single().Contains("*.csv") && picker.GetParent() == chooser);
        string output = Path.Combine(_out, "ui-export-cliloc" + Suffix + ".csv"); picker.EmitSignal(EditorFileDialog.SignalName.FileSelected, output);
        CsvCheck("browser_export_ui_uses_working_text", ClilocCsv.Parse(File.ReadAllBytes(output), ClilocCsvFormat.Fiddler).Single() == new ClilocCsvRow(500207, "scoped CSV output 日本語", original.Flag) && doc.IsDirty);
        CsvCheck("independent_python_csv_reader", IndependentSoundCsv(output, "utf-8", new[] { new[] { "Number", "Text", "Flag" },
            new[] { "500207", "scoped CSV output 日本語", original.Flag switch { 0 => "Original", 1 => "Custom", 2 => "Modified", 3 => "Custom, Modified", _ => original.Flag.ToString() } } }));
        chooser = ClilocCsvDialog.Choose(panel, false); chooser.ScopeOption.Selected = 2; chooser.FormatOption.Selected = 1;
        chooser.EmitSignal(ConfirmationDialog.SignalName.Confirmed); picker = chooser.GetChildren().OfType<EditorFileDialog>().Last();
        output = Path.Combine(_out, "ui-selected-cliloc" + Suffix + ".csv"); picker.EmitSignal(EditorFileDialog.SignalName.FileSelected, output);
        CsvCheck("selected_export_ui", ClilocCsv.Parse(File.ReadAllBytes(output), ClilocCsvFormat.QuotedUtf8).Single().Id == 500207);
        chooser = ClilocCsvDialog.Choose(panel, true); chooser.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        picker = chooser.GetChildren().OfType<EditorFileDialog>().Last();
        CsvCheck("native_import_picker", picker.FileMode == EditorFileDialog.FileModeEnum.OpenFile && picker.GetParent() == chooser);
        picker.EmitSignal(EditorFileDialog.SignalName.FileSelected, csv);
        review = panel.GetChildren().OfType<ClilocCsvDialog>().Last(dialog => dialog.Plan != null && dialog.Visible);
        CsvCheck("picker_opens_captured_review_without_applying", !chooser.Visible && review.Plan.Changes.Count == 2 && doc.Get(9999901) == null);
        review.Hide(); review.EmitSignal(ConfirmationDialog.SignalName.Canceled);
        doc.Undo(); panel.Search("500207");
        CsvCheck("toolbar_icons_have_names_and_tooltips", panel.ImportCsvButton.Icon != null && panel.ImportCsvButton.Text == "" && panel.ImportCsvButton.AccessibilityName.Length > 0
            && panel.ExportCsvButton.Icon != null && panel.ExportCsvButton.TooltipText.Length > 0);
    }
}
#endif
