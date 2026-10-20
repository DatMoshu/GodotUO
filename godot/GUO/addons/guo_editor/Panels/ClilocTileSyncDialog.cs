#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>Review every mapped change before a selected, atomic project-document edit.</summary>
[Tool]
public partial class ClilocTileSyncDialog : ConfirmationDialog
{
    private readonly HashSet<int> _selected = new();
    private ClilocTileSyncPlan _plan;
    private OptionButton _kind;
    private LineEdit _search;
    private Label _summary, _pageLabel;
    private Button _previous, _next;
    private TextEdit _before, _after;
    private Label _warning;
    private int _page;
    internal Tree Rows { get; private set; }
    internal Label Status { get; private set; }
    internal int SelectionCount => _selected.Count;
    internal static ClilocTileSyncDialog Open(ClilocPanel panel, ClilocDocument document, ClilocTileSyncPlan plan)
    {
        var dialog = new ClilocTileSyncDialog { Title = $"Sync from tiledata · {document.DisplayName}", _plan = plan,
            DialogHideOnOk = false, OkButtonText = "Apply selected to working copy",
            Size = (Vector2I)(new Vector2(900, 0) * EditorInterface.Singleton.GetEditorScale()) };
        var body = new VBoxContainer(); dialog.AddChild(body);
        body.AddChild(AssetUi.Label($"Target: {document.DisplayName} · {plan.Description}"));
        body.AddChild(AssetUi.Label("Check the target language: item names replace selected translations. Blank/whitespace item names propose removal, not empty text. Source files stay unchanged."));
        var tools = new HBoxContainer(); body.AddChild(tools);
        dialog._kind = new OptionButton { AccessibilityName = "Synchronization change type" };
        foreach (string text in new[] { "All changes", "Add", "Update", "Remove" }) dialog._kind.AddItem(text);
        tools.AddChild(dialog._kind);
        dialog._search = new LineEdit { PlaceholderText = "Filter item hex ID, Cliloc ID or text", ClearButtonEnabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = "Filter synchronization changes" }; tools.AddChild(dialog._search);
        tools.AddChild(AssetUi.Action("GuiChecked", "Select every matching change across all pages, including matching removal rows", dialog.SelectMatching));
        tools.AddChild(AssetUi.Action("GuiUnchecked", "Clear the entire synchronization selection, including hidden pages", dialog.ClearSelection));
        dialog._summary = AssetUi.Label(""); body.AddChild(dialog._summary);
        dialog.Rows = new Tree { Columns = 5, HideRoot = true, ColumnTitlesVisible = true, AccessibilityName = "Selectable tiledata synchronization changes",
            CustomMinimumSize = new Vector2(0, 200) * EditorInterface.Singleton.GetEditorScale() }; body.AddChild(dialog.Rows);
        float scale = EditorInterface.Singleton.GetEditorScale();
        foreach (var column in new[] { (0, "Use", 48), (1, "Item", 88), (2, "Cliloc ID", 125), (3, "Action", 85) })
        { dialog.Rows.SetColumnTitle(column.Item1, column.Item2); dialog.Rows.SetColumnExpand(column.Item1, false); dialog.Rows.SetColumnCustomMinimumWidth(column.Item1, (int)(column.Item3 * scale)); }
        dialog.Rows.SetColumnTitle(4, "Proposed text");
        var paging = new HBoxContainer(); body.AddChild(paging);
        dialog._previous = AssetUi.Action("Back", "Previous synchronization page", () => { dialog._page--; dialog.RefreshRows(); }); paging.AddChild(dialog._previous);
        dialog._pageLabel = AssetUi.Label(""); paging.AddChild(dialog._pageLabel);
        dialog._next = AssetUi.Action("Forward", "Next synchronization page", () => { dialog._page++; dialog.RefreshRows(); }); paging.AddChild(dialog._next);
        var comparison = new HBoxContainer(); body.AddChild(comparison);
        TextEdit Field(string label)
        {
            var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; comparison.AddChild(column); column.AddChild(AssetUi.Label(label));
            var input = new TextEdit { Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, AccessibilityName = label,
                CustomMinimumSize = new Vector2(0, 75) * scale }; column.AddChild(input); return input;
        }
        dialog._before = Field("Current working text"); dialog._after = Field("Proposed item name");
        dialog._warning = AssetUi.Label(""); dialog._warning.Visible = false; body.AddChild(dialog._warning);
        body.AddChild(AssetUi.Label("Only selected rows are applied, as one undo step. Save copy persists project JSON. Existing flags stay unchanged; new strings use flag 0. No native file is written."));
        dialog.Status = AssetUi.Label(""); dialog.Status.Visible = false; body.AddChild(dialog.Status);
        dialog._kind.ItemSelected += _ => { dialog._page = 0; dialog.RefreshRows(); };
        dialog._search.TextChanged += _ => { dialog._page = 0; dialog.RefreshRows(); };
        dialog.Rows.ItemSelected += dialog.SelectRow;
        dialog.Rows.ItemEdited += () =>
        {
            var item = dialog.Rows.GetEdited(); if (item == null) return;
            int id = (int)item.GetMetadata(0); if (item.IsChecked(0)) dialog._selected.Add(id); else dialog._selected.Remove(id); dialog.UpdateSummary();
        };
        dialog.Confirmed += () =>
        {
            try { panel.PrepareExchange(document); plan.Apply(dialog._selected); panel.OnAssetsChanged(); dialog.Hide(); dialog.QueueFree(); }
            catch (Exception e) { dialog.Status.Text = e.Message; dialog.Status.Visible = true; }
        };
        dialog.Canceled += dialog.QueueFree; panel.AddChild(dialog); dialog.RefreshRows(); dialog.PopupCenteredClamped();
        Callable.From(() => { if (GodotObject.IsInstanceValid(dialog)) { dialog.Size = new Vector2I(dialog.Size.X, 0); dialog.PopupCenteredClamped(); } }).CallDeferred();
        return dialog;
    }
    private IEnumerable<ClilocTileChange> Filtered()
    {
        string kind = _kind.Selected == 0 ? "" : _kind.GetItemText(_kind.Selected), query = _search.Text.Trim();
        return _plan.Changes.Where(row => (kind.Length == 0 || row.Kind == kind) && (query.Length == 0
            || $"0x{row.TileId:X4} {row.Id} {row.Before} {row.After}".Contains(query, StringComparison.OrdinalIgnoreCase)));
    }
    internal void ChooseFilter(int kind, string query = "") { _kind.Selected = kind; _search.Text = query; _page = 0; RefreshRows(); }
    internal void SelectMatching() { foreach (var row in Filtered()) _selected.Add(row.Id); RefreshRows(); }
    internal void ClearSelection() { _selected.Clear(); RefreshRows(); }
    internal void SetSelected(int id, bool selected) { if (selected) _selected.Add(id); else _selected.Remove(id); RefreshRows(); }
    internal void NextPage() { _page++; RefreshRows(); }
    private void RefreshRows()
    {
        var rows = Filtered().ToArray(); int pages = Math.Max(1, (rows.Length + 249) / 250); _page = Math.Clamp(_page, 0, pages - 1);
        Rows.Clear(); TreeItem root = Rows.CreateItem();
        foreach (var row in rows.Skip(_page * 250).Take(250))
        {
            TreeItem item = Rows.CreateItem(root); item.SetMetadata(0, row.Id); item.SetCellMode(0, TreeItem.TreeCellMode.Check);
            item.SetEditable(0, true); item.SetChecked(0, _selected.Contains(row.Id));
            item.SetText(1, $"0x{row.TileId:X4}"); item.SetText(2, row.Id.ToString()); item.SetText(3, row.Kind);
            string preview = row.After == null ? "[remove from working copy]" : row.After.Replace('\r', ' ').Replace('\n', ' ');
            item.SetText(4, preview.Length > 120 ? preview[..120] + "…" : preview); item.SetTooltipText(4, row.Origin + "\n" + (row.After ?? "Remove from working copy"));
        }
        _previous.Disabled = _page == 0; _next.Disabled = _page == pages - 1;
        _pageLabel.Text = $"{rows.Length:N0} matching of {_plan.Changes.Count:N0} changes · page {_page + 1}/{pages}";
        if (root.GetFirstChild() is { } first) { first.Select(1); SelectRow(); }
        else { _before.Text = _after.Text = ""; _warning.Visible = false; }
        UpdateSummary();
    }
    private void UpdateSummary()
    {
        var rows = _plan.Changes.Where(row => _selected.Contains(row.Id)).ToArray();
        _summary.Text = $"Selected: {rows.Length:N0} · Add {rows.Count(row => row.Kind == "Add"):N0} · Update {rows.Count(row => row.Kind == "Update"):N0} · Remove {rows.Count(row => row.Kind == "Remove"):N0} · across all pages and filters";
        GetOkButton().Disabled = rows.Length == 0;
    }
    private void SelectRow()
    {
        if (Rows.GetSelected() is not { } item) return;
        var row = _plan.Changes.First(row => row.Id == (int)item.GetMetadata(0));
        _before.Text = row.Before ?? "[not in working copy]"; _after.Text = row.After ?? "[remove from working copy]";
        _warning.Text = row.Origin + (row.Warning.Length > 0 ? " · " + row.Warning : ""); _warning.Visible = true;
    }
}
#endif
