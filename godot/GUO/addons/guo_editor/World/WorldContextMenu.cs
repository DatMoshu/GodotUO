#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GUO.Game.GameObjects;

public partial class WorldView
{
    private sbyte? _itemPlacementZ;

    private void ShowMapMenu()
    {
        WorldTool tool = Tool;
        var project = _host.Project;
        int facet = _host.Facet;
        var area = Area;
        var actions = new List<(string Label, Action Run)>();
        void Add(string label, Action run) => actions.Add((label, run));

        if (_host.Picked is GameObject picked)
        {
            int x = picked.X, y = picked.Y;
            sbyte z = picked.Z;
            sbyte ground = _host.World.Map.GetTileZ(x, y);
            sbyte standing = StandZ(picked);
            ushort art = picked.Graphic, hue = picked.Hue;
            bool land = picked is Land, isStatic = picked is Static;
            var id = _objects.IdOf(picked);
            void Stack() => ShowNearbyAt(x, y);
            void Sample() => SampleBrush(art, hue, z, land);
            switch (tool)
            {
                case WorldTool.Select:
                    Add("Inspect tile stack", Stack);
                    if (land || isStatic) Add("Sample art and hue", Sample);
                    break;
                case WorldTool.Brush:
                    if (land == _recipe.Land && (land || isStatic))
                        Add(land ? "Sample ground material" : "Sample static", Sample);
                    Add("Brush settings", () => ShowToolControl(_operation));
                    if (BrushSettingsChanged()) Add("Reset brush settings", ResetBrushSettings);
                    break;
                case WorldTool.Stamp:
                    if (isStatic) Add("Sample this static", Sample);
                    Add($"Use Z {z} for stamp", () => SetStampElevation(z));
                    Add("Stamp settings", () => ShowToolControl(_brush));
                    break;
                case WorldTool.Erase:
                    Add("Choose a static from tile stack", Stack);
                    break;
                case WorldTool.Hue:
                    if (isStatic) Add($"Sample hue {hue}", () => BrushHue = hue);
                    Add("Choose a static from tile stack", Stack);
                    Add("Hue settings", () => ShowToolControl(_hue));
                    break;
                case WorldTool.Raise:
                case WorldTool.Lower:
                    Add($"Inspect ground height · Z {ground}", Stack);
                    Add($"Set up Flatten brush at Z {ground}", () =>
                    {
                        QuickRecipe(true, "Flatten", _recipe.Size, _recipe.Density, _recipe.Spacing);
                        _numbers["Z / ground offset"].Value = ground;
                        ShowToolControl(_operation);
                    });
                    Add("Elevation settings", () => ShowToolControl(_numbers["Z / ground offset"]));
                    break;
                case WorldTool.PlaceItem:
                    if (isStatic) Add("Sample item art", Sample);
                    Add($"Use Z {z} for item placement", () =>
                    {
                        _itemPlacementZ = z;
                        _status.Text = $"Item placement elevation set to Z {z}.";
                    });
                    if (_itemPlacementZ != null)
                        Add("Use automatic item elevation", () => _itemPlacementZ = null);
                    Add("Item settings", () => ShowToolControl(_brush));
                    AddObjectInfo(id, x, y, z, Add);
                    break;
                case WorldTool.PlaceSpawner:
                    Add("Spawner settings", () => ShowToolControl(_spawnEntry));
                    AddObjectInfo(id, x, y, z, Add);
                    if (id is Guid spawner && _objects.Objects?.Find(spawner) is ShardSpawner)
                        Add("Show spawn ranges", () => SetLayer("Spawns", true));
                    break;
                case WorldTool.MoveObject:
                case WorldTool.DeleteObject:
                    AddObjectInfo(id, x, y, z, Add);
                    if (tool == WorldTool.MoveObject && _moving == null && id is Guid source)
                        Add("Use as move source", () => { _moving = source; _status.Text = "Click the destination."; });
                    if (tool == WorldTool.DeleteObject && id is Guid target)
                        Add("Delete project object…", () => ConfirmObjectDelete(target, project, facet));
                    break;
                case WorldTool.Measure:
                    Add("Start measurement here", () => { _mapLayers.Measure.Clear(); _status.Text = ApplyMapTool(x, y, standing); });
                    if (_mapLayers.Measure.A != null && _mapLayers.Measure.B == null)
                        Add("Finish measurement here", () => _status.Text = ApplyMapTool(x, y, standing));
                    break;
                case WorldTool.Route:
                    Add("Set route start here", () => { _mapLayers.Route.Clear(); _status.Text = ApplyMapTool(x, y, standing); });
                    if (_mapLayers.Route.Start != null)
                        Add("Set route destination here", () => _status.Text = ApplyMapTool(x, y, standing));
                    break;
                case WorldTool.Pin:
                    var pin = _mapLayers.Pins.All.FirstOrDefault(p => p.Facet == facet && p.X == x && p.Y == y);
                    if (pin != null)
                    {
                        Add($"Rename {pin.Name}", () => RenamePin(pin, project));
                        Add($"Remove {pin.Name}", () => _mapLayers.Pins.Remove(pin));
                    }
                    else if (project != null)
                        Add("Add pin here", () => _status.Text = ApplyMapTool(x, y, standing));
                    break;
                case WorldTool.PickRegion:
                case WorldTool.AudioZones:
                    Add(tool == WorldTool.PickRegion ? "Pick region here" : "Pick audio zone here",
                        () => { _boundaryMode.Select((int)BoundaryMode.Pick); PickBoundary(x, y); });
                    Add("Draw new boundary", () => { ClearBoundary(); _boundaryMode.Select((int)BoundaryMode.Draw); UpdateBoundaryControls(); });
                    break;
            }
            Add("Copy map coordinates", () => DisplayServer.ClipboardSet($"map{facet} {x},{y},{z}"));
        }

        if (tool is WorldTool.Select or WorldTool.Area && area is { } selected)
        {
            MapBoundary bounds = new(selected.X0, selected.Y0, selected.X1, selected.Y1);
            Add("Center on selection", () => { if (Area == area) CenterBoundary(bounds); });
            Add("Clear selection", () => { if (Area == area) ClearArea(); });
            if (tool == WorldTool.Area)
            {
                Add("Send statics to Multi Editor", () => { if (Area == area) SaveAreaAsMulti(); });
                Add("Use area in Regions", () => { if (Area == area) UseAreaInBoundaryTool(WorldTool.PickRegion, bounds); });
                Add("Use area in Audio Zones", () => { if (Area == area) UseAreaInBoundaryTool(WorldTool.AudioZones, bounds); });
            }
        }
        if (tool == WorldTool.MoveObject && _moving != null)
            Add("Cancel move", () => { _moving = null; _status.Text = "Move canceled."; });
        if (tool == WorldTool.Measure && _mapLayers.Measure.A != null)
        {
            if (_mapLayers.Measure.Result is { } result)
                Add("Copy measurement", () => DisplayServer.ClipboardSet($"{result.Tiles} tiles · ΔX {result.Dx} · ΔY {result.Dy} · straight {result.Straight:0.0}"));
            Add("Clear measurement", _mapLayers.Measure.Clear);
        }
        if (tool == WorldTool.Route && (_mapLayers.Route.Start != null || _mapLayers.Route.Path != null))
            Add("Clear route", _mapLayers.Route.Clear);
        if (BoundaryTool && _boundaryDraft is { } draft)
        {
            Add("Resize boundary", () => { _boundaryMode.Select((int)BoundaryMode.Resize); UpdateBoundaryControls(); });
            Add("Frame boundary", () => CenterBoundary(draft));
            Add("Cancel boundary edit", ClearBoundary);
        }
        if (actions.Count == 0) return;

        var menu = new PopupMenu { Name = "WorldMapActions" };
        EditorInterface.Singleton.GetBaseControl().AddChild(menu);
        for (int i = 0; i < actions.Count; i++) menu.AddItem(actions[i].Label, i);
        menu.IdPressed += id =>
        {
            if (project == _host.Project && facet == _host.Facet && tool == Tool) actions[(int)id].Run();
            menu.Hide();
        };
        menu.PopupHide += () => menu.QueueFree();
        Vector2I point = ForcedMouse is { } forced ? (Vector2I)_container.GlobalPosition + forced
            : (Vector2I)GetGlobalMousePosition();
        menu.Popup(new Rect2I(point, new Vector2I(10, 10)));
    }

    private void ShowToolControl(Control control)
    {
        if (control == null) return;
        _leftWorkspace.Show();
        var tabs = (TabContainer)_settings;
        for (Node node = control; node != null; node = node.GetParent())
        {
            if (node.GetParent() == tabs)
            {
                tabs.CurrentTab = node.GetIndex();
                break;
            }
        }
        for (Node node = control.GetParent(); node != null && node != tabs; node = node.GetParent())
            if (node is ScrollContainer scroll) scroll.CallDeferred(ScrollContainer.MethodName.EnsureControlVisible, control);
        control.GrabFocus();
    }

    private bool BrushSettingsChanged() =>
        _recipe.Size != 7 || _recipe.Density != 35 || _recipe.Spacing != 2 || _recipe.Seed != 1 ||
        _recipe.Strength != 1 || _recipe.MaxSlope != 127 || _recipe.Height != 0 || _recipe.FixedHeight ||
        _recipe.Land || _recipe.Square || !_recipe.KeepStatics || !_recipe.AvoidWater || BrushHue != 0 ||
        _recipe.Operation != "Paint" || _recipe.Variants.Length != 0 ||
        _recipe.AllowedLand.Length != 0 || _recipe.Edges.Length != 0;

    private void ResetBrushSettings()
    {
        QuickRecipe(false, "Paint", 7, 35, 2);
        _numbers["Seed"].Value = 1;
        _numbers["Strength"].Value = 1;
        _numbers["Maximum slope"].Value = 127;
        _numbers["Z / ground offset"].Value = 0;
        _checks["Fixed placement plane"].ButtonPressed = false;
        _checks["Square footprint"].ButtonPressed = false;
        _checks["Keep existing statics"].ButtonPressed = true;
        _checks["Avoid water"].ButtonPressed = true;
        _allowed.Text = _edges.Text = "";
        _recipe.AllowedLand = _recipe.Edges = "";
        BrushHue = 0;
        _status.Text = "Brush settings reset.";
    }

    private void ConfirmObjectDelete(Guid id, WorldProject project, int facet)
    {
        if (_objects.Objects?.Find(id) is not { } item) return;
        string name = item is ShardSpawner spawner ? $"spawner at {spawner.X},{spawner.Y}"
            : item is ShardItem decoration ? $"item at {decoration.X},{decoration.Y}" : "this project object";
        var dialog = new ConfirmationDialog { Title = "Delete project object", DialogText = $"Delete {name}?", OkButtonText = "Delete" };
        AddChild(dialog);
        dialog.Confirmed += () =>
        {
            if (_host.Project == project && _host.Facet == facet && _objects.Delete(id))
                _status.Text = $"Deleted {name}.";
            dialog.QueueFree();
        };
        dialog.Canceled += () => dialog.QueueFree();
        dialog.PopupCentered();
    }

    private void SetStampElevation(sbyte z)
    {
        _checks["Fixed placement plane"].ButtonPressed = true;
        _numbers["Z / ground offset"].Value = z;
        _status.Text = $"Stamp placement plane set to Z {z}.";
    }

    private void AddObjectInfo(Guid? id, int x, int y, sbyte z, Action<string, Action> add)
    {
        if (id is not Guid objectId || _objects.Objects?.Find(objectId) == null) return;
        add("Inspect project object", () =>
        {
            if (_objects.Objects?.Find(objectId) is not { } item) return;
            var info = new Inspection
            {
                Source = "World",
                Title = item is ShardSpawner ? "Project spawner" : "Project item",
                Text = $"map{_host.Facet} {x},{y},{z}\nObject ID {objectId}",
            };
            Inspect?.Invoke(info);
            _detailTabs.CurrentTab = 0;
        });
    }

    private void RenamePin(Pin pin, WorldProject project)
    {
        var dialog = new ConfirmationDialog { Title = "Rename pin", OkButtonText = "Save" };
        var entry = new LineEdit { Text = pin.Name, CustomMinimumSize = new Vector2(240, 0) };
        dialog.AddChild(entry);
        AddChild(dialog);
        dialog.Confirmed += () =>
        {
            if (_host.Project == project) _mapLayers.Pins.Rename(pin, entry.Text);
            dialog.QueueFree();
        };
        dialog.Canceled += () => dialog.QueueFree();
        dialog.PopupCentered();
        entry.GrabFocus();
        entry.SelectAll();
    }
}
#endif
