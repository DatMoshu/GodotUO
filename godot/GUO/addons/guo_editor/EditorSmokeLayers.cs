#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using Godot;
using GUO.Game.GameObjects;

/// <summary>The smoke stage for ADR-0027's map layers, at map0 1496,1628, with a regions fixture under tools/editor_smoke/fixtures.</summary>
public partial class EditorSmoke
{
    private void AddLayerSteps()
    {
        MapLayers layers = _world.Layers;
        ImagePaint canvas = null;
        House house = null;
        uint serial = 0x4000_0200;

        _steps.Add((2, () =>
        {
            ModeContext ctx = _world.Modes.Context(false);
            WorldData data = _world.Modes.Data;
            // The editor's own pack store must not leak the machine's installed packs into the fixture counts.
            layers.PackStoreRoot = () => Path.Combine(_out, "store_empty");
            layers.ShardFolder = FixtureShard();

            // Places: the hand kept list plus the shard folder's Data/Locations.
            var places = layers.Items("Places", 0).ToList();
            _modesReport["places_facet0"] = places.Count;
            ModeExpect(places.Any(p => p.Label == "Britain") && places.Any(p => p.Label == "Fixture Landmark"), "layer_places");

            // Regions: the fixture's two on Felucca, its one on Trammel.
            var regions = layers.Items("Regions", 0).ToList();
            _modesReport["regions_facet0"] = regions.Select(r => r.Label).ToList();
            ModeExpect(regions.Count == 2 && regions.Any(r => r.Label == "Fixture Square") && layers.Items("Regions", 1).Count() == 1, "layer_regions_fixture");

            // Pack regions from an installed pack and a deployed server-content.json, each labelled with its source.
            string store = BuildRegionPackFixture(Path.Combine(_out, "store_fixture"), Path.Combine(_out, "deployed_shard"));
            layers.PackStoreRoot = () => store;
            layers.ShardFolder = Path.Combine(_out, "deployed_shard");
            var packRegions = layers.Items("Regions", 0).ToList();
            _modesReport["pack_regions"] = packRegions.Select(r => $"{r.Label} | {r.Detail}").ToList();
            ModeExpect(packRegions.Any(r => r.Label == "Store Fixture Region" && r.Detail.Contains("installed pack store-fixture-regions 1.0.0")), "layer_regions_installed_pack");
            ModeExpect(packRegions.Any(r => r.Label == "Deployed Fixture Region" && r.Detail.Contains("deployed fixture-deploy:deployed")), "layer_regions_deployed");
            layers.PackStoreRoot = () => Path.Combine(_out, "store_empty");
            layers.ShardFolder = FixtureShard();

            // Spawns: one in the project, with its home ring.
            ShardSpawner sp = _world.Objects.PlaceSpawner(0, 1500, 1630, data.LandZ(1500, 1630), "Horse");
            ModeExpect(sp != null && layers.Items("Spawns", 0).Count() == 1, "layer_spawns");

            // Houses: a three part multi through the game's own House.
            var world = _world.Host.World;
            house = new House(world, serial, 0, false);
            for (int i = 0; i < 3; i++)
            {
                house.Add(0x0E75, 0, (ushort)(1490 + i), 1620, data.LandZ(1490 + i, 1620), false, false);
            }

            // Other stages may have placed houses already: the fixture adds exactly one.
            int housesBefore = layers.Items("Houses", 0).Count();
            world.HouseManager.Add(serial, house);
            ModeExpect(layers.Items("Houses", 0).Count() == housesBefore + 1, "layer_houses");

            // Live: a mobile and a player as the bridge would report them.
            layers.LiveSource = () => new[] { new LiveMobile("Fixture Player", 0, 1497, 1629, 0, true), new LiveMobile("a horse", 0, 1494, 1630, 0, false) };
            ModeExpect(layers.Items("Live", 0).Count() == 2, "layer_live");

            // Pins: saved in the project and read back.
            Pin pin = layers.Pins.Add("Smoke pin", 0, 1498, 1627, 0);
            string pins = Path.Combine(_world.Host.Project.Root, "pins.json");
            ModeExpect(pin != null && File.Exists(pins) && File.ReadAllText(pins).Contains("Smoke pin"), "layer_pins_saved");
            layers.Pins.Reload();
            ModeExpect(layers.Items("Pins", 0).Any(p => p.Label == "Smoke pin"), "layer_pins_reload");

            // Measure: 3 east, 4 south is 4 tiles for a walker and 5 in a straight line.
            layers.Measure.Click(0, 1496, 1628, 0);
            layers.Measure.Click(0, 1499, 1632, 0);
            ModeExpect(layers.Measure.Result is { Tiles: 4 } r && Math.Abs(r.Straight - 5) < 1e-6, "layer_measure");

            // Route: A* on the client's walking rules to a cell the fill says is reachable.
            var fill = new ReachFill(data, ModeX, ModeY, data.LandZ(ModeX, ModeY));
            fill.Finish();
            var goal = NearCentre(8).FirstOrDefault(c => Math.Max(Math.Abs(c.X - ModeX), Math.Abs(c.Y - ModeY)) == 6
                && fill.Reached(c.X, c.Y) && data.WalkAt(c.X, c.Y) == Walk.Walkable);
            var path = Route.Find(data, ModeX, ModeY, data.LandZ(ModeX, ModeY), goal.X, goal.Y);
            layers.Route.Facet = 0;
            layers.Route.Path = path;
            _modesReport["route_length"] = layers.Route.Length;
            ModeExpect(path != null && layers.Route.Length >= 6 && layers.Route.Length <= 24
                && path.Zip(path.Skip(1), (a, b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y))).All(d => d == 1), "layer_route");

            // Sextant, as ModernUO's Sextant item computes it.
            string sextant = Coordinates.Sextant(0, ModeX, ModeY);
            _modesReport["sextant"] = sextant;
            ModeExpect(sextant == "0°21'S 12°9'E", "sextant_britain");

            // Switch them all on through the menu path, draw them over the Types mode, and look for each one's colour.
            foreach (string n in _world.LayerNames)
            {
                ModeExpect(_world.SetLayer(n, true) && _world.LayerOn(n), $"layer_on_{n}");
            }

            ctx = _world.Modes.Context(false);
            canvas = new ImagePaint(ctx.Geo.Width, ctx.Geo.Height);
            new TypesMode().Draw(canvas, ctx);
            layers.Draw(canvas, ctx);
            Directory.CreateDirectory(Path.Combine(_out, "modes"));
            canvas.ToImage().SavePng(Path.Combine(_out, "modes", "layers.png"));
            ModeExpect(Painted(canvas, MapLayers.ZoneColour("Fixture Square")), "layers_region_outline_drawn");
            ModeExpect(Painted(canvas, new Color(1f, 0.6f, 0.1f)), "layers_spawn_ring_drawn");
            ModeExpect(Painted(canvas, new Color(0.3f, 1f, 0.3f)), "layers_live_player_drawn");
            ModeExpect(Painted(canvas, new Color(0.4f, 0.8f, 1f)), "layers_route_drawn");
            ModeExpect(canvas.Labels.Any(l => l.Text == "Fixture Square") && canvas.Labels.Any(l => l.Text == "Britain"), "layers_labels");
            ModeExpect(_world.Minimap?.Layers == layers, "minimap_shares_layers");
        }));

        // The scene pack: shot (windowed only), a mode image each, scene.json.
        _steps.Add((2, () =>
        {
            _world.SetViewMode("Walkability");
            string dir = _world.WriteScenePack(null, Path.Combine(_out, "scene_packs"));
            _modesReport["scene_pack"] = dir;
            ModeExpect(dir != null && File.Exists(Path.Combine(dir, "scene.json")) && File.Exists(Path.Combine(dir, "walkability.png")), "scene_pack_files");
            if (dir == null)
            {
                return;
            }

            var scene = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "scene.json")));
            ModeExpect((int)scene["format"] == 1 && (int)scene["facet"] == 0 && (int)scene["camera"]["centre"][0] == ModeX, "scene_json_camera");
            int objects = scene["objects"].AsArray().Count;
            _modesReport["scene_objects"] = objects;
            ModeExpect(objects > 20 && scene["layers"]["Regions"] != null && scene["modes"].AsArray().Count == 1, "scene_json_objects_layers_modes");
            var first = scene["pixel_to_cell"]["grid"].AsArray()[5];
            CellGeometry geo = _world.Modes.Context(false).Geo;
            Vector2 back = geo.Project((float)first["x"], (float)first["y"], geo.CentreZ);
            ModeExpect(Math.Abs(back.X - (int)first["px"]) <= 3 && Math.Abs(back.Y - (int)first["py"]) <= 3, "scene_json_pixel_to_cell_round_trips");
            _world.SetViewMode("Off");
            string defaults = _world.WriteScenePack(null, Path.Combine(_out, "scene_packs"));
            ModeExpect(defaults != null && File.Exists(Path.Combine(defaults, "height.png")) && File.Exists(Path.Combine(defaults, "types.png")), "scene_pack_default_modes");
        }));

        // Take the test objects away again.
        _steps.Add((2, () =>
        {
            foreach (string n in _world.LayerNames)
            {
                _world.SetLayer(n, false);
            }

            layers.LiveSource = () => Array.Empty<LiveMobile>();
            layers.Pins.Remove(layers.Pins.All.FirstOrDefault(p => p.Name == "Smoke pin"));
            layers.Route.Clear();
            layers.Measure.Clear();
            house?.ClearComponents();
            _world.Host.World.HouseManager.Remove(serial);
            foreach (ShardSpawner s in _world.Objects.Objects.Spawners.ToList())
            {
                _world.Objects.Delete(s.Id);
            }

            _world.Modes.Invalidate();
            _world.SetFixedSize(null);
            _modesReport.TryAdd("ok", true);
        }));
    }

    /// <summary>An installed region pack (hash-checked by StoreClient) in a store folder, and a shard folder with a deployed server-content.json.</summary>
    private static string BuildRegionPackFixture(string store, string shard)
    {
        const string id = "store-fixture-regions", version = "1.0.0";
        string dir = Path.Combine(store, id, version);
        Directory.CreateDirectory(dir);
        string row = "{\"name\":\"Store Fixture Region\",\"facet\":0,\"priority\":50,\"areas\":[{\"x\":1480,\"y\":1610,\"z\":-128,\"width\":30,\"height\":30,\"depth\":255}]}";
        File.WriteAllText(Path.Combine(dir, "region.json"), row);
        File.WriteAllBytes(Path.Combine(dir, "preview.png"), new byte[] { 1, 2, 3 });
        var manifest = new GUO.Store.StoreManifest
        {
            Schema = "guo/store-pack@2", Id = id, Version = version, Kind = "content", Title = "Store fixture regions", Author = "smoke",
            Licence = "CC0-1.0", MinProfileVersion = 0, Preview = "preview.png", Target = "server",
            Dependencies = new System.Collections.Generic.Dictionary<string, string>(),
            Files = new System.Collections.Generic.Dictionary<string, string>
            {
                ["region.json"] = GUO.Store.StorePack.HashFile(Path.Combine(dir, "region.json")),
                ["preview.png"] = GUO.Store.StorePack.HashFile(Path.Combine(dir, "preview.png")),
            },
            Components = new System.Collections.Generic.List<GUO.Store.StoreComponent>
            {
                new() { Id = "square", Type = "region", Target = "server", Entry = "region.json" },
            },
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(manifest));

        string deployed = Path.Combine(shard, "Data", "GUO");
        Directory.CreateDirectory(deployed);
        File.WriteAllText(Path.Combine(deployed, "server-content.json"),
            "{\"regions\":[{\"identity\":\"fixture-deploy:deployed\",\"music_id\":-1,\"content\":"
            + "{\"name\":\"Deployed Fixture Region\",\"facet\":0,\"priority\":50,\"areas\":[{\"x\":1520,\"y\":1610,\"z\":-128,\"width\":20,\"height\":20,\"depth\":255}]}}]}");
        return store;
    }
}
#endif
