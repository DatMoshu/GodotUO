using System.Globalization;
using System.Text.Json;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CentrED.MapGen.Passes.Terrain;

public sealed class ImageImportParams
{
    [TunableDisplay("Terrain image (PNG/BMP)", Tooltip = "RGB image; pixels matched to palette → BiomeId/LandId")]
    public string TerrainImagePath { get; set; } = "design/Core/MapPaint/terrain.png";

    [TunableDisplay("Altitude image (PNG/BMP)", Tooltip = "Greyscale or palette image; brightness 0..255 → Z range below")]
    public string AltitudeImagePath { get; set; } = "design/Core/MapPaint/altitude.png";

    [TunableDisplay("Palette JSON (optional)", Tooltip = "Custom palette path. Empty = use baked-in DragonMod-style palette")]
    public string PaletteJsonPath { get; set; } = "";

    [TunableDisplay("Min Z (black pixel)")] [TunableRange(-128, 127)]
    public int MinZ { get; set; } = -10;

    [TunableDisplay("Max Z (white pixel)")] [TunableRange(-128, 127)]
    public int MaxZ { get; set; } = 80;

    [TunableDisplay("Sample-X origin in image (px)", Tooltip = "Image pixel that maps to scope.X1")]
    [TunableRange(0, 65535)]
    public int OriginX { get; set; } = 0;

    [TunableDisplay("Sample-Y origin in image (px)")]
    [TunableRange(0, 65535)]
    public int OriginY { get; set; } = 0;

    [TunableDisplay("Pixel scale", Tooltip = "World tiles per image pixel. 1 = 1:1.")]
    [TunableRange(1, 16)]
    public int PixelScale { get; set; } = 1;
}

// DragonMod / Landscaper-style worldgen entry point: artist paints terrain + altitude
// images, this pass fills Biome[] / LandId[] / Height_Z[] from pixel colours. Place this
// EARLY in the pipeline (replaces NoiseHeightPass + BiomeAssignPass + LandIdResolvePass)
// when active. Downstream passes (LandTransition, CoastSmooth, AutoCoast, ForestScatter,
// MountainEdgeStatics) run unchanged on top.
//
// Reads:  None.
// Writes: Height + Biome + LandId.
public sealed class ImageImportPass : IGenerationPass
{
    public string Name => "Image Import";
    public string Category => "Terrain";

    public IrFields Reads => IrFields.None;
    public IrFields Writes => IrFields.Height | IrFields.Biome | IrFields.LandId;

    public object CreateDefaultParams() => new ImageImportParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (ImageImportParams)parameters;
        var ir = ctx.IR;

        var terrainPath = RepoRootResolver.Resolve(p.TerrainImagePath);
        var altitudePath = RepoRootResolver.Resolve(p.AltitudeImagePath);
        if (!File.Exists(terrainPath))
        {
            ctx.Report.Warnings.Add($"ImageImport: terrain not found: {terrainPath}");
            return;
        }
        if (!File.Exists(altitudePath))
        {
            ctx.Report.Warnings.Add($"ImageImport: altitude not found: {altitudePath}");
            return;
        }

        var palette = LoadPalette(p.PaletteJsonPath);
        ir.EnsureHeight();
        ir.EnsureBiome();
        ir.EnsureLandId();
        var z = ir.Height_Z!;
        var biome = ir.Biome!;
        var land = ir.LandId!;
        var scope = ir.Scope;

        using var terrain = Image.Load<Rgba32>(terrainPath);
        using var altitude = Image.Load<L8>(altitudePath);

        int sampled = 0, paletteMisses = 0;
        int scale = Math.Max(1, p.PixelScale);
        int zRange = p.MaxZ - p.MinZ;
        var diag = new BiomeDiagnostics();

        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int px = p.OriginX + (x - scope.X1) / scale;
            int py = p.OriginY + (y - scope.Y1) / scale;
            if ((uint)px >= (uint)terrain.Width || (uint)py >= (uint)terrain.Height) continue;

            var rgba = terrain[px, py];
            var entry = palette.Match(rgba.R, rgba.G, rgba.B);
            if (entry is null)
            {
                paletteMisses++;
                continue;
            }

            int idx = ir.Index(x, y);
            biome[idx] = (byte)entry.Biome;
            // Pick a random LandId from the entry's pool. Earlier behaviour ("first only")
            // produced uniform mountain faces (all 0xDC) and required LandIdResolvePass to
            // run downstream — but ImageImport disables LandIdResolve, so mountain interiors
            // never got variety. Random pick + weighted palette pools (e.g. Beach has 0x19
            // listed 8x and the rough variants once each) gives the right base+sprinkle.
            ushort assignedLand = entry.LandIds.Length > 0
                ? entry.LandIds[ctx.Rng.Next(entry.LandIds.Length)]
                : (ushort)0x03;
            land[idx] = assignedLand;
            var bstats = diag.GetOrAdd(entry.Biome);
            bstats.CellsInScope++;
            bstats.LandIdHistogram.TryGetValue(assignedLand, out var lc);
            bstats.LandIdHistogram[assignedLand] = lc + 1;

            if ((uint)px < (uint)altitude.Width && (uint)py < (uint)altitude.Height)
            {
                byte b = altitude[px, py].PackedValue;
                // Round-to-nearest (+128 ≈ ½·255) instead of int truncation.
                // The old `(zRange * b) / 255` truncated mid-range shades down by
                // 1 — shade=28 (every painter's intended sea-level value) landed
                // at Z=-1, and CoastSmoothPass:75-87 then reclassified those
                // cells as water, dropping ~60% of painted islands into the sea.
                int zv = p.MinZ + ((zRange * b) + 128) / 255;
                z[idx] = (sbyte)Math.Clamp(zv, sbyte.MinValue, sbyte.MaxValue);
            }
            sampled++;
        }

        ctx.Report.TilesTouched = sampled;
        ctx.Report.Diagnostics = diag;
        ctx.Report.Notes.Add($"sampled={sampled} paletteMisses={paletteMisses}");
        if (paletteMisses > 0)
            ctx.Report.Warnings.Add($"ImageImport: {paletteMisses} pixels did not match any palette entry — paint with the swatch colours only.");
    }

    private static Palette LoadPalette(string jsonPath)
    {
        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            var resolved = RepoRootResolver.Resolve(jsonPath);
            if (File.Exists(resolved))
            {
                try
                {
                    using var fs = File.OpenRead(resolved);
                    using var doc = JsonDocument.Parse(fs);
                    return Palette.FromJson(doc.RootElement);
                }
                catch { /* fall through to default */ }
            }
        }
        return Palette.Default;
    }

    // ---- palette ----
    public sealed record PaletteEntry(string Name, byte R, byte G, byte B, BiomeId Biome, ushort[] LandIds);

    public sealed class Palette
    {
        private readonly PaletteEntry[] _entries;
        public Palette(PaletteEntry[] entries) { _entries = entries; }
        public IReadOnlyList<PaletteEntry> Entries => _entries;

        // Nearest-colour match (squared RGB distance). Indexed PNGs paint exact colours so
        // exact match is the common case; the nearest-fallback rescues lossy compressors.
        public PaletteEntry? Match(byte r, byte g, byte b)
        {
            if (_entries.Length == 0) return null;
            PaletteEntry? best = null;
            int bestD = int.MaxValue;
            foreach (var e in _entries)
            {
                int dr = e.R - r, dg = e.G - g, db = e.B - b;
                int d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = e; }
            }
            // Reject very-far matches so a stray colour isn't silently classified.
            return bestD <= 64 * 64 * 3 ? best : null;
        }

        // Baked-in palette modelled on DragonMod's default colortable. Hex colours chosen
        // to be far apart in RGB space so painters can pick from a swatch reliably.
        public static Palette Default => FromTables(TileTables.Default);

        public static Palette FromTables(TileTables tables) => new(ColorSwatches.Entries.Select(e =>
            tables.Land.TryGetValue(e.Biome, out var ids) && ids.Length > 0 ? e with { LandIds = ids.ToArray() } : e).ToArray());

        // RGB swatches stay stable for existing painted documents. The legacy
        // tile pools below are fallbacks only for biomes absent from TileTables.
        private static Palette ColorSwatches { get; } = new(new[]
        {
            new PaletteEntry("DeepWater",    0x00, 0x00, 0x80, BiomeId.DeepWater,    new ushort[] { 0xA8, 0xA9, 0xAA, 0xAB }),
            // ShallowWater: 0x136/0x137 are the canonical shallow-water variants per
            // TileFlags.cs:11-16 IsWaterLandId. Previously shared 0xA8-0xAB with DeepWater
            // making the two biomes visually identical.
            new PaletteEntry("ShallowWater", 0x40, 0x80, 0xC0, BiomeId.ShallowWater, new ushort[] { 0x0136, 0x0137 }),
            // Beach: WALKABLE sand is ONLY 0x16-0x19 (4 soft-sand variants). 0x1A-0x1C are
            // Impassable in tiledata (sloped-sand for cliff/dune edges, not beach fill).
            // 0x64 is also impassable (decorative scatter). Distribution uniform.
            new PaletteEntry("Beach",        0xF0, 0xE0, 0xB0, BiomeId.Beach,        new ushort[] { 0x16, 0x17, 0x18, 0x19 }),
            // Grassland: 0x03-0x06 are the canonical lush green variants.
            new PaletteEntry("Grassland",    0x60, 0xC0, 0x40, BiomeId.Grassland,    new ushort[] { 0x03, 0x04, 0x05, 0x06 }),
            // Forest: 0xC5-0xC7 leafy forest floor. Three IDs.
            new PaletteEntry("Forest",       0x20, 0x80, 0x20, BiomeId.Forest,       new ushort[] { 0xC5, 0xC6, 0xC7 }),
            // DenseForest: SAME LandId pool as Forest — UO Classic doesn't ship a darker
            // dense-forest floor variant. Distinction in the field comes from
            // ForestScatterPass placing denser canopy via ForestSpecies[DenseForest] and
            // BiomeStaticScatterPass loading Forest Yew.xml (different mushroom/log weights).
            // TODO: if a real darker forest-floor LandId is found (mine T2A jungle interior?),
            // swap in here for a true visual distinction.
            new PaletteEntry("DenseForest",  0x10, 0x50, 0x10, BiomeId.DenseForest,  new ushort[] { 0xC5, 0xC6, 0xC7 }),
            // Jungle: 0xC8-0xCB lush green leafy floor.
            new PaletteEntry("Jungle",       0x00, 0x60, 0x00, BiomeId.Jungle,       new ushort[] { 0xC8, 0xC9, 0xCA, 0xCB }),
            // Savanna: dry-grass variants 0x07-0x0A. Was 0x03-0x04 (identical to Grassland).
            // 0x07-0x0A are the yellower grass tiles in the 0x03-0x0B family per
            // TileTables.cs ForestSpecies comment (0x07-0x0E referenced as grass continuum).
            // TODO: verify these render as actual dry/yellow grass and not as another biome.
            new PaletteEntry("Savanna",      0xC0, 0xB0, 0x40, BiomeId.Savanna,      new ushort[] { 0x07, 0x08, 0x09, 0x0A }),
            // Desert: SAME LandId pool as Beach. UO Classic doesn't ship a walkable
            // distinct-desert sand variant — 0x1A-0x1F are all impassable cliff slopes.
            // Visual distinction in the field comes from Sand.xml scatter (cacti + bones
            // + dead trees vs Beach.xml palms + shells). The terrain looks like sand
            // either way, which IS faithful to UO's T2A desert (sandy with cacti scatter).
            new PaletteEntry("Desert",       0xE0, 0xC0, 0x60, BiomeId.Desert,       new ushort[] { 0x16, 0x17, 0x18, 0x19 }),
            // Snow + Tundra share the same Felucca white-snow art at 0x011A-0x011D
            // per user request: "treat tundra and snow the same". Old pool 0x6C-0x71
            // was brown wood-grain dirt — wrong art entirely.
            new PaletteEntry("Tundra",       0xC0, 0xC0, 0xA0, BiomeId.Tundra,       new ushort[] { 0x011A, 0x011B, 0x011C, 0x011D }),
            new PaletteEntry("Snow",         0xF0, 0xF0, 0xF0, BiomeId.Snow,         new ushort[] { 0x011A, 0x011B, 0x011C, 0x011D }),
            // Mountain: 0x022C-0x022F is the canonical Felucca mountain-top/interior tile
            // family (per DragonMod maptrans.txt block (06) and a real-Felucca dump at
            // (1924,46)→(2179,301) showing 12,168 mountain tiles split ~24% each across
            // 0x22C/D/E/F). Earlier value 0x00DC-0xDF is the cliff-FACE family — sharp
            // dark rock tiles intended for vertical cliff edges, which when used as the
            // mountain interior produces the "labyrinth weave" texture (images 25/26).
            new PaletteEntry("Mountain",     0x80, 0x80, 0x80, BiomeId.Mountain,     new ushort[] { 0x22C, 0x22D, 0x22E, 0x22F }),
            // HighMountain: SAME LandId pool as Mountain. Distinction comes from
            // BiomeAltitudeJitter producing higher Z and MountainEdgeStaticsPass
            // placing differently-weighted rocks. TODO: verify if 0x230+ contains
            // distinct peak/snow-cap tiles worth using for HighMountain.
            new PaletteEntry("HighMountain", 0x40, 0x40, 0x40, BiomeId.HighMountain, new ushort[] { 0x22C, 0x22D, 0x22E, 0x22F }),
            // Swamp: 0x3DEB-0x3DEF — the canonical Felucca swamp ground (saturated dark
            // green moss/algae texture). Confirmed in user dumps at X≈1979,2279 (0x3DEF)
            // and X≈1982,2281 (0x3DEB). Previous pool 0x9C-0x9F was actually the
            // "dirt → swamp unwalkable slope" family, producing brown diagonal slope
            // patterns instead of the swamp interior surface — see image 49 vs 50/51.
            new PaletteEntry("Swamp",        0x60, 0x80, 0x40, BiomeId.Swamp,        new ushort[] { 0x3DEB, 0x3DEC, 0x3DED, 0x3DEE, 0x3DEF }),
            // Wetland: same 0x3DE-family swamp ground. UO Classic doesn't ship a clearly
            // distinct "wetland" ground tile, so Wetland uses a smaller subset of the
            // Swamp pool (lighter visual mix). Distinction in the field comes from
            // Wetland.xml scatter weights (heavier reeds/cattails, fewer cypress).
            new PaletteEntry("Wetland",      0x80, 0xA0, 0x60, BiomeId.Wetland,      new ushort[] { 0x3DEB, 0x3DED, 0x3DEF }),
            // Cave: dungeon stone floor. Tiledata confirms (scanned tiledata.mul 2026-05-17):
            //   0x0244          → name="NoName", flags=Wall|Impassable        → RENDERS BLACK
            //   0x0245..0x0249  → name="cave",    flags=(none)                → WALKABLE FLOOR
            //   0x024A..0x026D  → name="cave",    flags=Wall|Impassable       → cave-wall variants
            // The 79.5% dominance of 0x0244 in dumped cave areas is the wall-backing land
            // hidden under wall statics, NOT the visible walkable floor. The visible cave
            // surface is 0x0245-0x0249 (five variants, evenly weighted).
            new PaletteEntry("Cave",         0x60, 0x40, 0x20, BiomeId.Cave,         new ushort[] { 0x0245, 0x0246, 0x0247, 0x0248, 0x0249 }),
            // CaveWall: kept in the palette for the future "NoDraw + wall-static" rendering
            // model but NOT used by the default dungeon painter. The painter currently uses
            // HighMountain (Mountain land 0x022C-0x022F) for walls because mountain LAND
            // tiles render as a coherent rocky face without requiring per-cell statics.
            // If you opt in to NoDraw walls, pair this entry with CaveWallStaticsPass at
            // WallDensity=1.0 so every CaveWall cell gets a 0x0241-0x0243 wall static.
            new PaletteEntry("CaveWall",     0xA0, 0x90, 0x80, BiomeId.CaveWall,     new ushort[] { 0x01AE }),
        });

        public static Palette FromJson(JsonElement root)
        {
            var list = new List<PaletteEntry>();
            if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    string name = el.GetProperty("name").GetString() ?? "?";
                    string hex = el.GetProperty("color").GetString() ?? "#000000";
                    var (r, g, b) = ParseHex(hex);
                    var biome = Enum.Parse<BiomeId>(el.GetProperty("biome").GetString() ?? "Unassigned", true);
                    var ids = new List<ushort>();
                    if (el.TryGetProperty("land_ids", out var lids))
                        foreach (var v in lids.EnumerateArray())
                            ids.Add((ushort)v.GetInt32());
                    list.Add(new PaletteEntry(name, r, g, b, biome, ids.ToArray()));
                }
            }
            return new Palette(list.ToArray());
        }

        private static (byte R, byte G, byte B) ParseHex(string hex)
        {
            hex = hex.TrimStart('#');
            return (
                byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber),
                byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber),
                byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber)
            );
        }
    }
}
