// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned, ADR-0048 amendment: common shadow selection and eligibility.
using System;
using GUO.Assets;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

// XY and radius are tiles; Z is UO units, matching the surface buffer.
internal readonly record struct ShadowLamp(N3 Position, float Radius, uint Id);

internal static class PolarShadowGeometry
{
    internal const int AngleSamples = 256, AtlasRows = 64, MaxLamps = 10;
    internal const float EmptyDistance = 65504;

    public static bool Casts(TileFlag flags,int height,bool mobile = false)
    {
        const TileFlag excluded = TileFlag.Roof | TileFlag.Surface | TileFlag.Bridge |
            TileFlag.Background | TileFlag.Translucent | TileFlag.LightSource;
        return (flags & excluded) == 0 && (mobile || height >= 3);
    }

    public static int Select(ReadOnlySpan<ShadowLamp> lamps,N3 viewer,int requested,Span<int> selected)
    {
        int limit = Math.Min(Math.Clamp(requested,0,MaxLamps),selected.Length),count = 0;
        Span<float> distances = stackalloc float[MaxLamps];
        for (int i = 0; i < lamps.Length; i++)
        {
            if (!(lamps[i].Radius > 0) || !float.IsFinite(lamps[i].Radius)) continue;
            // Selection is horizontal: Z is in UO units, not tile lengths.
            float x = lamps[i].Position.X-viewer.X,y = lamps[i].Position.Y-viewer.Y;
            float d = x*x+y*y;
            if (!float.IsFinite(d)) continue;
            int at = count;
            while (at > 0 && d < distances[at-1]) at--;
            if (at >= limit) continue;
            for (int j = Math.Min(count,limit-1); j > at; j--)
            { selected[j] = selected[j-1]; distances[j] = distances[j-1]; }
            selected[at] = i; distances[at] = d; count = Math.Min(count+1,limit);
        }
        return count;
    }
}
