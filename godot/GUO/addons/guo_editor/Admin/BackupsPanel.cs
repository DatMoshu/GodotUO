#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using GUO.Workspace;

/// <summary>
/// The Admin tab's Backups (sprint "Admin tab", AD6), for a server on this computer: Back up now saves the world and
/// keeps a copy of its save folder (the newest N kept, the rest removed), and Restore puts one back. Both go through
/// the editor bridge's admin channel (admin_backup; ADR-0035), which audits them. A restore first saves and backs up
/// the world as it is (a "before-restore" backup, so it can be undone), then restarts the server through the run bar
/// and, with the server stopped, puts the picked backup in place of the save folder (<see cref="ShardBackups"/>).
/// ModernUO's own automatic backups and archives are left alone. Folders are used, never logged.
/// </summary>
/// <remarks>The VPS profile's buttons (muo_shard's backup and restore plans) are a later story (AD7).</remarks>
[Tool]
public partial class BackupsPanel : VBoxContainer
{
    /// <summary>Where a restore is.</summary>
    public enum RestorePhase { None, BackingUp, Restarting }

    private const string KeepSetting = "guo/admin/backup_keep";

    private Button _now, _restore, _refresh;
    private SpinBox _keep;
    private Label _status, _hint;
    private Tree _tree;
    private ConfirmationDialog _confirm;
    private readonly List<JsonObject> _rows = new();
    private string _savesPath, _backupPath;
    private string _restoring;
    private int _req;
    private bool _busy;

    /// <summary>Sends a message on the admin channel; false when there is none.</summary>
    public Func<JsonObject, bool> Send { get; set; }

    /// <summary>The Admin tab: Restart through the run bar, and why it cannot run.</summary>
    public AdminView Admin { get; set; }

    public event Action<string> Logged;

    /// <summary>The backups of the last list, newest first.</summary>
    public IReadOnlyList<JsonObject> Rows => _rows;

    /// <summary>How many lists arrived (with a reply to Back up now or Restore, which carry one too).</summary>
    public int Lists { get; private set; }

    /// <summary>The last reply to Back up now or to a restore's backup.</summary>
    public JsonNode LastReply { get; private set; }

    /// <summary>How many such replies arrived.</summary>
    public int Replies { get; private set; }

    public RestorePhase Restoring { get; private set; }

    /// <summary>How many restores finished, well or not (the smoke check waits on it).</summary>
    public int RestoresFinished { get; private set; }

    /// <summary>The backup the last restore put in place, or null.</summary>
    public string LastRestored { get; private set; }

    /// <summary>Why the last restore did not put its backup in place, or null.</summary>
    public string LastRestoreError { get; private set; }

    /// <summary>The backup picked in the list, or null.</summary>
    public string Selected { get; private set; }

    public int Keep => (int)(_keep?.Value ?? ShardBackups.DefaultKeep);

    public string StatusText => _status?.Text ?? "";

    public string HintText => _hint?.Text ?? "";

    /// <summary>True while the restore confirm is open.</summary>
    public bool ConfirmOpen => _confirm?.Visible == true;

    public BackupsPanel()
    {
        Name = "Backups";
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
        _now = new Button { Text = "Back up now", TooltipText = "Saves the world, then keeps a copy of the save. Players stay on." };
        _now.Pressed += () => BackUpNow();
        bar.AddChild(_now);
        _restore = new Button { Text = "Restore...", TooltipText = "Puts the picked backup back: the server restarts with the world as it was then." };
        _restore.Pressed += () => ConfirmRestore(Selected);
        bar.AddChild(_restore);
        _refresh = new Button { Text = "Refresh", TooltipText = "Asks the server for its backups again." };
        _refresh.Pressed += () => RequestList();
        bar.AddChild(_refresh);
        bar.AddChild(new Label { Text = "Keep" });
        _keep = new SpinBox { MinValue = 1, MaxValue = ShardBackups.MaxKeep, Value = SavedKeep(),
            TooltipText = "How many backups to keep. Back up now removes the oldest beyond this (never the one a restore needs)." };
        _keep.ValueChanged += v => SaveKeep((int)v);
        bar.AddChild(_keep);
        _status = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        bar.AddChild(_status);

        _tree = new Tree
        {
            Columns = 4,
            HideRoot = true,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        string[] titles = { "Backup", "Taken", "Why", "Size" };
        for (int i = 0; i < titles.Length; i++)
        {
            _tree.SetColumnTitle(i, titles[i]);
            _tree.SetColumnClipContent(i, true);
            _tree.SetColumnExpand(i, i is 0 or 2);
        }

        _tree.SetColumnCustomMinimumWidth(1, Px(190));
        _tree.SetColumnCustomMinimumWidth(3, Px(110));
        _tree.ItemSelected += () => Select(_tree.GetSelected()?.GetMetadata(0).AsString());
        AddChild(_tree);
        _hint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_hint);
        UpdateView();
    }

    // Sizes in the editor's own scale (a 4K display runs the editor at 2x).
    private static int Px(int size) => (int)Math.Round(size * EditorInterface.Singleton.GetEditorScale());

    private static int SavedKeep()
    {
        EditorSettings s = EditorInterface.Singleton.GetEditorSettings();
        return s.HasSetting(KeepSetting) ? ShardBackups.Keep((int)s.GetSetting(KeepSetting)) : ShardBackups.DefaultKeep;
    }

    private static void SaveKeep(int keep) => EditorInterface.Singleton.GetEditorSettings().SetSetting(KeepSetting, ShardBackups.Keep(keep));

    /// <summary>Sets the keep count as if typed (the scripted check).</summary>
    public void SetKeep(int keep)
    {
        if (_keep == null)
        {
            _Ready();
        }

        _keep.Value = ShardBackups.Keep(keep);
    }

    public void OnAdminOpen()
    {
        RequestList();
        if (Restoring == RestorePhase.Restarting)
        {
            // The restarted server answers: the restore is over; the list shows what is there now.
            Restoring = RestorePhase.None;
            RestoresFinished++;
            Log(LastRestoreError == null
                ? $"restore: the server is back with the world of backup {LastRestored}"
                : $"[color=orange]restore: {LastRestoreError}; the server is back with the world it had[/color]");
        }

        UpdateView();
    }

    public void OnClosed() => UpdateView();

    /// <summary>The restart stopped before the server was back (the save failed, the run bar failed, or it timed out).</summary>
    public void OnRestartAbandoned()
    {
        if (Restoring == RestorePhase.None)
        {
            return;
        }

        Restoring = RestorePhase.None;
        RestoresFinished++;
        LastRestoreError ??= "the restart did not finish";
        Log($"[color=orange]restore: {LastRestoreError}; see the Logs dock[/color]");
        UpdateView();
    }

    public bool RequestList() => Send?.Invoke(new JsonObject { ["op"] = "admin_backup", ["action"] = "list", ["req"] = ++_req }) == true;

    /// <summary>Why Back up now cannot run, or null.</summary>
    public string BackupBlocker()
    {
        if (Admin == null || !Admin.Connected || Admin.Granted == null)
        {
            return "Backups need the admin channel: connect to the server first.";
        }

        return _busy || Restoring != RestorePhase.None ? "A backup or a restore is already under way." : null;
    }

    /// <summary>Why <paramref name="name"/> cannot be restored, or null.</summary>
    public string RestoreBlocker(string name)
    {
        if (BackupBlocker() is { } why)
        {
            return why;
        }

        if (Admin.RestartBlocker() is { } restart)
        {
            return "Restore restarts the server. " + restart;
        }

        if (!ShardBackups.ValidName(name) || _rows.All(r => (string)r["name"] != name))
        {
            return "Pick a backup in the list first.";
        }

        if (string.IsNullOrEmpty(_backupPath) || string.IsNullOrEmpty(_savesPath) || !Directory.Exists(Path.Combine(_backupPath, name)))
        {
            return "This backup is not on this computer, so the editor cannot put it in place. Restore works for a server on this computer only.";
        }

        return null;
    }

    /// <summary>Saves the world and keeps a copy; the reply comes once both are on disk.</summary>
    public bool BackUpNow()
    {
        if (BackupBlocker() is { } why)
        {
            Log($"[color=orange]{why}[/color]");
            return false;
        }

        if (Send?.Invoke(new JsonObject { ["op"] = "admin_backup", ["action"] = "now", ["keep"] = Keep, ["req"] = ++_req }) != true)
        {
            return false;
        }

        _busy = true;
        Log($"backing up: saving the world, then copying its save (keeping the newest {Keep})...");
        UpdateView();
        return true;
    }

    /// <summary>Asks before a restore, in plain words.</summary>
    public void ConfirmRestore(string name)
    {
        if (RestoreBlocker(name) is { } why)
        {
            Log($"[color=orange]{why}[/color]");
            return;
        }

        JsonObject row = _rows.First(r => (string)r["name"] == name);
        _confirm?.QueueFree();
        _confirm = new ConfirmationDialog
        {
            Title = "Restore " + name,
            DialogText = $"Put the world back as it was when backup {name} was taken ({Taken(row)})?\n\n"
                + "First the world as it is now is saved and backed up (a \"before-restore\" backup, so you can come back to it).\n"
                + "Then the server restarts with the backup's world. Everyone online is disconnected until it is back,\n"
                + "and everything done in the game since the backup is gone from the running world.",
            OkButtonText = "Restore",
        };
        AddChild(_confirm);
        _confirm.Canceled += CloseConfirm;
        _confirm.Confirmed += () =>
        {
            CloseConfirm();
            Restore(name);
        };
        _confirm.PopupCentered();
    }

    public void CloseConfirm()
    {
        _confirm?.QueueFree();
        _confirm = null;
    }

    /// <summary>Restores <paramref name="name"/> with no dialog (the confirm and the smoke check call this).</summary>
    public bool Restore(string name)
    {
        if (RestoreBlocker(name) is { } why)
        {
            Log($"[color=orange]{why}[/color]");
            return false;
        }

        if (Send?.Invoke(new JsonObject { ["op"] = "admin_backup", ["action"] = "restore", ["name"] = name, ["keep"] = Keep, ["req"] = ++_req }) != true)
        {
            return false;
        }

        _restoring = name;
        LastRestoreError = null;
        LastRestored = null;
        Restoring = RestorePhase.BackingUp;
        Log($"restore {name}: saving and backing up the world as it is first...");
        UpdateView();
        return true;
    }

    public void Handle(JsonNode msg)
    {
        string action = (string)msg["action"];
        bool ok = (bool?)msg["ok"] == true;
        if (msg["snapshots"] is JsonArray list)
        {
            _rows.Clear();
            _rows.AddRange(list.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()));
            _savesPath = (string)msg["saves_path"];
            _backupPath = (string)msg["backup_path"];
            Lists++;
        }

        if (action != "list")
        {
            LastReply = msg;
            Replies++;
            _busy = false;
        }

        if (!ok)
        {
            Log($"[color=orange]backups: the server refused: {(string)msg["error"]}[/color]");
            if (action == "restore" && Restoring == RestorePhase.BackingUp)
            {
                Restoring = RestorePhase.None;
                RestoresFinished++;
                LastRestoreError = (string)msg["error"];
                Log("[color=orange]restore: nothing was changed[/color]");
            }
        }
        else if (action is "now" or "restore")
        {
            JsonNode snap = msg["snapshot"];
            string pruned = msg["pruned"] is JsonArray p && p.Count > 0 ? $"; removed the oldest: {string.Join(", ", p.Select(n => (string)n))}" : "";
            Log($"backed up the world as {(string)snap?["name"]} ({Size((long?)snap?["bytes"] ?? 0)}, {(int?)snap?["files"] ?? 0} files; "
                + $"save {(long?)msg["save_ms"] ?? 0} ms, copy {(long?)msg["copy_ms"] ?? 0} ms){pruned}");
            if (action == "restore" && Restoring == RestorePhase.BackingUp)
            {
                RestartForRestore();
            }
            else
            {
                Admin?.RequestStatus();
            }
        }

        Fill();
        UpdateView();
    }

    // The before-restore backup is on disk: the run bar restarts the server, the backup going in while it is stopped.
    private void RestartForRestore()
    {
        string name = _restoring, root = _backupPath, saves = _savesPath;
        Restoring = RestorePhase.Restarting;
        Log($"restore {name}: restarting the server to put the backup in place...");
        bool started = Admin.Restart(() =>
        {
            try
            {
                ShardBackups.Restore(root, name, saves);
                LastRestored = name;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // ShardBackups put the previous save back: the server starts with the world it had.
                LastRestoreError = "the backup could not be put in place (" + e.Message.Replace(saves, "Saves").Replace(root, "the backups folder") + ")";
            }
        });
        if (!started)
        {
            Restoring = RestorePhase.None;
            RestoresFinished++;
            LastRestoreError = "the restart did not start";
            Log("[color=orange]restore: the restart did not start, so nothing was put back (the before-restore backup is kept)[/color]");
        }
    }

    private void Select(string name)
    {
        Selected = name;
        UpdateView();
    }

    /// <summary>Picks a backup in the list (the scripted check).</summary>
    public bool SelectBackup(string name)
    {
        for (TreeItem it = _tree?.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext())
        {
            if (it.GetMetadata(0).AsString() == name)
            {
                it.Select(0);
                Select(name);
                return true;
            }
        }

        return false;
    }

    private void Fill()
    {
        if (_tree == null)
        {
            return;
        }

        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        foreach (JsonObject r in _rows)
        {
            TreeItem it = _tree.CreateItem(root);
            string name = (string)r["name"];
            it.SetText(0, name);
            it.SetMetadata(0, name);
            it.SetText(1, Taken(r));
            it.SetText(2, Why((string)r["reason"]) + ((string)r["editor"] is { Length: > 0 } ed ? $", by {ed}" : ""));
            it.SetText(3, Size((long?)r["bytes"] ?? 0));
            if (name == Selected)
            {
                it.Select(0);
            }
        }

        if (Selected != null && _rows.All(r => (string)r["name"] != Selected))
        {
            Selected = null;
        }
    }

    private void UpdateView()
    {
        if (_tree == null)
        {
            return;
        }

        string blocker = BackupBlocker();
        _now.Disabled = blocker != null;
        _now.TooltipText = blocker ?? "Saves the world, then keeps a copy of the save. Players stay on.";
        _refresh.Disabled = Admin?.Granted == null;
        string restore = RestoreBlocker(Selected);
        _restore.Disabled = restore != null;
        _status.Text = Restoring switch
        {
            RestorePhase.BackingUp => "restore: backing up the world first...",
            RestorePhase.Restarting => "restore: restarting the server with the backup...",
            _ => _busy ? "backing up..." : $"{_rows.Count} backup{(_rows.Count == 1 ? "" : "s")}, keeping the newest {Keep}",
        };
        _hint.Text = Describe(restore);
    }

    // The panel's plain words: what there is, what the buttons do, and why one is off.
    private string Describe(string restoreBlocker)
    {
        var text = new StringBuilder();
        text.Append(_rows.Count == 0
            ? "There is no backup of this world yet. Back up now saves the world and keeps a copy of it."
            : $"The newest backup was taken {Taken(_rows[0])}. Back up now saves the world and keeps a copy; the oldest beyond {Keep} are removed.");
        text.Append(' ');
        text.Append(Selected == null
            ? "Pick a backup to restore it."
            : restoreBlocker ?? $"Restore puts {Selected} back: the world as it is now is backed up first, then the server restarts with the backup's world.");
        return text.ToString();
    }

    private static string Taken(JsonObject r)
    {
        string at = (string)r["at"];
        return DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)
            ? t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z (" + AdminView.Duration((long)(DateTime.UtcNow - t).TotalSeconds) + " ago)"
            : at ?? "";
    }

    private static string Why(string reason) => reason switch
    {
        ShardBackups.BeforeRestore => "before a restore",
        "manual" => "Back up now",
        _ => reason ?? "",
    };

    internal static string Size(long bytes) => bytes >= 1024 * 1024
        ? (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";

    private void Log(string line) => Logged?.Invoke(line);
}
#endif
