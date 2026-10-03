#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using GUO.Assets;

/// <summary>
/// The Multi Editor's palette (ADR-0031): the client's own house tables (walls, floors, doors, stairs,
/// roofs, misc, teleporters) read in place, every static by tiledata name, favourites and recent tiles.
/// A static dragged from the UO Assets panel is accepted. Icons are scaled nearest.
/// </summary>
[Tool]
public partial class MultiPalette : VBoxContainer
{
    private const int Icon = 44;
    private const int MaxResults = 480;

    private EditorData _data;
    private HouseTables _tables;
    private OptionButton _mode;
    private OptionButton _group;
    private LineEdit _search;
    private ItemList _list;
    private Label _current;
    private SpinBox _hue;
    private Button _star;
    private List<ushort> _allWithArt;
    private readonly List<ushort> _shown = new();
    private readonly List<ushort> _favourites = new();
    private readonly List<ushort> _recent = new();
    private bool _loaded;

    public ushort TileId { get; private set; }
    public ushort Hue => (ushort)(_hue?.Value ?? 0);
    public int ShownCount => _shown.Count;
    public int GroupCount => _tables?.Groups.Count ?? 0;
    public IReadOnlyList<ushort> Favourites => _favourites;
    public IReadOnlyList<ushort> Recent => _recent;
    public string Mode => _mode?.GetItemText(_mode.Selected) ?? "";

    public event Action<ushort> TileChanged;
    public event Action<ushort> HueChanged;

    private static string StorePath => ProjectSettings.GlobalizePath("user://guo_multiedit_palette.json");

    public MultiPalette()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        CustomMinimumSize = new Vector2(300, 0);
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    internal void Attach(EditorData data, HouseTables tables)
    {
        _data = data;
        _tables = tables;
        EnsureUi();
        LoadStore();
        Rebuild();
    }

    private void EnsureUi()
    {
        if (_list != null)
        {
            return;
        }

        _mode = new OptionButton { TooltipText = "Where the tiles come from" };
        foreach (string m in new[] { "Client tables", "All statics", "Favourites", "Recent" })
        {
            _mode.AddItem(m);
        }

        _mode.ItemSelected += _ => Rebuild();
        AddChild(_mode);

        _group = new OptionButton { TooltipText = "A style from the client's walls, floors, doors, stairs, roofs, misc and teleporter tables", ClipText = true };
        _group.ItemSelected += _ => Fill();
        AddChild(_group);

        _search = new LineEdit { PlaceholderText = "search: name, or id (0x0007)", ClearButtonEnabled = true };
        _search.TextChanged += _ => Fill();
        AddChild(_search);

        _list = new ItemList
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            IconMode = ItemList.IconModeEnum.Top,
            MaxColumns = 0,
            SameColumnWidth = true,
            FixedIconSize = new Vector2I(Icon, Icon),
            FixedColumnWidth = Icon + 22,
            TextureFilter = TextureFilterEnum.Nearest,
            CustomMinimumSize = new Vector2(0, 260),
        };
        _list.ItemSelected += i => Choose(_shown[(int)i]);
        AddChild(_list);

        var row = new HBoxContainer();
        AddChild(row);
        _current = new Label { Text = "no tile", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        row.AddChild(_current);
        _star = new Button { Text = "Fav", TooltipText = "Add or remove this tile in the favourites", ToggleMode = false };
        _star.Pressed += () => ToggleFavourite();
        row.AddChild(_star);

        var hueRow = new HBoxContainer();
        AddChild(hueRow);
        hueRow.AddChild(new Label { Text = "hue" });
        _hue = new SpinBox { MinValue = 0, MaxValue = 3000, Step = 1, TooltipText = "Hue for what you draw (decimal). A multi record has no hue: it is kept in the editor's description only." };
        _hue.ValueChanged += v => HueChanged?.Invoke((ushort)v);
        hueRow.AddChild(_hue);
    }

    public void SetHue(ushort hue)
    {
        EnsureUi();
        _hue.Value = hue;
    }

    /// <summary>Rebuilds the group list and the tiles for the current mode.</summary>
    public void Rebuild()
    {
        EnsureUi();
        bool tables = _mode.Selected == 0;
        _group.Visible = tables;
        _group.Clear();
        if (tables && _tables != null)
        {
            string last = null;
            for (int i = 0; i < _tables.Groups.Count; i++)
            {
                PaletteGroup g = _tables.Groups[i];
                string label = g.Kind == last ? "    " + g.Name : $"{g.Kind}: {g.Name}";
                last = g.Kind;
                _group.AddItem(label, i);
            }
        }

        Fill();
    }

    private IEnumerable<ushort> AllWithArt()
    {
        if (_allWithArt == null)
        {
            _allWithArt = new List<ushort>();
            if (_data?.IsLoaded == true)
            {
                int n = Math.Min(_data.Files.TileData.StaticData.Length, 0x10000);
                for (int id = 0; id < n; id++)
                {
                    if (_data.HasArt(EditorData.LandCount + (uint)id))
                    {
                        _allWithArt.Add((ushort)id);
                    }
                }
            }
        }

        return _allWithArt;
    }

    private void Fill()
    {
        if (_list == null || _data == null || !_data.IsLoaded)
        {
            return;
        }

        string q = _search.Text.Trim();
        IEnumerable<ushort> source = _mode.Selected switch
        {
            0 => _tables != null && _group.ItemCount > 0 && _group.Selected >= 0
                ? _tables.Groups[_group.GetItemId(_group.Selected)].Items.Select(i => i.Id)
                : Enumerable.Empty<ushort>(),
            1 => AllWithArt(),
            2 => _favourites,
            _ => _recent,
        };

        ushort? exact = ParseId(q);
        _shown.Clear();
        foreach (ushort id in source.Distinct())
        {
            if (q.Length == 0 || (exact == null ? Name(id).Contains(q, StringComparison.OrdinalIgnoreCase) : id == exact))
            {
                _shown.Add(id);
                if (_shown.Count >= MaxResults)
                {
                    break;
                }
            }
        }

        _list.Clear();
        foreach (ushort id in _shown)
        {
            Image img = _data.ArtImage(EditorData.LandCount + id);
            int item = _list.AddItem($"{id:X4}", img != null ? ImageTexture.CreateFromImage(img) : null);
            string role = _tables?.RoleOf(id);
            _list.SetItemTooltip(item, $"0x{id:X4} {Name(id)}{(role != null ? "\n" + role : "")}");
        }
    }

    private string Name(ushort id) => _data.NameOf(EditorData.LandCount + id);

    private static ushort? ParseId(string q)
    {
        if (q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(q.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int h) && h is >= 0 and < 0x10000)
        {
            return (ushort)h;
        }

        return null;
    }

    /// <summary>Makes a tile the current one (a pick, a drop, the pipette).</summary>
    public void Choose(ushort id, bool remember = true)
    {
        EnsureUi();
        TileId = id;
        _current.Text = _data?.IsLoaded == true ? $"0x{id:X4}  {Name(id)}" : $"0x{id:X4}";
        if (remember)
        {
            _recent.Remove(id);
            _recent.Insert(0, id);
            if (_recent.Count > 40)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }

            SaveStore();
        }

        TileChanged?.Invoke(id);
    }

    public bool ToggleFavourite()
    {
        if (TileId == 0)
        {
            return false;
        }

        if (!_favourites.Remove(TileId))
        {
            _favourites.Add(TileId);
        }

        SaveStore();
        if (_mode.Selected == 2)
        {
            Fill();
        }

        return _favourites.Contains(TileId);
    }

    /// <summary>Selects a mode by its label ("All statics"), for F3 and the smoke check.</summary>
    public void ShowMode(string label, string search = "")
    {
        EnsureUi();
        for (int i = 0; i < _mode.ItemCount; i++)
        {
            if (_mode.GetItemText(i) == label)
            {
                _mode.Selected = i;
            }
        }

        _search.Text = search;
        Rebuild();
    }

    private void LoadStore()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (File.Exists(StorePath))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(StorePath));
                foreach (JsonElement e in doc.RootElement.GetProperty("favourites").EnumerateArray())
                {
                    _favourites.Add((ushort)e.GetInt32());
                }

                foreach (JsonElement e in doc.RootElement.GetProperty("recent").EnumerateArray())
                {
                    _recent.Add((ushort)e.GetInt32());
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] multi palette store: {ex.Message}");
        }
    }

    private void SaveStore()
    {
        try
        {
            File.WriteAllText(StorePath, JsonSerializer.Serialize(new { favourites = _favourites, recent = _recent }));
        }
        catch (Exception)
        {
        }
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) => MultiCanvas.DropId(data) != null;

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (MultiCanvas.DropId(data) is ushort id)
        {
            Choose(id);
        }
    }
}
#endif
