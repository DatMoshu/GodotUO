using System.Buffers.Binary;
using CentrED.MapGen.Paint;

namespace CentrED.MapGen.Tests;

public class PaintGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mapgen-paint-tests-" + Guid.NewGuid().ToString("N"));

    public PaintGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<string> GeneratorIds()
    {
        var data = new TheoryData<string>();
        foreach (var g in PaintGenerators.All) data.Add(g.Id);
        return data;
    }

    [Fact]
    public void Registry_HasUniqueIds_AndCoreGenerators()
    {
        var ids = PaintGenerators.All.Select(g => g.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var id in new[] { "fullworld", "archipelago", "island", "two-islands", "heightgradient", "colortest", "bigmountain" })
            Assert.NotNull(PaintGenerators.Find(id));
        Assert.Null(PaintGenerators.Find("no-such-generator"));
        Assert.All(PaintGenerators.All, g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Description));
            Assert.True(g.DefaultWidth > 0 && g.DefaultHeight > 0);
        });
    }

    [Theory]
    [MemberData(nameof(GeneratorIds))]
    public void Generate_WritesBothPngs_AtRequestedSize_WithOnlyPaletteColours(string id)
    {
        const int w = 96, h = 64;
        var gen = PaintGenerators.Find(id)!;
        string prefix = Path.Combine(_dir, id);
        gen.Generate(prefix, w, h, gen.DefaultSeed);

        Assert.True(File.Exists(PaintPngPair.TerrainPath(prefix)));
        Assert.True(File.Exists(PaintPngPair.AltitudePath(prefix)));
        Assert.Equal((w, h), PngSize(PaintPngPair.TerrainPath(prefix)));
        Assert.Equal((w, h), PngSize(PaintPngPair.AltitudePath(prefix)));

        var data = PaintPngPair.Read(prefix);
        Assert.Equal(w, data.Width);
        Assert.Equal(h, data.Height);
        Assert.NotNull(data.Altitude);

        var palette = PaintPngPair.Palette.Select(e => (e.R, e.G, e.B)).ToHashSet();
        for (int i = 0; i < w * h; i++)
        {
            var px = (data.TerrainRgb[i * 3], data.TerrainRgb[i * 3 + 1], data.TerrainRgb[i * 3 + 2]);
            Assert.True(palette.Contains(px), $"{id}: pixel {i % w},{i / w} = {px} is not a palette colour");
        }
    }

    [Theory]
    [MemberData(nameof(GeneratorIds))]
    public void Generate_IsDeterministic_ForSameSeed(string id)
    {
        var gen = PaintGenerators.Find(id)!;
        string a = Path.Combine(_dir, id + "-a"), b = Path.Combine(_dir, id + "-b");
        gen.Generate(a, 80, 72, 4242);
        gen.Generate(b, 80, 72, 4242);
        Assert.Equal(File.ReadAllBytes(PaintPngPair.TerrainPath(a)), File.ReadAllBytes(PaintPngPair.TerrainPath(b)));
        Assert.Equal(File.ReadAllBytes(PaintPngPair.AltitudePath(a)), File.ReadAllBytes(PaintPngPair.AltitudePath(b)));
    }

    [Fact]
    public void SeededGenerators_DifferBySeed()
    {
        foreach (var gen in PaintGenerators.All.Where(g => g.UsesSeed))
        {
            var r1 = gen.Render(128, 128, 1);
            var r2 = gen.Render(128, 128, 2);
            Assert.False(r1.Terrain.AsSpan().SequenceEqual(r2.Terrain) && r1.Altitude.AsSpan().SequenceEqual(r2.Altitude),
                $"{gen.Id}: seeds 1 and 2 produced identical output");
        }
    }

    [Fact]
    public void Generators_ProduceMoreThanOneBiome()
    {
        foreach (var gen in PaintGenerators.All.Where(g => g.Id != "flat-grass"))
        {
            var r = gen.Render(gen.DefaultWidth > 1024 ? 448 : 256, gen.DefaultHeight > 1024 ? 256 : 256, gen.DefaultSeed);
            Assert.True(r.Terrain.Distinct().Count() > 1, $"{gen.Id}: only one biome");
        }
    }

    [Fact]
    public void PaintPngPair_RoundTrip_IsExact()
    {
        const int w = 37, h = 23;
        var rgb = new byte[w * h * 3];
        var alt = new byte[w * h];
        var rng = new Random(7);
        rng.NextBytes(rgb);
        rng.NextBytes(alt);
        string prefix = Path.Combine(_dir, "sub", "roundtrip");

        PaintPngPair.Write(prefix, w, h, rgb, alt);
        var data = PaintPngPair.Read(prefix);

        Assert.Equal(w, data.Width);
        Assert.Equal(h, data.Height);
        Assert.Equal(rgb, data.TerrainRgb);
        Assert.Equal(alt, data.Altitude);

        // Writing what was read reproduces the files byte for byte.
        string again = Path.Combine(_dir, "sub", "roundtrip2");
        PaintPngPair.Write(again, w, h, data.TerrainRgb, data.Altitude!);
        Assert.Equal(File.ReadAllBytes(PaintPngPair.TerrainPath(prefix)), File.ReadAllBytes(PaintPngPair.TerrainPath(again)));
        Assert.Equal(File.ReadAllBytes(PaintPngPair.AltitudePath(prefix)), File.ReadAllBytes(PaintPngPair.AltitudePath(again)));
    }

    [Fact]
    public void PaintPngPair_Read_MissingAltitude_IsNull_MissingTerrain_Throws()
    {
        string prefix = Path.Combine(_dir, "partial");
        PaintPngPair.Write(prefix, 4, 4, new byte[48], new byte[16]);
        File.Delete(PaintPngPair.AltitudePath(prefix));
        Assert.Null(PaintPngPair.Read(prefix).Altitude);
        Assert.Throws<FileNotFoundException>(() => PaintPngPair.Read(Path.Combine(_dir, "absent")));
    }

    [Fact]
    public void Palette_Helpers_AgreeWithPalette()
    {
        var entries = PaintPngPair.Palette;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            Assert.Equal(i, PaintPngPair.IndexOf(e.Name));
            Assert.Equal((e.R, e.G, e.B), PaintPngPair.Rgb(e.Name));
            Assert.Equal(i, PaintPngPair.SnapToIndex(e.R, e.G, e.B));
        }
        Assert.Equal(-1, PaintPngPair.IndexOf("NotABiome"));
        Assert.Equal(-1, PaintPngPair.SnapToIndex(255, 0, 255));
        Assert.Throws<ArgumentException>(() => PaintPngPair.Rgb("NotABiome"));

        // Shade ↔ Z round-trips every Z in the default import range.
        for (int z = -10; z <= 80; z++)
            Assert.Equal(z, PaintPngPair.ShadeToZ(PaintPngPair.ZToShade(z, -10, 80), -10, 80));
    }

    /// <summary>Width/height from the PNG IHDR chunk.</summary>
    private static (int W, int H) PngSize(string path)
    {
        var head = new byte[24];
        using (var fs = File.OpenRead(path)) fs.ReadExactly(head);
        return (BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20)));
    }
}
