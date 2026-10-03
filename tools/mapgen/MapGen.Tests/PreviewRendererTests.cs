using CentrED.MapGen.IR;
using CentrED.MapGen.Preview;
using CentrED.Network;
namespace CentrED.MapGen.Tests;
public class PreviewRendererTests
{
    private static GenIR Ir(ushort w, ushort h) => new(w, h, new RectU16(0, 0, (ushort)(w-1), (ushort)(h-1)), 1)
    { LandId = new ushort[w*h], Height_Z = new sbyte[w*h] };
    [Fact] public void PaletteDecodesLittleEndianRgb555()
    {
        var bytes = new byte[0x8000]; bytes[0] = 0; bytes[1] = 0x7C;
        Assert.Equal(0xFF0000F8u, RadarPalette.Decode(bytes)[0]);
        Assert.Throws<InvalidDataException>(() => RadarPalette.Decode(new byte[3]));
    }
    [Fact] public void DownsampleAveragesEveryTileAndPreservesPartialBlocks()
    {
        var ir = Ir(3, 2); ir.LandId = [0, 1, 1, 1, 0, 1];
        var result = PreviewRenderer.Render(ir, PreviewLayer.LandIdRadar, 2, [0xFF000000, 0xFFFFFFFF]);
        Assert.Equal(2, result.Width); Assert.Equal(1, result.Height);
        Assert.Equal(new uint[] {0xFF7F7F7F, 0xFFFFFFFF}, result.Pixels);
    }
    [Fact] public void RadarUsesHighestVisibleStaticIncludingShallowWater()
    {
        var ir = Ir(1, 1); ir.Height_Z![0] = -15;
        ir.StaticOps.Add(new(StaticOpKind.Add, 0, 0, -5, 1, 0));
        ir.StaticOps.Add(new(StaticOpKind.Add, 0, 0, -10, 2, 0));
        var palette = new uint[0x4003]; palette[0x4001] = 0xFFAA0000;
        Assert.Equal(0xFFAA0000u, PreviewRenderer.Render(ir, PreviewLayer.LandIdRadar, 1, palette).Pixels[0]);
        Assert.Equal(-15, ir.Height_Z[0]);
    }
    [Theory] [InlineData(0x22C)] [InlineData(0x3DEB)] [InlineData(0xAC)]
    [InlineData(0xC4)] [InlineData(0x11A)] [InlineData(0x4C)] [InlineData(0x33)]
    public void KnownTerrainHasFallbackColor(ushort id)
    {
        var ir = Ir(1, 1); ir.LandId![0] = id;
        Assert.NotEqual(0xFFA05A8Cu, PreviewRenderer.Render(ir, PreviewLayer.LandId, 1).Pixels[0]);
    }
    [Fact] public void InvalidPreviewSizeIsRejected()
    { Assert.Throws<ArgumentOutOfRangeException>(() => PreviewRenderer.Render(Ir(1,1), PreviewLayer.Height, 0)); }
}
