#if TOOLS
namespace GUO.Editor;

using System;
using Godot;
using GUO.Game;
using CPoint = GUO.Compat.Point;

/// <summary>
/// The editor's own guides over the world view: the cell grid, land
/// altitude numbers and map block boundaries. Drawn above the game's frame
/// by the editor, never by the renderer (plan §4.4), so the game's output is
/// untouched underneath.
/// </summary>
/// <remarks>
/// Positions come from the game's own maths so a guide sits exactly on the
/// tile it describes. A cell's diamond is where <c>GameObject.UpdateRealScreenPosition</c>
/// puts it: <c>((x - y) * 22 - offX - 22, (x + y) * 22 - z * 4 - offY - 22)</c>,
/// with <c>offX, offY</c> as <c>GameScene.GetViewPort</c> computes them from
/// the player and the camera bounds. Each of the four corners takes the land
/// height at that corner, so the grid follows the terrain as land is drawn
/// stretched. World pixels become viewport pixels through
/// <c>Camera.WorldToScreen</c>, so zoom is the game's too.
/// </remarks>
[Tool]
public partial class WorldGuides : Node2D
{
    private WorldHost _host;
    private Font _font;

    public bool Grid { get; set; }
    public bool Altitude { get; set; }
    public bool Blocks { get; set; } = true;

    /// <summary>Cells outlined on the last draw, for the smoke check.</summary>
    public int CellsDrawn { get; private set; }

    /// <summary>A cell to outline as the pointer's target, or null.</summary>
    public (int X, int Y)? Hover { get; set; }

    internal void Attach(WorldHost host) => _host = host;

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        CellsDrawn = 0;
        if (_host == null || !_host.IsBooted || _host.World?.Map == null)
        {
            return;
        }

        var camera = _host.Scene.Camera;
        int w = camera.Bounds.Width, h = camera.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        World world = _host.World;
        int px = world.Player.X, py = world.Player.Y, pz = world.Player.Z;
        int offX = (px - py) * 22 - (w >> 1);
        int offY = (px + py) * 22 - ((h >> 1) + (pz << 2));

        // Enough cells to cover the view at this zoom.
        int range = (int)(Math.Max(w, h) / 44f * camera.Zoom) + 2;
        var map = world.Map;
        _font ??= ThemeDB.FallbackFont;

        var gridColour = new Color(1, 1, 1, 0.4f);
        var blockColour = new Color(1f, 0.85f, 0.2f, 0.85f);
        var hoverColour = new Color(0.3f, 1f, 0.3f, 0.95f);

        Vector2 Corner(int x, int y)
        {
            sbyte z = map.GetTileZ(x, y);
            var world = new CPoint((x - y) * 22 - offX, (x + y) * 22 - (z << 2) - offY - 22);
            CPoint s = camera.WorldToScreen(world);
            return new Vector2(s.X, s.Y);
        }

        for (int x = px - range; x <= px + range; x++)
        {
            for (int y = py - range; y <= py + range; y++)
            {
                if (x < 0 || y < 0)
                {
                    continue;
                }

                // The diamond's corners: top (x,y), right (x+1,y),
                // bottom (x+1,y+1), left (x,y+1).
                Vector2 top = Corner(x, y), right = Corner(x + 1, y), bottom = Corner(x + 1, y + 1), left = Corner(x, y + 1);
                if (top.X < -64 || top.X > w + 64 || top.Y < -64 || top.Y > h + 128)
                {
                    continue;
                }

                CellsDrawn++;
                if (Grid)
                {
                    DrawLine(top, right, gridColour);
                    DrawLine(top, left, gridColour);
                }

                if (Blocks)
                {
                    // A block edge is the line where x or y crosses a multiple of 8.
                    if ((x & 7) == 0)
                    {
                        DrawLine(top, left, blockColour, 2f);
                    }

                    if ((y & 7) == 0)
                    {
                        DrawLine(top, right, blockColour, 2f);
                    }
                }

                if (Altitude)
                {
                    sbyte z = map.GetTileZ(x, y);
                    Vector2 centre = (top + bottom) / 2;
                    string text = z.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    DrawString(_font, centre + new Vector2(-6, 4), text, HorizontalAlignment.Left, -1, 10, new Color(1, 1, 1, 0.9f));
                }

                if (Hover is { } hv && hv.X == x && hv.Y == y)
                {
                    DrawPolyline(new[] { top, right, bottom, left, top }, hoverColour, 2f);
                }
            }
        }
    }
}
#endif
