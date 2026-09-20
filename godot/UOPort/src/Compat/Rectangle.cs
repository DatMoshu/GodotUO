namespace UOPort.Compat;

using System;

/// <summary>
/// Integer axis-aligned rectangle, API-compatible with the FNA/XNA struct.
/// </summary>
/// <remarks>
/// Used throughout the ported code for sprite source rects, gump bounds and
/// hit testing. The containment and intersection semantics below are
/// half-open — inclusive of the top-left edge, exclusive of the bottom-right
/// — because that is what the original code assumes. Changing it produces
/// one-pixel seams between tiles that are very hard to trace.
/// </remarks>
public struct Rectangle : IEquatable<Rectangle>
{
    public int X;
    public int Y;
    public int Width;
    public int Height;

    public Rectangle(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public static Rectangle Empty => new Rectangle(0, 0, 0, 0);

    public readonly int Left => X;

    public readonly int Top => Y;

    /// <summary>One past the last contained column.</summary>
    public readonly int Right => X + Width;

    /// <summary>One past the last contained row.</summary>
    public readonly int Bottom => Y + Height;

    public readonly bool IsEmpty => Width == 0 && Height == 0 && X == 0 && Y == 0;

    public Point Location
    {
        readonly get => new Point(X, Y);
        set
        {
            X = value.X;
            Y = value.Y;
        }
    }

    public Point Size
    {
        readonly get => new Point(Width, Height);
        set
        {
            Width = value.X;
            Height = value.Y;
        }
    }

    /// <summary>Centre point, truncated toward zero as in the original.</summary>
    public readonly Point Center => new Point(X + (Width / 2), Y + (Height / 2));

    public readonly bool Contains(int x, int y) =>
        x >= X && x < Right && y >= Y && y < Bottom;

    public readonly bool Contains(Point point) => Contains(point.X, point.Y);

    public readonly bool Contains(Rectangle other) =>
        other.X >= X && other.Right <= Right && other.Y >= Y && other.Bottom <= Bottom;

    public readonly bool Intersects(Rectangle other) =>
        other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    /// <summary>
    /// The overlapping region of two rectangles, or <see cref="Empty"/> when
    /// they do not overlap.
    /// </summary>
    public static Rectangle Intersect(Rectangle a, Rectangle b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.Right, b.Right);
        int bottom = Math.Min(a.Bottom, b.Bottom);

        if (right <= left || bottom <= top)
        {
            return Empty;
        }

        return new Rectangle(left, top, right - left, bottom - top);
    }

    /// <summary>Smallest rectangle containing both inputs.</summary>
    public static Rectangle Union(Rectangle a, Rectangle b)
    {
        int left = Math.Min(a.X, b.X);
        int top = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.Right, b.Right);
        int bottom = Math.Max(a.Bottom, b.Bottom);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    /// <summary>Grows the rectangle on all four sides, in place.</summary>
    public void Inflate(int horizontal, int vertical)
    {
        X -= horizontal;
        Y -= vertical;
        Width += horizontal * 2;
        Height += vertical * 2;
    }

    /// <summary>Moves the rectangle, in place.</summary>
    public void Offset(int dx, int dy)
    {
        X += dx;
        Y += dy;
    }

    public void Offset(Point amount) => Offset(amount.X, amount.Y);

    public static bool operator ==(Rectangle a, Rectangle b) =>
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

    public static bool operator !=(Rectangle a, Rectangle b) => !(a == b);

    public bool Equals(Rectangle other) => this == other;

    public override bool Equals(object obj) => obj is Rectangle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public override string ToString() =>
        $"{{X:{X} Y:{Y} Width:{Width} Height:{Height}}}";

    // --- engine boundary -----------------------------------------------

    public readonly Godot.Rect2I ToGodot() => new Godot.Rect2I(X, Y, Width, Height);

    public readonly Godot.Rect2 ToGodotF() => new Godot.Rect2(X, Y, Width, Height);

    public static Rectangle FromGodot(Godot.Rect2I r) =>
        new Rectangle(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y);
}
