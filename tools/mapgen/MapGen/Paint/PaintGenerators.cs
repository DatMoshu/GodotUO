namespace CentrED.MapGen.Paint;

// C# ports of the procedural painters in tools/scripts/mapgen (paint-*.ps1, test-paint*.ps1).
// Each generator writes the same {prefix}.terrain.png + {prefix}.altitude.png pair that
// ImageImportPass (MapGen.Cli import=, the editor's image-import mode) consumes.
//
// The ports keep each script's palette, altitude-shade table, geometry and random-number
// call order: seeded System.Random is the same algorithm in Windows PowerShell and .NET, so
// the noise-driven painters reproduce the scripts' layouts. Small differences remain where
// System.Drawing rasterisation was used (ellipse edge pixels) and in the altitude PNG format
// (8-bit greyscale here, 24-bit grey in the scripts — ImageImportPass reads both the same).
//
// Ported:            fullworld, archipelago, island (test-paint-island), two-islands,
//                    islands3 (test-paint-archipelago), heightgradient, colortest,
//                    bigmountain, bigtower, quadrants / quadrants-gradient (test-paint),
//                    flat-grass (paint-maze + paint-rooms-maze, identical), dungeon
//                    (test-paint-dungeon), organic-cave (test-paint-organic-dungeon).
// Not ported:        the name-banner script — it renders text with a system TrueType font through
//                    System.Drawing; reproducing that needs a font dependency in MapGen for a
//                    one-off vanity map. Keep using the script for it.
//
// Quirk preserved on purpose: in test-paint-archipelago.ps1 the expressions
// `RandDouble * [Math]::PI * 2` call the RandDouble *function* with ignored arguments, so the
// island coast phases and patch angles are NextDouble() in [0,1) radians, not [0,2π). The
// port does the same so the same seed yields the same islands.

/// <summary>A procedural painter that produces an ImageImportPass PNG pair.</summary>
public interface IPaintGenerator
{
    /// <summary>Stable id (used by UI pickers and settings).</summary>
    string Id { get; }
    /// <summary>One-line description for UI.</summary>
    string Description { get; }
    int DefaultWidth { get; }
    int DefaultHeight { get; }
    int DefaultSeed { get; }
    /// <summary>True when the output depends on the seed.</summary>
    bool UsesSeed { get; }

    /// <summary>Paints into memory.</summary>
    PaintRaster Render(int width, int height, int seed);

    /// <summary>Paints and writes {prefix}.terrain.png + {prefix}.altitude.png.</summary>
    void Generate(string prefix, int width, int height, int seed) => Render(width, height, seed).Save(prefix);
}

/// <summary>Registry of all ported paint generators.</summary>
public static class PaintGenerators
{
    public static IReadOnlyList<IPaintGenerator> All { get; } = new IPaintGenerator[]
    {
        new FullWorldGenerator(),
        new ArchipelagoGenerator(),
        new IslandGenerator(),
        new TwoIslandsGenerator(),
        new Islands3Generator(),
        new HeightGradientGenerator(),
        new ColorTestGenerator(),
        new BigMountainGenerator(),
        new BigTowerGenerator(),
        new QuadrantsGenerator(false),
        new QuadrantsGenerator(true),
        new FlatGrassGenerator(),
        new DungeonGenerator(),
        new OrganicCaveGenerator(),
    };

    /// <summary>Generator with id <paramref name="id"/> (case-insensitive), or null.</summary>
    public static IPaintGenerator? Find(string id)
        => All.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs generator <paramref name="id"/>; width/height/seed default to the generator's own.</summary>
    public static void Generate(string id, string prefix, int? width = null, int? height = null, int? seed = null)
    {
        var g = Find(id) ?? throw new ArgumentException($"unknown paint generator '{id}'", nameof(id));
        g.Generate(prefix, width ?? g.DefaultWidth, height ?? g.DefaultHeight, seed ?? g.DefaultSeed);
    }
}

// ---------------------------------------------------------------------------
// Shared tables
// ---------------------------------------------------------------------------

internal static class PaintTables
{
    // Shades from paint-fullworld.ps1 / paint-archipelago.ps1 / paint-colortest.ps1.
    public static readonly Dictionary<string, byte> WorldShades = new()
    {
        ["DeepWater"] = 0, ["ShallowWater"] = 12, ["Beach"] = 32, ["Grassland"] = 50,
        ["Forest"] = 60, ["DenseForest"] = 65, ["Jungle"] = 55, ["Savanna"] = 55,
        ["Desert"] = 50, ["Tundra"] = 80, ["Snow"] = 100, ["Mountain"] = 200,
        ["HighMountain"] = 240, ["Swamp"] = 35,
    };

    public static byte S(string swatch) => PaintRaster.Swatch(swatch);
}

/// <summary>Low-res value noise + box blur + bilinear upsample (paint-fullworld / paint-archipelago).</summary>
internal static class ValueNoiseWorld
{
    public static double[,] GenNoise(Random rng, int w, int h)
    {
        var arr = new double[w, h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            arr[x, y] = rng.NextDouble();
        return arr;
    }

    public static void BoxBlur(double[,] src, int w, int h, int passes)
    {
        var tmp = new double[w, h];
        for (int p = 0; p < passes; p++)
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double sum = 0; int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= w) continue;
                        sum += src[nx, ny]; n++;
                    }
                }
                tmp[x, y] = sum / n;
            }
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                src[x, y] = tmp[x, y];
        }
    }

    public static string PickBiome(double height, double moisture, double latitude)
    {
        if (height < 0.30) return "DeepWater";
        if (height < 0.36) return "ShallowWater";
        if (height < 0.40) return "Beach";
        if (height < 0.62)
        {
            if (latitude > 0.85) return "Snow";
            if (latitude > 0.70) return "Tundra";
            if (latitude < 0.18) return moisture > 0.55 ? "Jungle" : "Savanna";
            if (moisture > 0.78) return "Swamp";
            if (moisture > 0.65) return "DenseForest";
            if (moisture > 0.45) return "Forest";
            if (moisture > 0.25) return "Grassland";
            return "Desert";
        }
        if (height < 0.78) return latitude > 0.75 ? "Snow" : "Mountain";
        return "HighMountain";
    }

    /// <summary>Bilinear-upsamples the two noise grids and classifies every pixel (WriteBiomeImages).</summary>
    public static PaintRaster Classify(int width, int height, double[,] heightLow, double[,] moistureLow, int noiseW, int noiseH)
    {
        var r = new PaintRaster(width, height);
        var swatch = new Dictionary<string, byte>();
        foreach (var k in PaintTables.WorldShades.Keys) swatch[k] = PaintTables.S(k);
        double cyW = height > 1 ? (height - 1) / 2.0 : 1.0;
        for (int y = 0; y < height; y++)
        {
            double fy = (y / (double)height) * (noiseH - 1);
            int iy = (int)Math.Floor(fy); double ty = fy - iy;
            int iyP = Math.Min(iy + 1, noiseH - 1);
            double latitude = Math.Abs(y - cyW) / cyW;
            for (int x = 0; x < width; x++)
            {
                double fx = (x / (double)width) * (noiseW - 1);
                int ix = (int)Math.Floor(fx); double tx = fx - ix;
                int ixP = Math.Min(ix + 1, noiseW - 1);
                double h0 = heightLow[ix, iy] + (heightLow[ixP, iy] - heightLow[ix, iy]) * tx;
                double h1 = heightLow[ix, iyP] + (heightLow[ixP, iyP] - heightLow[ix, iyP]) * tx;
                double hv = h0 + (h1 - h0) * ty;
                double m0 = moistureLow[ix, iy] + (moistureLow[ixP, iy] - moistureLow[ix, iy]) * tx;
                double m1 = moistureLow[ix, iyP] + (moistureLow[ixP, iyP] - moistureLow[ix, iyP]) * tx;
                double mv = m0 + (m1 - m0) * ty;
                var biome = PickBiome(hv, mv, latitude);
                r.Set(x, y, swatch[biome], PaintTables.WorldShades[biome]);
            }
        }
        return r;
    }
}

// ---------------------------------------------------------------------------
// Noise worlds
// ---------------------------------------------------------------------------

/// <summary>paint-fullworld.ps1: one radial continent, latitude bands, value-noise biomes.</summary>
public sealed class FullWorldGenerator : IPaintGenerator
{
    public string Id => "fullworld";
    public string Description => "Single continent with latitude climate bands (paint-fullworld.ps1)";
    public int DefaultWidth => 7168;
    public int DefaultHeight => 4096;
    public int DefaultSeed => 1234567;
    public bool UsesSeed => true;
    public int NoiseW { get; init; } = 224;
    public int NoiseH { get; init; } = 128;

    public PaintRaster Render(int width, int height, int seed)
    {
        var rng = new Random(seed);
        var heightLow = ValueNoiseWorld.GenNoise(rng, NoiseW, NoiseH);
        var moistureLow = ValueNoiseWorld.GenNoise(rng, NoiseW, NoiseH);
        ValueNoiseWorld.BoxBlur(heightLow, NoiseW, NoiseH, 3);
        ValueNoiseWorld.BoxBlur(moistureLow, NoiseW, NoiseH, 4);

        double cxN = (NoiseW - 1) / 2.0, cyN = (NoiseH - 1) / 2.0;
        for (int y = 0; y < NoiseH; y++)
        for (int x = 0; x < NoiseW; x++)
        {
            double dx = (x - cxN) / cxN, dy = (y - cyN) / cyN;
            double continent = Math.Max(0.0, 1.0 - (dx * dx + dy * dy));
            heightLow[x, y] = heightLow[x, y] * 0.45 + continent * 0.55;
        }
        return ValueNoiseWorld.Classify(width, height, heightLow, moistureLow, NoiseW, NoiseH);
    }
}

/// <summary>paint-archipelago.ps1: many radial island centres over value noise.</summary>
public sealed class ArchipelagoGenerator : IPaintGenerator
{
    public string Id => "archipelago";
    public string Description => "12-18 small/medium islands from scattered radial centres (paint-archipelago.ps1)";
    public int DefaultWidth => 3584;
    public int DefaultHeight => 4096;
    public int DefaultSeed => 1234567;
    public bool UsesSeed => true;
    public int NoiseW { get; init; } = 224;
    public int NoiseH { get; init; } = 256;
    public int IslandCount { get; init; } = 22;

    public PaintRaster Render(int width, int height, int seed)
    {
        var rng = new Random(seed);
        var heightLow = ValueNoiseWorld.GenNoise(rng, NoiseW, NoiseH);
        var moistureLow = ValueNoiseWorld.GenNoise(rng, NoiseW, NoiseH);
        ValueNoiseWorld.BoxBlur(heightLow, NoiseW, NoiseH, 3);
        ValueNoiseWorld.BoxBlur(moistureLow, NoiseW, NoiseH, 4);

        var centres = new List<(int Cx, int Cy, double Rad, double Strength)>();
        for (int i = 0; i < IslandCount; i++)
        {
            int cx = rng.Next(0, NoiseW);
            int cy = rng.Next(0, NoiseH);
            double rad = rng.NextDouble() * 0.07 + 0.03;
            double strength = rng.NextDouble() * 0.25 + 0.30;
            centres.Add((cx, cy, rad, strength));
        }

        double diagN = Math.Sqrt(NoiseW * NoiseW + NoiseH * NoiseH);
        for (int y = 0; y < NoiseH; y++)
        for (int x = 0; x < NoiseW; x++)
        {
            double sum = 0;
            foreach (var c in centres)
            {
                double dx = x - c.Cx, dy = y - c.Cy;
                double d = Math.Sqrt(dx * dx + dy * dy) / diagN;
                sum += Math.Max(0.0, 1.0 - d / c.Rad) * c.Strength;
            }
            heightLow[x, y] = heightLow[x, y] * 0.20 + Math.Min(1.0, sum) * 0.80;
        }
        ValueNoiseWorld.BoxBlur(heightLow, NoiseW, NoiseH, 1);
        return ValueNoiseWorld.Classify(width, height, heightLow, moistureLow, NoiseW, NoiseH);
    }
}

// ---------------------------------------------------------------------------
// Test islands
// ---------------------------------------------------------------------------

/// <summary>test-paint-island.ps1: one wobbly island, central mountain, climate patches.</summary>
public sealed class IslandGenerator : IPaintGenerator
{
    public string Id => "island";
    public string Description => "Single island with mountain core and 14 biome patches (test-paint-island.ps1)";
    public int DefaultWidth => 512;
    public int DefaultHeight => 512;
    public int DefaultSeed => 0;
    public bool UsesSeed => false;

    private static readonly Dictionary<string, byte> Alt = new()
    {
        ["DeepWater"] = 0, ["ShallowWater"] = 8, ["Beach"] = 28, ["Grassland"] = 28, ["Forest"] = 28,
        ["DenseForest"] = 28, ["Jungle"] = 28, ["Savanna"] = 28, ["Desert"] = 28, ["Tundra"] = 28,
        ["Snow"] = 28, ["Swamp"] = 28, ["Wetland"] = 28, ["Mountain"] = 120, ["HighMountain"] = 220,
    };

    public PaintRaster Render(int width, int height, int seed)
    {
        double size = Math.Min(width, height);
        double cx = width / 2.0, cy = height / 2.0;
        double rBase = size * 0.41;
        var rTbl = new double[720];
        for (int i = 0; i < 720; i++)
        {
            double t = i * Math.PI / 360.0;
            rTbl[i] = rBase + size * 0.047 * Math.Sin(3 * t + 0.7) + size * 0.023 * Math.Sin(5 * t + 2.1)
                      + size * 0.012 * Math.Sin(7 * t + 4.3);
        }
        (string Name, double Cx, double Cy, double R)[] patches =
        {
            ("Snow", 0.28, 0.16, 0.085), ("Snow", 0.62, 0.14, 0.065), ("Tundra", 0.45, 0.22, 0.080),
            ("Tundra", 0.74, 0.22, 0.060), ("DenseForest", 0.22, 0.34, 0.075), ("DenseForest", 0.72, 0.36, 0.065),
            ("Forest", 0.42, 0.36, 0.060), ("Forest", 0.20, 0.70, 0.080), ("Forest", 0.56, 0.68, 0.065),
            ("Wetland", 0.78, 0.70, 0.065), ("Swamp", 0.30, 0.80, 0.075), ("Savanna", 0.55, 0.82, 0.090),
            ("Desert", 0.78, 0.84, 0.075), ("Jungle", 0.42, 0.90, 0.075),
        };
        double mCx = width * 0.50, mCy = height * 0.46, mRx = size * 0.20, mRy = size * 0.13;
        double mNoise = size * 0.03;
        var mTbl = new double[720];
        for (int i = 0; i < 720; i++)
            mTbl[i] = 1.0 + (mNoise / Math.Min(mRx, mRy)) * Math.Sin(5.0 * (i * Math.PI / 360.0) + 1.3);

        var r = new PaintRaster(width, height);
        var sw = Alt.Keys.ToDictionary(k => k, PaintTables.S);
        for (int y = 0; y < height; y++)
        {
            double dyC = y - cy, yN = y / (double)height;
            for (int x = 0; x < width; x++)
            {
                string b = Classify(x, y, dyC, yN);
                r.Set(x, y, sw[b], Alt[b]);
            }
        }
        return r;

        string Classify(int x, int y, double dyC, double yN)
        {
            double dxC = x - cx;
            double dist = Math.Sqrt(dxC * dxC + dyC * dyC);
            double coastR = rTbl[AngleIndex(dxC, dyC)];
            if (dist > coastR + 25) return "DeepWater";
            if (dist > coastR + 8) return "ShallowWater";
            if (dist > coastR - 12) return "Beach";
            double mdx = x - mCx, mdy = y - mCy;
            double ms = mTbl[AngleIndex(mdx, mdy)];
            double mE = mdx * mdx / (mRx * mRx * ms * ms) + mdy * mdy / (mRy * mRy * ms * ms);
            if (mE < 1.0) return mE < 0.12 ? "HighMountain" : "Mountain";
            string bg = "Grassland";
            if (yN < 0.18) bg = "Snow";
            else if (yN < 0.27) bg = "Tundra";
            else if (yN > 0.86) bg = "Desert";
            else if (yN > 0.78) bg = "Savanna";
            string? hit = null;
            foreach (var p in patches)
            {
                double pdx = x - width * p.Cx, pdy = y - height * p.Cy, pr = size * p.R;
                if (pdx * pdx + pdy * pdy < pr * pr) hit = p.Name;
            }
            return hit ?? bg;
        }
    }

    /// <summary>Half-degree lookup index used by the island painters ((atan2 deg + 360) % 360 * 2).</summary>
    internal static int AngleIndex(double dx, double dy)
    {
        double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        int idx = (int)(((deg + 360.0) % 360.0) * 2.0);
        return Math.Min(idx, 719);
    }
}

/// <summary>test-paint-two-islands.ps1: gentle west island vs. cliff-edged east island, side by side.</summary>
public sealed class TwoIslandsGenerator : IPaintGenerator
{
    public string Id => "two-islands";
    public string Description => "West island with gentle shore vs east island with a cliff edge (test-paint-two-islands.ps1)";
    public int DefaultWidth => 512;
    public int DefaultHeight => 256;
    public int DefaultSeed => 0;
    public bool UsesSeed => false;

    public PaintRaster Render(int width, int height, int seed)
    {
        double size = Math.Min(width / 2.0, height);
        var r = new PaintRaster(width, height);
        byte deep = PaintTables.S("DeepWater"), shallow = PaintTables.S("ShallowWater"), beach = PaintTables.S("Beach"),
            grass = PaintTables.S("Grassland"), forest = PaintTables.S("Forest"), mountain = PaintTables.S("Mountain");
        r.Fill(deep, 0);

        double wcx = size * 0.5, wcy = size * 0.5;
        r.FillCircle(wcx, wcy, size * 0.46, shallow, 8);
        r.FillCircle(wcx, wcy, size * 0.38, beach, 28);
        r.FillCircle(wcx, wcy, size * 0.34, grass, 28);
        r.FillCircle(wcx - size * 0.13, wcy - size * 0.13, size * 0.10, forest, 28);
        r.FillCircle(wcx + size * 0.13, wcy + size * 0.13, size * 0.08, mountain, 200);

        double ecx = size * 1.5, ecy = size * 0.5;
        int split = (int)Math.Round(ecx, MidpointRounding.ToEven);
        int sz = (int)size;
        var west = (sz, 0, split - sz, height);
        var east = (split, 0, width - split, height);
        r.FillCircle(ecx, ecy, size * 0.46, shallow, 8, west);
        r.FillCircle(ecx, ecy, size * 0.38, beach, 28, west);
        r.FillCircle(ecx, ecy, size * 0.34, grass, 28, west);
        r.FillCircle(ecx, ecy, size * 0.46, grass, 51, east);   // raised grass straight to the water: cliff
        return r;
    }
}

/// <summary>test-paint-archipelago.ps1: 3 themed islands with ridges + 8-12 islets.</summary>
public sealed class Islands3Generator : IPaintGenerator
{
    public string Id => "islands3";
    public string Description => "3 themed islands (ridges, climate patches) + small islets (test-paint-archipelago.ps1)";
    public int DefaultWidth => 1024;
    public int DefaultHeight => 1024;
    public int DefaultSeed => 1234567;
    public bool UsesSeed => true;

    private static readonly Dictionary<string, byte> Alt = new()
    {
        ["DeepWater"] = 0, ["ShallowWater"] = 8, ["Beach"] = 32, ["Grassland"] = 32, ["Forest"] = 32,
        ["DenseForest"] = 32, ["Jungle"] = 32, ["Savanna"] = 32, ["Desert"] = 32, ["Tundra"] = 32,
        ["Snow"] = 32, ["Swamp"] = 32, ["Wetland"] = 32, ["Mountain"] = 120, ["HighMountain"] = 220,
    };

    private sealed record Theme(string Name, string Background, string[] Patches, bool HasRidge, double RidgeChance);

    private static readonly Theme[] Themes =
    {
        new("Tropical", "Grassland", new[] { "Jungle", "Jungle", "Forest", "Beach", "Wetland" }, true, 0.7),
        new("Temperate", "Grassland", new[] { "Forest", "DenseForest", "Forest", "Grassland", "Wetland" }, true, 0.8),
        new("Arctic", "Snow", new[] { "Tundra", "Tundra", "DenseForest", "Snow", "Snow" }, true, 0.6),
        new("Arid", "Savanna", new[] { "Desert", "Desert", "Savanna", "Grassland" }, true, 0.5),
        new("Marsh", "Wetland", new[] { "Swamp", "Swamp", "Wetland", "Jungle", "DenseForest" }, false, 0),
        new("Mixed", "Grassland", new[] { "Forest", "Desert", "Jungle", "Snow", "Swamp", "Savanna" }, true, 0.9),
    };

    private sealed class Island
    {
        public double Cx, Cy, BaseR, RidgeAngle, RidgeHalf;
        public double[] RTbl = Array.Empty<double>();
        public Theme Theme = Themes[0];
        public bool HasRidge;
        public List<(string Name, double Cx, double Cy, double R)> Patches = new();
    }

    public PaintRaster Render(int width, int height, int seed)
    {
        // The script is laid out for 1024²; distances scale with the canvas.
        double k = Math.Min(width, height) / 1024.0;
        var rng = new Random(seed);
        double RandRange(double lo, double hi) => lo + (hi - lo) * rng.NextDouble();

        double margin = 140 * k, minSep = 280 * k;
        var islands = new List<Island>();
        for (int n = 0; n < 3; n++)
        {
            (double X, double Y)? pos = null;
            for (int a = 0; a < 50 && pos is null; a++)
            {
                double px = RandRange(margin, width - margin), py = RandRange(margin, height - margin);
                bool ok = islands.All(e => Math.Sqrt((px - e.Cx) * (px - e.Cx) + (py - e.Cy) * (py - e.Cy)) >= minSep);
                if (ok) pos = (px, py);
            }
            if (pos is null) continue;
            var isl = new Island { Cx = pos.Value.X, Cy = pos.Value.Y };
            isl.Theme = Themes[rng.Next(Themes.Length)];
            isl.BaseR = RandRange(110, 170) * k;
            double wA = RandRange(18, 36) * k, wB = RandRange(8, 16) * k, wC = RandRange(4, 10) * k;
            double phA = rng.NextDouble(), phB = rng.NextDouble(), phC = rng.NextDouble(); // see quirk note
            isl.HasRidge = isl.Theme.HasRidge && rng.NextDouble() < isl.Theme.RidgeChance;
            isl.RidgeAngle = RandRange(0, Math.PI);
            isl.RidgeHalf = RandRange(12, 24) * k;
            isl.RTbl = new double[720];
            for (int i = 0; i < 720; i++)
            {
                double t = i * Math.PI / 360.0;
                isl.RTbl[i] = isl.BaseR + wA * Math.Sin(3 * t + phA) + wB * Math.Sin(5 * t + phB) + wC * Math.Sin(7 * t + phC);
            }
            int patchCount = rng.Next(5, 9);
            for (int p = 0; p < patchCount; p++)
            {
                double ang = rng.NextDouble(); // see quirk note
                double dist = RandRange(0, isl.BaseR * 0.65);
                string name = isl.Theme.Patches[rng.Next(isl.Theme.Patches.Length)];
                double pr = RandRange(isl.BaseR * 0.10, isl.BaseR * 0.22);
                isl.Patches.Add((name, isl.Cx + Math.Cos(ang) * dist, isl.Cy + Math.Sin(ang) * dist, pr));
            }
            islands.Add(isl);
        }

        var islets = new List<(double Cx, double Cy, double R)>();
        int isletCount = rng.Next(8, 13);
        for (int n = 0; n < isletCount; n++)
        {
            double ix = RandRange(30 * k, width - 30 * k), iy = RandRange(30 * k, height - 30 * k);
            bool tooClose = islands.Any(e => Math.Sqrt((ix - e.Cx) * (ix - e.Cx) + (iy - e.Cy) * (iy - e.Cy)) < e.BaseR + 60 * k);
            if (tooClose) continue;
            islets.Add((ix, iy, RandRange(2, 6)));
        }

        var r = new PaintRaster(width, height);
        var sw = Alt.Keys.ToDictionary(x => x, PaintTables.S);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            string b = "DeepWater";
            foreach (var isl in islands)
            {
                double dx = x - isl.Cx, dy = y - isl.Cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double coastR = isl.RTbl[IslandGenerator.AngleIndex(dx, dy)];
                if (dist > coastR + 22 * k) continue;
                if (dist > coastR + 7 * k) { if (b == "DeepWater") b = "ShallowWater"; continue; }
                if (dist > coastR - 10 * k) { b = "Beach"; break; }
                if (isl.HasRidge)
                {
                    double perp = Math.Abs(-dx * Math.Sin(isl.RidgeAngle) + dy * Math.Cos(isl.RidgeAngle));
                    if (perp < isl.RidgeHalf)
                    {
                        b = perp < isl.RidgeHalf * 0.35 ? "HighMountain" : "Mountain";
                        break;
                    }
                }
                b = isl.Theme.Background;
                foreach (var p in isl.Patches)
                {
                    double pdx = x - p.Cx, pdy = y - p.Cy;
                    if (pdx * pdx + pdy * pdy < p.R * p.R) b = p.Name;
                }
                break;
            }
            if (b is "DeepWater" or "ShallowWater")
            {
                foreach (var it in islets)
                {
                    double d = Math.Sqrt((x - it.Cx) * (x - it.Cx) + (y - it.Cy) * (y - it.Cy));
                    if (d < it.R) { b = "Beach"; break; }
                    if (d < it.R + 4 && b == "DeepWater") b = "ShallowWater";
                }
            }
            r.Set(x, y, sw[b], Alt[b]);
        }
        return r;
    }
}

// ---------------------------------------------------------------------------
// Test patterns
// ---------------------------------------------------------------------------

/// <summary>paint-heightgradient.ps1: 8 vertical bands, deep water → high mountain.</summary>
public sealed class HeightGradientGenerator : IPaintGenerator
{
    public string Id => "heightgradient";
    public string Description => "8 vertical biome bands with rising altitude (paint-heightgradient.ps1)";
    public int DefaultWidth => 256;
    public int DefaultHeight => 256;
    public int DefaultSeed => 0;
    public bool UsesSeed => false;

    private static readonly (string Name, byte Shade)[] Bands =
    {
        ("DeepWater", 0), ("ShallowWater", 12), ("Beach", 32), ("Grassland", 50),
        ("Forest", 60), ("DenseForest", 70), ("Mountain", 200), ("HighMountain", 240),
    };

    public PaintRaster Render(int width, int height, int seed)
    {
        var r = new PaintRaster(width, height);
        int bandWidth = Math.Max(1, width / Bands.Length);
        for (int x = 0; x < width; x++)
        {
            var band = Bands[Math.Min(x / bandWidth, Bands.Length - 1)];
            byte s = PaintTables.S(band.Name);
            for (int y = 0; y < height; y++) r.Set(x, y, s, band.Shade);
        }
        return r;
    }
}

/// <summary>paint-colortest.ps1: 4x4 grid exercising 14 biomes.</summary>
public sealed class ColorTestGenerator : IPaintGenerator
{
    public string Id => "colortest";
    public string Description => "4x4 grid of biome swatches (paint-colortest.ps1)";
    public int DefaultWidth => 256;
    public int DefaultHeight => 256;
    public int DefaultSeed => 0;
    public bool UsesSeed => false;

    private static readonly string[][] Grid =
    {
        new[] { "DeepWater", "ShallowWater", "Beach", "Swamp" },
        new[] { "Savanna", "Grassland", "Desert", "Tundra" },
        new[] { "Forest", "Jungle", "DenseForest", "Snow" },
        new[] { "Mountain", "HighMountain", "Mountain", "Forest" },
    };

    public PaintRaster Render(int width, int height, int seed)
    {
        var r = new PaintRaster(width, height);
        int cellW = Math.Max(1, width / 4), cellH = Math.Max(1, height / 4);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            string b = Grid[Math.Min(y / cellH, 3)][Math.Min(x / cellW, 3)];
            r.Set(x, y, PaintTables.S(b), PaintTables.WorldShades[b]);
        }
        return r;
    }
}

/// <summary>paint-bigmountain.ps1: concentric rings up to a z=127 peak.</summary>
public sealed class BigMountainGenerator : IPaintGenerator
{
    public string Id => "bigmountain";
    public string Description => "Concentric island rising to a sbyte-max peak (paint-bigmountain.ps1)";
    public int DefaultWidth => 256;
    public int DefaultHeight => 256;
    public int DefaultSeed => 0;
    public bool UsesSeed => false;

    public PaintRaster Render(int width, int height, int seed)
    {
        double size = Math.Min(width, height), cx = width / 2.0, cy = height / 2.0;
        var r = new PaintRaster(width, height);
        r.Fill(PaintTables.S("DeepWater"), 0);
        r.FillCircle(cx, cy, size * 0.48, PaintTables.S("ShallowWater"), 12);
        r.FillCircle(cx, cy, size * 0.44, PaintTables.S("Beach"), 32);
        r.FillCircle(cx, cy, size * 0.40, PaintTables.S("Grassland"), 60);
        r.FillCircle(cx, cy, size * 0.32, PaintTables.S("Forest"), 100);
        r.FillCircle(cx, cy, size * 0.22, PaintTables.S("Mountain"), 200);
        r.FillCircle(cx, cy, size * 0.12, PaintTables.S("HighMountain"), 240);
        r.FillCircle(cx, cy, size * 0.04, PaintTables.S("HighMountain"), 255);
        return r;
    }
}

/// <summary>paint-bigtower.ps1: a steep rock tower straight out of the sea.</summary>
public sealed class BigTowerGenerator : IPaintGenerator
{
    public string Id => "bigtower";
    public string Description => "Steep rock tower rising from a thin beach (paint-bigtower.ps1)";
    public int DefaultWidth => 256;
    public int DefaultHeight => 256;
    public int DefaultSeed => 0;
    public bool UsesSeed => false;

    public PaintRaster Render(int width, int height, int seed)
    {
        double size = Math.Min(width, height), cx = width / 2.0, cy = height / 2.0;
        var r = new PaintRaster(width, height);
        r.Fill(PaintTables.S("DeepWater"), 0);
        r.FillCircle(cx, cy, size * 0.40, PaintTables.S("ShallowWater"), 12);
        r.FillCircle(cx, cy, size * 0.37, PaintTables.S("Beach"), 32);
        r.FillCircle(cx, cy, size * 0.34, PaintTables.S("Mountain"), 200);
        r.FillCircle(cx, cy, size * 0.27, PaintTables.S("HighMountain"), 240);
        r.FillCircle(cx, cy, size * 0.20, PaintTables.S("HighMountain"), 255);
        return r;
    }
}

/// <summary>test-paint.ps1: water / grass / forest / mountain quadrants, flat or with a NW→SE altitude ramp.</summary>
public sealed class QuadrantsGenerator : IPaintGenerator
{
    private readonly bool _gradient;
    public QuadrantsGenerator(bool gradient) { _gradient = gradient; }

    public string Id => _gradient ? "quadrants-gradient" : "quadrants";
    public string Description => _gradient
        ? "4 biome quadrants with a NW→SE altitude ramp (test-paint.ps1 -Gradient)"
        : "4 flat biome quadrants: water, grass, forest, mountain (test-paint.ps1)";
    public int DefaultWidth => 256;
    public int DefaultHeight => 256;
    public int DefaultSeed => 0;
    public bool UsesSeed => false;

    public PaintRaster Render(int width, int height, int seed)
    {
        var r = new PaintRaster(width, height);
        int hx = width / 2, hy = height / 2;
        r.FillRect(0, 0, hx, hy, PaintTables.S("DeepWater"), 5);
        r.FillRect(hx, 0, width - hx, hy, PaintTables.S("Grassland"), 38);
        r.FillRect(0, hy, hx, height - hy, PaintTables.S("Forest"), 58);
        r.FillRect(hx, hy, width - hx, height - hy, PaintTables.S("Mountain"), 184);
        if (_gradient)
        {
            double span = Math.Max(1, width + height - 2);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                r.SetAltitude(x, y, (byte)Math.Round((x + y) / span * 255.0));
        }
        return r;
    }
}

/// <summary>paint-maze.ps1 / paint-rooms-maze.ps1: flat Grassland at z=0 for the maze / rooms passes.</summary>
public sealed class FlatGrassGenerator : IPaintGenerator
{
    public string Id => "flat-grass";
    public string Description => "Flat grassland at z=0, canvas for Maze / Rooms passes (paint-maze.ps1)";
    public int DefaultWidth => 512;
    public int DefaultHeight => 512;
    public int DefaultSeed => 0;
    public bool UsesSeed => false;

    public PaintRaster Render(int width, int height, int seed)
    {
        var r = new PaintRaster(width, height);
        r.Fill(PaintTables.S("Grassland"), 28);
        return r;
    }
}

/// <summary>test-paint-dungeon.ps1: two cave chambers joined by a bent corridor inside rock.</summary>
public sealed class DungeonGenerator : IPaintGenerator
{
    public string Id => "dungeon";
    public string Description => "Two cave chambers and a horseshoe corridor in solid rock (test-paint-dungeon.ps1)";
    public int DefaultWidth => 512;
    public int DefaultHeight => 512;
    public int DefaultSeed => 0;
    public bool UsesSeed => false;

    public PaintRaster Render(int width, int height, int seed)
    {
        double size = Math.Min(width, height);
        var r = new PaintRaster(width, height);
        r.Fill(PaintTables.S("HighMountain"), 198);
        byte floor = PaintTables.S("Cave");
        const byte floorZ = 28;
        int ax = (int)(width * 0.22), ay = (int)(height * 0.28);
        int bx = (int)(width * 0.38), by = (int)(height * 0.55);
        int cx = (int)(width * 0.78), cy = (int)(height * 0.72);
        int rr = (int)(size * 0.075), wHalf = (int)(size * 0.012);
        r.FillCircle(ax, ay, rr, floor, floorZ);
        r.FillCircle(cx, cy, rr, floor, floorZ);
        r.FillCapsule(ax, ay, bx, by, wHalf, floor, floorZ);
        r.FillCapsule(bx, by, cx, cy, wHalf, floor, floorZ);
        return r;
    }
}

/// <summary>test-paint-organic-dungeon.ps1: value-noise cave with forced chambers/corridors + CA smoothing.</summary>
public sealed class OrganicCaveGenerator : IPaintGenerator
{
    public string Id => "organic-cave";
    public string Description => "Organic cave: noise blobs, 8 chambers, corridors, CA smoothing (test-paint-organic-dungeon.ps1)";
    public int DefaultWidth => 512;
    public int DefaultHeight => 512;
    public int DefaultSeed => 1337;
    public bool UsesSeed => true;

    public PaintRaster Render(int width, int height, int seed)
    {
        var g = Generate(width, height, seed);
        var r = new PaintRaster(width, height);
        byte floor = PaintTables.S("Cave"), wall = PaintTables.S("HighMountain");
        for (int i = 0; i < g.Length; i++)
        {
            r.Terrain[i] = g[i] == 1 ? floor : wall;
            r.Altitude[i] = g[i] == 1 ? (byte)28 : (byte)198;
        }
        return r;
    }

    // Port of the script's inline OrganicCave.Generate, generalised from size² to width×height.
    // 1 = floor, 0 = wall.
    internal static byte[] Generate(int w, int h, int seed)
    {
        const int edgePad = 4, smoothingPasses = 2, birthLimit = 5, deathLimit = 4, corridorHalfWidth = 3;
        double[] chFx = { 0.20, 0.50, 0.78, 0.30, 0.62, 0.18, 0.50, 0.82 };
        double[] chFy = { 0.22, 0.18, 0.25, 0.50, 0.50, 0.78, 0.80, 0.78 };
        double[] chFr = { 0.060, 0.050, 0.065, 0.055, 0.050, 0.055, 0.055, 0.060 };
        int[] corA = { 0, 1, 0, 2, 3, 3, 4, 4, 5, 6 };
        int[] corB = { 1, 2, 3, 4, 4, 5, 6, 7, 6, 7 };
        int size = Math.Min(w, h);

        var g = new byte[w * h];
        var rng = new Random(seed);
        int[] cellSizes = { 64, 32, 16 };
        double[] amps = { 1.0, 0.5, 0.25 };
        double ampSum = amps.Sum();
        var lattices = new double[cellSizes.Length][];
        var latW = new int[cellSizes.Length];
        for (int o = 0; o < cellSizes.Length; o++)
        {
            int cs = cellSizes[o];
            int lw = w / cs + 2, lh = h / cs + 2;
            latW[o] = lw;
            var lat = new double[lw * lh];
            for (int i = 0; i < lat.Length; i++) lat[i] = rng.NextDouble() * 2.0 - 1.0;
            lattices[o] = lat;
        }

        bool Edge(int x, int y) => x < edgePad || x >= w - edgePad || y < edgePad || y >= h - edgePad;

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (Edge(x, y)) continue;
            double sample = 0;
            for (int o = 0; o < cellSizes.Length; o++)
            {
                int cs = cellSizes[o];
                double fx = (double)x / cs, fy = (double)y / cs;
                int ix = (int)fx, iy = (int)fy;
                double tx = fx - ix, ty = fy - iy;
                tx = tx * tx * (3.0 - 2.0 * tx);
                ty = ty * ty * (3.0 - 2.0 * ty);
                int lw = latW[o];
                var lat = lattices[o];
                double v00 = lat[iy * lw + ix], v10 = lat[iy * lw + ix + 1];
                double v01 = lat[(iy + 1) * lw + ix], v11 = lat[(iy + 1) * lw + ix + 1];
                double a = v00 + (v10 - v00) * tx, b = v01 + (v11 - v01) * tx;
                sample += (a + (b - a) * ty) * amps[o];
            }
            g[y * w + x] = (byte)(sample / ampSum > 0.0 ? 1 : 0);
        }

        int n = chFx.Length;
        var chCx = new int[n]; var chCy = new int[n]; var chCr = new int[n];
        for (int i = 0; i < n; i++)
        {
            chCx[i] = (int)(chFx[i] * w);
            chCy[i] = (int)(chFy[i] * h);
            chCr[i] = (int)(chFr[i] * size);
        }
        for (int i = 0; i < n; i++) StampDisk(g, w, h, edgePad, chCx[i], chCy[i], chCr[i]);
        for (int i = 0; i < corA.Length; i++)
            StampCorridor(g, w, h, edgePad, chCx[corA[i]], chCy[corA[i]], chCx[corB[i]], chCy[corB[i]], corridorHalfWidth);

        var tmp = new byte[w * h];
        for (int it = 0; it < smoothingPasses; it++)
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (Edge(x, y)) { tmp[i] = 0; continue; }
                int rm = i - w, rp = i + w;
                int floorN = g[rm - 1] + g[rm] + g[rm + 1] + g[i - 1] + g[i + 1] + g[rp - 1] + g[rp] + g[rp + 1];
                tmp[i] = (byte)(g[i] == 1 ? (floorN >= deathLimit ? 1 : 0) : (floorN >= birthLimit ? 1 : 0));
            }
            (g, tmp) = (tmp, g);
        }

        for (int i = 0; i < n; i++) StampDisk(g, w, h, edgePad, chCx[i], chCy[i], 4);
        for (int i = 0; i < corA.Length; i++)
            StampCorridor(g, w, h, edgePad, chCx[corA[i]], chCy[corA[i]], chCx[corB[i]], chCy[corB[i]], 1);
        return g;
    }

    private static void StampDisk(byte[] g, int w, int h, int pad, int cx, int cy, int r)
    {
        int r2 = r * r;
        for (int y = Math.Max(pad, cy - r); y <= Math.Min(h - pad - 1, cy + r); y++)
        for (int x = Math.Max(pad, cx - r); x <= Math.Min(w - pad - 1, cx + r); x++)
        {
            int dx = x - cx, dy = y - cy;
            if (dx * dx + dy * dy <= r2) g[y * w + x] = 1;
        }
    }

    private static void StampCorridor(byte[] g, int w, int h, int pad, int ax, int ay, int bx, int by, int halfW)
    {
        int dx = bx - ax, dy = by - ay;
        int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (steps <= 0) return;
        for (int s = 0; s <= steps; s++)
        {
            int px = ax + (int)((double)dx * s / steps);
            int py = ay + (int)((double)dy * s / steps);
            for (int y = Math.Max(pad, py - halfW); y <= Math.Min(h - pad - 1, py + halfW); y++)
            for (int x = Math.Max(pad, px - halfW); x <= Math.Min(w - pad - 1, px + halfW); x++)
                g[y * w + x] = 1;
        }
    }
}
