#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The AI dock's Sessions tab (ADR-0028): what is already on this machine, read only. Claude Code
/// transcripts, Codex sessions and Cursor projects, each with its project, last activity and the
/// first user line. Metadata only: see <see cref="SessionScanner"/> for what is and is not read.
/// "Open transcript" hands the file to the operating system; "Send to queue" posts to
/// tools/agent_queue addressed to that session's name. The list refreshes when asked.
/// </summary>
[Tool]
public partial class AiSessionsTab : VBoxContainer
{
    private AiHub _hub;
    private Func<AiQueueTab> _queue;
    private ItemList _list;
    private Label _status;
    private LineEdit _message, _recipient;
    private Button _open, _send;
    private bool _built, _loaded;
    private Task _refreshTask;
    private readonly List<SessionInfo> _sessions = new();

    public AiSessionsTab() : this(null, null)
    {
    }

    internal AiSessionsTab(AiHub hub, Func<AiQueueTab> queue)
    {
        Name = "Sessions";
        _hub = hub;
        _queue = queue;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    /// <summary>The home folder to read; null is the user's own. The smoke points it at a fake one.</summary>
    public string Home { get; set; } = SessionScanner.HomeOverride;

    /// <summary>Called with each file the scanner opens (the smoke watches this).</summary>
    public Action<string> Opened { get; set; }

    /// <summary>How "Open transcript" reaches the operating system; the smoke replaces it.</summary>
    public Action<string> OpenWith { get; set; } = path => OS.ShellOpen(path);

    public IReadOnlyList<SessionInfo> Sessions => _sessions;

    /// <summary>The list as plain text, one row per line.</summary>
    public string ListText
    {
        get
        {
            var rows = new List<string>();
            for (int i = 0; _list != null && i < _list.ItemCount; i++)
            {
                rows.Add(_list.GetItemText(i));
            }

            return string.Join("\n", rows);
        }
    }

    public override void _Ready()
    {
        if (_built || _hub == null)
        {
            return;
        }

        _built = true;
        var top = new HBoxContainer();
        AddChild(top);
        var refresh = new Button { Text = "Refresh", TooltipText = "Read the session folders again. Nothing is sent anywhere." };
        refresh.Pressed += () => _ = RefreshAsync();
        top.AddChild(refresh);
        _open = new Button { Text = "Open transcript", Disabled = true, TooltipText = "Open the transcript (or the project folder) with the operating system's viewer" };
        _open.Pressed += () => OpenSelected();
        top.AddChild(_open);
        top.AddChild(new Label { Text = "Message" });
        _message = new LineEdit { PlaceholderText = "A request for the selected session. Never put a secret here.", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        top.AddChild(_message);
        top.AddChild(new Label { Text = "Queue address" });
        _recipient = new LineEdit { CustomMinimumSize = new Vector2(150, 0), TooltipText = "Use a registered address from the Queue tab. A transcript's session name may have no listener." };
        top.AddChild(_recipient);
        _send = new Button { Text = "Send to queue", Disabled = true, TooltipText = "Post the message to the agent queue, addressed to this session's name" };
        _send.Pressed += () => _ = SendSelectedAsync(_message.Text);
        top.AddChild(_send);
        _status = new Label { Text = "Press Refresh to read ~/.claude, ~/.codex and Cursor's project folders (names and first lines only).", ClipText = true };
        AddChild(_status);

        _list = new ItemList
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(300, 100),
            TextureFilter = TextureFilterEnum.Nearest,
            AllowReselect = true,
        };
        _list.ItemSelected += _ =>
        {
            _open.Disabled = Selected == null;
            _send.Disabled = Selected == null;
            _recipient.Text = Selected?.QueueName ?? "";
        };
        AddChild(_list);

        // The first time the tab is shown it reads, so the dock costs nothing until it is looked at.
        VisibilityChanged += () =>
        {
            if (IsVisibleInTree() && !_loaded)
            {
                _ = RefreshAsync();
            }
        };
    }

    /// <summary>The selected session, or null.</summary>
    public SessionInfo Selected
    {
        get
        {
            int[] sel = _list?.GetSelectedItems();
            return sel != null && sel.Length > 0 && sel[0] < _sessions.Count ? _sessions[sel[0]] : null;
        }
    }

    /// <summary>Selects a row by session id (the smoke clicks this way).</summary>
    public bool Select(string id)
    {
        int at = _sessions.FindIndex(s => s.Id == id);
        if (at < 0)
        {
            return false;
        }

        _list.Select(at);
        _open.Disabled = false;
        _send.Disabled = false;
        _recipient.Text = _sessions[at].QueueName;
        return true;
    }

    /// <summary>Reads the folders on a worker thread and redraws the list.</summary>
    public Task RefreshAsync()
    {
        // Showing the tab and an explicit Refresh can arrive together. Share the
        // scan so a later completion cannot clear a selection made in between.
        if (_refreshTask != null && !_refreshTask.IsCompleted)
        {
            return _refreshTask;
        }

        return _refreshTask = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        _loaded = true;
        string home = string.IsNullOrWhiteSpace(Home) ? null : Home;
        Action<string> opened = Opened;
        SetStatus("reading...");
        List<SessionInfo> found;
        try
        {
            found = await Task.Run(() => new SessionScanner(home, null, opened).Scan());
        }
        catch (Exception ex)
        {
            SetStatus($"could not read the session folders: {ex.Message}", true);
            return;
        }

        _sessions.Clear();
        _sessions.AddRange(found);
        _list.Clear();
        foreach (SessionInfo s in found)
        {
            string first = s.First.Length > 0 ? $"  |  {s.First}" : "";
            _list.AddItem($"{s.Source,-6} {s.Project}  |  {s.Last:yyyy-MM-dd HH:mm}{first}");
        }

        _open.Disabled = true;
        _send.Disabled = true;
        SetStatus($"{found.Count} session(s)");
    }

    /// <summary>Opens the selected transcript (or project folder) in the operating system's viewer.</summary>
    public bool OpenSelected()
    {
        SessionInfo s = Selected;
        if (s?.Path == null)
        {
            SetStatus(s == null ? "select a session first" : "this session has no file (it is only in the index)", true);
            return false;
        }

        OpenWith?.Invoke(s.Path);
        SetStatus($"opened {System.IO.Path.GetFileName(s.Path)}");
        return true;
    }

    /// <summary>Posts to the agent queue, addressed to the selected session's name. The new request id, or 0.</summary>
    public async Task<long> SendSelectedAsync(string text, string address = null)
    {
        SessionInfo s = Selected;
        AiQueueTab q = _queue?.Invoke();
        if (s == null || q == null)
        {
            SetStatus("select a session first", true);
            return 0;
        }

        string to = (address ?? _recipient?.Text ?? s.QueueName).Trim();
        long id = await q.PostAsync(to, "guo-editor", text ?? "");
        SetStatus(id > 0 ? $"posted request {id} to {to}" : $"not posted: {q.LastError}", id <= 0);
        return id;
    }

    public string StatusText => _status?.Text ?? "";

    private void SetStatus(string text, bool error = false)
    {
        if (_status != null)
        {
            _status.Text = text;
            _status.AddThemeColorOverride("font_color", error ? new Color(1f, 0.6f, 0.3f) : new Color(0.7f, 0.7f, 0.7f));
        }
    }
}
#endif
