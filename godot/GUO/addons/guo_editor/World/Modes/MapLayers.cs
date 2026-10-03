#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

/// <summary>Something a layer shows, listed for the smoke check, the scene pack and F3.</summary>
internal readonly record struct LayerItem(string Layer, string Label, int Facet, int X, int Y, int Z, string Detail = "");

/// <summary>Where a layer's cells are on the surface it draws (the World view, or the minimap).</summary>
internal sealed class LayerView
{
    public Func<float, float, float, Vector2> Project;
    public int Facet;
    public float PxPerCell = 44f;
    public Rect2 Screen;
    public float MinX, MinY, MaxX, MaxY;
    public bool Minimap;
    public Func<int, int, float> GroundZ = (_, _) => 0f;

    public bool Sees(Vector2 p) => Screen.Grow(40).HasPoint(p);

    public bool SeesBox(float x0, float y0, float x1, float y1) => !(x1 < MinX || x0 > MaxX || y1 < MinY || y0 > MaxY);
}

internal interface IMapLayer
{
    string Name { get; }
    string Summary { get; }
    bool On { get; set; }
    void Draw(IPaint p, LayerView v);
    IEnumerable<LayerItem> Items(int facet);
}

/// <summary>A live player or mobile, as the shard's bridge reports it.</summary>
internal readonly record struct LiveMobile(string Name, int Facet, int X, int Y, int Z, bool Player);

/// <summary>Sextant and plain coordinates, the way ModernUO's sextant computes them.</summary>
internal static class Coordinates
{
    public static string Sextant(int facet, int x, int y)
    {
        int xc = 1323, yc = 1624, xw = 5120, yh = 4096;
        if (facet is 0 or 1 && x >= 5120 && y >= 2304)
        {
            xc = 5936;
            yc = 3112;
        }

        double lon = (double)((x - xc) * 360) / xw, lat = (double)((y - yc) * 360) / yh;
        if (lon > 180.0)
        {
            lon = -180.0 + lon % 180.0;
        }

        if (lat > 180.0)
        {
            lat = -180.0 + lat % 180.0;
        }

        bool east = lon >= 0, south = lat >= 0;
        lon = Math.Abs(lon);
        lat = Math.Abs(lat);
        return $"{(int)lat}°{(int)(lat % 1.0 * 60)}'{(south ? "S" : "N")} {(int)lon}°{(int)(lon % 1.0 * 60)}'{(east ? "E" : "W")}";
    }

    public static string Format(int facet, int x, int y, int z) => $"{x}, {y}, {z}   {Sextant(facet, x, y)}";
}

/// <summary>A bookmark saved in the world project (<c>pins.json</c>).</summary>
internal sealed class Pin
{
    public string Name = "";
    public int Facet, X, Y, Z;
    public string Note = "";
}

/// <summary>
/// The map layers (ADR-0027): drawn on the World tab and on the minimap from
/// the same list. Data comes from what the editor already has (the world
/// project, its world objects, the open world) or from a shard folder
/// (ModernUO's <c>Data/regions.json</c> and <c>Data/Locations</c>) when one is set.
/// </summary>
internal sealed class MapLayers
{
    public readonly PlacesLayer Places;
    public readonly RegionsLayer Regions;
    public readonly SpawnsLayer Spawns;
    public readonly HousesLayer Houses;
    public readonly LiveLayer Live;
    public readonly PinsLayer Pins;
    public readonly MeasureLayer Measure;
    public readonly RouteLayer Route;
    public readonly List<IMapLayer> All;

    private string _shardFolder = "";
    public WorldHost Host;

    public Func<string> ProjectRoot = () => null;
    public Func<ShardObjects> Objects = () => null;
    public Func<IEnumerable<LiveMobile>> LiveSource = () => Array.Empty<LiveMobile>();

    public MapLayers(WorldHost host)
    {
        Host = host;
        Places = new PlacesLayer(this);
        Regions = new RegionsLayer(this);
        Spawns = new SpawnsLayer(this);
        Houses = new HousesLayer(this);
        Live = new LiveLayer(this);
        Pins = new PinsLayer(this);
        Measure = new MeasureLayer();
        Route = new RouteLayer();
        All = new List<IMapLayer> { Places, Regions, Spawns, Houses, Live, Pins, Measure, Route };
        _shardFolder = EditorData.Setting("UO_SHARD_DIST", "");
    }

    /// <summary>The ModernUO folder holding <c>Data/regions.json</c> and <c>Data/Locations</c> (UO_SHARD_DIST by default), or empty.</summary>
    public string ShardFolder
    {
        get => _shardFolder;
        set
        {
            _shardFolder = value ?? "";
            Places.Reload();
            Regions.Reload();
        }
    }

    public bool AnyOn => All.Any(l => l.On);

    public IMapLayer Named(string name) => All.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Draw(IPaint p, ModeContext ctx)
    {
        CellGeometry g = ctx.Geo;
        float half = g.Range + 2;
        var v = new LayerView
        {
            Project = g.Project,
            Facet = g.Facet,
            PxPerCell = 44f / g.Zoom,
            Screen = new Rect2(0, 0, g.Width, g.Height),
            MinX = g.CentreX - half,
            MaxX = g.CentreX + half,
            MinY = g.CentreY - half,
            MaxY = g.CentreY + half,
            GroundZ = (x, y) => g.ZAt(x, y),
        };
        DrawOn(p, v);
    }

    public void DrawOn(IPaint p, LayerView v)
    {
        foreach (IMapLayer l in All)
        {
            if (l.On)
            {
                l.Draw(p, v);
            }
        }
    }

    public IEnumerable<LayerItem> Items(string layer, int facet) => Named(layer)?.Items(facet) ?? Enumerable.Empty<LayerItem>();

    /// <summary>The loaders' view of the project root changed (a project opened): reload what lives in it.</summary>
    public void ProjectChanged() => Pins.Reload();

    internal static int FacetOfName(string name)
    {
        int f = ShardObjects.FacetOf(name.Replace(" ", ""));
        return f;
    }

    internal static void Outline(IPaint p, LayerView v, float x0, float y0, float x1, float y1, Color c, float w = 2f)
    {
        float zz = v.GroundZ((int)x0, (int)y0);
        Vector2 a = v.Project(x0, y0, zz), b = v.Project(x1, y0, zz), d = v.Project(x1, y1, zz), e = v.Project(x0, y1, zz);
        p.Line(a, b, c, w);
        p.Line(b, d, c, w);
        p.Line(d, e, c, w);
        p.Line(e, a, c, w);
    }
}

internal sealed class PlacesLayer : IMapLayer
{
    private readonly MapLayers _m;
    private List<LayerItem> _items;

    public PlacesLayer(MapLayers m) => _m = m;

    public string Name => "Places";
    public string Summary => "town and landmark names (Search/places.json, and the shard folder's Data/Locations)";
    public bool On { get; set; }

    public void Reload() => _items = null;

    private List<LayerItem> Load()
    {
        var items = new List<LayerItem>();
        try
        {
            using Godot.FileAccess f = Godot.FileAccess.Open("res://addons/guo_editor/Search/places.json", Godot.FileAccess.ModeFlags.Read);
            using JsonDocument doc = JsonDocument.Parse(f.GetAsText());
            foreach (JsonElement e in doc.RootElement.GetProperty("places").EnumerateArray())
            {
                items.Add(new LayerItem("Places", e.GetProperty("name").GetString(), e.GetProperty("map").GetInt32(),
                    e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32(), 0, "places.json"));
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] places layer: {ex.Message}");
        }

        string dir = Path.Combine(_m.ShardFolder, "Data", "Locations");
        if (_m.ShardFolder.Length > 0 && Directory.Exists(dir))
        {
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    int facet = MapLayers.FacetOfName(Path.GetFileNameWithoutExtension(file));
                    if (facet >= 0)
                    {
                        Walk(JsonNode.Parse(File.ReadAllText(file)), facet, items);
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[GUO editor] {file}: {ex.Message}");
                }
            }
        }

        return items;
    }

    private static void Walk(JsonNode n, int facet, List<LayerItem> items)
    {
        foreach (JsonNode l in n?["locations"]?.AsArray() ?? new JsonArray())
        {
            JsonArray at = l["location"]?.AsArray();
            if (at is { Count: >= 2 })
            {
                items.Add(new LayerItem("Places", (string)l["name"], facet, (int)at[0], (int)at[1], at.Count > 2 ? (int)at[2] : 0, "Data/Locations"));
            }
        }

        foreach (JsonNode c in n?["categories"]?.AsArray() ?? new JsonArray())
        {
            Walk(c, facet, items);
        }
    }

    public IEnumerable<LayerItem> Items(int facet) => (_items ??= Load()).Where(i => i.Facet == facet);

    public void Draw(IPaint p, LayerView v)
    {
        foreach (LayerItem i in Items(v.Facet))
        {
            Vector2 at = v.Project(i.X + 0.5f, i.Y + 0.5f, v.GroundZ(i.X, i.Y));
            if (!v.Sees(at))
            {
                continue;
            }

            p.Circle(at, 3, new Color(1f, 0.95f, 0.5f));
            p.Text(at + new Vector2(5, 4), i.Label, new Color(1f, 0.95f, 0.5f), v.Minimap ? 9 : 12);
        }
    }
}

internal sealed class RegionsLayer : IMapLayer
{
    internal sealed class Region
    {
        public string Name = "", Type = "";
        public int Facet;
        public List<(int X0, int Y0, int X1, int Y1)> Areas = new();
        public string Source = "";
    }

    private readonly MapLayers _m;
    private List<Region> _regions;

    public RegionsLayer(MapLayers m) => _m = m;

    public string Name => "Regions";
    public string Summary => "ModernUO Data/regions.json and the project's pack regions, outlined and named";
    public bool On { get; set; }

    public void Reload() => _regions = null;

    public IReadOnlyList<Region> All => _regions ??= Load();

    private List<Region> Load()
    {
        var list = new List<Region>();
        string file = Path.Combine(_m.ShardFolder, "Data", "regions.json");
        if (_m.ShardFolder.Length > 0 && File.Exists(file))
        {
            try
            {
                foreach (JsonNode r in JsonNode.Parse(File.ReadAllText(file)).AsArray())
                {
                    int facet = MapLayers.FacetOfName((string)r["Map"] ?? "");
                    if (facet < 0)
                    {
                        continue;
                    }

                    var reg = new Region { Name = (string)r["Name"] ?? "", Type = (string)r["$type"] ?? "", Facet = facet, Source = "regions.json" };
                    JsonNode area = r["Area"];
                    foreach (JsonNode a in area is JsonArray arr ? arr : area != null ? new JsonArray(area.DeepClone()) : new JsonArray())
                    {
                        if (a["x1"] != null)
                        {
                            reg.Areas.Add(((int)a["x1"], (int)a["y1"], (int)a["x2"], (int)a["y2"]));
                        }
                        else if (a["x"] != null && a["width"] != null)
                        {
                            reg.Areas.Add(((int)a["x"], (int)a["y"], (int)a["x"] + (int)a["width"] - 1, (int)a["y"] + (int)a["height"] - 1));
                        }
                    }

                    list.Add(reg);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GUO editor] {file}: {ex.Message}");
            }
        }

        // Pack regions (ADR-0026, kind "region"): <project>/regions/**/*.json, with facet and areas x, y, width, height.
        string root = _m.ProjectRoot();
        string packs = root == null ? null : Path.Combine(root, "regions");
        if (packs != null && Directory.Exists(packs))
        {
            foreach (string f in Directory.GetFiles(packs, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    JsonNode j = JsonNode.Parse(File.ReadAllText(f));
                    var reg = new Region { Name = (string)j["name"] ?? Path.GetFileNameWithoutExtension(f), Type = "PackRegion", Facet = (int?)j["facet"] ?? 0, Source = "pack" };
                    foreach (JsonNode a in j["areas"]?.AsArray() ?? new JsonArray())
                    {
                        reg.Areas.Add(((int)a["x"], (int)a["y"], (int)a["x"] + (int)a["width"] - 1, (int)a["y"] + (int)a["height"] - 1));
                    }

                    list.Add(reg);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[GUO editor] {f}: {ex.Message}");
                }
            }
        }

        return list;
    }

    public static Color ColourOf(string type) => type switch
    {
        "TownRegion" => new Color(0.4f, 1f, 0.4f),
        "DungeonRegion" => new Color(0.8f, 0.5f, 1f),
        "GuardedRegion" => new Color(0.4f, 0.7f, 1f),
        "PackRegion" => new Color(1f, 0.4f, 0.7f),
        "NoHousingRegion" => new Color(1f, 0.5f, 0.4f),
        _ => new Color(1f, 0.8f, 0.3f),
    };

    public IEnumerable<LayerItem> Items(int facet) =>
        All.Where(r => r.Facet == facet && r.Areas.Count > 0)
            .Select(r => new LayerItem("Regions", r.Name, facet, (r.Areas[0].X0 + r.Areas[0].X1) / 2, (r.Areas[0].Y0 + r.Areas[0].Y1) / 2, 0, r.Type));

    public void Draw(IPaint p, LayerView v)
    {
        foreach (Region r in All)
        {
            if (r.Facet != v.Facet)
            {
                continue;
            }

            Color c = ColourOf(r.Type);
            bool labelled = false;
            foreach (var a in r.Areas)
            {
                if (!v.SeesBox(a.X0, a.Y0, a.X1 + 1, a.Y1 + 1))
                {
                    continue;
                }

                MapLayers.Outline(p, v, a.X0, a.Y0, a.X1 + 1, a.Y1 + 1, c, v.Minimap ? 1f : 2f);
                if (!labelled && r.Name.Length > 0)
                {
                    Vector2 at = v.Project((a.X0 + a.X1) / 2f, (a.Y0 + a.Y1) / 2f, v.GroundZ((a.X0 + a.X1) / 2, (a.Y0 + a.Y1) / 2));
                    if (v.Sees(at))
                    {
                        p.Text(at, r.Name, c, v.Minimap ? 9 : 12);
                        labelled = true;
                    }
                }
            }
        }
    }
}

internal sealed class SpawnsLayer : IMapLayer
{
    private readonly MapLayers _m;

    public SpawnsLayer(MapLayers m) => _m = m;

    public string Name => "Spawns";
    public string Summary => "the project's spawners with their home (orange) and walking (cyan) range rings";
    public bool On { get; set; }

    private IEnumerable<ShardSpawner> Of(int facet) => _m.Objects()?.Spawners.Where(s => ShardObjects.FacetOf(s.Map) == facet) ?? Enumerable.Empty<ShardSpawner>();

    public IEnumerable<LayerItem> Items(int facet) =>
        Of(facet).Select(s => new LayerItem("Spawns", s.Entries.Count > 0 ? s.Entries[0].Name : "spawner", facet, s.X, s.Y, s.Z, $"home {s.HomeRange} walk {s.WalkingRange}"));

    public void Draw(IPaint p, LayerView v)
    {
        var home = new Color(1f, 0.6f, 0.1f);
        var walk = new Color(0.2f, 0.9f, 1f);
        foreach (ShardSpawner s in Of(v.Facet))
        {
            Vector2 at = v.Project(s.X + 0.5f, s.Y + 0.5f, s.Z);
            int reach = Math.Max(s.HomeRange, s.WalkingRange);
            if (!v.SeesBox(s.X - reach, s.Y - reach, s.X + reach + 1, s.Y + reach + 1))
            {
                continue;
            }

            if (s.HomeRange > 0)
            {
                MapLayers.Outline(p, v, s.X - s.HomeRange, s.Y - s.HomeRange, s.X + s.HomeRange + 1, s.Y + s.HomeRange + 1, home, 1.5f);
            }

            if (s.WalkingRange > 0)
            {
                MapLayers.Outline(p, v, s.X - s.WalkingRange, s.Y - s.WalkingRange, s.X + s.WalkingRange + 1, s.Y + s.WalkingRange + 1, walk, 1.5f);
            }

            p.Circle(at, v.Minimap ? 2.5f : 5f, home);
            p.Circle(at, v.Minimap ? 2.5f : 5f, Colors.Black, false);
            if (!v.Minimap)
            {
                p.Text(at + new Vector2(7, 4), s.Entries.Count > 0 ? $"{s.Entries[0].Name} x{s.Count}" : "spawner", home);
            }
        }
    }
}

internal sealed class HousesLayer : IMapLayer
{
    private readonly MapLayers _m;

    public HousesLayer(MapLayers m) => _m = m;

    public string Name => "Houses";
    public string Summary => "footprints of the multis (houses, boats) in the world";
    public bool On { get; set; }

    private IEnumerable<GUO.Game.GameObjects.House> Houses =>
        _m.Host?.World?.HouseManager?.Houses ?? (IEnumerable<GUO.Game.GameObjects.House>)Array.Empty<GUO.Game.GameObjects.House>();

    public IEnumerable<LayerItem> Items(int facet) =>
        Houses.Where(h => h.Components.Count > 0 && _m.Host.World.MapIndex == facet)
            .Select(h => new LayerItem("Houses", $"multi 0x{h.Serial:X8}", facet, h.Components[0].X, h.Components[0].Y, h.Components[0].Z, $"{h.Components.Count} parts"));

    public void Draw(IPaint p, LayerView v)
    {
        if (_m.Host?.World == null || _m.Host.World.MapIndex != v.Facet)
        {
            return;
        }

        foreach (var h in Houses)
        {
            if (h.Components.Count == 0)
            {
                continue;
            }

            var c = Color.FromHsv((h.Serial * 0.61803f) % 1f, 0.6f, 1f, 0.45f);
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            foreach (var m in h.Components)
            {
                x0 = Math.Min(x0, m.X);
                y0 = Math.Min(y0, m.Y);
                x1 = Math.Max(x1, m.X);
                y1 = Math.Max(y1, m.Y);
            }

            if (!v.SeesBox(x0, y0, x1 + 1, y1 + 1))
            {
                continue;
            }

            if (v.Minimap)
            {
                MapLayers.Outline(p, v, x0, y0, x1 + 1, y1 + 1, c with { A = 1f }, 1f);
                continue;
            }

            var seen = new HashSet<long>();
            foreach (var m in h.Components)
            {
                if (seen.Add(((long)m.X << 20) | (uint)m.Y))
                {
                    p.Quad(v.Project(m.X, m.Y, m.Z), v.Project(m.X + 1, m.Y, m.Z), v.Project(m.X + 1, m.Y + 1, m.Z), v.Project(m.X, m.Y + 1, m.Z), c);
                }
            }
        }
    }
}

internal sealed class LiveLayer : IMapLayer
{
    private readonly MapLayers _m;

    public LiveLayer(MapLayers m) => _m = m;

    public string Name => "Live";
    public string Summary => "players and mobiles the shard reports over the UO Shard dock's bridge (when connected)";
    public bool On { get; set; }

    public IEnumerable<LayerItem> Items(int facet) =>
        _m.LiveSource().Where(l => l.Facet == facet).Select(l => new LayerItem("Live", l.Name, facet, l.X, l.Y, l.Z, l.Player ? "player" : "mobile"));

    public void Draw(IPaint p, LayerView v)
    {
        foreach (LiveMobile l in _m.LiveSource())
        {
            if (l.Facet != v.Facet)
            {
                continue;
            }

            Vector2 at = v.Project(l.X + 0.5f, l.Y + 0.5f, l.Z);
            if (!v.Sees(at))
            {
                continue;
            }

            Color c = l.Player ? new Color(0.3f, 1f, 0.3f) : new Color(0.8f, 0.8f, 0.8f);
            p.Circle(at, l.Player ? 4f : 3f, c);
            p.Circle(at, l.Player ? 4f : 3f, Colors.Black, false);
            if (!v.Minimap)
            {
                p.Text(at + new Vector2(6, 4), l.Name, c);
            }
        }
    }
}

internal sealed class PinsLayer : IMapLayer
{
    private readonly MapLayers _m;
    private List<Pin> _pins;
    private string _root;

    public PinsLayer(MapLayers m) => _m = m;

    public string Name => "Pins";
    public string Summary => "bookmarks saved in the world project (pins.json)";
    public bool On { get; set; }

    private string File_ => _m.ProjectRoot() is { } r ? Path.Combine(r, "pins.json") : null;

    public void Reload() => _pins = null;

    public IReadOnlyList<Pin> All
    {
        get
        {
            string root = _m.ProjectRoot();
            if (_pins == null || root != _root)
            {
                _root = root;
                _pins = Read();
            }

            return _pins;
        }
    }

    private List<Pin> Read()
    {
        var list = new List<Pin>();
        string f = File_;
        if (f == null || !File.Exists(f))
        {
            return list;
        }

        try
        {
            foreach (JsonNode n in JsonNode.Parse(File.ReadAllText(f))["pins"]?.AsArray() ?? new JsonArray())
            {
                list.Add(new Pin { Name = (string)n["name"] ?? "", Facet = (int)n["facet"], X = (int)n["x"], Y = (int)n["y"], Z = (int?)n["z"] ?? 0, Note = (string)n["note"] ?? "" });
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] pins.json: {ex.Message}");
        }

        return list;
    }

    private void Save()
    {
        string f = File_;
        if (f == null)
        {
            return;
        }

        var arr = new JsonArray();
        foreach (Pin p in _pins)
        {
            arr.Add(new JsonObject { ["name"] = p.Name, ["facet"] = p.Facet, ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z, ["note"] = p.Note });
        }

        var root = new JsonObject { ["format"] = 1, ["pins"] = arr };
        File.WriteAllText(f, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>Adds a pin and saves it. Null when no world project is open.</summary>
    public Pin Add(string name, int facet, int x, int y, int z, string note = "")
    {
        if (File_ == null)
        {
            return null;
        }

        var pin = new Pin { Name = string.IsNullOrWhiteSpace(name) ? $"Pin {All.Count + 1}" : name, Facet = facet, X = x, Y = y, Z = z, Note = note };
        All.ToString();
        _pins.Add(pin);
        Save();
        return pin;
    }

    public bool Remove(Pin pin)
    {
        bool ok = All.Count > 0 && _pins.Remove(pin);
        if (ok)
        {
            Save();
        }

        return ok;
    }

    public IEnumerable<LayerItem> Items(int facet) =>
        All.Where(p => p.Facet == facet).Select(p => new LayerItem("Pins", p.Name, facet, p.X, p.Y, p.Z, p.Note));

    public void Draw(IPaint p, LayerView v)
    {
        var c = new Color(1f, 0.3f, 0.5f);
        foreach (Pin pin in All.Where(x => x.Facet == v.Facet))
        {
            Vector2 at = v.Project(pin.X + 0.5f, pin.Y + 0.5f, pin.Z);
            if (!v.Sees(at))
            {
                continue;
            }

            float s = v.Minimap ? 4f : 8f;
            p.Line(at, at + new Vector2(0, -s * 2), Colors.Black, 3);
            p.Line(at, at + new Vector2(0, -s * 2), c, 1.5f);
            p.Quad(at + new Vector2(0, -s * 2), at + new Vector2(s * 1.4f, -s * 1.6f), at + new Vector2(0, -s * 1.1f), at + new Vector2(0, -s * 1.1f), c);
            p.Text(at + new Vector2(4, -s * 2), pin.Name, c);
        }
    }
}

internal sealed class MeasureLayer : IMapLayer
{
    public string Name => "Measure";
    public string Summary => "the Measure tool: click two cells, read the distance in tiles";
    public bool On { get; set; }

    public (int Facet, int X, int Y, int Z)? A, B;

    /// <summary>Distance in tiles: the larger of the two axes (how far a walker needs), and the straight line.</summary>
    public (int Tiles, double Straight, int Dx, int Dy)? Result =>
        A is { } a && B is { } b
            ? (Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)), Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)), b.X - a.X, b.Y - a.Y)
            : null;

    public void Click(int facet, int x, int y, int z)
    {
        if (A == null || B != null)
        {
            A = (facet, x, y, z);
            B = null;
        }
        else
        {
            B = (facet, x, y, z);
        }
    }

    public void Clear() => A = B = null;

    public IEnumerable<LayerItem> Items(int facet)
    {
        if (A is { } a && a.Facet == facet)
        {
            yield return new LayerItem("Measure", "A", facet, a.X, a.Y, a.Z);
        }

        if (B is { } b && b.Facet == facet)
        {
            yield return new LayerItem("Measure", Result is { } r ? $"{r.Tiles} tiles" : "B", facet, b.X, b.Y, b.Z);
        }
    }

    public void Draw(IPaint p, LayerView v)
    {
        var c = new Color(1f, 1f, 0.2f);
        foreach (var pt in new[] { A, B })
        {
            if (pt is { } q && q.Facet == v.Facet)
            {
                p.Circle(v.Project(q.X + 0.5f, q.Y + 0.5f, q.Z), 4, c);
            }
        }

        if (A is { } a && B is { } b && a.Facet == v.Facet && Result is { } r)
        {
            Vector2 pa = v.Project(a.X + 0.5f, a.Y + 0.5f, a.Z), pb = v.Project(b.X + 0.5f, b.Y + 0.5f, b.Z);
            p.Line(pa, pb, Colors.Black, 4);
            p.Line(pa, pb, c, 2);
            p.Text((pa + pb) / 2 + new Vector2(6, -6), $"{r.Tiles} tiles ({r.Dx:+0;-0;0}, {r.Dy:+0;-0;0})  straight {r.Straight:0.0}", c, 12);
        }
    }
}

internal sealed class RouteLayer : IMapLayer
{
    public string Name => "Route";
    public string Summary => "the Route tool: click a start then a goal; the path is A* over the client's walking rules";
    public bool On { get; set; }

    public (int Facet, int X, int Y, int Z)? Start;
    public List<(int X, int Y, sbyte Z)> Path;
    public int Facet;
    public string Note = "";

    public int Length => Path == null ? 0 : Path.Count - 1;

    public void Clear()
    {
        Start = null;
        Path = null;
        Note = "";
    }

    public IEnumerable<LayerItem> Items(int facet)
    {
        if (Path != null && Facet == facet && Path.Count > 0)
        {
            yield return new LayerItem("Route", $"{Length} steps", facet, Path[^1].X, Path[^1].Y, Path[^1].Z);
        }
    }

    public void Draw(IPaint p, LayerView v)
    {
        var c = new Color(0.4f, 0.8f, 1f);
        if (Start is { } s && s.Facet == v.Facet)
        {
            p.Circle(v.Project(s.X + 0.5f, s.Y + 0.5f, s.Z), 4, c);
        }

        if (Path == null || Facet != v.Facet || Path.Count == 0)
        {
            return;
        }

        for (int i = 1; i < Path.Count; i++)
        {
            Vector2 a = v.Project(Path[i - 1].X + 0.5f, Path[i - 1].Y + 0.5f, Path[i - 1].Z);
            Vector2 b = v.Project(Path[i].X + 0.5f, Path[i].Y + 0.5f, Path[i].Z);
            p.Line(a, b, Colors.Black, 4);
            p.Line(a, b, c, 2);
        }

        Vector2 end = v.Project(Path[^1].X + 0.5f, Path[^1].Y + 0.5f, Path[^1].Z);
        p.Circle(end, 4, c);
        p.Text(end + new Vector2(6, -6), $"route: {Length} steps", c, 12);
    }
}
#endif
