// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using Godot;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// The world map's markers manager, Modern (ADR-0024, gump index 9): the
/// markers of each marker file as finger-sized rows with their colour, a search,
/// and Go to. Classic's is 620 × 500 of small columns. Editing and removing stay
/// in its Classic view, which saves the file its own way.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. The markers are WorldMapGump's own
/// (WorldMapGump._markerFiles, the files the map draws); Go to is
/// WorldMapGump.GoToMarker, as MarkersManagerGump's Go to button calls it.
/// Opened from the world map's context menu, over the map.
/// </remarks>
internal sealed partial class ModernMarkers : ModernGump
{
    private const int RowHeight = 26;
    private const int MaxRows = 300; // a long file is searched, not scrolled to the end

    private int _file;
    private Label _fileLabel, _count;
    private LineEdit _search;
    private VBoxContainer _list;

    public ModernMarkers(World world) : base(world) { }

    protected override float MaxArtWidth => 480f;

    protected override void Build(PanelContainer card)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        card.AddChild(col);

        var header = new HBoxContainer();
        col.AddChild(header);
        Label title = UoTheme.Label("Map markers", UoTheme.Heading, 2);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);
        Button classic = UoTheme.Button("Classic view", 90);
        classic.Pressed += OpenClassic;
        header.AddChild(classic);
        Button close = UoTheme.Button("X", 30);
        close.Pressed += Close;
        header.AddChild(close);

        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 4);
        col.AddChild(filters);
        Button prev = UoTheme.Button("‹", 30), next = UoTheme.Button("›", 30);
        prev.SetMeta("markers", "file prev");
        next.SetMeta("markers", "file next");
        prev.Pressed += () => Step(-1);
        next.Pressed += () => Step(1);
        _fileLabel = UoTheme.Label("", UoTheme.Ink);
        _fileLabel.CustomMinimumSize = new Vector2(120, 0);
        _fileLabel.HorizontalAlignment = HorizontalAlignment.Center;
        filters.AddChild(prev);
        filters.AddChild(_fileLabel);
        filters.AddChild(next);
        _search = new LineEdit { PlaceholderText = "Search markers", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 18) };
        _search.TextChanged += _ => Rebuild();
        filters.AddChild(_search);

        _count = UoTheme.Label("", UoTheme.Muted);
        col.AddChild(_count);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 4));
        col.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 1);
        scroll.AddChild(_list);
    }

    private static List<WorldMapGump.WMapMarkerFile> Files => WorldMapGump._markerFiles;

    private void Step(int d)
    {
        if (Files.Count == 0)
        {
            return;
        }

        _file = ((_file + d) % Files.Count + Files.Count) % Files.Count;
        Rebuild();
    }

    protected override void OnOpen()
    {
        _file = Math.Clamp(_file, 0, Math.Max(0, Files.Count - 1));
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (Node n in _list.GetChildren())
        {
            _list.RemoveChild(n);
            n.QueueFree();
        }

        if (Files.Count == 0)
        {
            _fileLabel.Text = "No marker files";
            _count.Text = "The world map has no marker files loaded.";
            return;
        }

        WorldMapGump.WMapMarkerFile file = Files[_file];
        _fileLabel.Text = file.Name;
        string query = _search.Text.Trim();
        int shown = 0, matched = 0;

        foreach (WorldMapGump.WMapMarker m in file.Markers)
        {
            if (query.Length > 0 && (m.Name == null || !m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            matched++;

            if (shown < MaxRows)
            {
                _list.AddChild(Row(m));
                shown++;
            }
        }

        _count.Text = matched > shown ? $"{matched} markers; the first {shown} shown. Search to narrow them."
            : $"{matched} markers";
    }

    private Control Row(WorldMapGump.WMapMarker m)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, RowHeight) };
        row.AddThemeConstantOverride("separation", 6);

        Button go = UoTheme.Button("Go to", 50);
        go.SetMeta("markers", "go " + m.Name);
        go.Pressed += () => GoTo(m);
        row.AddChild(go);

        var swatch = new ColorRect
        {
            Color = new Color(m.Color.R / 255f, m.Color.G / 255f, m.Color.B / 255f),
            CustomMinimumSize = new Vector2(8, 8),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        row.AddChild(swatch);

        Label name = UoTheme.Label(string.IsNullOrEmpty(m.Name) ? "(unnamed)" : m.Name, UoTheme.Ink);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.ClipText = true;
        row.AddChild(name);

        Label at = UoTheme.Label($"{m.X}, {m.Y}", UoTheme.Muted);
        at.CustomMinimumSize = new Vector2(80, 0);
        at.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(at);

        return row;
    }

    /// <summary>Centre the world map on the marker, as the classic Go to does, and show the map.</summary>
    private void GoTo(WorldMapGump.WMapMarker m)
    {
        WorldMapGump map = UIManager.GetGump<WorldMapGump>();

        if (map == null)
        {
            GameActions.OpenWorldMap(World);
            map = UIManager.GetGump<WorldMapGump>();
        }

        map?.GoToMarker(m.X, m.Y, false);
        TouchInput.Note($"modern: map markers -> go to {m.Name} at {m.X},{m.Y}");
        Close();
    }

    private void OpenClassic()
    {
        Close();
        ModernGumps.OpenClassicNext(typeof(MarkersManagerGump));
        UIManager.Add(new MarkersManagerGump(World));
    }

    /// <summary>For the probe: a control by its tag or text.</summary>
    public Control Find(string what)
    {
        foreach (Node n in Card.FindChildren("*", "Control", true, false))
        {
            if (n is Control c && c.IsVisibleInTree()
                && ((c.HasMeta("markers") && (string)c.GetMeta("markers") == what) || c is Button b && b.Text == what))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>For the probe: the first marker's name in the open file, or null.</summary>
    public string FirstMarker => Files.Count > _file && Files[_file].Markers.Count > 0 ? Files[_file].Markers[0].Name : null;
}
