using System.Security.Cryptography;
using CentrED.MapGen.IR;
using CentrED.MapGen.Preview;

namespace GuoMapGen;

/// <summary>An RGBA image, pixels 0xAABBGGRR (the PngWriter layout), one pixel per map cell.</summary>
public sealed class MapImage(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public uint[] Pixels { get; } = new uint[width * height];

    public static uint Rgb(int r, int g, int b) => 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | (uint)r;

    /// <summary>Writes a PNG no wider or taller than <paramref name="max"/>, averaging blocks when it shrinks
    /// (picking one cell per block would drop one-tile coast bands and transitions).</summary>
    public void Save(string path, int max)
    {
        int f = Math.Max(1, (int)Math.Ceiling(Math.Max(Width, Height) / (double)Math.Max(1, max)));
        if (f == 1) { PngWriter.Write(path, Pixels, Width, Height); return; }
        int w = Width / f, h = Height / f;
        var outPx = new uint[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            long r = 0, g = 0, b = 0; int n = 0;
            for (int dy = 0; dy < f; dy++)
            for (int dx = 0; dx < f; dx++)
            {
                uint c = Pixels[(y * f + dy) * Width + x * f + dx];
                r += c & 0xFF; g += (c >> 8) & 0xFF; b += (c >> 16) & 0xFF; n++;
            }
            outPx[y * w + x] = Rgb((int)(r / n), (int)(g / n), (int)(b / n));
        }
        PngWriter.Write(path, outPx, w, h);
    }
}

/// <summary>The client's radarcol.mul: one 16-bit 555 colour per land id, statics at 0x4000 + id.</summary>
public sealed class RadarColors(ushort[] table)
{
    public static RadarColors? Load(string? clientData)
    {
        var dir = clientData ?? Environment.GetEnvironmentVariable("UO_CLIENT_DATA");
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var path = Path.Combine(dir, "radarcol.mul");
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var table = new ushort[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, table, 0, table.Length * 2);
        return new RadarColors(table);
    }

    public uint Land(ushort id) => Convert(id < table.Length ? table[id] : (ushort)0);
    public uint Static(ushort id) => Convert(0x4000 + id < table.Length ? table[0x4000 + id] : (ushort)0);

    private static uint Convert(ushort c) =>
        MapImage.Rgb(((c >> 10) & 0x1F) * 255 / 31, ((c >> 5) & 0x1F) * 255 / 31, (c & 0x1F) * 255 / 31);
}

public static class Render
{
    /// <summary>Radar colours when the client data is there and land ids exist yet; biome or height otherwise.</summary>
    public static MapImage Best(GenIR ir, RadarColors? radar)
    {
        if (radar is not null && ir.LandId is not null && ir.LandId.Any(id => id != 0)) return Radar(ir, radar);
        if (ir.Biome is not null && ir.Biome.Any(b => b != 0)) return Biome(ir);
        return Height(ir);
    }

    /// <summary>What the client's radar shows: the top static's colour where there is one, else the land's.</summary>
    public static MapImage Radar(GenIR ir, RadarColors radar)
    {
        var img = new MapImage(ir.Width, ir.Height);
        var land = ir.LandId!;
        for (int i = 0; i < land.Length; i++) img.Pixels[i] = radar.Land(land[i]);
        foreach (var (cell, id) in TopStatics(ir)) img.Pixels[cell] = radar.Static(id);
        return img;
    }

    public static Dictionary<int, ushort> TopStatics(GenIR ir)
    {
        var top = new Dictionary<int, (sbyte Z, ushort Id)>();
        foreach (var op in ir.StaticOps)
        {
            if (op.Kind != StaticOpKind.Add || op.X >= ir.Width || op.Y >= ir.Height) continue;
            int cell = op.Y * ir.Width + op.X;
            if (!top.TryGetValue(cell, out var cur) || op.Z >= cur.Z) top[cell] = (op.Z, op.Id);
        }
        return top.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
    }

    public static MapImage Biome(GenIR ir)
    {
        var img = new MapImage(ir.Width, ir.Height);
        var b = ir.Biome;
        for (int i = 0; i < img.Pixels.Length; i++) img.Pixels[i] = BiomeColor(b is null ? BiomeId.Unassigned : (BiomeId)b[i]);
        return img;
    }

    public static MapImage Height(GenIR ir)
    {
        var img = new MapImage(ir.Width, ir.Height);
        var z = ir.Height_Z;
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            int v = z is null ? 0 : z[i];
            if (v < 0) { int d = Math.Clamp(120 + v * 4, 20, 120); img.Pixels[i] = MapImage.Rgb(0, d / 3, d); }
            else { int g = Math.Clamp(40 + v * 2, 0, 255); img.Pixels[i] = MapImage.Rgb(g, g, g); }
        }
        return img;
    }

    public static uint BiomeColor(BiomeId b) => b switch
    {
        BiomeId.DeepWater => MapImage.Rgb(16, 40, 96),
        BiomeId.ShallowWater => MapImage.Rgb(40, 80, 140),
        BiomeId.Beach => MapImage.Rgb(214, 196, 140),
        BiomeId.Grassland => MapImage.Rgb(80, 130, 50),
        BiomeId.Forest => MapImage.Rgb(40, 90, 30),
        BiomeId.DenseForest => MapImage.Rgb(25, 65, 20),
        BiomeId.Jungle => MapImage.Rgb(30, 110, 50),
        BiomeId.Savanna => MapImage.Rgb(170, 160, 80),
        BiomeId.Desert => MapImage.Rgb(220, 200, 130),
        BiomeId.Tundra => MapImage.Rgb(150, 160, 150),
        BiomeId.Snow => MapImage.Rgb(240, 240, 245),
        BiomeId.Mountain => MapImage.Rgb(120, 110, 100),
        BiomeId.HighMountain => MapImage.Rgb(160, 150, 140),
        BiomeId.Swamp => MapImage.Rgb(70, 80, 50),
        BiomeId.Wetland => MapImage.Rgb(80, 100, 70),
        BiomeId.Lava => MapImage.Rgb(200, 60, 20),
        BiomeId.Cave => MapImage.Rgb(60, 50, 45),
        BiomeId.Road => MapImage.Rgb(140, 110, 80),
        _ => MapImage.Rgb(0, 0, 0),
    };
}

/// <summary>Determinism fingerprint: land ids, heights and the static ops, in pipeline order.</summary>
public static class MapHash
{
    public static string Of(GenIR ir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData("guo-mapgen-1"u8);
        sha.AppendData(BitConverter.GetBytes(ir.Width));
        sha.AppendData(BitConverter.GetBytes(ir.Height));
        if (ir.LandId is { } land) sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(land.AsSpan()));
        if (ir.Height_Z is { } z) sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(z.AsSpan()));
        Span<byte> op = stackalloc byte[10];
        foreach (var o in ir.StaticOps)
        {
            op[0] = (byte)o.Kind;
            BitConverter.TryWriteBytes(op[1..], o.X);
            BitConverter.TryWriteBytes(op[3..], o.Y);
            op[5] = (byte)o.Z;
            BitConverter.TryWriteBytes(op[6..], o.Id);
            BitConverter.TryWriteBytes(op[8..], o.Hue);
            sha.AppendData(op);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>
/// map.bin: the analyzer dump. int32 width, int32 height, then per cell (row-major) uint16 land id,
/// int8 z, uint8 biome; then int32 static count and per static uint16 x, uint16 y, int8 z, uint16 id.
/// </summary>
public static class MapDump
{
    public static void Write(GenIR ir, string path)
    {
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write((int)ir.Width); bw.Write((int)ir.Height);
        int n = ir.Width * ir.Height;
        for (int i = 0; i < n; i++)
        {
            bw.Write(ir.LandId is null ? (ushort)0 : ir.LandId[i]);
            bw.Write(ir.Height_Z is null ? (sbyte)0 : ir.Height_Z[i]);
            bw.Write(ir.Biome is null ? (byte)0 : ir.Biome[i]);
        }
        var adds = ir.StaticOps.Where(o => o.Kind == StaticOpKind.Add).ToList();
        bw.Write(adds.Count);
        foreach (var o in adds) { bw.Write(o.X); bw.Write(o.Y); bw.Write(o.Z); bw.Write(o.Id); }
    }
}
