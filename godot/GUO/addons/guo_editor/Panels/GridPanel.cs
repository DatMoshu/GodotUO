#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// A searchable, paged list of ids: the shape of most Assets panels. UO id
/// spaces are large and sparse, so a search produces a list of ids and only
/// the page on screen is decoded.
/// </summary>
[Tool]
public abstract partial class GridPanel : AssetPanel
{
    /// <summary>Items decoded per page.</summary>
    protected virtual int PageSize => 240;

    private readonly List<int> _results = new();
    private ItemList _list;
    private Label _status;
    private Button _prev, _next;
    private int _page;
    private bool _pending;

    protected LineEdit SearchBox { get; private set; }

    /// <summary>The id last picked, or null.</summary>
    public int? Selected { get; private set; }

    /// <summary>Icon edge in pixels; 0 makes a text list.</summary>
    protected virtual int IconSize => 44;

    private int EffectiveIcon => IconSize == 0 ? 0 : (_cell > 0 ? _cell : IconSize);

    private int _cell;

    /// <summary>The icon edge the UO Assets view asks for (text lists ignore it).</summary>
    public void SetCellSize(int px)
    {
        _cell = px;
        if (_list != null && _list.IconMode == ItemList.IconModeEnum.Top)
        {
            _list.FixedIconSize = new Vector2I(px, px);
            _list.FixedColumnWidth = px + 20;
            if (Data != null && Data.IsLoaded && _results.Count > 0)
            {
                ShowPage(_page);
            }
        }
    }

    protected virtual string Placeholder => "id (0x0E75, 3701) or name";

    /// <summary>Every id that exists, ascending.</summary>
    protected abstract IEnumerable<int> Ids();

    /// <summary>The label in the list.</summary>
    protected abstract string Caption(int id);

    protected virtual string Tooltip(int id) => Caption(id);

    protected virtual Image Icon(int id) => null;

    protected virtual bool Matches(int id, string query) =>
        Tooltip(id).Contains(query, StringComparison.OrdinalIgnoreCase);

    protected abstract Inspection Describe(int id);

    /// <summary>Extra controls before the search box (a kind switch, a language).</summary>
    protected virtual void BuildToolbar(HBoxContainer bar)
    {
    }

    public override void _Ready() => EnsureUi();

    protected void EnsureUi()
    {
        if (_list != null)
        {
            return;
        }

        SizeFlagsVertical = SizeFlags.ExpandFill;

        // Scanning an id space is not free (every sound, every multi); a panel
        // does it the first time its tab is shown, not at editor start.
        VisibilityChanged += () =>
        {
            if (_pending && IsVisibleInTree())
            {
                _pending = false;
                Refresh();
            }
        };

        var bar = new HBoxContainer();
        AddChild(bar);
        BuildToolbar(bar);

        SearchBox = new LineEdit
        {
            PlaceholderText = Placeholder,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClearButtonEnabled = true,
        };
        SearchBox.TextSubmitted += _ => Refresh();
        bar.AddChild(SearchBox);

        bool icons = EffectiveIcon > 0;
        _list = new ItemList
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            IconMode = icons ? ItemList.IconModeEnum.Top : ItemList.IconModeEnum.Left,
            MaxColumns = icons ? 0 : 1,
            SameColumnWidth = icons,
            FixedIconSize = icons ? new Vector2I(EffectiveIcon, EffectiveIcon) : Vector2I.Zero,
            FixedColumnWidth = icons ? EffectiveIcon + 20 : 0,
            // Pixel art is never filtered (AGENTS.md rule 7); icons are scaled.
            TextureFilter = TextureFilterEnum.Nearest,
            CustomMinimumSize = new Vector2(0, 240),
        };
        _list.ItemSelected += item => Pick((int)(long)_list.GetItemMetadata((int)item));
        AddChild(_list);

        var pager = new HBoxContainer();
        AddChild(pager);
        _prev = new Button { Text = "<" };
        _prev.Pressed += () => ShowPage(_page - 1);
        _next = new Button { Text = ">" };
        _next.Pressed += () => ShowPage(_page + 1);
        _status = new Label
        {
            Text = "loading client data...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
        };
        pager.AddChild(_prev);
        pager.AddChild(_status);
        pager.AddChild(_next);
    }

    public override void OnDataLoaded()
    {
        EnsureUi();
        if (IsVisibleInTree())
        {
            Refresh();
        }
        else
        {
            _pending = true;
        }
    }

    public override int? Search(string text)
    {
        EnsureUi();
        SearchBox.Text = text ?? "";
        _pending = false;
        return Refresh();
    }

    /// <summary>Runs the search box again, as if Enter were pressed.</summary>
    protected int? Refresh()
    {
        _results.Clear();
        if (Data == null || !Data.IsLoaded)
        {
            return null;
        }

        string q = SearchBox.Text.Trim();
        if (TryParseId(q, out int id))
        {
            _results.AddRange(Ids());
            int at = _results.BinarySearch(id);
            ShowPage(at >= 0 ? at / PageSize : Math.Max(0, ~at) / PageSize);
            if (at >= 0)
            {
                _list.Select(at % PageSize);
                _list.EnsureCurrentIsVisible();
                Pick(id);
                return id;
            }

            _status.Text = $"0x{id:X4} ({id}) does not exist";
            return null;
        }

        foreach (int i in Ids())
        {
            if (q.Length == 0 || Matches(i, q))
            {
                _results.Add(i);
            }
        }

        ShowPage(0);
        if (q.Length > 0 && _results.Count > 0)
        {
            _list.Select(0);
            Pick(_results[0]);
            return _results[0];
        }

        return null;
    }

    private void ShowPage(int page)
    {
        int pages = Math.Max(1, (_results.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(page, 0, pages - 1);
        _list.Clear();

        int start = _page * PageSize;
        int end = Math.Min(start + PageSize, _results.Count);
        for (int n = start; n < end; n++)
        {
            int id = _results[n];
            Texture2D icon = null;
            if (EffectiveIcon > 0)
            {
                Image img = Icon(id);
                icon = img != null ? ImageTexture.CreateFromImage(img) : null;
            }

            int item = _list.AddItem(Caption(id), icon);
            _list.SetItemMetadata(item, (long)id);
            _list.SetItemTooltip(item, Tooltip(id));
        }

        _prev.Disabled = _page == 0;
        _next.Disabled = _page >= pages - 1;
        _status.Text = $"{_results.Count} found, page {_page + 1}/{pages}";
    }

    private void Pick(int id)
    {
        Selected = id;
        Raise(Describe(id));
    }

    /// <summary>Drops any cached id list; the next <see cref="Ids"/> call builds it again.</summary>
    protected virtual void ForgetIds()
    {
    }

    /// <summary>
    /// After the asset overlay changed (an import or a revert): rebuild the
    /// id list, redraw the page on show and inspect the selection again.
    /// </summary>
    public void OnAssetsChanged()
    {
        if (Data == null || !Data.IsLoaded || _list == null)
        {
            return;
        }

        ForgetIds();
        int page = _page;
        int? selected = Selected;
        Refresh();
        ShowPage(page);
        Selected = selected;
        Reinspect();
    }

    /// <summary>Inspects the selection again, after a toolbar setting changed.</summary>
    protected void Reinspect()
    {
        if (Selected.HasValue)
        {
            Raise(Describe(Selected.Value));
        }
    }
}
#endif
