#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>
/// Where a static on a block is on the World tab's screen: the aim point a
/// person would click, shared by the smoke check and the tour so neither
/// carries its own copy of the camera maths.
/// </summary>
internal static class WorldAim
{
    /// <summary>The viewport position of a drawn object's art, lower middle, as <see cref="ScreenOf"/> does for a static.</summary>
    public static Vector2I ScreenOfObject(WorldView world, EditorData data, GUO.Game.GameObjects.GameObject o)
    {
        Image art = data.ArtImage(EditorData.LandCount + o.Graphic);
        int h = art?.GetHeight() ?? 44;
        var at = new GUO.Compat.Point(o.RealScreenPosition.X + 22, o.RealScreenPosition.Y + 44 - h / 3);
        var screen = world.Host.Scene.Camera.WorldToScreen(at);
        return new Vector2I(screen.X, screen.Y);
    }

    /// <summary>
    /// The viewport position of a static's art, lower middle (as a user aims at
    /// it), projected with the game's own camera. Null when block
    /// <paramref name="bx"/>,<paramref name="by"/> holds no such static.
    /// </summary>
    public static Vector2I? ScreenOf(WorldView world, EditorData data, int bx, int by, ushort id)
    {
        var chunk = world.Host.World.Map.GetChunk2(bx, by, load: true);
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                for (var o = chunk?.GetHeadObject(x, y); o != null; o = o.TNext)
                {
                    if (o is GUO.Game.GameObjects.Static && o.Graphic == id)
                    {
                        Image art = data.ArtImage(EditorData.LandCount + id);
                        int h = art?.GetHeight() ?? 44;
                        var at = new GUO.Compat.Point(o.RealScreenPosition.X + 22, o.RealScreenPosition.Y + 44 - h / 3);
                        var screen = world.Host.Scene.Camera.WorldToScreen(at);
                        return new Vector2I(screen.X, screen.Y);
                    }
                }
            }
        }

        return null;
    }
}
#endif
