#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

/// <summary>
/// The Logs tab's contents: one sub-tab per log source, found automatically and re-checked every few
/// seconds. The server (the selected profile's own log files), each client the run bar started (its
/// console, which the run bar redirects to a file) and the client's file logs, the Godot log, and any
/// file the user adds. Read only: nothing here writes to a log or deletes one, and nothing writes to the
/// client install. Everything it holds (worker tasks) is released in <see cref="Shutdown"/>.
/// </summary>
[Tool]
public partial class LogsPanel : VBoxContainer
{
    private const double RefreshSeconds = 3;

    private TabContainer _tabs;
    private readonly Dictionary<string, LogView> _views = new();
    private LogProfile _profile;
    private string[] _serverDirs = Array.Empty<string>();
    private string[] _clientDirs = Array.Empty<string>();
    private LogSources.Settings _settings = new();
    private FileDialog _dialog;
    private double _since = RefreshSeconds;
    private bool _built;
    private int _slots = 4;

    /// <summary>Where the extra files and the line cap are kept (a test points this at its own folder).</summary>
    public string SettingsPath { get; set; } = LogSources.SettingsPath;

    /// <summary>The run bar's server folder (a test points this at its own folder).</summary>
    public string ServersRoot { get; set; } = LogSources.ServersRoot;

    /// <summary>Poll interval handed to every tailer (a test shortens it).</summary>
    public int PollMilliseconds { get; set; } = 250;

    /// <summary>When false, the panel does not look for the server, clients and Godot log by itself (a test adds its own).</summary>
    public bool AutoDiscover { get; set; } = true;

    public IReadOnlyDictionary<string, LogView> Views => _views;

    public int Cap => _settings.Cap;

    public LogView View(string key) => _views.TryGetValue(key, out LogView v) ? v : null;

    public IEnumerable<string> ExtraFiles => _settings.Files;

    public override void _Ready()
    {
        Build();
    }

    private void Build()
    {
        if (_built)
        {
            return;
        }

        _built = true;
        TextureFilter = TextureFilterEnum.Nearest;
        var top = new HBoxContainer();
        AddChild(top);
        var add = new Button { Text = "Add file…", TooltipText = "Follow any other log file, read only" };
        add.Pressed += ChooseFile;
        top.AddChild(add);
        top.AddChild(new Label
        {
            Text = "Read only: nothing here changes a log or the client install. Secrets in a line are hidden.",
            Modulate = new Color(1, 1, 1, 0.6f),
        });

        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(480, 200), TextureFilter = TextureFilterEnum.Nearest };
        AddChild(_tabs);
        _settings = LogSources.LoadSettings(SettingsPath);
        foreach (string file in _settings.Files.ToArray())
        {
            AttachCustom(file);
        }

        Refresh();
    }

    public override void _Process(double delta)
    {
        _since += delta;
        if (_since >= RefreshSeconds)
        {
            _since = 0;
            Refresh();
        }
    }

    /// <summary>Looks again for the sources that come and go: the profile's server folders, the client consoles.</summary>
    public void Refresh()
    {
        if (!_built)
        {
            Build();
            return;
        }

        if (!AutoDiscover)
        {
            return;
        }

        _profile = LogSources.SelectedProfile(ServersRoot);
        _serverDirs = LogSources.ServerFolders(_profile, EditorData.Setting("UO_SHARD_DIST", ""));
        _clientDirs = LogSources.ClientFileFolders(_profile, ProjectSettings.GlobalizePath("res://"));

        string name = _profile?.Name ?? "no profile";
        LogView server = Ensure("server", "Server", () => Dynamic(() => _serverDirs), utc: false);
        server.Title = "Server: " + name;
        server.Note = ServerNote();
        _tabs.SetTabTitle(server.GetIndex(), server.Title);

        for (int slot = 0; slot < _slots; slot++)
        {
            if (_profile == null || string.IsNullOrEmpty(_profile.Id))
            {
                break;
            }

            string file = LogSources.ClientConsole(_profile.Id, slot, ServersRoot);
            string key = "client" + (slot + 1);
            if (_views.ContainsKey(key) || File.Exists(file))
            {
                LogView v = Ensure(key, "Client " + (slot + 1), () => new LogTailer(file), utc: true);
                v.Note = "the client's console, written by the run bar";
            }
        }

        LogView files = Ensure("clientfiles", "Client files", () => Dynamic(() => _clientDirs), utc: false);
        files.Note = "the client's own file logs (packet log, crashes)";
        LogView godot = Ensure("godot", "Godot log", () => new LogTailer(LogSources.GodotLog()), utc: false);
        godot.Note = "the editor's own output and the project's (needs file logging on in Project Settings)";
    }

    private string ServerNote()
    {
        if (_profile == null)
        {
            return "no server profile yet: add one in Manage servers";
        }

        string state = Path.Combine(ServersRoot, _profile.Id, "process.json");
        bool managed = false;
        try
        {
            managed = ManagedServerProcess.Running(state);
        }
        catch (Exception)
        {
            // An unreadable state file means "not running" here.
        }

        return managed
            ? "started by the run bar; its console output is not captured, so its log files are shown"
            : "the shard's log files (the newest under its Logs folder)";
    }

    private LogTailer Dynamic(Func<string[]> folders)
    {
        // Looking through a folder tree is not free: do it at most every two seconds, on the tailer's own thread.
        string cached = null;
        DateTime next = DateTime.MinValue;
        return new LogTailer(() =>
        {
            if (DateTime.UtcNow >= next)
            {
                cached = LogSources.Newest(folders());
                next = DateTime.UtcNow.AddSeconds(2);
            }

            return cached;
        });
    }

    private LogView Ensure(string key, string title, Func<LogTailer> makeTailer, bool utc)
    {
        if (_views.TryGetValue(key, out LogView existing))
        {
            return existing;
        }

        var view = new LogView { Key = key, Title = title, Utc = utc, Name = key };
        view.SetCap(_settings.Cap);
        LogTailer tailer = makeTailer();
        tailer.PollMilliseconds = PollMilliseconds;
        view.Attach(tailer);
        _views[key] = view;
        _tabs.AddChild(view);
        _tabs.SetTabTitle(_tabs.GetTabCount() - 1, title);
        return view;
    }

    /// <summary>Follows one more file (and remembers it). The F3 command "Logs: add file" and the button both end here.</summary>
    public LogView AddFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        if (!_settings.Files.Contains(path))
        {
            _settings.Files.Add(path);
            try
            {
                LogSources.SaveSettings(SettingsPath, _settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                GD.PushWarning("[GUO editor] could not remember the log file: " + ex.Message);
            }
        }

        return AttachCustom(path);
    }

    private LogView AttachCustom(string path)
    {
        string key = "file:" + path;
        if (_views.TryGetValue(key, out LogView existing))
        {
            ShowView(existing);
            return existing;
        }

        LogView view = Ensure(key, Path.GetFileName(path), () => new LogTailer(path), utc: false);
        view.Removable = true;
        view.Note = "a file you added";
        view.RemoveRequested += RemoveFile;
        ShowView(view);
        return view;
    }

    private void RemoveFile(LogView view)
    {
        string path = view.Key.StartsWith("file:", StringComparison.Ordinal) ? view.Key.Substring(5) : null;
        if (path == null)
        {
            return;
        }

        _settings.Files.Remove(path);
        try
        {
            LogSources.SaveSettings(SettingsPath, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning("[GUO editor] could not save the log file list: " + ex.Message);
        }

        _views.Remove(view.Key);
        view.Release();
        _tabs.RemoveChild(view);
        view.QueueFree();
    }

    public void ChooseFile()
    {
        if (_dialog == null)
        {
            _dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Title = "Follow a log file",
                Filters = new[] { "*.log, *.txt ; Log files", "* ; Any file" },
                UseNativeDialog = false,
            };
            _dialog.FileSelected += p => AddFile(p);
            AddChild(_dialog);
        }

        _dialog.PopupCentered(new Vector2I(760, 480));
    }

    /// <summary>Brings a source's sub-tab forward; false when there is none (a client that was not started).</summary>
    public bool Show(string key)
    {
        if (!_views.TryGetValue(key, out LogView v))
        {
            return false;
        }

        ShowView(v);
        return true;
    }

    private void ShowView(LogView v)
    {
        int index = _tabs != null && v.GetParent() == _tabs ? v.GetIndex() : -1;
        if (index >= 0)
        {
            _tabs.CurrentTab = index;
        }
    }

    /// <summary>The cap for every view, remembered.</summary>
    public void SetCap(int cap)
    {
        _settings.Cap = Math.Clamp(cap, 100, 200000);
        foreach (LogView v in _views.Values)
        {
            v.SetCap(_settings.Cap);
        }
    }

    /// <summary>Adds a source that is not discovered (the smoke check's own, with a fast poll). The caller owns what it follows.</summary>
    public LogView AddSource(string key, string title, LogTailer tailer, bool utc = false)
    {
        return Ensure(key, title, () => tailer, utc);
    }

    /// <summary>Stops every tailer. The plugin calls this before an assembly reload and when the editor closes.</summary>
    public void Shutdown()
    {
        foreach (LogView v in _views.Values)
        {
            v.Release();
        }
    }

    public override void _ExitTree()
    {
        Shutdown();
    }
}
#endif
