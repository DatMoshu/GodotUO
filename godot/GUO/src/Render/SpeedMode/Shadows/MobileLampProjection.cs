// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: bounded skewed silhouettes, never sprite cards in a polar map.
using System;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

internal readonly record struct MobileLampShadow(int Offer,N2 Direction,float Length,float Strength);

internal static class MobileLampProjection
{
    // Up to two nearest affecting lamps per mobile. The existing batcher
    // projects native alpha; the scene adapter uses its interpolated contact.
    public static int Select(ReadOnlySpan<ShadowLamp> lamps,N3 foot,Span<MobileLampShadow> output)
    {
        Span<int> nearest=stackalloc int[2]; nearest.Fill(-1);
        Span<float> distances=stackalloc float[2]; distances.Fill(float.MaxValue);
        for (int i=0;i<lamps.Length;i++)
        {
            N3 d=foot-lamps[i].Position;
            float q=d.X*d.X+d.Y*d.Y;
            if (!float.IsFinite(q) || !(lamps[i].Radius>0) || q>=lamps[i].Radius*lamps[i].Radius || q<0.0001f) continue;
            int at=q<distances[0] ? 0 : q<distances[1] ? 1 : 2;
            if (at==2) continue;
            if (at==0) { nearest[1]=nearest[0]; distances[1]=distances[0]; }
            nearest[at]=i; distances[at]=q;
        }
        int count=0;
        for (int j=0;j<Math.Min(2,output.Length);j++)
        {
            int i=nearest[j]; if (i<0) break;
            N3 d=foot-lamps[i].Position;
            N2 screen=new(d.X-d.Y,d.X+d.Y);
            float distance=MathF.Sqrt(distances[j]);
            float fade=1-distance/lamps[i].Radius; fade*=fade;
            output[count++]=new MobileLampShadow(i,N2.Normalize(screen),
                Math.Clamp(distance/MathF.Max(1,(lamps[i].Position.Z-foot.Z)/4),.25f,4),.55f*fade);
        }
        return count;
    }
}
