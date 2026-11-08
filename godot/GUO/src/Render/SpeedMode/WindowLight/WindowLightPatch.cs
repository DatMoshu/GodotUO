// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned, ADR-0048: immutable per-window draw data for the core light target.
using System;
using System.Runtime.InteropServices;
using N2 = System.Numerics.Vector2;
using N4 = System.Numerics.Vector4;

namespace GUO.Renderer.SpeedMode.WindowLight;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct WindowLightPatch
{
    internal N4 Origin;       // world tile XY, base UO Z, intensity
    internal N4 Basis;        // tile-space width XY, depth XY
    internal N4 Opening;     // shape, aspect, row count, lower-card height UO Z
    internal N4 Screen;      // conservative screen-pixel min XY, max XY
    internal N4 Colour;
    internal N4 Rows0, Rows1, Rows2, Rows3;
    internal const int Bytes = 144;

    // Screen bounds must enclose the projected floor patch, plus the lowest
    // four UO Z units (16 pixels before zoom) of upright receivers. The caller
    // knows its current view transform and constructs those bounds at that seam.
    internal static bool TryCreate(WindowOpening window, N2 origin, float baseZ, N2 tangent,
        N2 inward, N2 sunRay, bool insideCovered, bool outsideOpen, float daylight,
        N4 screenBounds, N4 sunColour, out WindowLightPatch patch)
    {
        patch = default;
        if (!insideCovered || !outsideOpen || !float.IsFinite(daylight) || daylight <= .5f ||
            !window.TryProject(origin,tangent,inward,sunRay,out N2 width,out N2 depth)) return false;
        patch.Origin = new N4(origin,baseZ,Math.Clamp((daylight-.5f)*2,0,1));
        patch.Basis = new N4(width,depth.X,depth.Y);
        patch.Opening = new N4((int)window.Shape,window.Aspect,window.RowCount,4);
        patch.Screen = screenBounds;
        patch.Colour = sunColour;
        Span<float> rows = stackalloc float[16];
        rows.Clear();
        for (int i = 0; i < window.RowCount; i++) rows[i] = window.PanesInRow(i);
        patch.Rows0 = new N4(rows[0],rows[1],rows[2],rows[3]);
        patch.Rows1 = new N4(rows[4],rows[5],rows[6],rows[7]);
        patch.Rows2 = new N4(rows[8],rows[9],rows[10],rows[11]);
        patch.Rows3 = new N4(rows[12],rows[13],rows[14],rows[15]);
        return true;
    }
}
