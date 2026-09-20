namespace UOPort.Compat;

using System;

/// <summary>
/// Integer 2D point, API-compatible with the FNA/XNA struct of the same name.
/// </summary>
/// <remarks>
/// Ported ClassicUO code uses this constantly for tile coordinates, screen
/// offsets and sprite sizes. Godot's <see cref="Godot.Vector2I"/> is the same
/// shape but differs in member names and available operators, so a thin
/// dedicated struct keeps ported call sites compiling unchanged.
/// Conversion to and from the engine type is explicit and deliberate — see
/// the README in this folder.
/// </remarks>
public struct Point : IEquatable<Point>
{
    public int X;
    public int Y;

    public Point(int x, int y)
    {
        X = x;
        Y = y;
    }

    /// <summary>Both components set to <paramref name="value"/>.</summary>
    public Point(int value)
    {
        X = value;
        Y = value;
    }

    public static Point Zero => new Point(0, 0);

    public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);

    public static Point operator -(Point a, Point b) => new Point(a.X - b.X, a.Y - b.Y);

    public static Point operator *(Point a, Point b) => new Point(a.X * b.X, a.Y * b.Y);

    /// <summary>
    /// Component-wise integer division. Truncates toward zero, matching
    /// <c>int</c> division in the original code rather than flooring.
    /// </summary>
    public static Point operator /(Point a, Point b) => new Point(a.X / b.X, a.Y / b.Y);

    public static bool operator ==(Point a, Point b) => a.X == b.X && a.Y == b.Y;

    public static bool operator !=(Point a, Point b) => !(a == b);

    public bool Equals(Point other) => this == other;

    public override bool Equals(object obj) => obj is Point other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public override string ToString() => $"{{X:{X} Y:{Y}}}";

    // --- engine boundary -----------------------------------------------
    // Explicit on purpose: these calls mark exactly where ported logic hands
    // data to Godot, which is what a reviewer needs to be able to see.

    public readonly Godot.Vector2I ToGodot() => new Godot.Vector2I(X, Y);

    public readonly Godot.Vector2 ToGodotF() => new Godot.Vector2(X, Y);

    public static Point FromGodot(Godot.Vector2I v) => new Point(v.X, v.Y);
}
