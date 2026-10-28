#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using Godot;

/// <summary>Language-scoped exchange choices and protected review of a captured CSV candidate.</summary>
[Tool]
public partial class ClilocCsvDialog : ConfirmationDialog
{
    internal ClilocCsvPlan Plan { get; private set; }
    internal Tree Rows { get; private set; }
    internal Label Status { get; private set; }
    internal OptionButton FormatOption { get; private set; }
    internal OptionButton ScopeOption { get; private set; }
    internal static ClilocCsvDialog Choose(ClilocPanel panel, bool import)
    {
        ClilocDocument document = panel.WorkingDocument;
        try { panel.PrepareExchange(document); }
        catch (Exception e) { AssetActions.ReportEdit(e.Message, "Cliloc CSV", panel); return null; }
        var dialog = new ClilocCsvDialog { Title = import ? "Import Cliloc CSV" : "Export Cliloc CSV", OkButtonText = "Choose file…", DialogHideOnOk = false };
        var body = new VBoxContainer { CustomMinimumSize = new Vector2(540, 0) * EditorInterface.Singleton.GetEditorScale() }; dialog.AddChild(body);
        body.AddChild(AssetUi.Label($"Target: {document.DisplayName} · project working document"));
        body.AddChild(AssetUi.Label("Format")); var format = new OptionButton { AccessibilityName = "Cliloc CSV dialect" }; body.AddChild(format);
        dialog.FormatOption = format;
        format.AddItem("Fiddler CSV · UTF-8 · Number;Text;Flag"); format.AddItem("Quoted UTF-8 CSV · preserves multiline text and semicolons");
        OptionButton scope = null;
        if (!import)
        {
            body.AddChild(AssetUi.Label("Export scope")); scope = new OptionButton { AccessibilityName = "Cliloc CSV export scope" }; body.AddChild(scope);
            scope.AddItem("Entire working language"); scope.AddItem("Current browser results"); scope.AddItem("Selected browser rows");
            dialog.ScopeOption = scope;
        }
        body.AddChild(AssetUi.Label(import
            ? "CSV has no language identity. Check the target above. Import merges listed text only; omitted IDs stay unchanged and source flags are preserved."
            : "Exports active records and preserved flags. Removed strings are omitted. Fiddler CSV refuses text its importer would split or trim; quoted CSV is for lossless spreadsheet exchange."));
        dialog.Status = AssetUi.Label(""); dialog.Status.Visible = false; body.AddChild(dialog.Status);
        bool choosing = false;
        dialog.Confirmed += () =>
        {
            if (choosing) return;
            try
            {
                panel.PrepareExchange(document); var dialect = (ClilocCsvFormat)format.Selected;
                int[] ids = import ? Array.Empty<int>() : scope.Selected switch { 1 => panel.ResultAssetIds(), 2 => panel.SelectedAssetIds(), _ => document.Ids.ToArray() };
                if (!import && !ids.Any(id => document.Get(id) != null)) throw new IOException("Choose at least one active string to export.");
                choosing = true;
                EditorFileDialog picker = null;
                picker = AssetActions.Pick(import ? EditorFileDialog.FileModeEnum.OpenFile : EditorFileDialog.FileModeEnum.SaveFile,
                    import ? "" : $"Cliloc-{document.Language}.csv", path =>
                    {
                        choosing = false;
                        picker?.Hide();
                        try
                        {
                            panel.PrepareExchange(document);
                            if (import) { dialog.Hide(); panel.OpenImportReview(path, dialect); }
                            else ClilocCsv.Export(document, ids, path, dialect);
                            dialog.Hide(); dialog.QueueFree();
                        }
                        catch (Exception e) { dialog.Status.Text = e.Message; dialog.Status.Visible = true; dialog.PopupCenteredClamped(); }
                    }, new[] { "*.csv ; Cliloc UTF-8 CSV" }, dialog, import ? "Choose Cliloc CSV to review" : "Export working Cliloc CSV");
                picker.Canceled += () => choosing = false;
            }
            catch (Exception e) { dialog.Status.Text = e.Message; dialog.Status.Visible = true; }
        };
        dialog.Canceled += dialog.QueueFree; panel.AddChild(dialog); dialog.PopupCenteredClamped();
        return dialog;
    }
    internal static ClilocCsvDialog Review(ClilocPanel panel, ClilocDocument document, ClilocCsvPlan plan, string filename)
    {
        var dialog = new ClilocCsvDialog { Plan = plan, Title = $"Review CSV import · {document.DisplayName}",
            OkButtonText = "Import into working copy", DialogHideOnOk = false,
            Size = (Vector2I)(new Vector2(820, 0) * EditorInterface.Singleton.GetEditorScale()) };
        var body = new VBoxContainer(); dialog.AddChild(body);
        body.AddChild(AssetUi.Label($"{Path.GetFileName(filename)} · Target: {document.DisplayName}"));
        body.AddChild(AssetUi.Label($"{plan.Records:N0} records · {plan.Changes.Count:N0} changes ({plan.Changes.Count(row => row.Added):N0} added) · {plan.Unchanged:N0} unchanged"));
        int tokenWarnings = plan.Changes.Count(row => row.Warning.Length > 0);
        if (tokenWarnings > 0) body.AddChild(AssetUi.Label($"{tokenWarnings:N0} changed string(s) alter placeholder/reference tokens. Check the consuming scripts before saving."));
        body.AddChild(AssetUi.Label($"Incoming flags are advisory: {plan.FlagDifferences:N0} differ. Source flags stay unchanged; new IDs use flag 0. Omitted IDs stay unchanged."));
        dialog.Rows = new Tree { Columns = 3, HideRoot = true, ColumnTitlesVisible = true,
            CustomMinimumSize = new Vector2(0, Math.Min(160, 30 + plan.Changes.Count * 25)) * EditorInterface.Singleton.GetEditorScale(), AccessibilityName = "Cliloc CSV changes" };
        body.AddChild(dialog.Rows);
        foreach (var column in new[] { (0, "ID"), (1, "Action"), (2, "Incoming text") }) dialog.Rows.SetColumnTitle(column.Item1, column.Item2);
        float scale = EditorInterface.Singleton.GetEditorScale();
        dialog.Rows.SetColumnExpand(0, false); dialog.Rows.SetColumnCustomMinimumWidth(0, (int)(125 * scale));
        dialog.Rows.SetColumnExpand(1, false); dialog.Rows.SetColumnCustomMinimumWidth(1, (int)(80 * scale));
        TreeItem root = dialog.Rows.CreateItem();
        for (int i = 0; i < Math.Min(plan.Changes.Count, 250); i++)
        {
            var row = plan.Changes[i]; TreeItem item = dialog.Rows.CreateItem(root); item.SetMetadata(0, i);
            item.SetText(0, row.Id.ToString()); item.SetText(1, row.Added ? "Add" : "Edit");
            string line = row.After.Replace('\r', ' ').Replace('\n', ' ');
            item.SetText(2, line.Length > 160 ? line[..160] + "…" : line); item.SetTooltipText(2, row.After);
        }
        if (plan.Changes.Count > 250) body.AddChild(AssetUi.Label("First 250 changes shown; import includes every reviewed record."));
        var comparison = new HBoxContainer(); body.AddChild(comparison);
        TextEdit Field(string label)
        {
            var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; comparison.AddChild(column); column.AddChild(AssetUi.Label(label));
            var text = new TextEdit { Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(0, 75) * EditorInterface.Singleton.GetEditorScale(), AccessibilityName = label }; column.AddChild(text); return text;
        }
        var before = Field("Current working text"); var after = Field("Incoming text");
        var warning = AssetUi.Label(""); warning.Visible = false; body.AddChild(warning);
        void Select()
        {
            if (dialog.Rows.GetSelected() is not { } item) return;
            var row = plan.Changes[(int)item.GetMetadata(0)]; before.Text = row.Before ?? "[not in working copy]"; after.Text = row.After;
            warning.Text = row.Warning; warning.Visible = row.Warning.Length > 0;
        }
        dialog.Rows.ItemSelected += Select;
        if (root.GetFirstChild() is { } first) { first.Select(0); Select(); }
        body.AddChild(AssetUi.Label("One undo step · Save copy persists the language document · installed Cliloc file unchanged"));
        dialog.Status = AssetUi.Label(""); dialog.Status.Visible = false; body.AddChild(dialog.Status);
        dialog.Confirmed += () =>
        {
            try { panel.PrepareExchange(document); plan.Apply(); panel.OnAssetsChanged(); dialog.Hide(); dialog.QueueFree(); }
            catch (Exception e) { dialog.Status.Text = e.Message; dialog.Status.Visible = true; }
        };
        dialog.Canceled += dialog.QueueFree; panel.AddChild(dialog); dialog.PopupCenteredClamped();
        dialog.GetOkButton().Disabled = plan.Changes.Count == 0;
        Callable.From(() => { if (GodotObject.IsInstanceValid(dialog)) { dialog.Size = new Vector2I(dialog.Size.X, 0); dialog.PopupCenteredClamped(); } }).CallDeferred();
        return dialog;
    }
}
#endif
