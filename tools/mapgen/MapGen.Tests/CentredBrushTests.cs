using CentrED.MapGen.Data;

namespace CentrED.MapGen.Tests;

public class CentredBrushTests
{
    [Fact]
    public void ProfileBrushValueKeepsDirectionalEdges()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"Pier":{"Tiles":[33],"To":{"Bay":[{"TileID":53,"Direction":2}]}},"Bay":{"Tiles":[4],"To":{}}}""");
            var table = LandBrushTable.LoadOrEmpty(path);
            Assert.Equal("centred", table.Source);
            Assert.Equal((ushort)53, table.PickTransitionTile("Pier", "Bay", EdgeShapes.NE, 0u));
            Assert.Equal((ushort)0, table.PickTransitionTile("Pier", "Bay", EdgeShapes.SW, 0u));
            File.WriteAllText(path, """{"land_brushes":{"Pier":{"Tiles":[33],"To":{}}}}""");
            Assert.False(LandBrushTable.LoadOrEmpty(path).IsLoaded);
        }
        finally { File.Delete(path); }
    }
}
