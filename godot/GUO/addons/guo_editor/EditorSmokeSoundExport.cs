#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;

public partial class EditorSmoke
{
    private void SoundExportCheck(string name, bool ok)
    { _assetReport["sound_export_" + name] = ok; if (!ok) AssetFail("Sound export: " + name); }
    private bool IndependentSoundCsv(string path, string encoding, string[][] expected)
    {
        var start = new ProcessStartInfo(EditorData.Setting("PYTHON", "python")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-c", "import csv,json,sys; f=open(sys.argv[1],encoding=sys.argv[2],newline=''); rows=list(csv.reader(f,delimiter=';')); f.close(); assert rows==json.loads(sys.argv[3]),repr(rows)", path, encoding, JsonSerializer.Serialize(expected) }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start);
        if (!child.WaitForExit(10_000)) { child.Kill(entireProcessTree: true); child.WaitForExit(); return false; }
        if (child.ExitCode != 0) _assetReport["sound_export_csv_oracle_error"] = child.StandardError.ReadToEnd();
        return child.ExitCode == 0;
    }
    private void CheckSoundExports()
    {
        string root = Path.Combine(_out, "sound-export" + Suffix + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        byte[] first = { 0, 128, 255, 127, 0, 0, 255, 255 }, second = new byte[4410];
        var states = new Dictionary<int, SoundExportRead> { [0] = new("tone; \"quoted\"\nline", first, "first-source"), [1] = new("日本語", second, "second-source") };
        SoundExportPlan Plan() => new(new[] { 0, 1 }, id => states[id]); var plan = Plan();
        string csv = Path.Combine(root, "list.csv"); File.WriteAllBytes(csv, plan.Csv(false, 1));
        SoundExportCheck("utf8_csv_independent_exact", IndependentSoundCsv(csv, "utf-8", new[] { new[] { "ID", "Name", "Length" }, new[] { "0x001", states[0].Name, "0.00" }, new[] { "0x002", "日本語", "0.10" } }));
        SoundExportCheck("legacy_unrepresentable_refused", SoundDocumentRefused(() => plan.Csv(true, 0)));
        states[0] = states[0] with { Name = "café.wav" }; states[1] = states[1] with { Name = "second.wav" }; plan = Plan();
        CultureInfo prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); string legacy = Path.Combine(root, "legacy.csv"); File.WriteAllBytes(legacy, plan.Csv(true, 0));
            SoundExportCheck("fiddler_cp1252_locale_schema", IndependentSoundCsv(legacy, "cp1252", new[] { new[] { "ID", "Name", "Length" }, new[] { "0x000", "café.wav", "0,00" }, new[] { "0x001", "second.wav", "0,10" } }));
        }
        finally { CultureInfo.CurrentCulture = prior; }
        SoundExportCheck("filename_sanitization_and_devices", SoundExportPlan.FileName(1, "../CON?.WAV", false) == ".._CON_.wav"
            && SoundExportPlan.FileName(1, "NUL.wav", false) == "_NUL.wav" && SoundExportPlan.FileName(1, "...", true) == "0x0001 sound.wav"
            && SoundExportPlan.FileName(1, "COM¹.wav", false) == "_COM¹.wav" && SoundExportPlan.FileName(1, "CONOUT$.wav", false) == "_CONOUT$.wav");
        states[0] = states[0] with { Name = "same.wav" }; states[1] = states[1] with { Name = "SAME.WAV" }; plan = Plan();
        SoundExportCheck("case_insensitive_filename_collisions", SoundDocumentRefused(() => plan.FileNames(false)) && plan.FileNames(true).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2);
        SoundExportCheck("empty_duplicate_ids_refused", SoundDocumentRefused(() => new SoundExportPlan(Array.Empty<int>(), id => states[id]))
            && SoundDocumentRefused(() => new SoundExportPlan(new[] { 0, 0 }, id => states[id])) && SoundDocumentRefused(() => plan.Csv(false, 2)));
        string batch = Path.Combine(root, "batch");
        using (var job = new SoundExportJob(plan, SoundExportFormat.WaveFolder, batch, true, 0))
        { job.Step(); job.Cancel(); SoundExportCheck("cancel_removes_private_batch", !Directory.Exists(batch) && Directory.GetDirectories(root, ".guo-sounds-*").Length == 0 && !job.Succeeded); }
        File.WriteAllText(csv, "retain existing CSV");
        using (var job = new SoundExportJob(plan, SoundExportFormat.Utf8Csv, csv, true, 0))
        { job.Step(); job.Cancel(); SoundExportCheck("csv_cancel_preserves_existing", File.ReadAllText(csv) == "retain existing CSV"); }
        using (var job = new SoundExportJob(plan, SoundExportFormat.WaveFolder, batch, true, 0))
        { while (!job.Finished) job.Step(); SoundExportCheck("complete_batch_published", job.Succeeded && Directory.GetFiles(batch).Length == 3 && Directory.GetDirectories(root, ".guo-sounds-*").Length == 0); }
        File.WriteAllBytes(Path.Combine(root, "first.pcm"), first); File.WriteAllBytes(Path.Combine(root, "second.pcm"), second);
        string[] names = plan.FileNames(true);
        SoundExportCheck("batch_wavs_independent_pcm_exact", IndependentWave(Path.Combine(batch, names[0]), Path.Combine(root, "first.pcm")) && IndependentWave(Path.Combine(batch, names[1]), Path.Combine(root, "second.pcm")));
        SoundExportCheck("batch_manifest_independent", IndependentSoundCsv(Path.Combine(batch, "SoundList.csv"), "utf-8", new[] { new[] { "ID", "Name", "Length" }, new[] { "0x000", "same.wav", "0.00" }, new[] { "0x001", "SAME.WAV", "0.10" } }));
        SoundExportCheck("existing_folder_and_collision_refused", SoundDocumentRefused(() => new SoundExportJob(plan, SoundExportFormat.WaveFolder, batch, true, 0))
            && SoundDocumentRefused(() => new SoundExportJob(plan, SoundExportFormat.WaveFolder, Path.Combine(root, "collision"), false, 0)) && Directory.GetFiles(batch).Length == 3);
        string failedBatch = Path.Combine(root, "changed-batch");
        using (var job = new SoundExportJob(plan, SoundExportFormat.WaveFolder, failedBatch, true, 0))
        {
            job.Step(); states[1] = states[1] with { Name = "changed" }; job.Step();
            SoundExportCheck("mid_batch_change_no_partial_publish", job.Finished && !job.Succeeded && job.Error.Contains("changed after review")
                && !Directory.Exists(failedBatch) && Directory.GetDirectories(root, ".guo-sounds-*").Length == 0);
        }
        states[1] = states[1] with { Name = "SAME.WAV" }; plan = Plan(); states[0] = states[0] with { SourceBinding = "changed" };
        using (var job = new SoundExportJob(plan, SoundExportFormat.Utf8Csv, csv, true, 0))
        { job.Step(); SoundExportCheck("changed_binding_preserves_csv", job.Finished && !job.Succeeded && File.ReadAllText(csv) == "retain existing CSV"); }
        plan = Plan(); states[0] = states[0] with { Pcm = new byte[] { 0, 0 } };
        SoundExportCheck("changed_pcm_refused", SoundDocumentRefused(() => plan.ReadVerified(0)));
        plan = Plan();
        SoundExportCheck("install_and_unknown_format_refused", SoundDocumentRefused(() => new SoundExportJob(plan, SoundExportFormat.Utf8Csv, Path.Combine(_data.ClientData, "guo-sound-list-refusal.csv"), true, 0))
            && SoundDocumentRefused(() => new SoundExportJob(plan, (SoundExportFormat)99, csv, true, 0)) && !File.Exists(Path.Combine(_data.ClientData, "guo-sound-list-refusal.csv")));
        CheckSoundExportPanel(root);
        CheckSoundAudition();
    }
    private void CheckSoundExportPanel(string root)
    {
        var panel = _assets.Panel<SoundPanel>(); _assets.ShowPanel(panel); panel.SelectKind(false); panel.Search(""); panel.SortSounds(0);
        int total = panel.ResultAssetIds().Length;
        int[] ids = panel.ResultAssetIds().Take(2).ToArray(); if (ids.Length != 2) throw new IOException("Sound export smoke needs two active effects.");
        string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        var stamps = Directory.GetFiles(_data.ClientData).Where(path => Path.GetFileName(path).StartsWith("sound", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), Hash(path)));
        var a = panel.WorkingDocument(ids[0]); var b = panel.WorkingDocument(ids[1]);
        a.DraftName = "zzz export fixture"; a.CommitDraft(); b.DraftName = "aaa export fixture"; b.CommitDraft();
        a.ImportWave(SoundWave.Encode(new byte[4410])); b.ImportWave(SoundWave.Encode(new byte[8820]));
        panel.SortSounds(1); int[] ordered = panel.ResultAssetIds();
        SoundExportCheck("name_sort", Array.IndexOf(ordered, ids[1]) < Array.IndexOf(ordered, ids[0]));
        SoundExportCheck("sorted_id_jump", panel.Search($"0x{ids[0]:X4}") == ids[0]);
        panel.SortSounds(2); ordered = panel.ResultAssetIds(); SoundExportCheck("duration_sort", Array.IndexOf(ordered, ids[0]) < Array.IndexOf(ordered, ids[1]));
        panel.SortSounds(3); ordered = panel.ResultAssetIds(); SoundExportCheck("reverse_duration_sort", Array.IndexOf(ordered, ids[1]) < Array.IndexOf(ordered, ids[0]));
        var dialog = panel.OpenExportDialog(ids, true);
        SoundExportCheck("selected_review_working_pcm", dialog.Plan.Rows.Count == 2 && dialog.Plan.Rows[0].Samples == 2205 && dialog.Plan.Rows[1].Samples == 4410
            && dialog.Rows.GetRoot().GetChildCount() == 2 && !panel.ExportButton.Disabled && panel.ExportButton.Icon != null && panel.ExportButton.Text.Length == 0);
        var picker = dialog.Browse(); SoundExportCheck("native_batch_picker", picker.FileMode == EditorFileDialog.FileModeEnum.OpenDir && picker.GetParent() == dialog);
        picker.EmitSignal(EditorFileDialog.SignalName.Canceled); dialog.EmitSignal(ConfirmationDialog.SignalName.Canceled);
        SoundExportCheck("review_cancel_no_output", !Directory.Exists(Path.Combine(root, "ui-batch")));
        dialog = panel.OpenExportDialog(ids, true); dialog.Destination.Text = Path.Combine(root, "ui-batch"); dialog.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        for (int tick = 0; tick < 100 && dialog.IsBusy; tick++) dialog._Process(0);
        SoundExportCheck("ui_batch_export", dialog.ExportSucceeded && File.Exists(Path.Combine(root, "ui-batch", "SoundList.csv"))); dialog.Free();
        panel.Search("zzz export fixture"); dialog = panel.OpenExportDialog(ids);
        SoundExportCheck("all_scope_includes_unfiltered_library", dialog.Plan.Rows.Count == total && dialog.Plan.Rows.Any(row => row.Id == ids[0]) && dialog.Plan.Rows.Any(row => row.Id == ids[1]));
        dialog.Choose(1, SoundExportFormat.Utf8Csv);
        SoundExportCheck("browser_scope_matches_search", dialog.Plan.Rows.Count == 1 && dialog.Plan.Rows[0].Id == ids[0]);
        picker = dialog.Browse(); SoundExportCheck("native_csv_picker", picker.FileMode == EditorFileDialog.FileModeEnum.SaveFile && picker.Filters.Single().Contains("*.csv"));
        string output = Path.Combine(root, "ui-list.csv"); picker.EmitSignal(EditorFileDialog.SignalName.FileSelected, output);
        SoundExportCheck("picker_selection_does_not_export", !File.Exists(output)); dialog.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        for (int tick = 0; tick < 100 && dialog.IsBusy; tick++) dialog._Process(0);
        SoundExportCheck("ui_csv_working_name_and_duration", dialog.ExportSucceeded && IndependentSoundCsv(output, "utf-8", new[] { new[] { "ID", "Name", "Length" }, new[] { $"0x{ids[0]:X3}", "zzz export fixture", "0.10" } })); dialog.Free();
        a.DraftName = "same.wav"; b.DraftName = "SAME.WAV"; a.CommitDraft(); b.CommitDraft();
        dialog = panel.OpenExportDialog(ids, true); dialog.Choose(2, SoundExportFormat.WaveFolder, false); dialog.Destination.Text = Path.Combine(root, "ui-collision");
        bool blocked = dialog.GetOkButton().Disabled && dialog.Rows.GetRoot().GetChildCount() == 2 && dialog.Status.Contains("collides");
        dialog.Choose(2, SoundExportFormat.WaveFolder, true);
        SoundExportCheck("collision_review_repaired_by_prefix", blocked && !dialog.GetOkButton().Disabled); dialog.Free();
        a.DraftName = "日本語"; a.CommitDraft(); dialog = panel.OpenExportDialog(ids); dialog.Choose(2, SoundExportFormat.FiddlerCsv); dialog.Destination.Text = Path.Combine(root, "ui-unicode.csv");
        blocked = dialog.GetOkButton().Disabled && dialog.Rows.GetRoot().GetChildCount() == 2 && dialog.Status.Contains("UTF-8"); dialog.Choose(2, SoundExportFormat.Utf8Csv);
        SoundExportCheck("encoding_review_repaired_by_utf8", blocked && !dialog.GetOkButton().Disabled); dialog.Free();
        panel.SelectKind(true); SoundExportCheck("music_excluded_from_batch", panel.ExportButton.Disabled && SoundDocumentRefused(() => panel.OpenExportDialog()));
        panel.SelectKind(false); a.Revert(); b.Revert(); panel.SortSounds(0); panel.Search(ids[0].ToString()); panel.OnAssetsChanged();
        SoundExportCheck("native_sound_sources_unchanged", stamps.All(row => row.Value == (new FileInfo(row.Key).Length, File.GetLastWriteTimeUtc(row.Key), Hash(row.Key))));
    }
}
#endif
