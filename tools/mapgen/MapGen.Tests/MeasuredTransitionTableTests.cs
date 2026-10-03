using CentrED.MapGen.Data;

namespace CentrED.MapGen.Tests;

public class MeasuredTransitionTableTests
{
    private static MeasuredTransitionTable Read(string json, int support = 30)
    {
        string path = Path.GetTempFileName();
        try { File.WriteAllText(path, json); return MeasuredTransitionTable.Load(path, support); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OverlayPreservesSharedBrushAndUnsupportedMasks()
    {
        var atlas = Read("""{"schema":1,"pairs":{"Grassland>Forest":{"rules":{"1":[[204,100],[205,50],[206,1]],"2":[[207,4]],"3":[]}}}}""");
        var fallback = new ushort[]?[256]; fallback[1] = new ushort[] { 3 }; fallback[2] = new ushort[] { 4 };
        var overlay = atlas.Overlay("Grassland", "Forest", fallback)!;
        Assert.NotSame(fallback, overlay);
        Assert.Equal(new ushort[] { 3 }, fallback[1]);
        Assert.Same(fallback[2], overlay[2]);
        Assert.Equal(32, overlay[1]!.Count(t => t == 204));
        Assert.Equal(16, overlay[1]!.Count(t => t == 205));
        Assert.DoesNotContain((ushort)206, overlay[1]!);
        Assert.Equal(1, atlas.MaskCount);
        Assert.Same(fallback, atlas.Overlay("Forest", "Beach", fallback));
    }

    [Theory]
    [InlineData(16384, 10)]
    [InlineData(204, -1)]
    public void InvalidEvidenceFailsExplicitly(int tile, int count)
    {
        string json = "{\"schema\":1,\"pairs\":{\"Grassland>Forest\":{\"rules\":{\"1\":[[" + tile + "," + count + "]]}}}}";
        Assert.Throws<InvalidDataException>(() => Read(json));
    }
}
