#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The AI dock's Queue tab (ADR-0028, data_formats section 21): post a request to an agent session
/// that is already running somewhere (Claude Code, Codex, ...) and see its replies as they arrive.
/// It drives tools/agent_queue, so the queue's rules apply here too (no secrets, size limits).
/// </summary>
[Tool]
public partial class AiQueueTab : VBoxContainer
{
    private const double PollSeconds = 2.5;

    private AiHub _hub;
    private QueueClient _queue;
    private LineEdit _to, _from, _text;
    private ItemList _list;
    private RichTextLabel _detail;
    private RichTextLabel _listeners, _leases;
    private Label _status, _db;
    private double _clock = PollSeconds;
    private bool _polling, _built, _stopped;
    private long _selected;
    private readonly List<QueueRequest> _requests = new();

    public AiQueueTab() : this(null)
    {
    }

    internal AiQueueTab(AiHub hub)
    {
        Name = "Queue";
        _hub = hub;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    /// <summary>The queue file in use (tests point it at a temporary one).</summary>
    public string Db
    {
        get => _queue?.Db ?? "";
        set
        {
            _queue ??= new QueueClient();
            _queue.Db = value;
            if (_db != null)
            {
                _db.Text = ShownDb;
            }
        }
    }

    private string ShownDb => string.IsNullOrEmpty(_queue?.Db) ? "(the tool's default file)" : _queue.Db;

    public IReadOnlyList<QueueRequest> Requests => _requests;
    public QueueStatus BusStatus { get; private set; }
    public string ListenersText => _listeners?.GetParsedText() ?? "";
    public string LeasesText => _leases?.GetParsedText() ?? "";

    /// <summary>Why the last post was refused, or null.</summary>
    public string LastError { get; private set; }

    /// <summary>The detail pane as plain text.</summary>
    public string DetailText => _detail?.GetParsedText() ?? "";

    public override void _Ready()
    {
        if (_built || _hub == null)
        {
            return;
        }

        _built = true;
        _queue ??= new QueueClient();
        var row = new HBoxContainer();
        AddChild(row);
        row.AddChild(new Label { Text = "To" });
        _to = new LineEdit { Text = "claude", CustomMinimumSize = new Vector2(90, 0), TooltipText = "The agent session's queue name (it runs: tail --as NAME)" };
        row.AddChild(_to);
        row.AddChild(new Label { Text = "From" });
        _from = new LineEdit { Text = "guo-editor", CustomMinimumSize = new Vector2(90, 0) };
        row.AddChild(_from);
        _text = new LineEdit { PlaceholderText = "A request for a running agent session. Never put a secret here.", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _text.TextSubmitted += t => _ = PostAsync(_to.Text, _from.Text, _text.Text);
        row.AddChild(_text);
        var post = new Button { Text = "Post" };
        post.Pressed += () => _ = PostAsync(_to.Text, _from.Text, _text.Text);
        row.AddChild(post);
        var cancel = new Button { Text = "Cancel request" };
        cancel.Pressed += () => _ = CancelSelectedAsync();
        row.AddChild(cancel);

        var info = new HBoxContainer();
        AddChild(info);
        info.AddChild(new Label { Text = "Queue file" });
        _db = new Label { Text = ShownDb, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        info.AddChild(_db);
        _status = new Label();
        info.AddChild(_status);

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        _list = new ItemList
        {
            CustomMinimumSize = new Vector2(300, 50),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        _list.ItemSelected += i =>
        {
            _selected = i >= 0 && i < _requests.Count ? _requests[(int)i].Id : 0;
            _ = ShowSelectedAsync();
        };
        split.AddChild(_list);
        _detail = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = false,
            SelectionEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(300, 50),
            FocusMode = FocusModeEnum.Click,
        };
        split.AddChild(_detail);

        // Keep the tab's minimum height unchanged: split the old 100 px
        // message-pane budget between messages and bus status. Both can scroll.
        var bus = new HSplitContainer
        {
            CustomMinimumSize = new Vector2(0, Math.Max(0, 50 - GetThemeConstant("separation"))),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 0.35f,
        };
        AddChild(bus);
        _listeners = new RichTextLabel { BbcodeEnabled = true, SelectionEnabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _leases = new RichTextLabel { BbcodeEnabled = true, SelectionEnabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bus.AddChild(_listeners);
        bus.AddChild(_leases);

        if (!_queue.Available)
        {
            SetStatus(_queue.Why, true);
        }
    }

    public override void _Process(double delta)
    {
        if (!_built || _stopped || !IsVisibleInTree())
        {
            return;
        }

        _clock += delta;
        if (_clock >= PollSeconds && !_polling)
        {
            _clock = 0;
            _ = RefreshAsync();
        }
    }

    /// <summary>Posts a request. The new id, or 0 with the reason shown in the status line.</summary>
    public async Task<long> PostAsync(string to, string from, string text)
    {
        _queue ??= new QueueClient();
        (long id, string error) = await _queue.PostAsync(to.Trim(), from.Trim(), text);
        LastError = id > 0 ? null : error;
        _hub?.Post(() =>
        {
            if (id > 0)
            {
                SetStatus($"posted request {id} to {to}");
                if (_text != null)
                {
                    _text.Text = "";
                }

                _selected = id;
            }
            else
            {
                SetStatus(error, true);
            }
        });
        if (id > 0)
        {
            await RefreshAsync();
        }

        return id;
    }

    /// <summary>Reads the newest requests (and the selected one's replies) and redraws.</summary>
    public async Task RefreshAsync()
    {
        _queue ??= new QueueClient();
        _polling = true;
        try
        {
            (List<QueueRequest> list, string error) = await _queue.ListAsync();
            (QueueStatus bus, string busError) = await _queue.StatusAsync();
            QueueRequest shown = null;
            string showError = null;
            if (list != null && _selected > 0)
            {
                (shown, showError) = await _queue.ShowAsync(_selected);
            }

            var ready = new TaskCompletionSource();
            _hub?.Post(() =>
            {
                try
                {
                    if (list == null)
                    {
                        SetStatus(error, true);
                        return;
                    }

                    _requests.Clear();
                    _requests.AddRange(list);
                    if (_list != null)
                    {
                        _list.Clear();
                        int select = -1;
                        for (int i = 0; i < list.Count; i++)
                        {
                            QueueRequest r = list[i];
                            string one = r.Text.Replace('\n', ' ');
                            _list.AddItem($"{r.Id}  {r.Kind}  {r.Status}  {r.From} > {r.To}  {(one.Length > 48 ? one[..48] + "..." : one)}");
                            if (r.Id == _selected)
                            {
                                select = i;
                            }
                        }

                        if (select >= 0)
                        {
                            _list.Select(select);
                        }
                    }

                    if (shown != null)
                    {
                        Detail(shown);
                    }

                    BusStatus = bus;
                    ShowBus(bus);
                    SetStatus(busError ?? showError ?? $"{list.Count} message(s)", busError != null || showError != null);
                }
                finally
                {
                    ready.SetResult();
                }
            });
            if (_hub != null)
            {
                await ready.Task;
            }
            else
            {
                _requests.Clear();
                _requests.AddRange(list ?? new List<QueueRequest>());
                BusStatus = bus;
            }

            LastShown = shown;
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Stops polling and kills what is in flight (dock closing, assembly reload).</summary>
    public void Shutdown()
    {
        _stopped = true;
        _queue?.Shutdown();
    }

    /// <summary>The request the detail pane last showed, with its replies.</summary>
    public QueueRequest LastShown { get; private set; }

    /// <summary>Selects a message by id, without taking or answering it.</summary>
    public async Task SelectAsync(long id)
    {
        _selected = id;
        await RefreshAsync();
    }

    private void ShowBus(QueueStatus bus)
    {
        if (_listeners == null || _leases == null)
        {
            return;
        }

        var listeners = new StringBuilder("[b]Addresses · last seen (UTC)[/b]\n");
        foreach (QueueAddress a in bus?.Addresses ?? new List<QueueAddress>())
        {
            listeners.Append($"{AiHub.Esc(a.Address)}  {AiHub.Esc(a.Project)}  {AiHub.Esc(a.LastSeen.Length == 0 ? "never" : a.LastSeen)}  {(a.Stale ? "stale" : "active")}\n");
        }

        if (bus?.Addresses.Count == 0) listeners.Append("No registered addresses.\n");
        var leases = new StringBuilder("[b]Resource leases · until (UTC)[/b]\n");
        foreach (QueueLease l in bus?.Leases ?? new List<QueueLease>())
        {
            leases.Append($"{AiHub.Esc(l.Resource)}  {AiHub.Esc(l.Holder)}  {AiHub.Esc(l.Until)}  {(l.Expired ? "expired" : "held")}\n{AiHub.Esc(l.Purpose)}\n");
        }

        if (bus?.Leases.Count == 0) leases.Append("No resource leases.\n");
        _listeners.Text = listeners.ToString();
        _leases.Text = leases.ToString();
    }

    private async Task ShowSelectedAsync()
    {
        if (_selected == 0)
        {
            return;
        }

        (QueueRequest r, string error) = await _queue.ShowAsync(_selected);
        _hub.Post(() =>
        {
            if (r != null)
            {
                Detail(r);
            }
            else
            {
                SetStatus(error, true);
            }
        });
    }

    private void Detail(QueueRequest r)
    {
        var sb = new StringBuilder();
        sb.Append($"[b]#{r.Id}[/b]  {AiHub.Esc(r.Kind)}  {AiHub.Esc(r.Status)}  {AiHub.Esc(r.From)} > {AiHub.Esc(r.To)}  {AiHub.Esc(r.Created)}\n{AiHub.Esc(r.Text)}\n");
        if (r.Replies.Count == 0)
        {
            sb.Append("\n[color=#7a7a7a]no reply yet[/color]");
        }

        foreach (QueueReply rep in r.Replies)
        {
            sb.Append($"\n[color=#8fb8e0]{AiHub.Esc(rep.From)}[/color] [color=#7a7a7a]{AiHub.Esc(rep.Created)}[/color]\n{AiHub.Esc(rep.Text)}\n");
        }

        _detail.Clear();
        _detail.AppendText(sb.ToString());
    }

    private async Task CancelSelectedAsync()
    {
        if (_selected == 0)
        {
            SetStatus("select a request first", true);
            return;
        }

        string error = await _queue.CancelAsync(_selected);
        _hub.Post(() => SetStatus(error ?? $"request {_selected} cancelled", error != null));
    }

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
