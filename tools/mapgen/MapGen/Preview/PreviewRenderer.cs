using CentrED.MapGen.IR;

namespace CentrED.MapGen.Preview;

// CPU-side IR -> RGBA pixel buffer renderer. Outputs a mip-downsampled image so a
// 7168x4096 IR can render to e.g. 896x512 px with block-averaged colours. UI side uploads the buffer
// to a GPU texture each frame the IR changes.
public static class PreviewRenderer
{
    public sealed class Output
    {
        public required uint[] Pixels;       // RGBA8888 packed 0xAABBGGRR (FNA Texture2D Color order)
        public int Width;
        public int Height;
        public int MipShift;                 // tile-to-pixel = 1 << MipShift
    }

    public static Output Render(GenIR ir, PreviewLayer layers, int targetMaxDim = 1024, uint[]? radarColors = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetMaxDim);
        ArgumentOutOfRangeException.ThrowIfZero(ir.Width);
        ArgumentOutOfRangeException.ThrowIfZero(ir.Height);
        int shift = 0, stepSize = 1;
        while ((Math.Max(ir.Width, ir.Height) + stepSize - 1) / stepSize > targetMaxDim)
        { stepSize *= 2; shift++; }
        if (shift > 0)
        {
            // Average the composed map, including shore-water statics. Ceil dimensions
            // preserve partial blocks at the right and bottom edges.
            var full = Render(ir, layers, Math.Max(ir.Width, ir.Height), radarColors);
            int dw = (ir.Width + stepSize - 1) / stepSize;
            int dh = (ir.Height + stepSize - 1) / stepSize;
            var small = new Output { Pixels = new uint[dw * dh], Width = dw, Height = dh, MipShift = shift };
            for (int py = 0; py < dh; py++)
            for (int px = 0; px < dw; px++)
            {
                long r = 0, g = 0, b = 0, n = 0;
                for (int y = py * stepSize; y < Math.Min((py + 1) * stepSize, ir.Height); y++)
                for (int x = px * stepSize; x < Math.Min((px + 1) * stepSize, ir.Width); x++)
                {
                    uint color = full.Pixels[y * ir.Width + x];
                    r += color & 255; g += (color >> 8) & 255; b += (color >> 16) & 255; n++;
                }
                small.Pixels[py * dw + px] = Pack((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
            return small;
        }
        int w = ir.Width, h = ir.Height;

        var output = new Output
        {
            Pixels = new uint[w * h],
            Width = w,
            Height = h,
            MipShift = shift,
        };

        // Default background — dark grey so empty areas read clearly.
        for (int i = 0; i < output.Pixels.Length; i++) output.Pixels[i] = 0xFF202020u;

        int step = 1 << shift;

        // Compose layers in painter order (Height -> Moisture -> Temp -> Biome -> Radar -> Statics -> POIs -> Grid).
        if ((layers & PreviewLayer.Height) != 0 && ir.Height_Z is not null)
            CompositeHeight(ir, output, step);
        if ((layers & PreviewLayer.Moisture) != 0 && ir.Moisture is not null)
            CompositeChannel(ir.Moisture, ir, output, step, Pack(40, 30, 60), Pack(80, 160, 220));
        if ((layers & PreviewLayer.Temperature) != 0 && ir.Temperature is not null)
            CompositeChannel(ir.Temperature, ir, output, step, Pack(40, 80, 200), Pack(220, 60, 40));
        if ((layers & PreviewLayer.Biome) != 0 && ir.Biome is not null)
            CompositeBiome(ir, output, step);
        if ((layers & PreviewLayer.Mountains) != 0 && ir.Biome is not null)
            CompositeMountainMask(ir, output, step);
        if ((layers & PreviewLayer.Slope) != 0 && ir.Slope is not null)
            CompositeSlope(ir, output, step);
        if ((layers & PreviewLayer.LandId) != 0 && ir.LandId is not null)
            CompositeLandIdFallback(ir, output, step);
        if ((layers & PreviewLayer.LandIdRadar) != 0)
        {
            if (ir.LandId is not null)
                CompositeRadar(ir, output, radarColors ?? RadarPalette.LoadConfigured());
            else if (ir.Biome is not null) CompositeBiome(ir, output, step);
            else if (ir.Height_Z is not null) CompositeHeight(ir, output, step);
        }
        if ((layers & PreviewLayer.Rivers) != 0)
            DrawRivers(ir, output, step);
        if ((layers & PreviewLayer.Roads) != 0)
            DrawRoads(ir, output, step);
        if ((layers & PreviewLayer.Statics) != 0)
            DrawStatics(ir, output, step);
        if ((layers & PreviewLayer.Pois) != 0)
            DrawPois(ir, output, step);
        return output;
    }

    private static void CompositeRadar(GenIR ir, Output output, uint[]? palette)
    {
        for (int i = 0; i < output.Pixels.Length; i++)
        {
            int id = ir.LandId![i];
            output.Pixels[i] = palette is not null && id < palette.Length
                ? palette[id] : ColorForLandId((ushort)id);
        }
        if (palette is null) return;
        // StaticOps describes generated additions; removals are not visible props.
        var top = ir.Height_Z is null ? new sbyte[output.Pixels.Length] : (sbyte[])ir.Height_Z.Clone();
        foreach (var op in ir.StaticOps)
        {
            if (op.Kind != StaticOpKind.Add || op.X >= ir.Width || op.Y >= ir.Height) continue;
            int i = ir.Index(op.X, op.Y), id = 0x4000 + op.Id;
            if (op.Z < top[i] || id >= palette.Length) continue;
            top[i] = op.Z;
            output.Pixels[i] = palette[id];
        }
    }

    private static void CompositeSlope(GenIR ir, Output output, int step)
    {
        var s = ir.Slope!;
        // Built via Pack(r, g, b) so the RGBA byte order is correct.
        uint coolBlue = Pack(32, 64, 128);    // gentle slope = cool
        uint hotRed = Pack(220, 60, 40);      // steep slope = hot
        for (int py = 0; py < output.Height; py++)
        for (int px = 0; px < output.Width; px++)
        {
            byte v = s[ir.Index(px * step, py * step)];
            uint c = Lerp(coolBlue, hotRed, Math.Min(1.0, v / 64.0));
            output.Pixels[py * output.Width + px] = c;
        }
    }

    private static void CompositeMountainMask(GenIR ir, Output output, int step)
    {
        var b = ir.Biome!;
        for (int py = 0; py < output.Height; py++)
        for (int px = 0; px < output.Width; px++)
        {
            var biome = (BiomeId)b[ir.Index(px * step, py * step)];
            if (biome is BiomeId.Mountain)
                output.Pixels[py * output.Width + px] = Pack(120, 110, 100);
            else if (biome is BiomeId.HighMountain)
                output.Pixels[py * output.Width + px] = Pack(220, 215, 210);
            // else leave existing background
        }
    }

    // Color mapping by tile-id range so we don't need the radar palette loaded.
    // Coarse buckets: water, beach, grass, dirt, road, swamp, jungle, forest, snow, rock.
    private static void CompositeLandIdFallback(GenIR ir, Output output, int step)
    {
        var l = ir.LandId!;
        for (int py = 0; py < output.Height; py++)
        for (int px = 0; px < output.Width; px++)
        {
            ushort id = l[ir.Index(px * step, py * step)];
            output.Pixels[py * output.Width + px] = ColorForLandId(id);
        }
    }

    private static uint ColorForLandId(ushort id)
    {
        if (id is >= 0x22C and <= 0x22F) return Pack(120, 110, 100);
        if (id is >= 0x3DE9 and <= 0x3DEF) return Pack(60, 80, 50);
        if (id is >= 0xAC and <= 0xAF) return Pack(20, 130, 60);
        if (id is >= 0x11A and <= 0x11D) return Pack(240, 245, 250);
        if (id is >= 0x4C and <= 0x6B) return Pack(140, 110, 80); // sea bed / shore
        if (id is >= 0x1D and <= 0x3E) return Pack(180, 170, 120); // shore / fringe
        if (id is >= 0x77 and <= 0x78) return Pack(140, 110, 80);
        if (id is >= 0x3E9 and <= 0x3EC) return Pack(160, 150, 140);
        if (id == 0) return Pack(0x20, 0x20, 0x20);
        // Water (deep + shallow + animated)
        if (id >= 0xA8 && id <= 0xAB) return Pack(40, 90, 160);
        if (id >= 0x136 && id <= 0x137) return Pack(40, 90, 160);
        // Beach / sand
        if (id >= 0x16 && id <= 0x1C) return Pack(220, 200, 140);
        if (id >= 0x12B && id <= 0x131) return Pack(220, 200, 140);                // sand variants
        if (id >= 0x132 && id <= 0x135) return Pack(220, 200, 140);
        // Grass-sand TRANSITION tiles (the ones LandTransitionPass loves to paint).
        // Tinted slightly cooler than pure sand so the transition belt reads as a band.
        if (id >= 0x17F && id <= 0x1A4) return Pack(180, 180, 150);                // grass+sand transitions
        if (id >= 0x1B5 && id <= 0x1F3) return Pack(180, 180, 150);
        // Grass + grassy
        if (id >= 0x03 && id <= 0x0B) return Pack(100, 170, 70);
        if (id >= 0xC4 && id <= 0xC7) return Pack(40, 110, 50);                    // forest floor
        if (id >= 0xC8 && id <= 0xCB) return Pack(60, 130, 60);                    // forest variants
        // Dirt + road
        if (id >= 0x71 && id <= 0x76) return Pack(140, 110, 80);
        if (id >= 0x47 && id <= 0x49) return Pack(150, 130, 100);
        // Swamp / wetland
        if (id >= 0x9C && id <= 0x9F) return Pack(60, 80, 50);
        // Mountain rock + grass-mountain transitions
        if (id >= 0xDC && id <= 0xDF) return Pack(120, 110, 100);
        if (id >= 0x238 && id <= 0x244) return Pack(150, 140, 130);                // grass→mountain belt
        if (id >= 0x4CC && id <= 0x4D0) return Pack(150, 140, 130);
        // Snow + tundra
        if (id >= 0x6C && id <= 0x6F) return Pack(240, 245, 250);
        // Scrub + dry-grass
        if (id >= 0x16E && id <= 0x172) return Pack(180, 170, 80);
        // Animated lava
        if (id >= 0x4E8 && id <= 0x4EB) return Pack(220, 70, 20);
        // Cobble
        if (id >= 0x518 && id <= 0x547) return Pack(160, 150, 140);
        // Rock-edge (water-bleed) — these are the tiles producing visible water patches
        // when placed on land. Render as a distinct teal so they POP in the dump and
        // the user can localise them at a glance.
        if (id >= 0x179D && id <= 0x17AC) return Pack(80, 200, 220);
        // Unmapped — keep magenta so unknown-id outliers still stand out, but a
        // muted shade so the gallery isn't visually screaming.
        return Pack(140, 90, 160);
    }

    private static void DrawRivers(GenIR ir, Output output, int step)
    {
        uint col = Pack(70, 130, 220);
        foreach (var seg in ir.Rivers)
        {
            foreach (var (x, y) in seg.Path)
            {
                int px = x >> ir_log(step);
                int py = y >> ir_log(step);
                if (px < 0 || py < 0 || px >= output.Width || py >= output.Height) continue;
                output.Pixels[py * output.Width + px] = col;
            }
        }
    }

    private static void DrawRoads(GenIR ir, Output output, int step)
    {
        uint col = Pack(190, 130, 60);
        foreach (var seg in ir.Roads)
        {
            foreach (var (x, y) in seg.Path)
            {
                int px = x >> ir_log(step);
                int py = y >> ir_log(step);
                if (px < 0 || py < 0 || px >= output.Width || py >= output.Height) continue;
                output.Pixels[py * output.Width + px] = col;
            }
        }
    }

    private static void CompositeHeight(GenIR ir, Output output, int step)
    {
        var z = ir.Height_Z!;
        for (int py = 0; py < output.Height; py++)
        for (int px = 0; px < output.Width; px++)
        {
            int tx = px * step;
            int ty = py * step;
            sbyte zv = z[ir.Index(tx, ty)];
            byte g = (byte)(zv + 128);
            // Hill-shade-ish: water tinted blue, land tinted earthy.
            uint color = zv < 0
                ? Pack(20, 30, (byte)(60 + g / 4), 255)
                : Pack((byte)(80 + g / 3), (byte)(70 + g / 4), 50, 255);
            output.Pixels[py * output.Width + px] = color;
        }
    }

    private static void CompositeChannel(byte[] src, GenIR ir, Output output, int step, uint lo, uint hi)
    {
        for (int py = 0; py < output.Height; py++)
        for (int px = 0; px < output.Width; px++)
        {
            byte v = src[ir.Index(px * step, py * step)];
            output.Pixels[py * output.Width + px] = Lerp(lo, hi, v / 255.0);
        }
    }

    private static readonly uint[] BiomeColors = BuildBiomeColors();

    private static uint[] BuildBiomeColors()
    {
        var c = new uint[256];
        c[(int)BiomeId.Unassigned] = 0xFF000000u;
        c[(int)BiomeId.DeepWater] = Pack(10, 25, 80);
        c[(int)BiomeId.ShallowWater] = Pack(40, 90, 160);
        c[(int)BiomeId.Beach] = Pack(220, 200, 140);
        c[(int)BiomeId.Grassland] = Pack(100, 170, 70);
        c[(int)BiomeId.Forest] = Pack(40, 110, 50);
        c[(int)BiomeId.DenseForest] = Pack(20, 80, 40);
        c[(int)BiomeId.Jungle] = Pack(20, 130, 60);
        c[(int)BiomeId.Savanna] = Pack(180, 170, 80);
        c[(int)BiomeId.Desert] = Pack(220, 190, 110);
        c[(int)BiomeId.Tundra] = Pack(160, 170, 180);
        c[(int)BiomeId.Snow] = Pack(240, 245, 250);
        c[(int)BiomeId.Mountain] = Pack(120, 110, 100);
        c[(int)BiomeId.HighMountain] = Pack(180, 175, 170);
        c[(int)BiomeId.Swamp] = Pack(60, 80, 50);
        c[(int)BiomeId.Wetland] = Pack(80, 110, 90);
        c[(int)BiomeId.Lava] = Pack(220, 70, 20);
        c[(int)BiomeId.Cave] = Pack(40, 30, 25);
        c[(int)BiomeId.Road] = Pack(150, 130, 100);
        c[(int)BiomeId.River] = Pack(70, 130, 200);
        return c;
    }

    private static void CompositeBiome(GenIR ir, Output output, int step)
    {
        var b = ir.Biome!;
        for (int py = 0; py < output.Height; py++)
        for (int px = 0; px < output.Width; px++)
        {
            byte id = b[ir.Index(px * step, py * step)];
            output.Pixels[py * output.Width + px] = BiomeColors[id];
        }
    }

    private static void DrawStatics(GenIR ir, Output output, int step)
    {
        foreach (var op in ir.StaticOps)
        {
            int px = op.X >> ir_log(step);
            int py = op.Y >> ir_log(step);
            if (px < 0 || py < 0 || px >= output.Width || py >= output.Height) continue;
            output.Pixels[py * output.Width + px] = 0xFF00FF00u;
        }
    }

    private static void DrawPois(GenIR ir, Output output, int step)
    {
        foreach (var poi in ir.Pois)
        {
            int px = poi.X >> ir_log(step);
            int py = poi.Y >> ir_log(step);
            if (px < 0 || py < 0 || px >= output.Width || py >= output.Height) continue;
            // 3x3 cross
            uint col = 0xFFFFFFFFu;
            output.Pixels[py * output.Width + px] = col;
            if (px > 0) output.Pixels[py * output.Width + px - 1] = col;
            if (px + 1 < output.Width) output.Pixels[py * output.Width + px + 1] = col;
            if (py > 0) output.Pixels[(py - 1) * output.Width + px] = col;
            if (py + 1 < output.Height) output.Pixels[(py + 1) * output.Width + px] = col;
        }
    }

    private static int ir_log(int v) { int n = 0; while (v > 1) { v >>= 1; n++; } return n; }

    // Pack as little-endian RGBA8888 — FNA's Color struct expects byte order R,G,B,A
    // and Texture2D expects an int/uint per pixel. We construct uint with R in byte 0.
    private static uint Pack(byte r, byte g, byte b, byte a = 255)
        => (uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

    private static uint Lerp(uint a, uint b, double t)
    {
        byte ar = (byte)(a & 0xFF), ag = (byte)((a >> 8) & 0xFF), ab = (byte)((a >> 16) & 0xFF), aa = (byte)((a >> 24) & 0xFF);
        byte br = (byte)(b & 0xFF), bg = (byte)((b >> 8) & 0xFF), bb_ = (byte)((b >> 16) & 0xFF), ba = (byte)((b >> 24) & 0xFF);
        byte r = (byte)(ar + (br - ar) * t);
        byte g = (byte)(ag + (bg - ag) * t);
        byte b2 = (byte)(ab + (bb_ - ab) * t);
        byte a2 = (byte)(aa + (ba - aa) * t);
        return Pack(r, g, b2, a2);
    }
}
