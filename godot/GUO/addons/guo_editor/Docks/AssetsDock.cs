#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

/// <summary>
/// The Assets dock: search and browse the client's art, land and statics.
/// Phase 0 of docs/editor_plan.md §4.6 — the other panels (gumps,
/// animations, hues, multis, cliloc, sounds, maps) join it in phase 1.
/// </summary>
/// <remarks>
/// UO has 0x4000 land and up to 0x10000 static ids, most of them empty, so
/// the list is paged: a search produces a list of ids, and only the page on
/// screen is decoded into icons.
/// </remarks>
[Tool]
public partial class AssetsDock : EditorDock
{
    private const int PageSize = 240;
    private const int IconSize = 44;

    private readonly EditorData _data;
    private readonly List<uint> _results = new();

    private OptionButton _kind;
    private LineEdit _search;
    private ItemList _list;
    private Label _status;
    private Button _prev, _next;
    private int _page;

    /// <summary>Raised with the art index (land 0..0x3FFF, statics after) the user picked.</summary>
    public event Action<uint> ArtSelected;

    public AssetsDock() : this(null)
    {
    }

    public AssetsDock(EditorData data)
    {
        _data = data;
        Name = "UOAssets";
        Title = "UO Assets";
        LayoutKey = "guo_assets";
        DefaultSlot = DockSlot.LeftUl;
        AvailableLayouts = DockLayout.Vertical | DockLayout.Floating;
        IconName = "ImageTexture";
    }

    public override void _Ready()
    {
        if (_list != null)
        {
            return;
        }

        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        var row = new HBoxContainer();
        root.AddChild(row);

        _kind = new OptionButton();
        _kind.AddItem("Statics");
        _kind.AddItem("Land");
        _kind.ItemSelected += _ => Refresh();
        row.AddChild(_kind);

        _search = new LineEdit
        {
            PlaceholderText = "id (0x0E75, 3701) or name",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClearButtonEnabled = true,
        };
        _search.TextSubmitted += _ => Refresh();
        row.AddChild(_search);

        _list = new ItemList
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            IconMode = ItemList.IconModeEnum.Top,
            MaxColumns = 0,
            SameColumnWidth = true,
            FixedIconSize = new Vector2I(IconSize, IconSize),
            FixedColumnWidth = IconSize + 20,
            // Pixel art is never filtered (CLAUDE.md rule 7); the icons scale.
            TextureFilter = TextureFilterEnum.Nearest,
            CustomMinimumSize = new Vector2(0, 240),
        };
        _list.ItemSelected += OnItemSelected;
        root.AddChild(_list);

        var pager = new HBoxContainer();
        root.AddChild(pager);
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

        if (_data != null)
        {
            if (_data.IsLoaded || _data.Error != null)
            {
                OnDataLoaded();
            }
            else
            {
                _data.Loaded += OnDataLoaded;
            }
        }
    }

    private void OnDataLoaded()
    {
        if (!IsInstanceValid(this))
        {
            return;
        }

        if (!_data.IsLoaded)
        {
            _status.Text = _data.Error ?? "client data not loaded";
            return;
        }

        Refresh();
    }

    /// <summary>
    /// Runs a search as if typed. An id selects that art; text filters by
    /// tiledata name. Returns the selected index, or null.
    /// </summary>
    public uint? Search(string text)
    {
        _search.Text = text ?? "";
        return Refresh();
    }

    private uint? Refresh()
    {
        _results.Clear();
        if (_data == null || !_data.IsLoaded)
        {
            return null;
        }

        bool land = _kind.Selected == 1;
        string q = _search.Text.Trim();

        if (TryParseId(q, out uint id))
        {
            // An id is an id in the chosen kind: 0x0E75 in Statics is static
            // 0x0E75, art index 0x4000 + 0x0E75, as the game numbers it.
            uint index = land ? id : EditorData.LandCount + id;
            for (uint i = land ? 0 : EditorData.LandCount; i < Limit(land); i++)
            {
                if (_data.HasArt(i))
                {
                    _results.Add(i);
                }
            }

            int at = _results.BinarySearch(index);
            ShowPage(at >= 0 ? at / PageSize : Math.Max(0, ~at) / PageSize);
            if (at >= 0)
            {
                int row = at % PageSize;
                _list.Select(row);
                _list.EnsureCurrentIsVisible();
                ArtSelected?.Invoke(index);
                return index;
            }

            _status.Text = $"{(land ? "land" : "static")} 0x{id:X4} has no art";
            return null;
        }

        for (uint i = land ? 0 : EditorData.LandCount; i < Limit(land); i++)
        {
            if (!_data.HasArt(i))
            {
                continue;
            }

            if (q.Length > 0 && _data.NameOf(i).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            _results.Add(i);
        }

        ShowPage(0);
        return null;
    }

    private uint Limit(bool land) =>
        land ? EditorData.LandCount : (uint)Math.Min(_data.Files.Arts.File.Entries.Length, (int)EditorData.LandCount + 0x10000);

    private void ShowPage(int page)
    {
        int pages = Math.Max(1, (_results.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(page, 0, pages - 1);
        _list.Clear();

        int start = _page * PageSize;
        int end = Math.Min(start + PageSize, _results.Count);
        for (int n = start; n < end; n++)
        {
            uint index = _results[n];
            Image img = _data.ArtImage(index);
            Texture2D icon = img != null ? ImageTexture.CreateFromImage(img) : null;
            uint id = index < EditorData.LandCount ? index : index - EditorData.LandCount;
            int item = _list.AddItem($"{id:X4}", icon);
            _list.SetItemMetadata(item, (long)index);
            _list.SetItemTooltip(item, $"0x{id:X4} {_data.NameOf(index)}");
        }

        _prev.Disabled = _page == 0;
        _next.Disabled = _page >= pages - 1;
        _status.Text = $"{_results.Count} tiles, page {_page + 1}/{pages}";
    }

    private void OnItemSelected(long item)
    {
        ArtSelected?.Invoke((uint)(long)_list.GetItemMetadata((int)item));
    }

    private static bool TryParseId(string text, out uint id)
    {
        id = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
        }

        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    public override void _ExitTree()
    {
        if (_data != null)
        {
            _data.Loaded -= OnDataLoaded;
        }
    }
}
#endif
