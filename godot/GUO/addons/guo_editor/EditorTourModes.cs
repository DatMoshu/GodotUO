#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>The tour's segments for ADR-0027: the render modes, then the map layers and the scene pack.</summary>
public partial class EditorTour
{
    private async Task ModesSeg()
    {
        if (!NeedWorld())
        {
            return;
        }

        _overlay.SetDetail(null);
        _world.GoTo(0, 1496, 1628);
        _world.SetSolid(false);
        await Frames(25);
        Say("The View menu recolours the world to show what it means, drawn over the game's frame and never changing it. A legend chip names the colours. "
            + "Each mode is a diagnostic a builder used to check by walking a character.");
        MarkControl(_world.ViewMenu, "View menu");
        await Shot(3);

        foreach (string mode in _world.Modes.ModeNames)
        {
            if (mode == "Reachability")
            {
                _world.Modes.SetOrigin(1496, 1628, _world.Modes.Data.LandZ(1496, 1628));
            }

            Say(_world.Modes.ModeNamed(mode).Summary + ".");
            Check(_world.SetViewMode(mode), $"View: {mode}");
            await Frames(mode == "Walkability" || mode == "Reachability" ? 40 : 20);
            Check(_world.Chip.Visible && _world.Chip.Rows > 0, $"{mode}: the legend chip shows its colours");
            MarkControl(_world.Chip, "legend");
            await Shot(3);
        }

        Say("Solid fill hides the world under the colours; tinted lets it show through. Walkability tinted over the town:");
        _world.SetViewMode("Walkability");
        _world.SetSolid(false);
        await Frames(20);
        await Shot(3);
        _world.SetViewMode("Off");
        _overlay.ClearMarks();
    }

    private async Task MapLayersSeg()
    {
        if (!NeedWorld())
        {
            return;
        }

        _overlay.SetDetail(null);
        MapLayers layers = _world.Layers;
        layers.ShardFolder = EditorSmoke.FixtureShardFolder();
        _world.GoTo(0, 1496, 1628);
        await Frames(20);
        Say("Map layers draw on the world and on the minimap, in the manner of an online map: places, regions, spawns, houses, live players, pins, measure and route. "
            + "Regions come from the shard folder's Data/regions.json; this tour uses a small fixture.");
        MarkControl(_world.MapLayersMenu, "Map layers menu");
        foreach (string n in new[] { "Places", "Regions", "Spawns" })
        {
            Check(_world.SetLayer(n, true), $"layer {n} on");
        }

        await Frames(25);
        MarkControl(_world.Minimap, "the minimap shows the same layers");
        await Shot(4);

        Say("Pins are bookmarks saved in the world project. Measure counts tiles between two cells; Route is A* over the client's own walking rules, and shows its length.");
        _world.Layers.Pins.Add("Tour pin", 0, 1498, 1627, 0);
        _world.SetLayer("Pins", true);
        layers.Measure.Click(0, 1496, 1628, 0);
        layers.Measure.Click(0, 1499, 1632, 0);
        _world.SetLayer("Measure", true);
        WorldData data = _world.Modes.Data;
        var fill = new ReachFill(data, 1496, 1628, data.LandZ(1496, 1628));
        fill.Finish();
        var goal = Enumerable.Range(1490, 14).SelectMany(x => Enumerable.Range(1620, 14).Select(y => (X: x, Y: y)))
            .FirstOrDefault(c => Math.Max(Math.Abs(c.X - 1496), Math.Abs(c.Y - 1628)) == 6 && fill.Reached(c.X, c.Y));
        layers.Route.Facet = 0;
        layers.Route.Path = Route.Find(data, 1496, 1628, data.LandZ(1496, 1628), goal.X, goal.Y);
        _world.SetLayer("Route", true);
        Check(layers.Route.Path != null, $"Route found a way of {layers.Route.Length} steps");
        await Frames(25);
        _overlay.SetDetail(Coordinates.Format(0, 1496, 1628, data.LandZ(1496, 1628)));
        await Shot(4);

        string dir = _world.WriteScenePack(null, Path.Combine(EditorData.RepoRoot, "build", "scene_packs"));
        Check(dir != null && File.Exists(Path.Combine(dir, "scene.json")), "the scene pack was written under build/scene_packs");
        Say("The Scene pack button writes the frame, an image per chosen mode and scene.json (camera, pixel-to-cell map, objects with boxes) under build/scene_packs. It sends nothing anywhere.");
        await Shot(3);

        layers.Pins.Remove(layers.Pins.All.FirstOrDefault(p => p.Name == "Tour pin"));
        layers.Route.Clear();
        layers.Measure.Clear();
        foreach (string n in _world.LayerNames)
        {
            _world.SetLayer(n, false);
        }

        _overlay.SetDetail(null);
        _overlay.ClearMarks();
    }
}
#endif
