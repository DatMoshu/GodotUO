#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;
using GUO.Game.GameObjects;

public partial class WorldView
{
    private VBoxContainer _nearby;
    private Label _nearbyPosition;
    private CheckBox _pinNearby;
    private readonly Button[] _nearbyCells = new Button[9];
    private (int X, int Y)? _nearbyCenter;
    private int _nearbyVersion = -1, _nearbyFacet = -1;
    private int _nearbyMinZ = int.MinValue, _nearbyMaxZ = int.MaxValue;

    private void ApplyToolHeightRatio()
    {
        if (_leftWorkspace?.Size.Y > 0)
            // Split offsets are relative to the default midpoint, not absolute heights.
            _leftWorkspace.SplitOffsets = new[] { (int)(_leftWorkspace.Size.Y * (_toolHeightRatio - 0.5)) };
    }

    private void BuildNearbyTiles()
    {
        _nearby = new VBoxContainer { Name = "Nearby tiles" }; _detailTabs.AddChild(_nearby);
        var header = new HBoxContainer(); _nearby.AddChild(header);
        _nearbyPosition = new Label { Text = "Move over the map", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        header.AddChild(_nearbyPosition);
        _pinNearby = new CheckBox { Text = "Pin", TooltipText = "Keep this neighborhood while moving over the map" };
        header.AddChild(_pinNearby);
        var content = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; _nearby.AddChild(content);
        var grid = new GridContainer { Columns = 3, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        content.AddChild(grid);
        var group = new ButtonGroup();
        for (int i = 0; i < 9; i++)
        {
            int dx = i % 3 - 1, dy = i / 3 - 1;
            var button = new Button { CustomMinimumSize = new Vector2(46, 46), ExpandIcon = true,
                TextureFilter = TextureFilterEnum.Nearest, ToggleMode = true, ButtonGroup = group };
            button.AddThemeConstantOverride("icon_max_width", 44);
            button.Pressed += () =>
            {
                if (_nearbyCenter is not { } center) return;
                _pinNearby.ButtonPressed = true; _stackIndex = -1;
                RefreshStack(center.X + dx, center.Y + dy);
                _nearbyPosition.Text = $"Cell {center.X + dx}, {center.Y + dy}";
            };
            grid.AddChild(button); _nearbyCells[i] = button;
        }
        _stack = new ItemList { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            MaxTextLines = 2,
            FixedIconSize = new Vector2I(32, 32), TextureFilter = TextureFilterEnum.Nearest,
            CustomMinimumSize = new Vector2(140, 100), TooltipText = "Visible land and static stack, highest first. Select a row to inspect; double-click to use it as the brush." };
        content.AddChild(_stack);
        _stack.ItemSelected += i => { _stackIndex = (int)i; InspectPicked(); };
        _stack.ItemActivated += i => { _stackIndex = (int)i; PickBrushFromWorld(); };
        _nearby.AddChild(new Label { Text = "3×3 terrain · click a cell · double-click a tile to pick", ClipText = true });
    }

    private void UpdateNearbyTiles()
    {
        if (_nearby?.IsVisibleInTree() != true || _modeNode?.Data == null) return;
        if (!_pinNearby.ButtonPressed && _host.Picked is GameObject picked &&
            (ForcedMouse != null || _container.GetGlobalRect().HasPoint(_container.GetGlobalMousePosition())))
        {
            var cell = ((int)picked.X, (int)picked.Y);
            if (_nearbyCenter != cell) { _nearbyCenter = cell; _nearbyVersion = -1; }
        }
        if (_nearbyCenter is not { } center) return;
        if (_nearbyFacet == _host.Facet && _nearbyVersion == _modeNode.Data.Version &&
            _nearbyMinZ == _host.MinVisibleZ && _nearbyMaxZ == _host.MaxVisibleZ) return;
        _nearbyFacet = _host.Facet; _nearbyVersion = _modeNode.Data.Version;
        _nearbyMinZ = _host.MinVisibleZ; _nearbyMaxZ = _host.MaxVisibleZ;
        _nearbyPosition.Text = $"Cell {center.X}, {center.Y}";
        for (int i = 0; i < 9; i++)
        {
            int x = center.X + i % 3 - 1, y = center.Y + i / 3 - 1;
            var block = x < 0 || y < 0 ? null : _modeNode.Data.Block(x >> 3, y >> 3);
            var button = _nearbyCells[i]; button.Disabled = block == null;
            int tile = (y & 7) * 8 + (x & 7);
            button.Icon = block == null ? null : GhostTexture(block.LandId[tile]);
            button.TooltipText = block == null ? "Outside map" : $"{x}, {y} · Land 0x{block.LandId[tile]:X4}\n{_data.NameOf(block.LandId[tile])} · Z {block.LandZ[tile]}";
            button.SetPressedNoSignal(i == 4);
        }
        _stackIndex = -1; RefreshStack(center.X, center.Y);
    }

    internal void ShowNearbyForSmoke(int x, int y)
    {
        _detailTabs.CurrentTab = 1; _pinNearby.ButtonPressed = true;
        _nearbyCenter = (x, y); _nearbyVersion = -1; UpdateNearbyTiles();
    }

    internal bool NearbyHasCenterTile() => _nearbyCells[4].Icon != null && _stack.ItemCount > 0;
    /// <summary>The visible controls of the World top command row (and the minimap) that lie outside <paramref name="visible"/>, by name.</summary>
    internal List<string> CommandRowOutside(Rect2 visible)
    {
        var outside = new List<string>();
        foreach (Node child in _commandBar.GetChildren())
            if (child is Control c && c.Visible && c.Size.X > 0 && !visible.Encloses(c.GetGlobalRect()))
                outside.Add(child is Button b && b.Text.Length > 0 ? b.Text : child.Name);
        if (_minimap != null && _minimap.IsVisibleInTree() && !visible.Encloses(_minimap.GetGlobalRect())) outside.Add("Minimap");
        return outside;
    }

    internal bool CommonToolsFit() => _commonTools.GetVScrollBar().MaxValue <= _commonTools.Size.Y + 2;
}
#endif
