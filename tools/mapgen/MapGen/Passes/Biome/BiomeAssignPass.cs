using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Biome;

public sealed class BiomeAssignParams
{
    [TunableDisplay("Forest mosaic share", Tooltip = "Share of temperate grass/forest cells assigned to forest groves. Zero keeps climate-only classification.")]
    [TunableRange(0, 1)]
    public double ForestMosaicShare { get; set; } = 0;

    [TunableDisplay("Forest mosaic frequency")]
    [TunableRange(0.001, 0.5)]
    public double ForestMosaicFrequency { get; set; } = 0.06;

    [TunableDisplay("Flat lowland fraction", Tooltip = "Share of non-rock land on the sea-level plain. Zero preserves the legacy height scaling.")]
    [TunableRange(0, 1)]
    public double FlatLowlandFraction { get; set; } = 0;

    [TunableDisplay("Force beach fringe", Tooltip = "Legacy continuous sand coast. Disable to allow the inland material to reach the shore.")]
    public bool ForceBeachFringe { get; set; } = true;

    // Terrain-domain water threshold: cells below it are water. With RebaseToSeaLevel on
    // (default) the heightfield is shifted after classification so this level becomes
    // GenIR.SeaLevelZ = 0, and every later pass works in that one frame.
    [TunableDisplay("Sea level Z")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Rebase heights to sea level", Tooltip = "Shift heights so SeaLevelZ becomes Z 0 (Felucca's frame: shoreline 0, ocean -5, dug shore -15). Off = keep the noise frame; GenIR.SeaLevelZ is then set to SeaLevelZ.")]
    public bool RebaseToSeaLevel { get; set; } = true;

    [TunableDisplay("Land height scale", Tooltip = "Land Z above sea is multiplied by this during the rebase. Felucca lowland is nearly flat (half of all land at Z 0, 75% at Z<=6); noise heights of 0..90 read as endless hills. Mountain Shape raises the mountain interiors back up afterwards. 1 = keep noise heights.")]
    [TunableRange(0.05, 1.0)]
    public double LandHeightScale { get; set; } = 0.3;

    [TunableDisplay("Biome smoothing passes", Tooltip = "Majority-filter passes that remove single-cell specks and 1-wide slivers of a land biome. Slivers have no valid transition tiles (both sides of a 1-wide strip need an edge) and render as hard seams.")]
    [TunableRange(0, 6)]
    public int SmoothPasses { get; set; } = 2;

    [TunableDisplay("Swamp grass buffer", Tooltip = "Land cells of another biome (forest, desert, ...) within this many tiles of a swamp become Grassland, so swamp only ever meets grass (UO ships swamp<->grass transitions only). Mountain/snow next to swamp demotes the swamp cell instead. 0 = off.")]
    [TunableRange(0, 6)]
    public int SwampGrassBuffer { get; set; } = 2;

    [TunableDisplay("Snow-cap high mountains", Tooltip = "HighMountain cells colder than SnowTemperature become Snow (snowy peaks). Mountain is classified before Snow, so without this cold peaks never get snow.")]
    public bool SnowCaps { get; set; } = true;

    [TunableDisplay("Edge band (tiles)", Tooltip = "Outer band forced to ocean right after classification, so the coast passes give the cut a beach and edge tiles (the validator used to do this last and left a raw seam). Skipped when the band is mostly land (maps that run to the edge). Follows Map Validator's EdgeBandWidth unless set here.")]
    [TunableRange(0, 64)]
    public int EdgeBandWidth { get; set; } = 8;

    [TunableDisplay("Edge band max land share")] [TunableRange(0, 1)]
    public double EdgeBandMaxLandFraction { get; set; } = 0.25;

    [TunableDisplay("Mountain Z threshold")] [TunableRange(0, 127)]
    public int MountainZ { get; set; } = 50;

    [TunableDisplay("Min mountain share of land", Tooltip = "If fewer land cells than this lie above MountainZ, the threshold drops to the height that gives this share (0 = use MountainZ as is). Keeps ranges on low-relief seeds; Britannia is roughly a tenth rock.")]
    [TunableRange(0, 0.5)]
    public double MountainMinLandFraction { get; set; } = 0.06;

    [TunableDisplay("Range strength", Tooltip = "Mountains follow long ridge lines: the mountain test uses height + strength x (MountainZ - SeaLevelZ) x (ridge - 0.5), where ridge is a stretched ridged-noise line field. 0 = plain height threshold (round blobs on the noise peaks).")]
    [TunableRange(0, 2)]
    public double RangeStrength { get; set; } = 0.9;

    [TunableDisplay("Range frequency", Tooltip = "Ridge-line noise frequency along the range (1/tiles); across the range it is 2x higher, so ranges are long and narrow.")]
    [TunableRange(0.0005, 0.05)]
    public double RangeFrequency { get; set; } = 0.004;

    [TunableDisplay("Snow temperature")] [TunableRange(0, 255)]
    public int SnowTemperature { get; set; } = 60;

    // Cascade is "Desert if m<DesertMax, else Forest if m>ForestMin, else Grassland".
    // Defaults widen the Grassland window (40..180) so it's the dominant interior biome
    // when paired with the calibrated MoistureClimatePass sweep.
    [TunableDisplay("Desert moisture max")] [TunableRange(0, 255)]
    public int DesertMoistureMax { get; set; } = 18;

    [TunableDisplay("Forest moisture min")] [TunableRange(0, 255)]
    public int ForestMoistureMin { get; set; } = 180;

    // Optional extended-palette knobs. Defaults are set so old presets behave exactly
    // as before — each new biome only triggers when its threshold is set in-range.

    [TunableDisplay("Tundra temperature", Tooltip = "T < this (and >= SnowTemperature) → Tundra. Set 0 to disable.")]
    [TunableRange(0, 255)]
    public int TundraTemperature { get; set; } = 0;

    [TunableDisplay("Jungle temperature", Tooltip = "T > this AND M > ForestMoistureMin → Jungle. Set 256 to disable.")]
    [TunableRange(0, 256)]
    public int JungleTemperature { get; set; } = 256;

    [TunableDisplay("Savanna temperature", Tooltip = "T > this AND M between Desert and Forest → Savanna. Set 256 to disable.")]
    [TunableRange(0, 256)]
    public int SavannaTemperature { get; set; } = 256;

    [TunableDisplay("Dense-forest moisture min", Tooltip = "M > this → DenseForest (in temperate band). Set 256 to disable.")]
    [TunableRange(0, 256)]
    public int DenseForestMoistureMin { get; set; } = 256;

    [TunableDisplay("Swamp moisture min", Tooltip = "Low-altitude land with M > this → Swamp. Set 256 to disable.")]
    [TunableRange(0, 256)]
    public int SwampMoistureMin { get; set; } = 256;

    [TunableDisplay("Wetland moisture min", Tooltip = "Low-altitude land with M > this → Wetland (overrides Swamp). Set 256 to disable.")]
    [TunableRange(0, 256)]
    public int WetlandMoistureMin { get; set; } = 256;

    [TunableDisplay("Lowland Z window", Tooltip = "Cells with seaLevel < Z <= seaLevel+this are eligible for Swamp/Wetland.")]
    [TunableRange(0, 32)]
    public int LowlandZWindow { get; set; } = 4;

    [TunableDisplay("Swamp min distance from water", Tooltip = "Cells within N tiles of water or beach cannot become Swamp/Wetland. Default 2 = a 2-tile grass band always sits between any swamp and any lake/coastline (matches Felucca reference image #8 berm). Set 0 to disable the gate.")]
    [TunableRange(0, 16)]
    public int SwampMinDistFromWater { get; set; } = 2;

    [TunableDisplay("Swamp patch noise scale", Tooltip = "Frequency of the secondary noise that carves grass holes inside swamp regions. Larger value = smaller / tighter patches; smaller = bigger swamp blobs with fewer breaks. Set 0 to disable patchiness (solid swamp bodies).")]
    [TunableRange(0.0, 0.5)]
    public double SwampPatchNoiseScale { get; set; } = 0.08;

    [TunableDisplay("Swamp patch threshold", Tooltip = "Noise [0,1] above which a candidate swamp cell becomes Grassland instead. 0.35 ≈ 20% grass holes inside the swamp body. 0 disables carving (every swamp cell stays swamp).")]
    [TunableRange(0.0, 1.0)]
    public double SwampPatchThreshold { get; set; } = 0.35;

    // -----------------------------------------------------------------------
    // Equator gradient (ported from ProjectCog's HexBiomeAssigner). Biome
    // thresholds shift smoothly with latitude instead of being fixed cuts:
    // near the equator, forests get harder, deserts wider, jungle easier, snow
    // band shrinks. This is the technique ProjectCog uses to avoid horizontal
    // "biome stripes" without discrete latitude bands.
    //
    // EquatorBiasStrength = 0 → fully inert. Each shift knob is the *max*
    // delta applied at the equator (linearly scaled by 1 - latitude_norm).
    // -----------------------------------------------------------------------

    [TunableDisplay("Equator bias strength", Tooltip = "Master enable for latitude-based threshold shifts. 0 = off (legacy behavior). 1 = full shift applied at equator, decaying to 0 at poles.")]
    [TunableRange(0.0, 1.0)]
    public double EquatorBiasStrength { get; set; } = 0.0;

    [TunableDisplay("Equator forest shift", Tooltip = "ForestMoistureMin shifts UP by this much at the equator (forests need more moisture in hot climates → grassland/desert dominate dry interiors).")]
    [TunableRange(0, 80)]
    public int EquatorForestShift { get; set; } = 30;

    [TunableDisplay("Equator desert shift", Tooltip = "DesertMoistureMax shifts UP by this much at the equator (wider desert window in hot zones).")]
    [TunableRange(0, 80)]
    public int EquatorDesertShift { get; set; } = 15;

    [TunableDisplay("Equator jungle shift", Tooltip = "JungleTemperature shifts DOWN by this much at the equator (jungle triggers more easily in hot zones).")]
    [TunableRange(0, 80)]
    public int EquatorJungleShift { get; set; } = 25;

    [TunableDisplay("Equator snow shift", Tooltip = "SnowTemperature shifts DOWN by this much at the equator (snow band narrows in hot zones; harmless at the equator itself but smooths the transition).")]
    [TunableRange(0, 80)]
    public int EquatorSnowShift { get; set; } = 20;
}

public sealed class BiomeAssignPass : IGenerationPass
{
    public string Name => "Biome Assign";
    public string Category => "Biome";

    public IrFields Reads => IrFields.Height | IrFields.Moisture | IrFields.Temperature;
    public IrFields Writes => IrFields.Biome;

    public object CreateDefaultParams() => new BiomeAssignParams();

    public void Run(GenContext ctx, object parameters)
    {
        // TODO: fully data-driven rule table. v1 hardcoded thresholds for proof-of-pipeline.
        var p = (BiomeAssignParams)parameters;
        var ir = ctx.IR;
        ir.EnsureBiome();
        var z = ir.Height_Z!;
        var m = ir.Moisture!;
        var t = ir.Temperature!;
        var b = ir.Biome!;
        var scope = ir.Scope;
        int touched = 0;
        var counts = new int[256];

        // Equator bias precompute: latitude weight is 1 at equator (mid-Y),
        // 0 at poles. Master strength scales every per-threshold shift below.
        bool equatorBiasOn = p.EquatorBiasStrength > 0.0;

        // Mountain score: height plus a ridge-line bonus/penalty, so ranges run along long
        // narrow lines instead of sitting as round blobs on the height noise's peaks.
        var mscore = BuildMountainScore(ir, z, p);

        // Effective mountain threshold: never fewer than MountainMinLandFraction of the land.
        int mountainZ = p.MountainZ;
        if (p.MountainMinLandFraction > 0)
        {
            var zHist = new int[256];
            int landCells = 0;
            for (ushort y = scope.Y1; y <= scope.Y2; y++)
            for (ushort x = scope.X1; x <= scope.X2; x++)
            {
                int idx0 = ir.Index(x, y);
                if (z[idx0] < p.SeaLevelZ + 1) continue;
                zHist[mscore[idx0] + 128]++;
                landCells++;
            }
            int want = (int)(landCells * p.MountainMinLandFraction), above = 0;
            for (int zi = 255; zi >= 0; zi--)
            {
                if (zi - 128 <= p.MountainZ) { if (above < want) { above += zHist[zi]; if (above >= want) mountainZ = Math.Min(mountainZ, zi - 128 - 1); } }
                else above += zHist[zi];
            }
            if (mountainZ != p.MountainZ)
                ctx.Report.Notes.Add($"mountain threshold lowered {p.MountainZ} -> {mountainZ} to keep {p.MountainMinLandFraction:P0} of the land rock");
        }
        double halfH = ir.Height * 0.5;

        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        {
            // Per-row latitude weight (constant across X) — pull out of the X loop.
            // weight = 1 at equator, 0 at poles.
            double latWeight;
            if (equatorBiasOn)
            {
                double latNorm = Math.Abs((y - halfH) / halfH);
                latWeight = (1.0 - latNorm) * p.EquatorBiasStrength;
                if (latWeight < 0) latWeight = 0;
            }
            else latWeight = 0;

            // Latitude-shifted thresholds — equal to the source params when bias is off.
            int forestMin     = p.ForestMoistureMin + (int)Math.Round(latWeight * p.EquatorForestShift);
            int desertMax     = p.DesertMoistureMax + (int)Math.Round(latWeight * p.EquatorDesertShift);
            int jungleTemp    = p.JungleTemperature - (int)Math.Round(latWeight * p.EquatorJungleShift);
            int snowTemp      = p.SnowTemperature   - (int)Math.Round(latWeight * p.EquatorSnowShift);

            for (ushort x = scope.X1; x <= scope.X2; x++)
            {
                int idx = ir.Index(x, y);
                sbyte zv = z[idx];
                byte mv = m[idx];
                byte tv = t[idx];

                BiomeId biome;
                if (zv < p.SeaLevelZ - 8) biome = BiomeId.DeepWater;
                else if (zv < p.SeaLevelZ) biome = BiomeId.ShallowWater;
                // 1-Z-unit beach band only; the rest of the shoreline becomes Beach below via
                // a neighbour-of-water sweep so we get a 1-tile fringe instead of a chunky carpet.
                else if (p.ForceBeachFringe && zv < p.SeaLevelZ + 1) biome = BiomeId.Beach;
                else if (mscore[idx] > mountainZ + 30) biome = p.SnowCaps && tv < snowTemp ? BiomeId.Snow : BiomeId.HighMountain;
                else if (mscore[idx] > mountainZ) biome = BiomeId.Mountain;
                else if (tv < snowTemp) biome = BiomeId.Snow;
                else if (tv < p.TundraTemperature) biome = BiomeId.Tundra;
                // Lowland wet — only fire when explicitly enabled (defaults disable both).
                else if (zv <= p.SeaLevelZ + p.LowlandZWindow && mv >= p.WetlandMoistureMin) biome = BiomeId.Wetland;
                else if (zv <= p.SeaLevelZ + p.LowlandZWindow && mv >= p.SwampMoistureMin) biome = BiomeId.Swamp;
                // Hot bands.
                else if (tv > jungleTemp && mv > forestMin) biome = BiomeId.Jungle;
                else if (tv > p.SavannaTemperature && mv >= desertMax && mv < forestMin) biome = BiomeId.Savanna;
                // Moisture cascade (driest → wettest).
                else if (mv < desertMax) biome = BiomeId.Desert;
                else if (mv >= p.DenseForestMoistureMin) biome = BiomeId.DenseForest;
                else if (mv > forestMin) biome = BiomeId.Forest;
                else biome = BiomeId.Grassland;

                b[idx] = (byte)biome;
                counts[(byte)biome]++;
                touched++;
            }
        }

        // Fringe sweep: any non-water non-beach cell that touches water in the 4
        // cardinal directions becomes Beach. Produces a 1-tile sand fringe at the
        // shoreline; matches Felucca's natural look without a wide altitude band.
        int fringe = 0;
        if (p.ForceBeachFringe)
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            var current = (BiomeId)b[idx];
            if (current is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.Beach) continue;
            bool touchesWater = false;
            if (x > 0)              touchesWater |= IsWaterBiome(b[ir.Index(x - 1, y)]);
            if (!touchesWater && x + 1 < ir.Width)  touchesWater |= IsWaterBiome(b[ir.Index(x + 1, y)]);
            if (!touchesWater && y > 0)             touchesWater |= IsWaterBiome(b[ir.Index(x, y - 1)]);
            if (!touchesWater && y + 1 < ir.Height) touchesWater |= IsWaterBiome(b[ir.Index(x, y + 1)]);
            if (!touchesWater) continue;
            counts[(byte)current]--;
            b[idx] = (byte)BiomeId.Beach;
            counts[(byte)BiomeId.Beach]++;
            fringe++;
        }
        if (fringe > 0) ctx.Report.Notes.Add($"beach fringe: {fringe} cells reclassified");

        // Swamp placement post-processing: push swamps inland of water/beach and
        // carve internal grass patches so swamp regions read as Felucca's irregular
        // mosaic instead of a solid ring around every lake/coastline. Both gates are
        // tunable; the no-op path is hit when SwampMinDistFromWater == 0 AND
        // SwampPatchNoiseScale == 0, so legacy presets that don't care about swamp
        // distribution pay nothing.
        ApplyForestMosaic(ir, b, p);
        DemoteCoastalAndPatchSwamps(ctx, ir, b, p, counts);
        if (p.SmoothPasses > 0) SmoothSlivers(ctx, ir, b, p.SmoothPasses);
        if (p.SwampGrassBuffer > 0) BufferSwamps(ctx, ir, b, p.SwampGrassBuffer);
        Rebase(ctx, ir, z, b, p);
        ApplyEdgeBand(ctx, ir, z, b, p);

        // Recount after buffering/smoothing.
        Array.Clear(counts);
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
            counts[b[ir.Index(x, y)]]++;

        ctx.Report.TilesTouched = touched;
        // Histogram for tuning visibility — only print biomes that actually appeared.
        var present = new List<string>();
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            var name = Enum.IsDefined(typeof(BiomeId), (byte)i) ? ((BiomeId)i).ToString() : $"#{i}";
            double pct = 100.0 * counts[i] / Math.Max(1, touched);
            present.Add($"{name}={pct:F1}%");
        }
        ctx.Report.Notes.Add("biomes: " + string.Join(", ", present));
    }

    private static void ApplyForestMosaic(GenIR ir, byte[] b, BiomeAssignParams p)
    {
        if (p.ForestMosaicShare <= 0) return;
        var noise = FieldOps.Octaves(ir.Seed, 0x464F52455354UL, 4);
        var samples = new List<(double Value, int Index)>();
        var scope = ir.Scope;
        for (int y = scope.Y1; y <= scope.Y2; y++)
        for (int x = scope.X1; x <= scope.X2; x++)
        {
            int i = ir.Index(x, y);
            if ((BiomeId)b[i] is not (BiomeId.Grassland or BiomeId.Forest or BiomeId.DenseForest or BiomeId.Savanna)) continue;
            double value = FieldOps.Fbm(noise, x, y, p.ForestMosaicFrequency);
            samples.Add((value, i));
        }
        // Sort with a stable spatial tie-break so the requested coverage does not
        // depend on the climate histogram or on parallel scheduling.
        samples.Sort((a, c) => { int v = a.Value.CompareTo(c.Value); return v != 0 ? v : a.Index.CompareTo(c.Index); });
        int forest = (int)(samples.Count * Math.Clamp(p.ForestMosaicShare, 0, 1));
        for (int i = 0; i < samples.Count; i++)
            b[samples[i].Index] = (byte)(i < forest ? BiomeId.Forest : BiomeId.Grassland);
    }

    private static sbyte[] BuildMountainScore(GenIR ir, sbyte[] z, BiomeAssignParams p)
    {
        var score = (sbyte[])z.Clone();
        if (p.RangeStrength <= 0) return score;
        double span = Math.Max(8, p.MountainZ - p.SeaLevelZ) * p.RangeStrength;
        var oct = FieldOps.Octaves(ir.Seed, 0x5241_4E47UL, 3);
        var warp = FieldOps.Octaves(ir.Seed, 0x5741_5250UL, 2);
        // Range direction from the seed; the noise is sampled stretched along it.
        double ang = (FieldOps.Hash(7, 11, ir.Seed) % 3600) / 3600.0 * Math.PI;
        double ca = Math.Cos(ang), sa = Math.Sin(ang);
        var scope = ir.Scope;
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            if (z[idx] < p.SeaLevelZ + 1) continue;
            double wx = x + 40 * FieldOps.Fbm(warp, x, y, 0.004);
            double wy = y + 40 * FieldOps.Fbm(warp, x + 913, y - 377, 0.004);
            double u = wx * ca + wy * sa, v = (-wx * sa + wy * ca) * 2.0;
            double n = FieldOps.Fbm(oct, u, v, p.RangeFrequency);
            double ridge = 1.0 - Math.Min(1.0, Math.Abs(n) * 1.5);
            ridge *= ridge;
            int sv = (int)Math.Round(z[idx] + span * (ridge - 0.5));
            score[idx] = (sbyte)Math.Clamp(sv, sbyte.MinValue, sbyte.MaxValue);
        }
        return score;
    }

    private static bool IsWaterBiome(byte b) =>
        (BiomeId)b is BiomeId.DeepWater or BiomeId.ShallowWater;

    private static bool IsSmoothable(BiomeId id) => id is BiomeId.Grassland or BiomeId.Forest or BiomeId.DenseForest
        or BiomeId.Jungle or BiomeId.Savanna or BiomeId.Desert or BiomeId.Tundra or BiomeId.Snow
        or BiomeId.Swamp or BiomeId.Wetland or BiomeId.Mountain or BiomeId.HighMountain;

    // Ocean in the outer band, before rivers and coast: those passes then shape a proper
    // shore along the cut. Same skip rule as the validator's EdgeBandRule.
    private static void ApplyEdgeBand(GenContext ctx, GenIR ir, sbyte[] z, byte[] b, BiomeAssignParams p)
    {
        int n = p.EdgeBandWidth;
        if (n <= 0) return;
        var scope = ir.Scope;
        int cells = 0, land = 0;
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            if (!(x < n || y < n || x >= ir.Width - n || y >= ir.Height - n)) continue;
            cells++;
            if (!IsWaterBiome(b[ir.Index(x, y)])) land++;
        }
        if (cells == 0 || land == 0) return;
        if (land > cells * p.EdgeBandMaxLandFraction)
        {
            ctx.Report.Notes.Add($"edge band {100.0 * land / cells:F0}% land: map runs to the edge, band left as is");
            return;
        }
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            if (!(x < n || y < n || x >= ir.Width - n || y >= ir.Height - n)) continue;
            int idx = ir.Index(x, y);
            if (IsWaterBiome(b[idx])) continue;
            b[idx] = (byte)BiomeId.DeepWater;
            z[idx] = (sbyte)Math.Min(z[idx], ir.OceanZ);
        }
        ctx.Report.Notes.Add($"edge band: {land} land cells in the outer {n} tiles set to ocean");
    }

    // Shift the heightfield so the classification threshold becomes Z 0 and compress land
    // heights (Felucca lowland is near flat). Water keeps its depth below the new sea level.
    // Sets GenIR.SeaLevelZ, which every later pass reads instead of its own copy.
    private static void Rebase(GenContext ctx, GenIR ir, sbyte[] z, byte[] b, BiomeAssignParams p)
    {
        if (!p.RebaseToSeaLevel)
        {
            ir.SeaLevelZ = p.SeaLevelZ;
            return;
        }
        var scope = ir.Scope;
        double k = Math.Clamp(p.LandHeightScale, 0.05, 1.0);
        bool Soft(int i) => !IsWaterBiome(b[i]) && (BiomeId)b[i] is not (BiomeId.Mountain or BiomeId.HighMountain or BiomeId.Snow);
        int flatBelow = 0;
        if (p.FlatLowlandFraction > 0)
        {
            var samples = new List<int>();
            for (int y = scope.Y1; y <= scope.Y2; y++)
            for (int x = scope.X1; x <= scope.X2; x++)
            {
                int i = ir.Index(x, y);
                if (Soft(i)) samples.Add(Math.Max(0, z[i] - p.SeaLevelZ));
            }
            samples.Sort();
            if (samples.Count > 0) flatBelow = samples[Math.Clamp((int)(samples.Count * p.FlatLowlandFraction), 0, samples.Count - 1)];
        }
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            int rel = z[idx] - p.SeaLevelZ;
            int nz = rel < 0 || IsWaterBiome(b[idx]) ? Math.Min(rel, -1) : (int)Math.Round(rel * k);
            if (p.FlatLowlandFraction > 0 && Soft(idx))
            {
                // Broad sea-level plains with five-Z terraces above them. The two-Z
                // shoulders retain intermediate heights at the terrace joins.
                double h = Math.Max(0, rel - flatBelow) * k;
                int terrace = (int)(h / 5);
                double shoulder = h - terrace * 5;
                nz = terrace * 5 + (shoulder < 3 ? 0 : (int)Math.Round((shoulder - 3) * 2.5));
            }
            z[idx] = (sbyte)Math.Clamp(nz, sbyte.MinValue, sbyte.MaxValue);
        }
        ctx.Report.Notes.Add($"lowland flat threshold={flatBelow}, requested fraction={p.FlatLowlandFraction:F2}");
        ir.SeaLevelZ = 0;
        ctx.Report.Notes.Add($"rebased: terrain sea {p.SeaLevelZ} -> 0, land x{k:F2}");
    }

    // Swamp must only meet grass. Cells of a soft biome within `radius` of swamp become
    // Grassland; hard neighbours (mountain, snow) demote the touching swamp cell instead.
    private static void BufferSwamps(GenContext ctx, GenIR ir, byte[] b, int radius)
    {
        var scope = ir.Scope;
        int sw = scope.Width, sh = scope.Height;
        var seed = new bool[sw * sh];
        bool any = false;
        for (int ly = 0; ly < sh; ly++)
        for (int lx = 0; lx < sw; lx++)
        {
            var id = (BiomeId)b[ir.Index(scope.X1 + lx, scope.Y1 + ly)];
            if (id is BiomeId.Swamp or BiomeId.Wetland) { seed[ly * sw + lx] = true; any = true; }
        }
        if (!any) return;
        var dist = FieldOps.Distance4(seed, sw, sh, radius + 1);
        int toGrass = 0, demoted = 0;
        for (int ly = 0; ly < sh; ly++)
        for (int lx = 0; lx < sw; lx++)
        {
            int d = dist[ly * sw + lx];
            if (d == 0 || d > radius) continue;
            int idx = ir.Index(scope.X1 + lx, scope.Y1 + ly);
            var id = (BiomeId)b[idx];
            if (id is BiomeId.Forest or BiomeId.DenseForest or BiomeId.Jungle or BiomeId.Desert
                or BiomeId.Savanna or BiomeId.Tundra)
            {
                b[idx] = (byte)BiomeId.Grassland;
                toGrass++;
            }
        }
        // Hard neighbours: a swamp cell 4-adjacent to mountain/snow becomes grass.
        for (int ly = 0; ly < sh; ly++)
        for (int lx = 0; lx < sw; lx++)
        {
            if (!seed[ly * sw + lx]) continue;
            int gx = scope.X1 + lx, gy = scope.Y1 + ly;
            int idx = ir.Index(gx, gy);
            bool hard = false;
            for (int k = 0; k < 4 && !hard; k++)
            {
                int nx = gx + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = gy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if ((uint)nx >= ir.Width || (uint)ny >= ir.Height) continue;
                hard = (BiomeId)b[ir.Index(nx, ny)] is BiomeId.Mountain or BiomeId.HighMountain or BiomeId.Snow;
            }
            if (!hard) continue;
            b[idx] = (byte)BiomeId.Grassland;
            demoted++;
        }
        if (toGrass + demoted > 0) ctx.Report.Notes.Add($"swamp grass buffer: {toGrass} cells -> grass, {demoted} swamp cells demoted");
    }

    // Majority filter over land biomes. A cell whose biome has <= 2 of its 8 neighbours
    // (a speck or a 1-wide line) takes the most common neighbouring land biome. Works on a
    // snapshot per pass so the result does not depend on scan order.
    private static void SmoothSlivers(GenContext ctx, GenIR ir, byte[] b, int passes)
    {
        var scope = ir.Scope;
        var snap = new byte[b.Length];
        Span<int> tally = stackalloc int[32];
        int changed = 0;
        for (int pass = 0; pass < passes; pass++)
        {
            Array.Copy(b, snap, b.Length);
            int changedThisPass = 0;
            for (int y = scope.Y1; y <= scope.Y2; y++)
            for (int x = scope.X1; x <= scope.X2; x++)
            {
                int idx = ir.Index(x, y);
                var self = (BiomeId)snap[idx];
                if (!IsSmoothable(self)) continue;
                tally.Clear();
                int same = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < scope.X1 || ny < scope.Y1 || nx > scope.X2 || ny > scope.Y2) { same++; continue; }
                    byte nb = snap[ir.Index(nx, ny)];
                    if (nb == (byte)self) same++;
                    if (IsSmoothable((BiomeId)nb) && nb < 32) tally[nb]++;
                }
                if (same > 2) continue;
                int best = -1, bestN = 0;
                for (int i = 0; i < 32; i++)
                    if (tally[i] > bestN) { bestN = tally[i]; best = i; }
                if (best < 0 || best == (int)self) continue;
                // Never grow a mountain out of a speck of lowland; only demote.
                if ((BiomeId)best is BiomeId.Mountain or BiomeId.HighMountain && self is not (BiomeId.Mountain or BiomeId.HighMountain))
                    continue;
                b[idx] = (byte)best;
                changedThisPass++;
            }
            changed += changedThisPass;
            if (changedThisPass == 0) break;
        }
        if (changed > 0) ctx.Report.Notes.Add($"biome smoothing: {changed} sliver cells reassigned");
    }

    // Re-evaluates every Swamp/Wetland cell against two gates:
    //   1) Distance from the nearest water/beach must be >= SwampMinDistFromWater.
    //      Cells closer than that get demoted to Grassland, producing the visible
    //      grass berm between water bodies and swamp interior.
    //   2) For Swamp only (Wetland stays solid), a per-cell secondary OpenSimplex2
    //      noise sample above SwampPatchThreshold demotes to Grassland, carving
    //      irregular grass patches inside the swamp body.
    // BFS for distance is capped at SwampMinDistFromWater + 1 so cost is bounded
    // by O(scope * cap). The noise seed mix is independent of NoiseHeightPass so
    // changing SwampPatch* doesn't re-roll the terrain octaves.
    private static void DemoteCoastalAndPatchSwamps(GenContext ctx, GenIR ir, byte[] b, BiomeAssignParams p, int[] counts)
    {
        // Skip the whole pass when both gates are disabled and there are no swamp
        // candidates worth scanning (avoids the BFS allocation entirely).
        bool distGateOn = p.SwampMinDistFromWater > 0;
        bool patchGateOn = p.SwampPatchNoiseScale > 0 && p.SwampPatchThreshold > 0;
        if (!distGateOn && !patchGateOn) return;

        var scope = ir.Scope;
        int sw = scope.Width;
        int sh = scope.Height;

        // Distance grid in scope-local coordinates. byte covers up to 255 rings,
        // way more than SwampMinDistFromWater needs.
        byte[]? dist = null;
        if (distGateOn)
        {
            dist = new byte[sw * sh];
            int cap = p.SwampMinDistFromWater + 1;
            byte capByte = (byte)Math.Min(cap, 255);
            for (int i = 0; i < dist.Length; i++) dist[i] = capByte;

            var queue = new Queue<(int Lx, int Ly)>();
            for (int ly = 0; ly < sh; ly++)
            for (int lx = 0; lx < sw; lx++)
            {
                int gx = scope.X1 + lx, gy = scope.Y1 + ly;
                var biome = (BiomeId)b[ir.Index(gx, gy)];
                if (biome is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.Beach)
                {
                    dist[ly * sw + lx] = 0;
                    queue.Enqueue((lx, ly));
                }
            }
            ReadOnlySpan<(int dx, int dy)> nb = stackalloc (int, int)[]
            {
                (1, 0), (-1, 0), (0, 1), (0, -1),
            };
            while (queue.Count > 0)
            {
                var (lx, ly) = queue.Dequeue();
                int d = dist[ly * sw + lx];
                if (d >= cap) continue;
                foreach (var (dx, dy) in nb)
                {
                    int nx = lx + dx, ny = ly + dy;
                    if ((uint)nx >= (uint)sw || (uint)ny >= (uint)sh) continue;
                    if (dist[ny * sw + nx] <= d + 1) continue;
                    dist[ny * sw + nx] = (byte)(d + 1);
                    queue.Enqueue((nx, ny));
                }
            }
        }

        var patchNoise = patchGateOn
            ? new OpenSimplex2(unchecked((long)(ir.Seed ^ 0xCAFE_BABE_5A11_B0AAUL)))
            : null;

        int demotedDist = 0, demotedNoise = 0;
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            var current = (BiomeId)b[idx];
            if (current != BiomeId.Swamp && current != BiomeId.Wetland) continue;
            int lx = x - scope.X1;
            int ly = y - scope.Y1;

            if (distGateOn && dist![ly * sw + lx] < p.SwampMinDistFromWater)
            {
                b[idx] = (byte)BiomeId.Grassland;
                counts[(byte)current]--;
                counts[(byte)BiomeId.Grassland]++;
                demotedDist++;
                continue;
            }

            if (current == BiomeId.Swamp && patchGateOn)
            {
                double n = patchNoise!.Eval(x * p.SwampPatchNoiseScale, y * p.SwampPatchNoiseScale);
                // OpenSimplex2.Eval returns roughly [-1, 1]; map to [0, 1] for threshold comparison.
                double n01 = (n + 1.0) * 0.5;
                if (n01 > p.SwampPatchThreshold)
                {
                    b[idx] = (byte)BiomeId.Grassland;
                    counts[(byte)BiomeId.Swamp]--;
                    counts[(byte)BiomeId.Grassland]++;
                    demotedNoise++;
                }
            }
        }

        if (demotedDist > 0 || demotedNoise > 0)
            ctx.Report.Notes.Add($"swamp demoted: dist={demotedDist}, patch={demotedNoise}");
    }
}
