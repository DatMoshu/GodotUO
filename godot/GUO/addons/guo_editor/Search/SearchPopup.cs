#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// The F3 search: a centred overlay on the editor with a text field and a
/// result list that follows the typing. Up and Down move, Enter runs, Escape
/// closes. Results are grouped by kind; what was run lately ranks higher.
/// It is a Control on the editor's base control, not a native window, so it
/// opens at once and never takes the desktop's focus.
/// </summary>
/// <remarks>
/// F3 is the editor shortcut <c>guo_editor/search</c>: rebind it under Editor
/// Settings > Shortcuts. Indexing the UO data happens in <see cref="_Process"/>,
/// a few milliseconds a frame, because the loaders are single threaded.
/// </remarks>
[Tool]
public partial class SearchPopup : Control
{
    public const string ShortcutPath = "guo_editor/search";
    private const double IndexBudgetMs = 3;
    private const int ThumbRows = 14;

    private SearchContext _ctx;
    private SearchIndex _index;
    private LineEdit _box;
    private ItemList _list;
    private Label _status;
    private Control _panel;
    private ColorRect _dim;
    private readonly List<SearchEntry> _rows = new();
    private List<SearchGroup> _groups = new();
    private bool _ready;

    public SearchPopup()
    {
        Name = "GuoSearch";
    }

    /// <summary>The index, for the smoke check.</summary>
    public SearchIndex Index => _index;

    /// <summary>The groups the last query produced.</summary>
    public IReadOnlyList<SearchGroup> Groups => _groups;

    public string Query => _box?.Text ?? "";

    public bool IsOpen => Visible;

    public ItemList List => _list;

    /// <summary>
    /// Makes the popup, puts it on the editor, and registers F3. One call
    /// from the plugin; <see cref="Remove"/> undoes it before an assembly reload.
    /// </summary>
    public static SearchPopup Install(SearchContext ctx)
    {
        var popup = new SearchPopup();
        popup.Setup(ctx);
        GodotUi.Base.AddChild(popup);
        return popup;
    }

    public static void Remove(SearchPopup popup)
    {
        if (popup != null && GodotObject.IsInstanceValid(popup))
        {
            popup.GetParent()?.RemoveChild(popup);
            popup.QueueFree();
        }
    }

    private void Setup(SearchContext ctx)
    {
        _ctx = ctx;
        _index = new SearchIndex();
        var menus = new GodotMenuProvider();
        _index.Add(new GuoProvider(ctx));
        _index.Add(new AiProvider(ctx));
        _index.Add(new PlacesProvider(ctx));
        _index.Add(menus);
        _index.Add(new GodotScreenProvider());
        _index.Add(new GodotPanelProvider());
        _index.Add(new GodotSettingsProvider(menus));
        _index.Add(new UoIdProvider(ctx));
        _index.Add(new UoArtProvider(ctx));
        _index.Add(new UoHueProvider(ctx));
        _index.Add(new UoSoundProvider(ctx));
        _index.Add(new UoClilocProvider(ctx));   // last: the biggest table
        RegisterShortcut();
    }

    private static void RegisterShortcut()
    {
        EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
        if (settings.HasShortcut(ShortcutPath))
        {
            return;
        }

        var key = new InputEventKey { Keycode = Key.F3 };
        var shortcut = new Shortcut { Events = new Godot.Collections.Array { key } };
        settings.AddShortcut(ShortcutPath, shortcut);
    }

    public override void _Ready()
    {
        if (_box != null || _index == null)
        {
            return;
        }

        ZIndex = 100;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.Nearest;

        _dim = new ColorRect { Color = new Color(0, 0, 0, 0.35f) };
        ColorRect dim = _dim;
        dim.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true })
            {
                Close();
            }
        };
        AddChild(dim);

        _panel = new PanelContainer { ClipContents = true };
        AddChild(_panel);

        var margin = new MarginContainer();
        foreach (string side in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride("margin_" + side, 10);
        }

        _panel.AddChild(margin);
        var box = new VBoxContainer();
        margin.AddChild(box);

        _box = new LineEdit
        {
            PlaceholderText = "Search commands, settings, menus and UO data: backpack, 0x0E75, grid, 1434 1699 ...",
            ClearButtonEnabled = true,
            CustomMinimumSize = new Vector2(0, 34),
        };
        _box.TextChanged += _ => Refresh();
        _box.GuiInput += OnBoxInput;
        box.AddChild(_box);

        _list = new ItemList
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            // Pixel art is never filtered (AGENTS.md rule 7); thumbnails are art.
            TextureFilter = TextureFilterEnum.Nearest,
            FixedIconSize = new Vector2I(28, 28),
            AllowReselect = true,
            CustomMinimumSize = new Vector2(0, 120),
        };
        _list.ItemActivated += i => ActivateRow((int)i);
        _list.GuiInput += OnListInput;
        box.AddChild(_list);

        if (GetParent() is Control parent)
        {
            parent.Resized += Layout;
        }

        _status = new Label { Modulate = new Color(1, 1, 1, 0.6f) };
        box.AddChild(_status);
    }

    /// <summary>Fills the editor and centres the panel in its upper part, by hand: the parent is not a container.</summary>
    private void Layout()
    {
        if (_panel == null || GetParent() is not Control parent)
        {
            return;
        }

        Position = Vector2.Zero;
        Size = parent.Size;
        _dim.Position = Vector2.Zero;
        _dim.Size = parent.Size;
        var size = new Vector2(Math.Min(1500, parent.Size.X - 80), Math.Min(1000, parent.Size.Y - 120));
        _panel.Size = size;
        _panel.Position = new Vector2((parent.Size.X - size.X) / 2, Math.Max(40, (parent.Size.Y - size.Y) / 3));
    }

    public override void _Notification(int what)
    {
        if (what == NotificationParented || what == NotificationResized || what == NotificationVisibilityChanged)
        {
            Layout();
        }
    }

    public override void _ExitTree()
    {
        if (GetParent() is Control parent)
        {
            parent.Resized -= Layout;
        }

        if (_box != null)
        {
            _box.GuiInput -= OnBoxInput;
        }

        _rows.Clear();
        _groups.Clear();
        _index = null;
        _ctx = null;
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (EditorInterface.Singleton.GetEditorSettings().IsShortcut(ShortcutPath, k))
            {
                if (Visible)
                {
                    Close();
                }
                else
                {
                    Open();
                }

                GetViewport().SetInputAsHandled();
            }
            else if (Visible && k.Keycode == Key.Escape)
            {
                Close();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_index == null || _ready)
        {
            return;
        }

        _index.Step(IndexBudgetMs);
        if (_index.Ready)
        {
            _ready = true;
            if (Visible)
            {
                Refresh();
            }
        }
        else if (Visible)
        {
            UpdateStatus();
        }
    }

    public void Open(string text = "")
    {
        if (_box == null)
        {
            _Ready();
        }

        _index.Refresh();
        Visible = true;
        Layout();
        _box.Text = text;
        Refresh();
        _box.GrabFocus();
        _box.SelectAll();
    }

    public void Close()
    {
        Visible = false;
    }

    /// <summary>Types a query (as the smoke check and tour do) and returns the groups.</summary>
    public List<SearchGroup> SetQuery(string text)
    {
        if (_box == null)
        {
            _Ready();
        }

        _box.Text = text;
        Refresh();
        return _groups;
    }

    private void Refresh()
    {
        _groups = _index.Query(_box.Text);
        _list.Clear();
        _rows.Clear();
        int thumbs = 0;
        foreach (SearchGroup g in _groups)
        {
            int header = _list.AddItem(g.Kind.ToUpperInvariant());
            _list.SetItemSelectable(header, false);
            _list.SetItemCustomFgColor(header, new Color(0.62f, 0.78f, 1f));
            _rows.Add(null);

            foreach (var (entry, _) in g.Items)
            {
                Texture2D icon = null;
                if (entry.Thumb != null && thumbs < ThumbRows)
                {
                    thumbs++;
                    Image img = entry.Thumb();
                    icon = img != null ? ImageTexture.CreateFromImage(img) : null;
                }

                int row = _list.AddItem(entry.Hint.Length > 0 ? $"{entry.Title}     {entry.Hint}" : entry.Title, icon);
                _list.SetItemTooltip(row, $"{entry.Kind}: {entry.Title}\n{entry.Hint}");
                _rows.Add(entry);
            }
        }

        _list.GetVScrollBar().Value = 0;
        Select(FirstSelectable(0, 1));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        string indexing = _index.Ready ? "" : $"   indexing... {(int)(_index.Progress * 100)}% ({_index.Pending})";
        _status.Text = (_rows.Count == 0 ? "no matches" : "Up/Down move, Enter runs, Esc closes") + indexing;
    }

    private int FirstSelectable(int from, int step)
    {
        for (int i = from; i >= 0 && i < _rows.Count; i += step)
        {
            if (_rows[i] != null)
            {
                return i;
            }
        }

        return -1;
    }

    private void Select(int row)
    {
        if (row < 0)
        {
            _list.DeselectAll();
            return;
        }

        _list.Select(row);
        _list.EnsureCurrentIsVisible();
    }

    private int Current()
    {
        var sel = _list.GetSelectedItems();
        return sel.Length > 0 ? sel[0] : -1;
    }

    private void Move(int step)
    {
        int cur = Current();
        int next = FirstSelectable(cur < 0 ? (step > 0 ? 0 : _rows.Count - 1) : cur + step, step);
        if (next >= 0)
        {
            Select(next);
        }
    }

    private void OnBoxInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k)
        {
            return;
        }

        switch (k.Keycode)
        {
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.Pagedown: for (int i = 0; i < 8; i++) { Move(1); } break;
            case Key.Pageup: for (int i = 0; i < 8; i++) { Move(-1); } break;
            case Key.Enter:
            case Key.KpEnter: ActivateRow(Current()); break;
            case Key.Escape: Close(); break;
            default: return;
        }

        _box.AcceptEvent();
    }

    private void OnListInput(InputEvent e)
    {
        // Typing while the list has focus goes back to the field.
        if (e is InputEventKey { Pressed: true, Unicode: > 31 })
        {
            _box.GrabFocus();
        }
    }

    private void ActivateRow(int row)
    {
        if (row >= 0 && row < _rows.Count && _rows[row] != null)
        {
            Activate(_rows[row]);
        }
    }

    /// <summary>Runs an entry: closes the popup, remembers the choice, then runs it (a frame later, so the editor has the focus back).</summary>
    public void Activate(SearchEntry entry, bool now = false)
    {
        Close();
        _index.History.Record(entry.Key);
        if (now)
        {
            entry.Run();
        }
        else
        {
            Callable.From(() =>
            {
                try
                {
                    entry.Run();
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[GUO editor] F3 {entry.Kind} '{entry.Title}' failed: {ex.GetType().Name}: {ex.Message}");
                    SearchContext.Toast($"{entry.Title}: {ex.Message}", EditorToaster.Severity.Error);
                }
            }).CallDeferred();
        }
    }
}
#endif
