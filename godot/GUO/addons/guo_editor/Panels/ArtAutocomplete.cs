#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>F3's art provider and ranking, anchored to an asset search field.</summary>
[Tool]
public partial class ArtAutocomplete : PanelContainer
{
    private LineEdit _box;
    private SearchIndex _index;
    private ItemList _list;
    private Label _status;
    private Action<bool, int> _pick;
    private readonly List<SearchEntry> _rows = new();
    private double _delay = -1;
    private bool _wasReady;
    internal IReadOnlyList<SearchEntry> Results => _rows;

    public void Attach(LineEdit box, EditorData data, Action<bool, int> pick)
    {
        _box = box; _pick = pick;
        TopLevel = true; ZIndex = 100; Visible = false;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color("25272b"),
            BorderColor = new Color("59616e"), BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderWidthTop = 1, BorderWidthBottom = 1, ContentMarginLeft = 10,
            ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8 });
        _index = new SearchIndex();
        _index.Add(new UoArtProvider(new SearchContext { Data = data }));
        var column = new VBoxContainer(); AddChild(column);
        _status = new Label { Text = "Land & statics · ↑↓ choose · Enter apply · Esc close" };
        column.AddChild(_status);
        _list = new ItemList { CustomMinimumSize = new Vector2(400, 270),
            FixedIconSize = new Vector2I(32, 32), TextureFilter = TextureFilterEnum.Nearest,
            SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
        column.AddChild(_list);
        _list.ItemClicked += (i, _, button) => { if (button == (long)MouseButton.Left) Choose((int)i); };
        box.TextChanged += _ => { _delay = 0.12; };
        box.FocusEntered += () => { if (box.Text.Length > 0) _delay = 0; };
        box.GuiInput += e => HandleKey(e);
    }

    public override void _Process(double delta)
    {
        _index?.Step(2);
        if (_index == null || _box == null) return;
        if (!_box.IsVisibleInTree()) { Hide(); return; }
        if (!_wasReady && _index.Ready) { _wasReady = true; if (_box.HasFocus()) _delay = 0; }
        if (_delay >= 0)
        {
            _delay -= delta;
            if (_delay < 0) RefreshSuggestions();
        }
        if (Visible)
        {
            Vector2 viewport = GetViewportRect().Size;
            Size = new Vector2(Math.Min(680, viewport.X), Math.Min(600, viewport.Y));
            GlobalPosition = new Vector2(Math.Clamp(_box.GlobalPosition.X, 0, Math.Max(0, viewport.X - Size.X)),
                Math.Clamp(_box.GlobalPosition.Y + _box.Size.Y, 0, Math.Max(0, viewport.Y - Size.Y)));
        }
    }

    public void RefreshSuggestions()
    {
        if (!_box.HasFocus() || string.IsNullOrWhiteSpace(_box.Text)) { Hide(); return; }
        _rows.Clear(); _list.Clear();
        foreach (var group in _index.Query(_box.Text))
            foreach (var (entry, _) in group.Items)
            {
                using Image img = entry.Thumb?.Invoke();
                _list.AddItem($"{entry.Kind}  {entry.Title}  {entry.Hint}", img == null ? null : ImageTexture.CreateFromImage(img));
                _rows.Add(entry);
            }
        _status.Text = _rows.Count > 0 ? "Land & statics · ↑↓ choose · Enter apply · Esc close"
            : _index.Ready ? "No matching land or statics" : "Indexing land & statics…";
        if (_rows.Count > 0) _list.Select(0);
        Show();
    }

    private void HandleKey(InputEvent e)
    {
        if (!Visible || e is not InputEventKey k || !k.Pressed) return;
        var selected = _list.GetSelectedItems();
        int at = selected.Length > 0 ? selected[0] : 0;
        if (k.Keycode is Key.Down or Key.Up)
        {
            if (_rows.Count > 0) { _list.Select(Math.Clamp(at + (k.Keycode == Key.Down ? 1 : -1), 0, _rows.Count - 1)); _list.EnsureCurrentIsVisible(); }
        }
        else if (k.Keycode is Key.Enter or Key.KpEnter) Choose(at);
        else if (k.Keycode == Key.Escape) Hide();
        else return;
        AcceptEvent();
    }

    public override void _Input(InputEvent e)
    {
        if (Visible && e is InputEventMouseButton { Pressed: true } &&
            !GetGlobalRect().HasPoint(GetGlobalMousePosition()) && !_box.GetGlobalRect().HasPoint(_box.GetGlobalMousePosition())) Hide();
    }

    private void Choose(int at)
    {
        if (at < 0 || at >= _rows.Count) return;
        var entry = _rows[at];
        if (SearchQuery.TryNumber(entry.Key[(entry.Key.IndexOf(':') + 1)..], out long id)) _pick(entry.Kind == "Land", (int)id);
        _box.GrabFocus(); _delay = -1; Hide();
    }
}
#endif
