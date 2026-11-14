#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GUO.Game.GameObjects;
using GUO.IO.Audio;

internal readonly record struct MapBoundary(int X0, int Y0, int X1, int Y1)
{
    public static MapBoundary Between(int x, int y, int otherX, int otherY) =>
        new(Math.Min(x, otherX), Math.Min(y, otherY), Math.Max(x, otherX), Math.Max(y, otherY));

    public bool Contains(int x, int y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    public long Tiles => (X1 - (long)X0 + 1) * (Y1 - (long)Y0 + 1);
    public string Size => $"{X1 - X0 + 1} × {Y1 - Y0 + 1} tiles";

    public Vector2[] Corners(CellGeometry geo) =>
        new[] { geo.Corner(X0, Y0), geo.Corner(X1 + 1, Y0), geo.Corner(X1 + 1, Y1 + 1), geo.Corner(X0, Y1 + 1) };

    public (int X, int Y) OppositeTile(int corner) =>
        (corner is 0 or 3 ? X1 : X0, corner is 0 or 1 ? Y1 : Y0);
}

internal sealed record BoundaryEntry(string Name, MapBoundary Bounds, Func<string, MapBoundary, bool> Apply);

public partial class WorldView
{
    private enum BoundaryMode { Pick, Draw, Resize }

    internal RegionsDock RegionEditor { get; set; }
    private bool BoundaryTool => Tool is WorldTool.PickRegion or WorldTool.AudioZones;
    private TabContainer _boundaryTabs;
    private VBoxContainer _boundaryControls;
    private OptionButton _boundaryMode, _audioKind, _boundaryMatches;
    private LineEdit _boundaryName;
    private Label _boundaryHint;
    private Button _boundaryApply;
    private BoundaryEntry[] _boundaries = Array.Empty<BoundaryEntry>();
    private BoundaryEntry[] _matches = Array.Empty<BoundaryEntry>();
    private BoundaryEntry _boundaryTarget;
    private MapBoundary? _boundaryDraft, _boundaryPreview;
    private (int X, int Y)? _boundaryAnchor;
    private ZoneAudio.Layer AudioLayer => _audioKind.Selected == 0 ? ZoneAudio.Music : ZoneAudio.Sfx;
    private BoundaryMode ActiveBoundaryMode => (BoundaryMode)_boundaryMode.Selected;

    private void BuildBoundaryControls(TabContainer tabs)
    {
        _boundaryTabs = tabs;
        _boundaryControls = new VBoxContainer { Name = "Boundary" };
        tabs.AddChild(_boundaryControls);
        _audioKind = new OptionButton { Name = "AudioZoneType" };
        _audioKind.AddItem("Music Zones");
        _audioKind.AddItem("SFX Zones");
        _audioKind.ItemSelected += _ => { ClearBoundary(); RefreshBoundaries(); };
        _boundaryControls.AddChild(_audioKind);

        _boundaryMode = new OptionButton { Name = "BoundaryMode" };
        foreach (string name in Enum.GetNames<BoundaryMode>()) _boundaryMode.AddItem(name);
        _boundaryMode.ItemSelected += _ =>
        {
            CancelPointer();
            if (ActiveBoundaryMode == BoundaryMode.Draw) ClearBoundary();
            UpdateBoundaryControls();
        };
        _boundaryControls.AddChild(_boundaryMode);
        _boundaryMatches = new OptionButton { Visible = false, FitToLongestItem = false };
        _boundaryMatches.ItemSelected += i => SelectBoundary(_matches[(int)i]);
        _boundaryControls.AddChild(_boundaryMatches);
        _boundaryName = new LineEdit { PlaceholderText = "Name" };
        _boundaryName.TextChanged += _ => UpdateBoundaryControls();
        _boundaryControls.AddChild(_boundaryName);
        _boundaryHint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _boundaryControls.AddChild(_boundaryHint);
        var actions = new HBoxContainer();
        _boundaryControls.AddChild(actions);
        _boundaryApply = ActionButton(actions, "Save", SaveBoundary);
        ActionButton(actions, "Cancel", ClearBoundary);
        ActionButton(actions, "Frame", () => { if (_boundaryDraft is { } bounds) CenterBoundary(bounds); });
        tabs.SetTabHidden(_boundaryControls.GetIndex(), true);
    }

    private void UpdateBoundaryTool()
    {
        if (_boundaryControls == null) return;
        ClearBoundary();
        if (_guides != null) _guides.Area = BoundaryTool ? null : Area;
        int index = _boundaryControls.GetIndex();
        if (!BoundaryTool)
        {
            _boundaryTabs.SetTabHidden(index, true);
            _boundaries = Array.Empty<BoundaryEntry>();
            if (_guides != null)
            {
                _guides.Boundaries = _boundaries;
                _guides.BoundaryDraft = null;
            }
            return;
        }
        _boundaryTabs.SetTabHidden(index, false);
        _boundaryTabs.SetTabTitle(index, ToolName(Tool));
        _boundaryTabs.CurrentTab = index;
        _audioKind.Visible = Tool == WorldTool.AudioZones;
        _boundaryMode.Select((int)BoundaryMode.Pick);
        RefreshBoundaries();
        UpdateBoundaryControls();
    }

    internal void RefreshBoundaries()
    {
        if (!BoundaryTool || _boundaryControls == null || !_host.IsBooted) return;
        int facet = _host.Facet;
        if (Tool == WorldTool.PickRegion)
            _boundaries = RegionEditor?.Boundaries(facet).ToArray() ?? Array.Empty<BoundaryEntry>();
        else
        {
            var layer = AudioLayer;
            _boundaries = layer.Zones.Where(z => z.Facet < 0 || z.Facet == facet).Select(z =>
            {
                var bounds = MapBoundary.Between(z.X0, z.Y0, z.X1, z.Y1);
                string originalName = z.Name;
                return new BoundaryEntry(z.Name, bounds, (name, rect) =>
                    SaveAudioBoundary(layer, facet, name, rect, z, originalName, bounds));
            }).ToArray();
        }
    }

    private static bool SaveAudioBoundary(ZoneAudio.Layer layer, int facet, string name, MapBoundary rect,
        ZoneAudio.Zone existing = null, string originalName = null, MapBoundary originalBounds = default)
    {
        var zones = new List<ZoneAudio.Zone>(layer.Zones);
        int index = existing == null ? -1 : zones.IndexOf(existing);
        if (existing != null && (index < 0 || existing.Name != originalName ||
            MapBoundary.Between(existing.X0, existing.Y0, existing.X1, existing.Y1) != originalBounds)) return false;

        if ((existing == null || name != originalName) &&
            zones.Any(z => z != existing && z.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;

        var changed = new ZoneAudio.Zone
        {
            Name = name, Facet = existing?.Facet ?? facet,
            X0 = rect.X0, Y0 = rect.Y0, X1 = rect.X1, Y1 = rect.Y1,
            Z0 = existing?.Z0 ?? -128, Z1 = existing?.Z1 ?? 127, Key = existing?.Key,
            Tracks = existing?.Tracks ?? new(), PcmTracks = existing?.PcmTracks,
        };
        if (index >= 0) zones[index] = changed;
        else zones.Add(changed);
        return layer.Save(zones);
    }

    private void SaveBoundary()
    {
        string name = _boundaryName.Text.Trim();
        if (_boundaryDraft is not { } bounds || name.Length == 0) return;
        bool saved = _boundaryTarget != null ? _boundaryTarget.Apply(name, bounds)
            : Tool == WorldTool.PickRegion ? RegionEditor?.AddBoundary(_host.Facet, name, bounds) == true
            : SaveAudioBoundary(AudioLayer, _host.Facet, name, bounds);
        if (!saved)
        {
            _status.Text = "Boundary could not be saved. Check the destination or pick the current boundary again.";
            return;
        }
        _status.Text = Tool == WorldTool.PickRegion
            ? $"Applied {name}. Use Save regions.json in the Regions dock."
            : $"Saved audio zone {name}. Assign tracks in the audio controls.";
        ClearBoundary();
        RefreshBoundaries();
    }

    private void ClearBoundary()
    {
        CancelBoundaryPointer();
        _boundaryDraft = null;
        _boundaryTarget = null;
        _matches = Array.Empty<BoundaryEntry>();
        if (_boundaryMatches != null)
        {
            _boundaryMatches.Clear();
            _boundaryMatches.Hide();
            _boundaryName.Text = "";
            UpdateBoundaryControls();
        }
    }

    private void CancelBoundaryPointer()
    {
        _boundaryAnchor = null;
        _boundaryPreview = null;
    }

    private void UpdateBoundaryControls()
    {
        if (_boundaryHint == null || !BoundaryTool) return;
        _boundaryApply.Text = Tool == WorldTool.PickRegion ? "Apply boundary" : "Save zone";
        bool available = Tool != WorldTool.PickRegion || RegionEditor?.HasRegionFile == true;
        _boundaryApply.Disabled = !available || _boundaryDraft == null || string.IsNullOrWhiteSpace(_boundaryName.Text);
        if (!available)
        {
            _boundaryHint.Text = "Load a shard regions.json in the Regions dock.";
            return;
        }
        _boundaryHint.Text = _boundaryDraft is { } bounds
            ? $"{bounds.Size} · map{_host.Facet}\n{(Tool == WorldTool.PickRegion ? "Apply, then save in the Regions dock." : "Save writes to the selected audio layer.")}"
            : ActiveBoundaryMode switch
            {
                BoundaryMode.Pick => "Click a boundary to select it. Choose from the list if boundaries overlap.",
                BoundaryMode.Draw => "Click for one tile, or drag for a rectangle.",
                _ => "Pick a boundary, then drag a corner handle to resize it.",
            };
    }

    private void UseAreaInBoundaryTool(WorldTool tool, MapBoundary bounds)
    {
        Tool = tool;
        ClearArea();
        _boundaryMode.Select((int)BoundaryMode.Draw);
        _boundaryDraft = bounds;
        UpdateBoundaryControls();
    }

    private void PickBoundary(int x, int y)
    {
        RefreshBoundaries();
        _matches = _boundaries.Where(b => b.Bounds.Contains(x, y)).OrderBy(b => b.Bounds.Tiles).ToArray();
        _boundaryMatches.Clear();
        foreach (BoundaryEntry match in _matches) _boundaryMatches.AddItem($"{match.Name} · {match.Bounds.Size}");
        _boundaryMatches.Visible = _matches.Length > 1;
        if (_matches.Length == 0)
        {
            ClearBoundary();
            _status.Text = "No boundary of this type at the selected tile.";
            return;
        }
        _boundaryMatches.Select(0);
        SelectBoundary(_matches[0]);
        if (Tool == WorldTool.PickRegion) RegionClick?.Invoke(_host.Facet, x, y);
    }

    private void SelectBoundary(BoundaryEntry entry)
    {
        _boundaryTarget = entry;
        _boundaryDraft = entry.Bounds;
        _boundaryName.Text = entry.Name;
        UpdateBoundaryControls();
    }

    private Vector2 BoundaryMouse(Vector2 fallback) => ForcedMouse is { } p ? (Vector2)p : fallback;

    private void BeginBoundaryPointer(Vector2 pointer)
    {
        if (ActiveBoundaryMode == BoundaryMode.Draw && _host.Picked is GameObject at)
        {
            _boundaryAnchor = (at.X, at.Y);
            _boundaryPreview = MapBoundary.Between(at.X, at.Y, at.X, at.Y);
        }
        else if (ActiveBoundaryMode == BoundaryMode.Resize && _boundaryDraft is { } draft &&
                 CellGeometry.From(_host) is { } geo)
        {
            Vector2 mouse = BoundaryMouse(pointer);
            Vector2[] corners = draft.Corners(geo);
            for (int i = 0; i < corners.Length; i++)
            {
                if (corners[i].DistanceSquaredTo(mouse) > 144) continue;
                _boundaryAnchor = draft.OppositeTile(i);
                break;
            }
        }
    }

    private void FinishBoundaryPointer(bool dragged)
    {
        if (_boundaryAnchor is { } anchor && _host.Picked is GameObject at &&
            (ActiveBoundaryMode == BoundaryMode.Draw || dragged))
        {
            bool newBoundary = ActiveBoundaryMode == BoundaryMode.Draw;
            string name = _boundaryName.Text;
            if (newBoundary)
            {
                ClearBoundary();
                _boundaryName.Text = name;
            }
            _boundaryDraft = MapBoundary.Between(anchor.X, anchor.Y, at.X, at.Y);
        }
        else if (_boundaryAnchor == null && ActiveBoundaryMode != BoundaryMode.Draw &&
                 _host.Picked is GameObject picked)
        {
            PickBoundary(picked.X, picked.Y);
        }
        CancelBoundaryPointer();
        UpdateBoundaryControls();
    }

    private void UpdateBoundaryPointer(Vector2I? mouse)
    {
        if (mouse != null && _host.Picked is GameObject at)
        {
            if (_boundaryAnchor is { } anchor)
            {
                var preview = MapBoundary.Between(anchor.X, anchor.Y, at.X, at.Y);
                if (_boundaryPreview != preview)
                    _boundaryHint.Text = $"{preview.Size} · release to review";
                _boundaryPreview = preview;
            }
            else
            {
                var match = Array.Find(_boundaries, b => b.Bounds.Contains(at.X, at.Y));
                if (match != null) _container.TooltipText = match.Name;
            }
        }
        _guides.Boundaries = _boundaries;
        _guides.BoundaryDraft = _boundaryPreview ?? _boundaryDraft;
        _guides.BoundaryHandles = ActiveBoundaryMode == BoundaryMode.Resize;
    }

    private void CenterBoundary(MapBoundary bounds) =>
        GoTo(_host.Facet, bounds.X0 + (bounds.X1 - bounds.X0) / 2, bounds.Y0 + (bounds.Y1 - bounds.Y0) / 2);
}
#endif
