#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;
using CPoint = GUO.Compat.Point;

/// <summary>One map cell's diamond on the view, corners as the game's own maths puts them.</summary>
internal readonly struct CellQuad
{
    public readonly int X, Y;
    public readonly Vector2 Top, Right, Bottom, Left;

    public CellQuad(int x, int y, Vector2 top, Vector2 right, Vector2 bottom, Vector2 left)
    {
        X = x;
        Y = y;
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    public Vector2 Centre => (Top + Bottom) / 2;
}

/// <summary>
/// Where cells are on the World view, shared by the guides and the render
/// modes (ADR-0027) so every overlay sits on the tile it describes. A cell's
/// diamond is where <c>GameObject.UpdateRealScreenPosition</c> puts it:
/// <c>((x - y) * 22 - offX - 22, (x + y) * 22 - z * 4 - offY - 22)</c>, with
/// <c>offX, offY</c> as <c>GameScene.GetViewPort</c> computes them from the
/// player and the camera bounds; world pixels become viewport pixels through
/// <c>Camera.WorldToScreen</c>, so zoom is the game's too. Each of the four
/// corners takes the land height at that corner, so the diamonds follow the
/// terrain as land is drawn stretched.
/// </summary>
internal sealed class CellGeometry
{
    private readonly GUO.Renderer.Camera _camera;
    private readonly int _offX, _offY;
    private readonly Func<int, int, sbyte> _z;

    public readonly int Width, Height, CentreX, CentreY, CentreZ, Range, Facet;
    public readonly float Zoom;

    private CellGeometry(WorldHost host, WorldData data, int maxRange)
    {
        _camera = host.Scene.Camera;
        Width = _camera.Bounds.Width;
        Height = _camera.Bounds.Height;
        Zoom = _camera.Zoom;
        GUO.Game.World world = host.World;
        CentreX = world.Player.X;
        CentreY = world.Player.Y;
        CentreZ = world.Player.Z;
        Facet = world.MapIndex;
        _offX = (CentreX - CentreY) * 22 - (Width >> 1);
        _offY = (CentreX + CentreY) * 22 - ((Height >> 1) + (CentreZ << 2));

        // Enough cells to cover the view at this zoom.
        Range = Math.Min(maxRange, (int)(Math.Max(Width, Height) / 44f * Zoom) + 2);
        GUO.Game.Map.Map map = world.Map;
        _z = data != null ? data.LandZ : map.GetTileZ;
    }

    /// <summary>The geometry for the view as it is now, or null when the world is not up.</summary>
    public static CellGeometry From(WorldHost host, WorldData data = null, int maxRange = int.MaxValue)
    {
        if (host == null || !host.IsBooted || host.World?.Map == null)
        {
            return null;
        }

        var b = host.Scene.Camera.Bounds;
        return b.Width <= 0 || b.Height <= 0 ? null : new CellGeometry(host, data, maxRange);
    }

    /// <summary>The land height this geometry draws a corner at.</summary>
    public sbyte ZAt(int x, int y) => _z(x, y);

    /// <summary>A map point (cell units, fractions allowed) at height <paramref name="z"/> on the view.</summary>
    public Vector2 Project(float x, float y, float z)
    {
        var world = new CPoint((int)MathF.Round((x - y) * 22f) - _offX, (int)MathF.Round((x + y) * 22f - z * 4f) - _offY - 22);
        CPoint s = _camera.WorldToScreen(world);
        return new Vector2(s.X, s.Y);
    }

    /// <summary>The diamond's top corner for cell (x, y): the point (x, y) at the land's height there.</summary>
    public Vector2 Corner(int x, int y) => Project(x, y, _z(x, y));

    /// <summary>The cell under a viewport pixel assuming flat ground at <paramref name="z"/> (the inverse of <see cref="Project"/>).</summary>
    public (float X, float Y) CellAt(Vector2 screen, float z)
    {
        CPoint w = _camera.ScreenToWorld(new CPoint((int)screen.X, (int)screen.Y));
        float a = (w.X + _offX) / 22f;                 // x - y
        float b = (w.Y + _offY + 22 + z * 4f) / 22f;   // x + y
        return ((a + b) / 2f, (b - a) / 2f);
    }

    /// <summary>Whether a diamond whose top corner is here can be on the view at all.</summary>
    public bool OnView(Vector2 top) => !(top.X < -64 || top.X > Width + 64 || top.Y < -64 || top.Y > Height + 128);

    /// <summary>The cells in range whose diamonds are on the view, corners shared between neighbours.</summary>
    public IEnumerable<CellQuad> Cells()
    {
        int x0 = CentreX - Range, y0 = CentreY - Range, n = 2 * Range + 2;
        var grid = new Vector2[n * n];
        for (int gx = 0; gx < n; gx++)
        {
            for (int gy = 0; gy < n; gy++)
            {
                int x = x0 + gx, y = y0 + gy;
                grid[gx * n + gy] = x < 0 || y < 0 ? new Vector2(-9999, -9999) : Corner(x, y);
            }
        }

        for (int gx = 0; gx < n - 1; gx++)
        {
            for (int gy = 0; gy < n - 1; gy++)
            {
                int x = x0 + gx, y = y0 + gy;
                if (x < 0 || y < 0)
                {
                    continue;
                }

                Vector2 top = grid[gx * n + gy];
                if (!OnView(top))
                {
                    continue;
                }

                yield return new CellQuad(x, y, top, grid[(gx + 1) * n + gy], grid[(gx + 1) * n + gy + 1], grid[gx * n + gy + 1]);
            }
        }
    }
}
#endif
