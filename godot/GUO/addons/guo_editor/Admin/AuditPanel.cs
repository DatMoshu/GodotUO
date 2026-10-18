#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using GUO.Workspace;

/// <summary>
/// The Admin tab's Audit list (sprint "Admin tab", AD8): the server's audit log (ADR-0035) as rows of when (UTC, Z,
/// the tab's one clock), who, op and result, the newest first. It filters by op, by who and by free text, and
/// Export writes the rows shown to a dated CSV under build/admin_audit. It asks for the log through the admin channel
/// (admin_audit, Administrator), which holds the server's last <see cref="MaxEntries"/> entries; secrets arrive masked,
/// as the server wrote them, and stay so. Reading, filtering and the CSV are <see cref="AdminAuditView"/>.
/// </summary>
[Tool]
public partial class AuditPanel : VBoxContainer
{
    /// <summary>The most entries admin_audit answers with (the bridge's AuditLog.Keep).</summary>
    public const int MaxEntries = 200;

    private const string AnyOp = "All ops", AnyWho = "Anyone";

    private Button _refresh, _export;
    private OptionButton _op, _who;
    private LineEdit _text;
    private Label _status, _hint;
    private Tree _tree;
    private List<AdminAuditRow> _rows = new(), _shown = new();
    private int _req;
    private bool _open;
    private List<AdminAuditRow> _serverRows = new();

    internal void RefreshLocal()
    {
        _rows = _serverRows.Concat(EditorMcpAccess.AuditRows).OrderBy(r => r.At).ToList();
        if (_tree == null) return;
        FillPickers();
        ApplyFilter();
    }

    /// <summary>Sends a message on the admin channel; false when there is none.</summary>
    public Func<JsonObject, bool> Send { get; set; }

    public event Action<string> Logged;

    /// <summary>Every entry of the last reply, oldest first.</summary>
    internal IReadOnlyList<AdminAuditRow> Rows => _rows;

    /// <summary>The rows the filter keeps, as shown and as Export writes them.</summary>
    internal IReadOnlyList<AdminAuditRow> Shown => _shown;

    /// <summary>How many replies arrived (the scripted check waits on it).</summary>
    public int Replies { get; private set; }

    /// <summary>The file the last Export wrote, or null.</summary>
    public string LastExport { get; private set; }

    public string StatusText => _status?.Text ?? "";

    public AuditPanel()
    {
        Name = "Audit";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        if (_tree != null)
        {
            return;
        }

        var bar = new HBoxContainer();
        AddChild(bar);
        _refresh = new Button { Text = "Refresh", TooltipText = $"Asks the server for its audit log again (its last {MaxEntries} entries)." };
        _refresh.Pressed += () => RequestLog();
        bar.AddChild(_refresh);
        _op = new OptionButton { TooltipText = "Shows one op only." };
        _op.ItemSelected += _ => ApplyFilter();
        bar.AddChild(_op);
        _who = new OptionButton { TooltipText = "Shows one editor only." };
        _who.ItemSelected += _ => ApplyFilter();
        bar.AddChild(_who);
        _text = new LineEdit
        {
            PlaceholderText = "Find in the log...",
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Shows the rows holding this text anywhere: time, who, op, result or the request's fields.",
        };
        _text.TextChanged += _ => ApplyFilter();
        bar.AddChild(_text);
        _export = new Button { Text = "Export CSV", TooltipText = "Writes the rows shown to build/admin_audit as a dated CSV." };
        _export.Pressed += () => ExportShown();
        bar.AddChild(_export);

        _status = new Label { ClipText = true };
        AddChild(_status);
        _tree = new Tree
        {
            Columns = 4,
            HideRoot = true,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        string[] titles = { "When (UTC)", "Who", "Op", "Result" };
        for (int i = 0; i < titles.Length; i++)
        {
            _tree.SetColumnTitle(i, titles[i]);
            _tree.SetColumnClipContent(i, true);
            _tree.SetColumnExpand(i, i == 3);
        }

        // The whole UTC time fits; Result takes the rest of the width.
        _tree.SetColumnCustomMinimumWidth(0, Px(200));
        _tree.SetColumnCustomMinimumWidth(1, Px(220));
        _tree.SetColumnCustomMinimumWidth(2, Px(150));
        AddChild(_tree);
        _hint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_hint);
        RefreshLocal();
        UpdateView();
    }

    // Sizes in the editor's own scale (a 4K display runs the editor at 2x).
    private static int Px(int size) => (int)Math.Round(size * EditorInterface.Singleton.GetEditorScale());

    public void OnAdminOpen()
    {
        _open = true;
        RequestLog();
        UpdateView();
    }

    public void OnClosed()
    {
        _open = false;
        UpdateView();
    }

    /// <summary>Asks for the server's last <see cref="MaxEntries"/> audit entries.</summary>
    public bool RequestLog() => Send?.Invoke(new JsonObject { ["op"] = "admin_audit", ["count"] = MaxEntries, ["req"] = ++_req }) == true;

    public void Handle(JsonNode msg)
    {
        if ((bool?)msg["ok"] == false)
        {
            Log($"[color=orange]audit log refused: {(string)msg["error"]}[/color]");
            return;
        }

        _serverRows = AdminAuditView.FromEntries(msg["entries"] as JsonArray);
        Replies++;
        RefreshLocal();
    }

    /// <summary>Sets the filter as if picked and typed (the scripted check): null or empty is any.</summary>
    public void SetFilter(string op, string who, string text)
    {
        if (_tree == null)
        {
            _Ready();
        }

        Pick(_op, string.IsNullOrEmpty(op) ? AnyOp : op);
        Pick(_who, string.IsNullOrEmpty(who) ? AnyWho : who);
        _text.Text = text ?? "";
        ApplyFilter();
    }

    /// <summary>Writes the rows shown to build/admin_audit/guo_AD8_audit_YYYYMMDD-HHMM.csv; the path, or null.</summary>
    public string ExportShown()
    {
        try
        {
            LastExport = AdminAuditView.Export(EditorData.RepoRoot, _shown, DateTime.UtcNow);
            Log($"exported {_shown.Count} audit row{(_shown.Count == 1 ? "" : "s")} to build/{AdminAuditView.ExportFolder}/{Path.GetFileName(LastExport)}");
            UpdateView();
            return LastExport;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"[color=orange]audit export failed: {e.Message}[/color]");
            return null;
        }
    }

    private static string Picked(OptionButton b, string any) => b.Selected < 0 || b.GetItemText(b.Selected) == any ? null : b.GetItemText(b.Selected);

    private static void Pick(OptionButton b, string text)
    {
        for (int i = 0; i < b.ItemCount; i++)
        {
            if (b.GetItemText(i) == text)
            {
                b.Select(i);
                return;
            }
        }

        // A value the log no longer holds: kept in the picker so the filter does not silently widen.
        b.AddItem(text);
        b.Select(b.ItemCount - 1);
    }

    // The pickers list the ops and editors of the last reply, keeping what was picked.
    private void FillPickers()
    {
        if (_op == null)
        {
            return;
        }

        Refill(_op, AnyOp, AdminAuditView.Distinct(_rows, r => r.Op));
        Refill(_who, AnyWho, AdminAuditView.Distinct(_rows, r => r.Who));
    }

    private static void Refill(OptionButton b, string any, List<string> values)
    {
        string was = Picked(b, any);
        b.Clear();
        b.AddItem(any);
        foreach (string v in values)
        {
            b.AddItem(v);
        }

        Pick(b, was ?? any);
    }

    private void ApplyFilter()
    {
        if (_tree == null)
        {
            return;
        }

        // Newest first: the latest action is the top row, with no scrolling needed.
        _shown = AdminAuditView.Filter(_rows, Picked(_op, AnyOp), Picked(_who, AnyWho), _text.Text);
        _shown.Reverse();
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        foreach (AdminAuditRow r in _shown)
        {
            TreeItem it = _tree.CreateItem(root);
            it.SetText(0, r.When);
            it.SetText(1, r.Who);
            it.SetText(2, r.Op);
            it.SetText(3, r.Result);
            if (!r.Ok)
            {
                it.SetCustomColor(3, new Color(1f, 0.65f, 0.3f));
            }

            // The request's fields, masked as the server wrote them, on hover.
            string detail = (r.Level.Length > 0 ? $"as {r.Level}" : "no admin level") + (r.Args.Length > 0 ? "\n" + r.Args : "");
            for (int c = 0; c < 4; c++)
            {
                it.SetTooltipText(c, detail);
            }
        }

        UpdateView();
    }

    private void UpdateView()
    {
        if (_tree == null)
        {
            return;
        }

        _refresh.Disabled = !_open;
        _export.Disabled = _shown.Count == 0;
        _status.Text = Replies == 0
            ? _open ? "asking the server for its audit log..." : "not connected"
            : _shown.Count == _rows.Count
                ? $"{_rows.Count} entr{(_rows.Count == 1 ? "y" : "ies")}, the server's latest"
                : $"{_shown.Count} of {_rows.Count} entries match the filter";
        _hint.Text = !_open && Replies == 0
            ? "The audit log needs the admin channel at Administrator: connect to the server first."
            : $"Every admin action on this server, and every refused attempt, newest first: when (UTC), who, what and whether it ran. "
                + $"Hover a row for its level and the request's fields; passwords and tokens show as ***. "
                + "Export CSV writes the rows shown to build/admin_audit."
                + (LastExport != null ? $" Last export: {Path.GetFileName(LastExport)}." : "");
    }

    private void Log(string line) => Logged?.Invoke(line);
}
#endif
