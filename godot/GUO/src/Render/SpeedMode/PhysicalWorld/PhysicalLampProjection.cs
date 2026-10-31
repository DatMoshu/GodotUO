// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Renderer.SpeedMode.Shadows;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

/// <summary>One selected native lamp; six perspective faces in a 3 by 2 atlas.</summary>
internal readonly record struct PhysicalLampProjection(ShadowLamp Lamp, N2 Origin,
    int OfferIndex, int FaceResolution, float Near, float Far, float Tangent, float Bias)
{
    internal const float ZScale = PhysicalSunProjection.ZScale;
    internal static PhysicalLampProjection Create(PhysicalWorldSnapshot snapshot, ShadowLamp lamp,
        N2 surfaceOrigin, int selectedOffer, int faceResolution = 256)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Overflow || !Finite(lamp.Position) || !float.IsFinite(lamp.Radius) || lamp.Radius <= 0
            || lamp.Radius > 128 || !float.IsFinite(surfaceOrigin.X) || !float.IsFinite(surfaceOrigin.Y)
            || selectedOffer < 0 || faceResolution < 64 || faceResolution > 1024)
            throw new ArgumentException("Unsupported structural lamp domain.");
        // Existing attenuation is z/11. sqrt(2)*radius encloses that whole
        // legacy light ellipsoid in the new physical metric, without changing energy/radius.
        float far = lamp.Radius * MathF.Sqrt(2) + .25f;
        return new(lamp, surfaceOrigin, selectedOffer, faceResolution, .025f, far,
            1 + 2f / faceResolution, .02f);
    }

    internal static void Basis(int face, out N3 forward, out N3 right, out N3 up)
    {
        (forward, right, up) = face switch
        {
            0 => (N3.UnitX, -N3.UnitY, N3.UnitZ),
            1 => (-N3.UnitX, N3.UnitY, N3.UnitZ),
            2 => (N3.UnitY, N3.UnitX, N3.UnitZ),
            3 => (-N3.UnitY, -N3.UnitX, N3.UnitZ),
            4 => (N3.UnitZ, N3.UnitX, -N3.UnitY),
            5 => (-N3.UnitZ, N3.UnitX, N3.UnitY),
            _ => throw new ArgumentOutOfRangeException(nameof(face))
        };
    }

    internal bool TryProject(N3 world, out int face, out N2 atlasUv, out float depth)
    {
        face = 0; atlasUv = default; depth = 0;
        if (!Finite(world)) return false;
        N3 p = world - Lamp.Position; p.Z *= ZScale;
        N3 a = N3.Abs(p);
        face = a.X >= a.Y && a.X >= a.Z ? (p.X >= 0 ? 0 : 1)
            : a.Y >= a.Z ? (p.Y >= 0 ? 2 : 3) : (p.Z >= 0 ? 4 : 5);
        Basis(face, out N3 forward, out N3 right, out N3 up);
        float major = N3.Dot(forward, p);
        if (major < Near || major >= Far) return false;
        N2 uv = new(N3.Dot(right, p), N3.Dot(up, p));
        uv = uv / (major * Tangent) * .5f + new N2(.5f);
        float inset = .5f / FaceResolution;
        uv = N2.Clamp(uv, new N2(inset), new N2(1 - inset));
        atlasUv = new N2((face % 3 + uv.X) / 3, (face / 3 + uv.Y) / 2);
        depth = major / Far;
        return true;
    }

    /// <summary>Whole-cell finite segment receipts; no ray-marching per rendered pixel.</summary>
    internal int BuildKnownReceiverCells(PhysicalWorldSnapshot snapshot, Span<uint> known)
    {
        int count = checked(snapshot.Bounds.Width * snapshot.Bounds.Height);
        if (known.Length < count) throw new ArgumentException("Lamp coverage buffer is too small.");
        known[..count].Clear();
        if (snapshot.Overflow) return 0;
        int total = 0;
        var b = snapshot.Bounds;
        for (int y = b.Y; y < b.Bottom; y++)
        for (int x = b.X; x < b.Right; x++)
        {
            if (Math.Abs(x + .5f - Lamp.Position.X) > Lamp.Radius + 1 ||
                Math.Abs(y + .5f - Lamp.Position.Y) > Lamp.Radius + 1) continue;
            if (!KnownSegment(snapshot, x, y, Lamp.Position.X, Lamp.Position.Y)) continue;
            known[(y - b.Y) * b.Width + x - b.X] = 1; total++;
        }
        return total;
    }

    private static bool KnownSegment(PhysicalWorldSnapshot snapshot, int x, int y, float endX, float endY)
    {
        // The entire receiver tile is swept toward the lamp. Keeping its full unit
        // width at the source is conservative and avoids fractional-edge clear claims.
        const float epsilon = .00001f;
        float startX = x + .5f, startY = y + .5f;
        float dx = endX - startX, dy = endY - startY;
        int firstRow = (int)MathF.Floor(MathF.Min(startY, endY) - .5f + epsilon);
        int lastRow = (int)MathF.Ceiling(MathF.Max(startY, endY) + .5f - epsilon) - 1;
        var b = snapshot.Bounds;
        if (firstRow < b.Y || lastRow >= b.Bottom) return false;
        for (int row = firstRow; row <= lastRow; row++)
        {
            float enter = 0, leave = 1;
            if (MathF.Abs(dy) > epsilon)
            {
                float a = (row - .5f - startY) / dy, z = (row + 1.5f - startY) / dy;
                enter = MathF.Max(0, MathF.Min(a, z)); leave = MathF.Min(1, MathF.Max(a, z));
            }
            float first = startX + dx * enter, last = startX + dx * leave;
            int firstColumn = (int)MathF.Floor(MathF.Min(first, last) - 1.5f + epsilon) + 1;
            int lastColumn = (int)MathF.Ceiling(MathF.Max(first, last) + .5f - epsilon) - 1;
            if (firstColumn < b.X || lastColumn >= b.Right) return false;
            for (int column = firstColumn; column <= lastColumn; column++)
                if (!snapshot.IsCellComplete(column, row)) return false;
        }
        return true;
    }

    private static bool Finite(N3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
