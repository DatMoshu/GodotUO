#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

/// <summary>One line as the view holds it: already redacted, with its level.</summary>
public sealed class LogLine
{
    public string Text;
    public LogLevel Level;
    public bool Notice;
    public bool Shown;
}

/// <summary>
/// One log, on screen: a toolbar (follow, pause, filter, level, find, clear view, copy, open folder)
/// over a read-only text body. It never writes or deletes the file; "clear" empties only this view.
/// The tailer runs on a worker task, this drains it on the main thread, at most a few thousand lines a frame.
/// </summary>
[Tool]
public partial class LogView : VBoxContainer
{
    public const int DefaultCap = 5000;
    private const int DrainPerFrame = 3000;

    private LogTailer _tailer;
    private readonly List<LogLine> _lines = new();
    private RichTextLabel _text;
    private CheckBox _follow;
    private Button _pause, _clear, _copy, _folder, _remove, _prev, _next;
    private LineEdit _filter, _find;
    private OptionButton _level;
    private SpinBox _capBox;
    private Label _status;
    private bool _built;
    private int _cap = DefaultCap;
    private string _findText = "";
    private int _findCursor = -1;
    private int _dropped;

    /// <summary>A stable id the panel finds this view by.</summary>
    public string Key { get; set; } = "";

    /// <summary>The tab's caption.</summary>
    public string Title { get; set; } = "";

    /// <summary>Written in UTC: the local time goes in front of each line that has a timestamp, and the header says so.</summary>
    public bool Utc { get; set; }

    /// <summary>A hint under the toolbar (what this source is, and what it cannot show).</summary>
    public string Note { get; set; } = "";

    /// <summary>True for a file the user added: it can be removed from the list.</summary>
    public bool Removable { get; set; }

    public Action<LogView> RemoveRequested;

    public LogTailer Tailer => _tailer;

    public bool Paused { get; private set; }

    public LogLevel MinLevel { get; private set; } = LogLevel.Info;

    public string FilterText { get; private set; } = "";

    public int Cap => _cap;

    public int BufferCount => _lines.Count;

    /// <summary>How many lines the cap has pushed out of the view so far.</summary>
    public int DroppedLines => _dropped;

    public int ShownCount => _lines.Count(l => l.Shown);

    public IEnumerable<LogLine> Lines => _lines;

    /// <summary>What the body shows, as plain text.</summary>
    public string BodyText => _text?.GetParsedText() ?? "";

    public bool FollowOn => _follow != null && _follow.ButtonPressed;

    public bool BodyFollowing => _text != null && _text.ScrollFollowing;

    public string StatusText => _status?.Text ?? "";

    public int FindMatches { get; private set; }

    public static Color ColorFor(LogLevel level) => level switch
    {
        LogLevel.Error => new Color("#f0654a"),
        LogLevel.Warn => new Color("#e0b050"),
        _ => new Color("#c9cdd2"),
    };

    public static readonly Color NoticeColor = new("#7f8a96");
    private static readonly Color FindBackground = new("#3b4a63");

    public LogView()
    {
        // Pixel art is never filtered; the log has none, but nothing here may inherit smoothing.
        TextureFilter = TextureFilterEnum.Nearest;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    /// <summary>Starts following. A source that may not exist yet is fine: the view says it is waiting.</summary>
    public void Attach(LogTailer tailer)
    {
        _tailer?.Dispose();
        _tailer = tailer;
        _tailer.Start();
    }

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
        var bar = new HFlowContainer();
        AddChild(bar);
        _follow = new CheckBox { Text = "Follow", ButtonPressed = true, TooltipText = "Keep the newest line in view" };
        _follow.Toggled += on => _text.ScrollFollowing = on;
        bar.AddChild(_follow);
        _pause = new Button { Text = "Pause", ToggleMode = true, TooltipText = "Freeze this view. The file keeps being read; the lines wait and appear when you resume." };
        _pause.Toggled += on => Paused = on;
        bar.AddChild(_pause);
        _filter = new LineEdit { PlaceholderText = "Filter", ClearButtonEnabled = true, CustomMinimumSize = new Vector2(140, 0), TooltipText = "Show only lines containing this text (not case sensitive)" };
        _filter.TextChanged += t => SetFilter(t);
        bar.AddChild(_filter);
        _level = new OptionButton { TooltipText = "Level filter" };
        _level.AddItem("All levels", 0);
        _level.AddItem("Warnings and errors", 1);
        _level.AddItem("Errors only", 2);
        _level.ItemSelected += i => SetMinLevel((LogLevel)(int)i);
        bar.AddChild(_level);
        _find = new LineEdit { PlaceholderText = "Find", ClearButtonEnabled = true, CustomMinimumSize = new Vector2(120, 0), TooltipText = "Find in the lines shown; Enter jumps to the next match" };
        _find.TextChanged += t => SetFind(t);
        _find.TextSubmitted += _ => FindStep(1);
        bar.AddChild(_find);
        _prev = new Button { Text = "<", TooltipText = "Previous match" };
        _prev.Pressed += () => FindStep(-1);
        bar.AddChild(_prev);
        _next = new Button { Text = ">", TooltipText = "Next match" };
        _next.Pressed += () => FindStep(1);
        bar.AddChild(_next);
        _clear = new Button { Text = "Clear view", TooltipText = "Empties this view only. The file is never touched." };
        _clear.Pressed += ClearView;
        bar.AddChild(_clear);
        _copy = new Button { Text = "Copy selection", TooltipText = "Copy the selected text (or every line shown when nothing is selected)" };
        _copy.Pressed += CopySelection;
        bar.AddChild(_copy);
        _folder = new Button { Text = "Open folder", TooltipText = "Open the folder holding this log" };
        _folder.Pressed += OpenFolder;
        bar.AddChild(_folder);
        bar.AddChild(new Label { Text = "last" });
        _capBox = new SpinBox { MinValue = 100, MaxValue = 200000, Step = 100, Value = _cap, TooltipText = "Line cap: only the newest lines are kept" };
        _capBox.ValueChanged += v => SetCap((int)v);
        bar.AddChild(_capBox);
        bar.AddChild(new Label { Text = "lines" });
        if (Removable)
        {
            _remove = new Button { Text = "Remove", TooltipText = "Stop listing this file. The file is never touched." };
            _remove.Pressed += () => RemoveRequested?.Invoke(this);
            bar.AddChild(_remove);
        }

        _status = new Label { ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, Modulate = new Color(1, 1, 1, 0.7f) };
        AddChild(_status);

        _text = new RichTextLabel
        {
            SelectionEnabled = true,
            ContextMenuEnabled = true,
            ScrollFollowing = true,
            ScrollActive = true,
            FitContent = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ShortcutKeysEnabled = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 120),
            TextureFilter = TextureFilterEnum.Nearest,
        };
        try
        {
            Font mono = EditorInterface.Singleton.GetEditorTheme()?.GetFont("output_source", "EditorFonts");
            if (mono != null)
            {
                _text.AddThemeFontOverride("normal_font", mono);
            }
        }
        catch (Exception)
        {
            // A headless editor may not have a theme: the default font does.
        }

        AddChild(_text);
        Rebuild();
        UpdateStatus();
    }

    public override void _Process(double delta) => Pump();

    /// <summary>Takes what the tailer has found since the last call. The panel's frame loop calls this; the smoke check calls it too.</summary>
    public void Pump()
    {
        if (!_built)
        {
            Build();
        }

        if (_tailer == null)
        {
            return;
        }

        if (!Paused)
        {
            int n = 0;
            bool any = false;
            while (n++ < DrainPerFrame && _tailer.Queue.TryDequeue(out TailItem item))
            {
                Add(item);
                any = true;
            }

            if (any)
            {
                Trim();
            }
        }

        UpdateStatus();
    }

    private void Add(TailItem item)
    {
        string shown = item.Notice ? item.Text : LogText.Redact(item.Text);
        if (Utc && !item.Notice)
        {
            shown = LogText.WithLocalTime(shown);
        }

        var line = new LogLine { Text = shown, Notice = item.Notice, Level = item.Notice ? LogLevel.Info : LogText.Classify(item.Text) };
        line.Shown = Passes(line);
        _lines.Add(line);
        if (line.Shown)
        {
            Append(line);
        }
    }

    private bool Passes(LogLine l) =>
        (l.Notice || l.Level >= MinLevel)
        && (FilterText.Length == 0 || l.Notice || l.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    private void Append(LogLine l)
    {
        bool match = _findText.Length > 0 && l.Text.Contains(_findText, StringComparison.OrdinalIgnoreCase);
        if (match)
        {
            _text.PushBgcolor(FindBackground);
        }

        _text.PushColor(l.Notice ? NoticeColor : ColorFor(l.Level));
        _text.AddText(l.Text);
        _text.Pop();
        if (match)
        {
            _text.Pop();
        }

        _text.Newline();
    }

    private void Trim()
    {
        int over = _lines.Count - _cap;
        if (over <= 0)
        {
            return;
        }

        // Take them off the front of the body one paragraph at a time, so a busy log is not redrawn whole.
        for (int i = 0; i < over; i++)
        {
            if (_lines[i].Shown && _text.GetParagraphCount() > 0)
            {
                _text.RemoveParagraph(0, true);
            }
        }

        _lines.RemoveRange(0, over);
        _dropped += over;
    }

    /// <summary>Redraws the body from the lines held, after a filter or find change.</summary>
    public void Rebuild()
    {
        if (_text == null)
        {
            return;
        }

        _text.Clear();
        foreach (LogLine l in _lines)
        {
            l.Shown = Passes(l);
            if (l.Shown)
            {
                Append(l);
            }
        }
    }

    public void SetFilter(string text)
    {
        FilterText = text ?? "";
        Rebuild();
    }

    public void SetMinLevel(LogLevel level)
    {
        MinLevel = level;
        Rebuild();
    }

    public void SetCap(int cap)
    {
        _cap = Math.Clamp(cap, 100, 200000);
        Trim();
    }

    public void SetPaused(bool paused)
    {
        Paused = paused;
        _pause?.SetPressedNoSignal(paused);
    }

    public void SetFollow(bool on)
    {
        if (_follow != null)
        {
            _follow.ButtonPressed = on;
        }

        if (_text != null)
        {
            _text.ScrollFollowing = on;
        }
    }

    public void SetFind(string text)
    {
        _findText = text ?? "";
        _findCursor = -1;
        Rebuild();
        FindMatches = _findText.Length == 0 ? 0 : _lines.Count(l => l.Shown && l.Text.Contains(_findText, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Jumps to the next (+1) or previous (-1) line shown that contains the find text. Returns its line number among those shown, or -1.</summary>
    public int FindStep(int direction)
    {
        if (_findText.Length == 0)
        {
            return -1;
        }

        var shown = _lines.Where(l => l.Shown).ToList();
        if (shown.Count == 0)
        {
            return -1;
        }

        int start = _findCursor < 0 ? (direction > 0 ? -1 : shown.Count) : _findCursor;
        for (int step = 1; step <= shown.Count; step++)
        {
            int i = ((start + direction * step) % shown.Count + shown.Count) % shown.Count;
            if (shown[i].Text.Contains(_findText, StringComparison.OrdinalIgnoreCase))
            {
                _findCursor = i;
                SetFollow(false);
                _text.ScrollToParagraph(i);
                return i;
            }
        }

        return -1;
    }

    /// <summary>Empties the view. The file, and what the tailer has read of it, are untouched.</summary>
    public void ClearView()
    {
        _lines.Clear();
        _findCursor = -1;
        _text?.Clear();
    }

    public void CopySelection()
    {
        string selected = _text?.GetSelectedText() ?? "";
        if (selected.Length == 0)
        {
            selected = string.Join("\n", _lines.Where(l => l.Shown).Select(l => l.Text));
        }

        DisplayServer.ClipboardSet(selected);
    }

    public void OpenFolder()
    {
        string path = _tailer?.CurrentPath;
        string dir = path != null ? Path.GetDirectoryName(path) : null;
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            OS.ShellOpen(dir);
        }
    }

    private void UpdateStatus()
    {
        if (_status == null)
        {
            return;
        }

        string path = _tailer?.CurrentPath ?? "(no file yet)";
        string clock = Utc ? "  |  times in this log are UTC; local time is added in front" : "";
        string held = Paused ? $"  |  paused, {_tailer?.Queue.Count ?? 0} waiting" : "";
        string note = Note.Length > 0 ? $"  |  {Note}" : "";
        _status.Text = $"{path}  |  {_tailer?.State ?? "idle"}  |  {ShownCount}/{_lines.Count} lines{held}{clock}{note}";
        _status.TooltipText = _status.Text;
    }

    public override void _ExitTree()
    {
        Release();
    }

    /// <summary>Stops the worker task. Safe to call twice.</summary>
    public void Release()
    {
        _tailer?.Dispose();
    }
}
#endif
