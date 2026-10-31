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
    private void CustomClilocCheck(string name, bool ok) => ClilocCheck("custom_" + name, ok);
    private void CheckCustomClilocSources()
    {
        string root = Path.Combine(_out, "custom-cliloc" + Suffix + "-" + Guid.NewGuid().ToString("N")), install = Path.Combine(root, "install"), project = Path.Combine(root, "project");
        Directory.CreateDirectory(install);
        string a = Path.Combine(root, "Cliloc.guild-quests"), b = Path.Combine(root, "Cliloc.same-bytes"), empty = Path.Combine(root, "Cliloc.empty"), bad = Path.Combine(root, "Cliloc.bad");
        byte[] original = ClilocFixture((700001, 0xE3, "Quest ~1_NAME~"), (700002, 7, "日本語"));
        File.WriteAllBytes(a, original); File.WriteAllBytes(b, original); File.WriteAllBytes(empty, ClilocFixture()); File.WriteAllBytes(bad, new byte[] { 1, 2 });
        DateTime stamp = File.GetLastWriteTimeUtc(a);
        ClilocDocument Open(string slot, string file) => ClilocDocument.Open(project, install, slot, file);
        var first = Open("custom1", a); var sameBytes = Open("custom1", b); var second = Open("custom2", a); var installed = Open("enu", a);
        CustomClilocCheck("arbitrary_extension_exact_records_flags", first.IsCustom && first.Language == "custom1" && first.SourceRecords == 2
            && first.Get(700001).Flag == 0xE3 && first.Get(700002).Text == "日本語" && first.DisplayName.Contains("Cliloc.guild-quests"));
        CustomClilocCheck("same_bytes_distinct_paths_slots_and_installed_isolated", new[] { first.DocumentPath, sameBytes.DocumentPath, second.DocumentPath, installed.DocumentPath }.Distinct().Count() == 4
            && first.SourceHash == sameBytes.SourceHash && first.SourceIdentity != sameBytes.SourceIdentity && first.RecoveryIdentity != sameBytes.RecoveryIdentity);
        first.Edit(700001, "saved custom ~1_NAME~"); first.Save();
        CustomClilocCheck("project_save_reopen_source_readonly", Open("custom1", a).Get(700001).Text == "saved custom ~1_NAME~"
            && Open("custom1", b).Get(700001).Text == "Quest ~1_NAME~" && Open("custom2", a).Get(700001).Text == "Quest ~1_NAME~"
            && File.ReadAllBytes(a).AsSpan().SequenceEqual(original) && File.GetLastWriteTimeUtc(a) == stamp);
        CustomClilocCheck("source_identity_in_snapshot_no_machine_path", (string)first.Snapshot()["source_identity"] == first.SourceIdentity
            && !first.Snapshot().ToJsonString().Contains(a, StringComparison.OrdinalIgnoreCase));
        var wrong = (JsonObject)first.Snapshot().DeepClone(); wrong["source_identity"] = sameBytes.SourceIdentity;
        CustomClilocCheck("foreign_identical_source_snapshot_refused", ClilocRefused(() => sameBytes.Restore(first.Snapshot(), false))
            && ClilocRefused(() => first.Restore(wrong, false)) && first.Get(700001).Text == "saved custom ~1_NAME~" && !sameBytes.IsDirty);
        CustomClilocCheck("invalid_slot_malformed_file_refused", ClilocRefused(() => Open("custom3", a)) && ClilocRefused(() => Open("custom1", bad)));
        CustomClilocCheck("csv_cannot_replace_external_source", ClilocRefused(() => ClilocCsv.Export(first, first.Ids, a, ClilocCsvFormat.QuotedUtf8))
            && File.ReadAllBytes(a).AsSpan().SequenceEqual(original));
        string alias = Path.Combine(root, "source-alias.csv"); File.CreateSymbolicLink(alias, a);
        CustomClilocCheck("csv_source_symlink_refused", ClilocRefused(() => ClilocCsv.Export(first, first.Ids, alias, ClilocCsvFormat.QuotedUtf8))
            && File.ReadAllBytes(a).AsSpan().SequenceEqual(original));
        CustomClilocCheck("other_open_source_protected_from_csv_and_export_helpers", ClilocRefused(() => ClilocCsv.Export(first, first.Ids, b, ClilocCsvFormat.QuotedUtf8))
            && ClilocRefused(() => AssetActions.EnsureExportDestination(b)) && File.ReadAllBytes(b).AsSpan().SequenceEqual(original));
        string csv = Path.Combine(root, "custom.csv"); ClilocCsv.Export(first, first.Ids, csv, ClilocCsvFormat.QuotedUtf8);
        var import = new ClilocCsvPlan(first, ClilocCsv.Encode(new[] { new ClilocCsvRow(700002, "CSV 日本語", 2) }, ClilocCsvFormat.QuotedUtf8), ClilocCsvFormat.QuotedUtf8);
        import.Apply(); CustomClilocCheck("custom_csv_review_undo", first.Get(700002) == new ClilocDocument.Entry(7, "CSV 日本語")); first.Undo();
        CustomClilocCheck("custom_csv_export_working_text", ClilocCsv.Parse(File.ReadAllBytes(csv), ClilocCsvFormat.QuotedUtf8).First().Text == "saved custom ~1_NAME~" && first.Get(700002).Text == "日本語");
        string oldPatch = first.DocumentPath; File.WriteAllBytes(a, ClilocFixture((700001, 3, "new source revision")));
        var revision = Open("custom1", a);
        CustomClilocCheck("source_revision_new_patch_retains_old_version", revision.SourceHash != first.SourceHash && revision.DocumentPath != oldPatch && !revision.IsDirty
            && File.Exists(oldPatch) && ClilocRefused(first.Save));
        File.WriteAllBytes(a, original);
        CustomClilocCheck("restored_source_version_recovers_its_saved_patch", Open("custom1", a).Get(700001).Text == "saved custom ~1_NAME~");
        string selfProject = Path.Combine(root, "self-project"), self = Path.Combine(selfProject, "assets", "source.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(self)); File.WriteAllBytes(self, original);
        ClilocDocument.Open(selfProject, install, "custom1", self);
        CustomClilocCheck("shared_project_guard_protects_custom_source", ClilocRefused(() => EditorPaths.ProjectFile(selfProject, install, "assets", "source.bin"))
            && File.ReadAllBytes(self).AsSpan().SequenceEqual(original));
        string actual = Directory.GetFiles(_data.ClientData, "Cliloc.*").First(path => Path.GetExtension(path).Equals(".jpn", StringComparison.OrdinalIgnoreCase));
        var bwt = ClilocDocument.Open(project, _data.ClientData, "custom2", actual);
        using (var stream = File.OpenRead(actual)) CustomClilocCheck("custom_bwt_uses_exact_file", bwt.Compressed && bwt.SourceRecords > 1000 && bwt.Get(500207) != null
            && Convert.ToHexString(SHA256.HashData(stream)).Equals(bwt.SourceHash, StringComparison.OrdinalIgnoreCase));
        CheckCustomClilocPanel(root, b, empty, bad, original);
    }
    private void CheckCustomClilocPanel(string root, string source, string empty, string bad, byte[] original)
    {
        var settings = EditorInterface.Singleton.GetEditorSettings();
        Variant prior1 = settings.GetProjectMetadata("guo_cliloc", "custom1_source", ""), prior2 = settings.GetProjectMetadata("guo_cliloc", "custom2_source", ""), drafts = settings.GetProjectMetadata("guo_cliloc", "drafts", "[]");
        var panel = _assets.Panel<ClilocPanel>(); _assets.ShowPanel(panel);
        var documents = new System.Collections.Generic.HashSet<ClilocDocument>(); ClilocEditor recovered = null;
        try
        {
            settings.SetProjectMetadata("guo_cliloc", "custom1_source", ""); settings.SetProjectMetadata("guo_cliloc", "custom2_source", "");
            panel.RestoreCustomSources(); panel.ChooseLanguage("custom1");
            CustomClilocCheck("empty_slot_clear_inspector_and_tools", panel.WorkingDocument == null && panel.AddButton.Disabled && panel.ImportCsvButton.Disabled
                && !panel.OpenCustomButton.Disabled && _inspector.Current?.Strings == null && panel.SourceStatus.Contains("no file assigned"));
            var picker = panel.OpenCustomPicker();
            CustomClilocCheck("native_source_picker_owned_filtered", picker.GetParent() == panel && picker.FileMode == EditorFileDialog.FileModeEnum.OpenFile && picker.Filters.Any(filter => filter.StartsWith("cliloc.*")));
            picker.Hide(); picker.EmitSignal(EditorFileDialog.SignalName.Canceled);
            CustomClilocCheck("picker_cancel_leaves_empty_slot", panel.WorkingDocument == null);
            picker = panel.OpenCustomPicker(); picker.Hide(); picker.EmitSignal(EditorFileDialog.SignalName.FileSelected, source);
            var a = panel.WorkingDocument; documents.Add(a);
            CustomClilocCheck("picker_opens_and_selects_valid_source_record", a.IsCustom && panel.Selected == 700001 && _inspector.Strings.Document == a
                && _inspector.Current.Origin.Contains(Path.GetFileName(source)) && settings.GetProjectMetadata("guo_cliloc", "custom1_source", "").AsString() == EditorPaths.Real(source));
            var unchanged = panel.WorkingDocument;
            CustomClilocCheck("bad_selection_atomic_no_assignment_change", ClilocRefused(() => panel.OpenCustomFile("custom1", bad)) && panel.WorkingDocument == unchanged);
            _inspector.Strings.TextInput.Text = new string('x', 32768); _inspector.Strings.TextInput.EmitSignal(TextEdit.SignalName.TextChanged);
            string other = Path.Combine(root, "Cliloc.ui-identical"); File.WriteAllBytes(other, original);
            var b = panel.OpenCustomFile("custom1", other, remember: false); documents.Add(b);
            _inspector.Strings.TextInput.Text = "B independent draft"; _inspector.Strings.TextInput.EmitSignal(TextEdit.SignalName.TextChanged);
            panel.OpenCustomFile("custom1", source, remember: false);
            CustomClilocCheck("invalid_draft_survives_source_switch", panel.WorkingDocument == a && _inspector.Strings.TextInput.Text.Length == 32768 && !_inspector.Strings.PrepareExchange(a));
            _inspector.Strings.Undo(); _inspector.Strings.TextInput.Text = "A saved draft"; _inspector.Strings.TextInput.EmitSignal(TextEdit.SignalName.TextChanged); _inspector.Strings.Save();
            panel.OpenCustomFile("custom1", other, remember: false);
            CustomClilocCheck("identical_sources_keep_independent_drafts", _inspector.Strings.TextInput.Text == "B independent draft" && a.Get(700001).Text == "A saved draft");
            _inspector.Strings.StashDrafts();
            recovered = new ClilocEditor(); AddChild(recovered);
            var reopened = ClilocDocument.Open(b.Project, b.Install, "custom1", other); recovered.Bind(reopened, 700001, null);
            CustomClilocCheck("custom_recovery_is_source_isolated", recovered.TextInput.Text == "B independent draft"); recovered.Discard(reopened); recovered.Free(); recovered = null;
            var c = panel.OpenCustomFile("custom2", source, remember: false); documents.Add(c);
            CustomClilocCheck("custom_slots_independent_in_inspector", c != a && c.DocumentPath != a.DocumentPath && c.Get(700001).Text == "Quest ~1_NAME~");
            var zero = panel.OpenCustomFile("custom1", empty, remember: false); documents.Add(zero);
            CustomClilocCheck("empty_source_can_add_or_import_without_stale_binding", zero.SourceRecords == 0 && !panel.AddButton.Disabled && !panel.ImportCsvButton.Disabled && _inspector.Current?.Strings == null);
            string csv = Path.Combine(root, "empty-source.csv"); File.WriteAllBytes(csv, ClilocCsv.Encode(new[] { new ClilocCsvRow(9, "new empty-source record", 0) }, ClilocCsvFormat.Fiddler));
            var review = panel.OpenImportReview(csv, ClilocCsvFormat.Fiddler); review.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            CustomClilocCheck("empty_source_import_selects_new_record", zero.Get(9)?.Text == "new empty-source record" && _inspector.Strings.Document == zero && _inspector.Strings.Id == 9);
            _inspector.Strings.Undo();
            File.WriteAllBytes(source, ClilocFixture((800001, 9, "changed UI source")));
            var next = panel.OpenCustomFile("custom1", source, remember: false); documents.Add(next);
            CustomClilocCheck("explicit_changed_source_open_retains_prior_document", next != a && panel.Selected == 800001 && a.Get(700001).Text == "A saved draft" && File.Exists(a.DocumentPath));
            File.WriteAllBytes(source, original); panel.OpenCustomFile("custom1", source, remember: false);
            CustomClilocCheck("return_to_original_source_restores_cached_copy", panel.WorkingDocument == a && _inspector.Strings.TextInput.Text == "A saved draft");
            string missing = Path.Combine(root, "missing", "Cliloc.none"); settings.SetProjectMetadata("guo_cliloc", "custom2_source", missing); panel.RestoreCustomSources(); panel.ChooseLanguage("custom2");
            CustomClilocCheck("missing_remembered_source_reported_no_fallback", panel.WorkingDocument == null && panel.SourceStatus.Contains("Could not open source document") && _inspector.Current?.Strings == null);
            panel.SelectKind(true); CustomClilocCheck("speech_mode_excludes_custom_authoring", panel.OpenCustomButton.Disabled && panel.ImportCsvButton.Disabled && ClilocRefused(() => panel.OpenCustomFile("custom1", source)));
            panel.SelectKind(false); panel.ChooseLanguage("enu"); panel.Search("500207");
            CustomClilocCheck("installed_language_still_separate", !panel.WorkingDocument.IsCustom && panel.OpenCustomButton.Disabled && _inspector.Strings.Document.Language == "enu");
            CustomClilocCheck("custom_icon_named_and_tooltipped", panel.OpenCustomButton.Icon != null && panel.OpenCustomButton.Text.Length == 0 && panel.OpenCustomButton.AccessibilityName.Length > 0 && panel.OpenCustomButton.TooltipText.Length > 0);
        }
        finally
        {
            recovered?.Free(); foreach (var document in documents) _inspector.Strings.Discard(document);
            settings.SetProjectMetadata("guo_cliloc", "custom1_source", prior1); settings.SetProjectMetadata("guo_cliloc", "custom2_source", prior2); settings.SetProjectMetadata("guo_cliloc", "drafts", drafts);
            panel.RestoreCustomSources(); panel.SelectKind(false); panel.ChooseLanguage("enu"); panel.Search("500207");
        }
    }
}
#endif
