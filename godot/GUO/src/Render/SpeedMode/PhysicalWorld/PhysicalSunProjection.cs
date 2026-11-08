// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: native-coordinate structural sunlight, independent of presentation.
using System;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;
using N4 = System.Numerics.Vector4;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

/// <summary>
/// Orthographic sunlight in tile/tile/native-UO-Z coordinates. Rows consume the
/// same origin-relative XYZ as the native surface buffer. No camera matrix or
/// roof-visibility flag participates in the physical projection.
/// </summary>
internal readonly record struct PhysicalSunProjection(
    N4 U, N4 V, N4 Depth, N2 Origin, N3 SourceToSun,
    float ReceiverMinZ, float CasterCeiling, float DepthBias, float Strength,
    int Resolution)
{
    // Native isometric tile axes project to 22*sqrt(2) pixels; Z projects to
    // four pixels. This metric also preserves the existing sun stamp length:
    // a vertical H casts 4*H*Length screen pixels along the same heading.
    internal const float ZScale = 0.12856487f; // 4 / (22 * sqrt(2))
    // GameObject.Z is sbyte and native TileData.Height is byte. This bound is
    // proven from the source representation, not the tallest loaded building.
    internal const float NativeCasterCeiling = 127 + 255;
    internal const float NativeMinimumZ = -128;

    internal static PhysicalSunProjection Create(PhysicalWorldSnapshot snapshot,
        N2 surfaceOrigin, N2 shadowDirection, float length, float strength,
        int resolution = 2048)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Overflow || !Finite(snapshot.Min) || !Finite(snapshot.Max)
            || snapshot.Min.Z < NativeMinimumZ || snapshot.Max.Z > NativeCasterCeiling
            || !float.IsFinite(surfaceOrigin.X) || !float.IsFinite(surfaceOrigin.Y)
            || !float.IsFinite(shadowDirection.X) || !float.IsFinite(shadowDirection.Y)
            || shadowDirection.LengthSquared() < .000001f || !float.IsFinite(length)
            || length < .25f || length > 8 || !float.IsFinite(strength)
            || resolution < 64 || resolution > 4096)
            throw new ArgumentException("Unsupported native structural sun domain.");

        N2 heading = N2.Normalize(shadowDirection);
        N3 source = new(-heading.X, -heading.Y, 1f / (ZScale * length));
        N3 sunMetric = N3.Normalize(new N3(source.X, source.Y, source.Z * ZScale));
        N3 away = -sunMetric;
        N3 right = N3.Normalize(N3.Cross(N3.UnitZ, away));
        N3 up = N3.Cross(away, right);
        N3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        for (int i = 0; i < 8; i++)
        {
            N3 p = new((i & 1) == 0 ? snapshot.Min.X : snapshot.Max.X,
                (i & 2) == 0 ? snapshot.Min.Y : snapshot.Max.Y,
                (i & 4) == 0 ? snapshot.Min.Z : snapshot.Max.Z);
            p.X -= surfaceOrigin.X; p.Y -= surfaceOrigin.Y; p.Z *= ZScale;
            N3 light = new(N3.Dot(right, p), N3.Dot(up, p), N3.Dot(away, p));
            minimum = N3.Min(minimum, light); maximum = N3.Max(maximum, light);
        }
        // Two texels of guard band keep native edge receivers inside the map.
        N3 span = N3.Max(maximum - minimum, new N3(.25f));
        N3 pad = new(span.X * 2 / (resolution - 4), span.Y * 2 / (resolution - 4), .5f);
        minimum -= pad; maximum += pad; span = maximum - minimum;
        N4 Row(N3 axis, float low, float range) =>
            new(axis.X / range, axis.Y / range, axis.Z * ZScale / range, -low / range);
        // One map texel of conservative depth bias, bounded in native scale;
        // sampling is nearest. This is independent of the old lamp z/11 metric.
        float bias = MathF.Max(.000005f, (MathF.Max(span.X, span.Y) / resolution * 1.25f + .03f) / span.Z);
        return new(Row(right, minimum.X, span.X), Row(up, minimum.Y, span.Y),
            Row(away, minimum.Z, span.Z), surfaceOrigin, source, snapshot.Min.Z,
            NativeCasterCeiling, bias, Math.Clamp(strength, 0, 1), resolution);
    }

    internal N3 UvDepth(N3 nativeWorld)
    {
        N4 p = new(nativeWorld.X - Origin.X, nativeWorld.Y - Origin.Y, nativeWorld.Z, 1);
        return new(N4.Dot(U, p), N4.Dot(V, p), N4.Dot(Depth, p));
    }

    /// <summary>
    /// Qualifies a whole receiver cell for every height at or above ReceiverMinZ.
    /// The swept unit square reaches the proven native caster ceiling. Unknown
    /// cells and rays leaving the snapshot are rejected, never called empty sky.
    /// </summary>
    internal int BuildKnownReceiverCells(PhysicalWorldSnapshot snapshot, Span<uint> known)
    {
        PhysicalRect bounds = snapshot.Bounds;
        int count = checked(bounds.Width * bounds.Height);
        if (known.Length < count) throw new ArgumentException("Receiver coverage buffer is too small.");
        known[..count].Clear();
        if (snapshot.Overflow) return 0;
        N2 travel = new(SourceToSun.X / SourceToSun.Z, SourceToSun.Y / SourceToSun.Z);
        travel *= CasterCeiling - ReceiverMinZ;
        int total = 0;
        for (int y = bounds.Y; y < bounds.Bottom; y++)
            for (int x = bounds.X; x < bounds.Right; x++)
                if (KnownSweep(snapshot, x, y, travel))
                { known[(y - bounds.Y) * bounds.Width + x - bounds.X] = 1; total++; }
        return total;
    }

    private static bool KnownSweep(PhysicalWorldSnapshot snapshot, int x, int y, N2 travel)
    {
        // A segment swept by a unit square intersects a cell iff the centre
        // segment intersects that cell enlarged by half a tile. Walk row bands
        // instead of the sweep's bounding box, so diagonal rays remain narrow.
        const float epsilon = .00001f;
        float startX = x + .5f, startY = y + .5f;
        float endX = startX + travel.X, endY = startY + travel.Y;
        int firstRow = (int)MathF.Floor(MathF.Min(startY, endY) - .5f + epsilon);
        int lastRow = (int)MathF.Ceiling(MathF.Max(startY, endY) + .5f - epsilon) - 1;
        PhysicalRect bounds = snapshot.Bounds;
        if (firstRow < bounds.Y || lastRow >= bounds.Bottom) return false;
        for (int row = firstRow; row <= lastRow; row++)
        {
            float enter = 0, leave = 1;
            if (MathF.Abs(travel.Y) > epsilon)
            {
                float a = (row - .5f - startY) / travel.Y;
                float b = (row + 1.5f - startY) / travel.Y;
                enter = MathF.Max(0, MathF.Min(a, b));
                leave = MathF.Min(1, MathF.Max(a, b));
            }
            float aX = startX + travel.X * enter, bX = startX + travel.X * leave;
            int firstColumn = (int)MathF.Floor(MathF.Min(aX, bX) - 1.5f + epsilon) + 1;
            int lastColumn = (int)MathF.Ceiling(MathF.Max(aX, bX) + .5f - epsilon) - 1;
            if (firstColumn < bounds.X || lastColumn >= bounds.Right) return false;
            for (int column = firstColumn; column <= lastColumn; column++)
                if (!snapshot.IsCellComplete(column, row)) return false;
        }
        return true;
    }

    private static bool Finite(N3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
