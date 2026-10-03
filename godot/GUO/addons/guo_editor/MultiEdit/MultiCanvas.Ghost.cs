#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>
/// The canvas's preview layer and the placement tools that feed the generators (ADR-0031, phase 2). The ghost is a
/// list of parts drawn over the multi, translucent, in the client's order; it either stays where its parts say
/// (a generator preview) or follows the pointer until a click places it (paste, a stamp). The wall, roof and stair
/// tools only collect where the generator should work (a path, a box, an anchor and a direction); the view hands
/// that to the Generate panel, which does the generating.
/// </summary>
public partial class MultiCanvas
{
    private List<MultiPart> _ghost = new();
    private List<MultiPart> _ghostSorted = new();
    private bool _ghostDirty;

    /// <summary>Where the ghost sits relative to its parts' own coordinates.</summary>
    public (int X, int Y, int Z) GhostOffset { get; set; }

    /// <summary>When true the ghost's anchor (0,0) follows the pointer and a click places it.</summary>
    public bool GhostFollowsMouse { get; private set; }

    public int GhostCount => _ghost.Count;

    public IReadOnlyList<MultiPart> GhostParts => _ghost;

    /// <summary>Raised when a following ghost is clicked into place: its offset.</summary>
    public event Action<int, int, int> GhostPlaced;

    /// <summary>Raised when Escape drops a following ghost.</summary>
    public event Action GhostCancelled;

    // The generator tools' state.
    public List<(int X, int Y)> WallPath { get; } = new();
    public bool WallClosed { get; set; }
    public (int X0, int Y0, int X1, int Y1)? RoofBox { get; set; }
    public (int X, int Y, string Rise)? Flight { get; set; }

    /// <summary>Raised when a tool changed the path, box or flight (the view refreshes the generator's context).</summary>
    public event Action ToolContextChanged;

    /// <summary>Raised for "wall-finish" (Enter, or the path closed on its first point).</summary>
    public event Action<string> ToolAction;

    /// <summary>The view's menu commands that have a key: copy, cut, paste.</summary>
    public event Action<string> Command;

    public void SetGhost(IEnumerable<MultiPart> parts, bool follow = false)
    {
        _ghost = parts.ToList();
        GhostFollowsMouse = follow && _ghost.Count > 0;
        GhostOffset = (0, 0, 0);
        _ghostDirty = true;
        QueueRedraw();
    }

    public void ClearGhost()
    {
        if (_ghost.Count == 0 && !GhostFollowsMouse)
        {
            return;
        }

        _ghost = new List<MultiPart>();
        _ghostSorted = new List<MultiPart>();
        GhostFollowsMouse = false;
        QueueRedraw();
    }

    /// <summary>Forgets the wall path, the roof box and the flight.</summary>
    public void ClearToolState()
    {
        WallPath.Clear();
        WallClosed = false;
        RoofBox = null;
        Flight = null;
        ToolContextChanged?.Invoke();
        QueueRedraw();
    }

    private void DrawGhost()
    {
        if (_ghost.Count == 0)
        {
            return;
        }

        if (_ghostDirty)
        {
            _ghostSorted = MultiPanel.ClientOrderOf(_data, _ghost, p => (p.Id, p.X, p.Y, p.Z, true));
            _ghostDirty = false;
        }

        var (ox, oy, oz) = GhostOffset;
        foreach (MultiPart p in _ghostSorted)
        {
            MultiPart q = p;
            q.X = (short)(q.X + ox);
            q.Y = (short)(q.Y + oy);
            q.Z = (short)(q.Z + oz);
            var (tex, img) = ArtOf(q);
            Rect2 r = PartRect(q, img);
            var tint = new Color(0.75f, 0.95f, 1f, 0.62f);
            if (tex != null)
            {
                DrawTextureRect(tex, r, false, tint);
            }
            else
            {
                Vector2 c = P(q.X, q.Y, q.Z);
                DrawPolyline(new[] { c + new Vector2(0, -8), c + new Vector2(8, 0), c + new Vector2(0, 8), c + new Vector2(-8, 0), c + new Vector2(0, -8) }, tint, 1f / Zoom);
            }
        }
    }

    // --- the generator tools --------------------------------------------------------------------------

    private static bool IsPlacementTool(MultiTool t) => t is MultiTool.WallRun or MultiTool.Roof or MultiTool.Stairs;

    private void DrawDiamond((int X, int Y) cell, int z, Color c, float width = 1.5f)
    {
        Vector2 p = P(cell.X, cell.Y, z);
        DrawPolyline(new[] { p + new Vector2(0, -22), p + new Vector2(22, 0), p + new Vector2(0, 22), p + new Vector2(-22, 0), p + new Vector2(0, -22) }, c, width / Zoom);
    }

    /// <summary>Draws the tool state: the wall path, the roof box, the flight and its arrow.</summary>
    private void DrawGeneratorTools()
    {
        var path = new Color(1f, 0.8f, 0.2f, 0.95f);
        if (WallPath.Count > 0)
        {
            foreach (var c in WallPath)
            {
                DrawDiamond(c, EditZ, path);
            }

            for (int i = 1; i < WallPath.Count; i++)
            {
                DrawLine(P(WallPath[i - 1].X, WallPath[i - 1].Y, EditZ), P(WallPath[i].X, WallPath[i].Y, EditZ), path, 2f / Zoom);
            }

            if (Tool == MultiTool.WallRun && _hover is { } h)
            {
                DrawLine(P(WallPath[^1].X, WallPath[^1].Y, EditZ), P(h.X, h.Y, EditZ), new Color(1f, 0.8f, 0.2f, 0.45f), 1.5f / Zoom);
            }
        }

        if (RoofBox is { } b)
        {
            foreach (var c in RectCells((b.X0, b.Y0), (b.X1, b.Y1), true))
            {
                DrawDiamond(c, EditZ, new Color(0.9f, 0.5f, 0.3f, 0.9f));
            }
        }

        if (Tool == MultiTool.Roof && _drawingNow && _dragStart is { } a && _hover is { } hv)
        {
            foreach (var c in RectCells(a, hv, true))
            {
                DrawDiamond(c, EditZ, new Color(0.9f, 0.5f, 0.3f, 0.6f));
            }
        }

        if (Flight is { } f)
        {
            DrawFlight(f.X, f.Y, f.Rise, new Color(0.5f, 1f, 0.6f, 0.95f));
        }
        else if (Tool == MultiTool.Stairs && _drawingNow && _dragStart is { } s && _hover is { } hh)
        {
            DrawFlight(s.X, s.Y, RiseOf(s, hh), new Color(0.5f, 1f, 0.6f, 0.6f));
        }
    }

    private void DrawFlight(int x, int y, string rise, Color c)
    {
        (int dx, int dy) = RiseDelta(rise);
        DrawDiamond((x, y), EditZ, c);
        Vector2 a = P(x, y, EditZ), b = P(x + dx * 2, y + dy * 2, EditZ);
        DrawLine(a, b, c, 2.5f / Zoom);
        Vector2 dir = (b - a).Normalized();
        Vector2 side = new(-dir.Y, dir.X);
        DrawColoredPolygon(new[] { b + dir * 10, b - dir * 4 + side * 8, b - dir * 4 - side * 8 }, c);
    }

    public static (int Dx, int Dy) RiseDelta(string rise) => rise switch
    {
        "N" => (0, -1),
        "S" => (0, 1),
        "E" => (1, 0),
        _ => (-1, 0),
    };

    /// <summary>The direction a stair rises when dragged from a cell to another: the dominant axis (x east, y south).</summary>
    public static string RiseOf((int X, int Y) from, (int X, int Y) to)
    {
        int dx = to.X - from.X, dy = to.Y - from.Y;
        if (dx == 0 && dy == 0)
        {
            return "S";
        }

        return Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? "E" : "W") : (dy > 0 ? "S" : "N");
    }

    /// <summary>One click or drag of a placement tool. Returns whether it took the event.</summary>
    private bool PlacementPress(InputEventMouseButton lb)
    {
        _hover = CellAt(lb.Position, EditZ);
        switch (Tool)
        {
            case MultiTool.WallRun:
                if (WallPath.Count >= 3 && WallPath[0] == _hover.Value)
                {
                    WallClosed = true;
                    ToolContextChanged?.Invoke();
                    ToolAction?.Invoke("wall-finish");
                }
                else if (WallPath.Count == 0 || WallPath[^1] != _hover.Value)
                {
                    WallPath.Add(_hover.Value);
                    WallClosed = false;
                    ToolContextChanged?.Invoke();
                }

                return true;
            case MultiTool.Roof or MultiTool.Stairs:
                _drawingNow = true;
                _dragStart = _hover;
                return true;
        }

        return false;
    }

    private void PlacementRelease(InputEventMouseButton lb)
    {
        _hover = CellAt(lb.Position, EditZ);
        if (_dragStart is not { } a)
        {
            return;
        }

        switch (Tool)
        {
            case MultiTool.Roof:
                RoofBox = (Math.Min(a.X, _hover.Value.X), Math.Min(a.Y, _hover.Value.Y), Math.Max(a.X, _hover.Value.X), Math.Max(a.Y, _hover.Value.Y));
                ToolContextChanged?.Invoke();
                break;
            case MultiTool.Stairs:
                Flight = (a.X, a.Y, RiseOf(a, _hover.Value));
                ToolContextChanged?.Invoke();
                break;
        }
    }

    private bool GhostKey(InputEventKey k)
    {
        if (k.Keycode == Key.Escape && (GhostFollowsMouse || WallPath.Count > 0 || RoofBox != null || Flight != null))
        {
            bool wasFollow = GhostFollowsMouse;
            ClearGhost();
            ClearToolState();
            if (wasFollow)
            {
                GhostCancelled?.Invoke();
            }

            return true;
        }

        if (k.Keycode is Key.Enter or Key.KpEnter && Tool == MultiTool.WallRun && WallPath.Count >= 2)
        {
            ToolAction?.Invoke("wall-finish");
            return true;
        }

        return false;
    }

    /// <summary>Raises <see cref="Command"/>; the view does the copy, cut and paste.</summary>
    private void RaiseCommand(string name) => Command?.Invoke(name);

    /// <summary>Test seam: places a following ghost where a click at the cell would.</summary>
    public void PlaceGhostAt(int x, int y)
    {
        if (GhostFollowsMouse)
        {
            GhostOffset = (x, y, GhostOffset.Z);
            GhostPlaced?.Invoke(x, y, GhostOffset.Z);
        }
    }

    private bool GhostPress(InputEventMouseButton lb)
    {
        if (!GhostFollowsMouse)
        {
            return false;
        }

        (int x, int y) = CellAt(lb.Position, EditZ);
        GhostOffset = (x, y, GhostOffset.Z);
        GhostPlaced?.Invoke(x, y, GhostOffset.Z);
        return true;
    }

    private void GhostMove(Vector2 at)
    {
        if (GhostFollowsMouse)
        {
            (int x, int y) = CellAt(at, EditZ);
            GhostOffset = (x, y, GhostOffset.Z);
        }
    }
}
#endif
