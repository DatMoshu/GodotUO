#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;

/// <summary>
/// The suggestions under an <see cref="AssetField"/> while it is typed in:
/// picture, id and name per row, best match first. It is a Control on the
/// field's own window, not a popup window, so the field keeps the keyboard
/// and the typing goes on (as F3's overlay does). Pictures are decoded a few
/// per frame after the rows appear, so a keystroke never waits on a large gump.
/// </summary>
[Tool]
public partial class AssetSuggest : PanelContainer
{
    private const int MaxRows = 40;
    private const int VisibleRows = 9;
    private const double ThumbBudgetMs = 6;

    private readonly AssetField _field;
    private Tree _tree;
    private Label _footer;
    private readonly List<(TreeItem Item, int Id)> _rows = new();
    private int _thumbsDone;
    private string _pending;

    public AssetSuggest()
    {
    }

    public AssetSuggest(AssetField field)
    {
        _field = field;
        TopLevel = true;
        ZIndex = 100;
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    /// <summary>A row was clicked.</summary>
    public event Action<int> Picked;

    public bool IsOpen => IsInsideTree() && Visible;

    /// <summary>The highlighted id, or null.</summary>
    public int? Current => _tree?.GetSelected() is TreeItem t ? (int)t.GetMetadata(0).AsInt32() : null;

    /// <summary>The ids shown, for the smoke check.</summary>
    public IEnumerable<int> Ids
    {
        get
        {
            foreach (var (_, id) in _rows)
            {
                yield return id;
            }
        }
    }

    private static float UiScale => EditorInterface.Singleton.GetEditorScale();

    private void Build()
    {
        if (_tree != null)
        {
            return;
        }

        AddThemeStyleboxOverride("panel", _field.GetThemeStylebox("panel", "PopupMenu"));
        var box = new VBoxContainer();
        AddChild(box);
        float s = UiScale;
        _tree = new Tree
        {
            Columns = 3,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            FocusMode = FocusModeEnum.None,
            ScrollHorizontalEnabled = false,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            // Art is never filtered (AGENTS.md rule 7).
            TextureFilter = TextureFilterEnum.Nearest,
        };
        _tree.SetColumnExpand(0, false);
        _tree.SetColumnCustomMinimumWidth(0, (int)(118 * s));
        _tree.SetColumnExpand(1, true);
        _tree.SetColumnClipContent(1, true);
        _tree.SetColumnExpand(2, false);
        _tree.SetColumnCustomMinimumWidth(2, (int)(64 * s));
        _tree.ItemMouseSelected += (_, button) =>
        {
            if ((MouseButton)(int)button == MouseButton.Left && Current is int id)
            {
                Picked?.Invoke(id);
            }
        };
        box.AddChild(_tree);

        _footer = new Label { ClipText = true };
        _footer.AddThemeColorOverride("font_color", _field.GetThemeColor("font_placeholder_color", "LineEdit"));
        box.AddChild(_footer);
    }

    /// <summary>Shows the matches for <paramref name="text"/>; with <paramref name="always"/>, also when nothing is typed.</summary>
    public void Open(string text, bool always = false)
    {
        if (!always && string.IsNullOrWhiteSpace(text))
        {
            Close();
            return;
        }

        Window host = _field.GetWindow();
        if (host == null)
        {
            return;
        }

        if (GetParent() != host)
        {
            GetParent()?.RemoveChild(this);
            host.AddChild(this);
        }

        Build();
        Visible = true;

        // The query runs on the next frame: a fast typist gets one query, not one per key.
        _pending = text ?? "";
        SetProcess(true);
        SetProcessInput(true);
    }

    /// <summary>Hides the list. It stays on the window, hidden, so a close from its own _Process never removes a busy node.</summary>
    public void Close()
    {
        _pending = null;
        Visible = false;
        SetProcess(false);
        SetProcessInput(false);
    }

    /// <summary>Frees the overlay with its field.</summary>
    public void Discard()
    {
        Close();
        QueueFree();
    }

    private void Fill(string text)
    {
        AssetCatalog cat = AssetCatalog.Of(_field.DataSource);
        _tree.Clear();
        _rows.Clear();
        _thumbsDone = 0;
        TreeItem root = _tree.CreateItem();
        if (cat == null || !cat.Ready)
        {
            _footer.Text = "The client data is still loading.";
            Place(1);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        List<AssetHit> hits = cat.Query(_field.Kind, text, MaxRows);
        int[] recent = AssetCatalog.Recent(_field.Kind);
        Color muted = _field.GetThemeColor("font_placeholder_color", "LineEdit");
        foreach (AssetHit hit in hits)
        {
            TreeItem item = _tree.CreateItem(root);
            item.SetMetadata(0, hit.Id);
            item.SetText(0, AssetCatalog.Format(_field.Kind, hit.Id));
            string name = hit.Name.Length > 0 ? hit.Name : "(no name in the data)";
            item.SetText(1, name);
            item.SetTooltipText(1, name);
            if (hit.Name.Length == 0)
            {
                item.SetCustomColor(1, muted);
            }

            item.SetText(2, _field.Kind == AssetPickKind.Cliloc ? $"0x{hit.Id:X}" : hit.Id.ToString());
            item.SetCustomColor(2, muted);
            item.SetTextAlignment(2, HorizontalAlignment.Right);
            if (Array.IndexOf(recent, hit.Id) >= 0 && string.IsNullOrWhiteSpace(text))
            {
                item.SetSuffix(0, "  recent");
            }

            _rows.Add((item, hit.Id));
        }

        if (_rows.Count > 0)
        {
            _rows[0].Item.Select(0);
        }

        string noun = AssetCatalog.Noun(_field.Kind);
        _footer.Text = _rows.Count == 0
            ? $"No {noun} matches. Try a name, a hex id (0x0E75) or a decimal id."
            : $"Enter picks the highlighted {noun}. Ctrl+Enter browses all of them.";
        if (stopwatch.ElapsedMilliseconds > 250)
        {
            GD.Print($"[GUO editor] {noun} suggestions for \"{text}\" took {stopwatch.ElapsedMilliseconds} ms");
        }

        Place(Math.Max(1, Math.Min(_rows.Count, VisibleRows)));
    }

    /// <summary>Under the field, or over it when the window has no room below.</summary>
    private void Place(int rows)
    {
        float s = UiScale;
        bool thumbs = AssetCatalog.HasThumbs(_field.Kind);
        float rowH = (thumbs ? 34 : 24) * s;
        Rect2 at = _field.GetGlobalRect();
        Vector2 win = _field.GetWindow().Size;
        float w = Math.Min(Math.Max(at.Size.X, 440 * s), win.X - 8);
        float h = rows * rowH + 34 * s;
        float x = Math.Clamp(at.Position.X, 4, Math.Max(4, win.X - w - 4));
        float y = at.End.Y + 2;
        if (y + h > win.Y - 4 && at.Position.Y - h - 2 > 4)
        {
            y = at.Position.Y - h - 2;
        }

        Position = new Vector2(x, y);
        Size = new Vector2(w, h);
        CustomMinimumSize = Size;
    }

    public override void _Process(double delta)
    {
        if (!IsOpen || !_field.IsVisibleInTree())
        {
            Close();
            return;
        }

        if (_pending != null)
        {
            string text = _pending;
            _pending = null;
            Fill(text);
        }

        // Follow the field when its dock scrolls.
        Rect2 at = _field.GetGlobalRect();
        if (Position.Y >= at.Position.Y && Math.Abs(Position.Y - at.End.Y - 2) > 0.5f)
        {
            Place(Math.Max(1, Math.Min(_rows.Count, VisibleRows)));
        }

        AssetCatalog cat = AssetCatalog.Of(_field.DataSource);
        if (cat == null || _thumbsDone >= _rows.Count || !AssetCatalog.HasThumbs(_field.Kind))
        {
            return;
        }

        int size = (int)((_field.Kind == AssetPickKind.Hue ? 64 : 30) * UiScale);
        var sw = Stopwatch.StartNew();
        while (_thumbsDone < _rows.Count && sw.Elapsed.TotalMilliseconds < ThumbBudgetMs)
        {
            var (item, id) = _rows[_thumbsDone++];
            if (GodotObject.IsInstanceValid(item) && cat.Thumb(_field.Kind, id) is Texture2D tex)
            {
                item.SetIcon(0, tex);
                item.SetIconMaxWidth(0, size);
            }
        }
    }

    public override void _Input(InputEvent e)
    {
        if (!IsOpen || e is not InputEventMouseButton { Pressed: true } mb)
        {
            return;
        }

        // A click anywhere but here or on the field closes the list (and goes where it was meant to).
        if (!GetGlobalRect().HasPoint(mb.Position) && !_field.GetGlobalRect().HasPoint(mb.Position))
        {
            Close();
        }
    }

    /// <summary>Moves the highlight by <paramref name="step"/> rows.</summary>
    public void Move(int step)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        int at = 0;
        if (_tree.GetSelected() is TreeItem cur)
        {
            at = _rows.FindIndex(r => r.Item == cur);
        }

        at = Math.Clamp(at + step, 0, _rows.Count - 1);
        _rows[at].Item.Select(0);
        _tree.ScrollToItem(_rows[at].Item);
    }
}
#endif
