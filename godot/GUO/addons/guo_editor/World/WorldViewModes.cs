#if TOOLS
namespace GUO.Editor;

using System;
using System.Linq;
using Godot;
using GUO.Game.GameObjects;

/// <summary>The World tab's render modes and map layers (ADR-0027): the View and Map layers menus, the legend chip, the map tools.</summary>
public partial class WorldView
{
    private PopupMenu _viewPopup;
    private Button _scenePackButton;

    private void BuildModeRow()
    {
        var row = new HBoxContainer();
        AddChild(row);
        _legacyModes = row;

        _viewMenu = Menu(row, "View", "Render modes: recolour the world to show height, walkability, reachability, types, IDs, the land mesh, problems, project changes");
        _viewPopup = _viewMenu.GetPopup();
        _viewPopup.AddRadioCheckItem("Off", 0);
        _viewPopup.SetItemChecked(0, true);
        string[] names = _modeNode == null ? WorldModeList.All().Select(m => m.Name).ToArray() : _modeNode.ModeNames.ToArray();
        for (int i = 0; i < names.Length; i++)
        {
            _viewPopup.AddRadioCheckItem(names[i], i + 1);
            // ED6: what the colours mean, before the mode is chosen.
            IWorldMode mode = _modeNode?.ModeNamed(names[i]) ?? WorldModeList.All().FirstOrDefault(m => m.Name == names[i]);
            _viewPopup.SetItemTooltip(_viewPopup.GetItemIndex(i + 1), mode?.Meaning ?? "");
        }

        _viewPopup.AddSeparator();
        _viewPopup.AddCheckItem("Solid fill", 100);
        _viewPopup.IdPressed += id =>
        {
            if (id == 100)
            {
                bool solid = !_viewPopup.IsItemChecked(_viewPopup.GetItemIndex(100));
                _viewPopup.SetItemChecked(_viewPopup.GetItemIndex(100), solid);
                SetSolid(solid);
            }
            else
            {
                SetViewMode(id == 0 ? "Off" : names[(int)id - 1]);
            }
        };

        _layerMenu = Menu(row, "Map layers", "Layers drawn on the world and on the minimap: places, regions, spawns, houses, live, pins, measure, route");
        PopupMenu lp = _layerMenu.GetPopup();
        foreach (IMapLayer l in _mapLayers.All)
        {
            lp.AddCheckItem(l.Name);
            int index = lp.ItemCount - 1;
            lp.SetItemTooltip(index, l.Summary);
        }

        lp.AddSeparator();
        lp.AddCheckItem("Live players", 1000);
        lp.AddCheckItem("Live mobiles", 1001);
        lp.SetItemChecked(lp.GetItemIndex(1000), true);
        lp.SetItemChecked(lp.GetItemIndex(1001), true);
        lp.IdPressed += id =>
        {
            if (id == 1000 || id == 1001)
            {
                SetLiveKinds(id == 1000 ? !_mapLayers.Live.Players : _mapLayers.Live.Players,
                    id == 1001 ? !_mapLayers.Live.Mobiles : _mapLayers.Live.Mobiles);
            }
            else
            {
                SetLayer(_mapLayers.All[(int)id].Name, !_mapLayers.All[(int)id].On);
            }
        };
        // AddCheckItem without an id numbers items by index, which is what IdPressed reports.

        _scenePackButton = new Button
        {
            Text = "Scene pack",
            TooltipText = "Write build/scene_packs/<time>/: the frame, an image of the chosen render mode (Height, Walkability and Types when none is), and scene.json. Nothing is sent anywhere.",
        };
        _scenePackButton.Pressed += () =>
        {
            string dir = WriteScenePack();
            _status.Text = dir == null ? "Scene pack: the world is not up" : $"Scene pack written to {dir}";
        };
        row.AddChild(_scenePackButton);

        _cursor = new Label { Text = "", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        row.AddChild(_cursor);
    }

    /// <summary>Chooses a render mode by name ("Height", "Walkability", ...) or "Off". False when there is no such mode.</summary>
    public bool SetViewMode(string name)
    {
        if (_modeNode == null || !_modeNode.SetMode(name))
        {
            return false;
        }

        for (int i = 0; _viewPopup != null && i < _viewPopup.ItemCount; i++)
        {
            string text = _viewPopup.GetItemText(i);
            if (_viewPopup.GetItemId(i) != 100 && !_viewPopup.IsItemSeparator(i))
            {
                _viewPopup.SetItemChecked(i, text.Equals(string.IsNullOrEmpty(_modeNode.ModeName) ? "Off" : _modeNode.ModeName, StringComparison.OrdinalIgnoreCase));
            }
        }

        return true;
    }

    /// <summary>Writes a scene pack of the view as it is (ADR-0027); the active mode, or Height, Walkability and Types. Returns its folder.</summary>
    public string WriteScenePack(System.Collections.Generic.IEnumerable<string> modes = null, string root = null) =>
        ScenePack.Write(this, _data, modes ?? (ViewMode.Length > 0 ? new[] { ViewMode } : ScenePack.DefaultModes), root);

    public string ViewMode => _modeNode?.ModeName ?? "";

    /// <summary>Tinted (translucent over the world) or solid (opaque) fill for the render modes.</summary>
    public void SetSolid(bool solid)
    {
        if (_modeNode != null)
        {
            _modeNode.Tinted = !solid;
        }

        if (_viewPopup != null)
        {
            int i = _viewPopup.GetItemIndex(100);
            _viewPopup.SetItemChecked(i, solid);
        }
    }

    /// <summary>Shows or hides a map layer by name, through its menu item. False when there is no such layer.</summary>
    public bool SetLayer(string name, bool on)
    {
        IMapLayer l = _mapLayers.Named(name);
        if (l == null)
        {
            return false;
        }

        l.On = on;
        if (l == _mapLayers.Live)
        {
            SetLiveMenuCheck("Live", on);
        }
        PopupMenu lp = _layerMenu?.GetPopup();
        int i = _mapLayers.All.IndexOf(l);
        if (lp != null && i >= 0)
        {
            lp.SetItemChecked(i, on);
        }

        return true;
    }

    public bool LayerOn(string name) => _mapLayers.Named(name)?.On ?? false;

    /// <summary>Filters the Live layer's icons, labels and hover details by the bridge's player/mobile kind.</summary>
    public void SetLiveKinds(bool players, bool mobiles)
    {
        _mapLayers.Live.Players = players;
        _mapLayers.Live.Mobiles = mobiles;
        SetLiveMenuCheck("Live players", players);
        SetLiveMenuCheck("Live mobiles", mobiles);
        PopupMenu lp = _layerMenu?.GetPopup();
        if (lp != null)
        {
            lp.SetItemChecked(lp.GetItemIndex(1000), players);
            lp.SetItemChecked(lp.GetItemIndex(1001), mobiles);
        }
    }

    private void SetLiveMenuCheck(string label, bool on)
    {
        PopupMenu menu = _layers?.GetPopup();
        for (int i = 0; menu != null && i < menu.ItemCount; i++)
        {
            if (menu.GetItemText(i) == label)
            {
                menu.SetItemChecked(i, on);
                return;
            }
        }
    }

    public System.Collections.Generic.IReadOnlyList<string> LayerNames => _mapLayers.All.Select(l => l.Name).ToList();

    private void UpdateModeUi()
    {
        if (_modeNode == null || _chip == null)
        {
            return;
        }

        _modeNode.Hover = _host.Picked is GameObject h ? (h.X, h.Y) : null;
        string title = _modeNode.ModeName;
        if (title.Length > 0)
        {
            title += _modeNode.Tinted ? " (tinted)" : " (solid)";
        }

        _chip.Set(title, title.Length == 0 ? Array.Empty<LegendItem>() : _modeNode.Legend(), _modeNode.HoverText(), _modeNode.Mode?.Meaning);
        // Under the map's hint line, never over it.
        if (_previewLabel != null)
        {
            _chip.Position = new Vector2(10, _previewLabel.Position.Y + _previewLabel.Size.Y + 6);
        }
        if (_cursor != null)
        {
            _cursor.Text = _modeNode.Hover is { } c && _modeNode.Data != null
                ? Coordinates.Format(_host.Facet, c.X, c.Y, _modeNode.Data.SurfaceZ(c.X, c.Y))
                : "";
        }
    }

    /// <summary>The widest of the toolbar's rows when every control is at its minimum, for the check that the bar fits a 1920 px screen.</summary>
    internal float ToolbarMinWidth() =>
        GetChildren().OfType<HBoxContainer>().Take(1).Select(r => r.GetCombinedMinimumSize().X).DefaultIfEmpty(0).Max();

    private static sbyte StandZ(GameObject o) =>
        o is Static st ? (sbyte)Math.Min(127, st.Z + st.ItemData.Height) : o.Z;

    /// <summary>The Measure, Route and Pin tools on a cell.</summary>
    internal string ApplyMapTool(int x, int y, sbyte z)
    {
        int facet = _host.Facet;
        switch (Tool)
        {
            case WorldTool.Measure:
                SetLayer("Measure", true);
                _mapLayers.Measure.Click(facet, x, y, z);
                return _mapLayers.Measure.Result is { } r
                    ? $"Measure: {r.Tiles} tiles ({r.Dx:+0;-0;0}, {r.Dy:+0;-0;0}), straight {r.Straight:0.0}"
                    : $"Measure: from {x},{y}; click the other end";

            case WorldTool.Route:
            {
                SetLayer("Route", true);
                RouteLayer rl = _mapLayers.Route;
                if (rl.Start == null || rl.Path != null)
                {
                    rl.Clear();
                    rl.Start = (facet, x, y, z);
                    return "Route: from " + $"{x},{y}; click the goal";
                }

                var s = rl.Start.Value;
                rl.Facet = facet;
                rl.Path = Route.Find(_modeNode.Data, s.X, s.Y, (sbyte)s.Z, x, y);
                rl.Start = null;
                return rl.Path == null ? $"Route: no way from {s.X},{s.Y} to {x},{y} by the client's walking rules" : $"Route: {rl.Length} steps from {s.X},{s.Y} to {x},{y}";
            }

            case WorldTool.Pin:
            {
                Pin pin = _mapLayers.Pins.Add("", facet, x, y, z);
                SetLayer("Pins", true);
                return pin == null ? "Pin: no world project is open" : $"Pinned {pin.Name} at {x},{y},{z}";
            }
        }

        return "";
    }
}
#endif
