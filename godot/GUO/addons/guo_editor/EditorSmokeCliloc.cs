#if TOOLS
namespace GUO.Editor;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

public partial class EditorSmoke
{
    private void ClilocCheck(string name, bool ok)
    { _assetReport["cliloc_" + name] = ok; if (!ok) AssetFail("Cliloc: " + name); }

    private static bool ClilocRefused(Action action)
    { try { action(); return false; } catch (Exception e) when (e is IOException or ArgumentException) { return true; } }

    private static byte[] ClilocFixture(params (int Id, byte Flag, string Text)[] rows)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 1, 2, 3, 4, 5, 6 });
        foreach (var row in rows)
        { byte[] text = Encoding.UTF8.GetBytes(row.Text); writer.Write(row.Id); writer.Write(row.Flag); writer.Write((ushort)text.Length); writer.Write(text); }
        return stream.ToArray();
    }

    private void CheckClilocDocuments()
    {
        CheckClilocCsv();
        string root = Path.Combine(_out, "cliloc-fixture" + Suffix + "-" + Guid.NewGuid().ToString("N")), install = Path.Combine(root, "install"), project = Path.Combine(root, "project");
        Directory.CreateDirectory(install); string source = Path.Combine(install, "Cliloc.enu");
        byte[] bytes = ClilocFixture((2001, 0xF3, "original ~1_NAME~ #100"), (2002, 7, "older"), (2002, 0x82, "é 日本語"));
        File.WriteAllBytes(source, bytes);
        ClilocDocument Open() => ClilocDocument.Open(project, install, "enu", source);
        var doc = Open();
        ClilocCheck("utf8_header_duplicate_last_wins", doc.SourceRecords == 3 && doc.DuplicateIds == 1 && doc.Get(2002).Text == "é 日本語"
            && doc.Get(2002).Flag == 0x82 && doc.Header.AsSpan().SequenceEqual(bytes.AsSpan(0, 6)));
        byte[] header = doc.Header; header[0] = 0;
        ClilocCheck("header_is_immutable", doc.Header[0] == 1);
        doc.Edit(2001, "changed ~1_NAME~ #100"); doc.Edit(3_200_001, "new"); doc.Delete(2002);
        ClilocCheck("edit_add_delete_preserve_ids_flags", doc.Get(2001).Flag == 0xF3 && doc.Get(3_200_001).Flag == 0 && doc.Get(2002) == null
            && doc.ReviewIds.Contains(2002) && !doc.Ids.Contains(2002) && doc.ChangeCount == 3);
        doc.Undo(); ClilocCheck("undo_removal", doc.Get(2002).Text == "é 日本語"); doc.Redo();
        ClilocCheck("redo_removal", doc.Get(2002) == null); doc.Revert(2002);
        ClilocCheck("revert_flag_and_text", doc.Get(2002).Flag == 0x82 && !doc.Changed(2002));
        ClilocCheck("placeholder_warning", doc.TokenWarning(2001, "#100 changed ~1_NAME~") == "" && doc.TokenWarning(2001, "changed ~2_NAME~") != "");
        ClilocCheck("invalid_text_id_refused", ClilocRefused(() => doc.Edit(-1, "bad")) && ClilocRefused(() => doc.Edit(2001, "x\0y"))
            && ClilocRefused(() => doc.Edit(2001, new string('é', 16384))));
        doc.Save(); var reopened = Open();
        ClilocCheck("save_reload_source_unchanged", !doc.IsDirty && !reopened.IsDirty && reopened.Get(2001) == doc.Get(2001)
            && reopened.Get(3_200_001).Text == "new" && File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes));
        doc.Undo(); ClilocCheck("undo_after_save_is_dirty", doc.IsDirty); doc.Redo();
        JsonObject snapshot = doc.Snapshot(), wrongFlag = (JsonObject)snapshot.DeepClone(), duplicate = (JsonObject)snapshot.DeepClone();
        wrongFlag["changes"][0]["flag"] = 0; duplicate["changes"].AsArray().Add(duplicate["changes"][0].DeepClone());
        ClilocCheck("tampered_flags_duplicates_atomic_refusal", ClilocRefused(() => reopened.Restore(wrongFlag, false))
            && ClilocRefused(() => reopened.Restore(duplicate, false)) && reopened.Get(2001) == doc.Get(2001) && !reopened.IsDirty);
        string german = Path.Combine(install, "Cliloc.deu"); File.WriteAllBytes(german, ClilocFixture((2001, 3, "Deutsch")));
        var de = ClilocDocument.Open(project, install, "deu", german); de.Edit(2001, "Entwurf"); de.Save();
        ClilocCheck("languages_isolated", de.DocumentPath != reopened.DocumentPath && Open().Get(2001).Text.StartsWith("changed"));
        CheckClilocEditor(Open(), source, project, install);
        byte[] malformed = ClilocFixture((1, 0, "x")); malformed[^1] = 0xFF; string bad = Path.Combine(install, "Cliloc.bad"); File.WriteAllBytes(bad, malformed);
        ClilocCheck("invalid_utf8_refused", ClilocRefused(() => ClilocDocument.Open(project, install, "bad", bad)));
        File.WriteAllBytes(bad, bytes[..^1]);
        ClilocCheck("truncated_record_refused", ClilocRefused(() => ClilocDocument.Open(project, install, "bad", bad)));
        byte[] compressed = new byte[1029]; compressed[3] = 0x8e; File.WriteAllBytes(bad, compressed);
        ClilocCheck("bounded_bwt_refusal", ClilocRefused(() => ClilocDocument.Open(project, install, "bad", bad)));
        bytes[0] ^= 1; File.WriteAllBytes(source, bytes);
        ClilocCheck("source_change_refuses_save_and_reopen", ClilocRefused(reopened.Save) && ClilocRefused(() => Open()));
        ClilocCheck("install_and_escape_refused", ClilocRefused(() => EditorPaths.ProjectFile(install, install, "metadata", "cliloc.json"))
            && ClilocRefused(() => EditorPaths.ProjectFile(project, install, "..", "escaped.json")));
        CheckEditorOutputAliases(root, install);
        foreach (string language in new[] { "enu", "jpn" })
        {
            string actual = Directory.GetFiles(_data.ClientData, "Cliloc.*").FirstOrDefault(path => Path.GetExtension(path).Equals("." + language, StringComparison.OrdinalIgnoreCase));
            if (actual == null) { ClilocCheck("installed_" + language, false); continue; }
            string Hash() { using var input = File.OpenRead(actual); return Convert.ToHexString(SHA256.HashData(input)); }
            string before = Hash(); DateTime stamp = File.GetLastWriteTimeUtc(actual);
            var installed = ClilocDocument.Open(Path.Combine(root, "installed-" + language), _data.ClientData, language, actual);
            ClilocCheck("installed_" + language, installed.Compressed && installed.SourceRecords > 1000 && installed.Get(500207) != null
                && Hash() == before && File.GetLastWriteTimeUtc(actual) == stamp);
            _assetReport["cliloc_" + language + "_records"] = installed.SourceRecords;
            CheckInstalledClilocCsv(installed, root);
        }
        var panel = _assets.Panel<ClilocPanel>(); _assets.ShowPanel(panel); panel.ChooseLanguage("enu"); panel.Search("500207");
        ClilocCheck("panel_inspector_binding", _inspector.Current?.Strings != null && _inspector.Strings.Visible
            && _inspector.Strings.Id == 500207 && _inspector.Strings.Footer.Visible && _inspector.Current.Image == null);
        panel.AddButton.EmitSignal(Button.SignalName.Pressed);
        var add = panel.GetChildren().OfType<ConfirmationDialog>().Single(dialog => dialog.Title == "Add localized string");
        GodotUi.Walk(add).OfType<LineEdit>().Single().Text = "9999999";
        add.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        ClilocCheck("panel_add_selects_new_id", panel.Selected == 9_999_999 && panel.WorkingDocument.Get(9_999_999)?.Flag == 0);
        ClilocDocument current = panel.WorkingDocument;
        current.Revert(9_999_999); panel.Search("500207");
        _inspector.Strings.TextInput.Text = "temporary smoke draft"; _inspector.Strings.TextInput.EmitSignal(TextEdit.SignalName.TextChanged);
        _inspector.Strings.CommitPending(); _inspector.Strings.StashDrafts();
        panel.ReloadButton.EmitSignal(Button.SignalName.Pressed);
        var reopen = panel.GetChildren().OfType<ConfirmationDialog>().Last(); reopen.EmitSignal(ConfirmationDialog.SignalName.Canceled);
        ClilocCheck("reopen_cancel_retains_document", panel.WorkingDocument == current && current.Get(500207).Text == "temporary smoke draft");
        // Canceled windows are queued for deletion, so inspect the latest one.
        panel.ReloadButton.EmitSignal(Button.SignalName.Pressed);
        reopen = panel.GetChildren().OfType<ConfirmationDialog>().Last(); reopen.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        ClilocCheck("confirmed_reopen_reloads_saved_copy", panel.WorkingDocument != current && !panel.WorkingDocument.IsDirty
            && panel.WorkingDocument.Get(500207).Text != "temporary smoke draft");
        CheckCustomClilocSources();
        CheckClilocTileSync();
    }

    private void CheckClilocEditor(ClilocDocument document, string source, string project, string install)
    {
        var settings = EditorInterface.Singleton.GetEditorSettings();
        Variant previous = settings.GetProjectMetadata("guo_cliloc", "drafts", "[]");
        settings.SetProjectMetadata("guo_cliloc", "drafts", "[]");
        var editor = new ClilocEditor(); AddChild(editor);
        ClilocEditor recovered = null;
        try
        {
            editor.Bind(document, 2001, null); editor.TextInput.Text = new string('x', 32768); editor.TextInput.EmitSignal(TextEdit.SignalName.TextChanged);
            editor.Bind(document, 2002, null);
            ClilocCheck("other_invalid_record_blocks_language_save", editor.SaveButton.Disabled && !editor.Save());
            ClilocCheck("invalid_draft_blocks_csv_exchange", !editor.PrepareExchange(document));
            editor.Bind(document, 2001, null);
            ClilocCheck("invalid_draft_retained", editor.TextInput.Text.Length == 32768);
            editor.Undo(); ClilocCheck("undo_discards_invalid_draft", editor.TextInput.Text == document.Get(2001).Text);
            editor.TextInput.Text = "UI working copy"; editor.TextInput.EmitSignal(TextEdit.SignalName.TextChanged);
            editor.Bind(document, 2002, null);
            ClilocCheck("save_includes_other_record", editor.Save() && ClilocDocument.Open(project, install, "enu", source).Get(2001).Text == "UI working copy");
            editor.TextInput.Text = "recovered 日本語"; editor.TextInput.EmitSignal(TextEdit.SignalName.TextChanged); editor.StashDrafts();
            recovered = new ClilocEditor(); AddChild(recovered);
            recovered.Bind(ClilocDocument.Open(project, install, "enu", source), 2002, null);
            ClilocCheck("raw_draft_recovery", recovered.TextInput.Text == "recovered 日本語" && !recovered.SaveButton.Disabled);
            recovered.Discard(recovered.Document);
            ClilocCheck("confirmed_reopen_discards_recovery", !settings.GetProjectMetadata("guo_cliloc", "drafts", "[]").AsString().Contains("recovered"));
            recovered.Free(); recovered = null;
            var corrupt = new JsonArray(new JsonObject { ["key"] = document.Project + "|enu|" + document.SourceHash,
                ["snapshot"] = document.Snapshot(), ["raw"] = new JsonArray(new JsonObject { ["id"] = 2002, ["text"] = "first" }, new JsonObject { ["id"] = 2002, ["text"] = "duplicate" }) });
            settings.SetProjectMetadata("guo_cliloc", "drafts", corrupt.ToJsonString());
            var intact = ClilocDocument.Open(project, install, "enu", source);
            recovered = new ClilocEditor(); AddChild(recovered); recovered.Bind(intact, 2002, null);
            recovered.StashDrafts(); recovered.StashDrafts();
            ClilocCheck("malformed_recovery_atomic_and_not_duplicated", !intact.IsDirty && !recovered.TextInput.Editable
                && recovered.TextInput.Text == intact.Get(2002).Text && JsonNode.Parse(settings.GetProjectMetadata("guo_cliloc", "drafts", "[]").AsString()).AsArray().Count == 1);
            ClilocCheck("failed_recovery_blocks_csv_exchange", !recovered.PrepareExchange(intact));
            recovered.Discard(intact);
        }
        finally { recovered?.Free(); editor.Free(); settings.SetProjectMetadata("guo_cliloc", "drafts", previous); }
    }

    private void CheckEditorOutputAliases(string root, string install)
    {
        string target = Path.Combine(install, "source.png"), hardlink = Path.Combine(root, "hardlink.png"), symlink = Path.Combine(root, "install-alias");
        byte[] before = { 1, 2, 3, 4 }; File.WriteAllBytes(target, before);
        var start = new ProcessStartInfo(EditorData.Setting("PYTHON", "python")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-c", "import os,sys; os.link(sys.argv[1],sys.argv[2]); os.symlink(sys.argv[3],sys.argv[4],target_is_directory=True)", target, hardlink, install, symlink }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start);
        bool ready = child.WaitForExit(10_000) && child.ExitCode == 0;
        ClilocCheck("output_alias_fixture_ready", ready); if (!ready) return;
        ClilocCheck("directory_alias_refused", ClilocRefused(() => EditorPaths.ProjectFile(symlink, install, "metadata", "test.json")));
        AssetActions.ExportPng(FixtureStaticImage(), hardlink);
        ClilocCheck("png_hardlink_export_preserves_backing", File.ReadAllBytes(target).AsSpan().SequenceEqual(before)
            && File.ReadAllBytes(hardlink).Length > before.Length);
    }
}
#endif
