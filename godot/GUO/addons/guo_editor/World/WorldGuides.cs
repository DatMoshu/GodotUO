#if TOOLS
namespace GUO.Editor;

using System;
using Godot;

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
    public bool Blocks { get; set; }
    internal readonly System.Collections.Generic.HashSet<(int X, int Y)> BrushCells = new();
    internal int? PlaneZ;
    internal readonly System.Collections.Generic.List<(int X, int Y, int Z, Texture2D Texture)> Ghosts = new();
    internal readonly System.Collections.Generic.List<(int X, int Y, int Z, Texture2D Texture)> RoofGhosts = new();

    /// <summary>Cells outlined on the last draw, for the smoke check.</summary>
    public int CellsDrawn { get; private set; }

    /// <summary>A cell to outline as the pointer's target, or null.</summary>
    public (int X, int Y)? Hover { get; set; }

    /// <summary>A rectangle of cells (x0, y0, x1, y1) to outline: the Area tool's selection.</summary>
    public (int X0, int Y0, int X1, int Y1)? Area { get; set; }

    internal void Attach(WorldHost host) => _host = host;

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        CellsDrawn = 0;
        if (_host == null || !_host.IsBooted || _host.World?.Map == null)
        {
            return;
        }

        CellGeometry geo = CellGeometry.From(_host);
        if (geo == null)
        {
            return;
        }

        var map = _host.World.Map;
        TextureFilter = TextureFilterEnum.Nearest;
        _font ??= ThemeDB.FallbackFont;

        var gridColour = new Color(1, 1, 1, 0.4f);
        var blockColour = new Color(1f, 0.85f, 0.2f, 0.85f);
        var hoverColour = new Color(0.3f, 1f, 0.3f, 0.95f);

        {
            foreach (CellQuad q in geo.Cells())
            {
                int x = q.X, y = q.Y;
                Vector2 top = q.Top, right = q.Right, bottom = q.Bottom, left = q.Left;

                CellsDrawn++;
                if (BrushCells.Contains((x, y)))
                {
                    Vector2[] points = PlaneZ is int pz
                        ? new[] { geo.Project(x, y, pz), geo.Project(x + 1, y, pz), geo.Project(x + 1, y + 1, pz), geo.Project(x, y + 1, pz) }
                        : new[] { top, right, bottom, left };
                    DrawColoredPolygon(points, new Color(0.15f, 0.9f, 0.8f, 0.22f));
                    DrawPolyline(new[] { points[0], points[1], points[2], points[3], points[0] }, new Color(0.2f, 0.95f, 0.85f, 0.8f));
                }
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

                if (Area is { } a && x >= a.X0 && x <= a.X1 && y >= a.Y0 && y <= a.Y1)
                {
                    var areaColour = new Color(0.3f, 0.9f, 1f, 0.9f);
                    if (x == a.X0) DrawLine(top, left, areaColour, 2f);
                    if (x == a.X1) DrawLine(right, bottom, areaColour, 2f);
                    if (y == a.Y0) DrawLine(top, right, areaColour, 2f);
                    if (y == a.Y1) DrawLine(left, bottom, areaColour, 2f);
                }

                if (Hover is { } hv && hv.X == x && hv.Y == y)
                {
                    DrawPolyline(new[] { top, right, bottom, left, top }, hoverColour, 2f);
                }
            }
        }
        foreach (var ghost in RoofGhosts)
        {
            Vector2 foot = geo.Project(ghost.X + 0.5f, ghost.Y + 0.5f, ghost.Z);
            Vector2 size = ghost.Texture.GetSize() / geo.Zoom;
            DrawTextureRect(ghost.Texture, new Rect2(foot - new Vector2(size.X / 2, size.Y), size), false, new Color(1f, 1f, 1f, 0.18f));
        }
        foreach (var ghost in Ghosts)
        {
            Vector2 foot = geo.Project(ghost.X + 0.5f, ghost.Y + 0.5f, ghost.Z);
            Vector2 size = ghost.Texture.GetSize() / geo.Zoom;
            DrawTextureRect(ghost.Texture, new Rect2(foot - new Vector2(size.X / 2, size.Y), size), false, new Color(0.4f, 1f, 0.9f, 0.45f));
        }
    }
}
#endif
